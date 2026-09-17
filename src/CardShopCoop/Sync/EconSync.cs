using System;
using System.Reflection;
using BepInEx.Bootstrap;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The TUNING phone app's wire: the host publishes what TcgEconomy and TcgDifficulty are
    /// set to (EconState, on change and every 10 s so a late joiner gets one), a guest keeps
    /// the last one for display. Nothing is APPLIED from it on a guest - the economy factors
    /// already apply through SettingsState, and the crowd is the host's to run - so the app
    /// can never fight the league override or a difficulty change under it.
    ///
    /// The effective (override-aware) values are read straight off the plugins' public
    /// statics by reflection here rather than through <see cref="Util.Companions"/>, which
    /// only exposes each PC's CONFIG for the league board.
    /// </summary>
    public sealed class EconSync : MonoBehaviour
    {
        public const float ResendSeconds = 10f;

        /// <summary>Guest: the host's last snapshot, or null before one arrives.</summary>
        public static EconStateMessage Remote
        {
            get; private set;
        }
        public static float RemoteAt
        {
            get; private set;
        } = -1f;

        private static int s_lastHash;
        private static float s_lastSent = -100f;
        private static float s_nextCheck;

        private void Update()
        {
            try
            {
                if (CoopCore.Role != CoopRole.Client && Remote != null)
                    Remote = null; // left the session: the host's numbers are no longer ours to show
                if (CoopCore.Role != CoopRole.Host || Time.unscaledTime < s_nextCheck)
                    return;
                s_nextCheck = Time.unscaledTime + 1f;
                var gm = CSingleton<CGameManager>.Instance;
                if (gm == null || !gm.m_IsGameLevel)
                    return;
                var core = CoopCore.Instance;
                if (core == null)
                    return;
                var msg = HostSnapshot();
                int hash = Hash(msg);
                if (hash == s_lastHash && Time.unscaledTime - s_lastSent < ResendSeconds)
                    return;
                s_lastHash = hash;
                s_lastSent = Time.unscaledTime;
                core.RelayEconState(msg);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("EconSync: " + e.Message); }
        }

        /// <summary>Guest: keep the host's snapshot.</summary>
        public static void ClientApply(EconStateMessage msg)
        {
            if (msg == null)
                return;
            Remote = msg;
            RemoteAt = Time.unscaledTime;
        }

        /// <summary>Host: force the next tick to send (after an edit in the app).</summary>
        public static void MarkDirty()
        {
            s_lastHash = 0;
            s_nextCheck = 0f;
        }

        public static EconStateMessage HostSnapshot()
        {
            var m = new EconStateMessage();
            m.EconPresent = Util.Companions.Economy.Present;
            if (m.EconPresent)
            {
                Util.Companions.Economy.EffectiveFactors(out m.EconMargin, out m.EconCard, out m.EconPick, out m.EconCost, out m.EconBill);
                m.EconProfile = Util.Companions.Economy.LocalProfile();
                m.EconOverride = Econ.HasOverride;
                m.EconText = Util.Companions.Economy.Describe();
            }
            m.DiffPresent = Util.Companions.Difficulty.Present;
            if (m.DiffPresent)
            {
                m.DiffProfile = Diff.Profile;
                m.DiffOverride = Diff.HasOverride;
                m.PerPlayer = Diff.PerPlayerScale;
                m.Staff = Diff.StaffCostPerPlayer;
                m.Players = Diff.Players;
                float v;
                m.CustomCap = Util.Companions.TryGetFloat(Util.Companions.Difficulty.Guid, "Difficulty", "CustomCustomers", out v) ? v : 1f;
                m.CustomRate = Util.Companions.TryGetFloat(Util.Companions.Difficulty.Guid, "Difficulty", "CustomArrivalRate", out v) ? v : 1f;
                m.CustomWallet = Util.Companions.TryGetFloat(Util.Companions.Difficulty.Guid, "Difficulty", "CustomWallet", out v) ? v : 1f;
                m.DiffText = Util.Companions.Difficulty.Describe();
            }
            return m;
        }

        private static int Hash(EconStateMessage m)
        {
            int h = 17;
            h = h * 31 + (m.EconPresent ? 1 : 0);
            h = h * 31 + m.EconProfile;
            h = h * 31 + (m.EconOverride ? 1 : 0);
            h = h * 31 + (int)(m.EconMargin * 1000f);
            h = h * 31 + (int)(m.EconCard * 1000f);
            h = h * 31 + (int)(m.EconPick * 1000f);
            h = h * 31 + (int)(m.EconCost * 1000f);
            h = h * 31 + (int)(m.EconBill * 1000f);
            h = h * 31 + (m.DiffPresent ? 1 : 0);
            h = h * 31 + m.DiffProfile;
            h = h * 31 + (m.DiffOverride ? 1 : 0);
            h = h * 31 + (int)(m.PerPlayer * 1000f);
            h = h * 31 + (int)(m.Staff * 1000f);
            h = h * 31 + (int)(m.CustomCap * 1000f);
            h = h * 31 + (int)(m.CustomRate * 1000f);
            h = h * 31 + (int)(m.CustomWallet * 1000f);
            h = h * 31 + m.Players;
            return h;
        }

        // ------------------------------------------------------------ effective values, by reflection

        private static object Static(string guid, string typeName, string member)
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info.Instance == null)
                    return null;
                var t = info.Instance.GetType().Assembly.GetType(typeName);
                if (t == null)
                    return null;
                var p = t.GetProperty(member, BindingFlags.Public | BindingFlags.Static);
                if (p != null)
                    return p.GetValue(null);
                var f = t.GetField(member, BindingFlags.Public | BindingFlags.Static);
                return f?.GetValue(null);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("EconSync " + typeName + "." + member + ": " + e.Message);
                return null;
            }
        }

        /// <summary>TcgEconomy.EconomyTuning statics in force on this PC.</summary>
        internal static class Econ
        {
            public static bool HasOverride => Static(Util.Companions.Economy.Guid, "TcgEconomy.EconomyTuning", "HasOverride") is bool b && b;
        }

        /// <summary>TcgDifficulty.Difficulty statics in force on this PC (override-aware).</summary>
        internal static class Diff
        {
            private const string T = "TcgDifficulty.Difficulty";
            public static bool HasOverride => Static(Util.Companions.Difficulty.Guid, T, "HasOverride") is bool b && b;
            public static int Profile
            {
                get
                {
                    var v = Static(Util.Companions.Difficulty.Guid, T, "Profile");
                    return v != null ? Convert.ToInt32(v) : 0;
                }
            }
            public static float PerPlayerScale => Static(Util.Companions.Difficulty.Guid, T, "PerPlayerScale") is float f ? f : 0.35f;
            public static float StaffCostPerPlayer => Static(Util.Companions.Difficulty.Guid, T, "StaffCostPerPlayerValue") is float f ? f : 1f;
            public static int Players => Static(Util.Companions.Difficulty.Guid, T, "Players") is int n ? n : 1;
        }
    }
}
