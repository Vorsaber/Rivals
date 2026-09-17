using System;
using System.Collections.Generic;
using System.IO;
using CardShopCoop.Net.Messages;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// Lobby SERVER only: every shop-day report of the season, keyed by (shop name, day) so a
    /// re-opened report replaces itself and a shop that reconnects under a new connection id
    /// keeps one row per day. Persisted beside the league's saves
    /// (persistentDataPath/CardShopCoop.leagues/&lt;id&gt;/standings.json) so a host restart, or a
    /// "resume" of the league days later, still shows the season. A new league id starts empty.
    /// </summary>
    public static class LeagueHistory
    {
        /// <summary>Distinct day numbers kept - bounds the board payload for a long season.</summary>
        public const int MaxDays = 120;

        private static string s_leagueId = "";
        private static readonly List<RivalsDayReport> s_rows = new List<RivalsDayReport>();

        public static IReadOnlyList<RivalsDayReport> Rows => s_rows;

        private static string FileFor(string id) => Application.persistentDataPath + "/CardShopCoop.leagues/" + id + "/standings.json";

        /// <summary>Point the store at a league (loading its file); a change of id swaps seasons.</summary>
        public static void Use(string leagueId)
        {
            leagueId = (leagueId ?? "").Trim();
            if (leagueId == s_leagueId && (s_rows.Count > 0 || leagueId == ""))
                return;
            s_leagueId = leagueId;
            s_rows.Clear();
            if (leagueId == "")
                return;
            try
            {
                string p = FileFor(leagueId);
                if (File.Exists(p))
                {
                    var rows = JsonConvert.DeserializeObject<List<RivalsDayReport>>(File.ReadAllText(p));
                    if (rows != null)
                        foreach (var r in rows)
                            if (r != null)
                                s_rows.Add(r);
                }
                CoopPlugin.Log.LogInfo($"LeagueHistory: league {leagueId} - {s_rows.Count} shop-days on file");
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("LeagueHistory load: " + e.Message); }
        }

        /// <summary>Keep a report: same shop + same day replaces; then trim to MaxDays and save.</summary>
        public static void Record(RivalsDayReport r)
        {
            if (r == null)
                return;
            string name = r.Name ?? "";
            for (int i = s_rows.Count - 1; i >= 0; i--)
                if (s_rows[i] != null && (s_rows[i].Name ?? "") == name && s_rows[i].Day == r.Day)
                    s_rows.RemoveAt(i);
            s_rows.Add(r);
            Trim();
            Save();
        }

        private static void Trim()
        {
            var days = new SortedSet<int>();
            foreach (var r in s_rows)
                days.Add(r.Day);
            while (days.Count > MaxDays)
            {
                int oldest = days.Min;
                days.Remove(oldest);
                s_rows.RemoveAll(x => x.Day == oldest);
            }
        }

        private static void Save()
        {
            if (string.IsNullOrEmpty(s_leagueId))
                return;
            try
            {
                string p = FileFor(s_leagueId);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, JsonConvert.SerializeObject(s_rows));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("LeagueHistory save: " + e.Message); }
        }
    }
}
