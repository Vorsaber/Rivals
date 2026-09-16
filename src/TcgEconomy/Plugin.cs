using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace TcgEconomy
{
    /// <summary>
    /// TCG Economy Rebase - standalone BepInEx plugin for TCG Card Shop Simulator (game 1.0).
    /// Makes profit harder: see <see cref="EconomyTuning"/>. Works in single player; with
    /// CardShopCoop installed on every PC, guests automatically price like the host.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.vorsaber.tcgeconomy";
        public const string Name = "TCG Economy Rebase";
        public const string Version = "1.0.2";

        internal static ManualLogSource Log;
        internal static ConfigEntry<EconomyProfile> ProfileEntry;
        internal static ConfigEntry<float> CustomMargin;
        internal static ConfigEntry<float> CustomCardValue;
        internal static ConfigEntry<float> CustomPickiness;
        internal static ConfigEntry<float> CustomCost;
        internal static ConfigEntry<float> CustomBills;

        private void Awake()
        {
            Log = Logger;
            ProfileEntry = Config.Bind("Economy", "Profile", EconomyProfile.Tight,
                "Rebases the economy so profit is harder. Vanilla: the game's own numbers (customers accept a 50-200% markup as fair). Tight: half that spread, cards worth 3/4, customers 1.5x as picky about markups, stock costs 10% more, rent and electricity +25%. Harsh: a third of the spread, cards worth 60%, customers twice as picky, stock costs 25% more, rent and electricity +50%. Custom: the values below. Applies to an existing save immediately; nothing in the save is rewritten. In a CardShopCoop session the host's setting applies to everyone.");
            CustomMargin = Config.Bind("Economy", "CustomMarginScale", 0.5f,
                "Custom: scale on the wholesale-to-market spread (1 = vanilla, 0.5 = half the built-in markup, 0 = market price equals cost).");
            CustomCardValue = Config.Bind("Economy", "CustomCardValueScale", 0.75f,
                "Custom: multiplier on card market prices.");
            CustomPickiness = Config.Bind("Economy", "CustomPickiness", 1.5f,
                "Custom: multiplier on how harshly customers judge a price above market (2 = a 10% markup feels like 20%).");
            CustomCost = Config.Bind("Economy", "CustomStockCostScale", 1.1f,
                "Custom: multiplier on what the shop pays to restock.");
            CustomBills = Config.Bind("Economy", "CustomBillScale", 1.25f,
                "Custom: multiplier on the daily rent and electricity bills (wages are untouched).");

            var harmony = new Harmony(Guid);
            EconomyTuning.ApplyPatches(harmony);
            Log.LogInfo($"{Name} {Version} loaded - " + EconomyTuning.Describe());
        }
    }
}
