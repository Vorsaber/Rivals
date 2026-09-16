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
        public string LanAddress = "";         // for visits (phase 2)
        public int CoopPort;
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
