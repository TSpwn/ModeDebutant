using System.ComponentModel.Composition;
using System.Windows;

namespace ModeDebutant.Sequenceur {

    /// <summary>
    /// Code d'accompagnement du fichier SequenceurDockableTemplates.xaml.
    /// L'attribut [Export] signale à N.I.N.A. que ce fichier contient des
    /// gabarits d'interface à charger (même mécanique que pour Options.xaml).
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class SequenceurDockableTemplates : ResourceDictionary {

        public SequenceurDockableTemplates() {
            InitializeComponent();
        }
    }
}
