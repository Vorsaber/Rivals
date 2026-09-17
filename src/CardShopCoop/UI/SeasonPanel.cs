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
            if (board != null && board.SeasonOver && !string.IsNullOrEmpty(board.WinnerName))
            {
                GUILayout.Label($"<b><color=#FFD700>SEASON OVER - {board.WinnerName} wins</color></b>  with a shop value of {GameInstance.GetPriceString(board.WinnerValue)} after {days} days", CoopTheme.Label);
                return;
            }
            if (days <= 0)
            {
                GUILayout.Label("<size=10>season: endless - the host sets a length in the LEAGUE box to crown a winner</size>", CoopTheme.LabelDim);
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
            return v;
        }
    }
}
