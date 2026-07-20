using NINA.Plugin;
using NINA.Plugin.Interfaces;
using System.ComponentModel.Composition;

namespace ModeDebutant {

    /// <summary>
    /// La classe principale du plugin.
    ///
    /// Comment N.I.N.A. trouve-t-il notre plugin ?
    /// - Au démarrage, N.I.N.A. scanne le dossier Plugins et cherche dans
    ///   chaque DLL une classe marquée [Export(typeof(IPluginManifest))].
    ///   C'est un système de "prise électrique" standard (appelé MEF) :
    ///   nous exposons la prise, N.I.N.A. s'y branche.
    /// - La classe de base "PluginBase" (fournie par N.I.N.A.) lit toute
    ///   seule la carte d'identité écrite dans Properties\AssemblyInfo.cs
    ///   (nom, version, description...). Nous n'avons donc rien à recopier ici.
    ///
    /// Pour l'instant la classe est vide : c'est le squelette de l'étape 1.
    /// La logique TPPA (étape 3) et le séquenceur simplifié (étape 4)
    /// viendront s'ajouter dans des fichiers séparés.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class ModeDebutantPlugin : PluginBase {
    }
}
