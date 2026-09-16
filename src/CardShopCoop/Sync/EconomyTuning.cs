using System;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    public enum EconomyProfile
    {
        Vanilla = 0,
        Tight = 1,   // half the wholesale-to-market spread, cards worth 3/4, customers 1.5x as picky
        Harsh = 2,   // a third of the spread, cards worth 60%, customers twice as picky, stock costs 25% more
        Custom = 3,  // the Economy.Custom* values
    }

    /// <summary>
    /// Rebases the game's economy so profit is earned rather than handed over. Four
    /// multipliers, applied on the HOST and in single player, mirrored to guests through
    /// SettingsSync so every screen prices the same:
    ///
    ///  - MarginScale: the game generates each item's MARKET price as wholesale cost x 1.5-3.0
    ///    (ItemData.marketPriceMin/MaxPercent, default 1.5-3), i.e. customers already accept a
    ///    50-200% markup as "fair". This compresses that spread toward cost:
    ///    market' = cost' + (market - cost) * MarginScale.
    ///  - CardValueScale: card market prices (what a single card sells for) x this.
    ///  - Pickiness: CustomerManager.GetCustomerBuyItemChance keys on % over market; the
    ///    overage is multiplied, so at 2.0 a 10% markup is judged like a 20% one.
    ///  - CostScale: what the shop pays to restock, x this.
    ///
    /// All four are getter-level (RestockManager.GetItemMarketPrice / GetItemCost,
    /// CPlayerData.GetCardMarketPrice, the buy-chance curve) so an existing save is rebased
    /// the moment the profile changes and goes back to vanilla the moment it is turned off;
    /// nothing in the save is rewritten. The getters are bypassed while RestockManager.Init
    /// generates a fresh price table (it derives box prices from the getters, and scaling
    /// those at generation time would double-apply).
    /// </summary>
    public static class EconomyTuning
    {
        private static bool s_bypass;

        // the host's factors as mirrored to a guest (SettingsSync); a guest never uses its own config
        public static bool RemoteKnown;
        public static float RemoteMargin = 1f, RemoteCard = 1f, RemotePick = 1f, RemoteCost = 1f;

        public static EconomyProfile Profile
        {
            get
            {
                return CoopPlugin.EconomyProfile != null ? CoopPlugin.EconomyProfile.Value : EconomyProfile.Vanilla;
            }
        }

        /// <summary>The factors in force on THIS pc (host / solo: config; guest: the host's).</summary>
        public static void Factors(out float margin, out float card, out float pick, out float cost)
        {
            if (CoopCore.Role == CoopRole.Client)
            {
                if (RemoteKnown)
                {
                    margin = RemoteMargin;
                    card = RemoteCard;
                    pick = RemotePick;
                    cost = RemoteCost;
                }
                else
                {
                    margin = card = pick = cost = 1f;
                }
                return;
            }
            LocalFactors(out margin, out card, out pick, out cost);
        }

        public static void LocalFactors(out float margin, out float card, out float pick, out float cost)
        {
            switch (Profile)
            {
                case EconomyProfile.Tight:
                    margin = 0.5f;
                    card = 0.75f;
                    pick = 1.5f;
                    cost = 1.1f;
                    break;
                case EconomyProfile.Harsh:
                    margin = 0.33f;
                    card = 0.6f;
                    pick = 2f;
                    cost = 1.25f;
                    break;
                case EconomyProfile.Custom:
                    margin = CoopPlugin.EconomyCustomMargin != null ? CoopPlugin.EconomyCustomMargin.Value : 1f;
                    card = CoopPlugin.EconomyCustomCardValue != null ? CoopPlugin.EconomyCustomCardValue.Value : 1f;
                    pick = CoopPlugin.EconomyCustomPickiness != null ? CoopPlugin.EconomyCustomPickiness.Value : 1f;
                    cost = CoopPlugin.EconomyCustomCost != null ? CoopPlugin.EconomyCustomCost.Value : 1f;
                    break;
                default:
                    margin = card = pick = cost = 1f;
                    break;
            }
            margin = Mathf.Clamp(margin, 0.05f, 3f);
            card = Mathf.Clamp(card, 0.05f, 3f);
            pick = Mathf.Clamp(pick, 0.2f, 10f);
            cost = Mathf.Clamp(cost, 0.2f, 5f);
        }

        public static string Describe()
        {
            Factors(out float m, out float c, out float p, out float k);
            var p0 = CoopCore.Role == CoopRole.Client ? (RemoteKnown ? "host's" : "unknown") : Profile.ToString();
            if (Mathf.Approximately(m, 1f) && Mathf.Approximately(c, 1f) && Mathf.Approximately(p, 1f) && Mathf.Approximately(k, 1f))
                return p0 + " (vanilla)";
            return $"{p0}: margin x{m:0.00}, card value x{c:0.00}, pickiness x{p:0.00}, stock cost x{k:0.00}";
        }

        public static void SetProfile(EconomyProfile p)
        {
            if (CoopPlugin.EconomyProfile != null)
                CoopPlugin.EconomyProfile.Value = p;
        }

        private static bool Off()
        {
            if (s_bypass)
                return true;
            Factors(out float m, out float c, out float p, out float k);
            return Mathf.Approximately(m, 1f) && Mathf.Approximately(c, 1f) && Mathf.Approximately(p, 1f) && Mathf.Approximately(k, 1f);
        }

        private static float Cents(float v)
        {
            return Mathf.RoundToInt(v * 100f) / 100f;
        }

        // ---------------------------------------------------------------- patches

        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(RestockManager), "GetItemMarketPrice", nameof(ItemMarketPostfix));
            Try(h, typeof(RestockManager), "GetItemMarketPriceCustomPercent", nameof(ItemMarketCustomPostfix));
            Try(h, typeof(RestockManager), "GetItemCost", nameof(ItemCostPostfix));
            Try(h, typeof(CPlayerData), "GetCardMarketPrice", nameof(CardMarketPostfix), new[] { typeof(CardData) });
            Try(h, typeof(CPlayerData), "GetCardMarketPrice", nameof(CardMarketPostfix), new[] { typeof(int), typeof(ECardExpansionType), typeof(bool), typeof(int) });
            Try(h, typeof(CPlayerData), "GetCardMarketPriceCustomPercent", nameof(CardMarketPostfix));
            try
            {
                var buy = AccessTools.Method(typeof(CustomerManager), "GetCustomerBuyItemChance");
                if (buy != null)
                    h.Patch(buy, prefix: new HarmonyMethod(typeof(EconomyTuning), nameof(BuyChancePrefix)));
                var init = AccessTools.Method(typeof(RestockManager), "Init");
                if (init != null)
                    h.Patch(init, prefix: new HarmonyMethod(typeof(EconomyTuning), nameof(BypassOn)),
                        postfix: new HarmonyMethod(typeof(EconomyTuning), nameof(BypassOff)));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("EconomyTuning: " + e.Message); }
        }

        private static void Try(Harmony h, Type type, string method, string postfix, Type[] args = null)
        {
            try
            {
                var target = args != null ? AccessTools.Method(type, method, args) : AccessTools.Method(type, method);
                if (target == null)
                {
                    CoopPlugin.Log.LogWarning($"EconomyTuning: {type.Name}.{method} not found - skipped");
                    return;
                }
                h.Patch(target, postfix: new HarmonyMethod(typeof(EconomyTuning), postfix));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"EconomyTuning: patch {type.Name}.{method} failed: {e.Message}"); }
        }

        public static void BypassOn()
        {
            s_bypass = true;
        }

        public static void BypassOff()
        {
            s_bypass = false;
        }

        /// <summary>Raw (unscaled) wholesale cost - what the game itself would return.</summary>
        private static float RawCost(EItemType itemType)
        {
            try
            {
                var list = CPlayerData.m_GeneratedCostPriceList;
                var pct = CPlayerData.m_ItemPricePercentChangeList;
                int i = (int)itemType;
                if (list == null || i < 0 || i >= list.Count)
                    return 0f;
                float p = pct != null && i < pct.Count ? pct[i] : 0f;
                return Cents(list[i] + list[i] * (p / 100f));
            }
            catch { return 0f; }
        }

        public static void ItemMarketPostfix(EItemType itemType, ref float __result)
        {
            if (Off())
                return;
            Factors(out float margin, out _, out _, out float costScale);
            float cost = RawCost(itemType);
            if (cost <= 0f || __result <= cost)
                return;
            __result = Cents(cost * costScale + (__result - cost) * margin);
        }

        public static void ItemMarketCustomPostfix(EItemType itemType, ref float __result)
        {
            ItemMarketPostfix(itemType, ref __result);
        }

        public static void ItemCostPostfix(ref float __result)
        {
            if (Off())
                return;
            Factors(out _, out _, out _, out float costScale);
            __result = Cents(__result * costScale);
        }

        public static void CardMarketPostfix(ref float __result)
        {
            if (Off())
                return;
            Factors(out _, out float card, out _, out _);
            __result = Cents(__result * card);
        }

        /// <summary>Scale the overage above market before the vanilla curve reads it.</summary>
        public static void BuyChancePrefix(ref float currentPrice, float marketPrice)
        {
            if (Off() || marketPrice <= 0f || currentPrice <= marketPrice)
                return;
            Factors(out _, out _, out float pick, out _);
            currentPrice = marketPrice + (currentPrice - marketPrice) * pick;
        }
    }
}
