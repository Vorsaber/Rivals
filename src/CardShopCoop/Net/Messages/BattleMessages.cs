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
        public byte TableIndex;      // ShelfManager.m_PlayTableList index of the table in play
        public bool HostSideA;       // which side the host sits on (PlayTableGame.m_IsSideA)
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
}
