using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using HarmonyLib;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Lets the GUEST play the game-1.0 card battle against a waiting customer.
    ///
    /// Why this is small: the battle engine (PlayTableGame / PlayCardSet) is entirely local.
    /// The opponent is an AI PlayCardSet dealt from the built-in AI decks, not from the
    /// customer's collection; the player's deck comes from CPlayerData, which the guest has
    /// because it downloaded the host's save at join. The engine touches the real Customer
    /// in exactly two places, both through the table's occupied-customer list and both
    /// null-guarded (InteractablePlayTable.StartPlayerCardGame, PlayTableGame.FinishLeaveGame),
    /// and the guest's table has no occupied customers (puppets never CustomerHasReached).
    /// So the guest can run the whole battle on its own machine; only the SEAT is shared
    /// state, and the seat is three messages:
    ///
    ///   guest RMB on a table  -> BattleSit(table)            (HostOnlyFeatures routes it here)
    ///   host validates like OnRightMouseButtonUp, books the free seat for the guest,
    ///   calls StartPlayerCardGame so the customer sits and shows their card fan,
    ///                          <- BattleSitResult(table, granted, side | reason)
    ///   guest books the same seat locally and calls PlayCardGameManager.SetPlayTable:
    ///   the vanilla battle starts on the guest.
    ///   guest's battle ends   -> BattleExit(table, win, draw)  (postfix on ExitPlayerCardGame)
    ///   host runs table.ExitPlayerCardGame: StopTableGame stands the customer up, frees
    ///   the seat, and the table digest heals the visuals on every peer.
    ///
    /// The host's own RMB on that table meanwhile hits vanilla's "already playing" branch
    /// (both seats booked), and PlayTableSync's kick refusal covers it via
    /// GetHasStartPlayerPlayCard. A guest that disconnects mid-battle is stood up by
    /// HostReleaseConn. Tournament tables are refused: the tournament bracket is host
    /// bookkeeping that ExitPlayerCardGame would write on the wrong machine.
    ///
    /// Won gift packs spawn on the guest's board and go into the guest's hand through
    /// vanilla's TakeEndGameGiftItem; from there they are ordinary held items. The other
    /// players watch the guest's board through <see cref="BattleSync"/> (client digest,
    /// relayed by the host).
    /// </summary>
    public sealed class GuestBattle
    {
        public Action<INetMessage> SendToHost;                 // client side
        public Action<int, INetMessage> SendToClient;          // host side

        private static readonly FieldInfo FiPlayerOccupied = Util.ReflectionSurface.OptionalField(typeof(InteractablePlayTable), "m_IsPlayerOccupied");

        // host: which table/seat each guest is sitting at
        private readonly Dictionary<int, (byte table, int seat)> _guestSeats = new Dictionary<int, (byte, int)>();
        // client: the table we asked for / are playing at
        private int _pendingTable = -1;
        private float _pendingSince;
        private int _playingTable = -1;

        private static GuestBattle _active;
        public GuestBattle()
        {
            _active = this;
        }

        public static void ApplyPatches(Harmony h)
        {
            var pad = AccessTools.Method(typeof(ShelfManager), "UpdatePlayTableSaveData");
            if (pad == null)
                CoopPlugin.Log.LogWarning("GuestBattle patch target missing: ShelfManager.UpdatePlayTableSaveData");
            else
                h.Patch(pad, prefix: new HarmonyMethod(typeof(GuestBattle), nameof(PadPlayTableSaveDataPrefix)));
            var exit = AccessTools.Method(typeof(InteractablePlayTable), "ExitPlayerCardGame");
            if (exit == null)
                CoopPlugin.Log.LogWarning("GuestBattle patch target missing: InteractablePlayTable.ExitPlayerCardGame");
            else
                h.Patch(exit, postfix: new HarmonyMethod(typeof(GuestBattle), nameof(ExitPlayerCardGamePostfix)));
        }

        private static ShelfManager Sm() => CSingleton<ShelfManager>.Instance;

        /// <summary>ShelfManager.UpdatePlayTableSaveData indexes CPlayerData.m_PlayTableSaveDataList
        /// by the table's position in m_PlayTableList, but that save list is only rebuilt when
        /// the game SAVES. A play table placed since the last save has no entry, so the first
        /// UpdatePlayTableSaveData on it throws ArgumentOutOfRange - inside CustomerHasReached
        /// (customer sits), StopTableGame, and SetPlayerTableNumberScreen.OnPressConfirm, which
        /// then never reaches CloseScreen. Vanilla hides it behind frequent autosaves; a co-op
        /// guest never saves, and the host may not have saved since placing the table (seen
        /// 2026-09-16: 7 host + 9 guest crashes, tournament-number screen dead on both). Pad the
        /// list to the table count with blank entries; the next save rewrites them all.</summary>
        public static void PadPlayTableSaveDataPrefix(ShelfManager __instance)
        {
            try
            {
                var tables = __instance != null ? __instance.m_PlayTableList : null;
                if (tables == null)
                    return;
                var save = CPlayerData.m_PlayTableSaveDataList;
                if (save == null)
                    CPlayerData.m_PlayTableSaveDataList = save = new List<PlayTableSaveData>();
                bool padded = false;
                while (save.Count < tables.Count)
                {
                    var t = tables[save.Count];
                    var d = new PlayTableSaveData();
                    if (t != null)
                    {
                        d.objectType = t.m_ObjectType;
                        d.isSeatOccupied = t.GetIsSeatOccupied();
                        d.isPlayerSeat = t.GetIsPlayerSeat();
                    }
                    save.Add(d);
                    padded = true;
                }
                if (padded)
                    CoopPlugin.Log.LogInfo($"play table save list padded to {save.Count} (table placed since the last save)");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("PadPlayTableSaveData: " + e.Message);
            }
        }

        internal static int IndexOf(InteractablePlayTable table)
        {
            var sm = Sm();
            return (sm != null && sm.m_PlayTableList != null && table != null) ? sm.m_PlayTableList.IndexOf(table) : -1;
        }

        internal static InteractablePlayTable TableAt(int index)
        {
            var sm = Sm();
            var list = sm != null ? sm.m_PlayTableList : null;
            return (list != null && index >= 0 && index < list.Count) ? list[index] : null;
        }

        // ================================================================ client

        /// <summary>Called from HostOnlyFeatures' RMB prefix on the client. Returns false
        /// when the request could not even be sent (caller shows its notice).</summary>
        public static bool ClientRequestSit(InteractablePlayTable table)
        {
            var me = _active;
            if (me == null || CoopCore.Role != CoopRole.Client || me.SendToHost == null)
                return false;
            try
            {
                if (me._playingTable >= 0)
                    return true; // already in a battle; vanilla would have refused too
                int index = IndexOf(table);
                if (index < 0 || index > 255)
                    return false;
                // vanilla's own pre-checks that need no host knowledge (the tournament-day
                // rules run on the host against the mirrored entry; only the obvious
                // "not entered" case is answered here)
                var td = CPlayerData.m_TournamentData;
                bool dayOn = td != null && td.m_IsTournamentDay && !td.m_IsTournamentDayOver;
                if (dayOn && !TournamentSync.ClientHoldsEntry() && !TournamentSync.ClientIsProxy())
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.TournamentInProgress);
                    return true;
                }
                // R4: the challenger's round against the shop's player is PvP, not a customer battle
                if (dayOn && TournamentSync.ClientIsProxy() && TournamentSync.ClientProxyVsPlayer()
                    && table.GetTournamentPlayTableNumber() == TournamentSync.ClientProxyTable())
                {
                    if (!PvpBattle.ClientRequestPvp(table, index))
                        HostOnlyFeatures.Notice("Co-op: waiting for " + "the shop's player" + " to sit at this table");
                    return true;
                }
                if (CPlayerData.m_CurrentSelectedDeckIndex >= CPlayerData.m_DeckCompactCardDataList.Count
                    || CPlayerData.m_DeckCompactCardDataList[CPlayerData.m_CurrentSelectedDeckIndex].GetTotalCardCount() < GameInstance.GetMaxDeckCardCount())
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.DeckIncomplete);
                    return true;
                }
                if (me._pendingTable >= 0 && UnityEngine.Time.unscaledTime - me._pendingSince < 3f)
                    return true; // request in flight
                // nobody at the table: that is a request to play the HOST, not a customer
                var cust = table.GetOccupiedCustomerList();
                bool anyCustomer = cust != null && ((cust.Count > 0 && cust[0] != null) || (cust.Count > 1 && cust[1] != null));
                if (!anyCustomer && PvpBattle.ClientRequestPvp(table, index))
                    return true;
                me._pendingTable = index;
                me._pendingSince = UnityEngine.Time.unscaledTime;
                me.SendToHost(new BattleSitMessage { TableIndex = (byte)index });
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GuestBattle sit request: " + e.Message);
                return false;
            }
        }

        public void ClientApplySitResult(BattleSitResultMessage msg)
        {
            try
            {
                if (_pendingTable != msg.TableIndex)
                {
                    // not what we asked for (or we gave up waiting): release rather than strand
                    if (msg.Granted)
                        SendToHost?.Invoke(new BattleExitMessage { TableIndex = msg.TableIndex, PlayerWin = false, Draw = false });
                    return;
                }
                _pendingTable = -1;
                if (!msg.Granted)
                {
                    try
                    {
                        NotEnoughResourceTextPopup.ShowText((ENotEnoughResourceText)msg.Reason);
                    }
                    catch { }
                    return;
                }
                var table = TableAt(msg.TableIndex);
                var ptg = PlayCardGame.Game();
                int seat = msg.SideA ? 0 : 1;
                string refuse = null;
                if (table == null)
                    refuse = "table not found locally";
                else if (ptg == null)
                    refuse = "PlayTableGame not available on this client";
                else if (ptg.IsPlayTableGameMode())
                    refuse = "already in a battle";
                if (refuse == null)
                {
                    BookSeat(table, seat, true);
                    _playingTable = msg.TableIndex;
                    CoopPlugin.Log.LogInfo($"GuestBattle: seat granted at table {msg.TableIndex} side {(msg.SideA ? "A" : "B")}");
                    // vanilla's entry, but through the resolved manager - never the CSingleton
                    // accessor (see PlayCardGame): m_CanPlayTableMode false or an incomplete
                    // deck make it return without entering battle mode
                    ptg.SetPlayTable(table, msg.SideA);
                    if (!ptg.IsPlayTableGameMode())
                        refuse = "SetPlayTable refused (play-table mode off, or deck incomplete)";
                }
                if (refuse != null)
                {
                    // the host already booked the seat and told the customer the game started:
                    // hand it back, or she sits there 'playing' alone and every retry is refused
                    CoopPlugin.Log.LogWarning($"GuestBattle: could not start at table {msg.TableIndex}: {refuse} - releasing the seat");
                    _playingTable = -1;
                    if (table != null)
                        BookSeat(table, seat, false);
                    SendToHost?.Invoke(new BattleExitMessage { TableIndex = msg.TableIndex, PlayerWin = false, Draw = false });
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GuestBattle sit result: " + e.Message);
            }
        }

        /// <summary>Vanilla ends every battle through InteractablePlayTable.ExitPlayerCardGame
        /// (win, loss, draw, quit via SetPlayerTableNumberScreen). On the guest that only
        /// resets the local table; tell the host so the real customer stands up.</summary>
        public static void ExitPlayerCardGamePostfix(InteractablePlayTable __instance, bool isPlayerWin, bool isDraw)
        {
            var me = _active;
            if (me == null || CoopCore.Role != CoopRole.Client)
                return;
            try
            {
                int index = IndexOf(__instance);
                if (index < 0 || index != me._playingTable)
                    return;
                me._playingTable = -1;
                me.SendToHost?.Invoke(new BattleExitMessage { TableIndex = (byte)index, PlayerWin = isPlayerWin, Draw = isDraw });
                CoopPlugin.Log.LogInfo($"GuestBattle: battle at table {index} ended (win={isPlayerWin} draw={isDraw})");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GuestBattle exit: " + e.Message);
            }
        }

        // ================================================================ host

        public void HostApplySit(BattleSitMessage msg, int connId)
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            var table = TableAt(msg.TableIndex);
            int reason = 0;
            bool sideA = true;
            try
            {
                var td = CPlayerData.m_TournamentData;
                bool tournamentDay = td != null && td.m_IsTournamentDay && !td.m_IsTournamentDayOver;
                var ptd = CPlayerData.m_PlayerTournamentData;
                if (table == null)
                    reason = (int)ENotEnoughResourceText.SitPlaytableNoOtherPlayer;
                // R4: the challenger takes its NPC's seat once both NPCs are sitting; the table's
                // own timer stops and waits for the human's result instead of rolling a coin
                else if (tournamentDay && TournamentSync.IsProxy(connId))
                {
                    var proxy = TournamentSync.ProxyCustomer;
                    int pt = TournamentSync.ProxyTable();
                    if (proxy == null)
                        reason = (int)ENotEnoughResourceText.TournamentInProgress;
                    else if (proxy.GetCustomerTournamentData().m_HasFinishCurrentTournamentRound)
                        reason = (int)ENotEnoughResourceText.WaitNextRoundTournament;
                    else if (table.GetTournamentPlayTableNumber() != pt)
                        reason = (int)ENotEnoughResourceText.PlayAtWrongTableNumber;
                    else if (table.GetHasStartPlayerPlayCard())
                        reason = (int)ENotEnoughResourceText.SitPlaytableAlreadyPlaying;
                    else
                    {
                        var occ = table.GetOccupiedCustomerList();
                        int mySeat = -1, others = 0;
                        for (int i = 0; occ != null && i < occ.Count; i++)
                        {
                            if (occ[i] == proxy)
                                mySeat = i;
                            else if (occ[i] != null)
                                others++;
                        }
                        if (mySeat < 0 || others == 0 || !table.m_HasStartPlay)
                            reason = (int)ENotEnoughResourceText.SitPlaytableNoOtherPlayer; // not both seated yet
                        else
                        {
                            table.m_HasStartPlay = false; // no coin flip: the human's result decides
                            sideA = mySeat == 0;
                            _guestSeats.Remove(connId);
                            BookSeat(table, mySeat, true);
                            try
                            {
                                table.StartPlayerCardGame();
                            }
                            catch (Exception e) { CoopPlugin.Log.LogWarning("GuestBattle proxy start: " + e.Message); }
                            _guestSeats[connId] = (msg.TableIndex, mySeat);
                            CoopPlugin.Log.LogInfo($"GuestBattle: challenger {connId} plays its round at table {msg.TableIndex} seat {mySeat}");
                            SendToClient?.Invoke(connId, new BattleSitResultMessage { TableIndex = msg.TableIndex, Granted = true, SideA = sideA, Reason = 0 });
                            return;
                        }
                    }
                }
                // tournament day: vanilla's own rules for "the player", which is this guest
                // only when they hold the shop's entry (TournamentSync)
                else if (tournamentDay && !TournamentSync.GuestHoldsEntry(connId))
                    reason = (int)ENotEnoughResourceText.TournamentInProgress;
                else if (tournamentDay && ptd != null && ptd.m_HasRegisteredTournamentResult)
                    reason = (int)ENotEnoughResourceText.WaitNextRoundTournament;
                else if (tournamentDay && (ptd == null || ptd.m_TournamentCustomerPlayTableIndex != table.GetTournamentPlayTableNumber()))
                    reason = (int)ENotEnoughResourceText.PlayAtWrongTableNumber;
                else if (_guestSeats.TryGetValue(connId, out var held)
                         && TableAt(held.table) != null && TableAt(held.table).GetHasStartPlayerPlayCard())
                    reason = (int)ENotEnoughResourceText.SitPlaytableAlreadyPlaying;
                else if (table.GetHasStartPlayerPlayCard())
                    reason = (int)ENotEnoughResourceText.SitPlaytableAlreadyPlaying;
                else
                {
                    var occ = table.m_IsSeatOccupied;
                    bool a = occ != null && occ.Count > 0 && occ[0];
                    bool b = occ != null && occ.Count > 1 && occ[1];
                    if (a && b)
                        reason = (int)ENotEnoughResourceText.SitPlaytableAlreadyPlaying;
                    else if (!a && !b)
                        reason = (int)ENotEnoughResourceText.SitPlaytableNoOtherPlayer;
                    else
                        sideA = !a; // take the free seat: seat 0 is side A
                }
                if (reason == 0)
                {
                    _guestSeats.Remove(connId); // a stale entry (game ended without an exit) is replaced
                    int seat = sideA ? 0 : 1;
                    BookSeat(table, seat, true);
                    try
                    {
                        table.StartPlayerCardGame();
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("GuestBattle start: " + e.Message); }
                    _guestSeats[connId] = (msg.TableIndex, seat);
                    CoopPlugin.Log.LogInfo($"GuestBattle: guest {connId} seated at table {msg.TableIndex} side {(sideA ? "A" : "B")}");
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GuestBattle host sit: " + e.Message);
                reason = (int)ENotEnoughResourceText.SitPlaytableNoOtherPlayer;
            }
            SendToClient?.Invoke(connId, new BattleSitResultMessage
            {
                TableIndex = msg.TableIndex,
                Granted = reason == 0,
                SideA = sideA,
                Reason = reason
            });
        }

        public void HostApplyExit(BattleExitMessage msg, int connId)
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            if (!_guestSeats.TryGetValue(connId, out var seat) || seat.table != msg.TableIndex)
                return;
            _guestSeats.Remove(connId);
            var table = TableAt(msg.TableIndex);
            if (table == null)
                return;
            try
            {
                // R4: the challenger's result is what the table writes into the bracket
                var tdx = CPlayerData.m_TournamentData;
                if (tdx != null && tdx.m_IsTournamentDay && !tdx.m_IsTournamentDayOver && TournamentSync.IsProxy(connId))
                    TournamentSync.SetProxyResult(msg.PlayerWin, msg.Draw);
                table.ExitPlayerCardGame(msg.PlayerWin, msg.Draw);
                CoopPlugin.Log.LogInfo($"GuestBattle: guest {connId} left table {msg.TableIndex} (win={msg.PlayerWin} draw={msg.Draw})");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GuestBattle host exit: " + e.Message);
            }
        }

        /// <summary>Host / single player: stand every table down that has a player seat booked
        /// or a player game flagged, and forget every guest seat. The cheat menu's eviction.</summary>
        public static int HostFreeAllTables()
        {
            var me = _active;
            int n = 0;
            var sm = Sm();
            var list = sm != null ? sm.m_PlayTableList : null;
            if (list == null)
                return 0;
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t == null)
                    continue;
                bool playerSeat = false;
                var ps = t.GetIsPlayerSeat();
                if (ps != null)
                    for (int s = 0; s < ps.Count; s++)
                        playerSeat |= ps[s];
                if (!playerSeat && !t.GetHasStartPlayerPlayCard())
                    continue;
                try
                {
                    t.ExitPlayerCardGame(isPlayerWin: false, isDraw: false);
                    n++;
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("GuestBattle free table: " + e.Message); }
            }
            if (me != null)
                me._guestSeats.Clear();
            return n;
        }

        /// <summary>A guest dropped mid-battle: stand the customer up and free the seat.</summary>
        public void HostReleaseConn(int connId)
        {
            if (!_guestSeats.TryGetValue(connId, out var seat))
                return;
            _guestSeats.Remove(connId);
            var table = TableAt(seat.table);
            if (table == null)
                return;
            try
            {
                table.ExitPlayerCardGame(isPlayerWin: false, isDraw: false);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GuestBattle release: " + e.Message); }
        }

        public void Reset()
        {
            _guestSeats.Clear();
            _pendingTable = -1;
            _playingTable = -1;
        }

        // ================================================================ shared

        /// <summary>The seat flags vanilla's OnRightMouseButtonUp writes when the player sits
        /// (minus PlayCardGameManager.SetPlayTable), or their inverse to give the seat back.</summary>
        internal static void BookSeat(InteractablePlayTable table, int seat, bool book)
        {
            try
            {
                if (FiPlayerOccupied != null)
                    FiPlayerOccupied.SetValue(table, book);
                Set(table.m_IsSeatOccupied, seat, book);
                Set(table.m_IsSeatBooked, seat, book);
                Set(table.m_IsQueueOccupied, seat, book);
                Set(table.m_IsPlayerSeat, seat, book);
                var sm = Sm();
                if (sm != null)
                    sm.UpdatePlayTableSaveData(table);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GuestBattle seat flags: " + e.Message);
            }
        }

        private static void Set(List<bool> list, int i, bool v)
        {
            if (list != null && i >= 0 && i < list.Count)
                list[i] = v;
        }
    }
}
