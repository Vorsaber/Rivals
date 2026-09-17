using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>Mark the tutorial finished on the running world. Shared by the cheat menu's
    /// "Finish the tutorial" and a fresh league game (tutorial off by design).</summary>
    internal static class TutorialSkip
    {
        public static void Finish()
        {
            CPlayerData.m_HasFinishedTutorial = true;
            CPlayerData.m_TutorialIndex = 99;
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
