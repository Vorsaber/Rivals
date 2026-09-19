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
        public double Ante;              // the ante the sitter accepts (0 = a free match)
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
        public double Ante;              // each side's stake; the guest's comes out of its bag now
        // --- fv-680 guest-vs-guest pvp begin
        public bool Relay;               // the opponent is ANOTHER GUEST; the host forwards actions and runs no engine
        public bool SideA;               // relay: this guest is side A (decides who goes first, drives the rematch)
        // --- fv-680 guest-vs-guest pvp end
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
        Rematch = 10,       // A = 1 "I want a rematch"; host -> guest A = 2 "go" (B = 1: the ante is on again)
        Desync = 11,        // either side: the engines disagreed twice in a row - both abort the match
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
        // --- fv-680 guest-vs-guest pvp begin
        public bool HasResult;           // the sender's engine reported a winner before it left
        public bool PlayerWin;           // ...from the SENDER's side
        public bool Draw;
        // --- fv-680 guest-vs-guest pvp end
        public MsgType Type
        {
            get
            {
                return MsgType.PvpEnd;
            }
        }
    }

    /// <summary>Host -> the sitter: this table plays for money. A visitor accepts by sending
    /// PvpSit again carrying the ante; anything else and the host keeps waiting.</summary>
    [NetworkMessage(MsgType.PvpOffer, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class PvpOfferMessage : INetMessage
    {
        public byte TableIndex;
        public double Ante;
        public MsgType Type
        {
            get
            {
                return MsgType.PvpOffer;
            }
        }
    }

    /// <summary>Host -> the visitor: money for the carry-out bag - the pot, or the ante back.</summary>
    [NetworkMessage(MsgType.PvpSettle, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class PvpSettleMessage : INetMessage
    {
        public double Amount;
        public string Reason = "";
        public MsgType Type
        {
            get
            {
                return MsgType.PvpSettle;
            }
        }
    }

    /// <summary>Client -> host: a cheat-menu button pressed on a guest. The host runs the
    /// same action it would for its own press and answers with <see cref="CheatResultMessage"/>.</summary>
    [NetworkMessage(MsgType.CheatRequest, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class CheatRequestMessage : INetMessage
    {
        public int Op;
        public int A;
        public int B;
        public MsgType Type
        {
            get
            {
                return MsgType.CheatRequest;
            }
        }
    }

    [NetworkMessage(MsgType.CheatResult, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class CheatResultMessage : INetMessage
    {
        public string Text = "";
        public int SelectDeck = -1;     // a deck the host just made FOR this guest: select it once mirrored
        // --- fv-875 guest-cheats-toggle begin
        public int GuestCheats = -1;    // the host's Cheats > AllowGuestRequests: 1 on, 0 off, -1 not carried (older host)
        // --- fv-875 guest-cheats-toggle end
        public MsgType Type
        {
            get
            {
                return MsgType.CheatResult;
            }
        }
    }
    // --- fv-680 guest-vs-guest pvp begin
    /// <summary>Host -> the sitter: nobody was waiting at that table, so the sitter now waits
    /// there (Waiting) for the host or another guest to right-click it - or stopped waiting.</summary>
    [NetworkMessage(MsgType.PvpWait, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class PvpWaitMessage : INetMessage
    {
        public byte TableIndex;
        public bool Waiting;
        public string Text = "";
        public MsgType Type
        {
            get
            {
                return MsgType.PvpWait;
            }
        }
    }
    // --- fv-680 guest-vs-guest pvp end

    /// <summary>Client -> host: the guest is (or is no longer) ready to end the day.</summary>
    [NetworkMessage(MsgType.SleepVote, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class SleepVoteMessage : INetMessage
    {
        public bool Ready;
        public MsgType Type
        {
            get
            {
                return MsgType.SleepVote;
            }
        }
    }

    /// <summary>Host -> clients: one notice line about the end-of-day wait.</summary>
    [NetworkMessage(MsgType.SleepStatus, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class SleepStatusMessage : INetMessage
    {
        public string Text = "";
        public MsgType Type
        {
            get
            {
                return MsgType.SleepStatus;
            }
        }
    }
}

