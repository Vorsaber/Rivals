using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Host-versus-guest card battles (game 1.0). EXPERIMENTAL.
    ///
    /// The engine (<see cref="PlayTableGame"/> + two <see cref="PlayCardSet"/>s) only knows
    /// "the player" and "the enemy", and the enemy is driven by an AI. Lockstep PvP: BOTH PCs
    /// run the whole engine. Each human drives their own player set as vanilla; the enemy set
    /// has its AI switched off and is fed the other human's actions from a queue, applied under
    /// the same "may act now" gate the AI uses. The three random sites that change game state
    /// (deck shuffle, the AI-side search pick, who goes first) run under a per-match seed keyed
    /// by whose deck it is, so both engines make identical decisions; every other Random call
    /// in the engine is cosmetic (sounds, the AI deck's cosmetic borders). The enemy set's deck
    /// is swapped for the remote player's real deck at setup.
    ///
    /// Entry: the host right-clicks an empty play table to wait; the guest right-clicks the
    /// same table to start. Ends like any battle - each side's own exit frees its seat; a
    /// mid-game quit hands the other side the win.
    ///
    /// Divergence: a state hash (turn, both HPs, both hand sizes) is exchanged every 2s and a
    /// mismatch is logged loudly; an action that cannot be applied (card not in hand) is logged
    /// and dropped. There is no rollback - if the engines disagree, leave the table.
    /// </summary>
    public sealed class PvpBattle : TickableCoopModule
    {
        public Action<INetMessage> SendToHost;              // client
        public Action<int, INetMessage> SendToClient;       // host
        public Func<int, string> PeerName;                  // host

        public static bool Active
        {
            get; private set;
        }
        private static bool s_isHostPc;
        private static int s_seed;
        private static int s_table = -1;
        private static string s_opponent = "";
        private static List<CardData> s_remoteDeck;
        private static bool s_applying;                     // replaying a remote action: don't capture
        private static bool s_enemyMulliganWanted;          // DelayStart reached the enemy mulligan
        private static bool s_enemySearchPending;           // enemy set is waiting on a search pick
        private static readonly Queue<PvpActionMessage> s_queue = new Queue<PvpActionMessage>();
        private static readonly int[,] s_rngCounter = new int[3, 8];
        private static int s_seq;
        private static float s_hashTimer;
        private static float s_queueStuckSince = -1f;
        private static PvpBattle s_instance;

        // host bookkeeping
        private int _hostWaitingTable = -1;
        private int _peerConn = -1;

        public override string Name => nameof(PvpBattle);

        public PvpBattle()
        {
            s_instance = this;
        }

        // ---------------------------------------------------------------- reflection

        private static readonly MethodInfo MiCanTakeAction = AccessTools.Method(typeof(PlayCardSet), "CanTakeAction");
        private static readonly MethodInfo MiEvaluatePlace = AccessTools.Method(typeof(PlayCardSet), "EvaluatePlaceCardOnElementArea");
        private static readonly MethodInfo MiRemoveFromHold = AccessTools.Method(typeof(PlayCardSet), "RemoveFromHoldCard");
        private static readonly FieldInfo FiStopping = AccessTools.Field(typeof(PlayCardSet), "m_IsStoppingAction");
        private static readonly FieldInfo FiConfirmedResolve = AccessTools.Field(typeof(PlayCardSet), "m_IsConfirmedActionResolve");
        private static readonly FieldInfo FiSearchedLastTurn = AccessTools.Field(typeof(PlayCardSet), "m_SearchedLastTurn");
        private static readonly FieldInfo FiDiscardedTrigger = AccessTools.Field(typeof(PlayCardSet), "m_CardDiscardedAmountCurrentTrigger");
        private static readonly FieldInfo FiDragMonster = AccessTools.Field(typeof(PlayCardSet), "m_CurrentDragCardMonsterData");
        private static readonly FieldInfo FiHasSelectedTurn = AccessTools.Field(typeof(PlayTableGame), "m_HasSelectedTurnStartCard");
        private static readonly FieldInfo FiIsPlayerTurn = AccessTools.Field(typeof(PlayTableGame), "m_IsPlayerTurn");
        private static readonly FieldInfo FiHasEnemyMulligan = AccessTools.Field(typeof(PlayTableGame), "m_HasEnemyMulligan");
        private static readonly FieldInfo FiShowingDiscard = AccessTools.Field(typeof(PlayTableGame), "m_IsShowingDiscardPile");
        private static readonly FieldInfo FiGiftType = AccessTools.Field(typeof(PlayTableGame), "m_EndGameGiftItemType");

        public static void ApplyPatches(Harmony h)
        {
            // entry
            Try(h, typeof(InteractablePlayTable), "OnRightMouseButtonUp",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(HostTableClickPrefix)));
            // engine: AI off, remote deck in, seeded randomness
            Try(h, typeof(PlayCardSet), "EvaluateEnemyAI",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(EnemyAiPrefix)));
            Try(h, typeof(PlayCardSet), "ResetBoard",
                postfix: new HarmonyMethod(typeof(PvpBattle), nameof(ResetBoardPostfix)));
            Try(h, typeof(PlayCardSet), "ShuffleDeck",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(ShufflePrefix)),
                postfix: new HarmonyMethod(typeof(PvpBattle), nameof(RngRestorePostfix)));
            Try(h, typeof(PlayTableGame), "OnPressTurnSelectCard",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(TurnSelectPrefix)),
                postfix: new HarmonyMethod(typeof(PvpBattle), nameof(TurnSelectPostfix)));
            Try(h, typeof(PlayCardSet), "Update",
                postfix: new HarmonyMethod(typeof(PvpBattle), nameof(SetUpdatePostfix)));
            // the remote human's decisions replace the AI's
            Try(h, typeof(PlayTableGame), "SelectionMulliganOrKeep",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(MulliganPrefix)));
            Try(h, typeof(PlayCardSet), "ActionResolve_SearchCardSelection",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(SearchSelectionPrefix)));
            // capture the local human's actions
            Try(h, typeof(PlayCardSet), "QueueCardPlacement",
                postfix: new HarmonyMethod(typeof(PvpBattle), nameof(QueuePlacementPostfix)));
            Try(h, typeof(PlayCardSet), "OnPressEndTurn",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(EndTurnPrefix)));
            Try(h, typeof(PlayTableGame), "ConfirmSelectElementArea",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(ConfirmAreaPrefix)));
            Try(h, typeof(PlayTableGame), "OnPressConfirmActionResolve",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(ConfirmResolvePrefix)));
            Try(h, typeof(PlayTableGame), "OnPressConfirmSearchCardSelection",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(ConfirmSearchPrefix)));
            Try(h, typeof(PlayTableGame), "ConfirmQuitGame",
                prefix: new HarmonyMethod(typeof(PvpBattle), nameof(QuitPrefix)));
            // no gift packs in PvP (each engine would roll its own)
            Try(h, typeof(PlayTableGame), "EvaluateEndGameGift",
                postfix: new HarmonyMethod(typeof(PvpBattle), nameof(NoGiftPostfix)));
            // exit
            Try(h, typeof(InteractablePlayTable), "ExitPlayerCardGame",
                postfix: new HarmonyMethod(typeof(PvpBattle), nameof(ExitPostfix)));
        }

        private static void Try(Harmony h, Type type, string method, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    CoopPlugin.Log.LogWarning($"PvpBattle: {type.Name}.{method} not found - skipped");
                    return;
                }
                h.Patch(target, prefix, postfix);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"PvpBattle: patch {type.Name}.{method} failed: {e.Message}"); }
        }

        // ================================================================ entry

        /// <summary>Host: right-click on a table with nobody at it toggles "waiting for a
        /// guest". Vanilla would only say "no other player".</summary>
        public static bool HostTableClickPrefix(InteractablePlayTable __instance)
        {
            var self = s_instance;
            if (self == null || CoopCore.Role != CoopRole.Host || Active)
                return true;
            try
            {
                var occ = __instance.m_IsSeatOccupied;
                bool anySeat = occ != null && ((occ.Count > 0 && occ[0]) || (occ.Count > 1 && occ[1]));
                var cust = __instance.GetOccupiedCustomerList();
                bool anyCustomer = cust != null && ((cust.Count > 0 && cust[0] != null) || (cust.Count > 1 && cust[1] != null));
                if (anySeat || anyCustomer)
                    return true;
                var td = CPlayerData.m_TournamentData;
                if (td != null && td.m_IsTournamentDay && !td.m_IsTournamentDayOver)
                    return true;
                int index = GuestBattle.IndexOf(__instance);
                if (index < 0)
                    return true;
                if (self._hostWaitingTable == index)
                {
                    self._hostWaitingTable = -1;
                    HostOnlyFeatures.Notice("Co-op: no longer waiting for a guest at this table");
                    return false;
                }
                if (!DeckReady())
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.DeckIncomplete);
                    return false;
                }
                self._hostWaitingTable = index;
                HostOnlyFeatures.Notice("Co-op: waiting for a guest to right-click this table (right-click again to cancel)");
                CoopPlugin.Log.LogInfo($"PvpBattle: host waiting at table {index}");
                return false;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("PvpBattle.HostTableClickPrefix: " + e.Message);
                return true;
            }
        }

        /// <summary>Client: GuestBattle routes a right-click on a table with no customer here.
        /// Returns true when the click was consumed.</summary>
        public static bool ClientRequestPvp(InteractablePlayTable table, int index)
        {
            var self = s_instance;
            if (self == null || CoopCore.Role != CoopRole.Client || self.SendToHost == null)
                return false;
            if (Active)
                return true;
            if (!DeckReady())
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.DeckIncomplete);
                return true;
            }
            var deck = SelectedDeckEntry();
            if (deck == null)
                return false;
            self.SendToHost(new PvpSitMessage { TableIndex = (byte)index, Deck = deck });
            HostOnlyFeatures.Notice("Co-op: asking the host for a match...");
            return true;
        }

        public void HostApplySit(PvpSitMessage msg, int conn)
        {
            Guarded("sit", () =>
            {
                if (Active)
                {
                    SendToClient?.Invoke(conn, new BattleSitResultMessage { TableIndex = msg.TableIndex, Granted = false, Reason = (int)ENotEnoughResourceText.SitPlaytableAlreadyPlaying });
                    return;
                }
                if (_hostWaitingTable != msg.TableIndex)
                {
                    SendToClient?.Invoke(conn, new BattleSitResultMessage { TableIndex = msg.TableIndex, Granted = false, Reason = (int)ENotEnoughResourceText.SitPlaytableNoOtherPlayer });
                    return;
                }
                var table = GuestBattle.TableAt(msg.TableIndex);
                var ptg = PlayCardGame.Game();
                var cust = table != null ? table.GetOccupiedCustomerList() : null;
                bool anyCustomer = cust != null && ((cust.Count > 0 && cust[0] != null) || (cust.Count > 1 && cust[1] != null));
                if (table == null || ptg == null || ptg.IsPlayTableGameMode() || !DeckReady() || anyCustomer)
                {
                    SendToClient?.Invoke(conn, new BattleSitResultMessage { TableIndex = msg.TableIndex, Granted = false, Reason = (int)ENotEnoughResourceText.SitPlaytableAlreadyPlaying });
                    return;
                }
                var remote = ResolveDeck(msg.Deck);
                if (remote == null || remote.Count < GameInstance.GetMaxDeckCardCount())
                {
                    SendToClient?.Invoke(conn, new BattleSitResultMessage { TableIndex = msg.TableIndex, Granted = false, Reason = (int)ENotEnoughResourceText.DeckIncomplete });
                    return;
                }
                int seed = unchecked(Environment.TickCount * 31 + msg.TableIndex * 7 + (int)(Time.realtimeSinceStartup * 1000f));
                string name = PeerName?.Invoke(conn);
                _hostWaitingTable = -1;
                _peerConn = conn;
                SendToClient?.Invoke(conn, new PvpStartMessage
                {
                    TableIndex = msg.TableIndex,
                    Seed = seed,
                    HostSideA = true,
                    HostDeck = SelectedDeckEntry(),
                    HostName = CoopCore.Instance != null ? CoopCore.Instance.EffectivePlayerName : "host",
                });
                Begin(true, seed, msg.TableIndex, remote, string.IsNullOrEmpty(name) ? "guest" : name);
                // the host's own seat, then the guest's (a puppet on this PC)
                GuestBattle.BookSeat(table, 0, true);
                GuestBattle.BookSeat(table, 1, true);
                ptg.SetPlayTable(table, true);
                if (!ptg.IsPlayTableGameMode())
                {
                    CoopPlugin.Log.LogWarning("PvpBattle: SetPlayTable refused on the host");
                    Abort("host could not sit");
                }
            });
        }

        public void ClientApplyStart(PvpStartMessage msg)
        {
            Guarded("start", () =>
            {
                var table = GuestBattle.TableAt(msg.TableIndex);
                var ptg = PlayCardGame.Game();
                var remote = ResolveDeck(msg.HostDeck);
                if (table == null || ptg == null || ptg.IsPlayTableGameMode() || remote == null)
                {
                    CoopPlugin.Log.LogWarning("PvpBattle: cannot start (table/manager/deck missing)");
                    SendToHost?.Invoke(new PvpEndMessage { TableIndex = msg.TableIndex, Reason = "guest could not start" });
                    return;
                }
                Begin(false, msg.Seed, msg.TableIndex, remote, string.IsNullOrEmpty(msg.HostName) ? "host" : msg.HostName);
                GuestBattle.BookSeat(table, 0, true);
                GuestBattle.BookSeat(table, 1, true);
                ptg.SetPlayTable(table, !msg.HostSideA);
                if (!ptg.IsPlayTableGameMode())
                {
                    CoopPlugin.Log.LogWarning("PvpBattle: SetPlayTable refused on the guest");
                    SendToHost?.Invoke(new PvpEndMessage { TableIndex = msg.TableIndex, Reason = "guest could not sit" });
                    Abort("guest could not sit");
                }
            });
        }

        private static void Begin(bool hostPc, int seed, int table, List<CardData> remoteDeck, string opponent)
        {
            Active = true;
            s_isHostPc = hostPc;
            s_seed = seed;
            s_table = table;
            s_remoteDeck = remoteDeck;
            s_opponent = opponent;
            s_applying = false;
            s_enemyMulliganWanted = false;
            s_enemySearchPending = false;
            s_queue.Clear();
            Array.Clear(s_rngCounter, 0, s_rngCounter.Length);
            s_seq = 0;
            s_hashTimer = 0f;
            s_queueStuckSince = -1f;
            CoopPlugin.Log.LogInfo($"PvpBattle: match vs {opponent} at table {table}, seed {seed}, {(hostPc ? "host" : "guest")} PC");
            HostOnlyFeatures.Notice("Co-op: match vs " + opponent + " (experimental)");
        }

        private static void Abort(string why)
        {
            CoopPlugin.Log.LogWarning("PvpBattle: aborted - " + why);
            End();
        }

        private static void End()
        {
            Active = false;
            s_table = -1;
            s_remoteDeck = null;
            s_queue.Clear();
            s_enemyMulliganWanted = false;
            s_enemySearchPending = false;
            var self = s_instance;
            if (self != null)
            {
                self._peerConn = -1;
                self._hostWaitingTable = -1;
            }
        }

        public void HostApplyEnd(PvpEndMessage msg, int conn)
        {
            if (conn != _peerConn && !(_hostWaitingTable >= 0 && conn >= 0))
                return;
            Guarded("end", () =>
            {
                if (!Active)
                    return;
                CoopPlugin.Log.LogInfo($"PvpBattle: opponent left ({msg.Reason})");
                OpponentLeft();
            });
        }

        public void ClientApplyEnd(PvpEndMessage msg)
        {
            Guarded("end", () =>
            {
                if (!Active)
                    return;
                CoopPlugin.Log.LogInfo($"PvpBattle: opponent left ({msg.Reason})");
                OpponentLeft();
            });
        }

        /// <summary>The other side is gone mid-match: this side wins by default, unless the
        /// result screen is already up (then it just leaves the table normally).</summary>
        private static void OpponentLeft()
        {
            var ptg = PlayCardGame.Game();
            if (ptg == null || !ptg.IsPlayTableGameMode())
            {
                End();
                return;
            }
            try
            {
                if (ptg.m_PlayCardSetUIScreen_WinLoseScreen != null && ptg.m_PlayCardSetUIScreen_WinLoseScreen.IsScreenOpened())
                    return; // result already shown; ExitPostfix cleans up
                ptg.ReportWinner(true, false);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.OpponentLeft: " + e.Message); }
        }

        public void HostReleaseConn(int conn)
        {
            if (conn == _peerConn && Active)
            {
                CoopPlugin.Log.LogInfo("PvpBattle: opponent disconnected");
                OpponentLeft();
            }
        }

        public override void Reset()
        {
            End();
        }

        /// <summary>Either side's table exit ends the match locally and tells the other PC.</summary>
        public static void ExitPostfix(InteractablePlayTable __instance)
        {
            if (!Active)
                return;
            try
            {
                int index = GuestBattle.IndexOf(__instance);
                if (index != s_table)
                    return;
                var self = s_instance;
                var msg = new PvpEndMessage { TableIndex = (byte)index, Reason = "left the table" };
                if (s_isHostPc)
                {
                    if (self != null && self._peerConn >= 0)
                        self.SendToClient?.Invoke(self._peerConn, msg);
                    // the guest's puppet seat
                    GuestBattle.BookSeat(__instance, 1, false);
                }
                else
                {
                    self?.SendToHost?.Invoke(msg);
                    GuestBattle.BookSeat(__instance, 0, false);
                }
                CoopPlugin.Log.LogInfo("PvpBattle: match over");
                End();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.ExitPostfix: " + e.Message); }
        }

        // ================================================================ engine patches

        /// <summary>Owner of a set on THIS pc: 0 = host's deck, 1 = guest's deck.</summary>
        private static int OwnerOf(PlayCardSet set)
        {
            return set.m_IsPlayer == s_isHostPc ? 0 : 1;
        }

        private static int SeedFor(int owner, int site)
        {
            int n = s_rngCounter[owner, site]++;
            return unchecked(s_seed * 397 ^ (owner + 1) * 7919 ^ (site + 1) * 104729 ^ n * 31 + 17);
        }

        public static bool EnemyAiPrefix(PlayCardSet __instance)
        {
            return !(Active && !__instance.m_IsPlayer);
        }

        /// <summary>The enemy set built the AI deck; replace it with the remote player's cards.</summary>
        public static void ResetBoardPostfix(PlayCardSet __instance, bool canUpdateDeck)
        {
            if (!Active || __instance.m_IsPlayer || s_remoteDeck == null)
                return;
            try
            {
                __instance.m_SetupCardDataList.Clear();
                for (int i = 0; i < s_remoteDeck.Count; i++)
                    __instance.m_SetupCardDataList.Add(s_remoteDeck[i]);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.ResetBoardPostfix: " + e.Message); }
        }

        public static void ShufflePrefix(PlayCardSet __instance, out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            if (!Active)
                return;
            UnityEngine.Random.InitState(SeedFor(OwnerOf(__instance), 1));
        }

        public static void RngRestorePostfix(UnityEngine.Random.State __state)
        {
            if (!Active)
                return;
            UnityEngine.Random.state = __state;
        }

        /// <summary>Who goes first: the host's click decides; the guest's own click is ignored
        /// and the host's result is replayed (see <see cref="ApplyTurnFirst"/>).</summary>
        private static bool s_turnClickCounts;

        public static bool TurnSelectPrefix(PlayTableGame __instance, out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            s_turnClickCounts = false;
            if (!Active)
                return true;
            if (!s_isHostPc && !s_applying)
                return false;
            try
            {
                // vanilla ignores a click once the card is chosen or while it is not waiting;
                // only a click that will DECIDE may be seeded and sent (2026-09-16: a double
                // click sent TurnFirst twice and the duplicate blocked the guest's queue)
                bool chosen = FiHasSelectedTurn != null && (bool)FiHasSelectedTurn.GetValue(__instance);
                s_turnClickCounts = !chosen && __instance.IsWaitingResponse();
            }
            catch { s_turnClickCounts = true; }
            if (!s_turnClickCounts)
                return true;
            UnityEngine.Random.InitState(SeedFor(2, 3));
            return true;
        }

        public static void TurnSelectPostfix(PlayTableGame __instance, UnityEngine.Random.State __state)
        {
            if (!Active)
                return;
            if (!s_turnClickCounts)
                return;
            UnityEngine.Random.state = __state;
            if (!s_isHostPc || s_applying)
                return;
            try
            {
                bool hostFirst = (bool)FiIsPlayerTurn.GetValue(__instance);
                Send(new PvpActionMessage { Kind = PvpActionKind.TurnFirst, Flag = hostFirst });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.TurnSelectPostfix: " + e.Message); }
        }

        public static bool MulliganPrefix(PlayTableGame __instance, bool isMulligan, bool isPlayer)
        {
            if (!Active)
                return true;
            if (isPlayer)
            {
                if (!s_applying)
                    Send(new PvpActionMessage { Kind = PvpActionKind.Mulligan, Flag = isMulligan });
                return true;
            }
            if (s_applying)
                return true;
            // DelayStart's random enemy decision: the remote human decides instead
            s_enemyMulliganWanted = true;
            return false;
        }

        public static bool SearchSelectionPrefix(PlayCardSet __instance)
        {
            if (!Active || __instance.m_IsPlayer)
                return true;
            try
            {
                // the flags the AI branch sets before it picks; the pick itself comes from the queue
                __instance.m_IsWaitingActionResolve = true;
                FiStopping?.SetValue(__instance, true);
                FiDiscardedTrigger?.SetValue(__instance, 0);
                FiSearchedLastTurn?.SetValue(__instance, true);
                s_enemySearchPending = true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.SearchSelectionPrefix: " + e.Message); }
            return false;
        }

        public static void NoGiftPostfix(PlayTableGame __instance)
        {
            if (!Active)
                return;
            try
            {
                FiGiftType?.SetValue(__instance, EItemType.None);
            }
            catch { }
        }

        // ---------------------------------------------------------------- capture

        public static void QueuePlacementPostfix(PlayCardSet __instance, InteractableCard3d card3d, ECardDrawQueueType queueType, int index)
        {
            if (!Active || s_applying || !__instance.m_IsPlayer || queueType != ECardDrawQueueType.ToElementArea)
                return;
            try
            {
                var cd = card3d != null && card3d.m_Card3dUI != null && card3d.m_Card3dUI.m_CardUI != null ? card3d.m_Card3dUI.m_CardUI.GetCardData() : null;
                if (cd == null)
                    return;
                Send(new PvpActionMessage
                {
                    Kind = PvpActionKind.Place,
                    A = index,
                    Card = new DeckCardEntry { Expansion = cd.expansionType, IsDestiny = cd.isDestiny },
                    CardMonster = (int)cd.monsterType,
                    CardBorder = (int)cd.borderType,
                    CardFoil = cd.isFoil,
                });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.QueuePlacementPostfix: " + e.Message); }
        }

        public static void EndTurnPrefix(PlayCardSet __instance)
        {
            if (!Active || s_applying || !__instance.m_IsPlayer)
                return;
            try
            {
                if (MiCanTakeAction != null && (bool)MiCanTakeAction.Invoke(__instance, null))
                    Send(new PvpActionMessage { Kind = PvpActionKind.EndTurn });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.EndTurnPrefix: " + e.Message); }
        }

        public static void ConfirmAreaPrefix(PlayTableGame __instance, bool isPlayer, bool forceConfirm)
        {
            if (!Active || s_applying || !isPlayer)
                return;
            try
            {
                if (!(__instance.CanConfirmSelectElementArea() || forceConfirm))
                    return;
                Send(new PvpActionMessage
                {
                    Kind = PvpActionKind.AreaSelect,
                    A = __instance.GetSelectedPlayerElementAreaIndex(),
                    B = __instance.GetSelectedEnemyElementAreaIndex(),
                });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.ConfirmAreaPrefix: " + e.Message); }
        }

        public static void ConfirmResolvePrefix(PlayTableGame __instance)
        {
            if (!Active || s_applying)
                return;
            try
            {
                var set = __instance.m_PlayCardSetPlayer;
                if (set == null || !set.CanActionResolve())
                    return;
                var msg = new PvpActionMessage { Kind = PvpActionKind.ResolveSelect };
                var valid = set.m_ResolveActionValidCardList;
                var sel = set.m_ResolveActionSelectedCardList;
                for (int i = 0; sel != null && i < sel.Count; i++)
                {
                    int idx = valid != null ? valid.IndexOf(sel[i]) : -1;
                    if (idx >= 0)
                        msg.Sel.Add(idx);
                }
                Send(msg);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.ConfirmResolvePrefix: " + e.Message); }
        }

        public static void ConfirmSearchPrefix(PlayTableGame __instance, List<int> cardDataSelectedIndexList, List<int> cardDataRemainingIndexList, bool isResolveSuccess)
        {
            if (!Active || s_applying)
                return;
            try
            {
                if (FiShowingDiscard != null && (bool)FiShowingDiscard.GetValue(__instance))
                    return; // just closing the discard-pile viewer
                var msg = new PvpActionMessage { Kind = PvpActionKind.SearchSelect, Flag = isResolveSuccess };
                if (cardDataSelectedIndexList != null)
                    msg.Sel.AddRange(cardDataSelectedIndexList);
                if (cardDataRemainingIndexList != null)
                    msg.Rem.AddRange(cardDataRemainingIndexList);
                Send(msg);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.ConfirmSearchPrefix: " + e.Message); }
        }

        public static void QuitPrefix(PlayTableGame __instance)
        {
            if (!Active || s_applying)
                return;
            Send(new PvpActionMessage { Kind = PvpActionKind.Quit });
        }

        private static void Send(PvpActionMessage msg)
        {
            var self = s_instance;
            if (self == null || !Active)
                return;
            msg.Seq = ++s_seq;
            if (s_isHostPc)
            {
                if (self._peerConn >= 0)
                    self.SendToClient?.Invoke(self._peerConn, msg);
            }
            else
                self.SendToHost?.Invoke(msg);
            if (msg.Kind != PvpActionKind.Hash)
                CoopPlugin.Log.LogInfo($"PvpBattle: sent {msg.Kind} #{msg.Seq}");
        }

        // ---------------------------------------------------------------- receive / replay

        public void HostApplyAction(PvpActionMessage msg, int conn)
        {
            if (!Active || conn != _peerConn)
                return;
            Enqueue(msg);
        }

        public void ClientApplyAction(PvpActionMessage msg)
        {
            if (!Active)
                return;
            Enqueue(msg);
        }

        private static void Enqueue(PvpActionMessage msg)
        {
            if (msg.Kind == PvpActionKind.Hash)
            {
                CheckHash(msg);
                return;
            }
            if (msg.Kind == PvpActionKind.Quit)
            {
                s_queue.Clear();
                CoopPlugin.Log.LogInfo("PvpBattle: opponent quit");
                OpponentLeft();
                return;
            }
            s_queue.Enqueue(msg);
        }

        /// <summary>Runs after the ENEMY set's Update: apply the head of the remote queue once
        /// the engine is in the state that action belongs to (the same gate the AI used).</summary>
        public static void SetUpdatePostfix(PlayCardSet __instance)
        {
            if (!Active || __instance.m_IsPlayer)
                return;
            var ptg = __instance.m_PlayTableGame;
            if (ptg == null || !ptg.IsPlayTableGameMode())
                return;
            if (s_queue.Count == 0)
            {
                s_queueStuckSince = -1f;
                return;
            }
            var a = s_queue.Peek();
            bool done = false;
            s_applying = true;
            try
            {
                switch (a.Kind)
                {
                    case PvpActionKind.TurnFirst:
                        done = ApplyTurnFirst(ptg, a);
                        break;
                    case PvpActionKind.Mulligan:
                        if (s_enemyMulliganWanted && FiHasEnemyMulligan != null && !(bool)FiHasEnemyMulligan.GetValue(ptg))
                        {
                            ptg.SelectionMulliganOrKeep(a.Flag, false);
                            done = true;
                        }
                        break;
                    case PvpActionKind.Place:
                        if (AiGate(__instance, ptg))
                        {
                            ApplyPlace(__instance, a);
                            done = true;
                        }
                        break;
                    case PvpActionKind.EndTurn:
                        if (AiGate(__instance, ptg))
                        {
                            __instance.OnPressEndTurn();
                            done = true;
                        }
                        break;
                    case PvpActionKind.AreaSelect:
                        if (__instance.m_IsWaitingActionResolve && ptg.IsWaitingSelectElementArea()
                            && ptg.BothPlayerDrawQueueEmpty() && __instance.m_CenterShowCardList.Count == 0)
                        {
                            // the sender's own areas are OUR enemy's areas
                            if (a.A >= 0)
                                ptg.SetSelectedEnemyElementAreaIndex(a.A);
                            if (a.B >= 0)
                                ptg.SetSelectedPlayerElementAreaIndex(a.B);
                            ptg.ConfirmSelectElementArea(false, true);
                            done = true;
                        }
                        break;
                    case PvpActionKind.ResolveSelect:
                        if (__instance.m_IsWaitingActionResolve && !ptg.IsWaitingSelectElementArea() && !s_enemySearchPending
                            && FiConfirmedResolve != null && !(bool)FiConfirmedResolve.GetValue(__instance)
                            && ptg.BothPlayerDrawQueueEmpty() && __instance.m_CenterShowCardList.Count == 0)
                        {
                            var valid = __instance.m_ResolveActionValidCardList;
                            var sel = __instance.m_ResolveActionSelectedCardList;
                            sel.Clear();
                            for (int i = 0; i < a.Sel.Count; i++)
                                if (a.Sel[i] >= 0 && a.Sel[i] < valid.Count)
                                    sel.Add(valid[a.Sel[i]]);
                            __instance.ConfirmActionResolve();
                            done = true;
                        }
                        break;
                    case PvpActionKind.SearchSelect:
                        if (s_enemySearchPending)
                        {
                            s_enemySearchPending = false;
                            __instance.StartCoroutine(__instance.OnFinishSearchCardSelection(new List<int>(a.Sel), new List<int>(a.Rem), a.Flag));
                            done = true;
                        }
                        break;
                    default:
                        done = true;
                        break;
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"PvpBattle: applying {a.Kind} #{a.Seq} failed: {e.Message}");
                done = true;
            }
            finally { s_applying = false; }
            if (done)
            {
                s_queue.Dequeue();
                s_queueStuckSince = -1f;
                CoopPlugin.Log.LogInfo($"PvpBattle: applied {a.Kind} #{a.Seq}");
            }
            else
            {
                float now = Time.unscaledTime;
                if (s_queueStuckSince < 0f)
                    s_queueStuckSince = now;
                else if (now - s_queueStuckSince > 20f)
                {
                    CoopPlugin.Log.LogWarning($"PvpBattle: {a.Kind} #{a.Seq} has waited 20s for the engine - engines may have diverged");
                    s_queueStuckSince = now;
                }
            }
        }

        private static bool AiGate(PlayCardSet set, PlayTableGame ptg)
        {
            return MiCanTakeAction != null && (bool)MiCanTakeAction.Invoke(set, null)
                && !set.m_IsWaitingActionResolve && ptg.BothPlayerDrawQueueEmpty() && set.m_CenterShowCardList.Count == 0;
        }

        private static bool ApplyTurnFirst(PlayTableGame ptg, PvpActionMessage a)
        {
            if (s_isHostPc)
                return true; // never sent to the host
            if (FiHasSelectedTurn == null || (bool)FiHasSelectedTurn.GetValue(ptg))
                return true; // already decided (a duplicate) - drop it, never block the queue
            if (!ptg.IsWaitingResponse())
                return false;
            ptg.OnPressTurnSelectCard(0); // flips the card and rolls its own coin...
            bool guestFirst = !a.Flag;    // ...which we overrule with the host's result
            FiIsPlayerTurn.SetValue(ptg, guestFirst);
            try
            {
                var ui = ptg.m_PlayCardSetPlayer.m_PlayCardSetUI;
                ui.m_TurnSelectAreaFirstSecondListA[0].SetActive(guestFirst);
                ui.m_TurnSelectAreaFirstSecondListA[1].SetActive(!guestFirst);
            }
            catch { }
            return true;
        }

        private static void ApplyPlace(PlayCardSet enemy, PvpActionMessage a)
        {
            var hand = enemy.m_HoldCard3dList;
            int idx = -1;
            for (int i = 0; hand != null && i < hand.Count; i++)
            {
                var c = hand[i];
                var cd = c != null && c.m_Card3dUI != null && c.m_Card3dUI.m_CardUI != null ? c.m_Card3dUI.m_CardUI.GetCardData() : null;
                if (cd == null)
                    continue;
                if ((int)cd.monsterType == a.CardMonster && cd.expansionType == a.Card.Expansion
                    && (int)cd.borderType == a.CardBorder && cd.isFoil == a.CardFoil && cd.isDestiny == a.Card.IsDestiny)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
            {
                // exact copy not found: any card of that monster is the same play
                for (int i = 0; hand != null && i < hand.Count; i++)
                {
                    var cd = hand[i]?.m_Card3dUI?.m_CardUI?.GetCardData();
                    if (cd != null && (int)cd.monsterType == a.CardMonster)
                    {
                        idx = i;
                        break;
                    }
                }
            }
            if (idx < 0)
            {
                CoopPlugin.Log.LogWarning($"PvpBattle: DESYNC - opponent played monster {a.CardMonster} which is not in their hand here (hand {hand?.Count ?? -1})");
                return;
            }
            var card = hand[idx];
            enemy.m_CurrentSelectedCard = card;
            FiDragMonster?.SetValue(enemy, InventoryBase.GetMonsterData(card.m_Card3dUI.m_CardUI.GetCardData().monsterType));
            enemy.m_CurrentHoverElementAreaIndex = a.A;
            bool ok = MiEvaluatePlace != null && (bool)MiEvaluatePlace.Invoke(enemy, new object[] { card, true });
            if (!ok)
            {
                CoopPlugin.Log.LogWarning($"PvpBattle: DESYNC - opponent's play of monster {a.CardMonster} to area {a.A} is not legal here");
                enemy.m_CurrentSelectedCard = null;
                return;
            }
            var removed = (InteractableCard3d)MiRemoveFromHold.Invoke(enemy, new object[] { idx });
            enemy.QueueCardPlacement(removed, ECardDrawQueueType.ToElementArea, 0.35f, a.A);
        }

        // ---------------------------------------------------------------- divergence check

        protected override void OnHostTick(in SyncFrame frame) => HashTick(frame.Dt);
        protected override void OnClientTick(in SyncFrame frame) => HashTick(frame.Dt);

        private void HashTick(float dt)
        {
            if (!Active)
                return;
            s_hashTimer += dt;
            if (s_hashTimer < 2f)
                return;
            s_hashTimer = 0f;
            Guarded("hash", () =>
            {
                var ptg = PlayCardGame.Game();
                if (ptg == null || !ptg.IsPlayTableGameMode())
                    return;
                Send(new PvpActionMessage { Kind = PvpActionKind.Hash, A = ptg.GetTurnCount(), B = StateHash(ptg, fromPlayerSide: true) });
            });
        }

        /// <summary>Turn + HPs + hand sizes, ordered host-first so both PCs compute the same number.</summary>
        private static int StateHash(PlayTableGame ptg, bool fromPlayerSide)
        {
            var mine = ptg.m_PlayCardSetPlayer;
            var theirs = ptg.m_PlayCardSetEnemy;
            var host = s_isHostPc ? mine : theirs;
            var guest = s_isHostPc ? theirs : mine;
            int h = 17;
            h = h * 31 + ptg.GetTurnCount();
            h = h * 31 + host.GetCurrentHP();
            h = h * 31 + guest.GetCurrentHP();
            h = h * 31 + (host.GetHoldCard3dList()?.Count ?? 0);
            h = h * 31 + (guest.GetHoldCard3dList()?.Count ?? 0);
            return h;
        }

        private static void CheckHash(PvpActionMessage msg)
        {
            try
            {
                var ptg = PlayCardGame.Game();
                if (ptg == null || !ptg.IsPlayTableGameMode())
                    return;
                if (ptg.GetTurnCount() != msg.A)
                    return; // one side is mid-transition; compare on the same turn only
                int mine = StateHash(ptg, true);
                if (mine != msg.B)
                    CoopPlugin.Log.LogWarning($"PvpBattle: state hash differs on turn {msg.A} (mine {mine}, theirs {msg.B}) - watch for a desync");
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PvpBattle.CheckHash: " + e.Message); }
        }

        // ---------------------------------------------------------------- decks

        private static bool DeckReady()
        {
            try
            {
                var decks = CPlayerData.m_DeckCompactCardDataList;
                int sel = CPlayerData.m_CurrentSelectedDeckIndex;
                return decks != null && sel >= 0 && sel < decks.Count
                    && decks[sel].GetTotalCardCount() >= GameInstance.GetMaxDeckCardCount();
            }
            catch { return false; }
        }

        private static DeckEntry SelectedDeckEntry()
        {
            try
            {
                var d = CPlayerData.m_DeckCompactCardDataList[CPlayerData.m_CurrentSelectedDeckIndex];
                var e = new DeckEntry { Name = d.deckName ?? "", DeckBox = d.deckBoxIndex, Playmat = d.playmatIndex };
                var cards = d.compactCardDataAmountList;
                for (int c = 0; cards != null && c < cards.Count; c++)
                {
                    var cd = cards[c];
                    if (cd == null)
                        continue;
                    e.Cards.Add(new DeckCardEntry
                    {
                        Expansion = cd.expansionType,
                        Index = cd.cardSaveIndex,
                        Amount = cd.amount,
                        GradedIndex = cd.gradedCardIndex,
                        IsDestiny = cd.isDestiny,
                    });
                }
                return e;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("PvpBattle: selected deck unreadable: " + e.Message);
                return null;
            }
        }

        private static List<CardData> ResolveDeck(DeckEntry e)
        {
            if (e == null)
                return null;
            try
            {
                var list = new List<CardData>();
                for (int c = 0; c < e.Cards.Count; c++)
                {
                    var ce = e.Cards[c];
                    var cd = CPlayerData.GetCardData(ce.Index, ce.Expansion, ce.IsDestiny);
                    if (cd == null)
                        continue;
                    for (int k = 0; k < ce.Amount; k++)
                        list.Add(cd);
                }
                return list;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("PvpBattle: remote deck unreadable: " + ex.Message);
                return null;
            }
        }
    }
}
