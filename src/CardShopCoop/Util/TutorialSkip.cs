using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>Mark the tutorial finished on the running world. Shared by the cheat menu's
    /// "Finish the tutorial" and a fresh league game (tutorial off by design).</summary>
    internal static class TutorialSkip
    {
        public static void ApplyPatches(HarmonyLib.Harmony h)
        {
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(TutorialManager), "OnGameDataFinishLoaded");
                if (m != null)
                    h.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(TutorialSkip), nameof(LoadedPostfix)));
            }
            catch (System.Exception e) { CoopPlugin.Log.LogWarning("TutorialSkip patch: " + e.Message); }
        }

        /// <summary>Every load: the save's own "finished" flag persists, but the manager
        /// reopens panels from the task values regardless - honour the flag.</summary>
        public static void LoadedPostfix()
        {
            try
            {
                if (CPlayerData.m_HasFinishedTutorial || CPlayerData.m_TutorialIndex >= 99)
                    Finish();
            }
            catch (System.Exception e) { CoopPlugin.Log.LogWarning("TutorialSkip on load: " + e.Message); }
        }

        public static void Finish()
        {
            CPlayerData.m_HasFinishedTutorial = true;
            CPlayerData.m_TutorialIndex = 99;
            // what actually persists: the per-task values in the save. On load the manager
            // re-derives every panel's finished state from these (EvaluateTaskVisibility),
            // so without them the first panel comes back on the next load (Dan, 2026-09-16:
            // "tutorial came back in rivals")
            try
            {
                var list = CPlayerData.m_TutorialDataList ?? (CPlayerData.m_TutorialDataList = new System.Collections.Generic.List<TutorialData>());
                foreach (ETutorialTaskCondition cond in System.Enum.GetValues(typeof(ETutorialTaskCondition)))
                {
                    var entry = list.Find(t => t != null && t.tutorialTaskCondition == cond);
                    if (entry == null)
                        list.Add(new TutorialData { tutorialTaskCondition = cond, value = 100000f });
                    else if (entry.value < 100000f)
                        entry.value = 100000f;
                }
            }
            catch { }
            var tm = Object.FindObjectOfType<TutorialManager>();
            if (tm != null)
            {
                // mark every subgroup's task finished first, otherwise EvaluateTaskVisibility
                // reopens the first unfinished panel and rewrites the index
                var fi = HarmonyLib.AccessTools.Field(typeof(TutorialSubGroup), "m_IsTaskFinish");
                if (tm.m_TutorialSubGroupList != null)
                    foreach (var sg in tm.m_TutorialSubGroupList)
                    {
                        if (sg == null)
                            continue;
                        try
                        {
                            fi?.SetValue(sg, true);
                        }
                        catch { }
                        try
                        {
                            sg.CloseScreen();
                        }
                        catch { }
                    }
                try
                {
                    tm.EvaluateTaskVisibility();
                }
                catch { }
                CPlayerData.m_TutorialIndex = 99;
                if (tm.m_TutorialTargetIndicator != null)
                    tm.m_TutorialTargetIndicator.SetActive(false);
            }
            try
            {
                PlayerPrefs.SetInt("HasFinishedTutorial", 1);
            }
            catch { }
            GameUIScreen.SetGameUIVisible(isVisible: true);
        }
    }
}
