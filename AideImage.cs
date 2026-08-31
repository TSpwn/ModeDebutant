using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ModeDebutant {

    /// <summary>
    /// Outillage commun aux deux panneaux pour fabriquer la vignette de la
    /// dernière photo.
    ///
    /// ⚠ POURQUOI CE FICHIER EXISTE — le piège central de N.I.N.A.
    ///
    /// Une photo d'astronomie brute est presque entièrement noire : les étoiles
    /// n'occupent qu'une minuscule partie de l'échelle de luminosité. Pour la
    /// rendre regardable, il faut l'« étirer » (redistribuer les niveaux).
    ///
    /// N.I.N.A. le fait pour son propre écran, mais ne nous donne pas le
    /// résultat. Vérifié par décompilation de `ImageControlVM.ProcessAndUpdateImage`
    /// (N.I.N.A. 3.2.0.9001) :
    ///
    ///     var etiree = await ProcessImage(brute, parameters, ct);
    ///     ImagePrepared?.Invoke(this, new ImagePreparedEventArgs {
    ///         RenderedImage = brute,          // ← la BRUTE part dans l'événement
    ///         Parameters = parameters });
    ///     RenderedImage = etiree;             // l'étirée reste pour son écran
    ///
    /// L'événement transmet donc TOUJOURS l'image avant étirement, quels que
    /// soient les réglages du profil ou le PrepareImageParameters de la capture.
    /// Aucun réglage n'y change rien, et `GetThumbnail()` non plus (il se
    /// contente de réduire `Image`, donc noir aussi). La seule issue est de
    /// refaire l'étirement soi-même.
    ///
    /// Le débayerisage, lui, a bien lieu AVANT l'événement : l'image reçue est
    /// déjà en couleur.
    /// </summary>
    internal static class AideImage {

        /// <summary>
        /// Transforme l'image reçue par `ImagePrepared` en vignette affichable :
        /// étirement identique à celui de N.I.N.A., puis réduction.
        /// Renvoie null si l'image n'est pas exploitable.
        /// </summary>
        internal static async Task<ImageSource> PreparerVignette(
                IRenderedImage rendu, IProfileService profileService, int largeurMax = 640) {

            if (rendu == null || profileService == null) { return null; }

            var reglagesImage = profileService.ActiveProfile.ImageSettings;

            // « unlinked » = étirer chaque couche de couleur séparément.
            // Même condition que N.I.N.A., pour un rendu identique au sien.
            bool separement = rendu.RawImageData != null
                           && rendu.RawImageData.Properties.IsBayered
                           && reglagesImage.DebayerImage
                           && reglagesImage.UnlinkedStretch;

            // On étire toujours, même si l'étirement automatique est décoché
            // dans les options : ces vignettes existent précisément pour voir
            // s'il y a des étoiles et si elles sont nettes. Une vignette noire
            // ne servirait à rien. L'image « vraie », non étirée, reste
            // consultable dans l'onglet image de N.I.N.A.
            var etiree = await rendu.Stretch(reglagesImage.AutoStretchFactor,
                                             reglagesImage.BlackClipping,
                                             separement);

            return await ReduireEtFiger(etiree?.Image, largeurMax);
        }

        /// <summary>
        /// Réduit une image à une largeur maximale et la « fige » (Freeze), ce
        /// que WPF exige pour afficher une image fabriquée par un autre fil
        /// d'exécution. En cas de souci, renvoie l'image d'origine figée plutôt
        /// que rien.
        ///
        /// La réduction n'est pas qu'un confort : une image de 12 mégapixels
        /// pèse une cinquantaine de mégaoctets, pour une vignette de 220 pixels
        /// de haut. La copie en `WriteableBitmap` est ce qui permet de libérer
        /// l'original — un `TransformedBitmap` figé garderait une référence
        /// dessus. C'est exactement la méthode qu'utilise N.I.N.A. lui-même.
        /// </summary>
        private static async Task<ImageSource> ReduireEtFiger(BitmapSource source, int largeurMax) {
            if (source == null) { return null; }

            // Figer d'abord : une image non figée ne peut pas voyager d'un fil
            // d'exécution à l'autre
            if (!source.IsFrozen) {
                if (!source.CanFreeze) { return null; }
                source.Freeze();
            }

            if (source.PixelWidth <= largeurMax) { return source; }

            // La réduction doit se faire sur le fil de l'affichage. On l'attend
            // SANS le bloquer (InvokeAsync et non Invoke) : on tourne ici sur le
            // fil qui enchaîne les photos, et le figer en attendant un affichage
            // occupé retarderait toute la séquence.
            var filAffichage = System.Windows.Application.Current?.Dispatcher;
            if (filAffichage == null) { return source; }

            try {
                return await filAffichage.InvokeAsync(() => {
                    try {
                        double facteur = largeurMax / (double)source.PixelWidth;
                        var reduite = new WriteableBitmap(
                            new TransformedBitmap(source, new ScaleTransform(facteur, facteur)));
                        reduite.Freeze();
                        return (ImageSource)reduite;
                    } catch {
                        // On garde l'image pleine taille : moins économe, mais visible
                        return (ImageSource)source;
                    }
                });
            } catch {
                return source;
            }
        }
    }
}
