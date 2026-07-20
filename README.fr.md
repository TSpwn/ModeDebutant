# Mode Débutant — plugin N.I.N.A.

> 🇬🇧 [English version](README.md)

**L'astrophoto sans jargon.** Plugin pour [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime
Imaging 'N' Astronomy) qui ajoute deux panneaux « débutant », 100 % en français, par-dessus les
fonctions existantes :

- 🎯 **Alignement polaire simplifié** — quelle vis tourner, dans quel sens, en grosses flèches,
  jusqu'au « ✓ Objectif atteint » (s'appuie sur le plugin TPPA).
- 📷 **Séquenceur simplifié** — tapez « M31 », pointez, choisissez 3 chiffres, appuyez sur GO.
  Suggestions de cibles pour ce soir, météo de la nuit, alertes sur votre téléphone, darks
  automatiques et bilan de nuit inclus.

Écrit par un astrophotographe amateur, avec l'assistance de Claude (Anthropic).
Licence : MPL-2.0.

---

## 📦 Installation

1. Téléchargez `ModeDebutant.dll` depuis la page **[Releases](../../releases)**.
2. Créez le dossier `%LOCALAPPDATA%\NINA\Plugins\3.0.0\ModeDebutant\` et copiez-y la DLL.
   (Collez ce chemin tel quel dans la barre de l'explorateur Windows, il s'ouvrira tout seul.)
3. Redémarrez N.I.N.A. → onglet **Imagerie** → les deux panneaux sont dans la barre d'outils en
   haut à droite (icône cible et icône appareil photo).

**Prérequis** :
- N.I.N.A. **3.2** (.NET 8), Windows.
- Pour l'alignement polaire : le plugin **Three Point Polar Alignment (TPPA)** installé
  (onglet Plugins de N.I.N.A.) et un système de plate-solving qui marche (ASTAP).
- Le séquenceur, lui, fonctionne sans TPPA.

Vous préférez compiler ? `dotnet build -c Release` (SDK .NET 8, N.I.N.A. fermé) — la DLL se
copie automatiquement au bon endroit.

---

## 🎯 Guide : Alignement polaire simplifié

Le panneau traduit les mesures de TPPA en gestes physiques. Trois écrans qui s'enchaînent :

**📍 Votre position d'observation** (en haut de l'écran d'attente)
| Élément | À quoi ça sert |
|---|---|
| Ligne position | Latitude, longitude, altitude et hémisphère lus dans le profil N.I.N.A. — **tout l'alignement en dépend**, vérifiez que c'est chez vous |
| Ville affichée | Retrouvée par Internet, pour vérifier d'un coup d'œil (rien ne s'affiche sans Internet, ce n'est jamais bloquant) |
| ✏ Modifier à la main | Latitude/longitude en degrés décimaux (virgule acceptée) + altitude en mètres. Astuce : clic droit sur votre maison dans Google Maps pour lire les valeurs |
| 🌍 Me localiser par Internet | Règle tout automatiquement (précision « à la ville près », suffisant) ; l'altitude est déduite du relief |
| Alerte rouge (0°, 0°) | Position jamais réglée = consignes fausses. Réglez-la avant tout |

**Options de démarrage**
| Option | Défaut | Explication |
|---|---|---|
| Ma monture bouge toute seule (GoTo) | ON | OFF = c'est vous qui tournez la monture à la main entre les photos |
| Démarrer sur place | OFF | ON = mesurer là où pointe le télescope ; OFF = TPPA va d'abord à son point de départ |
| Précision visée | 1,0′ | Sous ce seuil, l'alignement est déclaré terminé et s'arrête seul (1′ est très bien pour débuter) |
| 🔧 Tous les réglages | vides | Pose, gain, offset, binning, filtre, rayon de recherche, rotation, vitesse, sens Est/Ouest — vide = réglage TPPA conservé |

**Pendant l'ajustement** : un gros cadran coloré (erreur totale + échelle Parfait / Presque /
Un peu / Beaucoup / Énormément), et deux cartes de vis — une seule flèche active à la fois :
- « Vis du bas (gauche-droite) » = les molettes d'azimut de la base ;
- « Vis du haut (monter-descendre) » = la vis de latitude.

Tournez doucement, les chiffres se remettent à jour à chaque photo. Vignette + comptage
d'étoiles + netteté (HFR) pour surveiller la qualité. Garde-fous : caméra/monture connectées,
monture non parquée, chien de garde si TPPA ne répond pas.

---

## 📷 Guide : Séquenceur simplifié

L'écran de préparation suit l'ordre d'une soirée :

**🧹 Nouvelle soirée** — remet à zéro les données de la dernière session (cible, verdicts,
bilan…) en conservant vos réglages. **📊 Revoir le bilan de la dernière série** juste en dessous.

**☁️ Météo de la nuit** (bandeau d'info) — couverture nuageuse heure par heure pour chez vous :
« Ciel dégagé de 22 h à 2 h, nuageux le reste de la nuit » + frise `21h☀ 22h🌤 23h⛅ 0h☁`.
Bouton ⟳ pour actualiser. (Sans Internet, le bandeau disparaît.)

**1 · La cible** *(facultatif : sans cible, on photographie là où pointe le télescope)*
| Élément | Explication |
|---|---|
| Champ + 🔍 Chercher | Tapez « M31 », « NGC 7000 », « Andromeda »… La liste montre magnitude et visibilité (✅ à 32° de haut / 🚫 sous l'horizon) |
| 🌌 Proposer des cibles pour ce soir | Les 5 meilleures cibles du moment **pour votre position** : assez brillantes et étendues pour débuter, hautes plusieurs heures, loin de la Lune si elle est brillante. « 🌟 au mieux 62° vers 23h » |
| 🔭 Pointer le télescope | GoTo sur la cible choisie (vérifie : monture connectée, déparquée, cible au-dessus de l'horizon) |

**2 · La série de photos**
| Réglage | Défaut | Explication |
|---|---|---|
| Nombre de photos | 30 | Plus il y en a, plus l'image empilée sera propre |
| Temps de pose | 30 s | 30 s est un bon départ avec un suivi correct |
| Gain / ISO | vide | Vide = garder le réglage actuel de la caméra |
| Dithering | ON | Petit décalage entre les photos (meilleur empilement). Nécessite le guidage — **ignoré automatiquement sinon** |
| Retournement au méridien | ON | La monture se retourne toute seule si la cible passe plein sud (ignoré si monture non connectée) |
| Darks à la fin | OFF | Voir plus bas |
| 🔧 Tous les réglages | — | Offset, binning, filtre (nom exact), dithering toutes les X photos, nombre de darks |

La **durée totale estimée** s'affiche en direct (« environ 1 h 05, fin vers 23 h 40 »).

**📱 Alertes sur le téléphone** *(en option, à régler une seule fois)*
1. Installez l'application gratuite **ntfy** (Android/iPhone).
2. Dans l'app : « Ajouter un abonnement » → recopiez le nom de canal affiché dans le panneau
   (gardez-le secret, c'est votre « fréquence » personnelle).
3. Activez l'interrupteur, puis « 📨 Envoyer une notification d'essai ».

Vous recevrez : série lancée (avec accusé « 📱 alerte envoyée ✔ » dans le panneau), photo
douteuse (uniquement aux **changements**, pas de spam), retour à la normale, « couvrez le
télescope » pour les darks, bilan de fin. Série trop nuageuse, mise au point qui dérive :
vous le savez depuis le canapé.

**▶ LANCER LA SÉRIE** — le plugin fabrique une **vraie séquence dans le séquenceur avancé** de
N.I.N.A. et la lance (bouton « voir le détail » pour apprendre comment c'est construit).
Garde-fous avant départ : caméra connectée, monture non parquée, pas de séquence déjà en cours,
météo (avertissement si ≥ 70 % de nuages sont prévus avant la fin — un second clic force).

**Pendant la série** : « Photo 12 sur 30 », barre de progression, temps restant et heure de fin,
vignette de la dernière photo, « ⭐ 543 étoiles · HFR 2,1 » et le **verdict automatique** :
- ✅ Photo validée — netteté stable, étoiles au rendez-vous ;
- ⚠ Mise au point à revoir — le HFR dépasse de 30 % le meilleur de la série ;
- ⚠ Moins d'étoiles — nuages ou buée probables ;
- ❌ Aucune étoile — bouchon ? gros souci ?

Le verdict se calibre sur **votre** série (pas de seuil universel). Boutons : ■ ARRÊTER,
📊 bilan provisoire (s'ouvre dans le navigateur), voir le séquenceur avancé.

**🌡️ Les darks** (si activés) — quand les photos se terminent, le panneau demande de **couvrir
le télescope** (+ alerte téléphone), explique à quoi servent les darks, puis enchaîne une série
identique (même pose/gain/offset/binning, type DARK). 15 darks suffisent, quel que soit le
nombre de photos. « Non merci » pour sauter.

**📊 Le bilan de nuit** — écrit automatiquement à côté des photos (`Bilan 2026-07-19 2130 M 31.html`) :
récolte (photos × pose = exposition totale, darks, gain), qualité (validées / à surveiller /
sans étoiles, HFR meilleur-moyen-pire), **courbe de netteté de la nuit**, et guide d'empilement
Siril en 4 étapes.

---

## ❓ Dépannage express

| Symptôme | Piste |
|---|---|
| Les panneaux n'apparaissent pas | DLL au bon endroit ? N.I.N.A. redémarré ? Version N.I.N.A. 3.2 ? |
| Bandeau rouge « TPPA absent » | Installez Three Point Polar Alignment via l'onglet Plugins |
| « Position non réglée (0°, 0°) » | Carte 📍 → « Me localiser par Internet » ou « Modifier à la main » |
| Pas de notification d'essai | Même nom de canal des deux côtés ? Internet ? Notifications autorisées pour l'app ntfy ? |
| Pas de météo / pas de ville | Pas d'Internet — tout le reste fonctionne normalement |
| « ⚠ TPPA ne répond pas » | Regardez les bulles d'erreur en bas de N.I.N.A. (matériel déconnecté ?) |

---

# 🔧 Documentation technique (développeurs)

## Environnement cible

| Élément | Valeur |
|---|---|
| N.I.N.A. | 3.2.0.9001 (.NET 8) |
| Paquet NuGet de référence | `NINA.Plugin` **3.2.0.9001** (version identique à l'application, exigé pour la compatibilité) |
| Cible de compilation | `net8.0-windows`, WPF, AnyCPU |
| Dépendance externe requise à l'exécution | plugin **TPPA** installé dans N.I.N.A. (aucune référence à sa DLL — voir architecture) |
| Déploiement | copie automatique post-build de `ModeDebutant.dll` vers `%LOCALAPPDATA%\NINA\Plugins\3.0.0\ModeDebutant\` |

Compilation : `dotnet build -c Release` (N.I.N.A. doit être fermé, sinon la DLL cible est verrouillée).

## Structure des fichiers

```
ModeDebutant/
├── ModeDebutant.csproj              Projet SDK .NET 8 WPF + cible MSBuild "DeployToNina"
├── Properties/AssemblyInfo.cs       Manifeste du plugin (GUID 3d87e151-d363-4708-bce9-5db3356abccc,
│                                    nom "Mode Debutant", version 0.1.0.0, métadonnées N.I.N.A.)
├── ModeDebutantPlugin.cs            Point d'entrée : [Export(IPluginManifest)], hérite PluginBase
│                                    (les métadonnées sont lues automatiquement dans AssemblyInfo)
├── Options.xaml / .cs               Page d'options du plugin (onglet Plugins) — placeholder
│                                    DataTemplate "Mode Debutant_Options", [Export(ResourceDictionary)]
├── AlignementPolaire/
│   ├── AlignementPolaireDockableVM.cs           ViewModel du panneau (toute la logique)
│   ├── AlignementPolaireDockableTemplates.xaml  UI du panneau (DataTemplate
│   │                                            "ModeDebutant.AlignementPolaire.AlignementPolaireDockableVM_Dockable")
│   ├── AlignementPolaireDockableTemplates.xaml.cs  [Export(ResourceDictionary)]
│   └── MessagesTppa.cs                          Messages sortants vers TPPA (IMessage)
└── Sequenceur/
    ├── SequenceurDockableVM.cs                  ViewModel du panneau (recherche, GoTo, fabrication
    │                                            de la séquence, avancement, bilan)
    ├── SequenceurDockableTemplates.xaml         UI du panneau (DataTemplate
    │                                            "ModeDebutant.Sequenceur.SequenceurDockableVM_Dockable")
    └── SequenceurDockableTemplates.xaml.cs      [Export(ResourceDictionary)]
```

## Architecture — principe central

**Aucune référence à la DLL de TPPA.** Toute la communication passe par le *message broker* de
N.I.N.A. (`NINA.Plugin.Interfaces.IMessageBroker` / `ISubscriber` / `IMessage`), un bus de
messages public inter-plugins. C'est le choix « option A » retenu après décompilation de TPPA
(ILSpy/ilspycmd) : TPPA publie et écoute volontairement ces canaux, ce qui en fait un contrat
public stable entre versions.

### Canaux utilisés (topics, relevés dans le code décompilé de TPPA)

**En réception (on s'y abonne) :**
- `PolarAlignmentPlugin_PolarAlignment_AlignmentError` — publié par TPPA à chaque plate-solve
  réussi pendant la phase d'ajustement (toutes les quelques secondes). `Content` = objet anonyme
  `{ AzimuthError, AltitudeError, TotalError }` en **degrés** (lecture par réflexion sur les
  propriétés publiques, même technique que TPPA utilise en sens inverse).
- `PolarAlignmentPlugin_PolarAlignment_Progress` — `Content` = `ApplicationStatus`
  (statut texte : Capture, Solving, Slewing…). Sert aussi de « signe de vie ».

**En émission :**
- `PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment` — démarre TPPA à distance.
  `Content` = classe `ContenuDemarrage` dont **toutes les propriétés sont de type `object`** :
  `null` = propriété non transmise → TPPA conserve son réglage (son `TryGetValue<T>` par
  réflexion ignore les null). Propriétés supportées (noms exacts attendus par TPPA) :
  `ManualMode` (bool), `StartFromCurrentPosition` (bool), `AlignmentTolerance` (double, arcmin),
  `ExposureTime` (double, s), `Gain` (int), `Offset` (int), `Binning` (short), `Filter` (string),
  `SearchRadius` (double, °), `TargetDistance` (int, °), `MoveRate` (int), `EastDirection` (bool).
- `PolarAlignmentPlugin_DockablePolarAlignmentVM_StopAlignment` — annule la routine en cours.

### Autres services N.I.N.A. injectés (MEF, [ImportingConstructor])

- `IProfileService` — profil actif (latitude pour l'hémisphère, stockage des réglages).
- `IMessageBroker` — le bus de messages ci-dessus.
- `IImagingMediator` — événement `ImagePrepared` : chaque image capturée/traitée par N.I.N.A.
  (dont celles de TPPA) fournit un `IRenderedImage` ; son `BitmapSource` est affiché en vignette
  (`Freeze()` obligatoire : l'événement arrive sur un thread non-UI).
- `ICameraMediator` / `ITelescopeMediator` — vérifications d'avant-démarrage
  (`GetInfo().Connected`, `GetInfo().AtPark`).
- `PluginOptionsAccessor` (NINA.Profile) — persistance des réglages par profil, clé = GUID du plugin.

## Architecture — séquenceur simplifié (étape 4)

Contrairement au panneau TPPA (message broker + contrat relevé par décompilation), le séquenceur
simplifié ne repose que sur des **interfaces publiques des paquets NuGet N.I.N.A.** — aucun
contrat fragile :

- `ISequenceMediator` (NINA.Sequencer.Interfaces.Mediator) — le point d'entrée officiel :
  `SetAdvancedSequence(racine)` remplace la séquence de l'onglet « séquenceur avancé » puis
  `StartAdvancedSequence(false)` la lance (l'`await` dure toute la série : le retour de la tâche
  = fin de série, normale ou annulée). `CancelAdvancedSequence()` pour le bouton STOP,
  `IsAdvancedSequenceRunning()` en garde-fou avant démarrage, `SwitchToAdvancedView()` pour le
  lien « voir le détail ».
- Séquence fabriquée par programme, à l'identique de ce que produirait l'onglet séquenceur :
  `SequenceRootContainer` + ses trois zones **dans cet ordre imposé** (`StartAreaContainer`,
  `TargetAreaContainer`, `EndAreaContainer` = `Items[0..2]`), un `DeepSkyObjectContainer`
  (porte le nom de la cible → noms de fichiers et affichage) contenant un `SmartExposure`
  (bloc officiel = SwitchFilter + TakeExposure + LoopCondition + DitherAfterExposures).
- Réglages traduits : `TakeExposure.ExposureTime/Gain` (−1 = réglage caméra), `Binning` 1×1,
  `ImageType` LIGHT ; `LoopCondition.Iterations` = nombre de photos ;
  `DitherAfterExposures.AfterExposures` = 1 si dithering demandé **et** guidage connecté, sinon 0 ;
  `MeridianFlipTrigger` ajouté aux déclencheurs de la racine si demandé **et** monture connectée.
- Avancement : abonnement `PropertyChanged` sur `LoopCondition.CompletedIterations` (la boucle
  compte elle-même ses tours) → « Photo x sur N » + temps restant estimé
  (N × (pose + 5 s de marge)).
- Recherche de cible : `NINA.Astrometry.DatabaseInteraction` (constructeur sans argument →
  base `%LOCALAPPDATA%\NINA\NINA.sqlite`, celle de l'atlas du ciel), `GetDeepSkyObjects` avec
  `ObjectName` (correspond sur « M31 », « M 31 », noms usuels du catalogue NAME), limite 8,
  tri magnitude croissante. Visibilité : `Coordinates.Transform(latitude, longitude)` →
  hauteur topocentrique > 0.
- GoTo : `ITelescopeMediator.SlewToCoordinatesAsync` (vérifs préalables : monture connectée,
  non parquée, cible au-dessus de l'horizon).
- Les constructeurs des blocs de séquence exigent une flopée de médiateurs N.I.N.A. : tous
  importés par MEF dans le constructeur du ViewModel (`IImageHistoryVM`, `IMeridianFlipVMFactory`,
  `INighttimeCalculator`, `IFramingAssistantVM`, `IPlanetariumFactory`, etc.).

### Machine à 3 états (SequenceurDockableVM)
- **Préparation** : cible (facultative — sinon on photographie là où pointe le télescope),
  réglages de la série, durée totale estimée en direct, gros bouton GO.
- **En cours** : avancement, barre de progression, vignette, qualité de mise au point
  (⭐ étoiles + HFR, mêmes recettes que le panneau alignement), STOP.
- **Bilan** : terminée ✅ / arrêtée ⏹ / erreur ⚠, nombre de photos réellement prises, dossier
  des images, bouton « nouvelle série ».

## Logique métier (AlignementPolaireDockableVM)

### Machine à 3 états
- **Attente** : consignes de préparation, options de démarrage, gros bouton vert.
- **Mesure** : après clic Démarrer ou dès qu'un message Progress arrive (couvre le cas où
  l'utilisateur lance TPPA depuis l'interface TPPA d'origine). Vignette photo + statut TPPA.
- **Ajustement** : dès qu'un message AlignmentError arrive. Cadran + flèches.

### Traduction des erreurs en consignes (conventions extraites de TPPA)
- Conversion : degrés × 60 → minutes d'arc ; affichage 1 décimale + symbole ′.
- Hémisphère : `Northern = latitude du profil > 0` ; inverse la consigne haut/bas au sud
  (identique au code TPPA `PolarErrorDetermination.CurrentMountAxis*ErrorDirection`).
- Azimut : erreur > 0 → pousser vers la GAUCHE (les deux hémisphères) ; < 0 → DROITE.
- Altitude (hémisphère nord) : erreur > 0 → axe trop haut → DESCENDRE ; < 0 → MONTER. Inversé au sud.
- Une seule flèche active à la fois : l'axe avec la plus grande erreur absolue.
- Échelle qualitative sur l'erreur totale : Parfait < 1′ · Presque 1–3′ (vert) · Un peu 3–10′
  (orange) · Beaucoup 10–30′ · Énormément > 30′ (rouge). Si l'erreur passe sous la tolérance
  choisie par l'utilisateur : état vert « Objectif atteint », les deux cartes affichent ✓
  (TPPA s'arrête alors de lui-même — comportement natif sous `AlignmentTolerance`).

### Garde-fous
- Avant démarrage : caméra connectée ; monture connectée (si mode GoTo) ; monture non parquée
  (`AtPark`) — sinon alerte orange en français avec la marche à suivre, et rien n'est lancé.
- Chien de garde (`System.Timers.Timer`, 5 s) : si aucun message TPPA dans les 20 s suivant un
  démarrage, alerte « TPPA ne répond pas » avec pistes de diagnostic.
- Détection de TPPA absent (existence du dossier
  `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`) → bandeau rouge en phase Attente.

### Qualité d'image
Sur chaque `ImagePrepared` : réutilise `RawImageData.StarDetectionAnalysis` si déjà calculée par
N.I.N.A., sinon lance `IRenderedImage.DetectStars(...)` (détecteur officiel, sensibilité normale).
Affiche « ⭐ N étoiles · Netteté (HFR) : x,x (petit = net) » ou une alerte si zéro étoile.
Garde anti-concurrence (une analyse à la fois, les images excédentaires sont sautées) ;
tout échec du comptage est silencieux (bonus d'information, jamais bloquant).

### Réglages persistés (PluginOptionsAccessor, par profil)
Simples : `MontureGoto` (défaut true), `DemarrerSurPlace` (défaut false), `ToleranceArcmin`
(défaut 1.0, curseur 0,5–10 pas 0,5). Avancés (section repliée, champs texte, vide = réglage TPPA
conservé, virgule décimale acceptée) : pose, gain, offset, binning, filtre, rayon de recherche,
distance de rotation, vitesse, sens Est/Ouest (ComboBox 3 états).

## Particularités UI notables
- Le thème N.I.N.A. restyle les `CheckBox` en interrupteurs ON/OFF **et masque leur contenu** :
  les libellés doivent être placés à côté (DockPanel), jamais dans le `Content`.
- DataTemplates découverts par convention de nommage N.I.N.A. :
  `"<Nom du plugin>_Options"` et `"<Namespace.Classe VM>_Dockable"`, dans des
  `ResourceDictionary` exportés via `[Export(typeof(ResourceDictionary))]`.
- `RaisePropertyChanged(string.Empty)` = rafraîchir tous les bindings (utilisé aux changements de phase).
- Textes 100 % français, zéro jargon : « Vis du bas (gauche-droite) », « Vis du haut
  (monter-descendre) », flèches Unicode 64 px, carte inactive estompée (opacité 0,35).

## Historique Git

| Commit | Contenu |
|---|---|
| 4c49c1e | Squelette du plugin (étape 1) — manifeste, options placeholder, build + déploiement auto |
| 02e8b10 | Écran « Alignement polaire simplifié » (étape 3) — abonnement TPPA, cadran, flèches |
| 8e77ef0 | Options de démarrage : GoTo/manuel, sur place, précision visée (persistées, transmises à TPPA) |
| 6c51ad4 | Section repliable « Tous les réglages » (tous les paramètres acceptés par TPPA par message) |
| 4990daf | Étiquettes déplacées devant les interrupteurs ON/OFF (contournement du thème N.I.N.A.) |
| 89495dc | Vignette de la dernière photo (événement ImagePrepared) |
| 2b7fbe1 | Vérifs caméra/monture avant démarrage, statut TPPA en direct, chien de garde 20 s |
| cc85cd2 | Compteur d'étoiles sous la vignette |
| cc69979 | Netteté HFR sur la ligne des étoiles + alerte monture parquée |
| 27e6d7a | README complet |
| 65e8af5 | Séquenceur simplifié (étape 4) : cible par nom, GoTo, série de photos, suivi — version 0.2.0.0 |

L'« étape 2 » du plan (reconnaissance TPPA par décompilation) n'a pas produit de code : ses
conclusions (topics, conventions de signes, TryGetValue) sont documentées ci-dessus.

## Extensions v0.3.0 (séquenceur)
- **Position d'observation en clair** (panneau alignement) : lat/long/altitude + ville (BigDataCloud),
  saisie manuelle (`ChangeLatitude/Longitude/Elevation`), géolocalisation IP (ipapi.co) + altitude
  du relief (open-meteo).
- **Alertes téléphone** : ntfy.sh (canal aléatoire persisté, POST HTTPS, titres ASCII) — série
  lancée / changements de verdict uniquement / fin / erreur. Bouton d'essai.
- **« Que photographier ce soir ? »** : base sqlite locale (mag ≤ 9, taille ≥ 5′), altitude simulée
  sur 4 instants de la soirée, Lune (NOVAS + illumination, écart 30° si > 40 %), note
  hauteur+brillance+distance Lune, top 5 dans la liste de cibles.
- **Météo de la nuit** : open-meteo cloud_cover horaire, résumé + frise emoji, garde-fou au GO
  (≥ 70 % de nuages avant la fin estimée → avertissement, second clic force).
- **Darks fin de série** : escale « couvrez le télescope » (alerte téléphone urgente), seconde
  séquence ImageType DARK avec réglages mémorisés identiques, verdict étoiles désactivé.
- **Bilan de nuit** : rapport HTML à côté des photos (récolte, verdicts, HFR min/moy/max,
  courbe SVG, guide Siril), bouton d'ouverture dans l'écran de fin.

## État et suite prévue
- **Fait, testé de jour** : affichage des deux panneaux, options, persistance, alertes matériel
  (dont monture parquée), démarrage/annulation, alertes téléphone (essai), suggestions de cibles,
  météo.
- **À valider sur le ciel (alignement)** : cycle complet mesure → ajustement (flèches, échelle,
  HFR, arrêt auto).
- **À valider sur le ciel (séquenceur)** : recherche + GoTo réels, série complète GO → darks →
  bilan, avancement, dithering avec guidage, retournement au méridien, alertes en conditions
  réelles.
- **Idées pour la suite** : centrage automatique sur la cible (plate-solving au GO), flats,
  refroidissement caméra avant la série et réchauffage après, aide à la mise au point manuelle
  (HFR en direct).

## Limites connues / risques assumés
- Si TPPA change les noms de ses topics ou de ses propriétés de message dans une future version,
  le panneau ne recevra plus rien (le chien de garde le signalera). Contrat considéré stable.
- Les textes de statut relayés de TPPA (Capture, Solving…) restent en anglais.
- Le comptage d'étoiles s'exécute sur toute image préparée pendant les phases actives, y compris
  une éventuelle capture manuelle simultanée (cas jugé marginal).
- `async void` utilisé pour les handlers de commandes/événements (pattern WPF classique),
  exceptions avalées volontairement dans le comptage d'étoiles.
- Le bouton GO **remplace** la séquence chargée dans l'onglet séquenceur avancé (comportement
  assumé de `SetAdvancedSequence` ; une séquence en cours d'exécution bloque le démarrage, mais
  une séquence simplement chargée est écrasée sans confirmation).
- Le pointage est un GoTo simple, sans centrage par plate-solving : la cible peut être légèrement
  décalée dans le champ (acceptable pour débuter ; le centrage automatique est une piste future).
- L'estimation du temps restant utilise une marge fixe de 5 s/photo (téléchargement, dithering) :
  purement indicative.
- Si la validation N.I.N.A. de la séquence trouve des problèmes au démarrage, ses messages
  apparaissent dans l'interface N.I.N.A. en anglais (dialogues natifs).
