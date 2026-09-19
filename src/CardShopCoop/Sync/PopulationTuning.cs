using System;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Host-side crowd tuning: a cap on concurrent customers and a spawn-rate multiplier,
    /// applied through the game's OWN pacing rather than a spawner loop.
    ///
    /// Why here: the "Higher Population" mod force-activated customers from a coroutine
    /// every 0.1-3s until it reached its number, bypassing CustomerManager's cap and timer.
    /// That fights this mod's population reconciliation, and game 1.0 broke it anyway
    /// (ActivateCustomer grew a second parameter). The vanilla knobs are recomputed in one
    /// place - <c>CustomerManager.EvaluateMaxCustomerCount</c> sets <c>m_CustomerCountMax</c>
    /// (clamped 3..30 plus a deco bonus) and <c>m_TimePerCustomer</c> (4..10s) - so a postfix
    /// there is the whole feature: the game's Update spawns at its usual cadence up to the
    /// new cap, AddCustomerPrefab grows the pool on demand, and NpcSync mirrors the crowd to
    /// every guest. Applied on the host and in single player; never on a guest, whose
    /// CustomerManager.Update is blocked anyway.
    /// </summary>
    internal static class PopulationTuning
    {
        public static void ApplyPatches(Harmony h)
        {
            var original = AccessTools.Method(typeof(CustomerManager), "EvaluateMaxCustomerCount");
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("PopulationTuning patch target missing: CustomerManager.EvaluateMaxCustomerCount");
                return;
            }
            h.Patch(original, postfix: new HarmonyMethod(typeof(PopulationTuning), nameof(EvaluateMaxCustomerCountPostfix)));
            // --- fv-912 traffic-zero-at-close begin
            var enter = AccessTools.Method(typeof(CustomerManager), "CustomerEnterShop");
            var update = AccessTools.Method(typeof(CustomerManager), "Update");
            if (enter == null || update == null)
                CoopPlugin.Log.LogWarning("PopulationTuning patch target missing: CustomerManager.CustomerEnterShop/Update - StopAtClose inert");
            else
            {
                h.Patch(update, prefix: new HarmonyMethod(typeof(PopulationTuning), nameof(UpdateClosedPrefix)));
                h.Patch(enter, prefix: new HarmonyMethod(typeof(PopulationTuning), nameof(CustomerEnterShopPrefix)));
            }
            // --- fv-912 traffic-zero-at-close end
        }

        /// <summary>Re-run the game's evaluation now (after a live config change).</summary>
        public static void Reapply()
        {
            try
            {
                var cm = UnityEngine.Object.FindObjectOfType<CustomerManager>(); // never CSingleton: it mints a fake
                var mi = AccessTools.Method(typeof(CustomerManager), "EvaluateMaxCustomerCount");
                if (cm != null && mi != null)
                    mi.Invoke(cm, null);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PopulationTuning.Reapply: " + e.Message); }
        }

        public static void EvaluateMaxCustomerCountPostfix(CustomerManager __instance)
        {
            try
            {
                if (CoopCore.Role == CoopRole.Client || __instance == null)
                    return;
                int cap = CoopPlugin.MaxCustomers != null ? CoopPlugin.MaxCustomers.Value : 0;
                float rate = CoopPlugin.SpawnRateMultiplier != null ? CoopPlugin.SpawnRateMultiplier.Value : 1f;
                if (cap > 0)
                    __instance.m_CustomerCountMax = Mathf.Clamp(cap, 3, 300);
                if (rate > 0f && !Mathf.Approximately(rate, 1f))
                    __instance.m_TimePerCustomer = Mathf.Max(0.5f, __instance.m_TimePerCustomer / Mathf.Clamp(rate, 0.1f, 10f));
                // Rivals: the price race moves the crowd (cheapest shop draws more, priciest fewer)
                float rivals = Rivals.RivalsLobby.CrowdMultiplier;
                if (rivals > 0f && !Mathf.Approximately(rivals, 1f))
                {
                    __instance.m_CustomerCountMax = Mathf.Clamp(Mathf.RoundToInt(__instance.m_CustomerCountMax * rivals), 3, 300);
                    __instance.m_TimePerCustomer = Mathf.Max(0.5f, __instance.m_TimePerCustomer / rivals);
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("PopulationTuning: " + e.Message);
            }
        }

        // --- fv-912 traffic-zero-at-close begin
        // The crowd faucet is CustomerManager.Update: every m_TimePerCustomer seconds it calls
        // CustomerEnterShop, which activates one pooled customer. Vanilla only turns that off
        // at the 21:00 day end (m_IsDayEnded); flipping the sign to CLOSED does nothing to it,
        // so walk-ups keep spawning, reach the door, get confused and leave - a queue that
        // never stops forming. Here "closed" is EITHER of those: the day has ended, or the
        // sign says CLOSED after the shop opened today (m_IsShopOnceOpen - reset each
        // morning, so the pre-opening crowd that gathers at the door is untouched), or a league
        // shop held at end-of-day by LeagueDaySync until every shop is READY. Host and
        // solo only: a guest never runs CustomerManager.Update (ClientBlockPrefix) and sees
        // the host's crowd through NpcSync, and the sign/clock it reads are the host's.

        /// <summary>True while arrivals are gated to 0 (host / solo; latched by UpdateClosedPrefix).</summary>
        public static bool ArrivalsStopped { get; private set; }

        private static bool s_wasClosed;

        private static bool StopAtClose => CoopPlugin.StopAtClose == null || CoopPlugin.StopAtClose.Value;

        /// <summary>Why the shop counts as closed right now, or null when it is open.</summary>
        private static string ClosedReason()
        {
            // A league shop parked at end-of-day waiting for the other shops to press READY
            // (fv-871: 21:00 reached / the recap up, nothing advancing). Checked against
            // LeagueDaySync's own stage, not just the vanilla flag, so the hold is explicit.
            if (Rivals.LeagueDaySync.Playing)
            {
                string stage = Rivals.LeagueDaySync.MyStage();
                if (stage == Rivals.LeagueDaySync.StageClosed || stage == Rivals.LeagueDaySync.StageReport)
                    return "league day-end wait";
            }
            if (LightManager.GetHasDayEnded())
                return "day end";
            if (CPlayerData.m_IsShopOnceOpen && !CPlayerData.m_IsShopOpen)
                return "sign CLOSED";
            return null;
        }

        /// <summary>Runs once a frame on the host / solo PC: latches the closed state and logs
        /// the transition. Never skips the original (out-of-bounds sweeps etc. still run).</summary>
        public static void UpdateClosedPrefix(CustomerManager __instance)
        {
            try
            {
                if (CoopCore.Role == CoopRole.Client || __instance == null)
                    return;
                string reason = StopAtClose ? ClosedReason() : null;
                bool closed = reason != null;
                ArrivalsStopped = closed;
                if (closed == s_wasClosed)
                    return;
                s_wasClosed = closed;
                if (closed)
                    CoopPlugin.Log.LogInfo("population: shop closed - arrivals 0 (" + reason + ", " + LightManager.GetTimeHour() + "h)");
                else
                    CoopPlugin.Log.LogInfo("population: shop open - arrivals resume");
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PopulationTuning.UpdateClosedPrefix: " + e.Message); }
        }

        /// <summary>The gate: no new customer while closed. The vanilla timer keeps ticking, so
        /// the next arrival follows the usual cadence as soon as the sign opens again.</summary>
        public static bool CustomerEnterShopPrefix()
        {
            if (CoopCore.Role == CoopRole.Client)
                return true;
            return !ArrivalsStopped;
        }
        // --- fv-912 traffic-zero-at-close end
    }
}
