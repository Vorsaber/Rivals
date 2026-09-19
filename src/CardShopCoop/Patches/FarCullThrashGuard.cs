using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Patches
{
    /// <summary>
    /// fv-877: stop the far-distance cull of a 3D card being flipped OFF in the same frame
    /// another patch just flipped it ON.
    ///
    /// Vanilla <c>Card3dUISpawner.Update</c> visits a few pooled cards per frame and, for a
    /// card farther than its simplify distance, calls
    /// <c>SetSimplifyCardDistanceCull(true)</c> then <c>CardUI.SetFarDistanceCull()</c>.
    /// Grading Overhaul 3.4.2 prefixes <c>SetSimplifyCardDistanceCull</c> and, for every card
    /// it graded, calls <c>CardUI.ResetFarDistanceCull()</c> - which SetActive(true)s the
    /// card's whole detail-UI subtree - and the very next vanilla line SetActive(false)s it
    /// again. Every visit, every far graded card: subtree ON (ActivateAwakeRecursively,
    /// OnEnable on every Graphic, a uGUI layout rebuild) then OFF. Measured live on the host
    /// 2026-09-19: 44-49% of every main-thread sample, 20 fps, and it grows with the number of
    /// graded cards in the shop.
    ///
    /// The guard is mod-agnostic: a postfix on <c>ResetFarDistanceCull</c> stamps the frame it
    /// ran on, and a prefix on <c>SetFarDistanceCull</c> skips the re-cull when the same CardUI
    /// was reset in the same frame. Vanilla never does reset-then-cull on one card in one
    /// frame (the two live in exclusive branches, and the culling loop is the only caller of
    /// SetFarDistanceCull), so without a patch like Grading Overhaul's the prefix never fires.
    /// With it, the card simply stays un-culled - which is what that patch was asking for.
    /// One log line at the first skip, one summary at quit.
    /// </summary>
    public static class FarCullThrashGuard
    {
        private static readonly Dictionary<CardUI, int> s_resetFrame = new Dictionary<CardUI, int>();
        private const int PruneAbove = 4096; // pooled CardUIs number in the hundreds; a scene reload replaces them

        private static long s_skipped;
        private static bool s_announced;
        private static bool s_quitHooked;

        public static void ApplyPatches(Harmony h)
        {
            var reset = AccessTools.Method(typeof(CardUI), "ResetFarDistanceCull");
            var cull = AccessTools.Method(typeof(CardUI), "SetFarDistanceCull");
            if (reset == null || cull == null)
            {
                CoopPlugin.Log.LogWarning("FarCull guard: target missing: CardUI."
                    + (reset == null ? "ResetFarDistanceCull" : "SetFarDistanceCull"));
                return;
            }
            h.Patch(reset, postfix: new HarmonyMethod(typeof(FarCullThrashGuard), nameof(ResetPostfix)));
            h.Patch(cull, prefix: new HarmonyMethod(typeof(FarCullThrashGuard), nameof(CullPrefix)));
            if (!s_quitHooked)
            {
                s_quitHooked = true;
                Application.quitting += ReportOnce;
            }
        }

        /// <summary>Remember the frame this card was last un-culled on.</summary>
        public static void ResetPostfix(CardUI __instance)
        {
            if ((object)__instance == null) return;
            if (s_resetFrame.Count > PruneAbove) s_resetFrame.Clear();
            s_resetFrame[__instance] = Time.frameCount;
        }

        /// <summary>Skip the re-cull when this card was un-culled earlier in the same frame.</summary>
        public static bool CullPrefix(CardUI __instance)
        {
            if ((object)__instance == null) return true;
            int frame;
            if (!s_resetFrame.TryGetValue(__instance, out frame) || frame != Time.frameCount)
                return true;

            s_skipped++;
            if (!s_announced)
            {
                s_announced = true;
                CoopPlugin.Log.LogInfo("FarCull guard: '" + Path(__instance) + "' was un-culled and re-culled in the same frame"
                    + " (another mod's patch on Card3dUIGroup.SetSimplifyCardDistanceCull) - keeping it un-culled"
                    + " instead of flipping its UI on and off every visit");
            }
            return false;
        }

        private static void ReportOnce()
        {
            if (s_skipped > 0)
                CoopPlugin.Log.LogInfo("FarCull guard: skipped " + s_skipped
                    + " same-frame re-culls (each would have been a SetActive on/off of a card's detail UI)");
        }

        private static string Path(Component c)
        {
            try
            {
                var t = c.transform;
                string p = t.name;
                while (t.parent != null)
                {
                    t = t.parent;
                    p = t.name + "/" + p;
                }
                return p;
            }
            catch (Exception) { return c != null ? c.name : "?"; }
        }
    }
}
