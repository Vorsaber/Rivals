using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace TcgDifficulty
{
    /// <summary>
    /// The v1.1 knobs - each one a small Harmony patch on the game site that owns the number.
    ///
    /// PATIENCE - <c>Customer.ThinkWantToBuyItem / ThinkWantToBuyCard / ThinkWantToGetCardFromBulkBox /
    /// ThinkWantToPlayTable</c> each compare the private <c>m_FailFindItemAttemptCount</c> against
    /// a random threshold and walk the customer out when it is exceeded (the game never times a
    /// customer out of a queue; giving up on finding something is its only patience). The
    /// prefix hands the comparison a scaled count (count / patience) and the postfix puts the
    /// real count back, preserving any increment the method made inside. Authority only - a
    /// guest's customers are puppets.
    ///
    /// DRIFT - <c>PriceChangeManager.OnDayStarted</c> rolls every item and card price by
    /// Random(m_PriceChangeMin, m_PriceChangeMax) percent (0.2..5 in game 1.0). The prefix
    /// scales that private range by the drift speed before the roll; 0 freezes the market.
    /// Authority only - CardShopCoop blocks the joiner's roll and ships the host's table.
    ///
    /// AI STRENGTH - <c>PlayCardSet.TakeDamage</c> on the ENEMY set (the AI opponent) divides
    /// the incoming damage by the strength, so a x2 opponent effectively has double HP while
    /// the guardian-card catch-up rule (keyed to real HP lost) keeps working. Every PC - a
    /// guest's battle runs on the guest. Skipped while the opponent is a human (co-op PvP:
    /// <see cref="Difficulty.HumanOpponentProvider"/>, else CardShopCoop's PvpBattle.Active
    /// by reflection), and, when <c>AiStrengthTournamentOnly</c> is set, outside a tournament day.
    /// </summary>
    internal static class Knobs
    {
        private static readonly FieldInfo FiFailCount = AccessTools.Field(typeof(Customer), "m_FailFindItemAttemptCount");
        private static readonly FieldInfo FiChangeMin = AccessTools.Field(typeof(PriceChangeManager), "m_PriceChangeMin");
        private static readonly FieldInfo FiChangeMax = AccessTools.Field(typeof(PriceChangeManager), "m_PriceChangeMax");

        public static void ApplyPatches(Harmony h)
        {
            // patience
            if (FiFailCount == null)
                Plugin.Log.LogWarning("Difficulty patience: Customer.m_FailFindItemAttemptCount missing, knob inert");
            else
            {
                foreach (var name in new[] { "ThinkWantToBuyItem", "ThinkWantToBuyCard", "ThinkWantToGetCardFromBulkBox", "ThinkWantToPlayTable" })
                {
                    var mi = AccessTools.Method(typeof(Customer), name);
                    if (mi == null)
                    {
                        Plugin.Log.LogWarning("Difficulty patience: Customer." + name + " missing");
                        continue;
                    }
                    h.Patch(mi,
                        prefix: new HarmonyMethod(typeof(Knobs), nameof(PatiencePrefix)),
                        postfix: new HarmonyMethod(typeof(Knobs), nameof(PatiencePostfix)));
                }
            }
            // drift
            var day = AccessTools.Method(typeof(PriceChangeManager), "OnDayStarted");
            if (day == null || FiChangeMin == null || FiChangeMax == null)
                Plugin.Log.LogWarning("Difficulty drift: PriceChangeManager.OnDayStarted / m_PriceChange* missing, knob inert");
            else
                h.Patch(day, prefix: new HarmonyMethod(typeof(Knobs), nameof(DriftPrefix)));
            // AI strength
            var dmg = AccessTools.Method(typeof(PlayCardSet), "TakeDamage", new[] { typeof(int) });
            if (dmg == null)
                Plugin.Log.LogWarning("Difficulty AI: PlayCardSet.TakeDamage missing, knob inert");
            else
                h.Patch(dmg, prefix: new HarmonyMethod(typeof(Knobs), nameof(AiDamagePrefix)));
        }

        // ---------------------------------------------------------------- patience

        // __state: (real count, scaled count) or null when nothing was changed
        public static void PatiencePrefix(Customer __instance, out int[] __state)
        {
            __state = null;
            try
            {
                if (__instance == null || !Difficulty.IsAuthority || Difficulty.Profile == DifficultyProfile.Off)
                    return;
                float patience = Difficulty.Patience;
                if (Mathf.Approximately(patience, 1f))
                    return;
                int real = (int)FiFailCount.GetValue(__instance);
                int scaled = Mathf.RoundToInt(real / patience);
                if (scaled == real)
                    return;
                FiFailCount.SetValue(__instance, scaled);
                __state = new[] { real, scaled };
            }
            catch (Exception e) { Plugin.Log.LogWarning("Difficulty patience: " + e.Message); }
        }

        public static void PatiencePostfix(Customer __instance, int[] __state)
        {
            if (__state == null || __instance == null)
                return;
            try
            {
                int now = (int)FiFailCount.GetValue(__instance);
                int real = __state[0], scaled = __state[1];
                if (now == scaled)
                    FiFailCount.SetValue(__instance, real);          // untouched inside: put the real count back
                else if (now == scaled + 1)
                    FiFailCount.SetValue(__instance, real + 1);      // one more failed attempt
                // anything else (a reset to 0, a jump to 5) is the game's own decision: keep it
            }
            catch (Exception e) { Plugin.Log.LogWarning("Difficulty patience: " + e.Message); }
        }

        // ---------------------------------------------------------------- drift

        private static float s_changeMinBase = -1f, s_changeMaxBase = -1f;
        private static float s_driftApplied = -1f;

        public static void DriftPrefix(PriceChangeManager __instance)
        {
            try
            {
                if (__instance == null || !Difficulty.IsAuthority)
                    return;
                if (s_changeMinBase < 0f)
                {
                    // the game's constants (0.2 / 5 in 1.0), read once from the first instance seen
                    s_changeMinBase = (float)FiChangeMin.GetValue(__instance);
                    s_changeMaxBase = (float)FiChangeMax.GetValue(__instance);
                }
                float drift = Difficulty.Profile == DifficultyProfile.Off ? 1f : Difficulty.DriftSpeed;
                FiChangeMin.SetValue(__instance, s_changeMinBase * drift);
                FiChangeMax.SetValue(__instance, s_changeMaxBase * drift);
                if (!Mathf.Approximately(drift, s_driftApplied))
                {
                    s_driftApplied = drift;
                    Plugin.Log.LogInfo($"Difficulty: market drift x{drift:0.00} (day-start swings {s_changeMinBase * drift:0.00}..{s_changeMaxBase * drift:0.00}%)");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Difficulty drift: " + e.Message); }
        }

        // ---------------------------------------------------------------- AI strength

        private static bool s_pvpResolved;
        private static PropertyInfo s_pvpActive;

        /// <summary>True while this PC's battle opponent is a human, so the knob must not touch it.</summary>
        private static bool HumanOpponent()
        {
            try
            {
                if (Difficulty.HumanOpponentProvider != null)
                    return Difficulty.HumanOpponentProvider();
                if (!s_pvpResolved)
                {
                    // CardShopCoop, if present: PvpBattle.Active (static bool). Resolved once
                    // it has loaded (plugin load order is not ours to pick); by GUID first,
                    // then any loaded plugin whose assembly carries the type.
                    Type t = null;
                    if (Chainloader.PluginInfos.TryGetValue("com.zwhit.cardshopcoop", out var info) && info.Instance != null)
                        t = info.Instance.GetType().Assembly.GetType("CardShopCoop.Sync.PvpBattle");
                    if (t == null)
                    {
                        foreach (var kv in Chainloader.PluginInfos)
                        {
                            if (kv.Value.Instance == null)
                                continue;
                            t = kv.Value.Instance.GetType().Assembly.GetType("CardShopCoop.Sync.PvpBattle");
                            if (t != null)
                                break;
                        }
                    }
                    if (t == null)
                        return false; // not loaded (yet): look again next time
                    s_pvpActive = t.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
                    s_pvpResolved = true;
                }
                return s_pvpActive != null && (bool)s_pvpActive.GetValue(null, null);
            }
            catch { return false; }
        }

        private static bool TournamentNow()
        {
            try
            {
                var t = CPlayerData.m_TournamentData;
                return t != null && t.m_IsTournamentDay && !t.m_IsTournamentDayOver;
            }
            catch { return false; }
        }

        public static void AiDamagePrefix(PlayCardSet __instance, ref int damage)
        {
            try
            {
                if (__instance == null || __instance.m_IsPlayer || damage <= 0)
                    return;
                if (Difficulty.Profile == DifficultyProfile.Off)
                    return;
                float ai = Difficulty.AiStrength;
                if (Mathf.Approximately(ai, 1f))
                    return;
                if (Plugin.AiTournamentOnly != null && Plugin.AiTournamentOnly.Value && !TournamentNow())
                    return;
                if (HumanOpponent())
                    return;
                damage = Mathf.Max(1, Mathf.RoundToInt(damage / ai));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Difficulty AI: " + e.Message); }
        }
    }
}
