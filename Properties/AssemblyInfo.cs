using System.Reflection;
using System.Runtime.InteropServices;

// ============================================================
// Carte d'identité du plugin.
// C'est CE fichier que N.I.N.A. lit pour afficher le plugin
// dans sa liste (onglet Plugins).
// ============================================================

// [OBLIGATOIRE] Identifiant unique du plugin (généré une fois, ne change jamais)
[assembly: Guid("3d87e151-d363-4708-bce9-5db3356abccc")]

// [OBLIGATOIRE] Version du plugin (à incrémenter à chaque nouvelle version)
[assembly: AssemblyVersion("0.3.0.0")]
[assembly: AssemblyFileVersion("0.3.0.0")]

// [OBLIGATOIRE] Nom affiché dans la liste des plugins de N.I.N.A.
[assembly: AssemblyTitle("Mode Debutant")]

// [OBLIGATOIRE] Description courte affichée sous le nom
[assembly: AssemblyDescription("L'astrophoto sans jargon : alignement polaire guidé (TPPA) et séquenceur en 3 réglages, avec de gros boutons et des consignes en français.")]

// Auteur du plugin
[assembly: AssemblyCompany("Tom")]
[assembly: AssemblyProduct("Mode Debutant")]
[assembly: AssemblyCopyright("Copyright © 2026 Tom")]

// Version minimale de N.I.N.A. compatible avec ce plugin
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]

// Licence du code du plugin
[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]

// Mots-clés de recherche
[assembly: AssemblyMetadata("Tags", "beginner,debutant,polar alignment,sequencer,simplifie")]

// Description longue affichée sur la page du plugin
[assembly: AssemblyMetadata("LongDescription", @"Deux panneaux « débutant » dans l'onglet imagerie, tout en français, sans jargon :

1. ALIGNEMENT POLAIRE SIMPLIFIÉ (nécessite le plugin TPPA)
Lance la mesure en un clic, puis traduit les chiffres en consignes physiques : quelle vis tourner (bas ou haut), dans quel sens (grosses flèches), et une échelle simple (Parfait / Presque / Un peu / Beaucoup / Énormément) avec code couleur. S'arrête tout seul quand la précision visée est atteinte.

2. SÉQUENCEUR SIMPLIFIÉ
- Tapez le nom d'une cible (M31, NGC 7000…) : le plugin la trouve, dit si elle est visible, et pointe le télescope dessus.
- Trois réglages : nombre de photos, temps de pose, gain — et un gros bouton GO.
- Le plugin fabrique alors une vraie séquence dans le séquenceur avancé de N.I.N.A. et la lance : dithering et retournement au méridien gérés automatiquement (ignorés si le matériel nécessaire n'est pas connecté).
- Pendant la série : avancement (photo x sur N), temps restant, dernière photo à l'écran et verdict qualité automatique (✅ photo validée / ⚠ mise au point à revoir / ⚠ nuages ou buée probables).
- « Que photographier ce soir ? » : cinq cibles idéales calculées pour chez vous (hauteur, Lune, brillance).
- Météo de la nuit intégrée (couverture nuageuse heure par heure) avec garde-fou avant de lancer une longue série.
- Alertes sur votre téléphone (application gratuite ntfy) : série lancée, photo douteuse, série terminée.
- Darks automatiques en fin de série : le plugin vous demande de couvrir le télescope, puis enchaîne tout seul.
- Bilan de nuit : un rapport HTML enregistré à côté des photos (qualité, courbe de netteté, guide Siril).
- Une section « Tous les réglages » (offset, binning, filtre, fréquence du dithering, nombre de darks) pour aller plus loin, au besoin.

Les photos sont enregistrées comme d'habitude dans le dossier d'images de N.I.N.A., prêtes à empiler (Siril, DeepSkyStacker…).")]

// Champs techniques standard (inutilisés mais attendus)
[assembly: ComVisible(false)]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
