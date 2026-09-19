using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TcgDifficulty
{
    /// <summary>One profile's curve: how its crowd grows with players and days. Config
    /// section <c>[Curve.&lt;Profile&gt;]</c>.</summary>
    public sealed class ProfileCurve
    {
        public ConfigEntry<float> PerPlayer;   // slope per extra player; negative = use [Difficulty] PerPlayerScale
        public ConfigEntry<float> DayRamp;     // extra crowd per in-game day after day 1 (0.01 = +1%/day); 0 = flat
        public ConfigEntry<float> DayRampCap;  // the ramp stops growing here (1 = at most +100%)
    }

    /// <summary>
    /// TCG Difficulty - standalone BepInEx plugin for TCG Card Shop Simulator (game 1.0).
    /// Crowd profiles, weighted by player count when CardShopCoop is installed.
    /// See <see cref="Difficulty"/>.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.vorsaber.tcgdifficulty";
        public const string Name = "TCG Difficulty";
        public const string Version = "1.0.2";

        internal static ManualLogSource Log;
        internal static ConfigEntry<DifficultyProfile> ProfileEntry;
        internal static ConfigEntry<float> PerPlayer;
        internal static ConfigEntry<float> CustomCap;
        internal static ConfigEntry<float> CustomRate;
        internal static ConfigEntry<float> CustomWallet;
        internal static ConfigEntry<float> CustomPatience;
        internal static ConfigEntry<float> CustomDrift;
        internal static ConfigEntry<float> CustomAi;
        internal static ConfigEntry<bool> AiTournamentOnly;
        internal static ConfigEntry<float> StaffCostPerPlayer;

        // per-profile curves, indexed by DifficultyProfile (Off has none)
        private static readonly ProfileCurve[] s_curves = new ProfileCurve[6];

        internal static ProfileCurve CurveFor(DifficultyProfile p)
        {
            int i = (int)p;
            return i >= 0 && i < s_curves.Length ? s_curves[i] : null;
        }

        private float _timer;
        private static int s_appliedPlayers = -1;
        private static int s_appliedDay = -1;
        private static DifficultyProfile s_appliedProfile = (DifficultyProfile)(-1);

        // --- fv-874 difficulty-log-line begin
        /// <summary>A switch (SetProfile / SetOverride / ClearOverride) already applied and
        /// logged the current tuple: the tick must not log it a second time.</summary>
        internal static void MarkApplied()
        {
            s_appliedPlayers = Difficulty.Players;
            s_appliedDay = Difficulty.Day;
            s_appliedProfile = Difficulty.Profile;
        }
        // --- fv-874 difficulty-log-line end

        private void Awake()
        {
            Log = Logger;
            ProfileEntry = Config.Bind("Difficulty", "Profile", DifficultyProfile.Normal,
                "Crowd profile. Off = the game's own numbers regardless of player count. Relaxed: fewer, richer, more patient customers, a calmer market, softer battle opponents. Normal: vanilla at one player. Busy / Chaos: more customers arriving faster and leaving sooner, a wilder market, tougher opponents. Custom: the Custom* multipliers below. With CardShopCoop installed the crowd also grows per extra player (PerPlayerScale, or the profile's own [Curve.X] slope) and the host's setting applies.");
            PerPlayer = Config.Bind("Difficulty", "PerPlayerScale", 0.35f,
                "How much the crowd grows per EXTRA player in a CardShopCoop session: 0.35 = a second player adds 35% more customers arriving 35% faster, a third adds another 35%. 0 = no scaling. Single player: no effect. A profile's [Curve.X] PerPlayerScale, when 0 or more, replaces this for that profile.");
            CustomCap = Config.Bind("Difficulty", "CustomCustomers", 1f,
                "Custom profile: multiplier on the customer cap (before the per-player scale).");
            CustomRate = Config.Bind("Difficulty", "CustomArrivalRate", 1f,
                "Custom profile: multiplier on how often customers arrive (before the per-player scale).");
            CustomWallet = Config.Bind("Difficulty", "CustomWallet", 1f,
                "Custom profile: multiplier on how much money customers carry.");
            CustomPatience = Config.Bind("Difficulty", "CustomPatience", 1f,
                "Custom profile: customer patience. A customer who cannot find what they want tries a few more times and then walks out; 2 = they tolerate twice as many failed attempts, 0.5 = half. Built-in: Relaxed 1.5, Normal 1, Busy 0.8, Chaos 0.6. Clamped 0.25..4.");
            CustomDrift = Config.Bind("Difficulty", "CustomDriftSpeed", 1f,
                "Custom profile: market drift speed - the size of the day-start item and card price swings (vanilla rolls 0.2..5% per item per day). 2 = swings twice as big, 0 = prices never move. Built-in: Relaxed 0.7, Normal 1, Busy 1.25, Chaos 1.75. Clamped 0..5.");
            CustomAi = Config.Bind("Difficulty", "CustomAiStrength", 1f,
                "Custom profile: NPC opponent strength in card battles - the damage the AI takes is divided by this (2 = it effectively has double HP, 0.5 = half). Built-in: Relaxed 0.8, Normal 1, Busy 1.2, Chaos 1.5. Clamped 0.25..4. Never applied against a human (co-op PvP).");
            AiTournamentOnly = Config.Bind("Difficulty", "AiStrengthTournamentOnly", true,
                "true = the AI strength knob only applies to battles on a tournament day (until the tournament is over); false = every NPC battle.");
            StaffCostPerPlayer = Config.Bind("Difficulty", "StaffCostPerPlayer", 1f,
                "Staff hire cost and daily wages grow by this much per EXTRA player in a CardShopCoop session: 1 = two players pay double, three pay triple (extra hands make hired staff a luxury). 0 = off. Any profile; single player unaffected.");

            BindCurve(DifficultyProfile.Relaxed, 0.25f);
            BindCurve(DifficultyProfile.Normal, -1f);
            BindCurve(DifficultyProfile.Busy, 0.45f);
            BindCurve(DifficultyProfile.Chaos, 0.6f);
            BindCurve(DifficultyProfile.Custom, -1f);

            var harmony = new Harmony(Guid);
            Difficulty.ApplyPatches(harmony);
            Log.LogInfo($"{Name} {Version} loaded - " + Difficulty.Describe());
        }

        private void BindCurve(DifficultyProfile p, float perPlayerDefault)
        {
            string section = "Curve." + p;
            var c = new ProfileCurve();
            c.PerPlayer = Config.Bind(section, "PerPlayerScale", perPlayerDefault,
                $"{p} profile: crowd growth per EXTRA player (CardShopCoop). Negative = use the global [Difficulty] PerPlayerScale.");
            c.DayRamp = Config.Bind(section, "DayRamp", 0f,
                $"{p} profile: extra crowd per in-game day after day 1, as a fraction (0.01 = +1% customers and arrivals per day, so day 31 is +30%). 0 = flat.");
            c.DayRampCap = Config.Bind(section, "DayRampCap", 1f,
                $"{p} profile: the day ramp stops growing at this fraction (1 = at most +100%).");
            s_curves[(int)p] = c;
        }

        /// <summary>Re-run the game's evaluation when the player count, the day or the profile
        /// changed (a join, a leave, a day roll, a config edit), so the crowd follows.</summary>
        private void Update()
        {
            _timer += Time.unscaledDeltaTime;
            if (_timer < 3f)
                return;
            _timer = 0f;
            Difficulty.ApplyStaffCosts(); // every PC, every scene load (cheap: a handful of fields)
            if (!Difficulty.IsAuthority)
                return;
            int players = Difficulty.Players;
            int day = Difficulty.Day;
            var p = Difficulty.Profile;
            if (players == s_appliedPlayers && p == s_appliedProfile && day == s_appliedDay)
                return;
            s_appliedPlayers = players;
            s_appliedProfile = p;
            s_appliedDay = day;
            Difficulty.Reapply();
            if (p != DifficultyProfile.Off)
                Log.LogInfo("Difficulty: " + Difficulty.Describe());
        }
    }
}
