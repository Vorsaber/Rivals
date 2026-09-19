using System;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Patches
{
    /// <summary>
    /// fv-877: silence the vanilla PlayCardSetUI NullReferenceException that fires twice a
    /// frame (Update + LateUpdate) for the whole session and buried every real log line
    /// (161 MB / 435k lines in one host session).
    ///
    /// Vanilla's first statement in both methods is
    /// <c>m_PlayCardSet.m_PlayTableGame.IsPlayTableGameMode()</c>. In the live scene one
    /// PlayCardSetUI never receives <c>Init(PlayCardSet)</c> (or its set has no table), so
    /// that line throws before the body does anything. The prefix skips the body under
    /// exactly that condition - a reference null, the same test the CLR made - so gameplay
    /// is unchanged: the frame that would have thrown now simply does nothing, quietly.
    /// One line at the first skip names the object; one summary line at quit gives the count.
    /// </summary>
    public static class PlayCardSetUIGuard
    {
        private static readonly AccessTools.FieldRef<PlayCardSetUI, PlayCardSet> SetRef =
            AccessTools.FieldRefAccess<PlayCardSetUI, PlayCardSet>("m_PlayCardSet");

        private static long s_skipped;
        private static bool s_announced;
        private static bool s_quitHooked;

        public static void ApplyPatches(Harmony h)
        {
            var prefix = new HarmonyMethod(typeof(PlayCardSetUIGuard), nameof(NullTablePrefix));
            foreach (var name in new[] { "Update", "LateUpdate" })
            {
                var original = AccessTools.Method(typeof(PlayCardSetUI), name);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("PlayCardSetUI guard: target missing: PlayCardSetUI." + name);
                    continue;
                }
                h.Patch(original, prefix: prefix);
            }
            if (!s_quitHooked)
            {
                s_quitHooked = true;
                Application.quitting += ReportOnce;
            }
        }

        /// <summary>Skip the vanilla body when the very first dereference would throw.</summary>
        public static bool NullTablePrefix(PlayCardSetUI __instance)
        {
            PlayCardSet set;
            try
            {
                set = SetRef(__instance);
            }
            catch (Exception)
            {
                return true; // field access itself failed: let vanilla run and speak for itself
            }
            // reference nulls only - Unity's fake-null (destroyed object) would not NRE here
            bool setNull = (object)set == null;
            if (!setNull && (object)set.m_PlayTableGame != null)
                return true;

            s_skipped++;
            if (!s_announced)
            {
                s_announced = true;
                CoopPlugin.Log.LogInfo("PlayCardSetUI guard: '" + Path(__instance) + "' has "
                    + (setNull ? "no PlayCardSet (Init never ran)" : "a PlayCardSet with no PlayTableGame")
                    + " - skipping its Update/LateUpdate for the session (vanilla threw an NRE here every frame)");
            }
            return false;
        }

        private static void ReportOnce()
        {
            if (s_skipped > 0)
                CoopPlugin.Log.LogInfo("PlayCardSetUI guard: skipped " + (s_skipped / 2) + " frames ("
                    + s_skipped + " Update/LateUpdate calls that would each have logged an NRE)");
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
