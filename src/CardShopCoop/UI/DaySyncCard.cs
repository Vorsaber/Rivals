using System;
using CardShopCoop.Sync.Rivals;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// fv-871: the LEAGUE CARD's ready-up. The same block sits on two surfaces: beside the
    /// vanilla end-of-day report (PhoneApps' LEAGUE - END OF DAY overlay, above the standings)
    /// where READY means "advance the day", and on a card of its own that the OPEN sign raises
    /// in the morning, where READY means "open the shop". Every playing shop is listed with
    /// its state; the day advances / the shops open on every PC at once when the last shop is
    /// ready (LeagueDaySync). The lobby host gets a Force button; the timeout is the fallback.
    /// </summary>
    public sealed class DaySyncCard : MonoBehaviour
    {
        private static DaySyncCard s_instance;
        private bool _visible;
        private Vector2 _scroll;
        private float _lastGuiError = -100f;

        // UI-mode ownership, same discipline as the cheat menu: only undo what we entered
        private bool _uiModeHeld;
        private InteractionPlayerController _uiModeController;

        private void Awake()
        {
            s_instance = this;
        }

        /// <summary>The OPEN sign was clicked in the morning: show the card.</summary>
        public static void Open()
        {
            var me = s_instance;
            if (me == null)
                return;
            if (!me._visible)
                CoopPlugin.Log.LogInfo("DaySyncCard: open (day " + LeagueDaySync.MyDay() + ")");
            me._visible = true;
        }

        public static void Close()
        {
            var me = s_instance;
            if (me != null)
                me._visible = false;
        }

        public static bool Visible => s_instance != null && s_instance._visible;

        private static InteractionPlayerController Player()
        {
            var ipc = InteractionPlayerController.m_Instance;
            if (ipc == null)
                ipc = CSingleton<InteractionPlayerController>.Instance;
            return ipc;
        }

        private void Update()
        {
            if (_visible)
            {
                // the shop opened (released, or a later free flip), the day moved on, or we left
                if (!LeagueDaySync.InLeague || LeagueDaySync.MyStage() != LeagueDaySync.StageMorning)
                    _visible = false;
                else if (Input.GetKeyDown(KeyCode.Escape))
                    _visible = false;
            }
            SyncUIMode();
        }

        private void SyncUIMode()
        {
            bool want = _visible;
            if (want)
            {
                if (_uiModeHeld && _uiModeController != null)
                {
                    if (!_uiModeController.IsInUIMode())
                        _uiModeController.EnterUIMode();
                    return;
                }
                var ipc = Player();
                if (ipc == null || ipc.IsInUIMode())
                    return; // a game screen owns UI mode; don't fight it
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

        private void OnGUI()
        {
            if (!_visible)
                return;
            try
            {
                CoopTheme.EnsureBuilt();
                float w = Mathf.Min(720f, Screen.width * 0.6f);
                float h = Mathf.Min(560f, Screen.height * 0.75f);
                var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
                CoopTheme.DrawWindowShadow(rect);
                GUI.Box(rect, GUIContent.none, CoopTheme.Window);
                GUILayout.BeginArea(rect);
                CoopTheme.DrawWindowChrome(new Rect(0f, 0f, w, h), "LEAGUE - OPEN DAY " + LeagueDaySync.MyDay(), "");
                GUILayout.Space(34f);
                GUILayout.BeginVertical(CoopTheme.ContentPanel);
                _scroll = GUILayout.BeginScrollView(_scroll);
                DrawReadyBlock(true);
                GUILayout.Space(6f);
                try
                {
                    LeaguePanel.DrawDayTable(w - 24f, true);
                }
                catch (Exception e)
                {
                    if (Event.current.type != EventType.Layout)
                        CoopPlugin.Log.LogWarning("DaySyncCard table: " + e.Message);
                }
                GUILayout.EndScrollView();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close (Esc) - the sign brings it back", CoopTheme.ButtonSecondary, GUILayout.Width(260f)))
                    _visible = false;
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.EndArea();
            }
            catch (Exception e)
            {
                if (Event.current.type == EventType.Repaint && Time.unscaledTime - _lastGuiError > 5f)
                {
                    _lastGuiError = Time.unscaledTime;
                    CoopPlugin.Log.LogWarning("DaySyncCard: " + e.Message);
                }
            }
        }

        // ---------------------------------------------------------------- the block (both surfaces)

        /// <summary>Who is ready, who we wait for, the READY button. <paramref name="boxed"/>
        /// wraps it in a section box.</summary>
        public static void DrawReadyBlock(bool boxed)
        {
            if (!LeagueDaySync.InLeague)
                return;
            string stage = LeagueDaySync.MyStage();
            int day = LeagueDaySync.MyDay();
            bool report = stage == LeagueDaySync.StageReport;
            if (!report && stage != LeagueDaySync.StageMorning)
            {
                // trading / closing time: just where the day stands
                string line = LeagueDaySync.StatusLine();
                if (line.Length > 0)
                    GUILayout.Label("<size=11>day sync: " + line + "</size>", CoopTheme.LabelDim);
                return;
            }
            if (boxed)
                GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label(report ? $"READY-UP - ADVANCE TO DAY {day + 1}" : $"READY-UP - OPEN DAY {day}", CoopTheme.SectionHeader);
            GUILayout.Label(report
                ? "<size=11>The day advances for every shop at once when every shop has pressed READY. Nothing else on the report advances it.</size>"
                : "<size=11>The shops open together when every shop has pressed READY - the clocks start in step and closing time lands together.</size>", CoopTheme.LabelDim);

            var shops = LeagueDaySync.Shops;
            var mine = LeagueDaySync.MyShopRow();
            double point = mine != null ? LeagueDaySync.ReadyPoint(mine) : 0;
            int myId = mine != null ? mine.Id : RivalsLobby.MyId;
            int ready = 0, total = 0;
            if (shops.Count == 0)
                GUILayout.Label("waiting for the lobby...", CoopTheme.LabelDim);
            foreach (var s in shops)
            {
                total++;
                // at or past my shop's ready point = done with it (ready, or already beyond)
                bool here = mine != null ? LeagueDaySync.Pos(s) >= point : s.Ready;
                if (here)
                    ready++;
                bool me = s.Id == myId;
                string tick = here ? "[READY]" : "[      ]";
                GUILayout.Label($"{tick} {s.Name}{(me ? " (you)" : "")} - day {s.Day}, {LeagueDaySync.StageText(s)}", here ? CoopTheme.Label : CoopTheme.LabelDim);
            }
            if (total > 0)
            {
                var behind = new System.Collections.Generic.List<string>();
                if (mine != null)
                    foreach (var s in shops)
                        if (s.Id != mine.Id && LeagueDaySync.Pos(s) < point)
                            behind.Add(s.Name);
                string waiting = string.Join(", ", behind);
                string line = $"Ready {ready}/{total}";
                if (mine != null && mine.Released)
                    line += report ? " - every shop is ready, day " + (day + 1) + " begins" : " - every shop is ready, opening";
                else if (waiting.Length > 0)
                    line += " - waiting for " + waiting;
                else if (mine != null && !mine.Ready)
                    line += " - waiting for you";
                string fc = LeagueDaySync.ForceCountdown();
                if (fc.Length > 0 && !(mine != null && mine.Released))
                    line += "  (host override: " + fc + ")";
                GUILayout.Label(line, CoopTheme.LabelWarn);
            }

            GUILayout.BeginHorizontal();
            if (LeagueDaySync.CanVote)
            {
                bool r = LeagueDaySync.MyReady;
                if (GUILayout.Button(r ? "READY - click to cancel" : (report ? "READY - advance the day" : "READY - open the shop"), r ? CoopTheme.ButtonPrimary : CoopTheme.ButtonSecondary, GUILayout.Width(220f)))
                    LeagueDaySync.ToggleReady();
            }
            else if (CoopCore.Role == CoopRole.Client)
                GUILayout.Label("your captain presses READY for the shop", CoopTheme.LabelDim);
            if (RivalsLobby.Role == RivalsLobby.LobbyRole.Server)
            {
                GUILayout.FlexibleSpace();
                // the admin override: locked until a shop has waited the timeout out (0 = never)
                bool unlocked = LeagueDaySync.ForceUnlocked();
                string fc = LeagueDaySync.ForceCountdown();
                GUI.enabled = unlocked;
                if (GUILayout.Button(unlocked ? "Force - release the ready shops" : (fc.Length > 0 ? fc : "Force (no timeout set)"), CoopTheme.ButtonDanger, GUILayout.Width(220f)))
                    LeagueDaySync.HostForce();
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
            if (boxed)
                GUILayout.EndVertical();
        }
    }
}
