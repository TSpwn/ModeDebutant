using NINA.Core.Enum;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;

namespace ModeDebutant.AlignementPolaire {

    /// <summary>
    /// Le "cerveau" du panneau « Alignement polaire simplifié ».
    ///
    /// Principe de fonctionnement :
    /// 1. Ce panneau s'abonne à la "radio interne" de N.I.N.A. sur le canal
    ///    où TPPA publie son erreur d'alignement en continu (toutes les
    ///    quelques secondes pendant la phase d'ajustement des vis).
    /// 2. À chaque message reçu, on traduit les degrés techniques en consignes
    ///    simples : quelle vis, quel sens, quelle ampleur, quelle couleur.
    /// 3. L'écran (le fichier .xaml associé) se contente d'afficher ces
    ///    propriétés — c'est le fonctionnement standard de WPF : le "cerveau"
    ///    expose des valeurs, l'écran s'y "branche" (binding).
    ///
    /// [Export(typeof(IDockableVM))] = la "prise" qui dit à N.I.N.A. :
    /// « j'apporte un panneau pour l'onglet imagerie ».
    /// </summary>
    [Export(typeof(IDockableVM))]
    public class AlignementPolaireDockableVM : DockableVM, ISubscriber {

        // Les canaux radio de TPPA (noms exacts relevés dans son code)
        private const string CanalErreur = "PolarAlignmentPlugin_PolarAlignment_AlignmentError";
        private const string CanalProgression = "PolarAlignmentPlugin_PolarAlignment_Progress";

        private readonly IMessageBroker messageBroker;
        private readonly IProfileService profileService;
        private readonly ICameraMediator cameraMediator;
        private readonly ITelescopeMediator telescopeMediator;

        // Chien de garde : vérifie régulièrement que TPPA donne signe de vie
        // après un démarrage (sinon on prévient au lieu d'attendre en silence)
        private readonly System.Timers.Timer chienDeGarde;
        private DateTime demarreLe;
        private bool tppaADonneSigneDeVie;

        // Coffre à réglages fourni par N.I.N.A. : mémorise nos options
        // dans le profil actif, d'une session à l'autre
        private readonly PluginOptionsAccessor reglages;

        // Les trois phases possibles de notre écran
        private enum Phase { Attente, Mesure, Ajustement }
        private Phase phase = Phase.Attente;

        [ImportingConstructor]
        public AlignementPolaireDockableVM(IProfileService profileService, IMessageBroker messageBroker, IImagingMediator imagingMediator, ICameraMediator cameraMediator, ITelescopeMediator telescopeMediator) : base(profileService) {
            this.profileService = profileService;
            this.messageBroker = messageBroker;
            this.cameraMediator = cameraMediator;
            this.telescopeMediator = telescopeMediator;

            // Toutes les 5 secondes, le chien de garde vérifie si TPPA répond
            chienDeGarde = new System.Timers.Timer(5000) { AutoReset = true };
            chienDeGarde.Elapsed += VerifierSigneDeVie;
            chienDeGarde.Start();

            // À chaque image capturée puis traitée par N.I.N.A. (dont celles
            // de TPPA), on reçoit une version affichable : on la garde pour
            // la montrer dans notre panneau
            imagingMediator.ImagePrepared += QuandImagePrete;

            // Le coffre à réglages est identifié par le GUID de notre plugin
            reglages = new PluginOptionsAccessor(profileService, Guid.Parse("3d87e151-d363-4708-bce9-5db3356abccc"));

            // Titre affiché dans l'en-tête du panneau et dans la liste des panneaux
            Title = "Alignement polaire simplifié";

            // Icône de l'en-tête : une simple cible (deux cercles + croix),
            // dessinée en "chemins" vectoriels
            var icone = new GeometryGroup();
            icone.Children.Add(Geometry.Parse("M 50,10 A 40,40 0 1 0 50,90 A 40,40 0 1 0 50,10 Z M 50,30 A 20,20 0 1 0 50,70 A 20,20 0 1 0 50,30 Z"));
            icone.Children.Add(Geometry.Parse("M 47,0 L 53,0 L 53,20 L 47,20 Z M 47,80 L 53,80 L 53,100 L 47,100 Z M 0,47 L 20,47 L 20,53 L 0,53 Z M 80,47 L 100,47 L 100,53 L 80,53 Z"));
            icone.Freeze();
            ImageGeometry = icone;

            // On se branche sur la radio : erreurs d'alignement + avancement
            messageBroker.Subscribe(CanalErreur, this);
            messageBroker.Subscribe(CanalProgression, this);

            DemarrerCommand = new CommandeSimple(Demarrer);
            ArreterCommand = new CommandeSimple(Arreter);
            OuvrirModificationPositionCommand = new CommandeSimple(OuvrirModificationPosition);
            EnregistrerPositionCommand = new CommandeSimple(EnregistrerPosition);
            LocaliserParInternetCommand = new CommandeSimple(LocaliserParInternet);
            OuvrirModificationMaterielCommand = new CommandeSimple(OuvrirModificationMateriel);
            EnregistrerMaterielCommand = new CommandeSimple(EnregistrerMateriel);
            RafraichirMateriel();

            // La position du lieu d'observation : affichée en clair, car tout
            // en dépend (consignes nord/sud, calculs de TPPA). Si elle change
            // (ici ou dans les options de N.I.N.A.), on se met à jour.
            profileService.LocationChanged += (s, e) => { RafraichirPosition(); ChercherVille(); };
            RafraichirPosition();
            ChercherVille();

            // Petit contrôle de confort : TPPA est-il installé ?
            // (on regarde simplement si son dossier existe)
            var dossierTppa = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins", "3.0.0", "Three Point Polar Alignment");
            TppaAbsent = !Directory.Exists(dossierTppa);
        }

        // Panneau rangé du côté "outils" de l'onglet imagerie (comme TPPA)
        public override bool IsTool => true;

        // ------------------------------------------------------------------
        // Position du lieu d'observation
        // C'est LA donnée dont tout dépend (hémisphère -> sens des consignes,
        // calculs de TPPA). N.I.N.A. la cache dans Options > Général : ici,
        // elle est affichée en clair, modifiable, et réglable par Internet.
        // ------------------------------------------------------------------

        // Un seul client web pour tout le panneau (règle .NET : on ne crée
        // pas un client par requête). Délai court : c'est un bonus, pas vital.
        private static readonly HttpClient clientWeb = CreerClientWeb();

        private static HttpClient CreerClientWeb() {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ModeDebutant-NINA-Plugin/0.2");
            return client;
        }

        /// <summary>« 48,857° N · 2,351° E — hémisphère Nord »</summary>
        public string PositionTexte { get; private set; } = "";

        /// <summary>La ville correspondante, trouvée par Internet (vide sinon).</summary>
        public string VilleTexte { get; private set; } = "";

        /// <summary>true = latitude ET longitude à zéro : réglage oublié.</summary>
        public bool PositionNonReglee { get; private set; }

        /// <summary>true = le petit formulaire de saisie manuelle est ouvert.</summary>
        public bool ModificationPositionOuverte { get; private set; }

        public string LatitudeSaisie { get; set; } = "";
        public string LongitudeSaisie { get; set; } = "";
        public string AltitudeSaisie { get; set; } = "";

        /// <summary>Petit message sous la carte position (résultat, erreur...).</summary>
        public string MessagePosition { get; private set; } = "";

        public ICommand OuvrirModificationPositionCommand { get; }
        public ICommand EnregistrerPositionCommand { get; }
        public ICommand LocaliserParInternetCommand { get; }

        private void RafraichirPosition() {
            double lat = profileService.ActiveProfile.AstrometrySettings.Latitude;
            double lon = profileService.ActiveProfile.AstrometrySettings.Longitude;
            double altitude = profileService.ActiveProfile.AstrometrySettings.Elevation;

            PositionNonReglee = Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001;

            PositionTexte = Math.Abs(lat).ToString("0.###") + "° " + (lat >= 0 ? "N" : "S")
                + "  ·  " + Math.Abs(lon).ToString("0.###") + "° " + (lon >= 0 ? "E" : "O")
                + "  ·  " + altitude.ToString("0") + " m"
                + "  —  hémisphère " + (lat >= 0 ? "Nord" : "Sud");

            RaisePropertyChanged(nameof(PositionTexte));
            RaisePropertyChanged(nameof(PositionNonReglee));
        }

        /// <summary>
        /// Retrouve le nom de la ville depuis les coordonnées (service gratuit
        /// BigDataCloud, sans clé). Échec silencieux : la ville est un confort,
        /// jamais un blocage (pas d'Internet au fond du jardin = pas grave).
        /// </summary>
        private async void ChercherVille() {
            double lat = profileService.ActiveProfile.AstrometrySettings.Latitude;
            double lon = profileService.ActiveProfile.AstrometrySettings.Longitude;
            if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001) {
                VilleTexte = "";
                RaisePropertyChanged(nameof(VilleTexte));
                return;
            }
            try {
                var url = "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude="
                    + lat.ToString(CultureInfo.InvariantCulture)
                    + "&longitude=" + lon.ToString(CultureInfo.InvariantCulture)
                    + "&localityLanguage=fr";
                var json = await clientWeb.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                string ville = LireChampJson(doc, "city");
                if (string.IsNullOrWhiteSpace(ville)) { ville = LireChampJson(doc, "locality"); }
                string pays = LireChampJson(doc, "countryName");

                VilleTexte = string.IsNullOrWhiteSpace(ville)
                    ? ""
                    : "📍 " + ville + (string.IsNullOrWhiteSpace(pays) ? "" : " (" + pays + ")");
                RaisePropertyChanged(nameof(VilleTexte));
            } catch {
                // Pas d'Internet ou service indisponible : on n'affiche rien
            }
        }

        private static string LireChampJson(JsonDocument doc, string nom) {
            return doc.RootElement.TryGetProperty(nom, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }

        /// <summary>
        /// Altitude du terrain à une position donnée (service gratuit
        /// open-meteo, sans clé). null = pas de réponse exploitable.
        /// </summary>
        private static async Task<double?> ChercherAltitude(double lat, double lon) {
            try {
                var url = "https://api.open-meteo.com/v1/elevation?latitude="
                    + lat.ToString(CultureInfo.InvariantCulture)
                    + "&longitude=" + lon.ToString(CultureInfo.InvariantCulture);
                var json = await clientWeb.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                // Réponse : { "elevation": [123.0] }
                if (doc.RootElement.TryGetProperty("elevation", out var tableau)
                    && tableau.ValueKind == JsonValueKind.Array && tableau.GetArrayLength() > 0
                    && tableau[0].ValueKind == JsonValueKind.Number) {
                    return tableau[0].GetDouble();
                }
            } catch {
                // Sans Internet ou service en panne : tant pis, pas d'altitude
            }
            return null;
        }

        /// <summary>Ouvre/ferme la saisie manuelle, préremplie avec les valeurs actuelles.</summary>
        private void OuvrirModificationPosition() {
            ModificationPositionOuverte = !ModificationPositionOuverte;
            if (ModificationPositionOuverte) {
                LatitudeSaisie = profileService.ActiveProfile.AstrometrySettings.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
                LongitudeSaisie = profileService.ActiveProfile.AstrometrySettings.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
                AltitudeSaisie = profileService.ActiveProfile.AstrometrySettings.Elevation.ToString("0", CultureInfo.InvariantCulture);
                MessagePosition = "Latitude : positif = Nord. Longitude : positif = Est, négatif = Ouest. Trouvez vos valeurs sur Google Maps (clic droit sur votre maison). Altitude : à ±100 m près, c'est très bien.";
            } else {
                MessagePosition = "";
            }
            RaisePropertyChanged(nameof(ModificationPositionOuverte));
            RaisePropertyChanged(nameof(LatitudeSaisie));
            RaisePropertyChanged(nameof(LongitudeSaisie));
            RaisePropertyChanged(nameof(AltitudeSaisie));
            RaisePropertyChanged(nameof(MessagePosition));
        }

        /// <summary>Enregistre la saisie manuelle dans le profil N.I.N.A.
        /// (API officielle : TPPA et tout N.I.N.A. voient le changement).</summary>
        private void EnregistrerPosition() {
            var latTexte = LatitudeSaisie?.Trim().Replace(',', '.');
            var lonTexte = LongitudeSaisie?.Trim().Replace(',', '.');
            var altTexte = AltitudeSaisie?.Trim().Replace(',', '.');

            if (!double.TryParse(latTexte, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(lonTexte, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) {
                MessagePosition = "⚠ Valeurs illisibles. Attendu : des degrés décimaux, ex. 48,8566 et 2,3522 (latitude entre -90 et 90, longitude entre -180 et 180).";
                RaisePropertyChanged(nameof(MessagePosition));
                return;
            }

            // Altitude : champ facultatif — vide ou illisible = inchangée
            // (bornes larges : mer Morte -430 m, observatoires ~5000 m)
            bool altValide = double.TryParse(altTexte, NumberStyles.Float, CultureInfo.InvariantCulture, out var alt)
                && alt >= -450 && alt <= 9000;

            profileService.ChangeLatitude(lat);
            profileService.ChangeLongitude(lon);
            if (altValide) { profileService.ChangeElevation(alt); }
            ModificationPositionOuverte = false;
            MessagePosition = "✅ Position enregistrée dans le profil N.I.N.A.";
            RaisePropertyChanged(nameof(ModificationPositionOuverte));
            RaisePropertyChanged(nameof(MessagePosition));
            // L'affichage et la ville se rafraîchissent via LocationChanged
        }

        /// <summary>
        /// Localisation par Internet (adresse IP, service gratuit ipapi.co) :
        /// précision « à la ville près », largement suffisante pour
        /// l'alignement polaire. Rien n'est enregistré si la requête échoue.
        /// </summary>
        private async void LocaliserParInternet() {
            MessagePosition = "🌍 Recherche de votre position par Internet…";
            RaisePropertyChanged(nameof(MessagePosition));
            try {
                var json = await clientWeb.GetStringAsync("https://ipapi.co/json/");
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("latitude", out var latElement)
                    || !doc.RootElement.TryGetProperty("longitude", out var lonElement)
                    || latElement.ValueKind != JsonValueKind.Number
                    || lonElement.ValueKind != JsonValueKind.Number) {
                    throw new Exception("réponse sans coordonnées");
                }

                double lat = latElement.GetDouble();
                double lon = lonElement.GetDouble();
                profileService.ChangeLatitude(lat);
                profileService.ChangeLongitude(lon);

                // L'adresse IP ne donne pas l'altitude : on la déduit du
                // relief à cet endroit (service gratuit open-meteo).
                // Bonus silencieux : sans réponse, l'altitude reste inchangée.
                double? altitude = await ChercherAltitude(lat, lon);
                if (altitude.HasValue) { profileService.ChangeElevation(altitude.Value); }

                string ville = LireChampJson(doc, "city");
                MessagePosition = "✅ Position réglée" + (string.IsNullOrWhiteSpace(ville) ? "" : " près de " + ville)
                    + (altitude.HasValue ? ", altitude " + altitude.Value.ToString("0") + " m" : "")
                    + " (précision : la ville — suffisant pour l'alignement).";
                ModificationPositionOuverte = false;
                RaisePropertyChanged(nameof(ModificationPositionOuverte));
            } catch {
                MessagePosition = "⚠ Localisation impossible (pas d'Internet ? service indisponible ?). Utilisez « Modifier à la main ».";
            }
            RaisePropertyChanged(nameof(MessagePosition));
        }

        // ------------------------------------------------------------------
        // Votre matériel : focale, diamètre, pixels de la caméra
        // Ces trois chiffres conditionnent le cadrage (champ de vision) et
        // les suggestions de cibles du séquenceur. N.I.N.A. les cache dans
        // ses options : ici, ils sont lisibles et modifiables directement.
        // (N.I.N.A. stocke focale + rapport F/D ; le diamètre s'en déduit.)
        // ------------------------------------------------------------------

        /// <summary>« Focale 400 mm · Diamètre 72 mm · F/5,6 · pixels 3,76 µm »</summary>
        public string MaterielTexte { get; private set; } = "";

        /// <summary>« → 1,9″ par pixel · champ 132′ × 88′ » (si calculable).</summary>
        public string CadrageTexte { get; private set; } = "";

        /// <summary>true = focale absente : cadrage et suggestions aveugles.</summary>
        public bool MaterielNonRegle { get; private set; }

        public bool ModificationMaterielOuverte { get; private set; }

        public string FocaleSaisie { get; set; } = "";
        public string DiametreSaisie { get; set; } = "";
        public string PixelSaisie { get; set; } = "";

        public string MessageMateriel { get; private set; } = "";

        public ICommand OuvrirModificationMaterielCommand { get; }
        public ICommand EnregistrerMaterielCommand { get; }

        private void RafraichirMateriel() {
            double focale = profileService.ActiveProfile.TelescopeSettings.FocalLength;
            double rapportFD = profileService.ActiveProfile.TelescopeSettings.FocalRatio;
            double pixel = profileService.ActiveProfile.CameraSettings.PixelSize;

            MaterielNonRegle = focale <= 0;

            if (focale > 0) {
                MaterielTexte = "Focale " + focale.ToString("0") + " mm";
                if (rapportFD > 0) {
                    MaterielTexte += "  ·  Diamètre " + (focale / rapportFD).ToString("0") + " mm"
                        + "  ·  F/" + rapportFD.ToString("0.#");
                }
                if (pixel > 0) { MaterielTexte += "  ·  pixels " + pixel.ToString("0.##") + " µm"; }
            } else {
                MaterielTexte = "Focale non renseignée";
            }

            // L'échantillonnage et le champ, si tout est connu
            CadrageTexte = "";
            if (focale > 0 && pixel > 0) {
                double arcsecParPixel = 206.265 * pixel / focale;
                CadrageTexte = "→ " + arcsecParPixel.ToString("0.0") + "″ par pixel";
                var camera = cameraMediator.GetInfo();
                if (camera.Connected && camera.XSize > 0 && camera.YSize > 0) {
                    CadrageTexte += "  ·  champ " + (camera.XSize * arcsecParPixel / 60.0).ToString("0")
                        + "′ × " + (camera.YSize * arcsecParPixel / 60.0).ToString("0") + "′";
                } else {
                    CadrageTexte += "  ·  connectez la caméra pour voir votre champ";
                }
            }

            RaisePropertyChanged(nameof(MaterielTexte));
            RaisePropertyChanged(nameof(CadrageTexte));
            RaisePropertyChanged(nameof(MaterielNonRegle));
        }

        /// <summary>Ouvre/ferme la saisie, préremplie avec les valeurs actuelles.</summary>
        private void OuvrirModificationMateriel() {
            ModificationMaterielOuverte = !ModificationMaterielOuverte;
            if (ModificationMaterielOuverte) {
                double focale = profileService.ActiveProfile.TelescopeSettings.FocalLength;
                double rapportFD = profileService.ActiveProfile.TelescopeSettings.FocalRatio;
                double pixel = profileService.ActiveProfile.CameraSettings.PixelSize;
                FocaleSaisie = focale > 0 ? focale.ToString("0", CultureInfo.InvariantCulture) : "";
                DiametreSaisie = focale > 0 && rapportFD > 0 ? (focale / rapportFD).ToString("0", CultureInfo.InvariantCulture) : "";
                PixelSaisie = pixel > 0 ? pixel.ToString("0.##", CultureInfo.InvariantCulture) : "";
                MessageMateriel = "Ces chiffres sont écrits sur le tube ou l'objectif (ex : « 72/400 » = diamètre 72 mm, focale 400 mm). La taille de pixel est dans la fiche technique de la caméra (souvent remplie automatiquement à la connexion).";
            } else {
                MessageMateriel = "";
            }
            RaisePropertyChanged(nameof(ModificationMaterielOuverte));
            RaisePropertyChanged(nameof(FocaleSaisie));
            RaisePropertyChanged(nameof(DiametreSaisie));
            RaisePropertyChanged(nameof(PixelSaisie));
            RaisePropertyChanged(nameof(MessageMateriel));
        }

        /// <summary>Enregistre dans le profil N.I.N.A. (champ vide = inchangé).</summary>
        private void EnregistrerMateriel() {
            var focaleTexte = FocaleSaisie?.Trim().Replace(',', '.');
            var diametreTexte = DiametreSaisie?.Trim().Replace(',', '.');
            var pixelTexte = PixelSaisie?.Trim().Replace(',', '.');

            bool focaleOk = double.TryParse(focaleTexte, NumberStyles.Float, CultureInfo.InvariantCulture, out var focale) && focale > 0 && focale < 20000;
            bool diametreOk = double.TryParse(diametreTexte, NumberStyles.Float, CultureInfo.InvariantCulture, out var diametre) && diametre > 0 && diametre < 2000;
            bool pixelOk = double.TryParse(pixelTexte, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixel) && pixel > 0 && pixel < 100;

            if (!focaleOk && !diametreOk && !pixelOk) {
                MessageMateriel = "⚠ Aucune valeur lisible. Attendu : des nombres, ex. focale 400, diamètre 72, pixels 3,76.";
                RaisePropertyChanged(nameof(MessageMateriel));
                return;
            }

            if (focaleOk) { profileService.ActiveProfile.TelescopeSettings.FocalLength = focale; }

            // Le diamètre est enregistré sous forme de rapport F/D (le format
            // de N.I.N.A.) : F/D = focale ÷ diamètre
            double focaleFinale = profileService.ActiveProfile.TelescopeSettings.FocalLength;
            if (diametreOk && focaleFinale > 0) {
                profileService.ActiveProfile.TelescopeSettings.FocalRatio = focaleFinale / diametre;
            }

            if (pixelOk) { profileService.ActiveProfile.CameraSettings.PixelSize = pixel; }

            ModificationMaterielOuverte = false;
            MessageMateriel = "✅ Matériel enregistré dans le profil N.I.N.A. (le cadrage et les suggestions du séquenceur en profitent).";
            RaisePropertyChanged(nameof(ModificationMaterielOuverte));
            RaisePropertyChanged(nameof(MessageMateriel));
            RafraichirMateriel();
        }

        // ------------------------------------------------------------------
        // Vignette de la dernière photo prise
        // ------------------------------------------------------------------

        /// <summary>La dernière image capturée, prête à afficher.</summary>
        public ImageSource DerniereImage { get; private set; }

        private void QuandImagePrete(object sender, ImagePreparedEventArgs e) {
            var image = e?.RenderedImage?.Image;
            if (image == null) { return; }

            // "Freeze" fige l'image : indispensable pour qu'elle puisse être
            // affichée par l'écran alors qu'elle a été produite par un autre
            // fil d'exécution (règle de sécurité de WPF)
            if (!image.IsFrozen && image.CanFreeze) { image.Freeze(); }
            if (!image.IsFrozen) { return; }

            DerniereImage = image;
            RaisePropertyChanged(nameof(DerniereImage));

            // Et on compte les étoiles sur cette photo (en tâche de fond)
            CompterEtoiles(e.RenderedImage);
        }

        // ------------------------------------------------------------------
        // Comptage des étoiles : un bon indicateur de qualité
        // (mise au point, nuages, buée...)
        // ------------------------------------------------------------------

        // Garde-fou : une seule analyse à la fois (si les photos arrivent
        // plus vite que l'analyse, on saute simplement celles de trop)
        private bool comptageEnCours;

        private async void CompterEtoiles(IRenderedImage rendu) {
            if (comptageEnCours || phase == Phase.Attente) { return; }
            comptageEnCours = true;
            try {
                // Si N.I.N.A. a déjà analysé cette image, on réutilise le résultat
                var analyse = rendu.RawImageData?.StarDetectionAnalysis;

                if (analyse == null || analyse.DetectedStars <= 0) {
                    // Sinon on lance nous-mêmes le détecteur d'étoiles officiel
                    // (sensibilité normale, sans réduction de bruit)
                    var renduAnalyse = await rendu.DetectStars(false, StarSensitivityEnum.Normal, NoiseReductionEnum.None);
                    analyse = renduAnalyse?.RawImageData?.StarDetectionAnalysis;
                }

                if (analyse != null) {
                    int n = analyse.DetectedStars;
                    if (n > 0) {
                        NbEtoilesTexte = "⭐ " + n + (n > 1 ? " étoiles" : " étoile");

                        // La netteté (HFR) : taille moyenne des étoiles sur la
                        // photo. Plus le chiffre est PETIT, plus c'est net.
                        // (La bonne valeur dépend de votre matériel : ce qui
                        // compte, c'est qu'elle ne grimpe pas.)
                        double hfr = analyse.HFR;
                        if (!double.IsNaN(hfr) && hfr > 0) {
                            NbEtoilesTexte += "   ·   Netteté (HFR) : " + hfr.ToString("0.0") + " (petit = net)";
                        }
                    } else {
                        NbEtoilesTexte = "⚠ Aucune étoile détectée — mise au point ? nuages ?";
                    }
                    RaisePropertyChanged(nameof(NbEtoilesTexte));
                }
            } catch {
                // Le comptage est un bonus : s'il échoue, il ne doit
                // jamais perturber l'alignement lui-même
            } finally {
                comptageEnCours = false;
            }
        }

        /// <summary>Résumé du comptage d'étoiles de la dernière photo.</summary>
        public string NbEtoilesTexte { get; private set; } = "";

        // ------------------------------------------------------------------
        // Réception des messages de TPPA
        // ------------------------------------------------------------------

        /// <summary>
        /// Appelé par N.I.N.A. chaque fois qu'un message arrive sur un canal
        /// auquel nous sommes abonnés.
        /// </summary>
        public Task OnMessageReceived(IMessage message) {
            if (message.Topic == CanalErreur) {
                // TPPA vit : on annule toute alerte du chien de garde
                tppaADonneSigneDeVie = true;
                Avertissement = "";

                // TPPA envoie trois nombres en DEGRÉS ; on les lit par leur nom
                if (LireDouble(message.Content, "AzimuthError", out var azimutDeg)
                    && LireDouble(message.Content, "AltitudeError", out var altitudeDeg)
                    && LireDouble(message.Content, "TotalError", out var totalDeg)) {
                    phase = Phase.Ajustement;
                    MettreAJourConsignes(azimutDeg, altitudeDeg, totalDeg);
                    NotifierToutChange();
                }
            } else if (message.Topic == CanalProgression) {
                // TPPA diffuse son avancement : il est bien vivant
                tppaADonneSigneDeVie = true;
                Avertissement = "";

                // On affiche le détail technique tel quel (texte anglais de
                // TPPA : Capture, Solving, Slewing…) — c'est un petit plus
                // pour comprendre ce qui se passe
                if (message.Content is NINA.Core.Model.ApplicationStatus statut && !string.IsNullOrWhiteSpace(statut.Status)) {
                    DetailTechnique = statut.Status;
                }

                if (phase == Phase.Attente) {
                    // Une routine TPPA tourne (peut-être lancée depuis
                    // l'interface TPPA elle-même) : on suit le mouvement
                    phase = Phase.Mesure;
                }
                NotifierToutChange();
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Le chien de garde : si 20 secondes après notre demande de démarrage
        /// TPPA n'a émis ni avancement ni mesure, quelque chose l'a bloqué
        /// (son message d'erreur apparaît alors en bulle, en bas de N.I.N.A.).
        /// </summary>
        private void VerifierSigneDeVie(object sender, System.Timers.ElapsedEventArgs e) {
            if (phase == Phase.Mesure && !tppaADonneSigneDeVie
                && DateTime.UtcNow - demarreLe > TimeSpan.FromSeconds(20)) {
                Avertissement = "⚠ TPPA ne répond pas. Regardez les messages d'erreur en bas de l'écran N.I.N.A. (matériel déconnecté ? plugin TPPA absent ?), puis cliquez sur Annuler pour réessayer.";
                RaisePropertyChanged(nameof(Avertissement));
            }
        }

        /// <summary>
        /// Lit une propriété numérique par son nom dans le contenu du message.
        /// (TPPA envoie un objet "anonyme" : on le lit par introspection,
        /// exactement comme TPPA le fait lui-même dans l'autre sens.)
        /// </summary>
        private static bool LireDouble(object contenu, string nom, out double valeur) {
            valeur = 0;
            var prop = contenu?.GetType().GetProperty(nom, BindingFlags.Instance | BindingFlags.Public);
            if (prop == null || !prop.CanRead) { return false; }
            if (prop.GetValue(contenu) is double d) { valeur = d; return true; }
            return false;
        }

        // ------------------------------------------------------------------
        // Traduction degrés -> consignes simples
        // ------------------------------------------------------------------

        // Seuils de l'échelle qualitative, en minutes d'arc (′)
        private const double SeuilParfait = 1.0;
        private const double SeuilPresque = 3.0;
        private const double SeuilUnPeu = 10.0;
        private const double SeuilBeaucoup = 30.0;

        private void MettreAJourConsignes(double azimutDeg, double altitudeDeg, double totalDeg) {
            // Conversion : 1 degré = 60 minutes d'arc
            double azimut = azimutDeg * 60.0;
            double altitude = altitudeDeg * 60.0;
            double total = Math.Abs(totalDeg * 60.0);

            // Hémisphère : au sud de l'équateur, la consigne haut/bas s'inverse
            // (même logique exacte que dans le code de TPPA)
            bool hemisphereNord = profileService.ActiveProfile.AstrometrySettings.Latitude > 0;

            // Le chiffre principal : erreur totale, 1 décimale max
            ErreurTotaleTexte = total.ToString("0.0") + "′";

            // L'échelle qualitative et la couleur.
            // Priorité à la précision visée : dès qu'on passe dessous,
            // c'est vert et terminé (TPPA s'arrête de lui-même).
            if (total < SeuilParfait) {
                NiveauTexte = "Parfait";
                CouleurEtat = BrosseVert;
            } else if (total <= ToleranceArcmin) {
                NiveauTexte = "Objectif atteint";
                CouleurEtat = BrosseVert;
            } else if (total < SeuilPresque) {
                NiveauTexte = "Presque";
                CouleurEtat = BrosseVert;
            } else if (total < SeuilUnPeu) {
                NiveauTexte = "Un peu";
                CouleurEtat = BrosseOrange;
            } else if (total < SeuilBeaucoup) {
                NiveauTexte = "Beaucoup";
                CouleurEtat = BrosseRouge;
            } else {
                NiveauTexte = "Énormément";
                CouleurEtat = BrosseRouge;
            }

            // Terminé = précision visée atteinte (ou mieux que « Parfait »)
            bool termine = total < SeuilParfait || total <= ToleranceArcmin;

            // Une seule flèche active à la fois : on corrige d'abord
            // l'axe dont l'erreur est la plus grande
            AzimutActif = !termine && Math.Abs(azimut) >= Math.Abs(altitude);
            AltitudeActif = !termine && !AzimutActif;

            // Sens des flèches — conventions relevées dans le code de TPPA :
            //  * azimut positif  -> pousser vers la GAUCHE (les deux hémisphères)
            //  * altitude positive -> l'axe pointe trop HAUT -> descendre
            //    (au nord ; inversé au sud)
            if (termine) {
                FlecheAzimut = "✓";
                FlecheAltitude = "✓";
                ConsigneAzimut = "C'est bon !";
                ConsigneAltitude = "C'est bon !";
            } else {
                FlecheAzimut = azimut > 0 ? "⬅" : "➡";
                ConsigneAzimut = azimut > 0 ? "Tournez vers la GAUCHE" : "Tournez vers la DROITE";

                bool descendre = hemisphereNord ? altitudeDeg > 0 : altitudeDeg < 0;
                FlecheAltitude = descendre ? "⬇" : "⬆";
                ConsigneAltitude = descendre ? "Faites DESCENDRE l'axe" : "Faites MONTER l'axe";
            }

            // Détail par vis (même format lisible : 1 décimale)
            AzimutTexte = Math.Abs(azimut).ToString("0.0") + "′";
            AltitudeTexte = Math.Abs(altitude).ToString("0.0") + "′";
        }

        // ------------------------------------------------------------------
        // Réglages choisis avant le démarrage (mémorisés dans le profil)
        // ------------------------------------------------------------------

        /// <summary>
        /// true (par défaut) = la monture a un GoTo et bouge toute seule.
        /// false = mode manuel : vous tournez la monture entre les photos.
        /// </summary>
        public bool MontureGoto {
            get => reglages.GetValueBoolean(nameof(MontureGoto), true);
            set { reglages.SetValueBoolean(nameof(MontureGoto), value); RaisePropertyChanged(); }
        }

        /// <summary>
        /// true = les mesures commencent là où pointe le télescope.
        /// false (par défaut) = TPPA amène d'abord le télescope à son point de départ.
        /// </summary>
        public bool DemarrerSurPlace {
            get => reglages.GetValueBoolean(nameof(DemarrerSurPlace), false);
            set { reglages.SetValueBoolean(nameof(DemarrerSurPlace), value); RaisePropertyChanged(); }
        }

        /// <summary>
        /// Précision visée, en minutes d'arc. Quand l'erreur totale passe
        /// dessous, TPPA considère l'alignement terminé et s'arrête seul.
        /// </summary>
        public double ToleranceArcmin {
            get => reglages.GetValueDouble(nameof(ToleranceArcmin), 1.0);
            set { reglages.SetValueDouble(nameof(ToleranceArcmin), value); RaisePropertyChanged(); }
        }

        // ------------------------------------------------------------------
        // Réglages avancés (section repliée « Tous les réglages »)
        // Saisis en texte libre : un champ vide = TPPA garde son réglage
        // habituel. Tous mémorisés dans le profil, comme les options simples.
        // ------------------------------------------------------------------

        // Deux petits raccourcis pour lire/écrire un champ texte mémorisé
        private string LireTexte(string nom) => reglages.GetValueString(nom, "");
        private void EcrireTexte(string nom, string valeur) { reglages.SetValueString(nom, valeur); RaisePropertyChanged(nom); }

        public string PoseTexte { get => LireTexte(nameof(PoseTexte)); set => EcrireTexte(nameof(PoseTexte), value); }
        public string GainTexte { get => LireTexte(nameof(GainTexte)); set => EcrireTexte(nameof(GainTexte), value); }
        public string OffsetTexte { get => LireTexte(nameof(OffsetTexte)); set => EcrireTexte(nameof(OffsetTexte), value); }
        public string BinningTexte { get => LireTexte(nameof(BinningTexte)); set => EcrireTexte(nameof(BinningTexte), value); }
        public string FiltreTexte { get => LireTexte(nameof(FiltreTexte)); set => EcrireTexte(nameof(FiltreTexte), value); }
        public string RayonRechercheTexte { get => LireTexte(nameof(RayonRechercheTexte)); set => EcrireTexte(nameof(RayonRechercheTexte), value); }
        public string DistanceTexte { get => LireTexte(nameof(DistanceTexte)); set => EcrireTexte(nameof(DistanceTexte), value); }
        public string VitesseTexte { get => LireTexte(nameof(VitesseTexte)); set => EcrireTexte(nameof(VitesseTexte), value); }

        /// <summary>Sens de rotation entre les photos : 0 = réglage TPPA, 1 = Est, 2 = Ouest.</summary>
        public int SensDeplacementIndex {
            get => reglages.GetValueInt32(nameof(SensDeplacementIndex), 0);
            set { reglages.SetValueInt32(nameof(SensDeplacementIndex), value); RaisePropertyChanged(); }
        }

        // Conversions texte -> nombre. Résultat null = champ vide ou illisible
        // = "ne rien transmettre, TPPA garde son réglage".
        // La virgule française est acceptée ("2,5" vaut "2.5").
        private static object VersDouble(string texte) {
            texte = texte?.Trim().Replace(',', '.');
            if (string.IsNullOrEmpty(texte)) { return null; }
            return double.TryParse(texte, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (object)d : null;
        }

        private static object VersEntier(string texte) {
            texte = texte?.Trim();
            if (string.IsNullOrEmpty(texte)) { return null; }
            return int.TryParse(texte, out var i) ? (object)i : null;
        }

        private static object VersPetitEntier(string texte) {
            texte = texte?.Trim();
            if (string.IsNullOrEmpty(texte)) { return null; }
            return short.TryParse(texte, out var s) ? (object)s : null;
        }

        // ------------------------------------------------------------------
        // Boutons
        // ------------------------------------------------------------------

        public ICommand DemarrerCommand { get; }
        public ICommand ArreterCommand { get; }

        private async void Demarrer() {
            // Contrôles avant de lancer quoi que ce soit : c'est plus clair
            // qu'un blocage silencieux ou une bulle d'erreur discrète
            if (!cameraMediator.GetInfo().Connected) {
                Avertissement = "⚠ La caméra n'est pas connectée. Allez dans l'onglet Équipement > Caméra, connectez-la, puis revenez.";
                RaisePropertyChanged(nameof(Avertissement));
                return;
            }
            var monture = telescopeMediator.GetInfo();
            if (MontureGoto && !monture.Connected) {
                Avertissement = "⚠ La monture n'est pas connectée. Allez dans l'onglet Équipement > Monture, connectez-la, puis revenez. (Ou décochez « GoTo » si vous tournez la monture à la main.)";
                RaisePropertyChanged(nameof(Avertissement));
                return;
            }
            // Une monture "parquée" est verrouillée en position repos :
            // elle refusera de bouger et TPPA échouera sans photo
            if (monture.Connected && monture.AtPark) {
                Avertissement = "⚠ La monture est parquée (position repos). Déparquez-la : onglet Équipement > Monture, bouton « Unpark », puis revenez cliquer sur Démarrer.";
                RaisePropertyChanged(nameof(Avertissement));
                return;
            }

            Avertissement = "";
            DetailTechnique = "";
            tppaADonneSigneDeVie = false;
            demarreLe = DateTime.UtcNow;
            phase = Phase.Mesure;
            NotifierToutChange();

            // Le "bon de commande" pour TPPA : les 3 options principales
            // toujours transmises, les réglages avancés seulement s'ils
            // ont été remplis (null = TPPA garde son réglage habituel)
            var contenu = new ContenuDemarrage {
                ManualMode = !MontureGoto,
                StartFromCurrentPosition = DemarrerSurPlace,
                AlignmentTolerance = ToleranceArcmin,
                ExposureTime = VersDouble(PoseTexte),
                Gain = VersEntier(GainTexte),
                Offset = VersEntier(OffsetTexte),
                Binning = VersPetitEntier(BinningTexte),
                Filter = string.IsNullOrWhiteSpace(FiltreTexte) ? null : FiltreTexte.Trim(),
                SearchRadius = VersDouble(RayonRechercheTexte),
                TargetDistance = VersEntier(DistanceTexte),
                MoveRate = VersEntier(VitesseTexte),
                EastDirection = SensDeplacementIndex == 0 ? null : (object)(SensDeplacementIndex == 1)
            };

            await messageBroker.Publish(new MessageDemarrerAlignement(contenu));
        }

        private async void Arreter() {
            phase = Phase.Attente;
            Avertissement = "";
            DetailTechnique = "";
            NotifierToutChange();
            await messageBroker.Publish(new MessageArreterAlignement());
        }

        // ------------------------------------------------------------------
        // Propriétés affichées par l'écran (le .xaml s'y "branche")
        // ------------------------------------------------------------------

        public bool EnAttente => phase == Phase.Attente;
        public bool EnMesure => phase == Phase.Mesure;
        public bool EnAjustement => phase == Phase.Ajustement;

        public bool TppaAbsent { get; }

        /// <summary>Message d'alerte affiché en orange (vide = pas d'alerte).</summary>
        public string Avertissement { get; private set; } = "";

        /// <summary>Ce que TPPA fait en ce moment (texte technique, en anglais).</summary>
        public string DetailTechnique { get; private set; } = "";

        public string ErreurTotaleTexte { get; private set; } = "—";
        public string NiveauTexte { get; private set; } = "";
        public Brush CouleurEtat { get; private set; } = BrosseGris;

        public bool AzimutActif { get; private set; }
        public bool AltitudeActif { get; private set; }
        public string FlecheAzimut { get; private set; } = "";
        public string FlecheAltitude { get; private set; } = "";
        public string ConsigneAzimut { get; private set; } = "";
        public string ConsigneAltitude { get; private set; } = "";
        public string AzimutTexte { get; private set; } = "";
        public string AltitudeTexte { get; private set; } = "";

        /// <summary>Prévient l'écran que les valeurs ont changé, pour qu'il se redessine.</summary>
        private void NotifierToutChange() {
            RaisePropertyChanged(string.Empty); // chaîne vide = "tout a changé"
        }

        // Couleurs de l'état ("Freeze" = optimisation standard WPF pour
        // les objets graphiques qui ne changeront plus)
        private static readonly Brush BrosseVert = CreerBrosse(46, 125, 50);
        private static readonly Brush BrosseOrange = CreerBrosse(230, 145, 10);
        private static readonly Brush BrosseRouge = CreerBrosse(198, 40, 40);
        private static readonly Brush BrosseGris = CreerBrosse(85, 85, 85);

        private static Brush CreerBrosse(byte r, byte g, byte b) {
            var brosse = new SolidColorBrush(Color.FromRgb(r, g, b));
            brosse.Freeze();
            return brosse;
        }
    }

    /// <summary>
    /// Une commande minimale pour relier un bouton de l'écran à une action C#.
    /// (WPF impose ce petit emballage appelé ICommand.)
    /// </summary>
    public class CommandeSimple : ICommand {
        private readonly Action action;

        public CommandeSimple(Action action) {
            this.action = action;
        }

        public event EventHandler CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter) => action();
    }
}
