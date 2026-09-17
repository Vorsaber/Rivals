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

        /// <summary>Ticked from RivalsLobby.Update (every frame; scans once a second).</summary>
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
                foreach (var b in title.GetComponentsInChildren<Button>(true))
                {
                    if (b == null || !b.gameObject.activeSelf || !IsGated(b, title))
                        continue;
                    b.gameObject.SetActive(false);
                    s_hidden.Add(b.gameObject);
                }
                s_hiding = true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TitleGate: " + e.Message); }
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
            s_hidden.Clear();
            s_hiding = false;
        }
    }
}
