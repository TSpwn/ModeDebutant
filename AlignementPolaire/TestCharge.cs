using NINA.Core.Enum;
using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ModeDebutant.AlignementPolaire {

    /// <summary>
    /// Test de charge de la monture.
    ///
    /// Objectif : distinguer une monture qui va bien d'une monture dont la
    /// liaison décroche quand les moteurs tirent du courant. C'est LE
    /// symptôme d'une alimentation insuffisante ou d'une tension qui
    /// s'effondre sous charge.
    ///
    /// Le test ne se contente pas de compter des erreurs : il vérifie les
    /// trois choses qui cassent réellement une nuit.
    ///
    ///  1. La monture RÉPOND-ELLE ? (sa position change quand on la commande)
    ///  2. S'ARRÊTE-T-ELLE ? (elle s'immobilise quand on lui dit stop)
    ///  3. RESTE-T-ELLE CONNECTÉE ? (pas de coupure pendant l'effort)
    ///
    /// Le point 2 est le plus important : un ordre d'arrêt perdu laisse l'axe
    /// en roue libre. La monture part alors n'importe où, et rien ne le
    /// signale — c'est ce qui transforme une soirée en cauchemar.
    /// </summary>
    public class TestCharge {

        private readonly ITelescopeMediator telescopeMediator;

        public TestCharge(ITelescopeMediator telescopeMediator) {
            this.telescopeMediator = telescopeMediator;
        }

        /// <summary>Compte rendu final, prêt à afficher.</summary>
        public class Resultat {
            public int CyclesFaits;
            public int PasBouge;        // commandé mais rien n'a bougé
            public int PasArrete;       // continue après l'ordre d'arrêt
            public int Deconnexions;
            public int Erreurs;
            public bool Interrompu;
            public string Detail = "";
        }

        // Vitesse de déplacement pendant le test, en degrés/seconde.
        // Volontairement modeste : on veut charger les moteurs, pas lancer
        // la monture à l'autre bout du ciel.
        private const double Vitesse = 1.5;

        private const int DureeMouvementMs = 2000;   // durée de chaque poussée
        private const int AttenteApresStopMs = 1500; // laisser la monture s'immobiliser
        private const int FenetreControleMs = 2500;  // puis on regarde si ça bouge encore

        /// <summary>
        /// Une monture qui suit en sidéral avance d'environ 0,004°/s en
        /// ascension droite. Sur notre fenêtre de contrôle, cela reste sous
        /// 0,02°. Au-delà, l'axe bouge pour une autre raison : il n'a pas
        /// obéi à l'ordre d'arrêt.
        /// </summary>
        private const double SeuilImmobiliteDeg = 0.05;

        /// <summary>
        /// Déplacement minimal attendu quand on commande un mouvement.
        /// À 1,5°/s pendant 2 s on devrait parcourir ~3° ; on exige un
        /// dixième de cela pour conclure « ça a bougé ».
        /// </summary>
        private const double SeuilMouvementDeg = 0.3;

        /// <summary>Position actuelle des deux axes, en degrés.</summary>
        private (double ad, double dec) Position() {
            var info = telescopeMediator.GetInfo();
            return (info.RightAscension * 15.0, info.Declination);
        }

        private static double Ecart((double ad, double dec) a, (double ad, double dec) b) {
            return Math.Abs(a.ad - b.ad) + Math.Abs(a.dec - b.dec);
        }

        private bool Connectee() {
            try { return telescopeMediator.GetInfo().Connected; } catch { return false; }
        }

        /// <summary>
        /// Lance le test. <paramref name="dire"/> est appelé à chaque étape
        /// pour tenir l'utilisateur au courant.
        /// </summary>
        public async Task<Resultat> Lancer(int nbCycles, Action<string> dire, CancellationToken jeton) {
            var r = new Resultat();

            if (!Connectee()) {
                r.Detail = "La monture n'est pas connectée.";
                return r;
            }

            try {
                // ---- Phase 1 : au repos -------------------------------------
                dire("Phase 1/3 — moteurs au repos, on observe 15 secondes…");
                var avant = Position();
                for (int i = 0; i < 15 && !jeton.IsCancellationRequested; i++) {
                    await Task.Delay(1000, jeton);
                    if (!Connectee()) { r.Deconnexions++; }
                }
                var apres = Position();
                r.Detail += "Au repos : déplacement de "
                    + Ecart(avant, apres).ToString("0.000", CultureInfo.InvariantCulture) + "° en 15 s"
                    + (r.Deconnexions > 0 ? " — ET " + r.Deconnexions + " coupure(s) de liaison" : "")
                    + Environment.NewLine;

                // ---- Phase 2 : sous charge ----------------------------------
                for (int cycle = 1; cycle <= nbCycles && !jeton.IsCancellationRequested; cycle++) {
                    // Un aller puis un retour : la monture revient à son point
                    // de départ, on ne la laisse pas dériver au fil du test.
                    foreach (var sens in new[] { 1.0, -1.0 }) {
                        if (jeton.IsCancellationRequested) { break; }

                        dire("Phase 2/3 — cycle " + cycle + "/" + nbCycles
                            + (sens > 0 ? " (aller)" : " (retour)") + "…");

                        var depart = Position();

                        // On pousse les DEUX axes en même temps : c'est le pire
                        // cas pour l'alimentation, donc le plus révélateur.
                        bool commandeOk = Bouger(0, Vitesse * sens) && Bouger(1, Vitesse * sens);
                        if (!commandeOk) { r.Erreurs++; }

                        await Task.Delay(DureeMouvementMs, jeton);

                        var enMouvement = Position();
                        bool aBouge = Ecart(depart, enMouvement) >= SeuilMouvementDeg;

                        // L'ORDRE D'ARRÊT — le moment de vérité
                        bool stopOk = Bouger(0, 0) && Bouger(1, 0);
                        if (!stopOk) { r.Erreurs++; }

                        await Task.Delay(AttenteApresStopMs, jeton);
                        var justeApresStop = Position();
                        await Task.Delay(FenetreControleMs, jeton);
                        var plusTard = Position();

                        bool sestArrete = Ecart(justeApresStop, plusTard) < SeuilImmobiliteDeg;

                        if (!aBouge) { r.PasBouge++; }
                        if (!sestArrete) { r.PasArrete++; }
                        if (!Connectee()) { r.Deconnexions++; }

                        r.CyclesFaits++;
                    }
                }

                // ---- Phase 3 : on s'assure que tout est bien à l'arrêt -------
                dire("Phase 3/3 — arrêt des axes…");
                Bouger(0, 0);
                Bouger(1, 0);
            } catch (OperationCanceledException) {
                r.Interrompu = true;
            } catch (Exception ex) {
                r.Erreurs++;
                r.Detail += "Exception : " + ex.Message + Environment.NewLine;
            } finally {
                // Quoi qu'il arrive, on n'abandonne jamais un axe en mouvement.
                try { Bouger(0, 0); } catch { }
                try { Bouger(1, 0); } catch { }
            }

            return r;
        }

        /// <summary>Commande un axe ; renvoie false si la monture refuse ou lève.</summary>
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
    }
}
