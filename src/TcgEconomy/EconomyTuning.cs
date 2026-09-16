using System;
using HarmonyLib;
using UnityEngine;

namespace TcgEconomy
{
    public enum EconomyProfile
    {
        Vanilla = 0,
        Tight = 1,   // half the wholesale-to-market spread, cards worth 3/4, customers 1.5x as picky, bills +25%
        Harsh = 2,   // a third of the spread, cards worth 60%, customers twice as picky, stock +25%, bills +50%
        Custom = 3,  // the Economy.Custom* values
    }

    /// <summary>
    /// Rebases the game's economy so profit is earned rather than handed over. Five
    /// multipliers, all applied where prices are READ, so an existing save is rebased the
    /// moment the profile changes and goes back to vanilla the moment it is turned off:
    ///
    ///  - MarginScale: the game generates each item's MARKET price as wholesale cost x 1.5-3.0
    ///    (ItemData.marketPriceMin/MaxPercent, default 1.5-3), i.e. customers already accept a
    ///    50-200% markup as "fair". This compresses that spread toward cost:
    ///    market' = cost' + (market - cost) * MarginScale.
    ///  - CardValueScale: card market prices (what a single card sells for) x this.
    ///  - Pickiness: CustomerManager.GetCustomerBuyItemChance keys on % over market; the
    ///    overage is multiplied, so at 2.0 a 10% markup is judged like a 20% one.
    ///  - CostScale: what the shop pays to restock, x this.
    ///  - BillScale: the daily rent and electricity bills, x this (wages are the player's choice
    ///    and stay as they are).
    ///
    /// The getters are bypassed while RestockManager.Init generates a fresh price table (it
    /// derives box prices from the getters, and scaling those at generation time would
    /// double-apply).
    ///
    /// Co-op: CardShopCoop, when installed, calls <see cref="SetOverride"/> on a guest with
    /// the host's factors so every screen in the session prices the same, and
    /// <see cref="ClearOverride"/> when the session ends. Without an override this PC's own
    /// config applies.
    /// </summary>
    public static class EconomyTuning
    {
        private static bool s_bypass;
        private static bool s_override;

        /// <summary>Set by another mod (CardShopCoop's Rivals league): an extra multiplier on
        /// customer pickiness for THIS shop, 1 = none. Stacks on the profile.</summary>
        public static float ExternalPickiness = 1f;
        private static float s_oMargin = 1f, s_oCard = 1f, s_oPick = 1f, s_oCost = 1f, s_oBill = 1f;

        // ---------------------------------------------------------------- public API (also used by CardShopCoop via reflection)

        public static EconomyProfile Profile
        {
            get
            {
                return Plugin.ProfileEntry != null ? Plugin.ProfileEntry.Value : EconomyProfile.Vanilla;
            }
        }

        public static void SetProfile(int profile)
        {
            if (Plugin.ProfileEntry != null && Enum.IsDefined(typeof(EconomyProfile), profile))
                Plugin.ProfileEntry.Value = (EconomyProfile)profile;
        }

        /// <summary>Another mod (CardShopCoop on a guest) dictates the factors.</summary>
        public static void SetOverride(float margin, float card, float pick, float cost, float bill)
        {
            s_override = true;
            s_oMargin = margin;
            s_oCard = card;
            s_oPick = pick;
            s_oCost = cost;
            s_oBill = bill;
        }

        public static void ClearOverride()
        {
            s_override = false;
        }

        public static bool HasOverride
        {
            get
            {
                return s_override;
            }
        }

        /// <summary>The factors in force on THIS pc (override if set, else this pc's config).</summary>
        public static void Factors(out float margin, out float card, out float pick, out float cost, out float bill)
        {
            if (s_override)
            {
                margin = s_oMargin;
                card = s_oCard;
                pick = s_oPick;
                cost = s_oCost;
                bill = s_oBill;
                return;
            }
            LocalFactors(out margin, out card, out pick, out cost, out bill);
        }

        /// <summary>This pc's configured factors, ignoring any override.</summary>
        public static void LocalFactors(out float margin, out float card, out float pick, out float cost, out float bill)
        {
            switch (Profile)
            {
                case EconomyProfile.Tight:
                    margin = 0.5f;
                    card = 0.75f;
                    pick = 1.5f;
                    cost = 1.1f;
                    bill = 1.25f;
                    break;
                case EconomyProfile.Harsh:
                    margin = 0.33f;
                    card = 0.6f;
                    pick = 2f;
                    cost = 1.25f;
                    bill = 1.5f;
                    break;
                case EconomyProfile.Custom:
                    margin = Plugin.CustomMargin != null ? Plugin.CustomMargin.Value : 1f;
                    card = Plugin.CustomCardValue != null ? Plugin.CustomCardValue.Value : 1f;
                    pick = Plugin.CustomPickiness != null ? Plugin.CustomPickiness.Value : 1f;
                    cost = Plugin.CustomCost != null ? Plugin.CustomCost.Value : 1f;
                    bill = Plugin.CustomBills != null ? Plugin.CustomBills.Value : 1f;
                    break;
                default:
                    margin = card = pick = cost = bill = 1f;
                    break;
            }
            margin = Mathf.Clamp(margin, 0.05f, 3f);
            card = Mathf.Clamp(card, 0.05f, 3f);
            pick = Mathf.Clamp(pick, 0.2f, 10f);
            cost = Mathf.Clamp(cost, 0.2f, 5f);
            bill = Mathf.Clamp(bill, 0.1f, 10f);
        }

        public static string Describe()
        {
            Factors(out float m, out float c, out float p, out float k, out float b);
            string p0 = s_override ? "host's" : Profile.ToString();
            if (IsUnity(m, c, p, k, b))
                return p0 + " (vanilla)";
            return $"{p0}: margin x{m:0.00}, card value x{c:0.00}, pickiness x{p:0.00}, stock cost x{k:0.00}, bills x{b:0.00}";
        }

        private static bool IsUnity(float m, float c, float p, float k, float b)
        {
            return Mathf.Approximately(m, 1f) && Mathf.Approximately(c, 1f) && Mathf.Approximately(p, 1f)
                && Mathf.Approximately(k, 1f) && Mathf.Approximately(b, 1f);
        }

        private static bool Off()
        {
            if (s_bypass)
                return true;
            Factors(out float m, out float c, out float p, out float k, out float b);
            return IsUnity(m, c, p, k, b);
        }

        private static float Cents(float v)
        {
            return Mathf.RoundToInt(v * 100f) / 100f;
        }

        // ---------------------------------------------------------------- patches

        public static void ApplyPatches(Harmony h)
        {
            Postfix(h, typeof(RestockManager), "GetItemMarketPrice", nameof(ItemMarketPostfix));
            Postfix(h, typeof(RestockManager), "GetItemMarketPriceCustomPercent", nameof(ItemMarketCustomPostfix));
            Postfix(h, typeof(RestockManager), "GetItemCost", nameof(ItemCostPostfix));
            Postfix(h, typeof(CPlayerData), "GetCardMarketPrice", nameof(CardMarketPostfix), new[] { typeof(CardData) });
            Postfix(h, typeof(CPlayerData), "GetCardMarketPrice", nameof(CardMarketPostfix), new[] { typeof(int), typeof(ECardExpansionType), typeof(bool), typeof(int) });
            Postfix(h, typeof(CPlayerData), "GetCardMarketPriceCustomPercent", nameof(CardMarketPostfix));
            try
            {
                var buy = AccessTools.Method(typeof(CustomerManager), "GetCustomerBuyItemChance");
                if (buy != null)
                    h.Patch(buy, prefix: new HarmonyMethod(typeof(EconomyTuning), nameof(BuyChancePrefix)));
                var bill = AccessTools.Method(typeof(CPlayerData), "UpdateBill");
                if (bill != null)
                    h.Patch(bill, prefix: new HarmonyMethod(typeof(EconomyTuning), nameof(UpdateBillPrefix)));
                var init = AccessTools.Method(typeof(RestockManager), "Init");
                if (init != null)
                    h.Patch(init, prefix: new HarmonyMethod(typeof(EconomyTuning), nameof(BypassOn)),
                        postfix: new HarmonyMethod(typeof(EconomyTuning), nameof(BypassOff)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("EconomyTuning: " + e.Message); }
        }

        private static void Postfix(Harmony h, Type type, string method, string postfix, Type[] args = null)
        {
            try
            {
                var target = args != null ? AccessTools.Method(type, method, args) : AccessTools.Method(type, method);
                if (target == null)
                {
                    Plugin.Log.LogWarning($"EconomyTuning: {type.Name}.{method} not found - skipped");
                    return;
                }
                h.Patch(target, postfix: new HarmonyMethod(typeof(EconomyTuning), postfix));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"EconomyTuning: patch {type.Name}.{method} failed: {e.Message}"); }
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
            Factors(out float margin, out _, out _, out float costScale, out _);
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
            Factors(out _, out _, out _, out float costScale, out _);
            __result = Cents(__result * costScale);
        }

        public static void CardMarketPostfix(ref float __result)
        {
            if (Off())
                return;
            Factors(out _, out float card, out _, out _, out _);
            __result = Cents(__result * card);
        }

        /// <summary>Scale the overage above market before the vanilla curve reads it.</summary>
        public static void BuyChancePrefix(ref float currentPrice, float marketPrice)
        {
            if (marketPrice <= 0f || currentPrice <= marketPrice)
                return;
            float pick = 1f;
            if (!Off())
                Factors(out _, out _, out pick, out _, out _);
            pick *= Mathf.Clamp(ExternalPickiness, 0.2f, 5f);
            if (Mathf.Approximately(pick, 1f))
                return;
            currentPrice = marketPrice + (currentPrice - marketPrice) * pick;
        }

        /// <summary>Rent and electricity scale; wages (EBillType.Employee) do not.</summary>
        public static void UpdateBillPrefix(EBillType billType, ref float amountToPayChange)
        {
            if (Off() || amountToPayChange <= 0f)
                return;
            if (billType != EBillType.Rent && billType != EBillType.Electric)
                return;
            Factors(out _, out _, out _, out _, out float bill);
            amountToPayChange = Cents(amountToPayChange * bill);
        }
    }
}
