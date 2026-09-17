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
        public static int MyId => Instance != null ? Instance._myId : -1;
        /// <summary>League client taking the league's market: block the local daily roll.</summary>
        public static bool MarketFromLeague
        {
            get; private set;
        }
        /// <summary>League server: the market changed (day roll); send it on the next tick.</summary>
        public static bool MarketDirty;
        private float _marketTimer;

        private ICoopTransport _net;
        private bool _viaSteam;
        public static bool ViaSteam => Instance != null && Instance._viaSteam;
        private readonly Dictionary<int, RivalsShop> _shops = new Dictionary<int, RivalsShop>(); // server: conn -> shop
        private readonly HashSet<int> _welcomed = new HashSet<int>();
        private int _myId = -1;
        private float _publishTimer;
        private float _boardTimer;

        // ---- league: teams, ready-up, START (see LeagueSession for the save side)
        public static string LeagueId = "";
        public static int LeagueTeams = 2, LeaguePerTeam = 1;
        /// <summary>Everyone in the lobby and where they stand (server-built, sent to all).</summary>
        public static readonly List<LeagueMember> Roster = new List<LeagueMember>();
        public static LeagueMember Me => Roster.Find(m => m.Id == MyId);
        private readonly Dictionary<int, LeagueMember> _members = new Dictionary<int, LeagueMember>(); // server: conn -> member
        private int _myTeam;
        private bool _myReady;
        private string _sentState = "";
        private float _stateTimer;
        private int _joinCaptain = -1;   // teammate: the captain whose shop to join once it opens
        private float _lastJoinTry = -100f;

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
                    me.InitLeagueAsServer();
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
                me.InitLeagueAsServer();
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
            _members.Clear();
            Roster.Clear();
            _myReady = false;
            _sentState = "";
            _joinCaptain = -1;
            Board = new RivalsBoardMessage();
            CrowdMultiplier = 1f;
            MyPriceRank = -1;
            MarketFromLeague = false;
            MarketDirty = false;
            Util.Companions.Economy.SetExternalPickiness(1f);
            if (_tuningApplied)
            {
                _tuningApplied = false;
                Util.Companions.Difficulty.ClearOverride();
                if (CoopCore.Role != CoopRole.Client)
                    Util.Companions.Economy.ClearOverride();
            }
            PopulationTuning.Reapply();
        }

        private void OnDestroy()
        {
            Leave();
        }

        // ================================================================ tick

        private void Update()
        {
            LeagueSession.Tick(); // a live league game keeps going whether or not the lobby is up
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
                        if (_members.Remove(d))
                            SendSetup();
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
                VisitorBag.Tick();
                _stateTimer += dt;
                if (_stateTimer >= 1f)
                {
                    _stateTimer = 0f;
                    PumpLeagueState();
                    TryJoinTeam();
                }
                if (Role == LobbyRole.Server)
                {
                    _boardTimer += dt;
                    if (_boardTimer >= 5f)
                    {
                        _boardTimer = 0f;
                        BuildAndSendBoard();
                    }
                    _marketTimer += dt;
                    bool shared = CoopPlugin.RivalsSharedMarket == null || CoopPlugin.RivalsSharedMarket.Value;
                    if (shared && (MarketDirty || _marketTimer >= 30f) && _net.ConnectionCount > 0)
                    {
                        var gm2 = CSingleton<CGameManager>.Instance;
                        if (gm2 != null && gm2.m_IsGameLevel && CPlayerData.m_ItemPricePercentChangeList != null && CPlayerData.m_ItemPricePercentChangeList.Count > 0)
                        {
                            _marketTimer = 0f;
                            MarketDirty = false;
                            try
                            {
                                _net.Broadcast(MarketSync.BuildLeagueState());
                            }
                            catch (Exception e) { CoopPlugin.Log.LogWarning("Rivals market send: " + e.Message); }
                        }
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
                    _members[msg.ConnId] = new LeagueMember { Id = msg.ConnId, Name = _shops[msg.ConnId].Name };
                    SendSetup();
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
                case RivalsLeagueMessage league:
                    OnLeagueMessage(msg.ConnId, league);
                    break;
                case MarketStateMessage market:
                    if (Role != LobbyRole.Client)
                        return;
                    MarketFromLeague = true;
                    CoopCore.Instance?.ApplyLeagueMarket(market);
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
                // visitable = hosting a co-op session right now (LAN address or Steam lobby)
                if (CoopCore.Instance != null && CoopCore.Role == CoopRole.Host)
                {
                    s.Visitable = true;
                    if (CoopCore.Instance.IsSteamSession && CoopCore.Instance.Steam != null)
                        s.SteamLobby = CoopCore.Instance.SteamLobbyIdForRivals;
                    s.LanAddress = CoopCore.Instance.LanAddressForRivals;
                    s.CoopPassword = CoopCore.Instance.HostPassword ?? "";
                }
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
                SharedMarket = CoopPlugin.RivalsSharedMarket == null || CoopPlugin.RivalsSharedMarket.Value,
                SharedTuning = CoopPlugin.RivalsSharedTuning == null || CoopPlugin.RivalsSharedTuning.Value,
            };
            if (board.SharedTuning)
            {
                if (Util.Companions.Difficulty.Present)
                    Util.Companions.Difficulty.LocalSettings(out board.DifficultyProfile, out board.PerPlayerScale, out board.StaffCostPerPlayer);
                board.EconomyPresent = Util.Companions.Economy.Present;
                Util.Companions.Economy.LocalFactors(out board.EconMargin, out board.EconCard, out board.EconPick, out board.EconCost, out board.EconBill);
            }
            foreach (var kv in _shops)
                if (kv.Value != null)
                    board.Shops.Add(kv.Value);
            // price rank: only shops that actually priced something compete; the rest sit neutral
            var priced = board.Shops.FindAll(x => x.PricedItems > 0);
            priced.Sort((a, b) => a.AvgMarkup.CompareTo(b.AvgMarkup));
            int n = priced.Count;
            // Scale by markup DISTANCE from the league average, not by rank position: two shops
            // at x1.10 and x1.11 are a dead heat and stay neutral (2026-09-16: rank flipped
            // 1<->2 every minute on that wobble, swinging the crowd x0.70 <-> x1.30). Full
            // effect needs a real gap: +-1 at half the league's spread, spread under 5% = nothing.
            float lo = n > 0 ? priced[0].AvgMarkup : 1f, hi = n > 0 ? priced[n - 1].AvgMarkup : 1f;
            float mid = (lo + hi) * 0.5f, half = (hi - lo) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                priced[i].PriceRank = i;
                float t = half >= 0.025f ? Mathf.Clamp((mid - priced[i].AvgMarkup) / half, -1f, 1f) : 0f;
                float m = 1f + board.PriceEffect * t;
                priced[i].CrowdMultiplier = Mathf.Round(m * 20f) / 20f; // 5% steps: no jitter
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

        private bool _tuningApplied;

        private void ApplyBoard(RivalsBoardMessage board)
        {
            Board = board;
            BoardAt = Time.unscaledTime;
            // the league host's tuning applies on every member (not on the host itself: it IS the source)
            if (Role == LobbyRole.Client && board.SharedTuning && CoopCore.Role != CoopRole.Client)
            {
                if (board.DifficultyProfile >= 0)
                    Util.Companions.Difficulty.SetOverride(board.DifficultyProfile, board.PerPlayerScale, board.StaffCostPerPlayer);
                if (board.EconomyPresent)
                    Util.Companions.Economy.SetOverride(board.EconMargin, board.EconCard, board.EconPick, board.EconCost, board.EconBill);
                _tuningApplied = true;
            }
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

        // ================================================================ visits

        /// <summary>Open the shop for visitors: host a LAN co-op session if not in one.</summary>
        public static void OpenShopForVisitors()
        {
            var core = CoopCore.Instance;
            if (core == null || CoopCore.Role != CoopRole.None)
                return;
            core.StartHosting();
            Status = CoopCore.Role == CoopRole.Host ? "shop open to visitors (LAN)" : Status;
        }

        /// <summary>Leave home and drop in on a rival: their world loads into the scratch
        /// slot, our save stays untouched, the bag records what we bring back. Must be done
        /// from the title screen (a co-op join always is); the bag opens on the way out.</summary>
        public static void Visit(RivalsShop shop)
        {
            var core = CoopCore.Instance;
            if (core == null || shop == null)
                return;
            if (CoopCore.Role != CoopRole.None)
            {
                Status = "leave your current session first";
                return;
            }
            if (!shop.Visitable)
            {
                Status = shop.Name + " is not open to visitors (they need to host their shop)";
                return;
            }
            var gm = CSingleton<CGameManager>.Instance;
            if (gm != null && gm.m_IsGameLevel)
            {
                // the bag must know home before the world changes; the join itself needs the title screen
                VisitorBag.Open(shop.Name);
                Status = "bag packed - go to the TITLE SCREEN (save first), then press Visit again";
                return;
            }
            if (!VisitorBag.IsOpen)
                VisitorBag.Open(shop.Name);
            CoopCore.JoiningAsVisitor = true;
            if (!JoinShop(shop, "visiting " + shop.Name))
                CoopCore.JoiningAsVisitor = false;
        }

        /// <summary>Join a shop's co-op session the way it published itself (Steam lobby or LAN
        /// address, with its password). Visits and teammates share this.</summary>
        private static bool JoinShop(RivalsShop shop, string what)
        {
            var core = CoopCore.Instance;
            if (core == null || shop == null)
                return false;
            string pw = shop.CoopPassword ?? "";
            if (shop.SteamLobby != 0 && core.Steam != null)
            {
                Status = what + " via Steam...";
                core.JoinSteam(shop.SteamLobby, pw);
                return true;
            }
            if (!string.IsNullOrEmpty(shop.LanAddress))
            {
                Status = what + " at " + shop.LanAddress + "...";
                core.Join(shop.LanAddress, shop.CoopPort > 0 ? shop.CoopPort : (CoopPlugin.Port != null ? CoopPlugin.Port.Value : 27886), pw);
                return true;
            }
            Status = shop.Name + " published no address";
            return false;
        }

        // ================================================================ league

        public static string MyShopNameForLeague()
        {
            return MyShopName();
        }

        private void InitLeagueAsServer()
        {
            LeagueId = CoopPlugin.RivalsLeagueId != null ? (CoopPlugin.RivalsLeagueId.Value ?? "").Trim() : "";
            if (string.IsNullOrEmpty(LeagueId))
                MintLeagueId();
            LeagueTeams = CoopPlugin.RivalsTeams != null ? CoopPlugin.RivalsTeams.Value : 2;
            LeaguePerTeam = CoopPlugin.RivalsPerTeam != null ? CoopPlugin.RivalsPerTeam.Value : 1;
            _members.Clear();
            _members[0] = new LeagueMember { Id = 0, Name = MyShopName(), Team = 1 };
            _myTeam = 1;
            _myReady = false;
            SendSetup();
        }

        private static void MintLeagueId()
        {
            LeagueId = Guid.NewGuid().ToString("N").Substring(0, 8);
            if (CoopPlugin.RivalsLeagueId != null)
                CoopPlugin.RivalsLeagueId.Value = LeagueId;
        }

        /// <summary>Server: start a brand-new league - new id, so every member's save state
        /// resets; the old league's saves stay archived on each PC.</summary>
        public static void HostNewLeague()
        {
            var me = Instance;
            if (me == null || Role != LobbyRole.Server)
                return;
            MintLeagueId();
            foreach (var m in me._members.Values)
            {
                m.Ready = false;
                m.HasSave = false;
            }
            me._myReady = false;
            me.SendSetup();
            PushChat("lobby", "new league " + LeagueId + " - everyone starts fresh");
            me._net.Broadcast(new RivalsChatMessage { From = "lobby", Text = "new league " + LeagueId + " - everyone starts fresh" });
        }

        public static void HostSetTeams(int teams)
        {
            var me = Instance;
            if (me == null || Role != LobbyRole.Server)
                return;
            LeagueTeams = Mathf.Clamp(teams, 1, 8);
            if (CoopPlugin.RivalsTeams != null)
                CoopPlugin.RivalsTeams.Value = LeagueTeams;
            me.SendSetup();
        }

        public static void HostSetPerTeam(int perTeam)
        {
            var me = Instance;
            if (me == null || Role != LobbyRole.Server)
                return;
            LeaguePerTeam = Mathf.Clamp(perTeam, 1, 4);
            if (CoopPlugin.RivalsPerTeam != null)
                CoopPlugin.RivalsPerTeam.Value = LeaguePerTeam;
            me.SendSetup();
        }

        /// <summary>Server: put a member on a team (the host can arrange everyone).</summary>
        public static void HostAssignTeam(int memberId, int team)
        {
            var me = Instance;
            if (me == null || Role != LobbyRole.Server)
                return;
            if (memberId == 0)
            {
                SetMyTeam(team);
                return;
            }
            if (me._members.TryGetValue(memberId, out var m))
            {
                m.Team = Mathf.Clamp(team, 0, LeagueTeams);
                me.SendSetup();
            }
        }

        public static void SetMyTeam(int team)
        {
            var me = Instance;
            if (me == null || Role == LobbyRole.None)
                return;
            me._myTeam = Mathf.Clamp(team, 0, Mathf.Max(1, LeagueTeams));
            me._stateTimer = 10f; // push now
        }

        public static void SetReady(bool ready)
        {
            var me = Instance;
            if (me == null || Role == LobbyRole.None)
                return;
            me._myReady = ready;
            me._stateTimer = 10f;
        }

        public static bool MyReady => Instance != null && Instance._myReady;
        public static int MyTeam => Instance != null ? Instance._myTeam : 0;

        private static bool AtTitle()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && !gm.m_IsGameLevel;
        }

        /// <summary>Once a second: my team / ready / at-title / has-save, sent when it changes.
        /// Ready only holds at the title screen.</summary>
        private void PumpLeagueState()
        {
            bool atTitle = AtTitle();
            if (!atTitle && _myReady)
                _myReady = false;
            bool hasSave = LeagueSession.HasSave(LeagueId);
            string key = $"{LeagueId}|{_myTeam}|{_myReady}|{atTitle}|{hasSave}";
            if (key == _sentState)
                return;
            _sentState = key;
            if (Role == LobbyRole.Server)
            {
                if (_members.TryGetValue(0, out var m))
                {
                    m.Team = _myTeam;
                    m.Ready = _myReady;
                    m.AtTitle = atTitle;
                    m.HasSave = hasSave;
                    m.Name = MyShopName();
                }
                SendSetup();
            }
            else if (!string.IsNullOrEmpty(LeagueId))
            {
                _net.Send(1, new RivalsLeagueMessage { Op = "state", LeagueId = LeagueId, Team = _myTeam, Ready = _myReady, AtTitle = atTitle, HasSave = hasSave });
            }
        }

        /// <summary>Server: captains are the members who hold the team's save (lowest id
        /// wins a tie), else the lowest id on the team; then the roster goes to everyone.</summary>
        private void SendSetup()
        {
            if (Role != LobbyRole.Server || _net == null)
                return;
            var list = new List<LeagueMember>(_members.Values);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var m in list)
                m.Captain = false;
            for (int t = 1; t <= LeagueTeams; t++)
            {
                LeagueMember cap = null;
                foreach (var m in list)
                    if (m.Team == t && (cap == null || (m.HasSave && !cap.HasSave)))
                        cap = m;
                if (cap != null)
                    cap.Captain = true;
            }
            var msg = new RivalsLeagueMessage { Op = "setup", LeagueId = LeagueId, Name = LobbyName, Teams = LeagueTeams, PerTeam = LeaguePerTeam, Members = list };
            Roster.Clear();
            Roster.AddRange(list);
            _net.Broadcast(msg);
        }

        private void OnLeagueMessage(int conn, RivalsLeagueMessage m)
        {
            switch (m.Op)
            {
                case "state":
                    if (Role != LobbyRole.Server || !_members.TryGetValue(conn, out var mem))
                        return;
                    mem.Team = Mathf.Clamp(m.Team, 0, LeagueTeams);
                    mem.Ready = m.Ready;
                    mem.AtTitle = m.AtTitle;
                    mem.HasSave = m.LeagueId == LeagueId && m.HasSave;
                    SendSetup();
                    break;
                case "setup":
                    if (Role != LobbyRole.Client)
                        return;
                    bool newId = m.LeagueId != LeagueId;
                    LeagueId = m.LeagueId ?? "";
                    LeagueTeams = Mathf.Max(1, m.Teams);
                    LeaguePerTeam = Mathf.Max(1, m.PerTeam);
                    Roster.Clear();
                    if (m.Members != null)
                        Roster.AddRange(m.Members);
                    if (_myTeam > LeagueTeams)
                        _myTeam = 0;
                    if (newId)
                        _stateTimer = 10f; // has-save is per league id: resend
                    break;
                case "start":
                    if (Role != LobbyRole.Client)
                        return;
                    BeginLeague(m);
                    break;
            }
        }

        /// <summary>Why START is not possible right now ("" = go).</summary>
        public static string CannotStart()
        {
            var me = Instance;
            if (me == null || Role != LobbyRole.Server)
                return "not hosting";
            if (Roster.Count == 0)
                return "nobody in the lobby";
            var perTeam = new int[LeagueTeams + 1];
            foreach (var m in Roster)
            {
                if (m.Team < 1)
                    return m.Name + " has no team";
                perTeam[m.Team]++;
                if (perTeam[m.Team] > LeaguePerTeam)
                    return "team " + m.Team + " has more than " + LeaguePerTeam;
                if (!m.AtTitle)
                    return m.Name + " is not at the title screen";
                if (!m.Ready)
                    return m.Name + " is not ready";
            }
            return "";
        }

        /// <summary>Server: everyone readied up at the title - go. Captains load (or create)
        /// the league save, teammates join their captain's shop as it opens.</summary>
        public static void HostStart()
        {
            var me = Instance;
            if (me == null || Role != LobbyRole.Server)
                return;
            string why = CannotStart();
            if (why.Length > 0)
            {
                Status = "can't start: " + why;
                return;
            }
            me.SendSetup(); // captains final
            var msg = new RivalsLeagueMessage { Op = "start", LeagueId = LeagueId, Name = LobbyName, Teams = LeagueTeams, PerTeam = LeaguePerTeam, Members = new List<LeagueMember>(Roster) };
            me._net.Broadcast(msg);
            PushChat("lobby", "league " + LeagueId + " STARTED");
            me._net.Broadcast(new RivalsChatMessage { From = "lobby", Text = "league " + LeagueId + " STARTED" });
            me.BeginLeague(msg);
        }

        private void BeginLeague(RivalsLeagueMessage m)
        {
            Roster.Clear();
            if (m.Members != null)
                Roster.AddRange(m.Members);
            var me = Roster.Find(x => x.Id == _myId);
            if (me == null)
            {
                Status = "START came but you are not on the roster";
                return;
            }
            _myReady = false;
            _sentState = "";
            if (!LeagueSession.Begin(m.LeagueId, m.Name, me.Captain, _viaSteam))
            {
                Status = "league start: " + LeagueSession.Status;
                return;
            }
            if (me.Captain)
            {
                _joinCaptain = -1;
                Status = LeagueSession.Status;
            }
            else
            {
                var cap = Roster.Find(x => x.Team == me.Team && x.Captain);
                _joinCaptain = cap != null ? cap.Id : -1;
                _lastJoinTry = -100f;
                Status = cap != null ? "waiting for " + cap.Name + "'s shop to open..." : "your team has no captain";
            }
        }

        /// <summary>Teammate: the captain's shop shows up on the board as visitable - join it
        /// as a regular co-op guest (the team shares the shop).</summary>
        private void TryJoinTeam()
        {
            if (_joinCaptain < 0)
                return;
            if (CoopCore.Role != CoopRole.None)
            {
                if (CoopCore.Role == CoopRole.Client)
                {
                    _joinCaptain = -1;
                    Status = "joined your team's shop";
                }
                return;
            }
            if (!AtTitle() || Time.unscaledTime - _lastJoinTry < 8f)
                return;
            var shop = Board.Shops.Find(s => s.Id == _joinCaptain);
            if (shop == null || !shop.Visitable)
                return;
            _lastJoinTry = Time.unscaledTime;
            CoopCore.JoiningAsVisitor = false;
            JoinShop(shop, "joining your team at " + shop.Name);
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

    /// <summary>The shop's markup over everything it has actually priced - set item prices and
    /// set card prices (every expansion) over their market price - WEIGHTED BY MARKET VALUE, so
    /// a $200 card counts for a hundred $2 packs. 1.0 = at market.</summary>
    internal static class PriceIndex
    {
        // built per call: the game REASSIGNS these lists on save load, so captured references go stale
        private static (List<float> list, ECardExpansionType exp, bool destiny)[] CardLists() => new[]
        {
            (CPlayerData.m_CardPriceSetList, ECardExpansionType.Tetramon, false),
            (CPlayerData.m_CardPriceSetListDestiny, ECardExpansionType.Destiny, false),
            (CPlayerData.m_CardPriceSetListGhost, ECardExpansionType.Ghost, true),
            (CPlayerData.m_CardPriceSetListGhostBlack, ECardExpansionType.Ghost, false),
            (CPlayerData.m_CardPriceSetListMegabot, ECardExpansionType.Megabot, false),
            (CPlayerData.m_CardPriceSetListFantasyRPG, ECardExpansionType.FantasyRPG, false),
            (CPlayerData.m_CardPriceSetListCatJob, ECardExpansionType.CatJob, false),
            (CPlayerData.m_CardPriceSetListAscension, ECardExpansionType.Ascension, false),
        };

        public static float AverageMarkup(out int priced)
        {
            priced = 0;
            double weighted = 0, weight = 0;
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
                    weighted += Mathf.Clamp(p / m, 0.1f, 10f) * m;
                    weight += m;
                    priced++;
                }
                foreach (var (list, exp, destiny) in CardLists())
                {
                    for (int i = 0; list != null && i < list.Count; i++)
                    {
                        float p = list[i];
                        if (p <= 0f)
                            continue;
                        float m;
                        try
                        {
                            m = CPlayerData.GetCardMarketPrice(i, exp, destiny, 0);
                        }
                        catch { continue; }
                        if (m <= 0f)
                            continue;
                        weighted += Mathf.Clamp(p / m, 0.1f, 10f) * m;
                        weight += m;
                        priced++;
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("Rivals PriceIndex: " + e.Message); }
            return weight > 0 ? (float)(weighted / weight) : 1f;
        }
    }
}
