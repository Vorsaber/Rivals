using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TcgDifficulty
{
    public enum DifficultyProfile
    {
        Off = 0,      // the game's own numbers, whatever the player count
        Relaxed = 1,  // fewer, richer customers
        Normal = 2,   // vanilla at one player, scaled up per extra player
        Busy = 3,
        Chaos = 4,
        Custom = 5,   // the Difficulty.Custom* multipliers
    }

    /// <summary>
    /// A PROFILE of multipliers over the three crowd knobs the game computes in
    /// <c>CustomerManager.EvaluateMaxCustomerCount</c> - the customer cap
    /// (<c>m_CustomerCountMax</c>), the arrival cadence (<c>m_TimePerCustomer</c>) and the
    /// customer wallet (<c>m_CustomerMaxMoney</c>) - WEIGHTED BY HOW MANY PEOPLE ARE PLAYING.
    /// Two pairs of hands clear a queue twice as fast, so the crowd grows with the player
    /// count (<c>PerPlayerScale</c> per extra player) and the profile sets where it starts.
    ///
    /// Standalone the player count is 1. CardShopCoop, when installed, sets
    /// <see cref="PlayerCountProvider"/> (host + guests) and <see cref="AuthorityProvider"/>
    /// (false on a guest, whose crowd is mirrored from the host, so nothing is applied there).
    /// The plugin re-runs the game's own evaluation whenever the count or profile changes.
    /// </summary>
    public static class Difficulty
    {
        /// <summary>Set by CardShopCoop: how many people are in the shop (default 1).</summary>
        public static Func<int> PlayerCountProvider;
        /// <summary>Set by CardShopCoop: false on a guest (the host simulates the crowd).</summary>
        public static Func<bool> AuthorityProvider;

        private static readonly MethodInfo MiEvaluate = AccessTools.Method(typeof(CustomerManager), "EvaluateMaxCustomerCount");
        private static readonly FieldInfo FiMaxMoney = AccessTools.Field(typeof(CustomerManager), "m_CustomerMaxMoney");
        private static int s_players = 1;

        public static int Players
        {
            get
            {
                try
                {
                    int n = PlayerCountProvider != null ? PlayerCountProvider() : 1;
                    return Mathf.Max(1, n);
                }
                catch { return 1; }
            }
        }

        public static bool IsAuthority
        {
            get
            {
                try { return AuthorityProvider == null || AuthorityProvider(); }
                catch { return true; }
            }
        }

        public static DifficultyProfile Profile
        {
            get
            {
                return Plugin.ProfileEntry != null ? Plugin.ProfileEntry.Value : DifficultyProfile.Off;
            }
        }

        public static void SetProfile(int profile)
        {
            if (Plugin.ProfileEntry != null && Enum.IsDefined(typeof(DifficultyProfile), profile))
                Plugin.ProfileEntry.Value = (DifficultyProfile)profile;
            Reapply();
        }

        /// <summary>Base multipliers at ONE player: cap, arrival rate, wallet.</summary>
        private static void Base(DifficultyProfile p, out float cap, out float rate, out float wallet)
        {
            switch (p)
            {
                case DifficultyProfile.Relaxed:
                    cap = 0.7f;
                    rate = 0.7f;
                    wallet = 1.25f;
                    break;
                case DifficultyProfile.Busy:
                    cap = 1.5f;
                    rate = 1.5f;
                    wallet = 1.0f;
                    break;
                case DifficultyProfile.Chaos:
                    cap = 2.5f;
                    rate = 2.5f;
                    wallet = 0.8f;
                    break;
                case DifficultyProfile.Custom:
                    cap = Plugin.CustomCap != null ? Plugin.CustomCap.Value : 1f;
                    rate = Plugin.CustomRate != null ? Plugin.CustomRate.Value : 1f;
                    wallet = Plugin.CustomWallet != null ? Plugin.CustomWallet.Value : 1f;
                    break;
                default:
                    cap = 1f;
                    rate = 1f;
                    wallet = 1f;
                    break;
            }
        }

        private static void Effective(DifficultyProfile p, int players, out float cap, out float rate, out float wallet)
        {
            Base(p, out cap, out rate, out wallet);
            float per = Plugin.PerPlayer != null ? Plugin.PerPlayer.Value : 0.35f;
            float crowd = 1f + Mathf.Clamp(per, 0f, 2f) * Mathf.Max(0, players - 1);
            cap *= crowd;
            rate *= crowd;
        }

        /// <summary>The multipliers in force right now.</summary>
        public static string Describe()
        {
            var p = Profile;
            if (p == DifficultyProfile.Off)
                return "Off (vanilla)";
            int n = Players;
            Effective(p, n, out float cap, out float rate, out float wallet);
            return $"{p}, {n} player{(n == 1 ? "" : "s")}: customers x{cap:0.00}, arrivals x{rate:0.00}, wallets x{wallet:0.00}";
        }

        // ---------------------------------------------------------------- patches

        public static void ApplyPatches(Harmony h)
        {
            if (MiEvaluate == null)
            {
                Plugin.Log.LogWarning("Difficulty patch target missing: CustomerManager.EvaluateMaxCustomerCount");
                return;
            }
            // Priority.High: an explicit cap from another mod's postfix (CardShopCoop's
            // Population.MaxCustomers) runs after and still wins
            h.Patch(MiEvaluate, postfix: new HarmonyMethod(typeof(Difficulty), nameof(EvaluatePostfix)) { priority = Priority.High });
        }

        public static void EvaluatePostfix(CustomerManager __instance)
        {
            try
            {
                if (__instance == null || !IsAuthority)
                    return;
                var p = Profile;
                if (p == DifficultyProfile.Off)
                    return;
                s_players = Players;
                Effective(p, s_players, out float cap, out float rate, out float wallet);
                __instance.m_CustomerCountMax = Mathf.Clamp(Mathf.RoundToInt(__instance.m_CustomerCountMax * cap), 3, 300);
                if (rate > 0f)
                    __instance.m_TimePerCustomer = Mathf.Max(0.5f, __instance.m_TimePerCustomer / Mathf.Clamp(rate, 0.1f, 10f));
                if (FiMaxMoney != null && wallet > 0f)
                {
                    float money = (float)FiMaxMoney.GetValue(__instance);
                    FiMaxMoney.SetValue(__instance, Mathf.Clamp(money * wallet, 50f, 60000f));
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Difficulty: " + e.Message); }
        }

        /// <summary>Force the game to recompute (and this postfix to re-scale) now.</summary>
        public static void Reapply()
        {
            try
            {
                var cm = UnityEngine.Object.FindObjectOfType<CustomerManager>(); // never CSingleton: it mints a fake
                if (cm != null && MiEvaluate != null)
                    MiEvaluate.Invoke(cm, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Difficulty.Reapply: " + e.Message); }
        }
    }
}
