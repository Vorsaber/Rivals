using System.Collections.Generic;

namespace CardShopCoop.Net.Messages
{
    /// <summary>Client -> host: the guest right-clicked a play table nobody is sitting at,
    /// asking to play the HOST (who must already be waiting there). Carries the guest's
    /// selected deck, since deck selection is per player and the host cannot see it.</summary>
    [NetworkMessage(MsgType.PvpSit, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class PvpSitMessage : INetMessage
    {
        public byte TableIndex;
        public DeckEntry Deck = new DeckEntry();
        public MsgType Type
        {
            get
            {
                return MsgType.PvpSit;
            }
        }
    }

    /// <summary>Host -> one client: the match is on. Both engines start from the same seed and
    /// each holds the other's deck as its "enemy" deck.</summary>
    [NetworkMessage(MsgType.PvpStart, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class PvpStartMessage : INetMessage
    {
        public byte TableIndex;
        public int Seed;
        public bool HostSideA;
        public DeckEntry HostDeck = new DeckEntry();
        public string HostName = "";
        public MsgType Type
        {
            get
            {
                return MsgType.PvpStart;
            }
        }
    }

    public enum PvpActionKind : byte
    {
        None = 0,
        Mulligan = 1,       // Flag = mulligan
        TurnFirst = 2,      // host -> guest only: Flag = the host goes first
        Place = 3,          // Card + A = element area
        EndTurn = 4,
        AreaSelect = 5,     // A = own area index (or -1), B = opponent area index (or -1)
        ResolveSelect = 6,  // Sel = indices into the valid-card list
        SearchSelect = 7,   // Sel / Rem = indices into the offered list, Flag = resolve success
        Quit = 8,
        Hash = 9,           // A = turn, B = state hash (from the sender's side)
    }

    /// <summary>Both directions: one thing the sending human did in the match, replayed on the
    /// receiving PC's ENEMY set in order.</summary>
    [NetworkMessage(MsgType.PvpAction, Policy = MessagePolicy.InGameOnly)]
    public sealed class PvpActionMessage : INetMessage
    {
        public int Seq;
        public PvpActionKind Kind;
        public int A;
        public int B;
        public bool Flag;
        public DeckCardEntry Card;      // Place: identity of the hand card
        public int CardMonster;         // Place: EMonsterType, the engine's own key
        public int CardBorder;
        public bool CardFoil;
        public List<int> Sel = new List<int>();
        public List<int> Rem = new List<int>();
        public MsgType Type
        {
            get
            {
                return MsgType.PvpAction;
            }
        }
    }

    /// <summary>Both directions: this side has left the table (normal exit or disconnect).</summary>
    [NetworkMessage(MsgType.PvpEnd, Policy = MessagePolicy.InGameOnly)]
    public sealed class PvpEndMessage : INetMessage
    {
        public byte TableIndex;
        public string Reason = "";
        public MsgType Type
        {
            get
            {
                return MsgType.PvpEnd;
            }
        }
    }
}
