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

    /// <summary>The six multipliers a profile sets, all 1 = vanilla.</summary>
    public struct ProfileValues
    {
        public float Cap;       // customer cap (m_CustomerCountMax)
        public float Rate;      // arrival cadence (m_TimePerCustomer, inverted)
        public float Wallet;    // customer wallet (m_CustomerMaxMoney)
        public float Patience;  // failed find-something attempts a customer tolerates before walking out
        public float Drift;     // size of the day-start market price swings (PriceChangeManager)
        public float Ai;        // NPC opponent toughness in card battles (damage it takes is divided by this)
    }

    /// <summary>
    /// A PROFILE of multipliers over the crowd knobs the game computes in
    /// <c>CustomerManager.EvaluateMaxCustomerCount</c> - the customer cap
    /// (<c>m_CustomerCountMax</c>), the arrival cadence (<c>m_TimePerCustomer</c>) and the
    /// customer wallet (<c>m_CustomerMaxMoney</c>) - WEIGHTED BY HOW MANY PEOPLE ARE PLAYING.
    /// Two pairs of hands clear a queue twice as fast, so the crowd grows with the player
    /// count (<c>PerPlayerScale</c> per extra player) and the profile sets where it starts.
    ///
    /// v1.1 adds three more knobs per profile - customer PATIENCE (how many failed attempts to
    /// find something before they walk out), market DRIFT speed (the size of each day-start
    /// price swing) and NPC AI strength in card battles - and a CURVE per profile: its own
    /// per-extra-player slope and an optional ramp per in-game day (see <see cref="Curve"/>).
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
        /// <summary>Set by another mod: true while the card battle on this PC is against a
        /// HUMAN (co-op PvP), so the AI-strength knob leaves it alone. When null the plugin
        /// looks for CardShopCoop's <c>PvpBattle.Active</c> by reflection.</summary>
        public static Func<bool> HumanOpponentProvider;

        // Set by another mod (CardShopCoop's Rivals league): the league host's settings apply
        // here instead of this PC's config. -1 / negative = not overridden.
        private static bool s_override;
        private static int s_oProfile;
        private static float s_oPerPlayer = 0.35f, s_oStaff = 1f;

        public static void SetOverride(int profile, float perPlayer, float staffPerPlayer)
        {
            s_override = true;
            s_oProfile = profile;
            s_oPerPlayer = perPlayer;
            s_oStaff = staffPerPlayer;
            Reapply();
            ApplyStaffCosts();
            Announce("league override");
        }

        public static void ClearOverride()
        {
            if (!s_override)
                return;
            s_override = false;
            Reapply();
            ApplyStaffCosts();
            Announce("league override cleared");
        }

        // --- fv-874 difficulty-log-line begin
        /// <summary>Log the multipliers in force right now, from the switch itself (the 3 s
        /// tick in <see cref="Plugin"/> only notices a change after the fact, and never a
        /// switch to Off). Also marks the tuple applied so the tick does not log it again.</summary>
        private static void Announce(string why)
        {
            try
            {
                Plugin.Log.LogInfo($"Difficulty: {Describe()} ({why})");
                Plugin.MarkApplied();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Difficulty.Announce: " + e.Message); }
        }
        // --- fv-874 difficulty-log-line end

        public static bool HasOverride => s_override;

        private static readonly MethodInfo MiEvaluate = AccessTools.Method(typeof(CustomerManager), "EvaluateMaxCustomerCount");
        private static readonly FieldInfo FiMaxMoney = AccessTools.Field(typeof(CustomerManager), "m_CustomerMaxMoney");
        private static int s_players = 1;

        // staff costs: the worker data assets are scaled IN PLACE (every screen and the daily
        // salary bill read the fields directly), with the originals kept so the factor can move
        private static readonly System.Collections.Generic.Dictionary<WorkerData, (float hire, float day)> s_staffBase =
            new System.Collections.Generic.Dictionary<WorkerData, (float hire, float day)>();
        private static float s_staffApplied = 1f;

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
                if (s_override && Enum.IsDefined(typeof(DifficultyProfile), s_oProfile))
                    return (DifficultyProfile)s_oProfile;
                return Plugin.ProfileEntry != null ? Plugin.ProfileEntry.Value : DifficultyProfile.Off;
            }
        }

        /// <summary>The global per-extra-player slope (the league host's under an override).
        /// A profile's curve may replace it - see <see cref="Curve"/>.</summary>
        public static float PerPlayerScale => s_override ? s_oPerPlayer : (Plugin.PerPlayer != null ? Plugin.PerPlayer.Value : 0.35f);
        public static float StaffCostPerPlayerValue => s_override ? s_oStaff : (Plugin.StaffCostPerPlayer != null ? Plugin.StaffCostPerPlayer.Value : 1f);

        public static void SetProfile(int profile)
        {
            bool known = Plugin.ProfileEntry != null && Enum.IsDefined(typeof(DifficultyProfile), profile);
            bool changed = known && Plugin.ProfileEntry.Value != (DifficultyProfile)profile;
            if (known)
                Plugin.ProfileEntry.Value = (DifficultyProfile)profile;
            Reapply();
            ApplyStaffCosts();
            Announce(!known ? "unknown profile " + profile + ", kept" : changed ? "profile set" : "profile re-set, unchanged");
        }

        /// <summary>Base multipliers at ONE player, day 1.</summary>
        public static ProfileValues Base(DifficultyProfile p)
        {
            var v = new ProfileValues { Cap = 1f, Rate = 1f, Wallet = 1f, Patience = 1f, Drift = 1f, Ai = 1f };
            switch (p)
            {
                case DifficultyProfile.Relaxed:
                    v.Cap = 0.7f;
                    v.Rate = 0.7f;
                    v.Wallet = 1.25f;
                    v.Patience = 1.5f;  // they browse longer before giving up
                    v.Drift = 0.7f;     // a calmer market
                    v.Ai = 0.8f;
                    break;
                case DifficultyProfile.Busy:
                    v.Cap = 1.5f;
                    v.Rate = 1.5f;
                    v.Wallet = 1.0f;
                    v.Patience = 0.8f;
                    v.Drift = 1.25f;
                    v.Ai = 1.2f;
                    break;
                case DifficultyProfile.Chaos:
                    v.Cap = 2.5f;
                    v.Rate = 2.5f;
                    v.Wallet = 0.8f;
                    v.Patience = 0.6f;  // empty shelf, they are gone
                    v.Drift = 1.75f;
                    v.Ai = 1.5f;
                    break;
                case DifficultyProfile.Custom:
                    v.Cap = Plugin.CustomCap != null ? Plugin.CustomCap.Value : 1f;
                    v.Rate = Plugin.CustomRate != null ? Plugin.CustomRate.Value : 1f;
                    v.Wallet = Plugin.CustomWallet != null ? Plugin.CustomWallet.Value : 1f;
                    v.Patience = Plugin.CustomPatience != null ? Plugin.CustomPatience.Value : 1f;
                    v.Drift = Plugin.CustomDrift != null ? Plugin.CustomDrift.Value : 1f;
                    v.Ai = Plugin.CustomAi != null ? Plugin.CustomAi.Value : 1f;
                    break;
            }
            return v;
        }

        // ---------------------------------------------------------------- curves

        /// <summary>The crowd multiplier's growth for a profile: 1 + slope x (players - 1),
        /// then x (1 + ramp x (day - 1)) capped at (1 + rampCap). Slope comes from the profile's
        /// own <c>[Curve.X] PerPlayerScale</c> when set (&gt;= 0), else the global
        /// <see cref="PerPlayerScale"/>; under a league override the host's global slope is
        /// used so every shop in the league grows the same way.</summary>
        public static float Curve(DifficultyProfile p, int players, int day)
        {
            var curve = Plugin.CurveFor(p);
            float slope = PerPlayerScale;
            if (!s_override && curve != null && curve.PerPlayer.Value >= 0f)
                slope = curve.PerPlayer.Value;
            float crowd = 1f + Mathf.Clamp(slope, 0f, 2f) * Mathf.Max(0, players - 1);
            if (curve != null && curve.DayRamp.Value > 0f && day > 1)
            {
                float ramp = Mathf.Min(curve.DayRamp.Value * (day - 1), Mathf.Max(0f, curve.DayRampCap.Value));
                crowd *= 1f + ramp;
            }
            return crowd;
        }

        /// <summary>The in-game day, 1-based (the ramp axis). 1 outside a loaded game.</summary>
        public static int Day
        {
            get
            {
                try { return Mathf.Max(1, CPlayerData.m_CurrentDay + 1); }
                catch { return 1; }
            }
        }

        /// <summary>The multipliers in force for a profile, player count and day: cap and
        /// rate follow the curve, the other four are the profile's base values.</summary>
        public static ProfileValues Effective(DifficultyProfile p, int players, int day)
        {
            var v = Base(p);
            float crowd = Curve(p, players, day);
            v.Cap *= crowd;
            v.Rate *= crowd;
            return v;
        }

        /// <summary>The knobs in force right now (1 = vanilla everywhere when Off).</summary>
        public static ProfileValues Current
        {
            get
            {
                var p = Profile;
                if (p == DifficultyProfile.Off)
                    return new ProfileValues { Cap = 1f, Rate = 1f, Wallet = 1f, Patience = 1f, Drift = 1f, Ai = 1f };
                return Effective(p, Players, Day);
            }
        }

        /// <summary>Patience multiplier now: >1 customers try longer before walking out, &lt;1 they
        /// give up sooner. Clamped 0.25..4.</summary>
        public static float Patience => Mathf.Clamp(Current.Patience, 0.25f, 4f);
        /// <summary>Market drift speed now: 0 freezes the day-start price swings, 2 doubles them. Clamped 0..5.</summary>
        public static float DriftSpeed => Mathf.Clamp(Current.Drift, 0f, 5f);
        /// <summary>NPC battle strength now: the damage the AI opponent takes is divided by this. Clamped 0.25..4.</summary>
        public static float AiStrength => Mathf.Clamp(Current.Ai, 0.25f, 4f);

        /// <summary>The multipliers in force right now.</summary>
        public static string Describe()
        {
            var p = Profile;
            int n = Players;
            string staff = Mathf.Approximately(StaffFactor(n), 1f) ? "" : $", staff hire+wages x{StaffFactor(n):0.00}";
            string src = s_override ? " [league]" : "";
            if (p == DifficultyProfile.Off)
                return "Off (vanilla)" + staff + src;
            int day = Day;
            var v = Effective(p, n, day);
            string ramp = "";
            var c = Plugin.CurveFor(p);
            if (c != null && c.DayRamp.Value > 0f)
                ramp = $" (day {day} ramp)";
            string ai = Plugin.AiTournamentOnly != null && Plugin.AiTournamentOnly.Value ? "tournament AI" : "AI";
            return $"{p}, {n} player{(n == 1 ? "" : "s")}{ramp}: customers x{v.Cap:0.00}, arrivals x{v.Rate:0.00}, wallets x{v.Wallet:0.00}, patience x{v.Patience:0.00}, drift x{v.Drift:0.00}, {ai} x{v.Ai:0.00}{staff}{src}";
        }

        // ---------------------------------------------------------------- staff costs

        /// <summary>1 + StaffCostPerPlayer x (players - 1): at the default 1.0, two players pay
        /// double to hire and keep staff, three pay triple. Independent of the crowd profile,
        /// and applied on every PC in a session (the guest's hire screen must show the price
        /// the host will charge).</summary>
        public static float StaffFactor(int players)
        {
            float per = StaffCostPerPlayerValue;
            return 1f + Mathf.Clamp(per, 0f, 10f) * Mathf.Max(0, players - 1);
        }

        public static void ApplyStaffCosts()
        {
            try
            {
                var wm = UnityEngine.Object.FindObjectOfType<WorkerManager>(); // never CSingleton
                var list = wm != null ? wm.m_WorkerDataList : null;
                if (list == null)
                    return;
                float f = StaffFactor(Players);
                for (int i = 0; i < list.Count; i++)
                {
                    var wd = list[i];
                    if (wd == null)
                        continue;
                    if (!s_staffBase.TryGetValue(wd, out var b))
                    {
                        b = (wd.hiringCost, wd.costPerDay);
                        s_staffBase[wd] = b;
                    }
                    wd.hiringCost = Mathf.Round(b.hire * f * 100f) / 100f;
                    wd.costPerDay = Mathf.Round(b.day * f * 100f) / 100f;
                }
                if (!Mathf.Approximately(f, s_staffApplied))
                {
                    s_staffApplied = f;
                    Plugin.Log.LogInfo($"Difficulty: staff hire cost and wages x{f:0.00} ({Players} player(s))");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Difficulty.ApplyStaffCosts: " + e.Message); }
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
            Knobs.ApplyPatches(h);
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
                var v = Effective(p, s_players, Day);
                __instance.m_CustomerCountMax = Mathf.Clamp(Mathf.RoundToInt(__instance.m_CustomerCountMax * v.Cap), 3, 300);
                if (v.Rate > 0f)
                    __instance.m_TimePerCustomer = Mathf.Max(0.5f, __instance.m_TimePerCustomer / Mathf.Clamp(v.Rate, 0.1f, 10f));
                if (FiMaxMoney != null && v.Wallet > 0f)
                {
                    float money = (float)FiMaxMoney.GetValue(__instance);
                    FiMaxMoney.SetValue(__instance, Mathf.Clamp(money * v.Wallet, 50f, 60000f));
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
