using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TcgDifficulty
{
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
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<DifficultyProfile> ProfileEntry;
        internal static ConfigEntry<float> PerPlayer;
        internal static ConfigEntry<float> CustomCap;
        internal static ConfigEntry<float> CustomRate;
        internal static ConfigEntry<float> CustomWallet;

        private float _timer;
        private int _appliedPlayers = -1;
        private DifficultyProfile _appliedProfile = (DifficultyProfile)(-1);

        private void Awake()
        {
            Log = Logger;
            ProfileEntry = Config.Bind("Difficulty", "Profile", DifficultyProfile.Normal,
                "Crowd profile. Off = the game's own numbers regardless of player count. Relaxed: fewer, richer customers. Normal: vanilla at one player. Busy / Chaos: more customers arriving faster. Custom: the Custom* multipliers below. With CardShopCoop installed the crowd also grows per extra player (PerPlayerScale) and the host's setting applies.");
            PerPlayer = Config.Bind("Difficulty", "PerPlayerScale", 0.35f,
                "How much the crowd grows per EXTRA player in a CardShopCoop session: 0.35 = a second player adds 35% more customers arriving 35% faster, a third adds another 35%. 0 = no scaling. Single player: no effect.");
            CustomCap = Config.Bind("Difficulty", "CustomCustomers", 1f,
                "Custom profile: multiplier on the customer cap (before the per-player scale).");
            CustomRate = Config.Bind("Difficulty", "CustomArrivalRate", 1f,
                "Custom profile: multiplier on how often customers arrive (before the per-player scale).");
            CustomWallet = Config.Bind("Difficulty", "CustomWallet", 1f,
                "Custom profile: multiplier on how much money customers carry.");

            var harmony = new Harmony(Guid);
            Difficulty.ApplyPatches(harmony);
            Log.LogInfo($"{Name} {Version} loaded - " + Difficulty.Describe());
        }

        /// <summary>Re-run the game's evaluation when the player count or the profile
        /// changed (a join, a leave, a config edit), so the crowd follows.</summary>
        private void Update()
        {
            _timer += Time.unscaledDeltaTime;
            if (_timer < 3f)
                return;
            _timer = 0f;
            if (!Difficulty.IsAuthority)
                return;
            int players = Difficulty.Players;
            var p = Difficulty.Profile;
            if (players == _appliedPlayers && p == _appliedProfile)
                return;
            _appliedPlayers = players;
            _appliedProfile = p;
            Difficulty.Reapply();
            if (p != DifficultyProfile.Off)
                Log.LogInfo("Difficulty: " + Difficulty.Describe());
        }
    }
}
