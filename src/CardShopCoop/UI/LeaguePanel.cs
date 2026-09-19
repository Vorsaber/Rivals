using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using CardShopCoop.Sync.Rivals;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>The LEAGUE app (P4): the live board, the end-of-day comparison with ranks, and
    /// the league chat. Also lends its day table to the overlay beside the vanilla report.</summary>
    internal static class LeaguePanel
    {
        private static string s_chat = "";

        public static void Draw(float width)
        {
            var R = RivalsLobby.Role;
            bool relayed = R == RivalsLobby.LobbyRole.None && RivalsLobby.Board.Shops.Count > 0;
            if (R == RivalsLobby.LobbyRole.None && !relayed)
            {
                GUILayout.Label("Not in a league. Host or join one on F2 > RIVALS.", CoopTheme.LabelDim);
                return;
            }
            GUILayout.Label(RivalsLobby.Status, CoopTheme.LabelDim);

            // live board
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("BOARD" + (string.IsNullOrEmpty(RivalsLobby.Board.LobbyName) ? "" : " - " + RivalsLobby.Board.LobbyName), CoopTheme.SectionHeader);
            // --- fv-683 leaderboard-v2 begin
            SeasonPanel.DrawStatus(RivalsLobby.Board);
            var shops = new List<Net.Messages.RivalsShop>(RivalsLobby.Board.Shops);
            shops.Sort((a, b) => (b.Finished ? b.FinalValue : b.ShopValue).CompareTo(a.Finished ? a.FinalValue : a.ShopValue));
            // --- fv-683 leaderboard-v2 end
            if (shops.Count == 0)
                GUILayout.Label("No shops on the board yet.", CoopTheme.LabelDim);
            for (int i = 0; i < shops.Count; i++)
            {
                var s = shops[i];
                bool mine = s.Id == RivalsLobby.MyId;
                string price = s.PriceRank < 0 ? "no prices" : (s.PriceRank == 0 ? "CHEAPEST" : "price #" + (s.PriceRank + 1)) + $" x{s.AvgMarkup:0.00}";
                string crowd = Mathf.Approximately(s.CrowdMultiplier, 1f) ? "" : $"  crowd x{s.CrowdMultiplier:0.00}";
                // --- fv-683 leaderboard-v2 begin
                GUILayout.Label($"{(mine ? "<b>" : "")}{i + 1}. {s.Name} - lvl {s.Level}, day {s.Day + 1}, {SeasonPanel.ValueText(s, RivalsLobby.Board.SeasonDays)}{(mine ? "</b>" : "")}", CoopTheme.Label);
                // --- fv-683 leaderboard-v2 end
                GUILayout.Label($"<size=10>   sales {s.SalesToday}, customers {s.CustomersToday}  |  {price}{crowd}{(s.TournamentToday ? "  TOURNAMENT TODAY" : "")}</size>", CoopTheme.LabelDim);
            }
            // --- fv-683 leaderboard-v2 begin
            SeasonPanel.DrawWeeks(RivalsLobby.Board, 3);
            // --- fv-683 leaderboard-v2 end
            GUILayout.EndVertical();

            // --- fv-871 league-day-sync begin
            DaySyncCard.DrawReadyBlock(true); // the day sync: where the shops stand, READY at a waiting point
            // --- fv-871 league-day-sync end
            DrawDayTable(width, true);

            // chat
            if (R != RivalsLobby.LobbyRole.None)
            {
                GUILayout.BeginVertical(CoopTheme.SectionBox);
                GUILayout.Label("LEAGUE CHAT", CoopTheme.SectionHeader);
                var lines = RivalsLobby.Chat;
                for (int i = Mathf.Max(0, lines.Count - 10); i < lines.Count; i++)
                    GUILayout.Label(lines[i].From + ": " + lines[i].Text, CoopTheme.Label);
                GUILayout.BeginHorizontal();
                GUI.SetNextControlName("coop_phone_lchat");
                s_chat = GUILayout.TextField(s_chat ?? "", 200);
                bool enter = Event.current.type == EventType.KeyDown
                    && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                    && GUI.GetNameOfFocusedControl() == "coop_phone_lchat";
                if (GUILayout.Button("Send", CoopTheme.ButtonSecondary, GUILayout.Width(60f)) || enter)
                {
                    if (!string.IsNullOrWhiteSpace(s_chat))
                        RivalsLobby.SendChat(s_chat);
                    s_chat = "";
                    if (enter)
                        Event.current.Use();
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
        }

        // --- fv-686 standings-v2 begin
        private enum View { Today, Window, Season }
        private static View s_view = View.Today;
        private static bool s_scoring;

        /// <summary>The end-of-day comparison: every shop ranked per KPI and overall - today's
        /// reports, the last N days summed, or the whole season; the lobby host can weight and
        /// toggle the KPIs here. <paramref name="boxed"/> wraps it in a section box.</summary>
        public static void DrawDayTable(float width, bool boxed)
        {
            if (boxed)
                GUILayout.BeginVertical(CoopTheme.SectionBox);
            bool host = RivalsLobby.Role == RivalsLobby.LobbyRole.Server;
            int kept = LeagueDay.DaysKept();
            if (s_view != View.Today && kept == 0)
                s_view = View.Today;

            GUILayout.BeginHorizontal();
            GUILayout.Label("END OF DAY - LEAGUE STANDINGS", CoopTheme.SectionHeader);
            GUILayout.FlexibleSpace();
            ViewButton(View.Today, "Today");
            ViewButton(View.Window, $"Last {LeagueDay.HistoryDays} days");
            ViewButton(View.Season, kept > 1 ? $"Season ({kept} days)" : "Season");
            if (host && GUILayout.Button(s_scoring ? "Scoring \u25B4" : "Scoring \u25BE", CoopTheme.ButtonSecondary, GUILayout.Width(84f)))
                s_scoring = !s_scoring;
            GUILayout.EndHorizontal();

            if (host && s_scoring)
                DrawScoring();

            List<LeagueDay.Ranked> ranked;
            switch (s_view)
            {
                case View.Window:
                    ranked = LeagueDay.ComputeWindow(LeagueDay.HistoryDays);
                    break;
                case View.Season:
                    ranked = LeagueDay.ComputeWindow(0);
                    break;
                default:
                    ranked = LeagueDay.Compute(LeagueDay.Reports);
                    break;
            }
            if (ranked.Count == 0)
            {
                GUILayout.Label("No day reports yet - they arrive as each shop closes its day.", CoopTheme.LabelDim);
                if (boxed)
                    GUILayout.EndVertical();
                return;
            }
            string me = RivalsLobby.MyShopNameForLeague();
            int n = ranked.Count;
            bool window = s_view != View.Today;
            // header
            float nameW = Mathf.Clamp(width * 0.22f, 90f, 180f);
            float colW = Mathf.Max(54f, (width - nameW - 40f) / LeagueDay.Kpis.Length);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<size=10>#  shop / day</size>", CoopTheme.LabelDim, GUILayout.Width(nameW + 40f));
            foreach (var (key, label, _, _) in LeagueDay.Kpis)
            {
                bool on = LeagueDay.IsEnabled(key);
                float w = LeagueDay.WeightOf(key);
                string tag = !on ? " (off)" : Mathf.Approximately(w, 1f) ? "" : $" x{w:0.##}";
                GUILayout.Label($"<size=10>{(on ? "" : "<color=#777777>")}{label}{tag}{(on ? "" : "</color>")}</size>", CoopTheme.LabelDim, GUILayout.Width(colW));
            }
            GUILayout.EndHorizontal();
            for (int i = 0; i < ranked.Count; i++)
            {
                var row = ranked[i];
                var r = row.Report;
                bool mine = r.Name == me;
                string sub = window ? $"d{r.Day} \u00B7 {row.Days} day{(row.Days == 1 ? "" : "s")} \u00B7 {row.Points:0.#} pts" : $"d{r.Day} \u00B7 {row.Points:0.#} pts";
                GUILayout.BeginHorizontal(i % 2 == 0 ? CoopTheme.RowEven : CoopTheme.RowOdd);
                GUILayout.Label($"{(mine ? "<b>" : "")}{Medal(row.Overall)} {r.Name}{(mine ? "</b>" : "")} <size=10>{sub}</size>", CoopTheme.Label, GUILayout.Width(nameW + 40f));
                foreach (var (key, _, get, money) in LeagueDay.Kpis)
                {
                    double v = get(r);
                    string text = money ? GameInstance.GetPriceString(v) : key == "satisfaction" ? $"{v:0}%" : $"{v:0}";
                    int rank = row.Rank.TryGetValue(key, out int rk) ? rk : n;
                    string col = !LeagueDay.IsEnabled(key) ? "#777777" : rank == 0 ? "#7CFC00" : rank == n - 1 && n > 1 ? "#ff8a80" : "#ffffff";
                    GUILayout.Label($"<size=11><color={col}>{text}</color></size>", CoopTheme.Label, GUILayout.Width(colW));
                }
                GUILayout.EndHorizontal();
            }
            string note = window
                ? "green = best of the window on that measure, red = last; points = each day's placings summed (weighted, (off) columns do not score); money and level are the latest; costs include rent, bills, wages, stock and upgrades"
                : "green = best of the league on that measure, red = last; points = sum of placings, weighted by the host's scoring ((off) columns do not score); costs include rent, bills, wages, stock and upgrades";
            GUILayout.Label("<size=10>" + note + "</size>", CoopTheme.LabelDim);
            // --- fv-826 weeks-under-season begin
            if (s_view == View.Season)
                DrawSeasonWeeks();
            // --- fv-826 weeks-under-season end
            if (boxed)
                GUILayout.EndVertical();
        }

        // --- fv-826 weeks-under-season begin
        /// <summary>Season tab only: every weekly winner the host has declared so far
        /// (RivalsLobby.Board.Weeks), newest first, under the season totals.</summary>
        private static void DrawSeasonWeeks()
        {
            var board = RivalsLobby.Board;
            if (board == null || board.Weeks == null || board.Weeks.Count == 0)
                return;
            int wk = Mathf.Max(1, board.WeekDays);
            GUILayout.Space(4f);
            GUILayout.Label("WEEKS", CoopTheme.SectionHeader);
            for (int i = board.Weeks.Count - 1; i >= 0; i--)
            {
                var w = board.Weeks[i];
                GUILayout.BeginHorizontal((board.Weeks.Count - 1 - i) % 2 == 0 ? CoopTheme.RowEven : CoopTheme.RowOdd);
                GUILayout.Label($"week {w.Week} <size=10>(days {(w.Week - 1) * wk + 1}-{w.LastDay})</size> - <b>{w.WinnerName}</b> <color=#7CFC00>{(w.WinnerDelta >= 0 ? "+" : "")}{GameInstance.GetPriceString(w.WinnerDelta)}</color> <size=10>close {GameInstance.GetPriceString(w.WinnerClose)}</size>", CoopTheme.Label);
                GUILayout.EndHorizontal();
            }
        }
        // --- fv-826 weeks-under-season end

        private static void ViewButton(View v, string text)
        {
            if (GUILayout.Button(text, s_view == v ? CoopTheme.TabSelected : CoopTheme.Tab))
                s_view = v;
        }

        /// <summary>Host only: weight and toggle each KPI, and the history window. Every change
        /// is written to config and the board is resent so all members re-rank at once.</summary>
        private static void DrawScoring()
        {
            var settings = LeagueDay.HostSettings();
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("<size=10>SCORING - a KPI's weight multiplies its placings; off = shown but not scored. Everyone in the league ranks by these.</size>", CoopTheme.LabelDim);
            foreach (var (key, label, _, _) in LeagueDay.Kpis)
            {
                RivalsKpiSetting st = null;
                foreach (var x in settings)
                    if (x.Key == key)
                        st = x;
                if (st == null)
                    continue;
                GUILayout.BeginHorizontal();
                bool on = GUILayout.Toggle(st.Enabled, " " + label, CoopTheme.Toggle, GUILayout.Width(130f));
                if (on != st.Enabled)
                    LeagueDay.HostSet(key, null, on);
                GUILayout.Label($"<size=11>weight {st.Weight:0.##}</size>", st.Enabled ? CoopTheme.Label : CoopTheme.LabelDim, GUILayout.Width(80f));
                if (GUILayout.Button("-", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
                    LeagueDay.HostSet(key, Mathf.Max(0f, st.Weight - 0.5f), null);
                if (GUILayout.Button("+", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
                    LeagueDay.HostSet(key, st.Weight + 0.5f, null);
                if (!Mathf.Approximately(st.Weight, 1f) && GUILayout.Button("1", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
                    LeagueDay.HostSet(key, 1f, null);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            int days = LeagueDay.HostHistoryDays();
            GUILayout.Label($"<size=11>Last N days window: {days}</size>", CoopTheme.Label, GUILayout.Width(180f));
            if (GUILayout.Button("-", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
                LeagueDay.HostSetHistoryDays(days - 1);
            if (GUILayout.Button("+", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
                LeagueDay.HostSetHistoryDays(days + 1);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }
        // --- fv-686 standings-v2 end

        private static string Medal(int overall)
        {
            switch (overall)
            {
                case 0:
                    return "1st";
                case 1:
                    return "2nd";
                case 2:
                    return "3rd";
                default:
                    return (overall + 1) + "th";
            }
        }
    }
}
