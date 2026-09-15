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
    /// vanilla's TakeEndGameGiftItem; from there they are ordinary held items. Not mirrored:
    /// the host does not see the guest's board (BattleSync is host-authored only) - only
    /// the guest's puppet seated at an occupied table.
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
        public GuestBattle() { _active = this; }

        public static void ApplyPatches(Harmony h)
        {
            var exit = AccessTools.Method(typeof(InteractablePlayTable), "ExitPlayerCardGame");
            if (exit == null)
                CoopPlugin.Log.LogWarning("GuestBattle patch target missing: InteractablePlayTable.ExitPlayerCardGame");
            else
                h.Patch(exit, postfix: new HarmonyMethod(typeof(GuestBattle), nameof(ExitPlayerCardGamePostfix)));
        }

        private static ShelfManager Sm() => CSingleton<ShelfManager>.Instance;

        private static int IndexOf(InteractablePlayTable table)
        {
            var sm = Sm();
            return (sm != null && sm.m_PlayTableList != null && table != null) ? sm.m_PlayTableList.IndexOf(table) : -1;
        }

        private static InteractablePlayTable TableAt(int index)
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
                // vanilla's own pre-checks that need no host knowledge
                if (table.GetIsTournamentPlayTable())
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.TournamentInProgress);
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
                    return; // stale
                _pendingTable = -1;
                if (!msg.Granted)
                {
                    try { NotEnoughResourceTextPopup.ShowText((ENotEnoughResourceText)msg.Reason); } catch { }
                    return;
                }
                var table = TableAt(msg.TableIndex);
                var game = CSingleton<PlayCardGameManager>.Instance;
                if (table == null || game == null || game.m_PlayTableGame == null)
                    return;
                if (game.m_PlayTableGame.IsPlayTableGameMode())
                    return;
                int seat = msg.SideA ? 0 : 1;
                BookSeat(table, seat, true);
                _playingTable = msg.TableIndex;
                CoopPlugin.Log.LogInfo($"GuestBattle: seat granted at table {msg.TableIndex} side {(msg.SideA ? "A" : "B")}");
                PlayCardGameManager.SetPlayTable(table, msg.SideA);
                if (!game.m_PlayTableGame.IsPlayTableGameMode())
                {
                    // SetPlayTable refused locally (m_CanPlayTableMode false, deck changed
                    // under us); give the seat back so the customer is not left waiting
                    _playingTable = -1;
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
                if (table == null)
                    reason = (int)ENotEnoughResourceText.SitPlaytableNoOtherPlayer;
                else if (table.GetIsTournamentPlayTable())
                    reason = (int)ENotEnoughResourceText.TournamentInProgress;
                else if (_guestSeats.ContainsKey(connId))
                    reason = (int)ENotEnoughResourceText.SitPlaytableAlreadyPlaying;
                else if (table.GetHasStartPlayerPlayCard())
                    reason = (int)ENotEnoughResourceText.SitPlaytableAlreadyPlaying;
                else
                {
                    var occ = table.m_IsSeatOccupied;
                    bool a = occ != null && occ.Count > 0 && occ[0];
                    bool b = occ != null && occ.Count > 1 && occ[1];
                    if (a && b) reason = (int)ENotEnoughResourceText.SitPlaytableAlreadyPlaying;
                    else if (!a && !b) reason = (int)ENotEnoughResourceText.SitPlaytableNoOtherPlayer;
                    else sideA = !a; // take the free seat: seat 0 is side A
                }
                if (reason == 0)
                {
                    int seat = sideA ? 0 : 1;
                    BookSeat(table, seat, true);
                    try { table.StartPlayerCardGame(); } catch (Exception e) { CoopPlugin.Log.LogWarning("GuestBattle start: " + e.Message); }
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
                TableIndex = msg.TableIndex, Granted = reason == 0, SideA = sideA, Reason = reason
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
                table.ExitPlayerCardGame(msg.PlayerWin, msg.Draw);
                CoopPlugin.Log.LogInfo($"GuestBattle: guest {connId} left table {msg.TableIndex} (win={msg.PlayerWin} draw={msg.Draw})");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GuestBattle host exit: " + e.Message);
            }
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
            try { table.ExitPlayerCardGame(isPlayerWin: false, isDraw: false); }
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
        private static void BookSeat(InteractablePlayTable table, int seat, bool book)
        {
            try
            {
                if (FiPlayerOccupied != null) FiPlayerOccupied.SetValue(table, book);
                Set(table.m_IsSeatOccupied, seat, book);
                Set(table.m_IsSeatBooked, seat, book);
                Set(table.m_IsQueueOccupied, seat, book);
                Set(table.m_IsPlayerSeat, seat, book);
                var sm = Sm();
                if (sm != null) sm.UpdatePlayTableSaveData(table);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GuestBattle seat flags: " + e.Message);
            }
        }

        private static void Set(List<bool> list, int i, bool v)
        {
            if (list != null && i >= 0 && i < list.Count) list[i] = v;
        }
    }
}
