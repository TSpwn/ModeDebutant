using NINA.Core.Enum;
using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ModeDebutant.AlignementPolaire {

    /// <summary>
    /// Test de charge de la monture.
    ///
    /// Objectif : savoir si la monture obéit vraiment quand les moteurs
    /// travaillent — et surtout si elle S'ARRÊTE quand on le lui demande.
    /// Un ordre d'arrêt perdu laisse l'axe en roue libre : la monture part
    /// n'importe où et rien ne le signale. C'est ce qui ruine une nuit.
    ///
    /// ── Pourquoi la mesure est délicate ──────────────────────────────
    /// On ne lit pas la monture en direct : N.I.N.A. tient une copie de sa
    /// position, rafraîchie à intervalle régulier (réglage « Device polling
    /// interval », 2 s par défaut). Relire trop tôt après l'ordre d'arrêt
    /// renvoie donc une valeur d'AVANT l'arrêt, et on conclut à tort que
    /// l'axe continue de bouger.
    ///
    /// D'où deux précautions :
    ///   1. on attend largement plus que l'intervalle de rafraîchissement
    ///      avant de commencer à mesurer ;
    ///   2. on ne compare pas à un seuil fixe mais à la dérive mesurée au
    ///      repos en début de test — ce qui absorbe d'un coup le suivi
    ///      sidéral, les arrondis et la latence de rafraîchissement.
    /// </summary>
    public class TestCharge {

        private readonly ITelescopeMediator telescopeMediator;

        /// <summary>
        /// Comment prendre une photo. Fournie par l'écran appelant : le test
        /// n'a pas à connaître la caméra, il a juste besoin de savoir
        /// occuper l'USB et l'alimentation pendant qu'il fait travailler les
        /// moteurs. null = pas de caméra pendant le test.
        /// </summary>
        private readonly Func<double, CancellationToken, Task> prendreUnePhoto;

        public TestCharge(ITelescopeMediator telescopeMediator,
                          Func<double, CancellationToken, Task> prendreUnePhoto = null) {
            this.telescopeMediator = telescopeMediator;
            this.prendreUnePhoto = prendreUnePhoto;
        }

        // ------------------------------------------------------------------
        // Réglages du test
        // ------------------------------------------------------------------

        public class Options {
            /// <summary>Vitesse de déplacement, en degrés par seconde.</summary>
            public double Vitesse = 1.5;

            /// <summary>Durée de chaque poussée, en millisecondes.</summary>
            public int DureeMouvementMs = 2000;

            /// <summary>Nombre d'allers-retours.</summary>
            public int NbCycles = 4;

            /// <summary>
            /// Faire tourner la caméra en boucle pendant le test. C'est ce qui
            /// reproduit les conditions réelles d'une séance : la monture et la
            /// caméra se partagent l'USB et l'alimentation, exactement comme
            /// pendant un alignement polaire.
            /// </summary>
            public bool AvecCamera = false;

            /// <summary>Durée des poses de la caméra, en secondes.</summary>
            public double PoseCamera = 2.0;

            public string Nom => NbCycles == 0 ? "verification du suivi"
                : AvecCamera ? "DUR (moteurs + caméra)" : "normal";

            /// <summary>Vrai quand on ne fait que mesurer la dérive au repos.</summary>
            public bool SuiviSeulement => NbCycles == 0;
        }

        public static Options Normal() => new Options();

        /// <summary>
        /// Vérification du suivi seule : on ne bouge rien, on regarde juste si
        /// la position reste stable. Une monture qui suit garde des
        /// coordonnées constantes ; une monture à l'arrêt voit sa position
        /// rapportée dériver à la vitesse sidérale (15″/s). 20 secondes
        /// suffisent pour faire la différence sans aucune ambiguïté.
        /// </summary>
        public static Options VerificationSuivi() => new Options { NbCycles = 0, AvecCamera = false };

        /// <summary>
        /// Mode dur : plus vite, plus longtemps, avec la caméra qui travaille
        /// en même temps. C'est le test qui ressemble à un vrai TPPA.
        /// </summary>
        public static Options Dur() => new Options {
            Vitesse = 3.0,
            DureeMouvementMs = 5000,
            NbCycles = 6,
            AvecCamera = true,
            PoseCamera = 2.0
        };

        // ------------------------------------------------------------------
        // Résultat
        // ------------------------------------------------------------------

        public class Resultat {
            public int Mesures;                 // nombre d'allers ou retours evalues
            public int PasBouge;                // commande, mais rien n'a bouge
            public int PasArrete;               // continue apres l'ordre d'arret
            public int Deconnexions;
            public int Erreurs;
            public int PhotosPrises;
            public bool Interrompu;
            public double DeriveReposDegParSec;
            public readonly List<string> Journal = new List<string>();

            public void Noter(string ligne) {
                Journal.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + ligne);
            }
        }

        // ------------------------------------------------------------------
        // Constantes de mesure
        // ------------------------------------------------------------------

        /// <summary>Durée de la mesure de dérive au repos, en secondes.</summary>
        private const int DureeReposSec = 20;

        /// <summary>
        /// Attente après l'ordre d'arrêt avant de commencer à mesurer.
        /// Doit dépasser confortablement l'intervalle de rafraîchissement de
        /// N.I.N.A. (2 s par défaut) plus le temps de freinage de la monture.
        /// </summary>
        private const int AttenteApresStopMs = 5000;

        /// <summary>Durée de la fenêtre d'observation après l'arrêt, en secondes.</summary>
        private const int FenetreControleSec = 6;

        /// <summary>
        /// Marge au-dessus de la dérive au repos, en degrés par seconde.
        /// 0,01°/s = 36″/s, soit plus du double du rythme sidéral : on ne
        /// déclenche que sur un mouvement franc, jamais sur du bruit.
        /// </summary>
        private const double MargeDegParSec = 0.010;

        /// <summary>Fraction du déplacement attendu à partir de laquelle on dit « ça a bougé ».</summary>
        private const double FractionMouvementAttendu = 0.10;

        // ------------------------------------------------------------------

        private (double ad, double dec) Position() {
            var info = telescopeMediator.GetInfo();
            return (info.RightAscension * 15.0, info.Declination);
        }

        private static double Ecart((double ad, double dec) a, (double ad, double dec) b) {
            // L'ascension droite boucle a 360 deg : on prend le plus court chemin
            double dad = Math.Abs(a.ad - b.ad);
            if (dad > 180) { dad = 360 - dad; }
            return dad + Math.Abs(a.dec - b.dec);
        }

        private bool Connectee() {
            try { return telescopeMediator.GetInfo().Connected; } catch { return false; }
        }

        private bool Bouger(int axe, double vitesse) {
            try {
                // MoveAxis ne renvoie rien : seule une exception signale un refus.
                telescopeMediator.MoveAxis(
                    axe == 0 ? TelescopeAxes.Primary : TelescopeAxes.Secondary, vitesse);
                return true;
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Observe la position pendant N secondes et renvoie la vitesse de
        /// déplacement observée, en degrés par seconde.
        /// </summary>
        private async Task<double> MesurerDerive(int secondes, Resultat r, CancellationToken jeton) {
            var debut = Position();
            var t0 = DateTime.UtcNow;
            for (int i = 0; i < secondes; i++) {
                await Task.Delay(1000, jeton);
                if (!Connectee()) { r.Deconnexions++; }
            }
            double duree = (DateTime.UtcNow - t0).TotalSeconds;
            return duree > 0 ? Ecart(debut, Position()) / duree : 0;
        }

        // ------------------------------------------------------------------

        public async Task<Resultat> Lancer(Options o, Action<string> dire, CancellationToken jeton) {
            var r = new Resultat();
            r.Noter("Test " + o.Nom + " — vitesse " + o.Vitesse.ToString("0.0", CultureInfo.InvariantCulture)
                + " deg/s, poussees de " + (o.DureeMouvementMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                + " s, " + o.NbCycles + " cycles");

            if (!Connectee()) {
                r.Noter("ARRET : monture non connectee");
                return r;
            }

            // La caméra tourne en tâche de fond pendant tout le test
            CancellationTokenSource arretCamera = null;
            Task boucleCamera = null;

            try {
                if (o.AvecCamera && prendreUnePhoto != null) {
                    arretCamera = CancellationTokenSource.CreateLinkedTokenSource(jeton);
                    boucleCamera = BoucleCamera(o.PoseCamera, r, arretCamera.Token);
                    r.Noter("Camera lancee en boucle (poses de "
                        + o.PoseCamera.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
                } else if (o.AvecCamera) {
                    r.Noter("Camera indisponible : test des moteurs seuls");
                }

                // ---- Phase 1 : dérive au repos (la référence) ---------------
                dire("Phase 1/3 — mesure au repos (" + DureeReposSec + " s), ne touchez à rien…");
                r.DeriveReposDegParSec = await MesurerDerive(DureeReposSec, r, jeton);
                r.Noter("Derive au repos : " + (r.DeriveReposDegParSec * 3600).ToString("0.0", CultureInfo.InvariantCulture)
                    + " arcsec/s");

                double seuil = r.DeriveReposDegParSec + MargeDegParSec;
                double attendu = o.Vitesse * (o.DureeMouvementMs / 1000.0);

                // ---- Phase 2 : sous charge ----------------------------------
                for (int cycle = 1; cycle <= o.NbCycles && !jeton.IsCancellationRequested; cycle++) {
                    foreach (var sens in new[] { 1.0, -1.0 }) {
                        if (jeton.IsCancellationRequested) { break; }

                        string etiquette = "cycle " + cycle + "/" + o.NbCycles + (sens > 0 ? " aller" : " retour");
                        dire("Phase 2/3 — " + etiquette + "…");

                        var depart = Position();

                        // Les deux axes ensemble : pire cas pour l'alimentation
                        if (!Bouger(0, o.Vitesse * sens) || !Bouger(1, o.Vitesse * sens)) { r.Erreurs++; }
                        await Task.Delay(o.DureeMouvementMs, jeton);

                        double parcouru = Ecart(depart, Position());
                        bool aBouge = parcouru >= attendu * FractionMouvementAttendu;

                        // L'ORDRE D'ARRÊT — le moment de vérité
                        if (!Bouger(0, 0) || !Bouger(1, 0)) { r.Erreurs++; }

                        // On laisse largement le temps à la monture de freiner
                        // ET à N.I.N.A. de rafraîchir sa copie de la position
                        dire("Phase 2/3 — " + etiquette + " : contrôle de l'arrêt…");
                        await Task.Delay(AttenteApresStopMs, jeton);

                        double deriveApres = await MesurerDerive(FenetreControleSec, r, jeton);
                        bool sestArrete = deriveApres <= seuil;

                        if (!aBouge) { r.PasBouge++; }
                        if (!sestArrete) { r.PasArrete++; }
                        if (!Connectee()) { r.Deconnexions++; }
                        r.Mesures++;

                        r.Noter(etiquette
                            + " | parcouru " + parcouru.ToString("0.00", CultureInfo.InvariantCulture)
                            + " deg (attendu ~" + attendu.ToString("0.0", CultureInfo.InvariantCulture) + ")"
                            + " | apres arret " + (deriveApres * 3600).ToString("0.0", CultureInfo.InvariantCulture)
                            + " arcsec/s (seuil " + (seuil * 3600).ToString("0.0", CultureInfo.InvariantCulture) + ")"
                            + (aBouge ? "" : "  >> N'A PAS BOUGE")
                            + (sestArrete ? "" : "  >> NE S'EST PAS ARRETE"));
                    }
                }

                dire("Phase 3/3 — arrêt des axes…");
            } catch (OperationCanceledException) {
                r.Interrompu = true;
                r.Noter("Test interrompu");
            } catch (Exception ex) {
                r.Erreurs++;
                r.Noter("EXCEPTION : " + ex.Message);
            } finally {
                // Quoi qu'il arrive, aucun axe ne reste en mouvement
                try { Bouger(0, 0); } catch { }
                try { Bouger(1, 0); } catch { }

                if (arretCamera != null) {
                    arretCamera.Cancel();
                    try { if (boucleCamera != null) { await boucleCamera; } } catch { }
                    arretCamera.Dispose();
                }
                r.Noter("Fin — " + r.Mesures + " mesures, " + r.PasArrete + " sans arret, "
                    + r.PasBouge + " sans mouvement, " + r.Deconnexions + " coupures, "
                    + r.PhotosPrises + " photos");
            }

            return r;
        }

        /// <summary>
        /// Prend des photos en continu pendant le test, pour charger l'USB et
        /// l'alimentation comme pendant une vraie séance.
        /// </summary>
        private async Task BoucleCamera(double pose, Resultat r, CancellationToken jeton) {
            try {
                while (!jeton.IsCancellationRequested) {
                    await prendreUnePhoto(pose, jeton);
                    r.PhotosPrises++;
                }
            } catch (OperationCanceledException) {
                // arrêt normal en fin de test
            } catch (Exception ex) {
                r.Noter("Camera arretee sur erreur : " + ex.Message);
            }
        }
    }
}
