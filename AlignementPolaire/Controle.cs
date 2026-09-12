using System.Windows.Media;

namespace ModeDebutant.AlignementPolaire {

    /// <summary>Gravité d'un point de contrôle.</summary>
    public enum NiveauControle {
        /// <summary>Tout va bien.</summary>
        Ok,

        /// <summary>Ça peut marcher, mais il y a mieux à faire.</summary>
        Attention,

        /// <summary>Ça ne marchera pas : à corriger avant de continuer.</summary>
        Probleme,

        /// <summary>Impossible à vérifier ici (ex. reconnaissance du ciel en plein jour).</summary>
        NonTeste
    }

    /// <summary>
    /// Une ligne du bilan « tout est prêt ? ». Un intitulé court, un verdict,
    /// et le détail chiffré qui permet de comprendre — jamais un simple
    /// « erreur » sans explication.
    /// </summary>
    public class Controle {

        public Controle(string nom, NiveauControle niveau, string detail) {
            Nom = nom;
            Niveau = niveau;
            Detail = detail;
        }

        public string Nom { get; }
        public NiveauControle Niveau { get; }
        public string Detail { get; }

        public string Icone {
            get {
                switch (Niveau) {
                    case NiveauControle.Ok: return "✅";
                    case NiveauControle.Attention: return "⚠";
                    case NiveauControle.Probleme: return "⛔";
                    default: return "—";
                }
            }
        }

        public Brush Couleur {
            get {
                switch (Niveau) {
                    case NiveauControle.Ok: return Brosse(129, 199, 132);
                    case NiveauControle.Attention: return Brosse(255, 183, 77);
                    case NiveauControle.Probleme: return Brosse(229, 115, 115);
                    default: return Brosse(150, 150, 150);
                }
            }
        }

        private static Brush Brosse(byte r, byte v, byte b) {
            var brosse = new SolidColorBrush(Color.FromRgb(r, v, b));
            brosse.Freeze();
            return brosse;
        }
    }
}
