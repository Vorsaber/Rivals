using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The small social layer: text chat, a "come here" ping that names where you are, and a
    /// per-player status line ("in a battle", "editing decks") with session counters, all on
    /// the F2 window. One relay path: a guest sends to the host, the host stamps the name and
    /// forwards to everyone else; the host's own go straight out. The host's status also
    /// carries the difficulty / economy readout so guests can see what the shop is running.
    /// </summary>
    public sealed class Social : TickableCoopModule
    {
        public Action<INetMessage> SendToHost;
        public Action<INetMessage> Broadcast;
        public Action<int, INetMessage> RelayFrom;   // host: (senderConn, msg) -> everyone but the sender
        public Func<int, string> PeerName;

        public const int MaxLines = 40;

        public sealed class Line
        {
            public string From;
            public string Text;
            public bool IsPing;
            public float At;
        }

        public sealed class PlayerStatus
        {
            public string Name = "";
            public string Status = "";
            public int Packs, Battles, Sales;
            public float UpdatedAt;
        }

        private static Social s_instance;
        public static readonly List<Line> Lines = new List<Line>();
        public static readonly Dictionary<string, PlayerStatus> Others = new Dictionary<string, PlayerStatus>();
        public static string HostInfo = "";
        public static float LastLineAt = -100f;

        // this player's session counters
        public static int Packs, Battles;
        private static int s_salesBase = -1;
        private static bool s_wasInBattle;

        private float _statusTimer;
        private string _lastStatus;
        private int _lastPacks, _lastBattles, _lastSales;
        private string _lastHostInfo;
        private float _hostInfoTimer;

        public override string Name => nameof(Social);

        public Social()
        {
            s_instance = this;
        }

        public static void ApplyPatches(Harmony h)
        {
            // nothing to patch: packs ride CoopCore's CEventPlayer_OnOpenCardPack listener
            // (NotePackOpened), battles and sales are read off game state in Tick
        }

        public override void Reset()
        {
            Lines.Clear();
            Others.Clear();
            HostInfo = "";
            Packs = 0;
            Battles = 0;
            s_salesBase = -1;
            _lastStatus = null;
            _lastHostInfo = null;
            _statusTimer = 0f;
            _hostInfoTimer = 0f;
        }

        // ---------------------------------------------------------------- counters

        public static void NotePackOpened()
        {
            Packs++;
        }

        private static int SalesSoFar()
        {
            try
            {
                int now = CPlayerData.m_GameReportDataCollectPermanent.manualCheckoutCount; // struct
                if (s_salesBase < 0)
                    s_salesBase = now;
                int mine = now - s_salesBase;
                if (CoopCore.Role == CoopRole.Host)
                {
                    // the host's counter also credits guests' checkouts (RegisterSync.CreditGuestCheckout)
                    foreach (var kv in Others)
                        mine -= kv.Value.Sales;
                }
                return Mathf.Max(0, mine);
            }
            catch { return 0; }
        }

        // ---------------------------------------------------------------- chat / ping

        public static void SendChat(string text)
        {
            var self = s_instance;
            if (self == null || string.IsNullOrWhiteSpace(text))
                return;
            text = text.Trim();
            if (text.Length > 200)
                text = text.Substring(0, 200);
            string me = CoopCore.Instance != null ? CoopCore.Instance.EffectivePlayerName : "me";
            AddLine(me, text, false);
            self.Send(new SocialMessage { Kind = SocialKind.Chat, From = me, Text = text });
        }

        public static void SendPing()
        {
            var self = s_instance;
            if (self == null || CoopCore.Role == CoopRole.None)
                return;
            string where = Landmark();
            string me = CoopCore.Instance != null ? CoopCore.Instance.EffectivePlayerName : "me";
            AddLine(me, "needs you at " + where, true);
            HostOnlyFeatures.Notice("Co-op: ping sent (" + where + ")");
            self.Send(new SocialMessage { Kind = SocialKind.Ping, From = me, Text = where });
        }

        private void Send(INetMessage msg)
        {
            if (CoopCore.Role == CoopRole.Host)
                Broadcast?.Invoke(msg);
            else if (CoopCore.Role == CoopRole.Client)
                SendToHost?.Invoke(msg);
        }

        private static void AddLine(string from, string text, bool ping)
        {
            Lines.Add(new Line { From = from ?? "", Text = text ?? "", IsPing = ping, At = Time.unscaledTime });
            while (Lines.Count > MaxLines)
                Lines.RemoveAt(0);
            LastLineAt = Time.unscaledTime;
        }

        /// <summary>Either role: a line arrived. The host stamps the sender's name and relays.</summary>
        public void Apply(SocialMessage msg, int conn)
        {
            Guarded("social", () =>
            {
                if (CoopCore.Role == CoopRole.Host)
                {
                    string n = PeerName?.Invoke(conn);
                    if (!string.IsNullOrEmpty(n))
                        msg.From = n;
                    RelayFrom?.Invoke(conn, msg);
                }
                string from = string.IsNullOrEmpty(msg.From) ? "?" : msg.From;
                if (msg.Kind == SocialKind.Ping)
                {
                    AddLine(from, "needs you at " + msg.Text, true);
                    HostOnlyFeatures.Notice("Co-op: " + from + " needs you at " + msg.Text);
                    try
                    {
                        CoopCore.Instance?.ShowTagFor(from, "over here!", 4f);
                    }
                    catch { }
                }
                else
                    AddLine(from, msg.Text, false);
            });
        }

        // ---------------------------------------------------------------- status

        protected override void OnHostTick(in SyncFrame frame) => Tick(frame.Dt, frame.InGame);
        protected override void OnClientTick(in SyncFrame frame) => Tick(frame.Dt, frame.InGame);

        private void Tick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            // battles: count the moment we enter play-table mode
            try
            {
                var g = PlayCardGame.Game();
                bool inBattle = g != null && g.IsPlayTableGameMode();
                if (inBattle && !s_wasInBattle)
                    Battles++;
                s_wasInBattle = inBattle;
            }
            catch { }
            _statusTimer += dt;
            _hostInfoTimer += dt;
            if (_statusTimer < 2f)
                return;
            _statusTimer = 0f;
            Guarded("status", () =>
            {
                string status = LocalStatus();
                int sales = SalesSoFar();
                string hostInfo = "";
                if (CoopCore.Role == CoopRole.Host)
                    hostInfo = "difficulty: " + Util.Companions.Difficulty.Describe() + "  |  economy: " + Util.Companions.Economy.Describe();
                bool changed = status != _lastStatus || Packs != _lastPacks || Battles != _lastBattles || sales != _lastSales
                    || hostInfo != _lastHostInfo || _hostInfoTimer >= 20f;
                if (!changed)
                    return;
                _hostInfoTimer = 0f;
                _lastStatus = status;
                _lastPacks = Packs;
                _lastBattles = Battles;
                _lastSales = sales;
                _lastHostInfo = hostInfo;
                string me = CoopCore.Instance != null ? CoopCore.Instance.EffectivePlayerName : "me";
                Send(new ActivityStatusMessage { From = me, Status = status, Packs = Packs, Battles = Battles, Sales = sales, HostInfo = hostInfo });
            });
        }

        public void ApplyStatus(ActivityStatusMessage msg, int conn)
        {
            Guarded("status-in", () =>
            {
                if (CoopCore.Role == CoopRole.Host)
                {
                    string n = PeerName?.Invoke(conn);
                    if (!string.IsNullOrEmpty(n))
                        msg.From = n;
                    msg.HostInfo = "";
                    RelayFrom?.Invoke(conn, msg);
                }
                else if (!string.IsNullOrEmpty(msg.HostInfo))
                    HostInfo = msg.HostInfo;
                string key = string.IsNullOrEmpty(msg.From) ? "?" : msg.From;
                if (!Others.TryGetValue(key, out var ps))
                    Others[key] = ps = new PlayerStatus { Name = key };
                ps.Status = msg.Status ?? "";
                ps.Packs = msg.Packs;
                ps.Battles = msg.Battles;
                ps.Sales = msg.Sales;
                ps.UpdatedAt = Time.unscaledTime;
            });
        }

        public static void Forget(string name)
        {
            if (!string.IsNullOrEmpty(name))
                Others.Remove(name);
        }

        /// <summary>What this player is doing, for the others' windows.</summary>
        public static string LocalStatus()
        {
            try
            {
                if (PvpBattle.Active)
                    return "in a PvP match";
                var g = PlayCardGame.Game();
                if (g != null && g.IsPlayTableGameMode())
                    return "in a card battle";
                var m = PlayCardGame.Manager();
                if (m != null && m.m_DeckListScreen != null && m.m_DeckListScreen.gameObject.activeInHierarchy)
                    return "editing decks";
                var ipc = InteractionPlayerController.m_Instance;
                if (ipc != null)
                {
                    if (ipc.m_IsPlayingTopDownGameMode)
                        return "at a play table";
                    if (FiCashMode != null && (bool)FiCashMode.GetValue(ipc))
                        return "at the register";
                    if (FiPhoneMode != null && (bool)FiPhoneMode.GetValue(ipc))
                        return "on the phone";
                }
                if (TournamentSync.ClientHoldsEntry())
                    return "entered in the tournament";
                return "in the shop";
            }
            catch { return ""; }
        }

        private static readonly System.Reflection.FieldInfo FiCashMode = AccessTools.Field(typeof(InteractionPlayerController), "m_IsCashCounterMode");
        private static readonly System.Reflection.FieldInfo FiPhoneMode = AccessTools.Field(typeof(InteractionPlayerController), "m_IsPhoneScreenMode");

        /// <summary>Nearest named thing to the player, for a ping.</summary>
        public static string Landmark()
        {
            try
            {
                var ipc = InteractionPlayerController.m_Instance;
                var t = ipc != null && ipc.m_PlayerCollider != null ? ipc.m_PlayerCollider.transform : null;
                if (t == null)
                    return "the shop";
                Vector3 p = t.position;
                string best = null;
                float bestD = 6f * 6f;
                var sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
                if (sm != null)
                {
                    Consider(sm.m_CashierCounterList, p, ref best, ref bestD, (o, i) => sm.m_CashierCounterList.Count > 1 ? "register " + (i + 1) : "the register");
                    Consider(sm.m_WorkbenchList, p, ref best, ref bestD, (o, i) => "the workbench");
                    Consider(sm.m_PlayTableList, p, ref best, ref bestD, (o, i) => "play table " + (i + 1));
                }
                var sign = UnityEngine.Object.FindObjectOfType<InteractableWarehouseAllowEnterSign>(true);
                if (sign != null)
                {
                    float d = (sign.transform.position - p).sqrMagnitude;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = "the warehouse door";
                    }
                }
                return best ?? (p.x > 3.5f ? "the warehouse side" : "the shop floor");
            }
            catch { return "the shop"; }
        }

        private static void Consider<T>(List<T> list, Vector3 p, ref string best, ref float bestD, Func<T, int, string> name) where T : Component
        {
            if (list == null)
                return;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (o == null)
                    continue;
                float d = (o.transform.position - p).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = name(o, i);
                }
            }
        }
    }
}
