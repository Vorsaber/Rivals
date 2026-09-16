using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
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
    /// Host-side difficulty companion: a PROFILE of multipliers over the three crowd knobs
    /// the game computes in <c>CustomerManager.EvaluateMaxCustomerCount</c> - the customer
    /// cap (<c>m_CustomerCountMax</c>), the arrival cadence (<c>m_TimePerCustomer</c>) and the
    /// customer wallet (<c>m_CustomerMaxMoney</c>) - WEIGHTED BY HOW MANY PEOPLE ARE PLAYING.
    /// Two pairs of hands clear a queue twice as fast, so the crowd grows with the player
    /// count (<c>Difficulty.PerPlayerScale</c> per extra player) and the profile sets where
    /// it starts. Re-applied whenever a player joins or leaves.
    ///
    /// Precedence: an explicit <c>Population.MaxCustomers</c> / <c>SpawnRateMultiplier</c>
    /// still wins over the profile (<see cref="PopulationTuning"/> runs after this). Never on
    /// a guest - the crowd is simulated on the host and mirrored.
    /// </summary>
    public sealed class Difficulty : TickableCoopModule
    {
        public Func<int> PeerCount;

        private static readonly MethodInfo MiEvaluate = AccessTools.Method(typeof(CustomerManager), "EvaluateMaxCustomerCount");
        private static readonly FieldInfo FiMaxMoney = AccessTools.Field(typeof(CustomerManager), "m_CustomerMaxMoney");
        private static Difficulty s_instance;
        private static int s_players = 1;
        private int _appliedPlayers = -1;
        private DifficultyProfile _appliedProfile = (DifficultyProfile)(-1);
        private float _timer;

        public override string Name => nameof(Difficulty);

        public Difficulty()
        {
            s_instance = this;
        }

        public static void ApplyPatches(Harmony h)
        {
            if (MiEvaluate == null)
            {
                CoopPlugin.Log.LogWarning("Difficulty patch target missing: CustomerManager.EvaluateMaxCustomerCount");
                return;
            }
            // BEFORE PopulationTuning's postfix (registered later in the catalog), so explicit
            // Population.* values still override the profile
            h.Patch(MiEvaluate, postfix: new HarmonyMethod(typeof(Difficulty), nameof(EvaluatePostfix)) { priority = Priority.High });
        }

        // ---------------------------------------------------------------- profiles

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
                    cap = CoopPlugin.DifficultyCustomCap != null ? CoopPlugin.DifficultyCustomCap.Value : 1f;
                    rate = CoopPlugin.DifficultyCustomRate != null ? CoopPlugin.DifficultyCustomRate.Value : 1f;
                    wallet = CoopPlugin.DifficultyCustomWallet != null ? CoopPlugin.DifficultyCustomWallet.Value : 1f;
                    break;
                default:
                    cap = 1f;
                    rate = 1f;
                    wallet = 1f;
                    break;
            }
        }

        public static DifficultyProfile Profile
        {
            get
            {
                return CoopPlugin.DifficultyProfile != null ? CoopPlugin.DifficultyProfile.Value : DifficultyProfile.Off;
            }
        }

        /// <summary>The multipliers in force right now, for the cheat menu's readout.</summary>
        public static string Describe()
        {
            var p = Profile;
            if (p == DifficultyProfile.Off)
                return "Off (vanilla)";
            Effective(p, s_players, out float cap, out float rate, out float wallet);
            return $"{p}, {s_players} player{(s_players == 1 ? "" : "s")}: customers x{cap:0.00}, arrivals x{rate:0.00}, wallets x{wallet:0.00}";
        }

        private static void Effective(DifficultyProfile p, int players, out float cap, out float rate, out float wallet)
        {
            Base(p, out cap, out rate, out wallet);
            float per = CoopPlugin.DifficultyPerPlayer != null ? CoopPlugin.DifficultyPerPlayer.Value : 0.35f;
            float crowd = 1f + Mathf.Clamp(per, 0f, 2f) * Mathf.Max(0, players - 1);
            cap *= crowd;
            rate *= crowd;
        }

        // ---------------------------------------------------------------- apply

        public static void EvaluatePostfix(CustomerManager __instance)
        {
            try
            {
                if (CoopCore.Role == CoopRole.Client || __instance == null)
                    return;
                var p = Profile;
                if (p == DifficultyProfile.Off)
                    return;
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
            catch (Exception e) { CoopPlugin.Log.LogWarning("Difficulty: " + e.Message); }
        }

        /// <summary>Host / solo: re-run the game's own evaluation when the player count or the
        /// profile changed, so the crowd follows joins and leaves.</summary>
        protected override void OnHostTick(in SyncFrame frame) => Tick(frame.Dt, frame.InGame);

        public void Tick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            _timer += dt;
            if (_timer < 3f)
                return;
            _timer = 0f;
            int players = 1 + (PeerCount != null ? Mathf.Max(0, PeerCount()) : 0);
            var p = Profile;
            if (players == _appliedPlayers && p == _appliedProfile)
                return;
            s_players = players;
            Reapply();
            _appliedPlayers = players;
            _appliedProfile = p;
            if (p != DifficultyProfile.Off)
                CoopPlugin.Log.LogInfo("Difficulty: " + Describe());
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
            catch (Exception e) { CoopPlugin.Log.LogWarning("Difficulty.Reapply: " + e.Message); }
        }

        /// <summary>Cheat menu / config: switch profile live.</summary>
        public static void SetProfile(DifficultyProfile p)
        {
            if (CoopPlugin.DifficultyProfile != null)
                CoopPlugin.DifficultyProfile.Value = p;
            var self = s_instance;
            if (self != null)
                self._appliedProfile = (DifficultyProfile)(-1); // next tick re-applies and logs
            Reapply();
        }

        public override void Reset()
        {
            _appliedPlayers = -1;
            _appliedProfile = (DifficultyProfile)(-1);
            _timer = 0f;
        }
    }
}
