using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// The Rivals lobby (competitive mode, phase 1). A SECOND, thin connection between shops:
    /// one PC hosts the lobby (any of them), every shop connects to it - a shop being a solo
    /// player or the HOST of a co-op session, whose guests ride along as its team. Nothing of
    /// the world is synced here; each shop keeps simulating on its own save. What travels:
    ///  - each shop's published state (level, money, day, average markup, sales, tournament)
    ///  - the board the server derives from it (price rank, crowd multiplier, leaderboard)
    ///  - lobby chat
    /// The price rank feeds the population: the cheapest shop draws more customers, the
    /// priciest fewer (<see cref="CrowdMultiplier"/>, read by PopulationTuning).
    ///
    /// Runs on its own LAN transport instance, independent of the co-op session's, so a team
    /// host is a co-op HOST and a lobby CLIENT at once. Ticked from its own MonoBehaviour.
    /// </summary>
    public sealed class RivalsLobby : MonoBehaviour
    {
        public static RivalsLobby Instance
        {
            get; private set;
        }

        public enum LobbyRole
        {
            None, Server, Client
        }

        public static LobbyRole Role
        {
            get; private set;
        }
        public static string Status = "not connected";
        public static string LobbyName = "";
        public static RivalsBoardMessage Board = new RivalsBoardMessage();
        public static readonly List<Social.Line> Chat = new List<Social.Line>();
        public static float BoardAt = -100f;

        /// <summary>This shop's population factor from the price rank (1 = neutral). Read by
        /// PopulationTuning on the host / solo PC.</summary>
        public static float CrowdMultiplier = 1f;
        public static int MyPriceRank = -1;

        private ICoopTransport _net;
        private bool _viaSteam;
        public static bool ViaSteam => Instance != null && Instance._viaSteam;
        private readonly Dictionary<int, RivalsShop> _shops = new Dictionary<int, RivalsShop>(); // server: conn -> shop
        private readonly HashSet<int> _welcomed = new HashSet<int>();
        private int _myId = -1;
        private float _publishTimer;
        private float _boardTimer;

        private void Awake()
        {
            Instance = this;
        }

        // ================================================================ connect / host

        /// <summary>Steam: host a friends-only league lobby; invite from the overlay.</summary>
        public static void HostLobbySteam()
        {
            var me = Instance;
            var steam = CoopCore.Instance != null ? CoopCore.Instance.Steam : null;
            if (me == null || steam == null)
            {
                Status = "no Steam on this install - use LAN";
                return;
            }
            me.Leave();
            try
            {
                LobbyName = CoopPlugin.RivalsLobbyName != null && !string.IsNullOrWhiteSpace(CoopPlugin.RivalsLobbyName.Value)
                    ? CoopPlugin.RivalsLobbyName.Value.Trim() : (MyShopName() + "'s league");
                me._net = steam.CreateRivalsTransport(true, new RivalsPingMessage());
                me._viaSteam = true;
                steam.OnRivalsLobbyLive = id =>
                {
                    Role = LobbyRole.Server;
                    Status = $"hosting league '{LobbyName}' on Steam - invite friends from the overlay";
                    me._shops.Clear();
                    me._welcomed.Clear();
                    me._myId = 0;
                    CoopPlugin.Log.LogInfo("Rivals: " + Status + " (lobby " + id + ")");
                };
                Role = LobbyRole.Server; // provisional until the lobby is live
                Status = "creating the Steam league lobby...";
                steam.HostRivals(LobbyName);
            }
            catch (Exception e)
            {
                Status = "could not host on Steam: " + e.Message;
                CoopPlugin.Log.LogWarning("Rivals: " + Status);
                me.Leave();
            }
        }

        /// <summary>Steam: join a league lobby (from an accepted overlay invite).</summary>
        public static void JoinSteam(ulong lobbyId)
        {
            var me = Instance;
            var steam = CoopCore.Instance != null ? CoopCore.Instance.Steam : null;
            if (me == null || steam == null)
                return;
            if (CoopCore.Role == CoopRole.Client)
            {
                Status = "you are a co-op guest - your host's shop represents the team";
                return;
            }
            me.Leave();
            try
            {
                me._net = steam.CreateRivalsTransport(false, new RivalsPingMessage());
                me._viaSteam = true;
                Role = LobbyRole.Client;
                Status = "joining the Steam league lobby...";
                steam.OnRivalsConnectedToHost = () =>
                {
                    Status = "connected via Steam - waiting for welcome";
                    me._net?.Send(1, new RivalsHelloMessage { Name = MyShopName(), Version = CoopPlugin.Version });
                };
                steam.JoinRivals(lobbyId);
            }
            catch (Exception e)
            {
                Status = "could not join on Steam: " + e.Message;
                CoopPlugin.Log.LogWarning("Rivals: " + Status);
                me.Leave();
            }
        }

        public static void OpenSteamInvite()
        {
            var steam = CoopCore.Instance != null ? CoopCore.Instance.Steam : null;
            steam?.OpenRivalsInviteDialog();
        }

        public static void HostLobby()
        {
            var me = Instance;
            if (me == null)
                return;
            me.Leave();
            try
            {
                int port = CoopPlugin.RivalsPort != null ? CoopPlugin.RivalsPort.Value : 27887;
                var tcp = new Transport { KeepaliveMessage = new RivalsPingMessage() };
                tcp.StartHost(port);
                me._net = tcp;
                me._viaSteam = false;
                Role = LobbyRole.Server;
                LobbyName = CoopPlugin.RivalsLobbyName != null && !string.IsNullOrWhiteSpace(CoopPlugin.RivalsLobbyName.Value)
                    ? CoopPlugin.RivalsLobbyName.Value.Trim() : (MyShopName() + "'s league");
                Status = $"hosting lobby '{LobbyName}' on port {port}";
                me._shops.Clear();
                me._welcomed.Clear();
                // the server is a shop too: id 0
                me._myId = 0;
                CoopPlugin.Log.LogInfo("Rivals: " + Status);
            }
            catch (Exception e)
            {
                Status = "could not host: " + e.Message;
                CoopPlugin.Log.LogWarning("Rivals: " + Status);
                me.Leave();
            }
        }

        public static void JoinLobby(string address)
        {
            var me = Instance;
            if (me == null)
                return;
            me.Leave();
            try
            {
                string host = (address ?? "").Trim();
                int port = CoopPlugin.RivalsPort != null ? CoopPlugin.RivalsPort.Value : 27887;
                int colon = host.LastIndexOf(':');
                if (colon > 0 && int.TryParse(host.Substring(colon + 1), out int p))
                {
                    port = p;
                    host = host.Substring(0, colon);
                }
                if (string.IsNullOrEmpty(host))
                {
                    Status = "enter the lobby address";
                    return;
                }
                var tcp = new Transport { KeepaliveMessage = new RivalsPingMessage() };
                tcp.StartClient(host, port);
                me._net = tcp;
                me._viaSteam = false;
                Role = LobbyRole.Client;
                Status = $"connecting to {host}:{port}...";
                me._net.Send(1, new RivalsHelloMessage { Name = MyShopName(), Version = CoopPlugin.Version });
                if (CoopPlugin.RivalsLastAddress != null)
                    CoopPlugin.RivalsLastAddress.Value = address.Trim();
                CoopPlugin.Log.LogInfo("Rivals: " + Status);
            }
            catch (Exception e)
            {
                Status = "could not connect: " + e.Message;
                CoopPlugin.Log.LogWarning("Rivals: " + Status);
                me.Leave();
            }
        }

        public static void LeaveLobby()
        {
            Instance?.Leave();
            Status = "not connected";
        }

        private void Leave()
        {
            try
            {
                _net?.Stop();
            }
            catch { }
            if (_viaSteam)
            {
                try
                {
                    CoopCore.Instance?.Steam?.LeaveRivals();
                }
                catch { }
            }
            _net = null;
            _viaSteam = false;
            Role = LobbyRole.None;
            _shops.Clear();
            _welcomed.Clear();
            _myId = -1;
            Board = new RivalsBoardMessage();
            CrowdMultiplier = 1f;
            MyPriceRank = -1;
            Util.Companions.Economy.SetExternalPickiness(1f);
            PopulationTuning.Reapply();
        }

        private void OnDestroy()
        {
            Leave();
        }

        // ================================================================ tick

        private void Update()
        {
            if (_net == null || Role == LobbyRole.None)
                return;
            try
            {
                _net.PumpMainThread(); // Steam does its I/O here; TCP ignores it
                while (_net.Connects.TryDequeue(out int c))
                {
                    if (Role == LobbyRole.Server)
                        CoopPlugin.Log.LogInfo($"Rivals: connection {c} arrived");
                    else
                        Status = "connected - waiting for welcome";
                }
                while (_net.Disconnects.TryDequeue(out int d))
                {
                    if (Role == LobbyRole.Server)
                    {
                        if (_shops.TryGetValue(d, out var s))
                            PushChat("lobby", s.Name + " left the league");
                        _shops.Remove(d);
                        _welcomed.Remove(d);
                    }
                    else
                    {
                        Status = "lobby closed";
                        Leave();
                        return;
                    }
                }
                int budget = 200;
                while (budget-- > 0 && _net.Incoming.TryDequeue(out var msg))
                    Dispatch(msg);
                float dt = Time.unscaledDeltaTime;
                _publishTimer += dt;
                if (_publishTimer >= 5f)
                {
                    _publishTimer = 0f;
                    PublishMyShop();
                }
                if (Role == LobbyRole.Server)
                {
                    _boardTimer += dt;
                    if (_boardTimer >= 5f)
                    {
                        _boardTimer = 0f;
                        BuildAndSendBoard();
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("Rivals tick: " + e.Message); }
        }

        private void Dispatch(InMsg msg)
        {
            switch (msg.Message)
            {
                case RivalsHelloMessage hello:
                    if (Role != LobbyRole.Server)
                        return;
                    if (hello.Version != CoopPlugin.Version)
                    {
                        _net.Send(msg.ConnId, new RivalsWelcomeMessage { Ok = false, Reason = $"version mismatch - lobby runs {CoopPlugin.Version}, you have {hello.Version}" });
                        return;
                    }
                    _welcomed.Add(msg.ConnId);
                    _shops[msg.ConnId] = new RivalsShop { Id = msg.ConnId, Name = string.IsNullOrEmpty(hello.Name) ? "shop " + msg.ConnId : hello.Name };
                    _net.Send(msg.ConnId, new RivalsWelcomeMessage { Ok = true, YourId = msg.ConnId, LobbyName = LobbyName });
                    PushChat("lobby", _shops[msg.ConnId].Name + " joined the league");
                    _net.Broadcast(new RivalsChatMessage { From = "lobby", Text = _shops[msg.ConnId].Name + " joined the league" });
                    break;
                case RivalsWelcomeMessage welcome:
                    if (Role != LobbyRole.Client)
                        return;
                    if (!welcome.Ok)
                    {
                        Status = "refused: " + welcome.Reason;
                        CoopPlugin.Log.LogWarning("Rivals: " + Status);
                        Leave();
                        return;
                    }
                    _myId = welcome.YourId;
                    LobbyName = welcome.LobbyName ?? "";
                    Status = $"in lobby '{LobbyName}'";
                    _publishTimer = 10f; // publish now
                    break;
                case RivalsShopStateMessage state:
                    if (Role != LobbyRole.Server || !_welcomed.Contains(msg.ConnId) || state.Shop == null)
                        return;
                    state.Shop.Id = msg.ConnId;
                    _shops[msg.ConnId] = state.Shop;
                    break;
                case RivalsBoardMessage board:
                    if (Role != LobbyRole.Client)
                        return;
                    ApplyBoard(board);
                    break;
                case RivalsChatMessage chat:
                    if (Role == LobbyRole.Server)
                    {
                        if (_shops.TryGetValue(msg.ConnId, out var from))
                            chat.From = from.Name;
                        foreach (int cid in _net.ConnIds())
                            if (cid != msg.ConnId)
                                _net.Send(cid, chat);
                    }
                    PushChat(chat.From, chat.Text);
                    break;
                case RivalsPingMessage _:
                    break;
            }
        }

        // ================================================================ my shop

        private static string MyShopName()
        {
            string n = CoopPlugin.RivalsShopName != null ? CoopPlugin.RivalsShopName.Value : "";
            if (!string.IsNullOrWhiteSpace(n))
                return n.Trim();
            try
            {
                string shop = CPlayerData.PlayerName;
                if (!string.IsNullOrWhiteSpace(shop))
                    return shop;
            }
            catch { }
            return CoopCore.Instance != null ? CoopCore.Instance.EffectivePlayerName : Environment.UserName;
        }

        /// <summary>Only a shop OWNER publishes: solo, or the host of a co-op session (its
        /// guests are the team). A co-op guest in the lobby would be a second voice for the
        /// same shop.</summary>
        private void PublishMyShop()
        {
            if (CoopCore.Role == CoopRole.Client)
                return;
            var gm = CSingleton<CGameManager>.Instance;
            if (gm == null || !gm.m_IsGameLevel)
                return;
            var shop = BuildMyShop();
            if (Role == LobbyRole.Server)
                _shops[0] = shop;
            else
                _net.Send(1, new RivalsShopStateMessage { Shop = shop });
        }

        private RivalsShop BuildMyShop()
        {
            var s = new RivalsShop { Id = _myId, Name = MyShopName() };
            try
            {
                s.Members.Add(CoopCore.Instance != null ? CoopCore.Instance.EffectivePlayerName : s.Name);
                if (CoopCore.Instance != null && CoopCore.Role == CoopRole.Host)
                    foreach (var kv in CoopCore.Instance.PeerNames)
                        s.Members.Add(kv.Value);
                s.Level = CPlayerData.m_ShopLevel;
                s.Money = CPlayerData.m_CoinAmountDouble;
                s.Day = CPlayerData.m_CurrentDay;
                s.SalesToday = CPlayerData.m_GameReportDataCollect.checkoutCount;
                s.CustomersToday = CPlayerData.m_GameReportDataCollect.customerVisited;
                var td = CPlayerData.m_TournamentData;
                s.TournamentScheduled = td != null && td.m_IsHostingTournament;
                s.TournamentToday = td != null && td.m_IsTournamentDay && !td.m_IsTournamentDayOver;
                s.AvgMarkup = PriceIndex.AverageMarkup(out s.PricedItems);
                s.ShopValue = s.Money;
                s.CoopPort = CoopPlugin.Port != null ? CoopPlugin.Port.Value : 0;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("Rivals BuildMyShop: " + e.Message); }
            return s;
        }

        // ================================================================ server: the board

        private void BuildAndSendBoard()
        {
            var board = new RivalsBoardMessage
            {
                LobbyName = LobbyName,
                PriceEffect = CoopPlugin.RivalsPriceEffect != null ? Mathf.Clamp01(CoopPlugin.RivalsPriceEffect.Value) : 0.3f,
            };
            foreach (var kv in _shops)
                if (kv.Value != null)
                    board.Shops.Add(kv.Value);
            // price rank: only shops that actually priced something compete; the rest sit neutral
            var priced = board.Shops.FindAll(x => x.PricedItems > 0);
            priced.Sort((a, b) => a.AvgMarkup.CompareTo(b.AvgMarkup));
            int n = priced.Count;
            for (int i = 0; i < n; i++)
            {
                priced[i].PriceRank = i;
                // cheapest -> 1 + effect, priciest -> 1 - effect, linear between; one shop = neutral
                float t = n > 1 ? (float)i / (n - 1) : 0.5f;
                priced[i].CrowdMultiplier = 1f + board.PriceEffect * (1f - 2f * t);
            }
            foreach (var x in board.Shops)
                if (x.PricedItems <= 0)
                {
                    x.PriceRank = -1;
                    x.CrowdMultiplier = 1f;
                }
            _net.Broadcast(board);
            ApplyBoard(board);
        }

        private void ApplyBoard(RivalsBoardMessage board)
        {
            Board = board;
            BoardAt = Time.unscaledTime;
            var mine = board.Shops.Find(x => x.Id == _myId);
            if (mine != null)
            {
                MyPriceRank = mine.PriceRank;
                float before = CrowdMultiplier;
                CrowdMultiplier = mine.CrowdMultiplier;
                if (!Mathf.Approximately(before, CrowdMultiplier))
                {
                    CoopPlugin.Log.LogInfo($"Rivals: price rank {mine.PriceRank + 1} of {board.Shops.Count} (markup x{mine.AvgMarkup:0.00}) -> customers x{CrowdMultiplier:0.00}");
                    PopulationTuning.Reapply();
                    // the second lever: customers at the priciest shop are also pickier about markups
                    Util.Companions.Economy.SetExternalPickiness(CrowdMultiplier > 0f ? 1f / CrowdMultiplier : 1f);
                }
            }
            // a team host passes the board down to its guests over co-op
            if (CoopCore.Role == CoopRole.Host)
                CoopCore.Instance?.RelayRivalsBoard(board);
        }

        /// <summary>A co-op GUEST gets the board from its host (no lobby connection of its own).</summary>
        public static void ApplyRelayedBoard(RivalsBoardMessage board)
        {
            if (Role != LobbyRole.None)
                return;
            Board = board;
            BoardAt = Time.unscaledTime;
            Status = board.Shops.Count > 0 ? $"in lobby '{board.LobbyName}' via your host" : Status;
        }

        // ================================================================ chat

        public static void SendChat(string text)
        {
            var me = Instance;
            if (me == null || me._net == null || Role == LobbyRole.None || string.IsNullOrWhiteSpace(text))
                return;
            text = text.Trim();
            if (text.Length > 200)
                text = text.Substring(0, 200);
            string from = MyShopName();
            var msg = new RivalsChatMessage { From = from, Text = text };
            if (Role == LobbyRole.Server)
                me._net.Broadcast(msg);
            else
                me._net.Send(1, msg);
            PushChat(from, text);
        }

        private static void PushChat(string from, string text)
        {
            Chat.Add(new Social.Line { From = from ?? "", Text = text ?? "", At = Time.unscaledTime });
            while (Chat.Count > 60)
                Chat.RemoveAt(0);
        }
    }

    /// <summary>The shop's average markup over everything it has actually priced: set item
    /// prices over market, and set Tetramon card prices over market. 1.0 = at market.</summary>
    internal static class PriceIndex
    {
        public static float AverageMarkup(out int priced)
        {
            priced = 0;
            double sum = 0;
            try
            {
                var set = CPlayerData.m_SetItemPriceList;
                for (int i = 0; set != null && i < set.Count; i++)
                {
                    float p = set[i];
                    if (p <= 0f)
                        continue;
                    float m = CPlayerData.GetItemMarketPrice((EItemType)i);
                    if (m <= 0f)
                        continue;
                    sum += Mathf.Clamp(p / m, 0.1f, 10f);
                    priced++;
                }
                var cards = CPlayerData.m_CardPriceSetList;
                for (int i = 0; cards != null && i < cards.Count; i++)
                {
                    float p = cards[i];
                    if (p <= 0f)
                        continue;
                    float m = CPlayerData.GetCardMarketPrice(i, ECardExpansionType.Tetramon, false, 0);
                    if (m <= 0f)
                        continue;
                    sum += Mathf.Clamp(p / m, 0.1f, 10f);
                    priced++;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("Rivals PriceIndex: " + e.Message); }
            return priced > 0 ? (float)(sum / priced) : 1f;
        }
    }
}
