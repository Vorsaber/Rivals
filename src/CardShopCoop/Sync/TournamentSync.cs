using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors tournament DATA and scheduling host->client (MsgType.TournamentState).
    /// The customer bracket only exists in the host simulation, so the joiner gets
    /// CPlayerData.m_TournamentData (schedule, fee, sign-ups, round, prize catalog)
    /// plus a per-customer digest of CustomerTournamentData - enough for the phone
    /// app (HostTournamentScreen reads m_TournamentData live on open) and for the
    /// physical pairing board, which we drive directly through TournamentPairingScreen
    /// because RefreshAllCustomerData wants live Customer objects the joiner never has.
    /// Client ops (game 1.0): the shop's one player ENTRY (TournamentEntry - a guest may hold
    /// it) and the PLAN (TournamentPlan - the guest runs the vanilla phone screen on the
    /// mirrored data and ships the result on close; the mirror waits while that screen is
    /// open). Prize shelf CONTENTS are synced elsewhere (CardShelfSync).
    /// </summary>
    public class TournamentSync : TickableCoopModule
    {
        /// <summary>TournamentPrizeShelf.m_ScreenMesh (the shelf's little tournament display) is
        /// absent from the Game Pass Assembly-CSharp, which made a direct field access fail to
        /// COMPILE against that build - one cosmetic toggle taking the whole universal DLL down
        /// with it. Resolved once through reflection instead, so a missing or renamed field just
        /// disables the show/hide. Null when the field isn't there; every use is null-guarded.
        /// (Reflection change contributed by Jburne10 for the Game Pass build.)</summary>
        private static readonly FieldInfo FiScreenMesh =
            AccessTools.Field(typeof(TournamentPrizeShelf), "m_ScreenMesh");

        /// <summary>Set by CoopCore: host -> clients state broadcast.</summary>
        public Action<INetMessage> BroadcastState;
        public Action<INetMessage> SendToHost;              // client side
        public Action<int, INetMessage> SendToClient;       // host side
        public Func<int, string> PeerName;                  // host side: conn -> display name

        // game 1.0: the shop has ONE player entry in its own tournament
        // (CPlayerData.m_IsPlayerRegisteredForTournament). Either the host or one guest holds
        // it. The host runs the vanilla sign-up either way, so the customer sim, bracket and
        // result registration all see "the player" exactly as vanilla; the guest just gets
        // the mirrored record and the seat at the assigned table.
        private const int NoEntry = -1;
        private const int HostEntry = -2;
        private int _entryConn = NoEntry;              // host
        private static string s_entryHolder = "";      // both: name of the holder, "" none
        private static bool s_entryMine;               // client: the entry is this guest's

        // R4 - the CHALLENGER: vanilla has one player slot; a second human plays the tournament
        // AS one of the NPC entrants (its "proxy"). The NPC still walks and sits; the human plays
        // its matches - against an NPC on their own PC (GuestBattle), against the shop's player
        // as PvP - and the result is written over the coin flip the table would have rolled.
        private int _proxyConn = NoEntry;              // host: the challenger's connection
        private Customer _proxyCustomer;               // host: the NPC they play as (assigned on tournament day)
        private int _proxyResult = -1;                 // host: -1 none, 0 lost, 1 won (pending for the table's resolution)
        private static string s_proxyHolder = "";      // both
        private static bool s_proxyMine;               // client
        private static int s_proxyTable;               // client
        private static bool s_proxyVsPlayer;           // client: this round's opponent is the shop's player (PvP)
        private static bool s_proxyFinished;           // client
        private static int s_proxyNoticedTable = -1;
        private static TournamentSync s_instance;

        // client: the phone's tournament screen is open here - the guest may be scheduling, so
        // the mirror waits (it would repaint the prize list under the open screen) and the
        // result goes up as one TournamentPlan when the screen closes
        private static bool s_planEditing;
        private static int s_planHashAtOpen;

        /// <summary>True while ClientApplyState writes CPlayerData.m_TournamentData, so
        /// no patch mistakes the authoritative copy for a local scheduling action.</summary>
        public static bool ApplyingRemote;

        private readonly SnapshotGate _gate = new SnapshotGate(1.5f, 15f, -6.1f);
        private int _clientHash;

        // NEVER CSingleton<CustomerManager>.Instance: touched while no real manager
        // exists (client reload loading screen, host mid-session save load) the getter
        // fabricates a fake empty DontDestroyOnLoad manager that shadows the real one
        // for the rest of the run (see WorldSync.ResolveShelfManager). Static because
        // the wire writer and hash are static; fake-null re-resolves after scene loads.
        private static CustomerManager _cm;

        private static CustomerManager Cm()
        {
            if (_cm == null)
                _cm = UnityEngine.Object.FindObjectOfType<CustomerManager>();
            return _cm;
        }

        public override string Name => "tournament";

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public override void Dispose()
        {
            base.Dispose();
            ApplyingRemote = false;
            _cm = null;
        }

        public override void Reset()
        {
            _gate.Reset(-6.1f);
            _clientHash = 0;
            _cm = null;
            _entryConn = NoEntry;
            s_entryHolder = "";
            s_entryMine = false;
            s_planEditing = false;
            _proxyConn = NoEntry;
            _proxyCustomer = null;
            _proxyResult = -1;
            s_proxyHolder = "";
            s_proxyMine = false;
            s_proxyTable = 0;
            s_proxyVsPlayer = false;
            s_proxyFinished = false;
            s_proxyNoticedTable = -1;
            Unpark(); // fv-689
            NpcSync.SetParkedCustomer(-1); // fv-689
            ResetPrizeClaims(); // fv-687
        }

        public TournamentSync()
        {
            s_instance = this;
        }

        public override void ForceResend()
        {
            _gate.Force();
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // Scheduling, cancelling and prize setup mutate m_TournamentData and the prize
            // plan. The guest runs the vanilla screen against the mirrored data and, on
            // closing it, ships the result up as one TournamentPlan; the host applies it and
            // the mirror carries it back. While the screen is open here the mirror waits.
            Try(h, typeof(HostTournamentScreen), "OnOpenScreen",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(ScreenOpenPostfix)));
            Try(h, typeof(HostTournamentScreen), "OnCloseScreen",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(ScreenClosePostfix)));
            Try(h, typeof(HostTournamentScreen), "OnPressConfirm",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(PlanChangedPostfix)));
            Try(h, typeof(HostTournamentScreen), "ConfirmCancelTournament",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(PlanChangedPostfix)));
            // the one player entry: guest asks the host; host is refused while a guest holds it
            Try(h, typeof(HostTournamentScreen), "OnPressPlayerSignUpTournament",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(SignUpPrefix)),
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(SignUpPostfix)));
            Try(h, typeof(HostTournamentScreen), "OnPressPlayerSignOutTournament",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(SignOutPrefix)),
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(SignUpPostfix)));
            // host may not take the guest's tournament seat
            Try(h, typeof(InteractablePlayTable), "OnRightMouseButtonUp",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(HostTableClickPrefix)));
            // R4: the challenger's real result replaces the coin flip for its NPC and the opponent
            Try(h, typeof(Customer), "SetTournamentWinLose",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ProxyWinLosePrefix)));
            // R4: the challenger's NPC does not collect the prize - the human takes it off the shelf
            Try(h, typeof(Customer), "OnTournamentEnded",
                prefix: new HarmonyMethod(typeof(TournamentSync), nameof(ProxyEndedPrefix)));
            Try(h, typeof(InteractablePlayTable), "ExitPlayerCardGame",
                postfix: new HarmonyMethod(typeof(TournamentSync), nameof(ProxyTableExitPostfix)));
        }

        // ================================================================ R4: the challenger

        public static bool IsProxy(int conn)
        {
            var self = s_instance;
            return self != null && conn != NoEntry && conn != HostEntry && self._proxyConn == conn;
        }

        public static Customer ProxyCustomer => s_instance != null ? s_instance._proxyCustomer : null;

        /// <summary>Host: the NPC's table number this round (0 = none).</summary>
        public static int ProxyTable()
        {
            var c = ProxyCustomer;
            try
            {
                return c != null ? c.GetCustomerTournamentData().m_TournamentCustomerPlayTableIndex : 0;
            }
            catch { return 0; }
        }

        private static bool ProxyFinishedRound()
        {
            var c = ProxyCustomer;
            try
            {
                return c != null && c.GetCustomerTournamentData().m_HasFinishCurrentTournamentRound;
            }
            catch { return false; }
        }

        /// <summary>Host: on tournament day, is this table the one where the challenger meets
        /// the shop's PLAYER (host holding the entry)? Then the host waits for PvP instead of
        /// sitting down against the NPC's AI.</summary>
        public static bool HostProxyVsPlayerTable(int tableIndex)
        {
            var self = s_instance;
            if (self == null || self._entryConn != HostEntry || self._proxyCustomer == null)
                return false;
            try
            {
                var td = CPlayerData.m_TournamentData;
                var ptd = CPlayerData.m_PlayerTournamentData;
                if (td == null || ptd == null || !td.m_IsTournamentDay || td.m_IsTournamentDayOver || !CPlayerData.m_IsPlayerRegisteredForTournament)
                    return false;
                if (ptd.m_HasRegisteredTournamentResult || ProxyFinishedRound())
                    return false;
                int pt = ProxyTable();
                if (pt <= 0 || ptd.m_TournamentCustomerPlayTableIndex != pt)
                    return false;
                var table = GuestBattle.TableAt(tableIndex);
                return table != null && table.GetTournamentPlayTableNumber() == pt;
            }
            catch { return false; }
        }

        /// <summary>Host: the challenger's match on their PC ended (GuestBattle exit) - hold
        /// the result for the table's resolution that follows.</summary>
        public static void SetProxyResult(bool win, bool draw)
        {
            var self = s_instance;
            if (self == null || self._proxyCustomer == null)
                return;
            self._proxyResult = win && !draw ? 1 : 0;
            CoopPlugin.Log.LogInfo($"TournamentSync: challenger {(win && !draw ? "won" : "lost")} the round at table {ProxyTable()}");
        }

        public static bool ClientIsProxy() => s_proxyMine;
        public static int ClientProxyTable() => s_proxyTable;
        public static bool ClientProxyVsPlayer() => s_proxyVsPlayer;
        public static bool ClientProxyFinished() => s_proxyFinished;

        /// <summary>Host: the tournament day started and the NPC field is in - the challenger
        /// becomes the last NPC entrant. Cleared when the next tournament is scheduled.</summary>
        private void TickProxy(TournamentData td)
        {
            if (_proxyConn == NoEntry)
            {
                if (_proxyCustomer != null)
                    _proxyCustomer = null;
                return;
            }
            if (!td.m_IsTournamentDay && !td.m_IsTournamentDayOver)
            {
                if (_proxyCustomer != null)
                {
                    _proxyCustomer = null;
                    _proxyConn = NoEntry; // one tournament per entry, like the player's own
                    s_proxyHolder = "";
                    _gate.Force();
                }
                return;
            }
            if (_proxyCustomer != null || !td.m_IsTournamentDay || td.m_IsTournamentDayOver)
                return;
            var cm = Cm();
            var list = cm != null ? cm.m_TournamentCustomerList : null;
            if (list == null || list.Count < td.m_TournamentMaxPlayerCount)
                return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var c = list[i];
                if (c == null)
                    continue;
                try
                {
                    if (!c.GetCustomerTournamentData().m_IsTournamentCustomer)
                        continue;
                }
                catch { continue; }
                _proxyCustomer = c;
                _gate.Force();
                CoopPlugin.Log.LogInfo($"TournamentSync: {NameOf(_proxyConn)} plays as tournament customer #{c.GetCustomerTournamentData().m_TournamentCustomerIndex}");
                HostOnlyFeatures.Notice("Co-op: " + NameOf(_proxyConn) + " plays the tournament as customer #" + c.GetCustomerTournamentData().m_TournamentCustomerIndex);
                return;
            }
        }

        public static void ProxyWinLosePrefix(Customer __instance, ref bool isWin, int opponentCustomerIndex)
        {
            var self = s_instance;
            if (self == null || CoopCore.Role != CoopRole.Host || self._proxyCustomer == null || self._proxyResult < 0)
                return;
            try
            {
                int proxyIdx = self._proxyCustomer.GetCustomerTournamentData().m_TournamentCustomerIndex;
                if (__instance == self._proxyCustomer)
                    isWin = self._proxyResult == 1;
                else if (opponentCustomerIndex == proxyIdx)
                    isWin = self._proxyResult != 1;
            }
            catch { }
        }

        public static void ProxyTableExitPostfix(InteractablePlayTable __instance)
        {
            var self = s_instance;
            if (self == null || self._proxyResult < 0)
                return;
            try
            {
                if (__instance.GetTournamentPlayTableNumber() == ProxyTable())
                    self._proxyResult = -1;
            }
            catch { self._proxyResult = -1; }
        }

        /// <summary>The NPC's prize goes to the human: send the NPC away empty-handed (a
        /// placement past the prize table) so the prizes stay on the shelf for the challenger.</summary>
        public static void ProxyEndedPrefix(Customer __instance, ref int tournamentPlacementIndex)
        {
            var self = s_instance;
            if (self == null || CoopCore.Role != CoopRole.Host || __instance != self._proxyCustomer)
                return;
            int placed = tournamentPlacementIndex;
            self._proxyPlaced = placed; // fv-687: the challenger's entitlement is this placement's prize list
            tournamentPlacementIndex = 99;
            string who = NameOfStatic(self._proxyConn);
            CoopPlugin.Log.LogInfo($"TournamentSync: challenger {who} placed #{placed + 1} - prizes left on the shelf for them");
            HostOnlyFeatures.Notice($"Co-op: {who} placed #{placed + 1} in the tournament" + (placed <= 7 ? " - their prize is on the shelf" : ""));
        }

        // --- fv-689 npc-seat begin
        // R12: while the challenger sits in its NPC's seat the NPC body is PARKED - renderers off
        // on the host, puppet hidden on every client (ProxyFlags bit 16 + ProxyNpcIndex ->
        // NpcSync.SetParkedCustomer). The sim is untouched: the customer keeps sitting, the table
        // keeps its occupant, the result path stays R4's. Parked from the frame the NPC's table
        // carries a player game (GuestBattle's proxy sit and PvpBattle's sit both book the seat)
        // until the table stands down, plus a short hold while the NPC is still in its sit pose so
        // the two bodies do not overlap during the stand-up.
        private const float ParkHold = 2f;
        private Customer _parkedCustomer;                                  // host: the body currently hidden
        private readonly List<Renderer> _parkedRenderers = new List<Renderer>();
        private readonly List<bool> _parkedWasEnabled = new List<bool>();
        private float _parkHoldUntil = -1f;

        /// <summary>Host: the challenger's NPC body is parked (hidden) right now.</summary>
        public static bool ProxyParked => s_instance != null && s_instance._parkedCustomer != null;

        /// <summary>Host: the proxy NPC's index in the customer list (NpcSync's identity), -1 none.</summary>
        private int ProxyNpcIndex()
        {
            var c = _proxyCustomer;
            var cm = Cm();
            if (c == null || cm == null)
                return -1;
            try
            {
                var list = cm.GetCustomerList();
                return list != null ? list.IndexOf(c) : -1;
            }
            catch { return -1; }
        }

        /// <summary>Host: is the proxy NPC seated at a table that has a player game on it -
        /// i.e. the challenger (or the host, for the PvP round) took the seat?</summary>
        private static bool ProxyTableHasPlayerGame(Customer proxy)
        {
            try
            {
                var sm = CSingleton<ShelfManager>.Instance;
                var tables = sm != null ? sm.m_PlayTableList : null;
                if (tables == null)
                    return false;
                for (int i = 0; i < tables.Count; i++)
                {
                    var t = tables[i];
                    if (t == null)
                        continue;
                    var occ = t.GetOccupiedCustomerList();
                    if (occ == null || !occ.Contains(proxy))
                        continue;
                    return t.GetHasStartPlayerPlayCard();
                }
            }
            catch { }
            return false;
        }

        private static bool ProxySitting(Customer proxy)
        {
            try
            {
                return proxy != null && proxy.m_Anim != null && proxy.m_Anim.GetBool("IsSitting");
            }
            catch { return false; }
        }

        /// <summary>Host, every frame (ahead of the snapshot gate): park or unpark the NPC body.</summary>
        private void TickPark()
        {
            var proxy = _proxyConn != NoEntry ? _proxyCustomer : null;
            bool want = false;
            if (proxy != null)
            {
                if (ProxyTableHasPlayerGame(proxy))
                {
                    want = true;
                    _parkHoldUntil = Time.unscaledTime + ParkHold;
                }
                else if (_parkedCustomer == proxy && Time.unscaledTime < _parkHoldUntil && ProxySitting(proxy))
                    want = true; // the game is over; stay out of the chair while still in the sit pose
            }
            if (_parkedCustomer != null && (!want || _parkedCustomer != proxy))
                Unpark();
            if (want && _parkedCustomer == null)
                Park(proxy);
        }

        private void Park(Customer c)
        {
            _parkedRenderers.Clear();
            _parkedWasEnabled.Clear();
            try
            {
                // every renderer under the body, inactive ones too: the game switches the card
                // fan / single card props on after the seat is booked, and those must stay dark
                var rs = c.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] == null)
                        continue;
                    _parkedRenderers.Add(rs[i]);
                    _parkedWasEnabled.Add(rs[i].enabled);
                    rs[i].enabled = false;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TournamentSync park: " + e.Message); }
            _parkedCustomer = c;
            _gate.Force();
            CoopPlugin.Log.LogInfo($"TournamentSync: parked the challenger's NPC (customer #{ProxyNpcIndex()}, {_parkedRenderers.Count} renderers) - {NameOf(_proxyConn)} has the seat");
        }

        private void Unpark()
        {
            if (_parkedCustomer == null)
                return;
            int restored = 0;
            for (int i = 0; i < _parkedRenderers.Count; i++)
            {
                try
                {
                    if (_parkedRenderers[i] != null && i < _parkedWasEnabled.Count && _parkedWasEnabled[i])
                    {
                        _parkedRenderers[i].enabled = true;
                        restored++;
                    }
                }
                catch { }
            }
            _parkedRenderers.Clear();
            _parkedWasEnabled.Clear();
            _parkedCustomer = null;
            _parkHoldUntil = -1f;
            _gate.Force();
            CoopPlugin.Log.LogInfo($"TournamentSync: unparked the challenger's NPC ({restored} renderers back)");
        }
        // --- fv-689 npc-seat end
        private static string NameOfStatic(int conn)
        {
            return s_instance != null ? s_instance.NameOf(conn) : "the challenger";
        }

        public static bool SignUpPrefix()
        {
            var self = s_instance;
            if (self == null)
                return true;
            if (CoopCore.Role == CoopRole.Host)
            {
                if (self._entryConn != NoEntry && self._entryConn != HostEntry)
                {
                    HostOnlyFeatures.Notice("Co-op: " + s_entryHolder + " is entered in this tournament");
                    return false;
                }
                return true;
            }
            if (CoopCore.Role == CoopRole.Client)
            {
                // the shop's one entry is taken by someone else: enter as the challenger
                bool taken = !string.IsNullOrEmpty(s_entryHolder) && !s_entryMine;
                self.SendToHost?.Invoke(new TournamentEntryMessage { Want = true, AsProxy = taken });
                return false;
            }
            return true;
        }

        public static bool SignOutPrefix()
        {
            var self = s_instance;
            if (self == null)
                return true;
            if (CoopCore.Role == CoopRole.Host)
            {
                if (self._entryConn != NoEntry && self._entryConn != HostEntry)
                {
                    HostOnlyFeatures.Notice("Co-op: " + s_entryHolder + " is entered in this tournament");
                    return false;
                }
                return true;
            }
            if (CoopCore.Role == CoopRole.Client)
            {
                if (s_entryMine)
                    self.SendToHost?.Invoke(new TournamentEntryMessage { Want = false });
                else if (s_proxyMine)
                    self.SendToHost?.Invoke(new TournamentEntryMessage { Want = false, AsProxy = true });
                else
                    HostOnlyFeatures.Notice("Co-op: " + (string.IsNullOrEmpty(s_entryHolder) ? "the host" : s_entryHolder) + " is entered in this tournament");
                return false;
            }
            return true;
        }

        /// <summary>Host: after its own sign-up / sign-out ran, record who holds the entry.</summary>
        public static void SignUpPostfix()
        {
            var self = s_instance;
            if (self == null || CoopCore.Role != CoopRole.Host)
                return;
            if (self._entryConn != NoEntry && self._entryConn != HostEntry)
                return;
            self.HostSetEntry(CPlayerData.m_IsPlayerRegisteredForTournament ? HostEntry : NoEntry);
        }

        public static bool HostTableClickPrefix(InteractablePlayTable __instance)
        {
            var self = s_instance;
            if (self == null || CoopCore.Role != CoopRole.Host)
                return true;
            try
            {
                var td = CPlayerData.m_TournamentData;
                if (td == null || !td.m_IsTournamentDay || td.m_IsTournamentDayOver)
                    return true;
                if (self._entryConn == NoEntry || self._entryConn == HostEntry)
                    return true;
                // a guest holds the entry: vanilla would seat the host as "the player"
                HostOnlyFeatures.Notice("Co-op: " + s_entryHolder + " is playing in this tournament");
                return false;
            }
            catch { return true; }
        }

        /// <summary>Host: does this connection hold the shop's tournament entry? (GuestBattle's
        /// seat check for tournament tables.)</summary>
        public static bool GuestHoldsEntry(int conn)
        {
            var self = s_instance;
            return self != null && self._entryConn == conn && conn != NoEntry && conn != HostEntry;
        }

        /// <summary>Client: this guest holds the entry (mirrored from the host).</summary>
        public static bool ClientHoldsEntry()
        {
            return s_entryMine;
        }

        private static bool TournamentOver()
        {
            try
            {
                var td = CPlayerData.m_TournamentData;
                return td != null && td.m_IsTournamentDayOver;
            }
            catch { return false; }
        }

        /// <summary>Host: this visitor played the tournament and it is over - what they take
        /// off the PRIZE shelf is their winnings, not a purchase.</summary>
        public static bool HostPrizeFree(int conn)
        {
            return (GuestHoldsEntry(conn) || IsProxy(conn)) && TournamentOver();
        }

        /// <summary>Visitor: same test from this side (mirrored entry + day over).</summary>
        public static bool ClientPrizeFree()
        {
            return (s_entryMine || s_proxyMine) && TournamentOver();
        }

        // --- fv-687 prize-entitlement begin
        // R9: HostPrizeFree/ClientPrizeFree say "this visitor is an entrant and the tournament is
        // over"; the ledger in Rivals.PrizeClaim says WHICH cards / items are theirs (their
        // placement's prize list, once). Every free take goes through the methods below.
        private int _proxyPlaced = -1;                 // host: the challenger's placement (ProxyEndedPrefix), -1 none

        private static void ResetPrizeClaims()
        {
            Rivals.PrizeClaim.Reset();
            if (s_instance != null)
                s_instance._proxyPlaced = -1;
        }

        /// <summary>Host tick: the tournament just ended - grant each human entrant its placement's
        /// prize list (the vanilla end loop writes the player's placement and the NPC prefix wrote
        /// the challenger's before it flips DayOver, so both are in by the time this sees it).
        /// A new day clears every ledger, as vanilla clears DayOver.</summary>
        private void TickPrizeClaims(TournamentData td)
        {
            if (!td.m_IsTournamentDayOver)
            {
                Rivals.PrizeClaim.HostRevokeAll();
                if (!td.m_IsTournamentDay)
                    _proxyPlaced = -1;
                return;
            }
            if (_entryConn != NoEntry && _entryConn != HostEntry && !Rivals.PrizeClaim.HostHas(_entryConn))
            {
                var ptd = CPlayerData.m_PlayerTournamentData;
                int placed = ptd != null ? ptd.m_TournamentPlacementIndex : 99;
                Rivals.PrizeClaim.HostGrant(_entryConn, placed, td);
                HostOnlyFeatures.Notice($"Co-op: {s_entryHolder} placed #{placed + 1}" + (Rivals.PrizeClaim.HostHasRemaining(_entryConn) ? " - their prize is on the shelf" : ""));
            }
            if (_proxyConn != NoEntry && _proxyPlaced >= 0 && !Rivals.PrizeClaim.HostHas(_proxyConn))
                Rivals.PrizeClaim.HostGrant(_proxyConn, _proxyPlaced, td);
        }

        /// <summary>Host: this displayed card is the visitor's prize - consumed off their ledger.
        /// False = charge it or refuse it like any other take.</summary>
        public static bool HostPrizeCard(int conn, CardData card)
        {
            return HostPrizeFree(conn) && Rivals.PrizeClaim.HostClaimCard(conn, card);
        }

        /// <summary>Host: these items are the visitor's prize (all of them) - consumed off their ledger.</summary>
        public static bool HostPrizeItems(int conn, EItemType type, int count)
        {
            return HostPrizeFree(conn) && Rivals.PrizeClaim.HostClaimItems(conn, type, count);
        }

        /// <summary>Host: an entrant with winnings still on the shelf is taking something that is
        /// not theirs - refuse the take (the client refuses it locally too; this catches a stale mirror).</summary>
        public static bool HostRefusePrizeShelfTake(int conn, EItemType type, int count)
        {
            return HostPrizeFree(conn) && Rivals.PrizeClaim.HostShouldRefuseItems(conn, type, count);
        }

        /// <summary>Host: a card claim (VisitorCardBuy Prize=true) it did not honour - bounce it so
        /// the visitor's bag gives the card back.</summary>
        public static void HostBouncePrizeCard(int conn, int key)
        {
            Rivals.PrizeClaim.HostSend(conn, key);
        }

        /// <summary>Visitor: this displayed card is my prize (consumed optimistically; the host's
        /// mirror confirms or bounces).</summary>
        public static bool ClientPrizeCard(CardData card, int key)
        {
            return ClientPrizeFree() && Rivals.PrizeClaim.ClientClaimCard(card, key);
        }

        /// <summary>Visitor: all these items are my prize? (check before the take is sent)</summary>
        public static bool ClientPrizeItems(EItemType type, int count)
        {
            return ClientPrizeFree() && Rivals.PrizeClaim.ClientHasItems(type, count);
        }

        /// <summary>Visitor: the host accepted the take - was it my prize? (consumes)</summary>
        public static bool ClientPrizeItemsTaken(EItemType type, int count)
        {
            return ClientPrizeFree() && Rivals.PrizeClaim.ClientClaimItems(type, count);
        }

        /// <summary>Visitor: I still have winnings to collect (a non-prize take off that shelf is refused).</summary>
        public static bool ClientPrizeRemaining()
        {
            return ClientPrizeFree() && Rivals.PrizeClaim.ClientHasRemaining;
        }

        public static string ClientPrizeDescribe()
        {
            return Rivals.PrizeClaim.ClientDescribeRemaining();
        }

        public void ClientApplyPrizeClaim(TournamentPrizeClaimMessage msg)
        {
            Guarded("prize-claim", () => Rivals.PrizeClaim.ClientApply(msg));
        }
        // --- fv-687 prize-entitlement end

        private void HostSetEntry(int conn)
        {
            if (_entryConn != conn && _entryConn != NoEntry && _entryConn != HostEntry) Rivals.PrizeClaim.HostRevoke(_entryConn); // fv-687
            _entryConn = conn;
            s_entryHolder = conn == NoEntry ? "" : NameOf(conn);
            _gate.Force();
        }

        private string NameOf(int conn)
        {
            if (conn == HostEntry)
                return CoopCore.Instance != null ? (CoopCore.Instance.EffectivePlayerName ?? "the host") : "the host";
            var name = PeerName?.Invoke(conn);
            return string.IsNullOrEmpty(name) ? "a guest" : name;
        }

        public static void ScreenOpenPostfix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            s_planEditing = true;
            try
            {
                s_planHashAtOpen = PlanHash(CPlayerData.m_TournamentData);
            }
            catch { s_planHashAtOpen = 0; }
        }

        public static void ScreenClosePostfix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            s_planEditing = false;
            s_instance?.ClientSendPlanIfChanged();
        }

        /// <summary>Scheduled or cancelled from the guest's phone: ship it now rather than on
        /// close, so the host (and the customers) see it immediately.</summary>
        public static void PlanChangedPostfix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            s_instance?.ClientSendPlanIfChanged();
        }

        private void ClientSendPlanIfChanged()
        {
            Guarded("plan-up", () =>
            {
                var td = CPlayerData.m_TournamentData;
                if (td == null || SendToHost == null)
                    return;
                int h = PlanHash(td);
                if (h == s_planHashAtOpen)
                    return;
                s_planHashAtOpen = h;
                var msg = new TournamentPlanMessage
                {
                    IsHosting = td.m_IsHostingTournament,
                    Fee = td.m_TournamentFee,
                    TotalValue = td.m_TournamentTotalValue,
                    MaxPlayerCount = td.m_TournamentMaxPlayerCount,
                    PrizeSlots = BuildPrizeSlots(td),
                };
                SendToHost(msg);
                CoopPlugin.Log.LogInfo($"TournamentSync: sent tournament plan (hosting={msg.IsHosting}, fee={msg.Fee}, cap={msg.MaxPlayerCount})");
            });
        }

        /// <summary>Everything the guest's screen can change.</summary>
        private static int PlanHash(TournamentData td)
        {
            if (td == null)
                return 0;
            int h = 17;
            h = h * 31 + (td.m_IsHostingTournament ? 1 : 0);
            h = h * 31 + td.m_TournamentMaxPlayerCount;
            h = h * 31 + (int)(td.m_TournamentFee * 100f);
            h = h * 31 + (int)(td.m_TournamentTotalValue * 100f);
            var lists = td.m_PrizeDataList;
            if (lists != null)
                for (int i = 0; i < lists.Count; i++)
                {
                    var inner = lists[i] != null ? lists[i].m_PrizeDataList : null;
                    if (inner == null)
                        continue;
                    for (int j = 0; j < inner.Count; j++)
                    {
                        var p = inner[j];
                        if (p == null)
                            continue;
                        h = h * 31 + (int)p.m_ItemType;
                        h = h * 31 + p.m_Count;
                        if (p.m_CardData != null)
                        {
                            h = h * 31 + (int)p.m_CardData.expansionType;
                            h = h * 31 + (int)p.m_CardData.monsterType;
                            h = h * 31 + (int)p.m_CardData.borderType;
                        }
                    }
                }
            return h;
        }

        /// <summary>Host: a guest changed the plan from their phone. Same writes the vanilla
        /// screen makes (OnPressConfirm / ConfirmCancelTournament / the prize screen) against
        /// the host's data; refused on tournament day, when vanilla refuses too.</summary>
        public void HostApplyPlan(TournamentPlanMessage msg, int conn)
        {
            Guarded("plan", () =>
            {
                var td = CPlayerData.m_TournamentData;
                if (td == null || td.m_IsTournamentDay)
                {
                    CoopPlugin.Log.LogInfo($"TournamentSync: plan from {NameOf(conn)} ignored (tournament day)");
                    return;
                }
                bool wasHosting = td.m_IsHostingTournament;
                if (msg.IsHosting && !wasHosting)
                {
                    try
                    {
                        CSingleton<CPlayerData>.Instance.ResetPlayerTournamentData();
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("ResetPlayerTournamentData: " + e.Message); }
                    td.m_TournamentSignedUpCustomerCount = 0;
                    HostSetEntry(NoEntry);
                }
                else if (!msg.IsHosting && wasHosting)
                {
                    td.m_TournamentSignedUpCustomerCount = 0;
                }
                td.m_IsHostingTournament = msg.IsHosting;
                td.m_TournamentFee = msg.Fee;
                td.m_TournamentTotalValue = msg.TotalValue;
                td.m_TournamentMaxPlayerCount = msg.MaxPlayerCount;
                ApplyPrizeSlots(td, msg.PrizeSlots);
                RefreshHostTournamentScreen();
                _gate.Force();
                CoopPlugin.Log.LogInfo($"TournamentSync: {NameOf(conn)} {(msg.IsHosting ? (wasHosting ? "updated" : "scheduled") : (wasHosting ? "cancelled" : "edited"))} the tournament (fee {msg.Fee}, cap {msg.MaxPlayerCount})");
            });
        }

        private static readonly MethodInfo MiScreenOpen =
            AccessTools.Method(typeof(HostTournamentScreen), "OnOpenScreen");

        /// <summary>If the phone's tournament screen is open on this PC, repaint it from data.</summary>
        private static void RefreshHostTournamentScreen()
        {
            try
            {
                var screen = UnityEngine.Object.FindObjectOfType<HostTournamentScreen>();
                if (screen == null || !screen.gameObject.activeInHierarchy || MiScreenOpen == null)
                    return;
                MiScreenOpen.Invoke(screen, null);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TournamentSync.RefreshHostTournamentScreen: " + e.Message); }
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            TickPark(); // fv-689: every frame, ahead of the gate (Force() on a change ships the state)
            if (!inGame)
                return;
            if (!_gate.Due(dt))
                return;
            Guarded("host", () =>
            {
                var td = CPlayerData.m_TournamentData;
                if (td == null)
                    return;
                TickProxy(td);
                TickPrizeClaims(td); // fv-687
                int hash = ComputeHash(td);
                if (!_gate.ShouldSend(hash))
                    return;
                BroadcastState?.Invoke(BuildState(td));
                Rivals.PrizeClaim.HostSendAll(); // fv-687: the ledgers ride the same heal cadence
            });
        }

        // Scheduling is blocked client-side with a toast rather than forwarded; the only
        // op a guest sends is the player entry below.

        /// <summary>Host: a guest wants in (or out) of the shop's tournament. Runs the vanilla
        /// sign-up body (HostTournamentScreen.OnPressPlayerSignUpTournament minus the button
        /// toggles) against the HOST's data, so from here on the customer sim treats the
        /// guest exactly as it would the host.</summary>
        public void HostApplyEntry(TournamentEntryMessage msg, int conn)
        {
            Guarded("entry", () =>
            {
                var td = CPlayerData.m_TournamentData;
                int reason = 0;
                if (td == null)
                    reason = (int)ENotEnoughResourceText.NoSlotToJoinTournament;
                else if (msg.AsProxy)
                {
                    if (msg.Want)
                    {
                        if (td.m_IsTournamentDay)
                            reason = (int)ENotEnoughResourceText.NoSlotToJoinTournament;
                        else if (_entryConn == conn)
                        { /* they already hold the real entry */
                        }
                        else if (_proxyConn != NoEntry && _proxyConn != conn)
                            reason = (int)ENotEnoughResourceText.PlayerAlreadyJoinedTournament;
                        else if (!td.m_IsHostingTournament)
                            reason = (int)ENotEnoughResourceText.NoSlotToJoinTournament;
                        else if (_proxyConn != conn)
                        {
                            _proxyConn = conn;
                            s_proxyHolder = NameOf(conn);
                            _gate.Force();
                            CoopPlugin.Log.LogInfo($"TournamentSync: {NameOf(conn)} entered as the challenger (plays as an NPC entrant)");
                            HostOnlyFeatures.Notice("Co-op: " + NameOf(conn) + " joins the tournament as a challenger");
                        }
                    }
                    else if (_proxyConn == conn)
                    {
                        if (td.m_IsTournamentDay)
                            reason = (int)ENotEnoughResourceText.CannotWithdrawTournament;
                        else
                        {
                            _proxyConn = NoEntry;
                            s_proxyHolder = "";
                            _gate.Force();
                            CoopPlugin.Log.LogInfo($"TournamentSync: {NameOf(conn)} withdrew as the challenger");
                        }
                    }
                }
                else if (msg.Want)
                {
                    if (td.m_IsTournamentDay)
                        reason = (int)ENotEnoughResourceText.NoSlotToJoinTournament;
                    else if (CPlayerData.m_IsPlayerRegisteredForTournament && _entryConn != conn)
                        reason = (int)ENotEnoughResourceText.PlayerAlreadyJoinedTournament;
                    else if (CPlayerData.m_IsPlayerRegisteredForTournament)
                    { /* already theirs */
                    }
                    else if (td.m_TournamentSignedUpCustomerCount < td.m_TournamentMaxPlayerCount)
                    {
                        td.m_TournamentSignedUpCustomerCount++;
                        CPlayerData.m_IsPlayerRegisteredForTournament = true;
                        HostSetEntry(conn);
                        RefreshHostScreen();
                        CoopPlugin.Log.LogInfo($"TournamentSync: {NameOf(conn)} entered the tournament");
                    }
                    else
                        reason = (int)ENotEnoughResourceText.NoSlotToJoinTournament;
                }
                else
                {
                    if (_entryConn != conn)
                    { /* not theirs to drop */
                    }
                    else if (td.m_IsTournamentDay)
                        reason = (int)ENotEnoughResourceText.CannotWithdrawTournament;
                    else if (CPlayerData.m_IsPlayerRegisteredForTournament)
                    {
                        td.m_TournamentSignedUpCustomerCount--;
                        CPlayerData.m_IsPlayerRegisteredForTournament = false;
                        HostSetEntry(NoEntry);
                        RefreshHostScreen();
                        CoopPlugin.Log.LogInfo($"TournamentSync: {NameOf(conn)} withdrew from the tournament");
                    }
                }
                SendToClient?.Invoke(conn, new TournamentEntryResultMessage
                {
                    Ok = reason == 0,
                    Registered = reason == 0 && _entryConn == conn,
                    Proxy = reason == 0 && _proxyConn == conn,
                    Reason = reason
                });
            });
        }

        /// <summary>Host: a guest left. Their entry stays (the customer sim already counts it);
        /// the host inherits it and can play or withdraw.</summary>
        public void HostReleaseConn(int conn)
        {
            if (_entryConn != conn)
                return;
            CoopPlugin.Log.LogInfo($"TournamentSync: {NameOf(conn)} left holding the tournament entry - the host inherits it");
            HostSetEntry(CPlayerData.m_IsPlayerRegisteredForTournament ? HostEntry : NoEntry);
        }

        /// <summary>Host: the challenger left - their NPC plays on as an NPC.</summary>
        public void HostReleaseProxy(int conn)
        {
            if (_proxyConn != conn)
                return;
            CoopPlugin.Log.LogInfo($"TournamentSync: challenger {NameOf(conn)} left - the NPC plays on by itself");
            Rivals.PrizeClaim.HostRevoke(conn); // fv-687
            _proxyPlaced = -1; // fv-687
            _proxyConn = NoEntry;
            _proxyCustomer = null;
            _proxyResult = -1;
            s_proxyHolder = "";
            _gate.Force();
        }

        /// <summary>Repaint the phone tournament screen's entry buttons and count if it is open
        /// on this PC (vanilla only does that inside its own button handlers).</summary>
        private static void RefreshHostScreen()
        {
            try
            {
                var screen = UnityEngine.Object.FindObjectOfType<HostTournamentScreen>();
                if (screen == null)
                    return;
                bool reg = CPlayerData.m_IsPlayerRegisteredForTournament;
                if (screen.m_PlayerSignUpButton != null)
                    screen.m_PlayerSignUpButton.SetActive(!reg);
                if (screen.m_PlayerSignOutButton != null)
                    screen.m_PlayerSignOutButton.SetActive(reg);
                MiEvaluateSignedUpCountText?.Invoke(screen, null);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TournamentSync.RefreshHostScreen: " + e.Message); }
        }

        private static readonly MethodInfo MiEvaluateSignedUpCountText =
            AccessTools.Method(typeof(HostTournamentScreen), "EvaluateSignedUpCountText");

        // ---------------- client ----------------

        public void ClientApplyState(TournamentStateMessage message)
        {
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(TournamentStateMessage message)
        {
            if (s_planEditing)
                return; // the guest's screen is open on this data; their close sends the plan up
            var td = CPlayerData.m_TournamentData;
            if (td == null)
            {
                CPlayerData.m_TournamentData = td = new TournamentData();
            }

            byte flags = message.Flags;
            td.m_IsHostingTournament = (flags & 1) != 0;
            bool wasDay = td.m_IsTournamentDay;
            bool wasOver = td.m_IsTournamentDayOver;
            td.m_IsTournamentDay = (flags & 2) != 0;
            td.m_IsTournamentDayOver = (flags & 4) != 0;
            td.m_TournamentMaxPlayerCount = message.MaxPlayerCount;
            td.m_TournamentSignedUpCustomerCount = message.SignedUpCustomerCount;
            td.m_TournamentFinishedCurrentRoundCustomerCount = message.FinishedCurrentRoundCustomerCount;
            td.m_TournamentCurrentRound = message.CurrentRound;
            td.m_TournamentMaxRound = message.MaxRound;
            td.m_TournamentFee = message.Fee;
            td.m_TournamentTotalValue = message.TotalValue;

            // the shop's player entry, mirrored so the guest's own table checks and
            // WinLose screen behave like vanilla when the entry is theirs
            byte pf = message.PlayerFlags;
            CPlayerData.m_IsPlayerRegisteredForTournament = (pf & 1) != 0;
            var ptd = CPlayerData.m_PlayerTournamentData;
            if (ptd == null)
                CPlayerData.m_PlayerTournamentData = ptd = new CustomerTournamentData();
            ptd.m_IsTournamentCustomer = (pf & 2) != 0;
            ptd.m_HasFinishCurrentTournamentRound = (pf & 4) != 0;
            ptd.m_HasRegisteredTournamentResult = (pf & 8) != 0;
            ptd.m_IsTournamentWin = (pf & 16) != 0;
            ptd.m_TournamentCustomerPlayTableIndex = message.PlayerTable;
            ptd.m_TournamentCustomerIndex = message.PlayerCustomerIndex;
            ptd.m_TournamentCustomerSortedIndex = message.PlayerSortedIndex;
            s_entryHolder = message.EntryHolder ?? "";
            string me = CoopCore.Instance != null ? CoopCore.Instance.EffectivePlayerName : null;
            s_entryMine = CPlayerData.m_IsPlayerRegisteredForTournament
                && !string.IsNullOrEmpty(me) && s_entryHolder == me;
            s_proxyHolder = message.ProxyHolder ?? "";
            s_proxyMine = !string.IsNullOrEmpty(me) && s_proxyHolder == me;
            s_proxyTable = (message.ProxyFlags & 1) != 0 ? message.ProxyTable : 0;
            s_proxyFinished = (message.ProxyFlags & 2) != 0;
            s_proxyVsPlayer = (message.ProxyFlags & 8) != 0;
            NpcSync.SetParkedCustomer((message.ProxyFlags & 16) != 0 ? message.ProxyNpcIndex : -1); // fv-689
            if (s_proxyMine && s_proxyTable > 0 && td.m_IsTournamentDay && !td.m_IsTournamentDayOver && !s_proxyFinished && s_proxyNoticedTable != s_proxyTable)
            {
                s_proxyNoticedTable = s_proxyTable;
                HostOnlyFeatures.Notice(s_proxyVsPlayer
                    ? $"Tournament: you're at table {s_proxyTable} against {s_entryHolder} - right-click it when they're waiting"
                    : $"Tournament: you're at table {s_proxyTable} (as customer #{message.ProxyCustomerIndex}) - right-click it once both are seated");
            }
            if (!s_proxyMine || s_proxyTable <= 0)
                s_proxyNoticedTable = -1;

            ApplyPrizeSlots(td, message.PrizeSlots);

            // bracket digest
            int n = message.Bracket.Count;
            var digest = new List<PairingEntry>(n);
            for (int i = 0; i < n; i++)
            {
                var be = message.Bracket[i];
                var e = new PairingEntry();
                e.SortedIndex = be.SortedIndex;
                e.ModelIndex = be.ModelIndex;
                byte f = be.Flags;
                e.IsFemale = (f & 1) != 0;
                e.IsWin = (f & 2) != 0;
                e.HasResult = (f & 4) != 0;
                e.WinCount = be.WinCount;
                e.WinPoints = be.WinPoints;
                e.OMW = be.OMW;
                e.OOMW = be.OOMW;
                digest.Add(e);
            }

            // the heal broadcast repeats unchanged state every 15s; skip the UI churn
            // (ShowPairingScreen resets every panel) when nothing actually moved
            int hash = ComputeHash(td);
            for (int i = 0; i < digest.Count; i++)
            {
                var e = digest[i];
                hash = hash * 31 + e.SortedIndex;
                hash = hash * 31 + e.ModelIndex;
                hash = hash * 31 + ((e.IsFemale ? 1 : 0) | (e.IsWin ? 2 : 0) | (e.HasResult ? 4 : 0));
                hash = hash * 31 + e.WinCount;
                hash = hash * 31 + e.WinPoints;
                hash = hash * 31 + e.OMW;
                hash = hash * 31 + e.OOMW;
            }
            if (hash == _clientHash)
                return;
            _clientHash = hash;

            RefreshBoards(td, digest, wasDay != td.m_IsTournamentDay || wasOver != td.m_IsTournamentDayOver);
        }

        public void ClientApplyEntryResult(TournamentEntryResultMessage msg)
        {
            Guarded("entry-result", () =>
            {
                if (!msg.Ok)
                {
                    try
                    {
                        NotEnoughResourceTextPopup.ShowText((ENotEnoughResourceText)msg.Reason);
                    }
                    catch { HostOnlyFeatures.Notice("Co-op: the host refused the tournament entry"); }
                    return;
                }
                if (msg.Proxy || (s_proxyMine && !msg.Registered))
                {
                    // the challenger path: nothing of the player's own record changes
                    s_proxyMine = msg.Proxy;
                    s_proxyNoticedTable = -1;
                    HostOnlyFeatures.Notice(msg.Proxy
                        ? "Co-op: you're in as the CHALLENGER - on tournament day you play as one of the entrants; watch for your table number"
                        : "Co-op: you withdrew as the challenger");
                    CoopPlugin.Log.LogInfo(msg.Proxy ? "TournamentSync: entered the host's tournament as the challenger" : "TournamentSync: withdrew as the challenger");
                    return;
                }
                // repaint now; the mirror confirms within a tick
                CPlayerData.m_IsPlayerRegisteredForTournament = msg.Registered;
                s_entryMine = msg.Registered;
                if (msg.Registered)
                    s_entryHolder = CoopCore.Instance != null ? CoopCore.Instance.EffectivePlayerName : s_entryHolder;
                RefreshHostScreen();
                CoopPlugin.Log.LogInfo(msg.Registered ? "TournamentSync: entered the host's tournament" : "TournamentSync: withdrew from the host's tournament");
            });
        }

        /// <summary>Prize catalog: mutate the vanilla 4-slot list in place so screens that
        /// index m_PrizeDataList[i] never see it shorter than they expect.</summary>
        private static void ApplyPrizeSlots(TournamentData td, List<TournamentPrizeSlot> slots)
        {
            if (td.m_PrizeDataList == null)
                td.m_PrizeDataList = new List<TournamentPrizeDataList>();
            int lists = slots != null ? slots.Count : 0;
            while (td.m_PrizeDataList.Count < lists)
                td.m_PrizeDataList.Add(new TournamentPrizeDataList { m_PrizeDataList = new List<TournamentPrizeData>() });
            for (int i = 0; i < lists; i++)
            {
                var slot = td.m_PrizeDataList[i];
                if (slot.m_PrizeDataList == null)
                    slot.m_PrizeDataList = new List<TournamentPrizeData>();
                slot.m_PrizeDataList.Clear();
                var dtoSlot = slots[i];
                for (int j = 0; j < dtoSlot.Prizes.Count; j++)
                {
                    var pe = dtoSlot.Prizes[j];
                    var p = new TournamentPrizeData();
                    if (pe.HasCard)
                        p.m_CardData = pe.Card;
                    // peer id -> ours (already translated by the DTO deserialize); a prize
                    // from a pack only the other side has becomes EItemType.None and the prize
                    // slot just shows nothing, which is what an unresolvable prize did before
                    // translation existed
                    p.m_ItemType = pe.ItemType;
                    p.m_Count = pe.Count;
                    slot.m_PrizeDataList.Add(p);
                }
            }
        }

        /// <summary>Client: the pairing board and shelf screen mesh are normally driven
        /// by day-start events, which the mod suppresses on the joiner - so we gate them
        /// here, exactly the way TournamentPrizeShelf.CheckTournamentScreenVisibility does.</summary>
        private void RefreshBoards(TournamentData td, List<PairingEntry> digest, bool visibilityChanged)
        {
            var cm = Cm();
            if (cm == null || cm.m_TournamentPairingScreen == null)
                return;
            var screen = cm.m_TournamentPairingScreen;
            bool showBoard = td.m_IsTournamentDay || td.m_IsTournamentDayOver;

            if (visibilityChanged)
            {
                try
                {
                    screen.gameObject.SetActive(showBoard);
                    var shelves = ShelfManager.GetTournamentPrizeShelfList();
                    for (int i = 0; i < shelves.Count; i++)
                    {
                        if (shelves[i] == null || FiScreenMesh == null)
                            continue;
                        var mesh = FiScreenMesh.GetValue(shelves[i]) as GameObject;
                        if (mesh != null)
                            mesh.SetActive(showBoard);
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("TournamentSync board vis: " + e.Message); }
            }
            if (!showBoard)
            {
                screen.ShowPairingScreen(isShow: false, 0);
                return;
            }

            // full repaint: ShowPairingScreen resets the panels, then we repopulate from
            // the digest with fabricated CustomerTournamentData - UpdateCustomerData only
            // reads the scalar fields we carry
            screen.ShowPairingScreen(isShow: true, td.m_TournamentMaxPlayerCount);
            screen.UpdateCurrentRound(td.m_TournamentCurrentRound, td.m_TournamentMaxRound);
            int panels = screen.m_TournamentPairingUIGrpList != null ? screen.m_TournamentPairingUIGrpList.Count : 0;
            for (int i = 0; i < digest.Count; i++)
            {
                var e = digest[i];
                if (e.SortedIndex / 2 >= panels)
                    continue;
                screen.OnCustomerRegisterStart(e.SortedIndex, e.ModelIndex, e.IsFemale);
                var ctd = new CustomerTournamentData
                {
                    m_TournamentCustomerSortedIndex = e.SortedIndex,
                    m_IsTournamentWin = e.IsWin,
                    m_HasRegisteredTournamentResult = e.HasResult,
                    m_TournamentWinCount = e.WinCount,
                    m_TournamentWinPoints = e.WinPoints,
                    m_TournamentOMW = e.OMW,
                    m_TournamentOOMW = e.OOMW,
                };
                screen.m_TournamentPairingUIGrpList[e.SortedIndex / 2].UpdateCustomerData(ctd);
            }
        }

        private struct PairingEntry
        {
            public int SortedIndex;
            public int ModelIndex;
            public bool IsFemale;
            public bool IsWin;
            public bool HasResult;
            public int WinCount;
            public int WinPoints;
            public int OMW;
            public int OOMW;
        }

        // ---------------- wire / hash ----------------

        private static List<TournamentPrizeSlot> BuildPrizeSlots(TournamentData td)
        {
            var slots = new List<TournamentPrizeSlot>();
            var lists = td.m_PrizeDataList;
            int lc = lists != null ? Mathf.Min(lists.Count, 8) : 0;
            for (int i = 0; i < lc; i++)
            {
                var slot = new TournamentPrizeSlot();
                var inner = lists[i] != null ? lists[i].m_PrizeDataList : null;
                int ec = inner != null ? Mathf.Min(inner.Count, 64) : 0;
                for (int j = 0; j < ec; j++)
                {
                    var p = inner[j];
                    bool hasCard = p != null && p.m_CardData != null;
                    slot.Prizes.Add(new TournamentPrizeEntry
                    {
                        HasCard = hasCard,
                        Card = hasCard ? p.m_CardData : null,
                        // item prizes are EItemTypes (a modded id space) - the card above
                        // already goes through the WriteCard chokepoint
                        ItemType = p != null ? p.m_ItemType : (EItemType)0,
                        Count = p != null ? p.m_Count : 0,
                    });
                }
                slots.Add(slot);
            }
            return slots;
        }

        private static TournamentStateMessage BuildState(TournamentData td)
        {
            var msg = new TournamentStateMessage
            {
                Flags = (byte)((td.m_IsHostingTournament ? 1 : 0)
                             | (td.m_IsTournamentDay ? 2 : 0)
                             | (td.m_IsTournamentDayOver ? 4 : 0)),
                MaxPlayerCount = td.m_TournamentMaxPlayerCount,
                SignedUpCustomerCount = td.m_TournamentSignedUpCustomerCount,
                FinishedCurrentRoundCustomerCount = td.m_TournamentFinishedCurrentRoundCustomerCount,
                CurrentRound = td.m_TournamentCurrentRound,
                MaxRound = td.m_TournamentMaxRound,
                Fee = td.m_TournamentFee,
                TotalValue = td.m_TournamentTotalValue,
                EntryHolder = s_entryHolder ?? "",
            };
            var ptd = CPlayerData.m_PlayerTournamentData;
            msg.PlayerFlags = (byte)((CPlayerData.m_IsPlayerRegisteredForTournament ? 1 : 0)
                | ((ptd != null && ptd.m_IsTournamentCustomer) ? 2 : 0)
                | ((ptd != null && ptd.m_HasFinishCurrentTournamentRound) ? 4 : 0)
                | ((ptd != null && ptd.m_HasRegisteredTournamentResult) ? 8 : 0)
                | ((ptd != null && ptd.m_IsTournamentWin) ? 16 : 0));
            msg.PlayerTable = ptd != null ? ptd.m_TournamentCustomerPlayTableIndex : 0;
            msg.PlayerCustomerIndex = ptd != null ? ptd.m_TournamentCustomerIndex : 0;
            msg.PlayerSortedIndex = ptd != null ? ptd.m_TournamentCustomerSortedIndex : 0;

            msg.PrizeSlots = BuildPrizeSlots(td);
            msg.ProxyHolder = s_proxyHolder ?? "";
            var proxyC = s_instance != null ? s_instance._proxyCustomer : null;
            if (proxyC != null)
            {
                try
                {
                    var pctd = proxyC.GetCustomerTournamentData();
                    msg.ProxyTable = pctd.m_TournamentCustomerPlayTableIndex;
                    msg.ProxyCustomerIndex = pctd.m_TournamentCustomerIndex;
                    bool vsPlayer = CPlayerData.m_IsPlayerRegisteredForTournament && ptd != null
                        && ptd.m_TournamentCustomerPlayTableIndex == pctd.m_TournamentCustomerPlayTableIndex && s_instance._entryConn == HostEntry;
                    msg.ProxyFlags = (byte)(1 | (pctd.m_HasFinishCurrentTournamentRound ? 2 : 0) | (pctd.m_IsTournamentWin ? 4 : 0) | (vsPlayer ? 8 : 0));
                }
                catch { }
                // fv-689: parked body -> every client hides the puppet
                if (s_instance._parkedCustomer == proxyC)
                    msg.ProxyFlags |= 16;
                msg.ProxyNpcIndex = s_instance.ProxyNpcIndex();
            }

            // bracket digest straight from the host's live sorted list (the same list
            // the vanilla pairing board renders from)
            var cm = Cm();
            var sorted = cm != null ? cm.m_TournamentSortedCustomerList : null;
            int n = sorted != null ? Mathf.Min(sorted.Count, 64) : 0;
            for (int i = 0; i < n; i++)
            {
                var c = sorted[i];
                var ctd = c != null ? c.GetCustomerTournamentData() : PlayerBracketData();
                if (ctd == null)
                {
                    msg.Bracket.Add(new TournamentBracketEntry());
                    continue;
                }
                // game 1.0: the HOST can enter their own tournament. Vanilla keeps a null in
                // the sorted list for that seat and renders it from CPlayerData's
                // m_PlayerTournamentData with model index -1 (the player icon). Send exactly
                // that, so the guest's board paints the host where vanilla would.
                msg.Bracket.Add(new TournamentBracketEntry
                {
                    SortedIndex = (byte)Mathf.Clamp(ctd.m_TournamentCustomerSortedIndex, 0, 255),
                    ModelIndex = c != null ? c.GetCustomerModelIndex() : -1,
                    Flags = (byte)(((c != null && c.m_IsFemale) ? 1 : 0)
                                 | (ctd.m_IsTournamentWin ? 2 : 0)
                                 | (ctd.m_HasRegisteredTournamentResult ? 4 : 0)),
                    WinCount = ctd.m_TournamentWinCount,
                    WinPoints = ctd.m_TournamentWinPoints,
                    OMW = ctd.m_TournamentOMW,
                    OOMW = ctd.m_TournamentOOMW,
                });
            }
            return msg;
        }

        /// <summary>The host's own competitor record when they joined the tournament (game
        /// 1.0), else null. Only meaningful for a null seat in the sorted list - see
        /// CustomerManager's AddTournamentCustomer(null, ...) and
        /// TournamentPairingScreen.RefreshAllCustomerData.</summary>
        private static CustomerTournamentData PlayerBracketData()
        {
            if (!CPlayerData.m_IsPlayerRegisteredForTournament)
                return null;
            var ptd = CPlayerData.m_PlayerTournamentData;
            return (ptd != null && ptd.m_IsTournamentCustomer) ? ptd : null;
        }

        /// <summary>Change detector over everything BuildState sends. The host also folds
        /// in the live bracket; the client re-derives the same shape from the payload.</summary>
        private static int ComputeHash(TournamentData td)
        {
            int hash = 17;
            hash = hash * 31 + ((td.m_IsHostingTournament ? 1 : 0)
                              | (td.m_IsTournamentDay ? 2 : 0)
                              | (td.m_IsTournamentDayOver ? 4 : 0));
            hash = hash * 31 + td.m_TournamentMaxPlayerCount;
            hash = hash * 31 + td.m_TournamentSignedUpCustomerCount;
            hash = hash * 31 + td.m_TournamentFinishedCurrentRoundCustomerCount;
            hash = hash * 31 + td.m_TournamentCurrentRound;
            hash = hash * 31 + td.m_TournamentMaxRound;
            hash = hash * 31 + (int)(td.m_TournamentFee * 100f);
            hash = hash * 31 + (int)(td.m_TournamentTotalValue * 100f);
            var ptd = CPlayerData.m_PlayerTournamentData;
            hash = hash * 31 + ((CPlayerData.m_IsPlayerRegisteredForTournament ? 1 : 0)
                | ((ptd != null && ptd.m_IsTournamentCustomer) ? 2 : 0)
                | ((ptd != null && ptd.m_HasFinishCurrentTournamentRound) ? 4 : 0)
                | ((ptd != null && ptd.m_HasRegisteredTournamentResult) ? 8 : 0)
                | ((ptd != null && ptd.m_IsTournamentWin) ? 16 : 0));
            hash = hash * 31 + (ptd != null ? ptd.m_TournamentCustomerPlayTableIndex : 0);
            hash = hash * 31 + (ptd != null ? ptd.m_TournamentCustomerIndex : 0);
            hash = hash * 31 + (ptd != null ? ptd.m_TournamentCustomerSortedIndex : 0);
            hash = hash * 31 + (s_entryHolder ?? "").GetHashCode();
            hash = hash * 31 + (s_proxyHolder ?? "").GetHashCode();
            hash = hash * 31 + ProxyTable();
            hash = hash * 31 + (ProxyFinishedRound() ? 1 : 0);
            hash = hash * 31 + (ProxyParked ? 1 : 0); // fv-689
            var lists = td.m_PrizeDataList;
            if (lists != null)
            {
                for (int i = 0; i < lists.Count; i++)
                {
                    var inner = lists[i] != null ? lists[i].m_PrizeDataList : null;
                    if (inner == null)
                        continue;
                    for (int j = 0; j < inner.Count; j++)
                    {
                        var p = inner[j];
                        if (p == null)
                            continue;
                        hash = hash * 31 + (int)p.m_ItemType;
                        hash = hash * 31 + p.m_Count;
                        if (p.m_CardData != null)
                        {
                            hash = hash * 31 + (int)p.m_CardData.expansionType;
                            hash = hash * 31 + (int)p.m_CardData.monsterType;
                            hash = hash * 31 + (int)p.m_CardData.borderType;
                            hash = hash * 31 + ((p.m_CardData.isFoil ? 1 : 0) | (p.m_CardData.isDestiny ? 2 : 0));
                        }
                    }
                }
            }
            // host side only: fold the live bracket so round results retrigger a send
            if (CoopCore.Role == CoopRole.Host)
            {
                var cm = Cm();
                var sorted = cm != null ? cm.m_TournamentSortedCustomerList : null;
                if (sorted != null)
                {
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        var ctd = sorted[i] != null ? sorted[i].GetCustomerTournamentData() : PlayerBracketData();
                        if (ctd == null)
                            continue;
                        hash = hash * 31 + ctd.m_TournamentCustomerSortedIndex;
                        hash = hash * 31 + (sorted[i] != null ? sorted[i].GetCustomerModelIndex() : -1);
                        hash = hash * 31 + (((sorted[i] != null && sorted[i].m_IsFemale) ? 1 : 0)
                                          | (ctd.m_IsTournamentWin ? 2 : 0)
                                          | (ctd.m_HasRegisteredTournamentResult ? 4 : 0));
                        hash = hash * 31 + ctd.m_TournamentWinCount;
                        hash = hash * 31 + ctd.m_TournamentWinPoints;
                        hash = hash * 31 + ctd.m_TournamentOMW;
                        hash = hash * 31 + ctd.m_TournamentOOMW;
                    }
                }
            }
            return hash;
        }
    }
}
