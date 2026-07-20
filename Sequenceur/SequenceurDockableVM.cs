using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;

namespace ModeDebutant.Sequenceur {

    /// <summary>
    /// Le "cerveau" du panneau « Séquenceur simplifié ».
    ///
    /// Principe de fonctionnement :
    /// 1. L'utilisateur choisit (au besoin) une cible par son nom : on la
    ///    cherche dans la base d'objets du ciel livrée avec N.I.N.A., puis
    ///    un bouton pointe le télescope dessus (GoTo).
    /// 2. Il règle trois chiffres : nombre de photos, temps de pose, gain.
    /// 3. Le bouton GO fabrique une vraie séquence pour le séquenceur avancé
    ///    de N.I.N.A. (via son interface publique ISequenceMediator) et la
    ///    lance. N.I.N.A. fait ensuite tout le travail : c'est exactement
    ///    comme si la séquence avait été construite à la main dans l'onglet
    ///    séquenceur, mais sans le labyrinthe de réglages.
    /// 4. Pendant la série : avancement (photo x sur N), vignette de la
    ///    dernière photo et qualité de mise au point (nombre d'étoiles + HFR).
    /// </summary>
    [Export(typeof(IDockableVM))]
    public class SequenceurDockableVM : DockableVM {

        private readonly IProfileService profileService;
        private readonly ISequenceMediator sequenceMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IImageHistoryVM imageHistoryVM;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IFocuserMediator focuserMediator;
        private readonly IApplicationStatusMediator applicationStatusMediator;
        private readonly IMeridianFlipVMFactory meridianFlipVMFactory;
        private readonly INighttimeCalculator nighttimeCalculator;
        private readonly IFramingAssistantVM framingAssistantVM;
        private readonly IApplicationMediator applicationMediator;
        private readonly IPlanetariumFactory planetariumFactory;

        // Coffre à réglages fourni par N.I.N.A. : mémorise nos options
        // dans le profil actif, d'une session à l'autre
        private readonly PluginOptionsAccessor reglages;

        // Les phases possibles de notre écran. AttenteDarks = les photos
        // sont finies, on attend que l'utilisateur couvre le télescope
        // avant d'enchaîner les darks.
        private enum Phase { Preparation, EnCours, AttenteDarks, Terminee }
        private Phase phase = Phase.Preparation;

        // La boucle « répéter N fois » de la séquence en cours : on la garde
        // sous la main pour afficher l'avancement (elle compte elle-même)
        private LoopCondition boucle;
        private int nbPhotosTotal;
        private double poseSecondesEnCours;
        private bool arretDemande;

        // Mémoire de la série en cours, pour fabriquer des darks IDENTIQUES
        // (même pose, même gain...) et pour le bilan final
        private int gainEnCours;
        private int offsetEnCours;
        private short binningEnCours;
        private string nomCibleEnCours = "";
        private bool darksEnCours; // true = la série actuelle, ce sont les darks
        private int nbLightsFaits; // photos "utiles" prises avant les darks
        private DateTime serieDebut; // pour la durée totale au bilan

        // Le carnet de notes de la nuit : une ligne par photo analysée
        // (étoiles, netteté, verdict) — la matière du bilan de fin de nuit
        private sealed class NotePhoto {
            public DateTime Heure;
            public int Etoiles;
            public double Hfr;
            public int Niveau; // 0 = validée, 1 = à surveiller, 2 = problème
        }
        private readonly List<NotePhoto> carnetNuit = new List<NotePhoto>();

        // Marge par photo pour estimer la durée (téléchargement, dithering...)
        private const double MargeParPhotoSecondes = 5.0;

        [ImportingConstructor]
        public SequenceurDockableVM(
            IProfileService profileService,
            ISequenceMediator sequenceMediator,
            ICameraMediator cameraMediator,
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM,
            IFilterWheelMediator filterWheelMediator,
            IFocuserMediator focuserMediator,
            IApplicationStatusMediator applicationStatusMediator,
            IMeridianFlipVMFactory meridianFlipVMFactory,
            INighttimeCalculator nighttimeCalculator,
            IFramingAssistantVM framingAssistantVM,
            IApplicationMediator applicationMediator,
            IPlanetariumFactory planetariumFactory) : base(profileService) {
            this.profileService = profileService;
            this.sequenceMediator = sequenceMediator;
            this.cameraMediator = cameraMediator;
            this.telescopeMediator = telescopeMediator;
            this.guiderMediator = guiderMediator;
            this.imagingMediator = imagingMediator;
            this.imageSaveMediator = imageSaveMediator;
            this.imageHistoryVM = imageHistoryVM;
            this.filterWheelMediator = filterWheelMediator;
            this.focuserMediator = focuserMediator;
            this.applicationStatusMediator = applicationStatusMediator;
            this.meridianFlipVMFactory = meridianFlipVMFactory;
            this.nighttimeCalculator = nighttimeCalculator;
            this.framingAssistantVM = framingAssistantVM;
            this.applicationMediator = applicationMediator;
            this.planetariumFactory = planetariumFactory;

            reglages = new PluginOptionsAccessor(profileService, Guid.Parse("3d87e151-d363-4708-bce9-5db3356abccc"));

            // À chaque image capturée puis traitée par N.I.N.A., on reçoit une
            // version affichable : vignette + qualité de mise au point
            imagingMediator.ImagePrepared += QuandImagePrete;

            Title = "Séquenceur simplifié";

            // Icône de l'en-tête : un appareil photo stylisé (boîtier +
            // objectif + déclencheur), dessiné en "chemins" vectoriels
            var icone = new GeometryGroup();
            icone.Children.Add(Geometry.Parse("M 10,30 L 30,30 L 38,18 L 62,18 L 70,30 L 90,30 L 90,85 L 10,85 Z"));
            icone.Children.Add(Geometry.Parse("M 50,38 A 18,18 0 1 0 50,74 A 18,18 0 1 0 50,38 Z M 50,46 A 10,10 0 1 1 50,66 A 10,10 0 1 1 50,46 Z"));
            icone.Children.Add(Geometry.Parse("M 74,36 L 84,36 L 84,44 L 74,44 Z"));
            icone.Freeze();
            ImageGeometry = icone;

            RechercherCommand = new CommandeAsync(Rechercher);
            PointerCommand = new CommandeAsync(Pointer);
            DemarrerCommand = new CommandeAsync(Demarrer);
            ArreterCommand = new CommandeSimple2(Arreter);
            NouvelleSerieCommand = new CommandeSimple2(NouvelleSerie);
            VoirSequenceurCommand = new CommandeSimple2(VoirSequenceur);
            LancerDarksCommand = new CommandeAsync(LancerDarks);
            PasserDarksCommand = new CommandeSimple2(PasserDarks);
            OuvrirBilanCommand = new CommandeSimple2(OuvrirBilan);
            GenererBilanCommand = new CommandeSimple2(GenererBilanMaintenant);
            NouvelleSessionCommand = new CommandeSimple2(NouvelleSession);
            TesterAlerteCommand = new CommandeAsync(TesterAlerte);
            ProposerCiblesCommand = new CommandeAsync(ProposerCibles);
            ActualiserMeteoCommand = new CommandeAsync(ChargerMeteo);

            // La météo de la nuit se charge au démarrage (en tâche de fond,
            // échec silencieux) et se recharge si la position change
            _ = ChargerMeteo();
            profileService.LocationChanged += (s, e) => _ = ChargerMeteo();
        }

        // ------------------------------------------------------------------
        // Météo de la nuit (couverture nuageuse, service gratuit open-meteo)
        // ------------------------------------------------------------------

        // Les prévisions heure par heure : (heure locale, % de nuages)
        private List<Tuple<DateTime, int>> previsionsNuages;

        // Premier clic sur GO avec mauvaise météo = avertissement ;
        // second clic = on y va quand même (c'est vous le chef)
        private bool meteoConfirmee;

        /// <summary>Résumé : « Ciel dégagé de 22 h à 2 h, nuages ensuite ».</summary>
        public string MeteoTexte { get; private set; } = "";

        /// <summary>La frise : « 21h☀ 22h☀ 23h⛅ 0h☁ … »</summary>
        public string MeteoFrise { get; private set; } = "";

        public ICommand ActualiserMeteoCommand { get; }

        private async Task ChargerMeteo() {
            double lat = profileService.ActiveProfile.AstrometrySettings.Latitude;
            double lon = profileService.ActiveProfile.AstrometrySettings.Longitude;
            if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001) { return; } // position non réglée

            try {
                var url = "https://api.open-meteo.com/v1/forecast?latitude="
                    + lat.ToString(CultureInfo.InvariantCulture)
                    + "&longitude=" + lon.ToString(CultureInfo.InvariantCulture)
                    + "&hourly=cloud_cover&forecast_days=2&timezone=auto";
                var json = await clientWeb.GetStringAsync(url);

                var previsions = new List<Tuple<DateTime, int>>();
                using (var doc = System.Text.Json.JsonDocument.Parse(json)) {
                    var horaire = doc.RootElement.GetProperty("hourly");
                    var heures = horaire.GetProperty("time");
                    var nuages = horaire.GetProperty("cloud_cover");
                    for (int i = 0; i < heures.GetArrayLength() && i < nuages.GetArrayLength(); i++) {
                        if (nuages[i].ValueKind != System.Text.Json.JsonValueKind.Number) { continue; }
                        previsions.Add(Tuple.Create(
                            DateTime.Parse(heures[i].GetString(), CultureInfo.InvariantCulture),
                            (int)nuages[i].GetDouble()));
                    }
                }
                previsionsNuages = previsions;
                ResumerMeteo();
            } catch {
                // Pas d'Internet ou service en panne : la carte météo
                // disparaît simplement (jamais bloquant)
                MeteoTexte = "";
                MeteoFrise = "";
                RaisePropertyChanged(nameof(MeteoTexte));
                RaisePropertyChanged(nameof(MeteoFrise));
            }
        }

        /// <summary>Traduit les pourcentages en une phrase et une frise lisibles.</summary>
        private void ResumerMeteo() {
            // La « nuit » étudiée : de ce soir 21 h (ou maintenant si on y
            // est déjà) jusqu'à demain 6 h
            var maintenant = DateTime.Now;
            var debutNuit = maintenant.Date.AddHours(21);
            if (maintenant > debutNuit) { debutNuit = new DateTime(maintenant.Year, maintenant.Month, maintenant.Day, maintenant.Hour, 0, 0); }
            var finNuit = maintenant.Date.AddDays(1).AddHours(6);
            if (maintenant.Hour < 6) { debutNuit = maintenant.Date.AddHours(maintenant.Hour); finNuit = maintenant.Date.AddHours(6); }

            var nuit = previsionsNuages.Where(p => p.Item1 >= debutNuit && p.Item1 <= finNuit).ToList();
            if (nuit.Count == 0) { return; }

            // La frise emoji, toutes les heures
            var frise = "";
            foreach (var p in nuit) {
                string emoji = p.Item2 < 25 ? "☀" : p.Item2 < 50 ? "🌤" : p.Item2 < 75 ? "⛅" : "☁";
                frise += p.Item1.Hour + "h" + emoji + "  ";
            }
            MeteoFrise = frise.TrimEnd();

            // La phrase : on cherche la plus longue période dégagée (≤ 35 %)
            int debutMeilleur = -1, longueurMeilleure = 0, debutCourant = -1, longueurCourante = 0;
            for (int i = 0; i < nuit.Count; i++) {
                if (nuit[i].Item2 <= 35) {
                    if (debutCourant < 0) { debutCourant = i; }
                    longueurCourante++;
                    if (longueurCourante > longueurMeilleure) { longueurMeilleure = longueurCourante; debutMeilleur = debutCourant; }
                } else {
                    debutCourant = -1; longueurCourante = 0;
                }
            }

            if (longueurMeilleure == 0) {
                MeteoTexte = "☁ Très nuageux toute la nuit — pas la bonne nuit pour une longue série.";
            } else if (longueurMeilleure >= nuit.Count - 1) {
                MeteoTexte = "✨ Ciel dégagé toute la nuit — foncez !";
            } else {
                var de = nuit[debutMeilleur].Item1;
                var jusqua = nuit[debutMeilleur + longueurMeilleure - 1].Item1.AddHours(1);
                MeteoTexte = "Ciel dégagé de " + de.Hour + " h à " + jusqua.Hour + " h, nuageux le reste de la nuit.";
            }
            RaisePropertyChanged(nameof(MeteoTexte));
            RaisePropertyChanged(nameof(MeteoFrise));
        }

        // ------------------------------------------------------------------
        // Alertes sur le téléphone (service gratuit ntfy.sh, sans compte)
        //
        // Principe : l'utilisateur installe l'application « ntfy » sur son
        // téléphone et s'abonne à un canal (un simple nom). Le plugin envoie
        // alors ses alertes par une requête web sur ce canal : série lancée,
        // photo douteuse, série terminée, erreur. Tout échec est silencieux :
        // les alertes sont un bonus, jamais un blocage.
        // ------------------------------------------------------------------

        private static readonly HttpClient clientWeb = CreerClientWeb();

        private static HttpClient CreerClientWeb() {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ModeDebutant-NINA-Plugin/0.2");
            return client;
        }

        /// <summary>Interrupteur principal des alertes téléphone.</summary>
        public bool AlerteTelActive {
            get => reglages.GetValueBoolean(nameof(AlerteTelActive), false);
            set { reglages.SetValueBoolean(nameof(AlerteTelActive), value); RaisePropertyChanged(); }
        }

        /// <summary>
        /// Le nom du canal ntfy. Généré une fois avec un suffixe aléatoire
        /// (les canaux ntfy sont publics : un nom devinable = des curieux).
        /// </summary>
        public string CanalNtfy {
            get {
                var canal = reglages.GetValueString(nameof(CanalNtfy), "");
                if (string.IsNullOrWhiteSpace(canal)) {
                    canal = "nina-debutant-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    reglages.SetValueString(nameof(CanalNtfy), canal);
                }
                return canal;
            }
            set { reglages.SetValueString(nameof(CanalNtfy), value); RaisePropertyChanged(); }
        }

        /// <summary>Petit retour sous le bouton « Tester » (envoyée / échec).</summary>
        public string MessageAlerteTel { get; private set; } = "";

        public ICommand TesterAlerteCommand { get; }

        private async Task TesterAlerte() {
            MessageAlerteTel = "Envoi de la notification d'essai…";
            RaisePropertyChanged(nameof(MessageAlerteTel));
            bool reussi = await EnvoyerVersNtfy("Essai reussi",
                "🎉 Les alertes du Mode Débutant fonctionnent ! Vous recevrez ici les nouvelles de vos séries de photos.", false);
            MessageAlerteTel = reussi
                ? "✅ Notification envoyée — elle doit apparaître sur votre téléphone dans les secondes qui viennent."
                : "⚠ Échec de l'envoi. Vérifiez votre connexion Internet et le nom du canal (lettres, chiffres et tirets uniquement).";
            RaisePropertyChanged(nameof(MessageAlerteTel));
        }

        /// <summary>Envoie une alerte si l'interrupteur est activé (échec silencieux).</summary>
        private async void EnvoyerAlerte(string titre, string texte, bool urgente = false) {
            if (!AlerteTelActive) { return; }
            await EnvoyerVersNtfy(titre, texte, urgente);
        }

        /// <summary>
        /// L'alerte de lancement, avec accusé de réception affiché dans la
        /// ligne d'infos de la série : « 📱 alerte envoyée ✔ » = la chaîne
        /// téléphone fonctionne, vous pouvez quitter le poste tranquille.
        /// </summary>
        private async Task ConfirmerAlerteLancement(string texte) {
            bool envoyee = await EnvoyerVersNtfy("Serie lancee", texte, false);
            NotesSerie += envoyee
                ? " · 📱 alerte envoyée ✔"
                : " · ⚠ alerte téléphone NON partie (Internet ? canal ?)";
            RaisePropertyChanged(nameof(NotesSerie));
        }

        /// <summary>
        /// L'appel web vers ntfy.sh. Le titre reste sans accents ni emoji
        /// (il voyage dans un en-tête HTTP, qui n'aime que l'ASCII) ; tout le
        /// français et les emojis vont dans le corps du message.
        /// </summary>
        private async Task<bool> EnvoyerVersNtfy(string titre, string texte, bool urgente) {
            var canal = CanalNtfy?.Trim();
            if (string.IsNullOrEmpty(canal)) { return false; }
            try {
                using var demande = new HttpRequestMessage(HttpMethod.Post, "https://ntfy.sh/" + Uri.EscapeDataString(canal)) {
                    Content = new StringContent(texte, System.Text.Encoding.UTF8)
                };
                demande.Headers.TryAddWithoutValidation("Title", titre);
                demande.Headers.TryAddWithoutValidation("Priority", urgente ? "high" : "default");
                demande.Headers.TryAddWithoutValidation("Tags", urgente ? "warning" : "telescope");
                var reponse = await clientWeb.SendAsync(demande);
                return reponse.IsSuccessStatusCode;
            } catch {
                return false; // pas d'Internet, service en panne... tant pis
            }
        }

        // Panneau rangé du côté "outils" de l'onglet imagerie
        public override bool IsTool => true;

        // ------------------------------------------------------------------
        // Recherche de la cible par son nom
        // ------------------------------------------------------------------

        /// <summary>Ce que l'utilisateur a tapé : « M31 », « NGC 7000 »...</summary>
        public string CibleTexte { get; set; } = "";

        /// <summary>Les objets trouvés dans la base du ciel de N.I.N.A.</summary>
        public ObservableCollection<CibleTrouvee> Resultats { get; } = new ObservableCollection<CibleTrouvee>();

        private CibleTrouvee cibleChoisie;

        /// <summary>L'objet sélectionné dans la liste (null = aucun).</summary>
        public CibleTrouvee CibleChoisie {
            get => cibleChoisie;
            set {
                cibleChoisie = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CibleEstChoisie));
            }
        }

        public bool CibleEstChoisie => CibleChoisie != null;

        /// <summary>Petit message sous la zone de recherche (résultat, pointage...).</summary>
        public string MessageCible { get; private set; } = "";

        public ICommand RechercherCommand { get; }

        private async Task Rechercher() {
            var texte = CibleTexte?.Trim();
            if (string.IsNullOrEmpty(texte)) {
                MessageCible = "Tapez un nom d'objet : M31, NGC 7000, Orion…";
                RaisePropertyChanged(nameof(MessageCible));
                return;
            }

            MessageCible = "Recherche en cours…";
            RaisePropertyChanged(nameof(MessageCible));
            Resultats.Clear();
            CibleChoisie = null;

            try {
                // La base d'objets du ciel installée avec N.I.N.A.
                // (la même que celle de son atlas du ciel)
                var baseCiel = new DatabaseInteraction();
                var criteres = new DatabaseInteraction.DeepSkyObjectSearchParams {
                    ObjectName = texte,
                    Limit = 8,
                    // Les plus brillants d'abord (magnitude petite = brillant)
                    SearchOrder = { Field = "magnitude", Direction = "ASC" }
                };
                var objets = await baseCiel.GetDeepSkyObjects(
                    string.Empty,
                    profileService.ActiveProfile.AstrometrySettings.Horizon,
                    criteres,
                    CancellationToken.None);

                // Où sommes-nous sur Terre ? (pour savoir si l'objet est
                // au-dessus de l'horizon en ce moment)
                var latitude = Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Latitude);
                var longitude = Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Longitude);
                var altitudeLieu = profileService.ActiveProfile.AstrometrySettings.Elevation;
                var champ = CalculerChampArcmin(); // pour annoter le cadrage

                foreach (var objet in objets) {
                    Resultats.Add(new CibleTrouvee(objet, latitude, longitude, altitudeLieu,
                        AnnoterCadre(objet.Size, champ)));
                }

                if (Resultats.Count == 0) {
                    MessageCible = "Aucun objet trouvé avec ce nom. Essayez « M31 », « NGC 7000 » ou un nom anglais (« Orion Nebula »).";
                } else {
                    MessageCible = "Cliquez sur un objet dans la liste, puis sur « Pointer le télescope ».";
                    MessageCible += champ != null
                        ? "  (Votre champ : " + champ.Item1.ToString("0") + "′ × " + champ.Item2.ToString("0") + "′.)"
                        : "  💡 Connectez la caméra (et renseignez focale + taille de pixel dans les options N.I.N.A.) pour vérifier le cadrage.";
                    // S'il n'y a qu'un seul résultat, on le sélectionne d'office
                    if (Resultats.Count == 1) { CibleChoisie = Resultats[0]; }
                }
            } catch (Exception ex) {
                MessageCible = "⚠ La recherche a échoué : " + ex.Message;
            }
            RaisePropertyChanged(nameof(MessageCible));
        }

        // ------------------------------------------------------------------
        // « Que photographier ce soir ? » — suggestions automatiques
        //
        // Tout se calcule en local (base d'objets + position du profil) :
        //  1. candidats brillants (magnitude <= 9) et étendus (>= 5')
        //  2. altitude simulée sur la soirée (4 instants espacés d'1 h 30) :
        //     il faut être déjà bien placé ET rester haut plusieurs heures
        //  3. la Lune : si elle est éclairée à plus de 40 %, on écarte tout
        //     ce qui est à moins de 30° d'elle (son halo noie les nébuleuses)
        //  4. note finale = hauteur + brillance + distance à la Lune
        // ------------------------------------------------------------------

        public ICommand ProposerCiblesCommand { get; }

        private async Task ProposerCibles() {
            double lat = profileService.ActiveProfile.AstrometrySettings.Latitude;
            double lon = profileService.ActiveProfile.AstrometrySettings.Longitude;
            double elevation = profileService.ActiveProfile.AstrometrySettings.Elevation;

            if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001) {
                MessageCible = "⚠ Réglez d'abord votre position (carte « 📍 Votre position » du panneau Alignement polaire simplifié) : les suggestions dépendent de l'endroit où vous êtes.";
                RaisePropertyChanged(nameof(MessageCible));
                return;
            }

            MessageCible = "🌌 Calcul des meilleures cibles pour ce soir, chez vous… (quelques secondes)";
            RaisePropertyChanged(nameof(MessageCible));
            Resultats.Clear();
            CibleChoisie = null;

            try {
                var horizon = profileService.ActiveProfile.AstrometrySettings.Horizon;
                var champ = CalculerChampArcmin(); // lu ici (thread d'interface),
                                                   // utilisé dans le calcul de fond
                // Le calcul (400 objets × 4 positions) part en tâche de fond
                // pour ne pas figer l'interface
                var suggestions = await Task.Run(() => CalculerSuggestions(lat, lon, elevation, horizon, champ));

                foreach (var suggestion in suggestions) { Resultats.Add(suggestion); }

                double illumination = AstroUtil.GetMoonIllumination(DateTime.Now);
                string lune = " (Lune éclairée à " + (illumination * 100).ToString("0") + " %)";
                MessageCible = suggestions.Count == 0
                    ? "Rien d'idéal en ce moment" + lune + " : objets trop bas ou trop près de la Lune. Réessayez à une autre heure."
                    : "Les " + suggestions.Count + " meilleures cibles de ce soir" + lune + ". Cliquez-en une, puis « Pointer le télescope ».";
                if (suggestions.Count > 0) {
                    MessageCible += champ != null
                        ? "  (Classées aussi selon votre champ : " + champ.Item1.ToString("0") + "′ × " + champ.Item2.ToString("0") + "′.)"
                        : "  💡 Connectez la caméra pour que les suggestions tiennent compte de votre cadrage.";
                }
            } catch (Exception ex) {
                MessageCible = "⚠ Le calcul a échoué : " + ex.Message;
            }
            RaisePropertyChanged(nameof(MessageCible));
        }

        private List<CibleTrouvee> CalculerSuggestions(double lat, double lon, double elevation, NINA.Core.Model.CustomHorizon horizon, Tuple<double, double> champ) {
            // La fenêtre étudiée : dès maintenant si c'est la nuit, sinon à
            // partir de 21 h ce soir — puis 4 instants espacés d'1 h 30
            var maintenant = DateTime.Now;
            var debut = maintenant.Hour >= 6 && maintenant.Hour < 17 ? maintenant.Date.AddHours(21) : maintenant;
            var instants = new[] { debut, debut.AddHours(1.5), debut.AddHours(3.0), debut.AddHours(4.5) };

            // Où est la Lune, et combien éclaire-t-elle ?
            double illumination = AstroUtil.GetMoonIllumination(debut);
            var observateur = new ObserverInfo { Latitude = lat, Longitude = lon, Elevation = elevation };
            var lune = AstroUtil.GetMoonPosition(debut, AstroUtil.GetJulianDate(debut), observateur);
            double raLuneDeg = lune.RA * 15.0; // NOVAS donne l'ascension droite en heures
            double decLuneDeg = lune.Dec;

            // Les candidats : brillants, étendus, les plus lumineux d'abord
            var baseCiel = new DatabaseInteraction();
            var criteres = new DatabaseInteraction.DeepSkyObjectSearchParams {
                Limit = 400,
                Magnitude = { Thru = 9.0 },
                Size = { From = 5.0 },
                SearchOrder = { Field = "magnitude", Direction = "ASC" }
            };
            var objets = baseCiel.GetDeepSkyObjects(string.Empty, horizon, criteres, CancellationToken.None).Result;

            var latAngle = Angle.ByDegree(lat);
            var lonAngle = Angle.ByDegree(lon);
            var notes = new List<Tuple<double, CibleTrouvee>>();

            foreach (var objet in objets) {
                // L'altitude au fil de la soirée
                double altDebut = objet.Coordinates.Transform(latAngle, lonAngle, instants[0]).Altitude.Degree;
                if (altDebut < 25) { continue; } // trop bas dès le départ

                double maxAlt = altDebut;
                var heureMax = instants[0];
                int instantsBienHaut = altDebut > 30 ? 1 : 0;
                for (int i = 1; i < instants.Length; i++) {
                    double alt = objet.Coordinates.Transform(latAngle, lonAngle, instants[i]).Altitude.Degree;
                    if (alt > maxAlt) { maxAlt = alt; heureMax = instants[i]; }
                    if (alt > 30) { instantsBienHaut++; }
                }
                if (maxAlt < 35 || instantsBienHaut < 2) { continue; } // jamais assez haut, ou pas assez longtemps

                // La Lune gêne-t-elle ?
                double distanceLune = SeparationDegres(objet.Coordinates.RADegrees, objet.Coordinates.Dec, raLuneDeg, decLuneDeg);
                if (illumination > 0.4 && distanceLune < 30) { continue; }

                double magnitude = objet.Magnitude ?? 9.0;
                double note = maxAlt                          // haut = bien
                    + (10.0 - magnitude) * 5.0                // brillant = bien
                    + Math.Min(distanceLune, 60.0) / 3.0;     // loin de la Lune = bien

                // Le cadrage compte aussi : bonus pour les objets qui font de
                // belles images dans VOTRE champ, malus pour les timbres-poste
                // et ce qui déborde largement
                var conseilCadre = AnnoterCadre(objet.Size, champ);
                if (champ != null && objet.Size.HasValue && objet.Size.Value > 0) {
                    double ratio = objet.Size.Value / Math.Min(champ.Item1, champ.Item2);
                    if (ratio >= 0.15 && ratio <= 1.1) { note += 15; }        // taille idéale
                    else if (ratio < 0.04) { note -= 25; }                    // timbre-poste
                    else if (ratio > 1.1) { note -= 5; }                      // déborde un peu
                }

                var complement = "🌟 au mieux " + maxAlt.ToString("0") + "° vers " + heureMax.ToString("HH\\h");
                if (!string.IsNullOrEmpty(conseilCadre)) { complement += "  ·  " + conseilCadre; }
                notes.Add(Tuple.Create(note, new CibleTrouvee(objet, latAngle, lonAngle, elevation, complement)));
            }

            return notes.OrderByDescending(x => x.Item1).Take(5).Select(x => x.Item2).ToList();
        }

        // ------------------------------------------------------------------
        // Le cadrage : votre champ de vision comparé à la taille de l'objet
        // ------------------------------------------------------------------

        /// <summary>
        /// Champ de vision (largeur, hauteur) en minutes d'arc, calculé avec
        /// la focale du profil et le capteur de la caméra connectée.
        /// null = pas calculable (focale/pixel non renseignés ou caméra
        /// déconnectée) — dans ce cas on n'annote simplement rien.
        /// </summary>
        private Tuple<double, double> CalculerChampArcmin() {
            try {
                double focaleMm = profileService.ActiveProfile.TelescopeSettings.FocalLength;
                double pixelMicrons = profileService.ActiveProfile.CameraSettings.PixelSize;
                var camera = cameraMediator.GetInfo();
                if (focaleMm <= 0 || pixelMicrons <= 0 || !camera.Connected || camera.XSize <= 0 || camera.YSize <= 0) {
                    return null;
                }
                // La formule classique : 206,265 × taille de pixel (µm) /
                // focale (mm) = secondes d'arc par pixel
                double arcsecParPixel = 206.265 * pixelMicrons / focaleMm;
                return Tuple.Create(camera.XSize * arcsecParPixel / 60.0, camera.YSize * arcsecParPixel / 60.0);
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Traduit « taille de l'objet vs petit côté du cadre » en conseil.
        /// Évite les deux déceptions classiques du débutant : l'objet
        /// gigantesque qui déborde (M31 !) et le timbre-poste invisible.
        /// </summary>
        private static string AnnoterCadre(double? tailleArcmin, Tuple<double, double> champ) {
            if (champ == null || !tailleArcmin.HasValue || tailleArcmin.Value <= 0) { return ""; }
            double petitCote = Math.Min(champ.Item1, champ.Item2);
            if (petitCote <= 0) { return ""; }
            double ratio = tailleArcmin.Value / petitCote;

            if (ratio > 1.1) { return "⚠ déborde de votre cadre — visez le cœur"; }
            if (ratio >= 0.5) { return "🖼 remplit superbement votre cadre"; }
            if (ratio >= 0.15) { return "🖼 belle taille dans votre cadre"; }
            if (ratio >= 0.04) { return "assez petit dans votre cadre"; }
            return "⚠ minuscule pour votre champ (timbre-poste)";
        }

        /// <summary>Écart angulaire entre deux points du ciel, en degrés.</summary>
        private static double SeparationDegres(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg) {
            double versRadians = Math.PI / 180.0;
            double d1 = dec1Deg * versRadians, d2 = dec2Deg * versRadians;
            double cosSep = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos((ra1Deg - ra2Deg) * versRadians);
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosSep))) / versRadians;
        }

        // ------------------------------------------------------------------
        // Pointage du télescope (GoTo)
        // ------------------------------------------------------------------

        public ICommand PointerCommand { get; }

        private async Task Pointer() {
            if (CibleChoisie == null) { return; }

            var monture = telescopeMediator.GetInfo();
            if (!monture.Connected) {
                MessageCible = "⚠ La monture n'est pas connectée. Allez dans l'onglet Équipement > Monture, connectez-la, puis revenez.";
                RaisePropertyChanged(nameof(MessageCible));
                return;
            }
            if (monture.AtPark) {
                MessageCible = "⚠ La monture est parquée (position repos). Déparquez-la : onglet Équipement > Monture, bouton « Unpark ».";
                RaisePropertyChanged(nameof(MessageCible));
                return;
            }
            if (!CibleChoisie.EstVisible) {
                MessageCible = "⚠ « " + CibleChoisie.Nom + " » est sous l'horizon en ce moment : le télescope ne peut pas le viser. Choisissez un autre objet (ou réessayez plus tard dans la nuit).";
                RaisePropertyChanged(nameof(MessageCible));
                return;
            }

            MessageCible = "🔭 Pointage en cours vers « " + CibleChoisie.Nom + " »… (le télescope se déplace)";
            RaisePropertyChanged(nameof(MessageCible));

            try {
                var reussi = await telescopeMediator.SlewToCoordinatesAsync(CibleChoisie.Coordonnees, CancellationToken.None);
                MessageCible = reussi
                    ? "✅ Télescope pointé sur « " + CibleChoisie.Nom + " ». Vous pouvez lancer la série."
                    : "⚠ Le pointage a été refusé par la monture. Vérifiez qu'elle est déparquée et que le suivi est actif.";
            } catch (Exception ex) {
                MessageCible = "⚠ Le pointage a échoué : " + ex.Message;
            }
            RaisePropertyChanged(nameof(MessageCible));
        }

        // ------------------------------------------------------------------
        // Réglages de la série (mémorisés dans le profil)
        // ------------------------------------------------------------------

        /// <summary>Nombre de photos à prendre (texte, pour rester tolérant).</summary>
        public string NbPhotosTexte {
            get => reglages.GetValueString(nameof(NbPhotosTexte), "30");
            set {
                reglages.SetValueString(nameof(NbPhotosTexte), value);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(DureeEstimeeTexte));
            }
        }

        /// <summary>Temps de pose de chaque photo, en secondes.</summary>
        public string PoseSecondesTexte {
            get => reglages.GetValueString(nameof(PoseSecondesTexte), "30");
            set {
                reglages.SetValueString(nameof(PoseSecondesTexte), value);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(DureeEstimeeTexte));
            }
        }

        /// <summary>Gain/ISO. Vide = garder le réglage actuel de la caméra.</summary>
        public string GainSerieTexte {
            get => reglages.GetValueString(nameof(GainSerieTexte), "");
            set { reglages.SetValueString(nameof(GainSerieTexte), value); RaisePropertyChanged(); }
        }

        /// <summary>Dithering : petit décalage entre les photos (guidage requis).</summary>
        public bool DitherActif {
            get => reglages.GetValueBoolean(nameof(DitherActif), true);
            set { reglages.SetValueBoolean(nameof(DitherActif), value); RaisePropertyChanged(); }
        }

        /// <summary>Surveiller le passage au méridien et retourner la monture.</summary>
        public bool FlipActif {
            get => reglages.GetValueBoolean(nameof(FlipActif), true);
            set { reglages.SetValueBoolean(nameof(FlipActif), value); RaisePropertyChanged(); }
        }

        /// <summary>Proposer les darks à la fin de la série de photos.</summary>
        public bool DarksActif {
            get => reglages.GetValueBoolean(nameof(DarksActif), false);
            set { reglages.SetValueBoolean(nameof(DarksActif), value); RaisePropertyChanged(); }
        }

        /// <summary>Nombre de darks à prendre (défaut : 15).</summary>
        public string NbDarksTexte {
            get => reglages.GetValueString(nameof(NbDarksTexte), "15");
            set { reglages.SetValueString(nameof(NbDarksTexte), value); RaisePropertyChanged(); }
        }

        // ------------------------------------------------------------------
        // Réglages avancés (section repliée « Tous les réglages »)
        // Un champ vide = garder le réglage habituel de la caméra / ne rien
        // changer. Tous mémorisés dans le profil, comme les options simples.
        // ------------------------------------------------------------------

        /// <summary>Offset de la caméra. Vide = réglage actuel de la caméra.</summary>
        public string OffsetSerieTexte {
            get => reglages.GetValueString(nameof(OffsetSerieTexte), "");
            set { reglages.SetValueString(nameof(OffsetSerieTexte), value); RaisePropertyChanged(); }
        }

        /// <summary>Binning : 1, 2, 3… Vide = 1 (pleine résolution).</summary>
        public string BinningSerieTexte {
            get => reglages.GetValueString(nameof(BinningSerieTexte), "");
            set { reglages.SetValueString(nameof(BinningSerieTexte), value); RaisePropertyChanged(); }
        }

        /// <summary>Nom exact du filtre à mettre en place. Vide = ne pas changer.</summary>
        public string FiltreSerieTexte {
            get => reglages.GetValueString(nameof(FiltreSerieTexte), "");
            set { reglages.SetValueString(nameof(FiltreSerieTexte), value); RaisePropertyChanged(); }
        }

        /// <summary>Dithering toutes les N photos (défaut : 1 = chaque photo).</summary>
        public string DitherFrequenceTexte {
            get => reglages.GetValueString(nameof(DitherFrequenceTexte), "1");
            set { reglages.SetValueString(nameof(DitherFrequenceTexte), value); RaisePropertyChanged(); }
        }

        /// <summary>« environ 25 min » : durée prévisible de la série.</summary>
        public string DureeEstimeeTexte {
            get {
                int nb = EntierOuDefaut(NbPhotosTexte, 0);
                double pose = DoubleOuDefaut(PoseSecondesTexte, 0);
                if (nb <= 0 || pose <= 0) { return ""; }
                var duree = TimeSpan.FromSeconds(nb * (pose + MargeParPhotoSecondes));
                return "Durée totale : environ " + FormatDuree(duree)
                    + " (fin vers " + DateTime.Now.Add(duree).ToString("HH\\hmm") + ")";
            }
        }

        // Conversions texte -> nombre, tolérantes (virgule française acceptée)
        private static int EntierOuDefaut(string texte, int defaut) {
            return int.TryParse(texte?.Trim(), out var i) && i > 0 ? i : defaut;
        }

        private static double DoubleOuDefaut(string texte, double defaut) {
            texte = texte?.Trim().Replace(',', '.');
            return double.TryParse(texte, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : defaut;
        }

        private static string FormatDuree(TimeSpan duree) {
            if (duree.TotalMinutes < 1) { return duree.Seconds + " s"; }
            if (duree.TotalHours < 1) { return duree.Minutes + " min"; }
            return (int)duree.TotalHours + " h " + duree.Minutes.ToString("00");
        }

        // ------------------------------------------------------------------
        // GO : fabrication de la séquence et démarrage
        // ------------------------------------------------------------------

        public ICommand DemarrerCommand { get; }

        private async Task Demarrer() {
            // Contrôles avant de lancer quoi que ce soit
            if (!cameraMediator.GetInfo().Connected) {
                Avertissement = "⚠ La caméra n'est pas connectée. Allez dans l'onglet Équipement > Caméra, connectez-la, puis revenez.";
                RaisePropertyChanged(nameof(Avertissement));
                return;
            }
            var monture = telescopeMediator.GetInfo();
            if (monture.Connected && monture.AtPark) {
                Avertissement = "⚠ La monture est parquée (position repos). Déparquez-la : onglet Équipement > Monture, bouton « Unpark », puis revenez.";
                RaisePropertyChanged(nameof(Avertissement));
                return;
            }
            if (sequenceMediator.IsAdvancedSequenceRunning()) {
                Avertissement = "⚠ Une séquence est déjà en cours dans le séquenceur de N.I.N.A. Arrêtez-la d'abord (ou attendez sa fin).";
                RaisePropertyChanged(nameof(Avertissement));
                return;
            }

            int nbPhotos = EntierOuDefaut(NbPhotosTexte, 0);
            double poseSecondes = DoubleOuDefaut(PoseSecondesTexte, 0);
            if (nbPhotos <= 0 || poseSecondes <= 0) {
                Avertissement = "⚠ Vérifiez le nombre de photos et le temps de pose : il faut des nombres plus grands que zéro.";
                RaisePropertyChanged(nameof(Avertissement));
                return;
            }

            // Météo : si des nuages épais (>= 70 %) sont prévus avant la fin
            // de la série, on prévient UNE fois — second clic = on y va
            // quand même (les prévisions se trompent aussi)
            if (!meteoConfirmee && previsionsNuages != null) {
                var finPrevue = DateTime.Now.AddSeconds(nbPhotos * (poseSecondes + MargeParPhotoSecondes));
                var mauvaiseHeure = previsionsNuages.FirstOrDefault(
                    p => p.Item1 >= DateTime.Now.AddMinutes(-30) && p.Item1 <= finPrevue && p.Item2 >= 70);
                if (mauvaiseHeure != null) {
                    Avertissement = "⚠ Météo : " + mauvaiseHeure.Item2 + " % de nuages prévus vers "
                        + mauvaiseHeure.Item1.Hour + " h, alors que la série se terminerait vers "
                        + finPrevue.ToString("HH\\hmm") + ". Raccourcissez la série… ou cliquez une seconde fois pour tenter quand même.";
                    RaisePropertyChanged(nameof(Avertissement));
                    meteoConfirmee = true;
                    return;
                }
            }

            Avertissement = "";
            var notes = "";

            // ---- La séquence, telle qu'on l'aurait construite à la main ----

            // Le squelette du séquenceur avancé : zone de début, zone des
            // cibles, zone de fin (structure imposée par N.I.N.A.)
            var racine = new SequenceRootContainer();
            var zoneDebut = new StartAreaContainer();
            var zoneCibles = new TargetAreaContainer();
            var zoneFin = new EndAreaContainer();
            racine.Add(zoneDebut);
            racine.Add(zoneCibles);
            racine.Add(zoneFin);
            racine.SequenceTitle = "Série simplifiée (Mode Débutant)";

            // La "cible" : porte le nom de l'objet (pour les noms de fichiers
            // et l'affichage) et contient les instructions de prise de vue
            var conteneurCible = new DeepSkyObjectContainer(profileService, nighttimeCalculator, framingAssistantVM,
                applicationMediator, planetariumFactory, cameraMediator, filterWheelMediator);
            if (CibleChoisie != null) {
                conteneurCible.Name = CibleChoisie.Nom;
                conteneurCible.Target.TargetName = CibleChoisie.Nom;
                conteneurCible.Target.InputCoordinates.Coordinates = CibleChoisie.Coordonnees;
            } else {
                // Pas de cible choisie : on photographie là où pointe le
                // télescope (s'il est connecté, on note quand même sa position)
                conteneurCible.Name = "Position actuelle";
                conteneurCible.Target.TargetName = "Position actuelle";
                if (monture.Connected) {
                    conteneurCible.Target.InputCoordinates.Coordinates = telescopeMediator.GetCurrentPosition();
                }
            }

            // L'instruction « pose intelligente » de N.I.N.A. : une boucle
            // toute faite [prendre une photo × N] avec dithering intégré
            var poseIntelligente = new SmartExposure(profileService, cameraMediator, imagingMediator,
                imageSaveMediator, imageHistoryVM, filterWheelMediator, guiderMediator);

            var prisePhoto = poseIntelligente.GetTakeExposure();
            prisePhoto.ExposureTime = poseSecondes;
            prisePhoto.ImageType = "LIGHT"; // photo "utile" classique
            prisePhoto.Gain = EntierOuDefaut(GainSerieTexte, -1); // -1 = réglage caméra
            prisePhoto.Offset = EntierOuDefaut(OffsetSerieTexte, -1); // idem
            short binning = (short)EntierOuDefaut(BinningSerieTexte, 1);
            prisePhoto.Binning = new BinningMode(binning, binning);

            // On note ces réglages : d'éventuels darks devront être IDENTIQUES
            gainEnCours = prisePhoto.Gain;
            offsetEnCours = prisePhoto.Offset;
            binningEnCours = binning;
            nomCibleEnCours = conteneurCible.Target.TargetName;

            // Filtre : seulement s'il est nommé ET reconnu dans le profil
            // (SwitchFilter laissé vide = la roue à filtres ne bouge pas)
            var nomFiltre = FiltreSerieTexte?.Trim();
            if (!string.IsNullOrEmpty(nomFiltre)) {
                FilterInfo filtreTrouve = null;
                foreach (var filtre in profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters) {
                    if (string.Equals(filtre.Name, nomFiltre, StringComparison.OrdinalIgnoreCase)) {
                        filtreTrouve = filtre;
                        break;
                    }
                }
                if (filtreTrouve != null) {
                    poseIntelligente.GetSwitchFilter().Filter = filtreTrouve;
                    notes += "Filtre « " + filtreTrouve.Name + " » · ";
                } else {
                    notes += "Filtre « " + nomFiltre + " » introuvable dans le profil, ignoré · ";
                }
            }

            boucle = poseIntelligente.GetLoopCondition();
            boucle.Iterations = nbPhotos;
            boucle.PropertyChanged += QuandProgression;

            // Dithering : seulement si demandé ET que le guidage tourne
            var dithering = poseIntelligente.GetDitherAfterExposures();
            if (DitherActif && guiderMediator.GetInfo().Connected) {
                int toutesLes = EntierOuDefaut(DitherFrequenceTexte, 1);
                dithering.AfterExposures = toutesLes;
                notes += toutesLes <= 1
                    ? "Dithering actif (chaque photo) · "
                    : "Dithering actif (toutes les " + toutesLes + " photos) · ";
            } else {
                dithering.AfterExposures = 0; // 0 = désactivé
                if (DitherActif) { notes += "Dithering ignoré (guidage non connecté) · "; }
            }

            conteneurCible.Add(poseIntelligente);
            zoneCibles.Add(conteneurCible);

            // Retournement au méridien : seulement si monture connectée
            if (FlipActif && monture.Connected) {
                racine.Add(new MeridianFlipTrigger(profileService, cameraMediator, telescopeMediator,
                    focuserMediator, applicationStatusMediator, meridianFlipVMFactory));
                notes += "Retournement au méridien surveillé · ";
            } else if (FlipActif) {
                notes += "Retournement au méridien ignoré (monture non connectée) · ";
            }

            NotesSerie = notes.TrimEnd(' ', '·');
            nbPhotosTotal = nbPhotos;
            poseSecondesEnCours = poseSecondes;
            PhotosFaites = 0;
            arretDemande = false;
            DerniereImage = null;
            NbEtoilesTexte = "";
            VerdictPhoto = "";
            CouleurVerdict = BrosseVert;
            meilleurHfr = double.NaN;   // les repères de qualité repartent
            meilleuresEtoiles = 0;      // de zéro à chaque série
            niveauVerdictPrecedent = 0; // et le téléphone repart de "tout va bien"
            meteoConfirmee = false;     // la prochaine série re-vérifiera la météo
            darksEnCours = false;
            nbLightsFaits = 0;
            serieDebut = DateTime.Now;
            carnetNuit.Clear();
            BilanFichier = "";
            phase = Phase.EnCours;
            NotifierToutChange();

            var dureePrevu = TimeSpan.FromSeconds(nbPhotos * (poseSecondes + MargeParPhotoSecondes));
            if (AlerteTelActive) {
                // Envoi en parallèle (la série démarre sans attendre), puis
                // confirmation visible : « alerte envoyée ✔ » — pour partir
                // se coucher l'esprit tranquille
                _ = ConfirmerAlerteLancement(
                    "▶ " + conteneurCible.Target.TargetName + " : " + nbPhotos + " photos de " + poseSecondes.ToString("0.#")
                    + " s. Fin prévue vers " + DateTime.Now.Add(dureePrevu).ToString("HH\\hmm") + ".");
            }

            try {
                // On remplace la séquence de l'onglet « séquenceur avancé »
                // par la nôtre, puis on la démarre. L'attente dure toute la
                // série : la ligne suivante ne rend la main qu'à la fin.
                sequenceMediator.SetAdvancedSequence(racine);
                await sequenceMediator.StartAdvancedSequence(false);
                TerminerSerie(null);
            } catch (Exception ex) {
                TerminerSerie(ex);
            }
        }

        /// <summary>Fin de série (normale, interrompue ou en erreur) : le bilan
        /// — ou, si les darks sont demandés, l'escale « couvrez le télescope ».</summary>
        private void TerminerSerie(Exception erreur) {
            if (phase != Phase.EnCours) { return; }
            if (boucle != null) { boucle.PropertyChanged -= QuandProgression; }

            int faites = PhotosFaites;

            // Les photos viennent de finir SANS accroc et les darks sont
            // demandés : on fait escale au lieu de conclure
            if (!darksEnCours && erreur == null && !arretDemande && faites >= nbPhotosTotal && faites > 0 && DarksActif) {
                nbLightsFaits = faites;
                phase = Phase.AttenteDarks;
                AttenteDarksTexte = "Vos " + faites + " photos de « " + nomCibleEnCours + " » sont dans la boîte. "
                    + "Avant de ranger : les darks ! Couvrez le télescope (bouchon sur le tube), puis lancez.";
                NotifierToutChange();
                EnvoyerAlerte("Photos terminees - place aux darks",
                    "✅ " + faites + " photos finies ! 🌡 Allez COUVRIR le télescope (bouchon sur le tube), puis validez « Lancer les darks » dans N.I.N.A.", true);
                return;
            }

            phase = Phase.Terminee;
            string quoi = darksEnCours ? "dark(s)" : "photo(s)";

            if (erreur != null) {
                TitreBilan = "⚠ La série s'est arrêtée sur une erreur";
                ResumeBilan = faites + " " + quoi + " sur " + nbPhotosTotal + ".\nDétail technique : " + erreur.Message;
                CouleurBilan = BrosseOrange;
            } else if (arretDemande || faites < nbPhotosTotal) {
                TitreBilan = darksEnCours ? "⏹ Darks arrêtés" : "⏹ Série arrêtée";
                ResumeBilan = faites + " " + quoi + " sur " + nbPhotosTotal + " prévus. Tout est enregistré et utilisable.";
                CouleurBilan = BrosseOrange;
            } else if (darksEnCours) {
                TitreBilan = "✅ Série complète !";
                ResumeBilan = nbLightsFaits + " photos de " + poseSecondesEnCours.ToString("0.#") + " s + "
                    + faites + " darks : la nuit est complète, l'empilement n'attend plus que vous. Bravo !";
                CouleurBilan = BrosseVert;
            } else {
                TitreBilan = "✅ Série terminée !";
                ResumeBilan = faites + " photos de " + poseSecondesEnCours.ToString("0.#") + " s sont dans la boîte. Bravo !";
                CouleurBilan = BrosseVert;
            }

            // Rappel pour les darks passés : lights + darks au bilan
            if (darksEnCours && nbLightsFaits > 0 && (arretDemande || faites < nbPhotosTotal || erreur != null)) {
                ResumeBilan += "\n(Les " + nbLightsFaits + " photos, elles, sont complètes.)";
            }

            var dossier = profileService.ActiveProfile.ImageFileSettings.FilePath;
            if (!string.IsNullOrWhiteSpace(dossier)) {
                ResumeBilan += "\n\nVos fichiers sont dans : " + dossier;
            }

            // Le rapport de la nuit, enregistré à côté des photos
            GenererBilanNuit(darksEnCours ? nbLightsFaits : faites, darksEnCours ? faites : 0);
            NotifierToutChange();

            // Le téléphone est prévenu du dénouement (urgent si erreur)
            if (erreur != null) {
                EnvoyerAlerte("Serie arretee sur une erreur",
                    "⚠ " + faites + " " + quoi + " sur " + nbPhotosTotal + ". Erreur : " + erreur.Message, true);
            } else if (arretDemande || faites < nbPhotosTotal) {
                EnvoyerAlerte(darksEnCours ? "Darks arretes" : "Serie arretee",
                    "⏹ " + faites + " " + quoi + " sur " + nbPhotosTotal + " prévus.");
            } else if (darksEnCours) {
                EnvoyerAlerte("Nuit complete",
                    "✅ " + nbLightsFaits + " photos + " + faites + " darks. Vous pouvez ranger le matériel, bravo !");
            } else {
                EnvoyerAlerte("Serie terminee",
                    "✅ " + faites + " photos de " + poseSecondesEnCours.ToString("0.#") + " s dans la boîte. Bravo !");
            }
        }

        // ------------------------------------------------------------------
        // Les darks : mêmes réglages que les photos, télescope couvert
        // ------------------------------------------------------------------

        /// <summary>Texte de l'escale « couvrez le télescope ».</summary>
        public string AttenteDarksTexte { get; private set; } = "";

        public ICommand LancerDarksCommand { get; }
        public ICommand PasserDarksCommand { get; }

        private async Task LancerDarks() {
            if (phase != Phase.AttenteDarks) { return; }
            if (!cameraMediator.GetInfo().Connected) {
                AttenteDarksTexte = "⚠ La caméra n'est plus connectée ! Reconnectez-la (onglet Équipement > Caméra) puis relancez les darks.";
                RaisePropertyChanged(nameof(AttenteDarksTexte));
                return;
            }

            int nbDarks = EntierOuDefaut(NbDarksTexte, 15);

            // La séquence des darks : la même mécanique que les photos, mais
            // ImageType DARK, pas de dithering, pas de retournement — le
            // télescope est couvert, il ne se passe rien dehors
            var racine = new SequenceRootContainer();
            var zoneDebut = new StartAreaContainer();
            var zoneCibles = new TargetAreaContainer();
            var zoneFin = new EndAreaContainer();
            racine.Add(zoneDebut);
            racine.Add(zoneCibles);
            racine.Add(zoneFin);
            racine.SequenceTitle = "Darks (Mode Débutant)";

            var conteneurCible = new DeepSkyObjectContainer(profileService, nighttimeCalculator, framingAssistantVM,
                applicationMediator, planetariumFactory, cameraMediator, filterWheelMediator);
            conteneurCible.Name = "Darks";
            // Même nom de cible que les photos : les fichiers se rangent au
            // même endroit (le type DARK les distingue)
            conteneurCible.Target.TargetName = nomCibleEnCours;

            var poseIntelligente = new SmartExposure(profileService, cameraMediator, imagingMediator,
                imageSaveMediator, imageHistoryVM, filterWheelMediator, guiderMediator);
            var priseDark = poseIntelligente.GetTakeExposure();
            priseDark.ExposureTime = poseSecondesEnCours;   // identiques aux photos,
            priseDark.Gain = gainEnCours;                   // c'est toute l'idée
            priseDark.Offset = offsetEnCours;               // des darks
            priseDark.Binning = new BinningMode(binningEnCours, binningEnCours);
            priseDark.ImageType = "DARK";

            boucle = poseIntelligente.GetLoopCondition();
            boucle.Iterations = nbDarks;
            boucle.PropertyChanged += QuandProgression;
            poseIntelligente.GetDitherAfterExposures().AfterExposures = 0;

            conteneurCible.Add(poseIntelligente);
            zoneCibles.Add(conteneurCible);

            darksEnCours = true;
            nbPhotosTotal = nbDarks;
            PhotosFaites = 0;
            arretDemande = false;
            DerniereImage = null;   // place aux images noires
            NbEtoilesTexte = "";
            VerdictPhoto = "";
            NotesSerie = "Télescope couvert · " + nbDarks + " darks de " + poseSecondesEnCours.ToString("0.#") + " s";
            phase = Phase.EnCours;
            NotifierToutChange();

            var duree = TimeSpan.FromSeconds(nbDarks * (poseSecondesEnCours + MargeParPhotoSecondes));
            EnvoyerAlerte("Darks lances",
                "🌡 " + nbDarks + " darks de " + poseSecondesEnCours.ToString("0.#") + " s — fin vers "
                + DateTime.Now.Add(duree).ToString("HH\\hmm") + ". Vous pouvez retourner au chaud.");

            try {
                sequenceMediator.SetAdvancedSequence(racine);
                await sequenceMediator.StartAdvancedSequence(false);
                TerminerSerie(null);
            } catch (Exception ex) {
                TerminerSerie(ex);
            }
        }

        /// <summary>« Non merci » : on conclut sans darks.</summary>
        private void PasserDarks() {
            if (phase != Phase.AttenteDarks) { return; }
            phase = Phase.Terminee;
            TitreBilan = "✅ Série terminée !";
            ResumeBilan = nbLightsFaits + " photos de " + poseSecondesEnCours.ToString("0.#") + " s sont dans la boîte (darks sautés). Bravo !";
            CouleurBilan = BrosseVert;
            var dossier = profileService.ActiveProfile.ImageFileSettings.FilePath;
            if (!string.IsNullOrWhiteSpace(dossier)) {
                ResumeBilan += "\n\nVos photos sont dans : " + dossier;
            }
            GenererBilanNuit(nbLightsFaits, 0);
            NotifierToutChange();
        }

        // ------------------------------------------------------------------
        // Le bilan de nuit : un fichier HTML enregistré à côté des photos
        // (stats de qualité, courbe de netteté, guide d'empilement Siril)
        // ------------------------------------------------------------------

        /// <summary>Chemin du bilan enregistré (vide = pas de bilan).</summary>
        public string BilanFichier { get; private set; } = "";

        public bool BilanDisponible => !string.IsNullOrEmpty(BilanFichier);

        public ICommand OuvrirBilanCommand { get; }

        private void OuvrirBilan() {
            if (string.IsNullOrEmpty(BilanFichier)) { return; }
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BilanFichier) { UseShellExecute = true });
            } catch { }
        }

        /// <summary>Retour du bouton « Générer le bilan maintenant ».</summary>
        public string MessageBilan { get; private set; } = "";

        public ICommand GenererBilanCommand { get; }

        /// <summary>
        /// Bilan à la demande, même en pleine série (instantané provisoire) :
        /// il est écrit avec les chiffres du moment, puis ouvert directement.
        /// </summary>
        private void GenererBilanMaintenant() {
            int lights = darksEnCours ? nbLightsFaits : Math.Max(PhotosFaites, carnetNuit.Count);
            int darks = darksEnCours ? PhotosFaites : 0;

            if (lights <= 0) {
                MessageBilan = "Rien à raconter pour l'instant : aucune photo prise dans cette session.";
                RaisePropertyChanged(nameof(MessageBilan));
                return;
            }

            GenererBilanNuit(lights, darks);
            if (BilanDisponible) {
                MessageBilan = "✅ Bilan enregistré et ouvert dans le navigateur.";
                OuvrirBilan();
            } else {
                MessageBilan = "⚠ Impossible d'écrire le bilan (dossier d'images de N.I.N.A. introuvable ?).";
            }
            RaisePropertyChanged(nameof(MessageBilan));
        }

        // ------------------------------------------------------------------
        // « Nouvelle session » : remet à zéro toutes les DONNÉES de travail.
        // Les RÉGLAGES (pose, gain, interrupteurs, canal d'alertes...) sont
        // volontairement conservés : ce sont des préférences, pas des données.
        // ------------------------------------------------------------------

        public ICommand NouvelleSessionCommand { get; }

        private void NouvelleSession() {
            if (phase == Phase.EnCours) {
                Avertissement = "⚠ Une série est en cours — arrêtez-la d'abord (bouton ARRÊTER) avant de remettre à zéro.";
                RaisePropertyChanged(nameof(Avertissement));
                return;
            }

            // La cible et ses recherches
            CibleTexte = "";
            Resultats.Clear();
            CibleChoisie = null;

            // Les images et verdicts de la dernière série
            DerniereImage = null;
            NbEtoilesTexte = "";
            VerdictPhoto = "";
            meilleurHfr = double.NaN;
            meilleuresEtoiles = 0;
            niveauVerdictPrecedent = 0;

            // Le carnet et le bilan de la nuit passée
            carnetNuit.Clear();
            BilanFichier = "";
            MessageBilan = "";

            // Les compteurs et textes de série
            PhotosFaites = 0;
            nbPhotosTotal = 0;
            nbLightsFaits = 0;
            darksEnCours = false;
            arretDemande = false;
            NotesSerie = "";
            AttenteDarksTexte = "";
            TitreBilan = "";
            ResumeBilan = "";
            Avertissement = "";
            MessageAlerteTel = "";
            meteoConfirmee = false;

            phase = Phase.Preparation;
            _ = ChargerMeteo(); // prévisions toutes fraîches
            MessageCible = "🧹 Session remise à zéro — vos réglages (pose, gain, alertes…) sont conservés.";
            NotifierToutChange();
        }

        private void GenererBilanNuit(int nbLights, int nbDarks) {
            try {
                if (nbLights <= 0) { return; } // rien à raconter

                var dossier = profileService.ActiveProfile.ImageFileSettings.FilePath;
                if (string.IsNullOrWhiteSpace(dossier) || !System.IO.Directory.Exists(dossier)) { return; }

                // Les stats depuis le carnet de la nuit
                var analysees = carnetNuit.Where(n => n.Etoiles > 0).ToList();
                int nbValidees = carnetNuit.Count(n => n.Niveau == 0);
                int nbASurveiller = carnetNuit.Count(n => n.Niveau == 1);
                int nbProblemes = carnetNuit.Count(n => n.Niveau == 2);
                var hfrs = analysees.Where(n => !double.IsNaN(n.Hfr) && n.Hfr > 0).Select(n => n.Hfr).ToList();

                var duree = DateTime.Now - serieDebut;
                double heuresExposition = nbLights * poseSecondesEnCours / 3600.0;

                var html = new System.Text.StringBuilder();
                html.Append("<!DOCTYPE html><html lang='fr'><head><meta charset='utf-8'>");
                html.Append("<title>Bilan de nuit — " + Proprifier(nomCibleEnCours) + "</title>");
                html.Append("<style>body{font-family:Segoe UI,sans-serif;background:#14161a;color:#e8e8e8;max-width:760px;margin:24px auto;padding:0 16px}");
                html.Append("h1{font-size:26px}h2{font-size:18px;margin-top:28px;border-bottom:1px solid #333;padding-bottom:4px}");
                html.Append(".carte{background:#1e2128;border-radius:10px;padding:14px;margin:10px 0}");
                html.Append(".vert{color:#7bc67e}.orange{color:#f0a95a}.rouge{color:#e57373}");
                html.Append("table{border-collapse:collapse}td{padding:3px 14px 3px 0}");
                html.Append("code,pre{background:#0d0f12;border-radius:6px;padding:2px 6px}pre{padding:10px;overflow-x:auto}</style></head><body>");

                html.Append("<h1>🔭 Bilan de nuit — " + Proprifier(nomCibleEnCours) + "</h1>");
                html.Append("<p>" + serieDebut.ToString("dddd d MMMM yyyy, HH\\hmm", new CultureInfo("fr-FR"))
                    + " → " + DateTime.Now.ToString("HH\\hmm") + " (" + FormatDuree(duree) + ")</p>");

                html.Append("<h2>📷 La récolte</h2><div class='carte'><table>");
                html.Append("<tr><td>Photos</td><td><b>" + nbLights + "</b> × " + poseSecondesEnCours.ToString("0.#") + " s = <b>"
                    + (heuresExposition >= 1 ? heuresExposition.ToString("0.0") + " h" : (heuresExposition * 60).ToString("0") + " min")
                    + "</b> d'exposition totale</td></tr>");
                if (nbDarks > 0) { html.Append("<tr><td>Darks</td><td><b>" + nbDarks + "</b> (mêmes réglages) ✔</td></tr>"); }
                if (gainEnCours >= 0) { html.Append("<tr><td>Gain</td><td>" + gainEnCours + "</td></tr>"); }
                html.Append("</table></div>");

                if (carnetNuit.Count > 0) {
                    html.Append("<h2>⭐ Qualité (sur " + carnetNuit.Count + " photos analysées)</h2><div class='carte'><table>");
                    html.Append("<tr><td class='vert'>✅ Validées</td><td><b>" + nbValidees + "</b></td></tr>");
                    html.Append("<tr><td class='orange'>⚠ À surveiller</td><td><b>" + nbASurveiller + "</b></td></tr>");
                    html.Append("<tr><td class='rouge'>❌ Sans étoiles</td><td><b>" + nbProblemes + "</b></td></tr>");
                    if (hfrs.Count > 0) {
                        html.Append("<tr><td>Netteté (HFR)</td><td>meilleure <b>" + hfrs.Min().ToString("0.00")
                            + "</b> · moyenne " + hfrs.Average().ToString("0.00")
                            + " · pire " + hfrs.Max().ToString("0.00") + " (petit = net)</td></tr>");
                    }
                    if (analysees.Count > 0) {
                        html.Append("<tr><td>Étoiles</td><td>en moyenne " + analysees.Average(n => n.Etoiles).ToString("0") + " par photo</td></tr>");
                    }
                    html.Append("</table></div>");
                }

                // La courbe de netteté de la nuit (un simple dessin SVG)
                if (hfrs.Count >= 3) {
                    double min = hfrs.Min(), max = hfrs.Max();
                    double plage = Math.Max(0.001, max - min);
                    var points = new System.Text.StringBuilder();
                    var serie = analysees.Where(n => !double.IsNaN(n.Hfr) && n.Hfr > 0).ToList();
                    for (int i = 0; i < serie.Count; i++) {
                        double x = 20 + i * 680.0 / Math.Max(1, serie.Count - 1);
                        double y = 20 + (serie[i].Hfr - min) / plage * 140.0;
                        points.Append(x.ToString("0.#", CultureInfo.InvariantCulture) + ","
                            + y.ToString("0.#", CultureInfo.InvariantCulture) + " ");
                    }
                    html.Append("<h2>📈 La netteté au fil de la nuit</h2><div class='carte'>");
                    html.Append("<svg viewBox='0 0 720 180' style='width:100%'>");
                    html.Append("<polyline points='" + points.ToString().Trim() + "' fill='none' stroke='#64b5f6' stroke-width='2'/>");
                    html.Append("<text x='4' y='24' fill='#7bc67e' font-size='12'>" + min.ToString("0.0") + "</text>");
                    html.Append("<text x='4' y='166' fill='#e57373' font-size='12'>" + max.ToString("0.0") + "</text>");
                    html.Append("</svg><p style='opacity:.6;font-size:13px'>Vers le haut = plus net. Si la courbe descend au fil des heures, pensez à refaire la mise au point en cours de nuit.</p></div>");
                }

                html.Append("<h2>🧑‍🍳 Et maintenant : l'empilement (Siril)</h2><div class='carte'>");
                html.Append("<p>1. Installez <b>Siril</b> (gratuit) · 2. Onglet <i>Scripts</i> → <i>OSC_Preprocessing</i> "
                    + "· 3. Rangez vos fichiers dans des dossiers <code>lights</code>" + (nbDarks > 0 ? ", <code>darks</code>" : "")
                    + " comme demandé par le script · 4. Lancez, patientez, admirez.</p>");
                html.Append("<p style='opacity:.6;font-size:13px'>Vos fichiers de cette nuit : <code>" + Proprifier(dossier) + "</code> (les DARK sont marqués dans le nom de fichier).</p>");
                html.Append("</div></body></html>");

                // Nom de fichier propre : « Bilan 2026-07-19 M 31.html »
                var nomFichier = "Bilan " + serieDebut.ToString("yyyy-MM-dd HHmm") + " " + nomCibleEnCours + ".html";
                foreach (var c in System.IO.Path.GetInvalidFileNameChars()) { nomFichier = nomFichier.Replace(c, '_'); }
                var chemin = System.IO.Path.Combine(dossier, nomFichier);
                System.IO.File.WriteAllText(chemin, html.ToString(), System.Text.Encoding.UTF8);

                BilanFichier = chemin;
                RaisePropertyChanged(nameof(BilanFichier));
                RaisePropertyChanged(nameof(BilanDisponible));
            } catch {
                // Le bilan est un bonus : s'il échoue, la nuit reste réussie
            }
        }

        /// <summary>Échappe les caractères spéciaux HTML.</summary>
        private static string Proprifier(string texte) {
            return (texte ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // ------------------------------------------------------------------
        // Pendant la série : avancement
        // ------------------------------------------------------------------

        /// <summary>Nombre de photos déjà prises (compté par la boucle N.I.N.A.).</summary>
        public int PhotosFaites { get; private set; }

        public int NbPhotosTotal => nbPhotosTotal;

        public string ProgressionTexte => (darksEnCours ? "Dark " : "Photo ") + Math.Min(PhotosFaites + 1, nbPhotosTotal)
            + " sur " + nbPhotosTotal + "  (" + PhotosFaites + " déjà " + (PhotosFaites > 1 ? "pris" + (darksEnCours ? "" : "es") : "pris" + (darksEnCours ? "" : "e")) + ")";

        public string TempsRestantTexte {
            get {
                double resteSecondes = Math.Max(0, (nbPhotosTotal - PhotosFaites) * (poseSecondesEnCours + MargeParPhotoSecondes));
                var reste = TimeSpan.FromSeconds(resteSecondes);
                return "Il reste environ " + FormatDuree(reste)
                    + " (fin vers " + DateTime.Now.Add(reste).ToString("HH\\hmm") + ")";
            }
        }

        /// <summary>Ce qui est actif pendant la série (dithering, méridien...).</summary>
        public string NotesSerie { get; private set; } = "";

        private void QuandProgression(object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(LoopCondition.CompletedIterations) && boucle != null) {
                PhotosFaites = boucle.CompletedIterations;
                RaisePropertyChanged(nameof(PhotosFaites));
                RaisePropertyChanged(nameof(ProgressionTexte));
                RaisePropertyChanged(nameof(TempsRestantTexte));
            }
        }

        // ------------------------------------------------------------------
        // Boutons Arrêter / Nouvelle série / Voir le séquenceur
        // ------------------------------------------------------------------

        public ICommand ArreterCommand { get; }
        public ICommand NouvelleSerieCommand { get; }
        public ICommand VoirSequenceurCommand { get; }

        private void Arreter() {
            arretDemande = true;
            try {
                sequenceMediator.CancelAdvancedSequence();
                // La fin réelle est signalée par le retour de
                // StartAdvancedSequence (dans Demarrer), qui fait le bilan.
            } catch (Exception ex) {
                TerminerSerie(ex);
            }
        }

        private void NouvelleSerie() {
            phase = Phase.Preparation;
            Avertissement = "";
            meteoConfirmee = false;
            _ = ChargerMeteo(); // prévisions toutes fraîches pour la suite
            NotifierToutChange();
        }

        /// <summary>Ouvre l'onglet du séquenceur avancé : la séquence générée
        /// s'y affiche en entier — une bonne façon d'apprendre !</summary>
        private void VoirSequenceur() {
            try { sequenceMediator.SwitchToAdvancedView(); } catch { }
        }

        // ------------------------------------------------------------------
        // Vignette + qualité de mise au point (mêmes recettes que le panneau
        // « Alignement polaire simplifié »)
        // ------------------------------------------------------------------

        /// <summary>La dernière photo prise, prête à afficher.</summary>
        public ImageSource DerniereImage { get; private set; }

        private void QuandImagePrete(object sender, ImagePreparedEventArgs e) {
            if (phase != Phase.EnCours) { return; }
            var image = e?.RenderedImage?.Image;
            if (image == null) { return; }

            // "Freeze" fige l'image : indispensable pour l'afficher alors
            // qu'elle vient d'un autre fil d'exécution (règle WPF)
            if (!image.IsFrozen && image.CanFreeze) { image.Freeze(); }
            if (!image.IsFrozen) { return; }

            DerniereImage = image;
            RaisePropertyChanged(nameof(DerniereImage));

            // Un dark est tout noir : compter ses étoiles n'aurait aucun sens
            // (le verdict hurlerait « aucune étoile ! » à chaque image)
            if (!darksEnCours) {
                AnalyserQualite(e.RenderedImage);
            }
        }

        // Garde-fou : une seule analyse à la fois
        private bool analyseEnCours;

        // Les "repères" de la série en cours, pour juger chaque photo par
        // rapport aux meilleures DE CETTE série (pas de seuil absolu : la
        // bonne valeur d'HFR dépend du matériel de chacun)
        private double meilleurHfr = double.NaN; // le plus PETIT HFR vu
        private int meilleuresEtoiles;           // le plus GRAND nombre d'étoiles vu

        // Niveau du dernier verdict envoyé au téléphone (0 = tout va bien,
        // 1 = à surveiller, 2 = problème) : on n'alerte qu'aux CHANGEMENTS,
        // jamais à chaque photo (sinon le téléphone vibre toute la nuit)
        private int niveauVerdictPrecedent;

        private async void AnalyserQualite(IRenderedImage rendu) {
            if (analyseEnCours) { return; }
            analyseEnCours = true;
            try {
                // Si N.I.N.A. a déjà analysé cette image, on réutilise le résultat
                var analyse = rendu.RawImageData?.StarDetectionAnalysis;

                if (analyse == null || analyse.DetectedStars <= 0) {
                    var renduAnalyse = await rendu.DetectStars(false, StarSensitivityEnum.Normal, NoiseReductionEnum.None);
                    analyse = renduAnalyse?.RawImageData?.StarDetectionAnalysis;
                }

                if (analyse != null) {
                    int n = analyse.DetectedStars;
                    double hfr = analyse.HFR;
                    bool hfrValide = !double.IsNaN(hfr) && hfr > 0;

                    if (n > 0) {
                        NbEtoilesTexte = "⭐ " + n + (n > 1 ? " étoiles" : " étoile");

                        // La netteté (HFR) : taille moyenne des étoiles.
                        // Plus le chiffre est PETIT, plus la mise au point est
                        // bonne. S'il grimpe au fil de la nuit : refaites-la !
                        if (hfrValide) {
                            NbEtoilesTexte += "   ·   Mise au point (HFR) : " + hfr.ToString("0.0") + " (petit = net)";
                        }
                    } else {
                        NbEtoilesTexte = "⚠ Aucune étoile détectée — mise au point ? nuages ? pose trop courte ?";
                    }
                    RaisePropertyChanged(nameof(NbEtoilesTexte));

                    JugerPhoto(n, hfrValide ? hfr : double.NaN);
                }
            } catch {
                // L'analyse est un bonus : si elle échoue, elle ne doit
                // jamais perturber la série elle-même
            } finally {
                analyseEnCours = false;
            }
        }

        /// <summary>
        /// Le verdict « photo validée / à vérifier », en comparant chaque
        /// photo aux MEILLEURES de la série en cours (auto-calibré, aucun
        /// seuil dépendant du matériel) :
        ///  - zéro étoile                        -> alerte (nuages ? buée ? mise au point ?)
        ///  - HFR à plus de +30 % du meilleur    -> mise au point à revoir
        ///  - moins de la moitié des étoiles     -> ciel dégradé probable
        ///  - sinon                              -> ✅ photo validée
        /// Les toutes premières photos servent de référence : elles sont
        /// validées et fixent les repères.
        /// </summary>
        private void JugerPhoto(int etoiles, double hfr) {
            int niveau; // 0 = validée, 1 = à surveiller, 2 = problème
            if (etoiles <= 0) {
                VerdictPhoto = "❌ Photo à vérifier : aucune étoile détectée";
                CouleurVerdict = BrosseRouge;
                niveau = 2;
            } else {
                // Mise à jour des repères de la série (les "records")
                if (etoiles > meilleuresEtoiles) { meilleuresEtoiles = etoiles; }
                if (!double.IsNaN(hfr) && (double.IsNaN(meilleurHfr) || hfr < meilleurHfr)) { meilleurHfr = hfr; }

                bool netteteEnBaisse = !double.IsNaN(hfr) && !double.IsNaN(meilleurHfr) && hfr > meilleurHfr * 1.3;
                bool etoilesEnChute = etoiles < meilleuresEtoiles / 2;

                if (netteteEnBaisse) {
                    VerdictPhoto = "⚠ Mise au point à revoir : la netteté baisse (HFR " + hfr.ToString("0.0")
                        + " contre " + meilleurHfr.ToString("0.0") + " au mieux)";
                    CouleurVerdict = BrosseOrange;
                    niveau = 1;
                } else if (etoilesEnChute) {
                    VerdictPhoto = "⚠ Beaucoup moins d'étoiles que tout à l'heure — nuages ou buée ?";
                    CouleurVerdict = BrosseOrange;
                    niveau = 1;
                } else {
                    VerdictPhoto = "✅ Photo validée";
                    CouleurVerdict = BrosseVert;
                    niveau = 0;
                }
            }
            RaisePropertyChanged(nameof(VerdictPhoto));
            RaisePropertyChanged(nameof(CouleurVerdict));

            // Une ligne de plus au carnet de la nuit (photos seulement)
            if (phase == Phase.EnCours && !darksEnCours) {
                carnetNuit.Add(new NotePhoto {
                    Heure = DateTime.Now,
                    Etoiles = etoiles,
                    Hfr = hfr,
                    Niveau = niveau
                });
            }

            // Téléphone : uniquement quand la situation CHANGE (pas de spam)
            if (niveau != niveauVerdictPrecedent) {
                string progression = "photo " + Math.Max(1, PhotosFaites) + "/" + nbPhotosTotal;
                if (niveau > niveauVerdictPrecedent) {
                    // Ça se dégrade : alerte (urgente si plus d'étoiles du tout)
                    EnvoyerAlerte(niveau == 2 ? "Probleme sur la serie" : "A surveiller",
                        VerdictPhoto + " (" + progression + ")", niveau == 2);
                } else if (niveau == 0) {
                    // Retour au vert : on rassure
                    EnvoyerAlerte("Retour a la normale",
                        "✅ Les photos sont de nouveau bonnes (" + progression + ").");
                }
                niveauVerdictPrecedent = niveau;
            }
        }

        /// <summary>Le verdict affiché sous la vignette.</summary>
        public string VerdictPhoto { get; private set; } = "";

        /// <summary>Vert (validée), orange (à surveiller), rouge (problème).</summary>
        public Brush CouleurVerdict { get; private set; } = BrosseVert;

        /// <summary>Qualité de la dernière photo : étoiles + netteté (HFR).</summary>
        public string NbEtoilesTexte { get; private set; } = "";

        // ------------------------------------------------------------------
        // Propriétés affichées par l'écran
        // ------------------------------------------------------------------

        public bool EnPreparation => phase == Phase.Preparation;
        public bool EnCours => phase == Phase.EnCours;
        public bool EnAttenteDarks => phase == Phase.AttenteDarks;
        public bool EstTerminee => phase == Phase.Terminee;

        /// <summary>Titre de la phase active : photos ou darks.</summary>
        public string TitreEnCours => darksEnCours ? "🌡️  Darks en cours… (télescope couvert)" : "📷  Série en cours…";

        /// <summary>Message d'alerte affiché en orange (vide = pas d'alerte).</summary>
        public string Avertissement { get; private set; } = "";

        public string TitreBilan { get; private set; } = "";
        public string ResumeBilan { get; private set; } = "";
        public Brush CouleurBilan { get; private set; } = BrosseVert;

        /// <summary>Prévient l'écran que les valeurs ont changé.</summary>
        private void NotifierToutChange() {
            RaisePropertyChanged(string.Empty); // chaîne vide = "tout a changé"
        }

        private static readonly Brush BrosseVert = CreerBrosse(46, 125, 50);
        private static readonly Brush BrosseOrange = CreerBrosse(230, 145, 10);
        private static readonly Brush BrosseRouge = CreerBrosse(198, 40, 40);

        private static Brush CreerBrosse(byte r, byte g, byte b) {
            var brosse = new SolidColorBrush(Color.FromRgb(r, g, b));
            brosse.Freeze();
            return brosse;
        }
    }

    /// <summary>
    /// Un objet du ciel trouvé par la recherche, avec tout ce qu'il faut
    /// pour l'afficher joliment et pointer le télescope dessus.
    /// </summary>
    public class CibleTrouvee {

        public CibleTrouvee(DeepSkyObject objet, Angle latitude, Angle longitude, double altitudeMetres, string complement = null) {
            // Le nom principal + le premier surnom connu (ex : M 31 possède
            // aussi le nom « Andromeda Galaxy »)
            Nom = objet.Name;
            var surnoms = objet.AlsoKnownAs;
            if (surnoms != null) {
                foreach (var surnom in surnoms) {
                    if (!string.Equals(surnom, Nom, StringComparison.OrdinalIgnoreCase)) {
                        Nom += " — " + surnom;
                        break;
                    }
                }
            }

            Coordonnees = objet.Coordinates;

            // L'objet est-il au-dessus de l'horizon en ce moment ?
            var topo = objet.Coordinates.Transform(latitude, longitude, altitudeMetres);
            double hauteur = topo.Altitude.Degree;
            EstVisible = hauteur > 0;

            Description = Nom;
            if (objet.Magnitude.HasValue) {
                Description += "  ·  magnitude " + objet.Magnitude.Value.ToString("0.0");
            }
            Description += EstVisible
                ? "  ·  ✅ visible (à " + hauteur.ToString("0") + "° de haut)"
                : "  ·  🚫 sous l'horizon en ce moment";

            // Ligne enrichie pour les suggestions (« au mieux 62° vers 23h »)
            if (!string.IsNullOrEmpty(complement)) {
                Description += "  ·  " + complement;
            }
        }

        /// <summary>Nom lisible : « M 31 — Andromeda Galaxy ».</summary>
        public string Nom { get; }

        /// <summary>La ligne affichée dans la liste des résultats.</summary>
        public string Description { get; }

        /// <summary>Position dans le ciel (J2000), prête pour le GoTo.</summary>
        public Coordinates Coordonnees { get; }

        /// <summary>Au-dessus de l'horizon en ce moment ?</summary>
        public bool EstVisible { get; }
    }

    /// <summary>Commande WPF minimale pour une action instantanée.</summary>
    public class CommandeSimple2 : ICommand {
        private readonly Action action;

        public CommandeSimple2(Action action) {
            this.action = action;
        }

        public event EventHandler CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter) => action();
    }

    /// <summary>Commande WPF minimale pour une action qui prend du temps
    /// (recherche, pointage, série de photos...).</summary>
    public class CommandeAsync : ICommand {
        private readonly Func<Task> action;

        public CommandeAsync(Func<Task> action) {
            this.action = action;
        }

        public event EventHandler CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object parameter) => true;

        public async void Execute(object parameter) => await action();
    }
}
