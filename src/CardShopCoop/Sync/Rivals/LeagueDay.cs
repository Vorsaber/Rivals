using System;
using System.Collections.Generic;
using System.Globalization;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// The league's end-of-day: when a shop opens its own day report, it publishes the day's
    /// numbers to the lobby; the lobby keeps the latest report per shop AND every shop-day of
    /// the season (LeagueHistory, persisted per league id) and sends the set to everyone (team
    /// hosts pass it to their guests). Each PC ranks the shops on the same KPIs with the HOST's
    /// weights and toggles, which ride the board so no two PCs disagree, and shows the table
    /// next to the vanilla report and on the LEAGUE phone app - today, the last N days, or the
    /// whole season. Shops end their days at different moments, so a row carries its own day.
    /// </summary>
    public static class LeagueDay
    {
        /// <summary>Latest report per shop (what the lobby ranks as "today").</summary>
        public static readonly List<RivalsDayReport> Reports = new List<RivalsDayReport>();
        /// <summary>Every shop-day the lobby has kept this season.</summary>
        public static readonly List<RivalsDayReport> History = new List<RivalsDayReport>();
        /// <summary>The host's weights and toggles as last received (empty = defaults).</summary>
        public static readonly List<RivalsKpiSetting> Settings = new List<RivalsKpiSetting>();
        /// <summary>The host's "last N days" window as last received.</summary>
        public static int HistoryDays = 7;
        public static float ReceivedAt = -100f;

        /// <summary>KPIs ranked, all "higher is better". Points = sum over ENABLED KPIs of
        /// weight x (shops - rank).</summary>
        public static readonly (string key, string label, Func<RivalsDayReport, double> get, bool money)[] Kpis =
        {
            ("profit", "Profit", r => r.Profit, true),
            ("revenue", "Revenue", r => r.Revenue, true),
            ("customers", "Customers", r => r.Customers, false),
            ("checkouts", "Checkouts", r => r.Checkouts, false),
            ("cards", "Cards sold", r => r.CardsSold, false),
            ("items", "Items sold", r => r.ItemsSold, false),
            ("satisfaction", "Satisfaction", r => r.Customers > 0 ? 100.0 * (1.0 - r.Dissatisfied / r.Customers) : 0, false),
            ("money", "Money", r => r.Money, true),
            ("level", "Level", r => r.Level, false),
        };

        /// <summary>KPIs that are a running state rather than a day's tally: a window keeps the
        /// LATEST value instead of summing them.</summary>
        private static readonly HashSet<string> Snapshot = new HashSet<string> { "money", "level" };

        public sealed class Ranked
        {
            public RivalsDayReport Report;
            public double Points;
            public int Overall;                       // 0 = first
            public int Days = 1;                      // shop-days aggregated into Report (window views)
            public readonly Dictionary<string, int> Rank = new Dictionary<string, int>(); // per KPI, 0 = best
        }

        // ================================================================ settings

        public static float WeightOf(string key)
        {
            foreach (var s in Settings)
                if (s != null && s.Key == key)
                    return Mathf.Max(0f, s.Weight);
            return 1f;
        }

        public static bool IsEnabled(string key)
        {
            foreach (var s in Settings)
                if (s != null && s.Key == key)
                    return s.Enabled;
            return true;
        }

        /// <summary>The lobby host's settings, read from config (KpiWeights / KpiDisabled).
        /// One entry per KPI, in table order, so the board always ships the full set.</summary>
        public static List<RivalsKpiSetting> HostSettings()
        {
            var list = new List<RivalsKpiSetting>();
            var weights = ParseWeights(CoopPlugin.RivalsKpiWeights != null ? CoopPlugin.RivalsKpiWeights.Value : "");
            var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in (CoopPlugin.RivalsKpiDisabled != null ? CoopPlugin.RivalsKpiDisabled.Value ?? "" : "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                off.Add(k.Trim());
            foreach (var (key, _, _, _) in Kpis)
                list.Add(new RivalsKpiSetting { Key = key, Weight = weights.TryGetValue(key, out float w) ? w : 1f, Enabled = !off.Contains(key) });
            return list;
        }

        public static int HostHistoryDays()
        {
            return CoopPlugin.RivalsHistoryDays != null ? Mathf.Clamp(CoopPlugin.RivalsHistoryDays.Value, 1, 60) : 7;
        }

        /// <summary>Host: write one KPI's weight or toggle back to config and tell the lobby to
        /// resend the board so every member re-ranks at once.</summary>
        public static void HostSet(string key, float? weight, bool? enabled)
        {
            var settings = HostSettings();
            foreach (var s in settings)
                if (s.Key == key)
                {
                    if (weight.HasValue)
                        s.Weight = Mathf.Clamp(weight.Value, 0f, 10f);
                    if (enabled.HasValue)
                        s.Enabled = enabled.Value;
                }
            var w = new List<string>();
            var off = new List<string>();
            foreach (var s in settings)
            {
                if (Math.Abs(s.Weight - 1f) > 1e-4f)
                    w.Add(s.Key + "=" + s.Weight.ToString("0.##", CultureInfo.InvariantCulture));
                if (!s.Enabled)
                    off.Add(s.Key);
            }
            if (CoopPlugin.RivalsKpiWeights != null)
                CoopPlugin.RivalsKpiWeights.Value = string.Join(",", w);
            if (CoopPlugin.RivalsKpiDisabled != null)
                CoopPlugin.RivalsKpiDisabled.Value = string.Join(",", off);
            RivalsLobby.RebroadcastDayBoard();
        }

        public static void HostSetHistoryDays(int days)
        {
            if (CoopPlugin.RivalsHistoryDays != null)
                CoopPlugin.RivalsHistoryDays.Value = Mathf.Clamp(days, 1, 60);
            RivalsLobby.RebroadcastDayBoard();
        }

        private static Dictionary<string, float> ParseWeights(string text)
        {
            var d = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                string k = part.Substring(0, eq).Trim();
                if (float.TryParse(part.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
                    d[k] = Mathf.Clamp(w, 0f, 10f);
            }
            return d;
        }

        // ================================================================ ranking

        /// <summary>Rank a set of reports (ties share a rank). Every KPI is ranked (the table
        /// colours by it); only enabled KPIs score, each scaled by its weight. Deterministic on
        /// every PC because the settings come from the board.</summary>
        public static List<Ranked> Compute(List<RivalsDayReport> reports)
        {
            var list = new List<Ranked>();
            if (reports == null)
                return list;
            foreach (var r in reports)
                if (r != null)
                    list.Add(new Ranked { Report = r });
            int n = list.Count;
            foreach (var (key, _, get, _) in Kpis)
            {
                var order = new List<Ranked>(list);
                order.Sort((a, b) => get(b.Report).CompareTo(get(a.Report)));
                double weight = IsEnabled(key) ? WeightOf(key) : 0.0;
                for (int i = 0; i < order.Count; i++)
                {
                    int rank = i;
                    if (i > 0 && Math.Abs(get(order[i].Report) - get(order[i - 1].Report)) < 1e-6)
                        rank = order[i - 1].Rank[key];
                    order[i].Rank[key] = rank;
                    order[i].Points += weight * (n - rank);
                }
            }
            Finish(list);
            return list;
        }

        /// <summary>Standings over a window of days: 0 = the whole season, else the last
        /// <paramref name="days"/> day numbers seen. Points = the sum of each day's points (a
        /// day is ranked among the shops that reported it); the KPI cells are the window's
        /// totals (satisfaction over all customers; money and level = latest).</summary>
        public static List<Ranked> ComputeWindow(int days)
        {
            var list = new List<Ranked>();
            if (History.Count == 0)
                return list;
            int maxDay = int.MinValue;
            foreach (var r in History)
                if (r != null && r.Day > maxDay)
                    maxDay = r.Day;
            int minDay = days > 0 ? maxDay - days + 1 : int.MinValue;

            // latest report per (shop, day) - a re-opened report on the same day republishes
            var byDay = new SortedDictionary<int, Dictionary<string, RivalsDayReport>>();
            foreach (var r in History)
            {
                if (r == null || r.Day < minDay)
                    continue;
                if (!byDay.TryGetValue(r.Day, out var shops))
                    byDay[r.Day] = shops = new Dictionary<string, RivalsDayReport>();
                shops[r.Name ?? ""] = r;
            }

            var agg = new Dictionary<string, Ranked>();
            foreach (var kv in byDay)
            {
                var dayRanked = Compute(new List<RivalsDayReport>(kv.Value.Values));
                foreach (var row in dayRanked)
                {
                    var r = row.Report;
                    string name = r.Name ?? "";
                    if (!agg.TryGetValue(name, out var a))
                    {
                        agg[name] = a = new Ranked { Report = new RivalsDayReport { Name = name, ShopId = r.ShopId }, Days = 0 };
                        list.Add(a);
                    }
                    var t = a.Report;
                    t.Day = r.Day; // days ascend, so this ends as the latest
                    t.Customers += r.Customers;
                    t.Checkouts += r.Checkouts;
                    t.Dissatisfied += r.Dissatisfied;
                    t.ItemsSold += r.ItemsSold;
                    t.CardsSold += r.CardsSold;
                    t.Played += r.Played;
                    t.Revenue += r.Revenue;
                    t.Costs += r.Costs;
                    t.Profit += r.Profit;
                    t.Exp += r.Exp;
                    t.Money = r.Money;
                    t.Level = r.Level;
                    t.Markup = r.Markup;
                    a.Days++;
                    a.Points += row.Points;
                }
            }

            // per-KPI ranks of the totals colour the cells; the points stay the daily sum
            int n = list.Count;
            foreach (var (key, _, get, _) in Kpis)
            {
                var order = new List<Ranked>(list);
                order.Sort((a, b) => get(b.Report).CompareTo(get(a.Report)));
                for (int i = 0; i < order.Count; i++)
                {
                    int rank = i;
                    if (i > 0 && Math.Abs(get(order[i].Report) - get(order[i - 1].Report)) < 1e-6)
                        rank = order[i - 1].Rank[key];
                    order[i].Rank[key] = rank;
                }
            }
            Finish(list);
            return list;
        }

        /// <summary>How many distinct day numbers the history holds (the season's length so far).</summary>
        public static int DaysKept()
        {
            var days = new HashSet<int>();
            foreach (var r in History)
                if (r != null)
                    days.Add(r.Day);
            return days.Count;
        }

        private static void Finish(List<Ranked> list)
        {
            list.Sort((a, b) => Math.Abs(b.Points - a.Points) > 1e-6 ? b.Points.CompareTo(a.Points) : b.Report.Profit.CompareTo(a.Report.Profit));
            for (int i = 0; i < list.Count; i++)
                list[i].Overall = i > 0 && Math.Abs(list[i].Points - list[i - 1].Points) < 1e-6 ? list[i - 1].Overall : i;
        }

        // ================================================================ publish

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(EndOfDayReportScreen), "OpenScreen");
                if (m != null)
                    h.Patch(m, postfix: new HarmonyMethod(typeof(LeagueDay), nameof(ReportOpenPostfix)));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("LeagueDay patch: " + e.Message); }
        }

        /// <summary>The vanilla report just opened on a shop owner's PC: publish today's numbers.</summary>
        public static void ReportOpenPostfix()
        {
            try
            {
                if (CoopCore.Role == CoopRole.Client)
                    return; // a guest's report is the shop's; the host publishes
                if (!EndOfDayReportScreen.IsActive())
                    return; // OpenScreen toggles: this call closed it
                var r = BuildMine();
                if (r != null)
                    RivalsLobby.PublishDayReport(r);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("LeagueDay report: " + e.Message); }
        }

        public static RivalsDayReport BuildMine()
        {
            try
            {
                var g = CPlayerData.m_GameReportDataCollect;
                var r = new RivalsDayReport
                {
                    Name = RivalsLobby.MyShopNameForLeague(),
                    Day = CPlayerData.m_CurrentDay + 1,
                    Customers = g.customerVisited,
                    Checkouts = g.checkoutCount,
                    Dissatisfied = g.customerDisatisfied,
                    ItemsSold = g.itemAmountSold,
                    CardsSold = g.cardAmountSold,
                    Played = g.customerPlayed,
                    Revenue = g.totalItemEarning + g.totalCardEarning + g.totalPlayTableEarning,
                    Costs = g.supplyCost + g.upgradeCost + g.employeeCost + g.rentCost + g.billCost,
                    Exp = g.storeExpGained,
                    Money = CPlayerData.m_CoinAmountDouble,
                    Level = CPlayerData.m_ShopLevel,
                };
                r.Profit = r.Revenue + r.Costs; // costs are negative in the game's collector
                r.Markup = PriceIndex.AverageMarkup(out _);
                return r;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("LeagueDay build: " + e.Message);
                return null;
            }
        }

        public static void Apply(RivalsDayBoardMessage board)
        {
            if (board == null)
                return;
            Reports.Clear();
            if (board.Reports != null)
                Reports.AddRange(board.Reports);
            History.Clear();
            if (board.History != null)
                History.AddRange(board.History);
            if (History.Count == 0)
                History.AddRange(Reports); // a lobby without history: today is all there is
            if (Reports.Count == 0 && History.Count > 0)
            {
                // a lobby restarted mid-season: "today" is each shop's latest day on file
                var latest = new Dictionary<string, RivalsDayReport>();
                foreach (var r in History)
                    if (r != null && (!latest.TryGetValue(r.Name ?? "", out var have) || r.Day >= have.Day))
                        latest[r.Name ?? ""] = r;
                Reports.AddRange(latest.Values);
            }
            Settings.Clear();
            if (board.Kpis != null)
                Settings.AddRange(board.Kpis);
            if (board.HistoryDays > 0)
                HistoryDays = board.HistoryDays;
            ReceivedAt = Time.unscaledTime;
        }

        public static void Clear()
        {
            Reports.Clear();
            History.Clear();
            Settings.Clear();
        }
    }
}
