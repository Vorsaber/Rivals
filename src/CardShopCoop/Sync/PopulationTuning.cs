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
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("PopulationTuning: " + e.Message);
            }
        }
    }
}
