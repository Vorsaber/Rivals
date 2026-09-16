using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// End-of-day coordination. Only the host can end the day (the recap is a host-driven
    /// broadcast; see GamePatches.GoNextDayScreenBlockPrefix), so a guest's Enter after
    /// closing time used to do nothing at all. Now it is a READY toggle the host sees, and
    /// the host's own Enter waits until every guest is ready - or goes anyway on a second
    /// press. Votes clear when the day actually changes and when a guest leaves.
    /// </summary>
    public sealed class SleepVote : CoopModule
    {
        public Action<INetMessage> SendToHost;
        public Action<INetMessage> Broadcast;
        public Func<int, string> PeerName;

        private static SleepVote s_instance;
        private readonly Dictionary<int, bool> _ready = new Dictionary<int, bool>();
        private bool _hostWaiting;
        private bool _clientReady;

        public override string Name => nameof(SleepVote);

        public static void ApplyPatches(HarmonyLib.Harmony h)
        {
            try
            {
                var next = HarmonyLib.AccessTools.Method(typeof(LightManager), "GoNextDay");
                if (next != null)
                    h.Patch(next, postfix: new HarmonyLib.HarmonyMethod(typeof(SleepVote), nameof(DayChangedPostfix)));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("SleepVote patches: " + e.Message); }
        }

        public static void DayChangedPostfix()
        {
            OnDayChanged();
        }

        public SleepVote()
        {
            s_instance = this;
        }

        public override void Reset()
        {
            _ready.Clear();
            _hostWaiting = false;
            _clientReady = false;
        }

        /// <summary>The day turned over (host mirror or local): everyone starts unready.</summary>
        public static void OnDayChanged()
        {
            s_instance?.Reset();
        }

        // ---------------------------------------------------------------- client

        /// <summary>Guest pressed Enter after closing time. Returns nothing to the caller;
        /// the vanilla screen stays blocked on a guest.</summary>
        public static void ClientPressed()
        {
            var self = s_instance;
            if (self == null || CoopCore.Role != CoopRole.Client || self.SendToHost == null)
                return;
            self._clientReady = !self._clientReady;
            self.SendToHost(new SleepVoteMessage { Ready = self._clientReady });
            HostOnlyFeatures.Notice(self._clientReady
                ? "Co-op: you're ready to sleep - the host ends the day (Enter again to cancel)"
                : "Co-op: no longer ready to sleep");
        }

        public void ClientApplyStatus(SleepStatusMessage msg)
        {
            if (!string.IsNullOrEmpty(msg.Text))
                HostOnlyFeatures.Notice("Co-op: " + msg.Text);
        }

        // ---------------------------------------------------------------- host

        public void HostApplyVote(SleepVoteMessage msg, int conn)
        {
            Guarded("vote", () =>
            {
                _ready[conn] = msg.Ready;
                string who = PeerName?.Invoke(conn);
                if (string.IsNullOrEmpty(who))
                    who = "a guest";
                HostOnlyFeatures.Notice("Co-op: " + who + (msg.Ready ? " is ready to sleep" : " is no longer ready to sleep"));
                CoopPlugin.Log.LogInfo($"SleepVote: {who} ready={msg.Ready}");
                if (_hostWaiting && msg.Ready && AllGuestsReady())
                {
                    _hostWaiting = false;
                    HostOnlyFeatures.Notice("Co-op: everyone is ready - press Enter to end the day");
                }
            });
        }

        public void HostReleaseConn(int conn)
        {
            _ready.Remove(conn);
        }

        private bool AllGuestsReady()
        {
            int guests = CoopCore.Instance != null ? CoopCore.Instance.GuestCount : 0;
            if (guests <= 0)
                return true;
            int ready = 0;
            foreach (var kv in _ready)
                if (kv.Value)
                    ready++;
            return ready >= guests;
        }

        /// <summary>Host pressed Enter after closing time. True = let the vanilla recap open.
        /// First press with guests still busy: hold, tell them, and tell the host. Second
        /// press: go anyway.</summary>
        public static bool HostMayProceed()
        {
            var self = s_instance;
            if (self == null || CoopCore.Role != CoopRole.Host)
                return true;
            if (CoopPlugin.SleepVoteEnabled != null && !CoopPlugin.SleepVoteEnabled.Value)
                return true;
            try
            {
                if (self.AllGuestsReady())
                    return true;
                if (self._hostWaiting)
                {
                    self._hostWaiting = false;
                    CoopPlugin.Log.LogInfo("SleepVote: host ended the day with guests not ready");
                    return true;
                }
                self._hostWaiting = true;
                var names = new List<string>();
                if (CoopCore.Instance != null)
                    foreach (int conn in CoopCore.Instance.GuestConnections())
                        if (!self._ready.TryGetValue(conn, out bool r) || !r)
                        {
                            string n = self.PeerName?.Invoke(conn);
                            names.Add(string.IsNullOrEmpty(n) ? "a guest" : n);
                        }
                string list = string.Join(", ", names);
                HostOnlyFeatures.Notice($"Co-op: waiting for {list} to be ready (Enter again to sleep anyway)");
                string host = CoopCore.Instance != null ? CoopCore.Instance.EffectivePlayerName : "the host";
                self.Broadcast?.Invoke(new SleepStatusMessage { Text = host + " wants to end the day - press Enter when you're ready" });
                return false;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("SleepVote: " + e.Message);
                return true;
            }
        }
    }
}
