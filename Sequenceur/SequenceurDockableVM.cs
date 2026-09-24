using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem.Camera;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.Sequencer.Trigger.Platesolving;
using NINA.Sequencer.Utility;
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
        private readonly IDomeMediator domeMediator;
        private readonly IDomeFollower domeFollower;
        private readonly IPlateSolverFactory plateSolverFactory;
        private readonly IWindowServiceFactory windowServiceFactory;

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

        // Le centrage de la série : gardé pour savoir, à la fin, si c'est
        // LUI qui a arrêté la série (null = pas de centrage dans la série)
        private Center centrageSerie;

        // Refroidissement + darks : on ne réchauffe la caméra qu'APRÈS les
        // darks (ils doivent être pris à la même température que les photos).
        // true = un réchauffement est dû à la fin de la séance.
        private bool rechauffementDiffere;
        private CancellationTokenSource rechauffementArret;

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
            IPlanetariumFactory planetariumFactory,
            IDomeMediator domeMediator,
            IDomeFollower domeFollower,
            IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory) : base(profileService) {
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
            this.domeMediator = domeMediator;
            this.domeFollower = domeFollower;
            this.plateSolverFactory = plateSolverFactory;
            this.windowServiceFactory = windowServiceFactory;

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
            MoinsDixCommand = new CommandeSimple2(() => AjusterPhotos(-10));
            MoinsUnCommand = new CommandeSimple2(() => AjusterPhotos(-1));
            PlusUnCommand = new CommandeSimple2(() => AjusterPhotos(+1));
            PlusDixCommand = new CommandeSimple2(() => AjusterPhotos(+10));
            FinirApresCommand = new CommandeSimple2(() => AjusterPhotos(0, finirApres: true));
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

            // Le tableau de bord « tout est prêt ? » se rafraîchit en continu
            DemarrerTableauBord();
        }

        // ------------------------------------------------------------------
        // Le tableau de bord « Tout est prêt ? »
        // L'état du matériel et des réglages en un coup d'œil, rafraîchi en
        // continu : plus besoin de découvrir les oublis un par un au clic.
        // ------------------------------------------------------------------

        private System.Windows.Threading.DispatcherTimer tableauBordMinuteur;

        /// <summary>Les lignes d'état (une par élément vérifié).</summary>
        public ObservableCollection<LigneEtat> EtatsMateriel { get; } = new ObservableCollection<LigneEtat>();

        /// <summary>« 🚦 Tout est prêt ! » / « Il manque l'essentiel »…</summary>
        public string TableauBordTitre { get; private set; } = "";

        public Brush CouleurTableauBord { get; private set; } = BrosseVert;

        private void DemarrerTableauBord() {
            // DispatcherTimer = minuteur qui "tique" sur le fil d'interface :
            // on peut toucher les listes affichées sans précaution
            tableauBordMinuteur = new System.Windows.Threading.DispatcherTimer {
                Interval = TimeSpan.FromSeconds(2)
            };
            tableauBordMinuteur.Tick += (s, e) => { if (phase == Phase.Preparation) { RafraichirTableauBord(); } };
            tableauBordMinuteur.Start();
            RafraichirTableauBord();
        }

        private void RafraichirTableauBord() {
            var lignes = new List<LigneEtat>();
            bool bloquant = false;   // rouge : la série ne peut pas partir
            bool remarque = false;   // orange : ça partira, mais en mode dégradé

            // 1. La caméra — indispensable
            if (cameraMediator.GetInfo().Connected) {
                lignes.Add(new LigneEtat("✅ Caméra connectée", BrosseVertClair));
            } else {
                lignes.Add(new LigneEtat("❌ Caméra non connectée — onglet Équipement > Caméra", BrosseRougeClair));
                bloquant = true;
            }

            // 2. La monture — GoTo, centrage et méridien en dépendent
            var monture = telescopeMediator.GetInfo();
            if (monture.Connected && monture.AtPark) {
                lignes.Add(new LigneEtat("⚠ Monture connectée mais PARQUÉE — bouton « Unpark » (Équipement > Monture)", BrosseOrangeClair));
                remarque = true;
            } else if (monture.Connected) {
                lignes.Add(new LigneEtat("✅ Monture connectée", BrosseVertClair));
            } else {
                lignes.Add(new LigneEtat("⚠ Monture non connectée — pointage, centrage et méridien indisponibles", BrosseOrangeClair));
                remarque = true;
            }

            // 3. Le guidage — seulement pour le dithering
            if (guiderMediator.GetInfo().Connected) {
                lignes.Add(new LigneEtat("✅ Guidage connecté — dithering possible", BrosseVertClair));
            } else if (DitherActif) {
                lignes.Add(new LigneEtat("⚠ Pas de guideur : le dithering sera ignoré — astuce : connectez « Direct Guider » (Équipement > Guideur)", BrosseOrangeClair));
                remarque = true;
            } else {
                lignes.Add(new LigneEtat("· Guidage non connecté (dithering désactivé, rien à faire)", BrosseGriseClair));
            }

            // 4. La position — les suggestions et les calculs en dépendent
            double lat = profileService.ActiveProfile.AstrometrySettings.Latitude;
            double lon = profileService.ActiveProfile.AstrometrySettings.Longitude;
            if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001) {
                lignes.Add(new LigneEtat("❌ Position non réglée (0°, 0°) — carte 📍 du panneau Alignement", BrosseRougeClair));
                bloquant = true;
            } else {
                lignes.Add(new LigneEtat("✅ Position réglée (" + Math.Abs(lat).ToString("0.#") + "° " + (lat >= 0 ? "N" : "S") + ")", BrosseVertClair));
            }

            // 5. Le matériel — le cadrage en dépend
            var champ = CalculerChampArcmin();
            double focale = profileService.ActiveProfile.TelescopeSettings.FocalLength;
            if (champ != null) {
                lignes.Add(new LigneEtat("✅ Matériel renseigné — champ " + champ.Item1.ToString("0") + "′ × " + champ.Item2.ToString("0") + "′", BrosseVertClair));
            } else if (focale > 0) {
                lignes.Add(new LigneEtat("⚠ Focale connue (" + focale.ToString("0") + " mm) mais champ incalculable — caméra déconnectée ou taille de pixel absente", BrosseOrangeClair));
                remarque = true;
            } else {
                lignes.Add(new LigneEtat("⚠ Focale non renseignée — carte 🔭 du panneau Alignement (cadrage aveugle sinon)", BrosseOrangeClair));
                remarque = true;
            }

            // 6. Le disque des images
            try {
                var dossier = profileService.ActiveProfile.ImageFileSettings.FilePath;
                if (!string.IsNullOrWhiteSpace(dossier)) {
                    double libreGo = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(dossier)).AvailableFreeSpace / 1e9;
                    if (libreGo < 5) {
                        lignes.Add(new LigneEtat("⚠ Disque des images presque plein : " + libreGo.ToString("0.#") + " Go libres", BrosseOrangeClair));
                        remarque = true;
                    } else {
                        lignes.Add(new LigneEtat("✅ " + libreGo.ToString("0") + " Go libres pour les images", BrosseVertClair));
                    }
                }
            } catch { /* lecteur réseau exotique : on n'affiche rien */ }

            // Le verdict global, façon feu tricolore
            if (bloquant) {
                TableauBordTitre = "🚦 Pas encore prêt — réglez les lignes rouges";
                CouleurTableauBord = BrosseRouge;
            } else if (remarque) {
                TableauBordTitre = "🚦 Prêt à lancer (avec les remarques ci-dessous)";
                CouleurTableauBord = BrosseOrange;
            } else {
                TableauBordTitre = "🚦 Tout est prêt — bonne nuit d'étoiles !";
                CouleurTableauBord = BrosseVert;
            }

            EtatsMateriel.Clear();
            foreach (var ligne in lignes) { EtatsMateriel.Add(ligne); }
            RaisePropertyChanged(nameof(TableauBordTitre));
            RaisePropertyChanged(nameof(CouleurTableauBord));
        }

        // ------------------------------------------------------------------
        // Météo de la nuit (couverture nuageuse, service gratuit open-meteo)
        // ------------------------------------------------------------------

        // Les prévisions heure par heure : (heure locale, % de nuages)
        private List<Tuple<DateTime, int>> previsionsNuages;

        // Premier clic sur GO avec un souci (météo, aube, cible basse,
        // disque plein) = avertissements ; second clic = on y va quand
        // même (c'est vous le chef)
        private bool goConfirme;

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
            // async void : sans ce filet, une coupure réseau (fréquente sur un
            // site d'observation) remonterait jusqu'à N.I.N.A. et le ferait tomber.
            try { await EnvoyerVersNtfy(titre, texte, urgente); } catch { }
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
                RaisePropertyChanged(nameof(PeutPointer));
            }
        }

        public bool CibleEstChoisie => CibleChoisie != null;

        /// <summary>Bouton « Pointer » actif : une cible, et aucun pointage déjà en cours.</summary>
        public bool PeutPointer => CibleChoisie != null && !pointageEnCours;

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
                // ⚠ Tester NaN explicitement : une case jamais remplie du profil
                // vaut NaN, et « NaN <= 0 » est FAUX. Sans ce test, le calcul
                // renvoyait un champ « NaN′ × NaN′ » que le tableau de bord
                // affichait fièrement avec une coche verte. Vu en vrai le
                // 2026-08-29 (FocalLength = NaN dans le profil de l'utilisateur).
                if (double.IsNaN(focaleMm) || double.IsNaN(pixelMicrons)
                    || focaleMm <= 0 || pixelMicrons <= 0
                    || !camera.Connected || camera.XSize <= 0 || camera.YSize <= 0) {
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

        // Un double-clic lançait deux centrages en parallèle, qui se
        // disputaient la caméra et la monture
        private bool pointageEnCours;

        private async Task Pointer() {
            if (CibleChoisie == null || pointageEnCours) { return; }

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

            // Un GoTo seul se fie au modèle de pointage interne de la monture.
            // Quand ce modèle est faux — monture allumée hors position de repos,
            // pas manqués, vis d'alignement retouchées depuis — on atterrit à
            // plusieurs degrés de la cible SANS que rien ne le signale.
            // Dès que la caméra est là, on fait donc le vrai centrage :
            // photo -> reconnaissance du ciel -> correction, jusqu'à la
            // tolérance du profil. Même instruction que dans la série.
            bool centrageDispo = cameraMediator.GetInfo().Connected;

            MessageCible = centrageDispo
                ? "🔭 Pointage et centrage précis vers « " + CibleChoisie.Nom + " »… (photo, reconnaissance du ciel, correction — comptez quelques dizaines de secondes)"
                : "🔭 Pointage en cours vers « " + CibleChoisie.Nom + " »… (le télescope se déplace)";
            RaisePropertyChanged(nameof(MessageCible));

            pointageEnCours = true;
            RaisePropertyChanged(nameof(PeutPointer));
            try {
                if (centrageDispo) {
                    // « Center » lit sa cible dans le conteneur parent :
                    // on lui en fabrique un, identique à celui de la série.
                    var conteneur = new DeepSkyObjectContainer(profileService, nighttimeCalculator, framingAssistantVM,
                        applicationMediator, planetariumFactory, cameraMediator, filterWheelMediator);
                    conteneur.Name = CibleChoisie.Nom;
                    conteneur.Target.TargetName = CibleChoisie.NomCatalogue;
                    conteneur.Target.InputCoordinates.Coordinates = CibleChoisie.Coordonnees;

                    // Le centrage photographie le ciel : sans suivi les étoiles
                    // filent et la reconnaissance échoue. On force le sidéral.
                    try { telescopeMediator.SetTrackingMode(TrackingMode.Sidereal); } catch { }

                    var centrage = new Center(profileService, telescopeMediator, imagingMediator,
                        filterWheelMediator, guiderMediator, domeMediator, domeFollower,
                        plateSolverFactory, windowServiceFactory);
                    conteneur.Add(centrage); // c'est l'ajout qui rattache le parent

                    // Borne de sécurité : un centrage qui n'aboutit pas ne doit
                    // pas bloquer le panneau indéfiniment.
                    using (var arret = new CancellationTokenSource(TimeSpan.FromMinutes(5))) {
                        await centrage.Run(new Progress<NINA.Core.Model.ApplicationStatus>(), arret.Token);
                    }

                    // ⚠ Run() ne lève PAS d'exception quand le centrage échoue :
                    // par défaut N.I.N.A. note l'échec dans Status et continue
                    // (ErrorBehavior = ContinueOnError). Sans ce test, on
                    // affichait « VÉRIFIÉ » après un centrage raté.
                    MessageCible = centrage.Status == SequenceEntityStatus.FINISHED
                        ? "✅ « " + CibleChoisie.Nom + " » est centré, et c'est VÉRIFIÉ : le ciel a été photographié et reconnu. Vous pouvez lancer la série."
                        : "❌ Le centrage a ÉCHOUÉ : le télescope n'est PAS vérifié sur « " + CibleChoisie.Nom + " ». Causes habituelles : étoiles filées (suivi arrêté), nuages, buée ou mise au point. Le détail est dans le journal de N.I.N.A.";
                } else {
                    var reussi = await telescopeMediator.SlewToCoordinatesAsync(CibleChoisie.Coordonnees, CancellationToken.None);
                    MessageCible = reussi
                        ? "⚠ Télescope pointé sur « " + CibleChoisie.Nom + " » — mais SANS vérification : la caméra n'est pas connectée, donc aucun contrôle n'a pu être fait. Si la monture se trompe, rien ne le dira. Connectez la caméra et recommencez."
                        : "⚠ Le pointage a été refusé par la monture. Vérifiez qu'elle est déparquée et que le suivi est actif.";
                }
            } catch (OperationCanceledException) {
                MessageCible = "⚠ Le centrage a dépassé 5 minutes et a été interrompu. Causes habituelles : étoiles filées (le suivi ne fonctionne pas), pose de plate-solve trop courte, ou ciel voilé.";
            } catch (Exception ex) {
                MessageCible = "⚠ Le pointage a échoué : " + ex.Message
                    + (centrageDispo ? " — si c'est la reconnaissance du ciel qui échoue, vérifiez que les étoiles sont des points et non des traits." : "");
            } finally {
                pointageEnCours = false;
                RaisePropertyChanged(nameof(PeutPointer));
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

        /// <summary>Recaler la cible pile au centre avant la première photo
        /// (plate-solving + correction de la monture, répété jusqu'à être
        /// dans la tolérance du profil).</summary>
        public bool CentrageActif {
            get => reglages.GetValueBoolean(nameof(CentrageActif), true);
            set { reglages.SetValueBoolean(nameof(CentrageActif), value); RaisePropertyChanged(); }
        }

        /// <summary>
        /// Forcer le mode de lecture de la caméra au début de la série.
        ///
        /// Pourquoi ce réglage existe : N.I.N.A. n'envoie le mode de lecture à
        /// la caméra qu'à la connexion, jamais avant une pose. Si quoi que ce
        /// soit l'a changé entre-temps, toute la nuit peut être enregistrée en
        /// 8 bits sans que rien ne le signale. Cette instruction le repose
        /// explicitement à chaque lancement. Coût : nul.
        /// </summary>
        public bool ModeLectureActif {
            get => reglages.GetValueBoolean(nameof(ModeLectureActif), true);
            set { reglages.SetValueBoolean(nameof(ModeLectureActif), value); RaisePropertyChanged(); }
        }

        /// <summary>Refroidir la caméra avant la série, la réchauffer après.</summary>
        public bool RefroidirActif {
            get => reglages.GetValueBoolean(nameof(RefroidirActif), false);
            set { reglages.SetValueBoolean(nameof(RefroidirActif), value); RaisePropertyChanged(); }
        }

        /// <summary>Température visée, en °C (défaut −10).</summary>
        public string TemperatureCibleTexte {
            get => reglages.GetValueString(nameof(TemperatureCibleTexte), "-10");
            set { reglages.SetValueString(nameof(TemperatureCibleTexte), value); RaisePropertyChanged(); }
        }

        /// <summary>Parquer la monture à la fin de la série.</summary>
        public bool ParkFinActif {
            get => reglages.GetValueBoolean(nameof(ParkFinActif), false);
            set { reglages.SetValueBoolean(nameof(ParkFinActif), value); RaisePropertyChanged(); }
        }

        /// <summary>Arrêter la série quand la cible descend trop bas.</summary>
        public bool ArretBasActif {
            get => reglages.GetValueBoolean(nameof(ArretBasActif), false);
            set { reglages.SetValueBoolean(nameof(ArretBasActif), value); RaisePropertyChanged(); }
        }

        /// <summary>Hauteur minimale de la cible, en degrés (défaut 30).</summary>
        public string AltitudeMiniTexte {
            get => reglages.GetValueString(nameof(AltitudeMiniTexte), "30");
            set { reglages.SetValueString(nameof(AltitudeMiniTexte), value); RaisePropertyChanged(); }
        }

        /// <summary>Recentrer automatiquement si la monture dérive.</summary>
        public bool RecentrageDeriveActif {
            // Actif par défaut : sans autoguidage (le cas de l'utilisateur),
            // c'est ce qui garde la cible dans le cadre sur une longue série
            get => reglages.GetValueBoolean(nameof(RecentrageDeriveActif), true);
            set { reglages.SetValueBoolean(nameof(RecentrageDeriveActif), value); RaisePropertyChanged(); }
        }

        /// <summary>Dérive tolérée avant recentrage, en minutes d'arc (défaut 15 :
        /// ~120 px au 135 mm, champ de 8,6° — assez pour ne pas recentrer pour rien).</summary>
        public string DeriveMaxTexte {
            get => reglages.GetValueString(nameof(DeriveMaxTexte), "15");
            set { reglages.SetValueString(nameof(DeriveMaxTexte), value); RaisePropertyChanged(); }
        }

        /// <summary>
        /// Seuil d'ALERTE, en minutes d'arc (défaut 60). Au-delà, ce n'est plus
        /// une dérive normale mais un problème : suivi arrêté, monture qui a
        /// glissé, câble accroché. Alerte urgente sur le téléphone.
        /// </summary>
        public string DeriveAlerteTexte {
            get => reglages.GetValueString(nameof(DeriveAlerteTexte), "60");
            set { reglages.SetValueString(nameof(DeriveAlerteTexte), value); RaisePropertyChanged(); }
        }

        /// <summary>Dernière dérive mesurée pendant la série (vide = pas encore mesurée).</summary>
        public string DeriveMesureeTexte { get; private set; } = "";

        /// <summary>Contrôle de dérive toutes les N photos (défaut 10). N.I.N.A.
        /// résout la photo déjà enregistrée, en fond : aucune pose en plus.</summary>
        public string DeriveFrequenceTexte {
            get => reglages.GetValueString(nameof(DeriveFrequenceTexte), "10");
            set { reglages.SetValueString(nameof(DeriveFrequenceTexte), value); RaisePropertyChanged(); }
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
            var camera = cameraMediator.GetInfo();
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

            // Les garde-fous de la nuit : météo, aube, cible qui se couche,
            // place sur le disque. Tout est vérifié d'un coup ; premier clic
            // = la liste des soucis, second clic = on y va quand même.
            if (!goConfirme) {
                var soucis = new List<string>();
                var finPrevue = DateTime.Now.AddSeconds(nbPhotos * (poseSecondes + MargeParPhotoSecondes));
                double latGarde = profileService.ActiveProfile.AstrometrySettings.Latitude;
                double lonGarde = profileService.ActiveProfile.AstrometrySettings.Longitude;
                bool positionConnue = Math.Abs(latGarde) > 0.001 || Math.Abs(lonGarde) > 0.001;

                // 1. La météo (>= 70 % de nuages avant la fin)
                if (previsionsNuages != null) {
                    var mauvaiseHeure = previsionsNuages.FirstOrDefault(
                        p => p.Item1 >= DateTime.Now.AddMinutes(-30) && p.Item1 <= finPrevue && p.Item2 >= 70);
                    if (mauvaiseHeure != null) {
                        soucis.Add("Météo : " + mauvaiseHeure.Item2 + " % de nuages prévus vers "
                            + mauvaiseHeure.Item1.Hour + " h (fin de série " + finPrevue.ToString("HH\\hmm") + ").");
                    }
                }

                // 2. L'aube : où sera le Soleil à la fin de la série ?
                // (au-dessus de -10°, le ciel est déjà trop clair pour le
                // ciel profond — les dernières photos seraient délavées)
                if (positionConnue) {
                    try {
                        var observateur = new ObserverInfo {
                            Latitude = latGarde,
                            Longitude = lonGarde,
                            Elevation = profileService.ActiveProfile.AstrometrySettings.Elevation
                        };
                        var soleil = AstroUtil.GetMoonAndSunPosition(finPrevue, AstroUtil.GetJulianDate(finPrevue), observateur).Item2;
                        double altitudeSoleil = new Coordinates(soleil.RA, soleil.Dec, Epoch.J2000, Coordinates.RAType.Hours)
                            .Transform(Angle.ByDegree(latGarde), Angle.ByDegree(lonGarde), finPrevue).Altitude.Degree;
                        if (altitudeSoleil > -10) {
                            soucis.Add("Aube : à la fin prévue (" + finPrevue.ToString("HH\\hmm")
                                + "), le ciel sera déjà clair — raccourcissez la série pour finir de nuit.");
                        }
                    } catch { /* calcul astro raté = pas d'avertissement */ }
                }

                // 3. La cible qui descend : où sera-t-elle à la fin ?
                if (positionConnue && CibleChoisie != null) {
                    try {
                        double altitudeFin = CibleChoisie.Coordonnees
                            .Transform(Angle.ByDegree(latGarde), Angle.ByDegree(lonGarde), finPrevue).Altitude.Degree;
                        if (altitudeFin < 0) {
                            soucis.Add("Cible : elle sera COUCHÉE avant la fin de la série (sous l'horizon à " + finPrevue.ToString("HH\\hmm") + ") ! Raccourcissez, ou choisissez une cible plus à l'est.");
                        } else if (altitudeFin < 20) {
                            soucis.Add("Cible : elle ne sera plus qu'à " + altitudeFin.ToString("0")
                                + "° de haut en fin de série — si bas, les photos deviennent molles (atmosphère). Raccourcir la série serait mieux.");
                        }
                    } catch { }
                }

                // 4. Le disque : y a-t-il la place pour toutes ces photos ?
                try {
                    var dossierImages = profileService.ActiveProfile.ImageFileSettings.FilePath;
                    var cameraInfos = cameraMediator.GetInfo();
                    if (!string.IsNullOrWhiteSpace(dossierImages) && cameraInfos.XSize > 0 && cameraInfos.YSize > 0) {
                        var disque = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(dossierImages));
                        int nbTotal = nbPhotos + (DarksActif ? EntierOuDefaut(NbDarksTexte, 15) : 0);
                        // ~2 octets par pixel (FITS 16 bits, marge comprise)
                        double besoinGo = (double)cameraInfos.XSize * cameraInfos.YSize * 2 * nbTotal / 1e9;
                        double libreGo = disque.AvailableFreeSpace / 1e9;
                        if (libreGo < besoinGo + 2) {
                            soucis.Add("Disque : la série pèsera environ " + besoinGo.ToString("0.#")
                                + " Go, mais il ne reste que " + libreGo.ToString("0.#")
                                + " Go sur le disque des images. Faites de la place !");
                        }
                    }
                } catch { }

                if (soucis.Count > 0) {
                    Avertissement = "⚠ " + string.Join("\n⚠ ", soucis)
                        + "\n→ Corrigez… ou cliquez une seconde fois sur LANCER pour y aller quand même.";
                    RaisePropertyChanged(nameof(Avertissement));
                    goConfirme = true;
                    return;
                }
            }

            Avertissement = "";
            var notes = "";

            // Un réchauffement de la séance précédente tourne peut-être
            // encore : il ne doit pas se battre avec le refroidissement
            try { rechauffementArret?.Cancel(); } catch { }

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

            // ---- Zone de début : ce qu'on fait AVANT la première photo ----

            // 1. Reposer le mode de lecture de la caméra.
            //    N.I.N.A. ne l'envoie qu'à la connexion de la caméra, jamais
            //    avant une pose : si quelque chose l'a changé entre-temps, la
            //    nuit entière peut partir en 8 bits sans le moindre message.
            //    Cette instruction officielle le repose explicitement.
            var modeVoulu = profileService.ActiveProfile.CameraSettings.ReadoutModeForNormalImages;
            //    ⚠ Seulement si la caméra connaît ce mode : un profil hérité
            //    d'une autre caméra (vu le 23 sept 2026 : mode 2 dans le
            //    profil, la SV405CC n'a que le mode 0) déclenchait l'alerte
            //    « Readout mode not supported » de N.I.N.A. à chaque série.
            int nbModes = camera.ReadoutModes?.Count() ?? 0;
            if (ModeLectureActif && camera.Connected && modeVoulu.HasValue) {
                if (modeVoulu.Value >= 0 && modeVoulu.Value < nbModes) {
                    zoneDebut.Add(new SetReadoutMode(cameraMediator) { Mode = modeVoulu.Value });
                    notes += "Mode de lecture reposé · ";
                } else {
                    notes += "Mode de lecture n° " + modeVoulu.Value + " du profil absent de la caméra, ignoré · ";
                }
            }

            // 2. Déparquer la monture : parquée, elle refuse de bouger et tout
            //    le reste échoue. Sans effet si elle ne l'est pas.
            if (monture.Connected) {
                zoneDebut.Add(new UnparkScope(telescopeMediator));
                // Puis FORCER le sidéral : un TPPA lancé juste avant l'a très
                // probablement coupé (« Stop tracking when done »). Sans suivi,
                // étoiles filées, et le centrage qui suit échoue.
                zoneDebut.Add(new SetTracking(telescopeMediator) { TrackingMode = TrackingMode.Sidereal });
            }

            // 3. Refroidir, en descendant progressivement (SVBONY recommande
            //    au moins 3 minutes ; on en met 5). La consigne doit être la
            //    MÊME que celle de vos darks, sinon ils ne correspondent plus.
            //    SANS ATTENDRE (choix de l'utilisateur, 23 sept 2026) : l'instruction
            //    CoolCamera bloquait la série jusqu'à la consigne. Le refroidissement
            //    part maintenant en parallèle (RefroidirEnFond) et les photos
            //    commencent tout de suite ; les premières sont un peu plus chaudes.
            double? consigneRefroidissement = null;
            if (RefroidirActif && camera.Connected) {
                consigneRefroidissement = DoubleOuDefaut(TemperatureCibleTexte, -10);
                notes += "Refroidissement à " + consigneRefroidissement.Value.ToString("0") + " °C (sans attendre) · ";
            }

            // La "cible" : porte le nom de l'objet (pour les noms de fichiers
            // et l'affichage) et contient les instructions de prise de vue
            var conteneurCible = new DeepSkyObjectContainer(profileService, nighttimeCalculator, framingAssistantVM,
                applicationMediator, planetariumFactory, cameraMediator, filterWheelMediator);
            if (CibleChoisie != null) {
                conteneurCible.Name = CibleChoisie.Nom;
                conteneurCible.Target.TargetName = CibleChoisie.NomCatalogue;
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

            // Centrage précis : l'instruction officielle « Center » de
            // N.I.N.A. (photo -> plate-solve -> recalage de la monture,
            // répété jusqu'à la tolérance du profil). Elle lit la cible du
            // conteneur parent. Seulement si une cible est choisie ET que la
            // monture obéit — sinon GoTo simple, comme avant.
            centrageSerie = null;
            if (CentrageActif && CibleChoisie != null && monture.Connected) {
                // Par défaut, N.I.N.A. passe à la suite quand une instruction
                // échoue : un centrage raté = toute la nuit photographiée au
                // mauvais endroit. On saute plutôt directement aux instructions
                // de fin (réchauffer, parquer) et TerminerSerie prévient.
                centrageSerie = new Center(profileService, telescopeMediator, imagingMediator,
                    filterWheelMediator, guiderMediator, domeMediator, domeFollower,
                    plateSolverFactory, windowServiceFactory) {
                    ErrorBehavior = InstructionErrorBehavior.SkipToSequenceEndInstructions
                };
                conteneurCible.Add(centrageSerie);
                notes += "Centrage précis au départ · ";
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

            // Arrêt quand la cible descend trop bas : sous ~30°, on photographie
            // à travers beaucoup plus d'atmosphère et les images se dégradent
            // vite. Utile surtout si vous laissez tourner en dormant.
            if (ArretBasActif && CibleChoisie != null) {
                double altMini = DoubleOuDefaut(AltitudeMiniTexte, 30);
                var conditionHauteur = new AltitudeCondition(profileService) { HasDsoParent = true };
                conditionHauteur.Data.Offset = altMini;
                conteneurCible.Add(conditionHauteur);
                notes += "Arrêt sous " + altMini.ToString("0") + "° · ";
            }

            zoneCibles.Add(conteneurCible);

            // Retournement au méridien : seulement si monture connectée
            if (FlipActif && monture.Connected) {
                racine.Add(new MeridianFlipTrigger(profileService, cameraMediator, telescopeMediator,
                    focuserMediator, applicationStatusMediator, meridianFlipVMFactory));
                notes += "Retournement au méridien surveillé · ";
            } else if (FlipActif) {
                notes += "Retournement au méridien ignoré (monture non connectée) · ";
            }

            // Recentrage automatique sur dérive : sans autoguidage, la cible
            // s'échappe lentement du cadre. Ce déclencheur mesure l'écart par
            // astrométrie et recentre au-delà du seuil. C'est ce qui sauve une
            // longue série non guidée.
            if (declencheurDerive != null) { declencheurDerive.PropertyChanged -= QuandDeriveMesuree; declencheurDerive = null; }
            DeriveMesureeTexte = "";
            deriveAlerteEnvoyee = false;
            if (RecentrageDeriveActif && monture.Connected && CibleChoisie != null) {
                double deriveMax = DoubleOuDefaut(DeriveMaxTexte, 15);
                declencheurDerive = new CenterAfterDriftTrigger(profileService, telescopeMediator,
                    filterWheelMediator, guiderMediator, imagingMediator, cameraMediator,
                    domeMediator, domeFollower, imageSaveMediator, applicationStatusMediator) {
                    DistanceArcMinutes = deriveMax,
                    AfterExposures = EntierOuDefaut(DeriveFrequenceTexte, 10)
                };
                declencheurDerive.PropertyChanged += QuandDeriveMesuree;
                conteneurCible.Add(declencheurDerive);
                notes += "Contrôle de dérive toutes les " + EntierOuDefaut(DeriveFrequenceTexte, 10)
                    + " photos, recentrage au-delà de " + deriveMax.ToString("0.#") + "′ · ";
            }

            // ---- Zone de fin : ce qu'on fait APRÈS la dernière photo ----

            // Réchauffer doucement : éviter le choc thermique et la condensation.
            // Sauf si des darks suivent : WarmCamera coupe le refroidissement,
            // et des darks pris à température ambiante ne correspondraient plus
            // aux photos. Le réchauffement se fait alors en toute fin de séance
            // (RechaufferSiDiffere).
            rechauffementDiffere = false;
            if (RefroidirActif && camera.Connected) {
                if (DarksActif) {
                    rechauffementDiffere = true;
                } else {
                    zoneFin.Add(new WarmCamera(cameraMediator) { Duration = 5 });
                }
            }

            // Parquer la monture : elle se remet en position repos, à l'abri
            if (ParkFinActif && monture.Connected) {
                zoneFin.Add(new ParkScope(telescopeMediator, guiderMediator));
                notes += "Monture parquée à la fin · ";
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
            goConfirme = false;         // la prochaine série re-vérifiera tout
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
                if (consigneRefroidissement.HasValue) { RefroidirEnFond(consigneRefroidissement.Value); }
                await sequenceMediator.StartAdvancedSequence(false);
                TerminerSerie(null);
            } catch (Exception ex) {
                TerminerSerie(ex);
            }
        }

        private CancellationTokenSource refroidissementArret;

        /// <summary>
        /// Refroidit la caméra en parallèle de la série : descente progressive
        /// sur 5 min (SVBONY recommande au moins 3), puis N.I.N.A. maintient la
        /// consigne. S'il ne l'atteint pas (nuit chaude, Peltier non alimenté),
        /// N.I.N.A. abandonne seul après ~2 min sans progrès : la série, elle,
        /// n'est jamais bloquée.
        /// </summary>
        private async void RefroidirEnFond(double consigne) {
            try { refroidissementArret?.Cancel(); } catch { }
            refroidissementArret = new CancellationTokenSource();
            try {
                await cameraMediator.CoolCamera(consigne, TimeSpan.FromMinutes(5),
                    new Progress<NINA.Core.Model.ApplicationStatus>(), refroidissementArret.Token);
            } catch (Exception ex) {
                // async void : rien ne doit remonter jusqu'à N.I.N.A.
                Logger.Error(ex);
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
            RechaufferSiDiffere();
            string quoi = darksEnCours ? "dark(s)" : "photo(s)";

            // Le centrage de départ a échoué : N.I.N.A. a sauté aux
            // instructions de fin (voir Demarrer). À dire clairement — ce
            // n'est ni un arrêt demandé, ni une série « presque » réussie.
            bool centrageRate = !darksEnCours && centrageSerie != null
                && centrageSerie.Status == SequenceEntityStatus.FAILED;

            if (centrageRate) {
                TitreBilan = "❌ Série arrêtée : le centrage a échoué";
                ResumeBilan = "Le télescope n'a pas pu être calé sur « " + nomCibleEnCours + " » (reconnaissance du ciel impossible). "
                    + "Plutôt que de photographier au mauvais endroit toute la nuit, la série a été arrêtée.\n"
                    + "Causes habituelles : étoiles filées (suivi arrêté), nuages, buée, mise au point.";
                CouleurBilan = BrosseRouge;
            } else if (erreur != null) {
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
            if (centrageRate) {
                EnvoyerAlerte("Serie arretee - centrage rate",
                    "❌ Centrage impossible sur « " + nomCibleEnCours + " » : série arrêtée plutôt que de photographier au mauvais endroit. Suivi ? nuages ? buée ?", true);
            } else if (erreur != null) {
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

            // Télescope bouché : le suivi ne sert plus à rien, et plus rien ne
            // surveille le méridien pendant les darks (pas de retournement ici).
            // On l'arrête pour que la monture ne tourne pas, sans surveillance,
            // vers le trépied. La prochaine série et le bouton « Pointer »
            // remettent eux-mêmes le sidéral.
            var montureDarks = telescopeMediator.GetInfo();
            bool suiviCoupe = montureDarks.Connected && !montureDarks.AtPark;
            if (suiviCoupe) {
                zoneDebut.Add(new SetTracking(telescopeMediator) { TrackingMode = TrackingMode.Stopped });
            }

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
            NotesSerie = "Télescope couvert · " + nbDarks + " darks de " + poseSecondesEnCours.ToString("0.#") + " s"
                + (suiviCoupe ? " · suivi de la monture arrêté (inutile, télescope bouché)" : "");
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
            RechaufferSiDiffere();
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

        /// <summary>
        /// Le réchauffement mis de côté pour les darks (voir Demarrer), fait
        /// en toute fin de séance — darks faits, sautés, ou série interrompue.
        /// Même instruction que la zone de fin d'une série sans darks.
        /// </summary>
        private async void RechaufferSiDiffere() {
            if (!rechauffementDiffere) { return; }
            rechauffementDiffere = false;
            try { refroidissementArret?.Cancel(); } catch { }
            try {
                if (!cameraMediator.GetInfo().Connected) { return; }
                rechauffementArret = new CancellationTokenSource();
                await new WarmCamera(cameraMediator) { Duration = 5 }
                    .Run(new Progress<NINA.Core.Model.ApplicationStatus>(), rechauffementArret.Token);
            } catch {
                // async void : rien ne doit remonter jusqu'à N.I.N.A.
            }
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
            goConfirme = false;

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
                // Le dossier de LA NUIT, tel que N.I.N.A. le nomme ($$DATEMINUS12$$ :
                // une nuit qui passe minuit garde la date de la veille)
                var dossierNuit = System.IO.Path.Combine(dossier, serieDebut.AddHours(-12).ToString("yyyy-MM-dd"));
                html.Append("<p>1. Copiez le dossier de la nuit <code>" + Proprifier(dossierNuit) + "</code> tel quel "
                    + "(LIGHT" + (nbDarks > 0 ? ", DARK" : "") + "…) · 2. Dans Siril : <i>Scripts</i> → <i>Scripts Python</i> → "
                    + "<b>OSC_Studio</b> · 3. Choisissez ce dossier : les cibles sont trouvées toutes seules · "
                    + "4. Lancez, patientez, admirez.</p>");
                html.Append("<p style='opacity:.6;font-size:13px'>Pas de darks cette nuit ? OSC Studio prend ceux de sa "
                    + "bibliothèque s'ils ont les mêmes réglages. Des flats en fin de nuit (sans toucher à la mise au point) "
                    + "corrigeraient le vignettage.</p>");
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

        private CenterAfterDriftTrigger declencheurDerive;
        private bool deriveAlerteEnvoyee;

        /// <summary>
        /// À chaque contrôle, N.I.N.A. publie la dérive mesurée
        /// (LastDistanceArcMinutes). On l'affiche, et au-delà du seuil
        /// d'alerte on prévient le téléphone (une fois par série) : une dérive
        /// pareille, c'est un suivi arrêté ou une monture qui a bougé.
        /// </summary>
        private void QuandDeriveMesuree(object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName != nameof(CenterAfterDriftTrigger.LastDistanceArcMinutes) || declencheurDerive == null) { return; }
            try {
                double d = declencheurDerive.LastDistanceArcMinutes;
                if (double.IsNaN(d) || d < 0) { return; }
                double seuil = DoubleOuDefaut(DeriveMaxTexte, 15);
                double alerte = DoubleOuDefaut(DeriveAlerteTexte, 60);

                DeriveMesureeTexte = "🎯 Dérive mesurée : " + d.ToString("0.0") + "′ à " + DateTime.Now.ToString("HH\\hmm")
                    + (d >= alerte ? "  ❌ ANORMAL (suivi arrêté ? monture qui a bougé ?)"
                       : d >= seuil ? "  → recentrage" : "  ✅");
                RaisePropertyChanged(nameof(DeriveMesureeTexte));

                if (d >= alerte && !deriveAlerteEnvoyee) {
                    deriveAlerteEnvoyee = true;
                    EnvoyerAlerte("Derive anormale",
                        "❌ La cible a dérivé de " + d.ToString("0") + "′ (seuil d'alerte " + alerte.ToString("0")
                        + "′). Ce n'est pas une dérive normale : suivi arrêté, monture qui a glissé, câble accroché ? N.I.N.A. tente de recentrer.", true);
                }
            } catch { /* l'affichage de la dérive est un bonus */ }
        }

        public ICommand MoinsDixCommand { get; }
        public ICommand MoinsUnCommand { get; }
        public ICommand PlusUnCommand { get; }
        public ICommand PlusDixCommand { get; }
        public ICommand FinirApresCommand { get; }

        /// <summary>
        /// Change le nombre de photos EN COURS de série : la boucle de
        /// N.I.N.A. relit Iterations avant chaque photo. Plancher = la photo
        /// en cours (jamais interrompue). Réduire au plancher = « finir après
        /// celle-ci » : la série se termine proprement, darks compris.
        /// </summary>
        private void AjusterPhotos(int delta, bool finirApres = false) {
            if (phase != Phase.EnCours || boucle == null) { return; }
            int plancher = PhotosFaites + 1;
            int nouveau = finirApres ? plancher : Math.Max(plancher, nbPhotosTotal + delta);
            boucle.Iterations = nouveau;
            nbPhotosTotal = nouveau;
            RaisePropertyChanged(nameof(NbPhotosTotal));
            RaisePropertyChanged(nameof(ProgressionTexte));
            RaisePropertyChanged(nameof(TempsRestantTexte));
        }

        private void NouvelleSerie() {
            phase = Phase.Preparation;
            Avertissement = "";
            goConfirme = false;
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

        // Une seule préparation de vignette à la fois : étirer une image de
        // 12 mégapixels prend un moment, et les photos peuvent s'enchaîner
        private bool vignetteEnCours;

        private async void QuandImagePrete(object sender, ImagePreparedEventArgs e) {
            if (phase != Phase.EnCours) { return; }
            var rendu = e?.RenderedImage;
            if (rendu == null) { return; }

            // Les photos de centrage (Center, recentrage sur dérive, retournement
            // au méridien) passent AUSSI par ici, en type SNAPSHOT : 5 s de pose,
            // donc bien moins d'étoiles. Jugées comme des photos de la série,
            // elles déclenchaient « nuages ou buée ? » sur le téléphone en
            // pleine nuit et faussaient le bilan. On ne garde que les vraies.
            var typeImage = rendu.RawImageData?.MetaData?.Image?.ImageType;
            if (typeImage != null && typeImage != (darksEnCours ? "DARK" : "LIGHT")) { return; }

            // ⚠ L'événement transmet l'image AVANT étirement : affichée telle
            // quelle, elle est presque noire. Voir AideImage pour le détail —
            // c'est un comportement de N.I.N.A., pas un réglage.
            if (!vignetteEnCours) {
                vignetteEnCours = true;
                try {
                    var affichable = await AideImage.PreparerVignette(rendu, profileService);
                    if (affichable != null) {
                        DerniereImage = affichable;
                        RaisePropertyChanged(nameof(DerniereImage));
                    }
                } catch {
                    // La vignette est un confort : jamais bloquant pour la séquence
                } finally {
                    vignetteEnCours = false;
                }
            }

            // Un dark est tout noir : compter ses étoiles n'aurait aucun sens
            // (le verdict hurlerait « aucune étoile ! » à chaque image)
            if (!darksEnCours) {
                AnalyserQualite(rendu);
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

        // Teintes claires pour du TEXTE sur le fond sombre de N.I.N.A.
        private static readonly Brush BrosseVertClair = CreerBrosse(129, 199, 132);
        private static readonly Brush BrosseOrangeClair = CreerBrosse(255, 183, 77);
        private static readonly Brush BrosseRougeClair = CreerBrosse(229, 115, 115);
        private static readonly Brush BrosseGriseClair = CreerBrosse(160, 160, 160);

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
            NomCatalogue = objet.Name;
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

        /// <summary>
        /// Nom de catalogue seul : « M 31 ». C'est lui qui part dans les
        /// fichiers (mot-clé FITS OBJECT, lu par OSC Studio sous Siril) : le
        /// tiret de « M 81 — Bode's Galaxy » y devenait « ? » (FITS = ASCII).
        /// </summary>
        public string NomCatalogue { get; }

        /// <summary>La ligne affichée dans la liste des résultats.</summary>
        public string Description { get; }

        /// <summary>Position dans le ciel (J2000), prête pour le GoTo.</summary>
        public Coordinates Coordonnees { get; }

        /// <summary>Au-dessus de l'horizon en ce moment ?</summary>
        public bool EstVisible { get; }
    }

    /// <summary>Une ligne du tableau de bord « Tout est prêt ? ».</summary>
    public class LigneEtat {

        public LigneEtat(string texte, Brush couleur) {
            Texte = texte;
            Couleur = couleur;
        }

        public string Texte { get; }
        public Brush Couleur { get; }
    }

    /// <summary>Commande WPF minimale pour une action instantanée.</summary>
    public class CommandeSimple2 : ICommand {
        private readonly Action action;

        public CommandeSimple2(Action action) {
            this.action = action;
        }

        public event EventHandler CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter) {
            try {
                action();
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError("Mode Débutant : " + ex.Message);
            }
        }
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

        // async void : une exception qui s'échappe d'ici fait tomber N.I.N.A.
        // tout entier (en pleine préparation de série, par exemple). On la
        // note dans le journal et on l'affiche, au lieu de planter.
        public async void Execute(object parameter) {
            try {
                await action();
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError("Mode Débutant : " + ex.Message);
            }
        }
    }
}
