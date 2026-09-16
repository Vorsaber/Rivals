using System.Collections.Generic;

namespace CardShopCoop.Net.Messages
{
    // ----------------------------------------------------------------- Battle (game 1.0)

    /// <summary>Host -> clients: spectator digest of the host's card battle at a play table
    /// (game 1.0 playable TCG). Visual only - the guest never plays. An inactive message
    /// (Active = false) is the tombstone: it tears the mirror down. Layout mirrors
    /// BattleSync.BuildState / ClientApplyState.</summary>
    [NetworkMessage(MsgType.BattleState, Policy = MessagePolicy.ClientOnly)]
    public sealed class BattleStateMessage : INetMessage
    {
        public bool Active;
        // who is playing: 0 = the host, else the guest's connection id (the host relays a
        // guest's BattleStateUp as this with SenderConn set; a peer in its own battle ignores it)
        public int SenderConn;
        public byte TableIndex;      // ShelfManager.m_PlayTableList index of the table in play
        public bool HostSideA;       // which side the PLAYER sits on (PlayTableGame.m_IsSideA)
        public BattleSideEntry Host = new BattleSideEntry();
        public BattleSideEntry Enemy = new BattleSideEntry();
        // gift packs the host won, still lying on the board (EItemType = host id space)
        public List<EItemType> GiftItems = new List<EItemType>();

        public MsgType Type
        {
            get
            {
                return MsgType.BattleState;
            }
        }
    }

    /// <summary>One side of the board: the numbers the UI paints plus every face-up card
    /// in a slot. Hands are not sent - the local player's hand is screen-space UI and the
    /// customer's is private.</summary>
    public sealed class BattleSideEntry
    {
        public int HP;
        public int ShieldHP;
        public int DeckCount;
        public int DiscardCount;
        public int HandCount;
        public List<CardData> Guardians = new List<CardData>();
        // one entry per element area, in slot order; an area holds a stack (evolution
        // chain) whose TOP card is what the table shows
        public List<BattleAreaEntry> Areas = new List<BattleAreaEntry>();
    }

    public sealed class BattleAreaEntry
    {
        public byte Area;            // index into PlayCardSet.m_ElementAreaPosList
        public List<CardData> Stack = new List<CardData>();
    }

    // ------------------------------------------------------- Guest-played battle (game 1.0)

    /// <summary>Client -> host: the guest right-clicked a play table to sit down for a battle.
    /// The host validates the seat exactly like InteractablePlayTable.OnRightMouseButtonUp
    /// (one customer waiting, one seat free) and answers with BattleSitResultMessage.</summary>
    [NetworkMessage(MsgType.BattleSit, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class BattleSitMessage : INetMessage
    {
        public byte TableIndex;
        public MsgType Type
        {
            get
            {
                return MsgType.BattleSit;
            }
        }
    }

    /// <summary>Host -> one client: seat granted (with the side to sit on) or refused with a
    /// vanilla ENotEnoughResourceText reason the guest shows locally.</summary>
    [NetworkMessage(MsgType.BattleSitResult, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class BattleSitResultMessage : INetMessage
    {
        public byte TableIndex;
        public bool Granted;
        public bool SideA;
        public int Reason;           // (int)ENotEnoughResourceText when !Granted, else 0
        public MsgType Type
        {
            get
            {
                return MsgType.BattleSitResult;
            }
        }
    }

    /// <summary>Client -> host: the guest's battle ended (win / loss / draw / quit). The host
    /// runs the table's own ExitPlayerCardGame so the customer stands up and leaves.</summary>
    [NetworkMessage(MsgType.BattleExit, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class BattleExitMessage : INetMessage
    {
        public byte TableIndex;
        public bool PlayerWin;
        public bool Draw;
        public MsgType Type
        {
            get
            {
                return MsgType.BattleExit;
            }
        }
    }

    /// <summary>Client -> host: the guest's own battle, same payload as BattleStateMessage.
    /// The host applies it (when not in a battle itself) and relays it to the other guests
    /// as a BattleStateMessage stamped with the sender's connection id.</summary>
    [NetworkMessage(MsgType.BattleStateUp, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class BattleStateUpMessage : INetMessage
    {
        public BattleStateMessage State = new BattleStateMessage();
        public MsgType Type
        {
            get
            {
                return MsgType.BattleStateUp;
            }
        }
    }

    // --------------------------------------------------------------- Decks (game 1.0)

    /// <summary>Host -> clients: the shop's card-game decks and which one is selected. The
    /// guest joins with the host's save (so decks match at join); this keeps them matching
    /// when the host edits a deck at the Workbench mid-session. Read-only on the guest -
    /// the deck editor is host-only (HostOnlyFeatures).</summary>
    [NetworkMessage(MsgType.DeckState, Policy = MessagePolicy.ClientOnly)]
    public sealed class DeckStateMessage : INetMessage
    {
        public int SelectedIndex;
        public List<DeckEntry> Decks = new List<DeckEntry>();
        public MsgType Type
        {
            get
            {
                return MsgType.DeckState;
            }
        }
    }

    public sealed class DeckEntry
    {
        public string Name = "";
        public int DeckBox;
        public int Playmat;
        public List<DeckCardEntry> Cards = new List<DeckCardEntry>();
    }

    public sealed class DeckCardEntry
    {
        public ECardExpansionType Expansion;
        public int Index;        // cardSaveIndex
        public int Amount;
        public int GradedIndex;
        public bool IsDestiny;
    }
    /// <summary>Client -> host: take (<c>Want</c>) or release the one deck-editor lock. Decks are
    /// shop state, so only one player edits at a time; the host answers with
    /// <see cref="DeckEditResultMessage"/>.</summary>
    [NetworkMessage(MsgType.DeckEditRequest, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class DeckEditRequestMessage : INetMessage
    {
        public bool Want;
        public MsgType Type
        {
            get
            {
                return MsgType.DeckEditRequest;
            }
        }
    }

    /// <summary>Host -> one client: the lock is theirs, or someone else (<c>Holder</c>) has it.</summary>
    [NetworkMessage(MsgType.DeckEditResult, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class DeckEditResultMessage : INetMessage
    {
        public bool Granted;
        public string Holder = "";
        public MsgType Type
        {
            get
            {
                return MsgType.DeckEditResult;
            }
        }
    }

    /// <summary>Client -> host: the lock holder's whole deck list. The host replaces its own
    /// list with it and the normal DeckState mirror carries it to everyone.</summary>
    [NetworkMessage(MsgType.DeckStateUp, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class DeckStateUpMessage : INetMessage
    {
        public bool Final;
        public List<DeckEntry> Decks = new List<DeckEntry>();
        public MsgType Type
        {
            get
            {
                return MsgType.DeckStateUp;
            }
        }
    }
}
