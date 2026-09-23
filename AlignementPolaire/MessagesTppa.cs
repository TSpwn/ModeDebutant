using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;

namespace ModeDebutant.AlignementPolaire {

    /// <summary>
    /// N.I.N.A. possède une "radio interne" entre plugins (le message broker) :
    /// chacun peut émettre des messages sur un canal nommé (un "Topic"),
    /// et écouter les canaux qui l'intéressent.
    ///
    /// Cette classe est le modèle commun de nos messages sortants : elle remplit
    /// les champs administratifs obligatoires (qui envoie, quand, etc.).
    /// Seul le nom du canal (Topic) change d'un message à l'autre.
    /// </summary>
    public abstract class MessageVersTppa : IMessage {

        // Identité de l'expéditeur : notre plugin (le GUID de AssemblyInfo.cs)
        public Guid SenderId => Guid.Parse("3d87e151-d363-4708-bce9-5db3356abccc");
        public string Sender => "Mode Debutant";

        // Horodatage et numéro unique du message
        public DateTimeOffset SentAt => DateTimeOffset.UtcNow;
        public Guid MessageId => Guid.NewGuid();

        // Champs prévus par N.I.N.A. mais dont nous n'avons pas besoin
        public DateTimeOffset? Expiration => null;
        public Guid? CorrelationId => null;
        public int Version => 1;
        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();

        // Le nom du canal : défini par chaque message concret ci-dessous
        public abstract string Topic { get; }

        // Contenu du message : vide par défaut ("utilise tes réglages actuels"),
        // mais un message peut le remplir pour transmettre des options.
        public virtual object Content => null;
    }

    /// <summary>
    /// Le "bon de commande" transmis à TPPA au démarrage.
    ///
    /// Chaque propriété porte le nom EXACT que TPPA cherche dans le message
    /// (relevé dans son code). Toutes sont de type "object" pour une raison
    /// précise : une valeur à null = "réglage non fourni", et TPPA garde
    /// alors son réglage habituel. C'est exactement ainsi que TPPA lit le
    /// message (il ignore proprement les propriétés absentes ou nulles).
    /// </summary>
    public class ContenuDemarrage {
        public object ManualMode { get; set; }                // bool  : monture sans GoTo
        public object StartFromCurrentPosition { get; set; }  // bool  : mesurer sur place
        public object AlignmentTolerance { get; set; }        // double: précision visée (′)
        public object ExposureTime { get; set; }              // double: temps de pose (s)
        public object Gain { get; set; }                      // int   : gain caméra
        public object Offset { get; set; }                    // int   : offset caméra
        public object Binning { get; set; }                   // short : binning (1, 2...)
        public object Filter { get; set; }                    // string: nom du filtre
        public object SearchRadius { get; set; }              // double: rayon de recherche (°)
        public object TargetDistance { get; set; }            // int   : rotation entre photos (°)
        public object MoveRate { get; set; }                  // int   : vitesse de déplacement
        public object EastDirection { get; set; }             // bool  : true = vers l'Est

        // bool : false = NE PAS couper le suivi à la fin de l'alignement.
        // TPPA l'active par défaut (« Stop tracking when done: True » dans
        // son journal), ce qui laissait la monture arrêtée après chaque
        // alignement : étoiles filées, plate solve impossible, centrage
        // impossible. On ne sait pas si toutes les versions de TPPA lisent
        // cette option dans le message — d'où le filet de sécurité
        // ReforcerSuiviSideral côté panneau.
        public object StopTrackingWhenDone { get; set; }
    }

    /// <summary>Demande à TPPA de démarrer sa routine avec nos réglages.</summary>
    public class MessageDemarrerAlignement : MessageVersTppa {
        private readonly ContenuDemarrage contenu;

        public MessageDemarrerAlignement(ContenuDemarrage contenu) {
            this.contenu = contenu;
        }

        public override string Topic => "PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment";

        public override object Content => contenu;
    }

    /// <summary>Demande à TPPA d'arrêter la routine en cours.</summary>
    public class MessageArreterAlignement : MessageVersTppa {
        public override string Topic => "PolarAlignmentPlugin_DockablePolarAlignmentVM_StopAlignment";
    }
}
