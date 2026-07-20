using System.ComponentModel.Composition;
using System.Windows;

namespace ModeDebutant.AlignementPolaire {

    /// <summary>
    /// Code d'accompagnement du fichier AlignementPolaireDockableTemplates.xaml.
    /// L'attribut [Export] signale à N.I.N.A. que ce fichier contient des
    /// gabarits d'interface à charger (même mécanique que pour Options.xaml).
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class AlignementPolaireDockableTemplates : ResourceDictionary {

        public AlignementPolaireDockableTemplates() {
            InitializeComponent();
        }
    }
}
