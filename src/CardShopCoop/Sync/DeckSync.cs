using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using HarmonyLib;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The shop's card-game decks (game 1.0). Decks live in
    /// <c>CPlayerData.m_DeckCompactCardDataList</c>, which nothing else syncs; the cards that
    /// move in and out of them already travel as CardDeltas (the AddCard / ReduceCard
    /// postfixes fire on both sides), so this module only carries the deck list.
    ///
    /// Ownership: the host's list is the truth, mirrored to guests as DeckState. A guest may
    /// edit too - the Workbench "deck" button asks the host for the ONE editor lock
    /// (DeckEditRequest); while the guest holds it, its list streams up as DeckStateUp, the
    /// host replaces its own and the mirror carries it to everyone. One editor at a time
    /// (host included) is what makes "replace the whole list" safe: nobody else's edit is in
    /// flight to be clobbered. The holder ignores the mirror while editing, for the same
    /// reason in reverse.
    ///
    /// The selected deck (<c>m_CurrentSelectedDeckIndex</c>) is per PLAYER, not mirrored:
    /// each player picks their own deck from the shared list and battles with it.
    ///
    /// Host: hash the deck list every 2s, broadcast on change (heal every 20s).
    /// Client: replace the list in place - never while in a battle, since the engine reads
    /// the selected deck at SetPlayTable and again at DelayStart.
    /// </summary>
    public sealed class DeckSync : TickableCoopModule
    {
        public Action<INetMessage> BroadcastState;
        public Action<INetMessage> SendToHost;              // client side
        public Action<int, INetMessage> SendToClient;       // host side
        public Func<int, string> PeerName;                  // host side: conn -> display name

        private const int NoEditor = -1;
        private const int HostEditor = -2;

        private readonly SnapshotGate _gate = new SnapshotGate(2f, 20f, -1.3f);

        // host
        private int _editorConn = NoEditor;
        private DeckStateUpMessage _pendingUp;

        // client
        private bool _requestPending;
        private bool _editing;
        private Action _remoteGranted;   // the phone deck builder waiting for the lock
        /// <summary>The phone deck builder holds the editor (host or client).</summary>
        public static bool RemoteEditing
        {
            get; private set;
        }
        private float _upTimer;
        private int _upHash;

        private static DeckSync s_instance;

        /// <summary>Client: select this index as soon as the mirrored list is long enough
        /// (a deck the host just built for us arrives on the next DeckState).</summary>
        public static int PendingSelect = -1;
        private static string s_lastSavedDeckName;

        public override string Name => nameof(DeckSync);

        public DeckSync()
        {
            s_instance = this;
        }

        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(WorkbenchUIScreen), "OnPressEditDeckButton",
                new HarmonyMethod(typeof(DeckSync), nameof(EditDeckButtonPrefix)));
            Try(h, typeof(PlayCardGameManager), "OnCloseDeckListScreen",
                null, new HarmonyMethod(typeof(DeckSync), nameof(CloseDeckListPostfix)));
        }

        private static void Try(Harmony h, Type type, string method, HarmonyMethod prefix, HarmonyMethod postfix = null)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    CoopPlugin.Log.LogWarning($"DeckSync: {type.Name}.{method} not found - skipped");
                    return;
                }
                h.Patch(target, prefix, postfix);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"DeckSync: patch {type.Name}.{method} failed: {e.Message}"); }
        }

        // ================================================================ patches

        /// <summary>The Workbench "deck" button. Gated HERE, not one call later in
        /// <c>OpenDeckListScreen</c>: the button handler sets <c>m_IsEditingDeck</c> on both the
        /// screen and the workbench before it opens the deck list, and while that flag is up
        /// <c>WorkbenchUIScreen.CloseScreen</c> refuses to close - cancelling only the screen
        /// welded the guest to the workbench.</summary>
        public static bool EditDeckButtonPrefix()
        {
            var self = s_instance;
            if (self == null)
                return true;
            try
            {
                if (CoopCore.Role == CoopRole.Host)
                {
                    if (self._editorConn != NoEditor && self._editorConn != HostEditor)
                    {
                        HostOnlyFeatures.Notice("Co-op: " + self.NameOf(self._editorConn) + " is editing the decks");
                        return false;
                    }
                    self._editorConn = HostEditor;
                    return true;
                }
                if (CoopCore.Role == CoopRole.Client)
                {
                    if (CoopCore.IsVisiting)
                    {
                        HostOnlyFeatures.Notice("Visit: you can't edit a rival's decks (your travel deck is the one marked ✈)");
                        return false;
                    }
                    if (self._editing)
                        return true; // lock granted: the re-press from ClientApplyEditResult
                    if (!self._requestPending)
                    {
                        self._requestPending = true;
                        self.SendToHost?.Invoke(new DeckEditRequestMessage { Want = true });
                    }
                    return false; // the grant re-presses the button
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("DeckSync.EditDeckButtonPrefix: " + e.Message); }
            return true;
        }

        /// <summary>Deck list closed: the editor is done. Host releases its own lock; the guest
        /// ships its final list and releases.</summary>
        public static void CloseDeckListPostfix()
        {
            var self = s_instance;
            if (self == null)
                return;
            try
            {
                if (CoopCore.Role == CoopRole.Host)
                {
                    if (self._editorConn == HostEditor)
                        self._editorConn = NoEditor;
                }
                else if (CoopCore.Role == CoopRole.Client && self._editing)
                {
                    self.ClientSendUp(final: true);
                    self._editing = false;
                    self.SendToHost?.Invoke(new DeckEditRequestMessage { Want = false });
                    CoopPlugin.Log.LogInfo("DeckSync: deck editor closed, lock released");
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("DeckSync.CloseDeckListPostfix: " + e.Message); }
        }

        // ================================================================ remote editor (phone)

        /// <summary>The phone deck builder wants the editor: same lock as the workbench, no
        /// workbench. Solo: always. Host: unless a guest holds it. Client: asks the host and
        /// runs <paramref name="granted"/> when the answer is yes. Returns false when refused
        /// outright (a notice says why).</summary>
        public static bool BeginRemoteEdit(Action granted)
        {
            var self = s_instance;
            if (CoopCore.IsVisiting)
            {
                HostOnlyFeatures.Notice("Visit: you can't edit a rival's decks (your travel deck is the one marked ✈)");
                return false;
            }
            if (InBattle())
            {
                HostOnlyFeatures.Notice("Decks can't change mid-battle");
                return false;
            }
            if (IsDeckListOpen())
            {
                CoopPlugin.Log.LogInfo("DeckSync: phone deck builder refused - the workbench deck list is open");
                HostOnlyFeatures.Notice("Close the workbench deck list first");
                return false;
            }
            if (self == null || CoopCore.Role == CoopRole.None)
            {
                RemoteEditing = true;
                granted?.Invoke();
                return true;
            }
            if (CoopCore.Role == CoopRole.Host)
            {
                if (self._editorConn != NoEditor && self._editorConn != HostEditor)
                {
                    HostOnlyFeatures.Notice("Co-op: " + self.NameOf(self._editorConn) + " is editing the decks");
                    return false;
                }
                self._editorConn = HostEditor;
                RemoteEditing = true;
                granted?.Invoke();
                return true;
            }
            if (self._editing)
            {
                RemoteEditing = true;
                granted?.Invoke();
                return true;
            }
            if (self._requestPending)
                return false;
            self._requestPending = true;
            self._remoteGranted = granted;
            self.SendToHost?.Invoke(new DeckEditRequestMessage { Want = true });
            return true;
        }

        public static void EndRemoteEdit()
        {
            var self = s_instance;
            if (!RemoteEditing)
                return;
            RemoteEditing = false;
            if (self == null)
                return;
            try
            {
                if (CoopCore.Role == CoopRole.Host)
                {
                    if (self._editorConn == HostEditor)
                        self._editorConn = NoEditor;
                }
                else if (CoopCore.Role == CoopRole.Client && self._editing)
                {
                    self.ClientSendUp(final: true);
                    self._editing = false;
                    self.SendToHost?.Invoke(new DeckEditRequestMessage { Want = false });
                    CoopPlugin.Log.LogInfo("DeckSync: phone deck builder closed, lock released");
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("DeckSync.EndRemoteEdit: " + e.Message); }
        }

        // ================================================================ host

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            if (_pendingUp != null && !InBattle())
            {
                var up = _pendingUp;
                _pendingUp = null;
                Guarded("apply-up", () => ApplyList(up.Decks));
                _gate.Force();
            }
            if (!_gate.Due(dt))
                return;
            Guarded("host", () =>
            {
                var decks = CPlayerData.m_DeckCompactCardDataList;
                if (decks == null)
                    return;
                if (!_gate.ShouldSend(Hash(decks)))
                    return;
                BroadcastState?.Invoke(BuildState(decks, CPlayerData.m_CurrentSelectedDeckIndex));
            });
        }

        public void HostApplyEditRequest(DeckEditRequestMessage msg, int conn)
        {
            Guarded("edit-request", () =>
            {
                if (!msg.Want)
                {
                    if (_editorConn == conn)
                    {
                        _editorConn = NoEditor;
                        CoopPlugin.Log.LogInfo($"DeckSync: {NameOf(conn)} released the deck editor");
                    }
                    return;
                }
                if (_editorConn == NoEditor || _editorConn == conn)
                {
                    _editorConn = conn;
                    CoopPlugin.Log.LogInfo($"DeckSync: deck editor granted to {NameOf(conn)}");
                    SendToClient?.Invoke(conn, new DeckEditResultMessage { Granted = true });
                    return;
                }
                string holder = _editorConn == HostEditor ? "the host" : NameOf(_editorConn);
                CoopPlugin.Log.LogInfo($"DeckSync: deck editor refused to {NameOf(conn)} - {holder} has it");
                SendToClient?.Invoke(conn, new DeckEditResultMessage { Granted = false, Holder = holder });
            });
        }

        public void HostApplyStateUp(DeckStateUpMessage msg, int conn)
        {
            Guarded("state-up", () =>
            {
                if (_editorConn != conn)
                {
                    CoopPlugin.Log.LogWarning($"DeckSync: deck list from {NameOf(conn)} ignored - not the editor");
                    return;
                }
                if (InBattle())
                {
                    _pendingUp = msg; // the engine may be reading the selected deck right now
                    return;
                }
                ApplyList(msg.Decks);
                _gate.Force();
                if (msg.Final)
                    CoopPlugin.Log.LogInfo($"DeckSync: {NameOf(conn)}'s deck edit applied ({msg.Decks.Count} decks)");
            });
        }

        public void HostReleaseConn(int conn)
        {
            if (_editorConn != conn)
                return;
            _editorConn = NoEditor;
            CoopPlugin.Log.LogInfo($"DeckSync: {NameOf(conn)} left while editing decks - lock released");
        }

        private string NameOf(int conn)
        {
            if (conn == HostEditor)
                return "the host";
            var name = PeerName?.Invoke(conn);
            return string.IsNullOrEmpty(name) ? "a guest" : name;
        }

        public override void OnFullyJoin(int connId)
        {
            _gate.Force();
        }

        public override void ForceResend()
        {
            _gate.Force();
        }

        public override void Reset()
        {
            _gate.Reset(-1.3f);
            _editorConn = NoEditor;
            _pendingUp = null;
            _requestPending = false;
            _editing = false;
            _upTimer = 0f;
            _upHash = 0;
            PendingSelect = -1;
            s_restoredOnce = false;
        }

        // ================================================================ client

        protected override void OnClientTick(in SyncFrame frame)
        {
            if (!frame.InGame)
                return;
            RememberSelection();
            if (!_editing)
                return;
            _upTimer += frame.Dt;
            if (_upTimer < 1f)
                return;
            _upTimer = 0f;
            Guarded("client", () => ClientSendUp(final: false));
        }

        private void ClientSendUp(bool final)
        {
            var decks = CPlayerData.m_DeckCompactCardDataList;
            if (decks == null)
                return;
            int h = Hash(decks);
            if (!final && h == _upHash)
                return;
            _upHash = h;
            var state = BuildState(decks, 0);
            SendToHost?.Invoke(new DeckStateUpMessage { Final = final, Decks = state.Decks });
        }

        public void ClientApplyEditResult(DeckEditResultMessage msg)
        {
            Guarded("edit-result", () =>
            {
                _requestPending = false;
                if (!msg.Granted)
                {
                    _remoteGranted = null;
                    HostOnlyFeatures.Notice("Co-op: " + (string.IsNullOrEmpty(msg.Holder) ? "someone" : msg.Holder) + " is editing the decks");
                    return;
                }
                if (_remoteGranted != null)
                {
                    // the phone deck builder asked, not the workbench
                    var cb = _remoteGranted;
                    _remoteGranted = null;
                    _editing = true;
                    _upTimer = 0f;
                    _upHash = Hash(CPlayerData.m_DeckCompactCardDataList ?? new List<DeckCompactCardDataList>());
                    RemoteEditing = true;
                    CoopPlugin.Log.LogInfo("DeckSync: deck editor lock granted to the phone deck builder");
                    cb();
                    return;
                }
                var wb = UnityEngine.Object.FindObjectOfType<WorkbenchUIScreen>();
                if (wb == null || wb.m_ScreenGrp == null || !wb.m_ScreenGrp.activeInHierarchy)
                {
                    // walked away before the answer came back - hand it straight back
                    SendToHost?.Invoke(new DeckEditRequestMessage { Want = false });
                    return;
                }
                _editing = true;
                _upTimer = 0f;
                _upHash = Hash(CPlayerData.m_DeckCompactCardDataList ?? new List<DeckCompactCardDataList>());
                CoopPlugin.Log.LogInfo("DeckSync: deck editor lock granted - opening");
                wb.OnPressEditDeckButton(); // prefix lets it through while _editing
                if (!IsDeckListOpen())
                {
                    _editing = false;
                    SendToHost?.Invoke(new DeckEditRequestMessage { Want = false });
                }
            });
        }

        public void ClientApplyState(DeckStateMessage message)
        {
            Guarded("apply", () =>
            {
                if (_editing)
                    return; // our list is the truth until we close the editor; the host re-mirrors it
                if (InBattle())
                    return; // mid-battle: the engine is reading the selected deck
                ApplyList(message.Decks);
            });
        }

        // ================================================================ shared

        private static bool InBattle()
        {
            var ptg = PlayCardGame.Game();
            return ptg != null && ptg.IsPlayTableGameMode();
        }

        private static bool IsDeckListOpen()
        {
            try
            {
                var m = PlayCardGame.Manager();
                if (m == null)
                    return false;
                var screen = m.m_DeckListScreen;
                if (!screen) // Unity's null: a destroyed or never-built screen is NOT open
                    return false;
                return screen.gameObject.activeInHierarchy;
            }
            catch (Exception e)
            {
                // fail OPEN: a shop that has never built its battle UI threw here and the owner
                // was refused for a list that was never open (2026-09-16, fresh league shop)
                CoopPlugin.Log.LogWarning("DeckSync.IsDeckListOpen: " + e.Message + " - treating as closed");
                return false;
            }
        }

        /// <summary>Replace the deck list in place, keeping THIS player's selected deck (clamped).</summary>
        private static void ApplyList(List<DeckEntry> entries)
        {
            var decks = CPlayerData.m_DeckCompactCardDataList;
            if (decks == null)
                CPlayerData.m_DeckCompactCardDataList = decks = new List<DeckCompactCardDataList>();
            Rivals.TravelDeck.BeforeApply();
            decks.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var d = new DeckCompactCardDataList
                {
                    deckName = e.Name ?? "",
                    deckBoxIndex = e.DeckBox,
                    playmatIndex = e.Playmat,
                };
                for (int c = 0; c < e.Cards.Count; c++)
                {
                    var ce = e.Cards[c];
                    d.compactCardDataAmountList.Add(new CompactCardDataAmount
                    {
                        expansionType = ce.Expansion,
                        cardSaveIndex = ce.Index,
                        amount = ce.Amount,
                        gradedCardIndex = ce.GradedIndex,
                        isDestiny = ce.IsDestiny,
                    });
                }
                decks.Add(d);
            }
            int sel = CPlayerData.m_CurrentSelectedDeckIndex;
            if (sel < 0 || sel >= decks.Count)
                CPlayerData.m_CurrentSelectedDeckIndex = 0;
            if (CoopCore.Role == CoopRole.Client)
                ClientRestoreSelection(decks);
            // a visitor's own deck rides on the end of the rival's list
            Rivals.TravelDeck.Inject(decks);
        }

        /// <summary>Client: honour a pending "select this" from the host, else on the first
        /// mirror after joining pick the deck this guest used last time (by name, saved in the
        /// guest's own config), so the scratch save's selection does not decide for them.</summary>
        private static bool s_restoredOnce;

        private static void ClientRestoreSelection(List<DeckCompactCardDataList> decks)
        {
            try
            {
                if (PendingSelect >= 0 && PendingSelect < decks.Count)
                {
                    CPlayerData.m_CurrentSelectedDeckIndex = PendingSelect;
                    PendingSelect = -1;
                    RememberSelection();
                    HostOnlyFeatures.Notice("Co-op: deck '" + (decks[CPlayerData.m_CurrentSelectedDeckIndex].deckName ?? "") + "' selected for you");
                    return;
                }
                if (s_restoredOnce)
                    return;
                s_restoredOnce = true;
                string want = CoopPlugin.GuestLastDeck != null ? CoopPlugin.GuestLastDeck.Value : "";
                if (string.IsNullOrEmpty(want))
                    return;
                for (int i = 0; i < decks.Count; i++)
                    if (decks[i] != null && decks[i].deckName == want)
                    {
                        CPlayerData.m_CurrentSelectedDeckIndex = i;
                        CoopPlugin.Log.LogInfo("DeckSync: restored your last deck '" + want + "'");
                        return;
                    }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("DeckSync.ClientRestoreSelection: " + e.Message); }
        }

        /// <summary>Client: persist the selected deck's NAME so it survives to the next session.</summary>
        public static void RememberSelection()
        {
            try
            {
                if (CoopCore.Role != CoopRole.Client || CoopPlugin.GuestLastDeck == null)
                    return;
                var decks = CPlayerData.m_DeckCompactCardDataList;
                int sel = CPlayerData.m_CurrentSelectedDeckIndex;
                if (decks == null || sel < 0 || sel >= decks.Count || decks[sel] == null)
                    return;
                string name = decks[sel].deckName ?? "";
                if (name == s_lastSavedDeckName)
                    return;
                s_lastSavedDeckName = name;
                CoopPlugin.GuestLastDeck.Value = name;
            }
            catch { }
        }

        private static DeckStateMessage BuildState(List<DeckCompactCardDataList> decks, int selected)
        {
            var msg = new DeckStateMessage { SelectedIndex = selected };
            for (int i = 0; i < decks.Count && i < 32; i++)
            {
                var d = decks[i];
                var e = new DeckEntry();
                if (d != null)
                {
                    e.Name = d.deckName ?? "";
                    e.DeckBox = d.deckBoxIndex;
                    e.Playmat = d.playmatIndex;
                    var cards = d.compactCardDataAmountList;
                    if (cards != null)
                        for (int c = 0; c < cards.Count && c < 128; c++)
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
                }
                msg.Decks.Add(e);
            }
            return msg;
        }

        private static int Hash(List<DeckCompactCardDataList> decks)
        {
            int h = 17;
            h = h * 31 + decks.Count;
            for (int i = 0; i < decks.Count && i < 32; i++)
            {
                var d = decks[i];
                if (d == null)
                {
                    h = h * 31;
                    continue;
                }
                h = h * 31 + (d.deckName ?? "").GetHashCode();
                h = h * 31 + d.deckBoxIndex;
                h = h * 31 + d.playmatIndex;
                var cards = d.compactCardDataAmountList;
                if (cards == null)
                    continue;
                for (int c = 0; c < cards.Count && c < 128; c++)
                {
                    var cd = cards[c];
                    if (cd == null)
                        continue;
                    h = h * 31 + (int)cd.expansionType;
                    h = h * 31 + cd.cardSaveIndex;
                    h = h * 31 + cd.amount;
                    h = h * 31 + cd.gradedCardIndex;
                    h = h * 31 + (cd.isDestiny ? 1 : 0);
                }
            }
            return h;
        }
    }
}
