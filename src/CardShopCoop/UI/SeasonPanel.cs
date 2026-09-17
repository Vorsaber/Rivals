using CardShopCoop.Net.Messages;
using CardShopCoop.Sync.Rivals;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>The season on the lobby (fv-683): the host's day count, how far each shop
    /// is, and the winner once every shop has closed the last day. Drawn by the F2 RIVALS
    /// tab (LEAGUE box + board) and the LEAGUE phone app's board.</summary>
    internal static class SeasonPanel
    {
        /// <summary>The season line: "SEASON OVER - X wins ..." or "season: N days" / endless.</summary>
        public static void DrawStatus(RivalsBoardMessage board)
        {
            int days = board != null && board.SeasonDays > 0 ? board.SeasonDays : RivalsLobby.SeasonDays;
            DrawLatestWeek(board);
            if (board != null && board.SeasonOver && !string.IsNullOrEmpty(board.WinnerName))
            {
                GUILayout.Label($"<b><color=#FFD700>SEASON OVER - {board.WinnerName} wins</color></b>  with a shop value of {GameInstance.GetPriceString(board.WinnerValue)} after {days} days", CoopTheme.Label);
                return;
            }
            if (days <= 0)
            {
                int wk = board != null && board.WeekDays > 0 ? board.WeekDays : RivalsLobby.WeekDays;
                GUILayout.Label(wk > 0
                    ? $"<size=10>league: endless - a winner every {wk} days (most shop value gained); the host can also set a season length to crown an overall winner</size>"
                    : "<size=10>league: endless - the host sets a week or a season length in the LEAGUE box to crown winners</size>", CoopTheme.LabelDim);
                return;
            }
            int finished = 0, total = board != null ? board.Shops.Count : 0;
            if (board != null)
                foreach (var s in board.Shops)
                    if (s.Finished)
                        finished++;
            string wait = total > 0 && finished > 0 ? $" - {finished} of {total} finished, waiting for the rest" : "";
            GUILayout.Label($"<size=10>season: {days} days - ranked by SHOP VALUE (cash + stock at market price){wait}</size>", CoopTheme.LabelDim);
        }

        /// <summary>The host's season length control (LEAGUE box). Members see the value.</summary>
        public static void DrawSetting(bool server)
        {
            int days = RivalsLobby.SeasonDays;
            GUILayout.BeginHorizontal();
            GUILayout.Label("season (days)", CoopTheme.Label, GUILayout.Width(90f));
            if (server && GUILayout.Button("-", CoopTheme.ButtonSecondary, GUILayout.Width(26f)))
                RivalsLobby.HostSetSeasonDays(days - 1);
            GUILayout.Label(days <= 0 ? "endless" : days.ToString(), CoopTheme.Label, GUILayout.Width(52f));
            if (server && GUILayout.Button("+", CoopTheme.ButtonSecondary, GUILayout.Width(26f)))
                RivalsLobby.HostSetSeasonDays(days + 1);
            if (server)
            {
                if (GUILayout.Button("+5", CoopTheme.ButtonSecondary, GUILayout.Width(32f)))
                    RivalsLobby.HostSetSeasonDays(days + 5);
                if (GUILayout.Button("-5", CoopTheme.ButtonSecondary, GUILayout.Width(32f)))
                    RivalsLobby.HostSetSeasonDays(days - 5);
            }
            GUILayout.Label("<size=10>a shop's score is its shop value when it closes the last day; the winner is named once every shop is there</size>", CoopTheme.LabelDim);
            GUILayout.EndHorizontal();

            int wk = RivalsLobby.WeekDays;
            GUILayout.BeginHorizontal();
            GUILayout.Label("week (days)", CoopTheme.Label, GUILayout.Width(90f));
            if (server && GUILayout.Button("-", CoopTheme.ButtonSecondary, GUILayout.Width(26f)))
                RivalsLobby.HostSetWeekDays(wk - 1);
            GUILayout.Label(wk <= 0 ? "off" : wk.ToString(), CoopTheme.Label, GUILayout.Width(52f));
            if (server && GUILayout.Button("+", CoopTheme.ButtonSecondary, GUILayout.Width(26f)))
                RivalsLobby.HostSetWeekDays(wk + 1);
            GUILayout.Label("<size=10>every stretch of this many days goes to the shop that gained the most shop value over it; the league runs on</size>", CoopTheme.LabelDim);
            GUILayout.EndHorizontal();
        }

        /// <summary>The most recent week's winner, one line (nothing until a week is declared).</summary>
        public static void DrawLatestWeek(RivalsBoardMessage board)
        {
            if (board == null || board.Weeks == null || board.Weeks.Count == 0)
                return;
            var w = board.Weeks[board.Weeks.Count - 1];
            GUILayout.Label($"<b><color=#FFD700>WEEK {w.Week} goes to {w.WinnerName}</color></b>  <size=10>days {(w.Week - 1) * Mathf.Max(1, board.WeekDays) + 1}-{w.LastDay} - shop value up {GameInstance.GetPriceString(w.WinnerDelta)} to {GameInstance.GetPriceString(w.WinnerClose)}</size>", CoopTheme.Label);
        }

        /// <summary>Every declared week, newest first (at most <paramref name="max"/>), with the
        /// full order of the latest one.</summary>
        public static void DrawWeeks(RivalsBoardMessage board, int max)
        {
            if (board == null || board.Weeks == null || board.Weeks.Count == 0)
                return;
            GUILayout.Label("WEEKLY WINNERS", CoopTheme.SectionHeader);
            for (int i = board.Weeks.Count - 1, shown = 0; i >= 0 && shown < max; i--, shown++)
            {
                var w = board.Weeks[i];
                GUILayout.Label($"week {w.Week} <size=10>(days {(w.Week - 1) * Mathf.Max(1, board.WeekDays) + 1}-{w.LastDay})</size> - <b>{w.WinnerName}</b> +{GameInstance.GetPriceString(w.WinnerDelta)}", CoopTheme.Label);
                if (i == board.Weeks.Count - 1 && w.Names != null && w.Names.Count > 1)
                {
                    var parts = new System.Text.StringBuilder();
                    for (int k = 0; k < w.Names.Count && k < w.Deltas.Count; k++)
                        parts.Append(k > 0 ? "  ·  " : "").Append(k + 1).Append(". ").Append(w.Names[k]).Append(' ').Append(w.Deltas[k] >= 0 ? "+" : "").Append(GameInstance.GetPriceString(w.Deltas[k]));
                    GUILayout.Label("<size=10>   " + parts + "</size>", CoopTheme.LabelDim);
                }
            }
        }

        /// <summary>One shop's value for a board row: "$1,234 value (cash $1,000 + stock $234)"
        /// plus its season standing.</summary>
        public static string ValueText(RivalsShop s, int seasonDays)
        {
            string v = $"{GameInstance.GetPriceString(s.ShopValue)} value <size=10>(cash {GameInstance.GetPriceString(s.Money)} + stock {GameInstance.GetPriceString(s.StockValue)})</size>";
            if (seasonDays > 0)
                v += s.Finished
                    ? $"  <color=#FFD700>FINISHED {GameInstance.GetPriceString(s.FinalValue)}</color>"
                    : $"  <size=10>day {Mathf.Min(s.Day + 1, seasonDays)}/{seasonDays}</size>";
            int wk = RivalsLobby.Board != null ? RivalsLobby.Board.WeekDays : 0;
            if (wk > 0 && !s.Finished)
                v += $"  <size=10>wk {s.WeeksClosed + 1} day {Mathf.Clamp(s.Day - s.WeeksClosed * wk + 1, 1, wk)}/{wk}</size>";
            return v;
        }
    }
}
