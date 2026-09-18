using System.Collections.Generic;
using System.Globalization;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// fv-827: the league's scoring rules as one line for the lobby's LEAGUE box, so a member
    /// reads them BEFORE readying up. The host reads its own config; a member reads what the
    /// last "setup" (or day board) carried. Only what differs from the defaults is spelled out:
    /// "Scoring: profit x2, level off - last 7 days window - season endless - week 7 d".
    /// </summary>
    public static class ScoringLine
    {
        public static string Build()
        {
            List<RivalsKpiSetting> settings = RivalsLobby.Role == RivalsLobby.LobbyRole.Server ? LeagueDay.HostSettings() : LeagueDay.Settings;
            int window = RivalsLobby.Role == RivalsLobby.LobbyRole.Server ? LeagueDay.HostHistoryDays() : LeagueDay.HistoryDays;
            var parts = new List<string>();
            foreach (var (key, label, _, _) in LeagueDay.Kpis)
            {
                RivalsKpiSetting st = null;
                foreach (var s in settings)
                    if (s != null && s.Key == key)
                        st = s;
                if (st == null)
                    continue;
                string name = label.ToLowerInvariant();
                if (!st.Enabled)
                    parts.Add(name + " off");
                else if (!Mathf.Approximately(st.Weight, 1f))
                    parts.Add(name + " x" + st.Weight.ToString("0.##", CultureInfo.InvariantCulture));
            }
            string kpis = settings.Count == 0 ? "not received yet" : (parts.Count == 0 ? "every KPI x1" : string.Join(", ", parts));
            int season = RivalsLobby.SeasonDays, week = RivalsLobby.WeekDays;
            return "Scoring: " + kpis
                + " - last " + Mathf.Max(1, window) + " days window"
                + " - season " + (season <= 0 ? "endless" : season + " d")
                + " - week " + (week <= 0 ? "off" : week + " d");
        }
    }
}
