using System.Collections.Generic;
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

        /// <summary>The end-of-day comparison: every shop's latest day, ranked per KPI and
        /// overall. <paramref name="boxed"/> wraps it in a section box.</summary>
        public static void DrawDayTable(float width, bool boxed)
        {
            var ranked = LeagueDay.Compute(LeagueDay.Reports);
            if (boxed)
                GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("END OF DAY - LEAGUE STANDINGS", CoopTheme.SectionHeader);
            if (ranked.Count == 0)
            {
                GUILayout.Label("No day reports yet - they arrive as each shop closes its day.", CoopTheme.LabelDim);
                if (boxed)
                    GUILayout.EndVertical();
                return;
            }
            string me = RivalsLobby.MyShopNameForLeague();
            int n = ranked.Count;
            // header
            float nameW = Mathf.Clamp(width * 0.22f, 90f, 180f);
            float colW = Mathf.Max(54f, (width - nameW - 40f) / LeagueDay.Kpis.Length);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<size=10>#  shop / day</size>", CoopTheme.LabelDim, GUILayout.Width(nameW + 40f));
            foreach (var (_, label, _, _) in LeagueDay.Kpis)
                GUILayout.Label("<size=10>" + label + "</size>", CoopTheme.LabelDim, GUILayout.Width(colW));
            GUILayout.EndHorizontal();
            for (int i = 0; i < ranked.Count; i++)
            {
                var row = ranked[i];
                var r = row.Report;
                bool mine = r.Name == me;
                GUILayout.BeginHorizontal(i % 2 == 0 ? CoopTheme.RowEven : CoopTheme.RowOdd);
                GUILayout.Label($"{(mine ? "<b>" : "")}{Medal(row.Overall)} {r.Name}{(mine ? "</b>" : "")} <size=10>d{r.Day} · {row.Points} pts</size>", CoopTheme.Label, GUILayout.Width(nameW + 40f));
                foreach (var (key, _, get, money) in LeagueDay.Kpis)
                {
                    double v = get(r);
                    string text = money ? GameInstance.GetPriceString(v) : key == "satisfaction" ? $"{v:0}%" : $"{v:0}";
                    int rank = row.Rank.TryGetValue(key, out int rk) ? rk : n;
                    string col = rank == 0 ? "#7CFC00" : rank == n - 1 && n > 1 ? "#ff8a80" : "#ffffff";
                    GUILayout.Label($"<size=11><color={col}>{text}</color></size>", CoopTheme.Label, GUILayout.Width(colW));
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("<size=10>green = best of the league on that measure, red = last; points = sum of placings; costs include rent, bills, wages, stock and upgrades</size>", CoopTheme.LabelDim);
            if (boxed)
                GUILayout.EndVertical();
        }

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
