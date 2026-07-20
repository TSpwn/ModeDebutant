using System.ComponentModel.Composition;
using System.Windows;

namespace ModeDebutant {

    /// <summary>
    /// Le "code-behind" du fichier Options.xaml : la partie C# qui
    /// accompagne la description d'interface.
    ///
    /// L'attribut [Export(typeof(ResourceDictionary))] est la même
    /// mécanique de "prise électrique" que pour la classe principale :
    /// il signale à N.I.N.A. que ce fichier contient des gabarits
    /// d'interface à charger.
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class Options : ResourceDictionary {

        public Options() {
            // Charge le contenu du fichier Options.xaml associé
            InitializeComponent();
        }
    }
}
