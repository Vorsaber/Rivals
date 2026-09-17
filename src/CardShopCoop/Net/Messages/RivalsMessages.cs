using System.Collections.Generic;

namespace CardShopCoop.Net.Messages
{
    /// <summary>Rivals lobby: a second, thin connection between SHOPS (each shop = one co-op
    /// session or a solo player), separate from the co-op world sync. The lobby server is any
    /// one of them. Messages ride the same LAN transport and codec as co-op.</summary>
    [NetworkMessage(MsgType.RivalsHello, Policy = MessagePolicy.Any)]
    public sealed class RivalsHelloMessage : INetMessage
    {
        public string Name = "";
        public string Version = "";
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsHello;
            }
        }
    }

    [NetworkMessage(MsgType.RivalsWelcome, Policy = MessagePolicy.Any)]
    public sealed class RivalsWelcomeMessage : INetMessage
    {
        public bool Ok;
        public string Reason = "";
        public int YourId;
        public string LobbyName = "";
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsWelcome;
            }
        }
    }

    /// <summary>One shop's published state. Client -> server every few seconds; the server
    /// folds them into the board.</summary>
    public sealed class RivalsShop
    {
        public int Id;
        public string Name = "";               // the shop (host) name
        public List<string> Members = new List<string>();  // team: host + guests
        public int Level;
        public double Money;
        public int Day;
        public float Hour;
        public float AvgMarkup = 1f;           // set price / market price, averaged over priced stock
        public int PricedItems;
        public int SalesToday;
        public int CustomersToday;
        public bool TournamentScheduled;
        public bool TournamentToday;
        public string LanAddress = "";         // for visits: the shop's co-op session
        public int CoopPort;
        public ulong SteamLobby;               // when the shop hosts its co-op session on Steam
        public bool Visitable;                 // hosting a co-op session right now
        public string CoopPassword = "";       // the session's password (a league is a friend group)
        // filled by the server
        public int PriceRank;                  // 0 = cheapest
        public float CrowdMultiplier = 1f;     // what this shop's population sim should apply
        public double ShopValue;               // money + stock value (phase 1: money)
    }

    [NetworkMessage(MsgType.RivalsShopState, Policy = MessagePolicy.Any)]
    public sealed class RivalsShopStateMessage : INetMessage
    {
        public RivalsShop Shop = new RivalsShop();
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsShopState;
            }
        }
    }

    /// <summary>Server -> everyone (and a team host -> its guests over co-op): the whole board.</summary>
    [NetworkMessage(MsgType.RivalsBoard, Policy = MessagePolicy.Any)]
    public sealed class RivalsBoardMessage : INetMessage
    {
        public string LobbyName = "";
        public float PriceEffect;
        public List<RivalsShop> Shops = new List<RivalsShop>();
        // the league host's settings, applied by every shop (0 = not carried)
        public bool SharedMarket;
        public bool SharedTuning;
        public int DifficultyProfile = -1;
        public float PerPlayerScale;
        public float StaffCostPerPlayer;
        public bool EconomyPresent;
        public float EconMargin = 1f, EconCard = 1f, EconPick = 1f, EconCost = 1f, EconBill = 1f;
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsBoard;
            }
        }
    }

    [NetworkMessage(MsgType.RivalsChat, Policy = MessagePolicy.Any)]
    public sealed class RivalsChatMessage : INetMessage
    {
        public string From = "";
        public string Text = "";
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsChat;
            }
        }
    }

    /// <summary>One lobby member's place in the league.</summary>
    public sealed class LeagueMember
    {
        public int Id;                 // lobby connection id (server = 0)
        public string Name = "";
        public int Team;               // 1-based; 0 = not picked
        public bool Ready;
        public bool AtTitle;           // ready-up only counts from the title screen
        public bool HasSave;           // holds a save for the current league id
        public bool Captain;           // owns the team's save; teammates join their shop
    }

    /// <summary>League setup and start. Op "setup": server -> members (id, teams, roster).
    /// Op "state": member -> server (my team, ready, at-title, has-save). Op "start": server ->
    /// members: begin the league session now (captains load, teammates join).</summary>
    [NetworkMessage(MsgType.RivalsLeague, Policy = MessagePolicy.Any)]
    public sealed class RivalsLeagueMessage : INetMessage
    {
        public string Op = "";
        public string LeagueId = "";
        public string Name = "";
        public int Teams;
        public int PerTeam;
        public List<LeagueMember> Members = new List<LeagueMember>();
        public bool Started;           // the host has started this league (members may return to their shop)
        // "state"
        public int Team;
        public bool Ready;
        public bool AtTitle;
        public bool HasSave;
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsLeague;
            }
        }
    }

    /// <summary>A co-op guest back from visiting a rival hands its carry-out bag to the shop it
    /// plays in (its team's shop): the host applies money, cards and items there.</summary>
    [NetworkMessage(MsgType.BagDeposit, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class BagDepositMessage : INetMessage
    {
        public string From = "";
        public string VisitedShop = "";
        public double Net;
        public List<int> ItemTypes = new List<int>();
        public List<int> ItemCounts = new List<int>();
        public List<int> CardExpansions = new List<int>();
        public List<int> CardIndices = new List<int>();
        public List<bool> CardDestiny = new List<bool>();
        public List<int> CardAmounts = new List<int>();
        public MsgType Type
        {
            get
            {
                return MsgType.BagDeposit;
            }
        }
    }

    /// <summary>The team's ONE carry-out bag, kept by the lobby server so two teammates out
    /// visiting draw on the same balance and bring home one bag. Member -> server: "open",
    /// "spend", "earn", "item", "card", "back". Server -> team: "state" (the mirror),
    /// "deliver" (to the captain, when the last teammate is home).</summary>
    [NetworkMessage(MsgType.RivalsBag, Policy = MessagePolicy.Any)]
    public sealed class RivalsBagMessage : INetMessage
    {
        public string Op = "";
        public CardShopCoop.Sync.Rivals.VisitorBag.State State;
        public bool Applied;    // "back": the member already applied this bag offline - drop the server's copy
        public double Amount;
        public string What = "";
        public int ItemType;
        public int Count;
        public int Expansion;
        public int Index;
        public bool IsDestiny;
        public float Paid;
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsBag;
            }
        }
    }

    /// <summary>A co-op guest about to leave on a visit asks the shop for travel money; the
    /// host takes it from the till and answers with the amount (clamped to what was there).</summary>
    [NetworkMessage(MsgType.BagWithdraw, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class BagWithdrawMessage : INetMessage
    {
        public double Amount;
        public MsgType Type
        {
            get
            {
                return MsgType.BagWithdraw;
            }
        }
    }

    [NetworkMessage(MsgType.BagWithdrawResult, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class BagWithdrawResultMessage : INetMessage
    {
        public double Amount;
        public MsgType Type
        {
            get
            {
                return MsgType.BagWithdrawResult;
            }
        }
    }

    /// <summary>A visitor took a card off a display: buy it. The host clears the slot, takes
    /// the card's set price into the till and echoes the empty slot to everyone.</summary>
    [NetworkMessage(MsgType.VisitorCardBuy, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class VisitorCardBuyMessage : INetMessage
    {
        public int Key;
        public bool Prize;   // off the tournament prize shelf, by the entrant: no charge
        public MsgType Type
        {
            get
            {
                return MsgType.VisitorCardBuy;
            }
        }
    }

    /// <summary>One line of a trade offer: a card by its save identity.</summary>
    public sealed class TradeCard
    {
        public int Exp;
        public int Index;
        public bool Destiny;
        public int Amount;
    }

    /// <summary>One side of the trade window.</summary>
    public sealed class TradeOffer
    {
        public double Money;
        public List<TradeCard> Cards = new List<TradeCard>();
        public bool Confirmed;
    }

    /// <summary>The trade window between a VISITOR and the SHOP. Visitor -> host: "open",
    /// "offer" (GuestMoney/GuestCards), "confirm" (GuestConfirmed), "cancel". Host -> visitor:
    /// "open"/"state" (both offers and confirmations), "done" (executed: move exactly this in
    /// the bag), "cancel".</summary>
    [NetworkMessage(MsgType.Trade, Policy = MessagePolicy.InGameOnly)]
    public sealed class TradeMessage : INetMessage
    {
        public string Op = "";
        public double HostMoney;
        public List<TradeCard> HostCards = new List<TradeCard>();
        public bool HostConfirmed;
        public double GuestMoney;
        public List<TradeCard> GuestCards = new List<TradeCard>();
        public bool GuestConfirmed;
        public MsgType Type
        {
            get
            {
                return MsgType.Trade;
            }
        }
    }

    /// <summary>One shop's end-of-day numbers (the vanilla report's collector, plus standing).</summary>
    public sealed class RivalsDayReport
    {
        public int ShopId;
        public string Name = "";
        public int Day;
        public double Customers, Checkouts, Dissatisfied, ItemsSold, CardsSold, Played;
        public double Revenue, Costs, Profit, Exp;
        public double Money;
        public int Level;
        public float Markup;
    }

    [NetworkMessage(MsgType.RivalsDayReport, Policy = MessagePolicy.Any)]
    public sealed class RivalsDayReportMessage : INetMessage
    {
        public RivalsDayReport Report;
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsDayReport;
            }
        }
    }

    [NetworkMessage(MsgType.RivalsDayBoard, Policy = MessagePolicy.Any)]
    public sealed class RivalsDayBoardMessage : INetMessage
    {
        public List<RivalsDayReport> Reports = new List<RivalsDayReport>();
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsDayBoard;
            }
        }
    }

    [NetworkMessage(MsgType.RivalsPing, Policy = MessagePolicy.Any)]
    public sealed class RivalsPingMessage : INetMessage
    {
        public MsgType Type
        {
            get
            {
                return MsgType.RivalsPing;
            }
        }
    }
}
