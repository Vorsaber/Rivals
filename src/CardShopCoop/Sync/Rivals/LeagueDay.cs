using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// The league's end-of-day: when a shop opens its own day report, it publishes the day's
    /// numbers to the lobby; the lobby keeps the latest report per shop and sends the set to
    /// everyone (and team hosts pass it to their guests). Each PC ranks the shops on the same
    /// KPIs and shows the table next to the vanilla report, and on the LEAGUE phone app.
    /// Shops end their days at different moments, so a row carries its own day number.
    /// </summary>
    public static class LeagueDay
    {
        public static readonly List<RivalsDayReport> Reports = new List<RivalsDayReport>();
        public static float ReceivedAt = -100f;

        /// <summary>KPIs ranked, all "higher is better". Points = sum over KPIs of (shops - rank).</summary>
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

        public sealed class Ranked
        {
            public RivalsDayReport Report;
            public int Points;
            public int Overall;                       // 0 = first
            public readonly Dictionary<string, int> Rank = new Dictionary<string, int>(); // per KPI, 0 = best
        }

        /// <summary>Rank a set of reports (ties share a rank). Deterministic on every PC.</summary>
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
                for (int i = 0; i < order.Count; i++)
                {
                    int rank = i;
                    if (i > 0 && Math.Abs(get(order[i].Report) - get(order[i - 1].Report)) < 1e-6)
                        rank = order[i - 1].Rank[key];
                    order[i].Rank[key] = rank;
                    order[i].Points += n - rank;
                }
            }
            list.Sort((a, b) => b.Points != a.Points ? b.Points.CompareTo(a.Points) : b.Report.Profit.CompareTo(a.Report.Profit));
            for (int i = 0; i < list.Count; i++)
                list[i].Overall = i > 0 && list[i].Points == list[i - 1].Points ? list[i - 1].Overall : i;
            return list;
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
            ReceivedAt = Time.unscaledTime;
        }

        public static void Clear()
        {
            Reports.Clear();
        }
    }
}
