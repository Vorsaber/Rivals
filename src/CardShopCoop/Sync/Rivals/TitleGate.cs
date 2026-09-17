using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>While you are in a league lobby the title screen's New Game / Continue / Load
    /// Game buttons go away: a league shop is entered through the lobby's START, never through
    /// the game's own menu (and a stray Continue would load your single-player shop under the
    /// league's nose). The buttons come back the moment you leave the lobby. Belt and braces:
    /// the handlers are also blocked, for controller navigation.</summary>
    internal static class TitleGate
    {
        private static readonly string[] Gated = { "OnPressStartGame", "OnPressLoadGame", "OpenLoadGameSlotScreen", "OnPressConfirmOverwrite" };
        private static readonly List<GameObject> s_hidden = new List<GameObject>();
        private static bool s_hiding;
        private static readonly List<CanvasGroup> s_addedGroups = new List<CanvasGroup>();
        private static float s_lastScan = -10f;

        private static bool InLobby => RivalsLobby.Role != RivalsLobby.LobbyRole.None;

        public static void ApplyPatches(Harmony h)
        {
            foreach (string name in Gated)
            {
                try
                {
                    var m = AccessTools.Method(typeof(TitleScreen), name);
                    if (m != null)
                        h.Patch(m, prefix: new HarmonyMethod(typeof(TitleGate), nameof(BlockPrefix)));
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("TitleGate " + name + ": " + e.Message); }
            }
        }

        public static bool BlockPrefix()
        {
            if (!InLobby)
                return true;
            RivalsLobby.Status = "you are in a league lobby - the host's START loads the game";
            return false;
        }

        /// <summary>Ticked from RivalsLobby.Update (every frame; re-applied once a second, because
        /// the title screen's controller extension re-enables its selectables). A gated button is
        /// hidden with a CanvasGroup (alpha 0, no raycasts) and deactivated; when every Button
        /// under its parent is gated, the parent FRAME goes too (the game draws the button's
        /// backing on that frame, which is what stayed on screen greyed out).</summary>
        public static void Tick()
        {
            bool want = InLobby;
            if (!want && !s_hiding)
                return;
            if (Time.unscaledTime - s_lastScan < 1f)
                return;
            s_lastScan = Time.unscaledTime;
            try
            {
                if (!want)
                {
                    Restore();
                    return;
                }
                var title = UnityEngine.Object.FindObjectOfType<TitleScreen>();
                if (title == null)
                {
                    s_hidden.RemoveAll(g => g == null);
                    return;
                }
                var gated = new List<Button>();
                foreach (var b in title.GetComponentsInChildren<Button>(true))
                    if (b != null && IsGated(b, title))
                        gated.Add(b);
                var targets = new HashSet<GameObject>();
                foreach (var b in gated)
                {
                    targets.Add(b.gameObject);
                    var parent = b.transform.parent;
                    if (parent == null || parent == title.transform)
                        continue;
                    bool allGated = true;
                    foreach (var other in parent.GetComponentsInChildren<Button>(true))
                        if (!gated.Contains(other))
                        {
                            allGated = false;
                            break;
                        }
                    if (allGated)
                        targets.Add(parent.gameObject);
                }
                foreach (var go in targets)
                    Hide(go);
                if (!s_hiding)
                {
                    var names = new List<string>();
                    foreach (var go in targets)
                        names.Add(Path(go.transform));
                    CoopPlugin.Log.LogInfo("TitleGate: hiding " + string.Join(", ", names));
                }
                s_hiding = true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TitleGate: " + e.Message); }
        }

        private static void Hide(GameObject go)
        {
            var cg = go.GetComponent<CanvasGroup>();
            if (cg == null)
            {
                cg = go.AddComponent<CanvasGroup>();
                s_addedGroups.Add(cg);
            }
            cg.alpha = 0f;
            cg.interactable = false;
            cg.blocksRaycasts = false;
            if (go.activeSelf)
                go.SetActive(false);
            if (!s_hidden.Contains(go))
                s_hidden.Add(go);
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                p = t.name + "/" + p;
            }
            return p;
        }

        private static bool IsGated(Button b, TitleScreen title)
        {
            if (b == title.m_ContinueButton || b == title.m_LoadGameButton)
                return true;
            var ev = b.onClick;
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                if (!(ev.GetPersistentTarget(i) is TitleScreen))
                    continue;
                string m = ev.GetPersistentMethodName(i);
                foreach (string g in Gated)
                    if (m == g)
                        return true;
            }
            return false;
        }

        private static void Restore()
        {
            foreach (var g in s_hidden)
                if (g != null)
                    g.SetActive(true);
            foreach (var cg in s_addedGroups)
                if (cg != null)
                    UnityEngine.Object.Destroy(cg);
            s_addedGroups.Clear();
            s_hidden.Clear();
            s_hiding = false;
        }
    }
}
