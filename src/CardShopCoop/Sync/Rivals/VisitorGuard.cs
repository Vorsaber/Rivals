using System;
using System.Collections.Generic;
using HarmonyLib;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// What a VISITOR's own hands may not do in a rival's shop. The host already drops every
    /// message that would change the shop (CoopCore.VisitorMayDo), but the visitor's local
    /// world used to act first and drift until the heal - furniture floated across their
    /// screen, a register opened, a price tag popped. This stops those interactions at the
    /// click, with a notice, so what the visitor sees is what the host has.
    ///
    /// Allowed, unchanged: walking, looking, the play tables (battles, PvP), taking stock and
    /// display cards (buy-by-taking), the tournament screen (entry), the card album, chat,
    /// the phone's read-only pages, TRADE / DECKS apps.
    /// </summary>
    internal static class VisitorGuard
    {
        private static bool Visiting => CoopCore.IsVisiting;

        // one prefix for every blocked entry point: bail with a notice while visiting
        public static bool BlockPrefix()
        {
            if (!Visiting)
                return true;
            HostOnlyFeatures.Notice("Visit: you can't change a rival's shop");
            return false;
        }

        public static bool BlockPhonePrefix()
        {
            if (!Visiting)
                return true;
            HostOnlyFeatures.Notice("Visit: that's the shop owner's business");
            return false;
        }

        public static void ApplyPatches(Harmony h)
        {
            var block = new HarmonyMethod(typeof(VisitorGuard), nameof(BlockPrefix)) { priority = Priority.First };
            var phone = new HarmonyMethod(typeof(VisitorGuard), nameof(BlockPhonePrefix)) { priority = Priority.First };
            int n = 0;

            // moving / boxing up ANY furniture: the base and every override (virtual dispatch
            // lands on the override, so the base patch alone would not catch a Shelf)
            foreach (var t in new[]
            {
                typeof(InteractableObject), typeof(Shelf), typeof(CardShelf), typeof(WarehouseShelf),
                typeof(InteractableAutoPackOpener), typeof(InteractableCashierCounter),
                typeof(InteractablePackagingBox), typeof(InteractablePlayTable),
            })
                foreach (string m in new[] { "StartMoveObject", "BoxUpObject" })
                    n += Patch(h, t, m, block);

            // the click on things that change the shop or take its money / stock outside a sale
            foreach (var t in new[]
            {
                typeof(InteractableCashierCounter), typeof(InteractableCounterMoneyChange), typeof(InteractableCustomerCash),
                typeof(InteractablePriceTag), typeof(InteractableCardPriceTag),
                typeof(InteractablePackagingBox), typeof(InteractableEmptyBoxStorage), typeof(InteractableStorageCompartment),
                typeof(InteractableWorkbench), typeof(InteractableAutoCleanser), typeof(InteractableAutoPackOpener),
                typeof(InteractableTrashBin), typeof(InteractableBulkDonationBox), typeof(InteractableLightSwitch),
                typeof(InteractableOpenCloseSign), typeof(InteractableWarehouseAllowEnterSign), typeof(InteractableShopBillboard),
                typeof(InteractableDecoration), typeof(InteractableScanItem),
            })
                foreach (string m in new[] { "OnMouseButtonUp", "OnRightMouseButtonUp" })
                    n += Patch(h, t, m, block);

            // staff
            n += Patch(h, typeof(WorkerCollider), "OnMousePress", block);
            n += Patch(h, typeof(WorkerCollider), "OnRightMousePress", block);

            // the phone: the owner's management pages (settings, camera, gallery, reviews, price
            // check and the tournament screen - for entering - stay open)
            foreach (string m in new[]
            {
                "OnPressBuyProductBtn", "OnPressBuyBoardGameProductBtn", "OnPressBuyFurnitureBtn", "OnPressExpandShopBtn",
                "OnPressManageEventBtn", "OnPressHireWorkerBtn", "OnPressRentBillBtn", "OnPressBuyDecorationBtn",
                "OnPressGradingCardBtn", "OnPressScannerBtn",
            })
                n += Patch(h, typeof(UI_PhoneScreen), m, phone);

            CoopPlugin.Log.LogInfo($"VisitorGuard: {n} entry points gated for visitors");
        }

        /// <summary>Patch the method only where it is DECLARED on that type (an inherited
        /// method is patched once, on its declaring type).</summary>
        private static int Patch(Harmony h, Type type, string method, HarmonyMethod prefix)
        {
            try
            {
                var m = AccessTools.DeclaredMethod(type, method);
                if (m == null)
                    return 0;
                h.Patch(m, prefix: prefix);
                return 1;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"VisitorGuard: {type.Name}.{method}: {e.Message}");
                return 0;
            }
        }
    }
}
