using System;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>"Buy this?" for a visitor's buy-by-taking. One purchase at a time: the item or
    /// card waits (in the hand / on the display) until Y or N. Drawn by its own MonoBehaviour
    /// so it shows whether or not the F2 window is open; the keys work with the cursor locked,
    /// the buttons when it is free. Off via Rivals.ConfirmPurchases.</summary>
    public sealed class PurchaseConfirm : MonoBehaviour
    {
        private static string s_title, s_detail;
        private static Action s_yes, s_no;
        private static float s_openedAt;
        // an unanswered dialog is a "no" - and it must resolve inside HandEscrow's 10 s window
        // for an unreported take, or the waiting item goes untracked in the hand
        private const float Timeout = 8f;

        public static bool Pending => s_yes != null;

        public static bool Enabled => CoopPlugin.RivalsConfirmPurchases == null || CoopPlugin.RivalsConfirmPurchases.Value;

        /// <summary>Show the question; exactly one of yes/no runs later on the main thread.
        /// Returns false (and runs nothing) when another purchase is still waiting.</summary>
        public static bool Ask(string title, string detail, Action yes, Action no)
        {
            if (Pending)
                return false;
            s_title = title ?? "";
            s_detail = detail ?? "";
            s_yes = yes;
            s_no = no;
            s_openedAt = Time.unscaledTime;
            return true;
        }

        private static void Resolve(bool buy)
        {
            var yes = s_yes;
            var no = s_no;
            s_yes = s_no = null;
            try
            {
                if (buy)
                    yes?.Invoke();
                else
                    no?.Invoke();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PurchaseConfirm: " + e.Message); }
        }

        private void Update()
        {
            if (!Pending)
                return;
            if (Time.unscaledTime - s_openedAt > Timeout)
            {
                Resolve(false);
                return;
            }
            // keys here, not in OnGUI: the game's input runs with the cursor locked and OnGUI
            // only sees key events when a GUI control has focus
            if (Input.GetKeyDown(KeyCode.Y) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                Resolve(true);
            else if (Input.GetKeyDown(KeyCode.N) || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
                Resolve(false);
        }

        private void OnGUI()
        {
            if (!Pending)
                return;
            float w = Mathf.Min(460f, Screen.width - 32f);
            float h = 150f;
            var rect = new Rect((Screen.width - w) / 2f, Screen.height * 0.32f, w, h);
            GUI.Box(rect, GUIContent.none);
            GUI.Box(rect, GUIContent.none); // twice: the default box is translucent
            GUILayout.BeginArea(new Rect(rect.x + 14f, rect.y + 10f, rect.width - 28f, rect.height - 20f));
            var title = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 17, fontStyle = FontStyle.Bold, wordWrap = true };
            var detail = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13, wordWrap = true };
            GUILayout.Label(s_title, title);
            GUILayout.Label(s_detail, detail);
            int left = Mathf.CeilToInt(Timeout - (Time.unscaledTime - s_openedAt));
            GUILayout.Label($"<color=#bbbbbb>put back automatically in {Mathf.Max(0, left)} s</color>", detail);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Buy  (Y)", GUILayout.Height(28f)))
                Resolve(true);
            if (GUILayout.Button("Put back  (N)", GUILayout.Height(28f)))
                Resolve(false);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
