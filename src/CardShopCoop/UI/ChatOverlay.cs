using System;
using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// The in-world chat: recent lines bottom-left for a few seconds after anything arrives,
    /// and a one-line input opened with the chat key (Enter sends, Esc cancels). Owns UI mode
    /// while typing, the same way the cheat menu does, so keystrokes don't walk the player.
    /// The ping key needs no UI: it sends "needs you at &lt;nearest landmark&gt;".
    /// </summary>
    public sealed class ChatOverlay : MonoBehaviour
    {
        private const float ShowFor = 8f;
        private bool _open;
        private string _input = "";
        private bool _focusPending;
        private bool _uiModeHeld;
        private InteractionPlayerController _uiModeController;

        private static bool InGame()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel;
        }

        private void Update()
        {
            if (CoopCore.Role == CoopRole.None || !InGame())
            {
                if (_open)
                    Close();
                return;
            }
            var chatKey = CoopPlugin.ChatKey != null ? CoopPlugin.ChatKey.Value : KeyCode.T;
            var pingKey = CoopPlugin.PingKey != null ? CoopPlugin.PingKey.Value : KeyCode.G;
            if (!_open)
            {
                if (CoopUI.TextFieldFocused || CoopCore.WindowBlocksInput)
                    return;
                var ipc = InteractionPlayerController.m_Instance;
                if (ipc != null && ipc.IsInUIMode())
                    return; // a game screen is up; keys belong to it
                if (chatKey != KeyCode.None && Input.GetKeyDown(chatKey))
                {
                    _open = true;
                    _input = "";
                    _focusPending = true;
                }
                else if (pingKey != KeyCode.None && Input.GetKeyDown(pingKey))
                    Social.SendPing();
            }
            SyncUIMode();
        }

        private void SyncUIMode()
        {
            if (_open)
            {
                if (_uiModeHeld && _uiModeController != null)
                {
                    if (!_uiModeController.IsInUIMode())
                        _uiModeController.EnterUIMode();
                    return;
                }
                var ipc = InteractionPlayerController.m_Instance;
                if (ipc == null || ipc.IsInUIMode())
                    return;
                ipc.EnterUIMode();
                _uiModeHeld = true;
                _uiModeController = ipc;
                return;
            }
            if (_uiModeHeld && _uiModeController != null)
                _uiModeController.ExitUIMode();
            _uiModeHeld = false;
            _uiModeController = null;
        }

        private void Close()
        {
            _open = false;
            _input = "";
            SyncUIMode();
        }

        private void OnGUI()
        {
            if (CoopCore.Role == CoopRole.None)
                return;
            float now = Time.unscaledTime;
            bool showLines = _open || now - Social.LastLineAt < ShowFor;
            if (!showLines)
                return;
            float w = Mathf.Min(520f, Screen.width * 0.45f);
            float lineH = 20f;
            int count = Mathf.Min(6, Social.Lines.Count);
            float h = count * lineH + (_open ? 30f : 0f) + 12f;
            var rect = new Rect(16f, Screen.height - h - 60f, w, h);
            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x + 6f, rect.y + 6f, rect.width - 12f, rect.height - 12f));
            var style = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false, fontSize = 13 };
            for (int i = Social.Lines.Count - count; i < Social.Lines.Count; i++)
            {
                var l = Social.Lines[i];
                string col = l.IsPing ? "#ffd166" : "#ffffff";
                GUILayout.Label($"<color=#9ad1ff>{Escape(l.From)}</color>: <color={col}>{Escape(l.Text)}</color>", style, GUILayout.Height(lineH));
            }
            if (_open)
            {
                var e = Event.current;
                if (e.type == EventType.KeyDown)
                {
                    if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                    {
                        if (!string.IsNullOrWhiteSpace(_input))
                            Social.SendChat(_input);
                        Close();
                        e.Use();
                    }
                    else if (e.keyCode == KeyCode.Escape)
                    {
                        Close();
                        e.Use();
                    }
                }
                if (_open)
                {
                    GUI.SetNextControlName("coop_chat");
                    _input = GUILayout.TextField(_input, 200, GUILayout.Height(24f));
                    if (_focusPending)
                    {
                        GUI.FocusControl("coop_chat");
                        _focusPending = false;
                    }
                }
            }
            GUILayout.EndArea();
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("<", "‹").Replace(">", "›");
        }
    }
}
