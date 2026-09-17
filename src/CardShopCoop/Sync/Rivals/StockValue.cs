using System;
using System.Collections.Generic;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// What the shop's STOCK is worth at MARKET price (fv-683): items on the sales shelves
    /// and the item side of combi shelves, every item box (on the floor, in hand, or stored
    /// on a warehouse shelf - the box carries the items, so warehouse compartments are not
    /// counted again), single cards on display, and card boxes. Market price, not the set
    /// price, so a shop cannot inflate its value with a $9,999 tag, and shops on the shared
    /// market compare like with like. Money + this = the shop value the board ranks by.
    /// Never CSingleton: FindObjectOfType (see WorldSync.ResolveShelfManager).
    /// </summary>
    internal static class StockValue
    {
        private static ShelfManager _sm;
        private static RestockManager _restock;

        public static void Reset()
        {
            _sm = null;
            _restock = null;
        }

        /// <summary>Total market value of the stock; <paramref name="items"/> and
        /// <paramref name="cards"/> are the unit counts behind it.</summary>
        public static double Compute(out int items, out int cards)
        {
            items = 0;
            cards = 0;
            double total = 0;
            try
            {
                if (_sm == null)
                    _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
                if (_restock == null)
                    _restock = UnityEngine.Object.FindObjectOfType<RestockManager>();
                var sm = _sm;
                if (sm == null)
                    return 0;

                // sales shelves
                foreach (var shelf in sm.m_ShelfList)
                {
                    if (shelf == null)
                        continue;
                    var comps = shelf.GetItemCompartmentList();
                    for (int i = 0; comps != null && i < comps.Count; i++)
                        total += ItemValue(comps[i], ref items);
                }
                // the item side of card+item combi shelves
                foreach (var combi in sm.m_CardItemCombiShelfList)
                {
                    if (combi == null)
                        continue;
                    var comps = combi.GetItemCompartmentList();
                    for (int i = 0; comps != null && i < comps.Count; i++)
                        total += ItemValue(comps[i], ref items);
                }
                // item boxes, wherever they are
                if (_restock != null && _restock.m_ItemPackagingBoxList != null)
                    foreach (var box in _restock.m_ItemPackagingBoxList)
                        if (box != null && box.m_ItemCompartment != null)
                            total += ItemValue(box.m_ItemCompartment, ref items);
                // single cards on display
                total += CardShelves(sm.m_CardShelfList, ref cards);
                foreach (var combi in sm.m_CardItemCombiShelfList)
                    total += CardShelf(combi, ref cards);
                // card boxes
                if (_restock != null && _restock.m_CardPackagingBoxList != null)
                    foreach (var box in _restock.m_CardPackagingBoxList)
                    {
                        if (box == null)
                            continue;
                        var list = box.GetCardDataList();
                        for (int i = 0; list != null && i < list.Count; i++)
                            total += CardValue(list[i], ref cards);
                    }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("Rivals StockValue: " + e.Message); }
            return total;
        }

        private static double ItemValue(ShelfCompartment comp, ref int items)
        {
            if (comp == null)
                return 0;
            var type = comp.GetItemType();
            int count = comp.GetItemCount();
            if (type == EItemType.None || count <= 0)
                return 0;
            float m = CPlayerData.GetItemMarketPrice(type);
            if (m <= 0f || float.IsNaN(m))
                return 0;
            items += count;
            return (double)m * count;
        }

        private static double CardShelves(List<CardShelf> shelves, ref int cards)
        {
            double total = 0;
            for (int i = 0; shelves != null && i < shelves.Count; i++)
                total += CardShelf(shelves[i], ref cards);
            return total;
        }

        private static double CardShelf(CardShelf shelf, ref int cards)
        {
            if (shelf == null)
                return 0;
            double total = 0;
            var comps = shelf.GetCardCompartmentList();
            for (int i = 0; comps != null && i < comps.Count; i++)
            {
                var comp = comps[i];
                if (comp == null || comp.m_StoredCardList == null || comp.m_StoredCardList.Count == 0)
                    continue;
                var card3d = comp.m_StoredCardList[0];
                if (card3d == null || card3d.m_Card3dUI == null || card3d.m_Card3dUI.m_CardUI == null)
                    continue;
                total += CardValue(card3d.m_Card3dUI.m_CardUI.GetCardData(), ref cards);
            }
            return total;
        }

        private static double CardValue(CardData card, ref int cards)
        {
            if (card == null)
                return 0;
            float m;
            try
            {
                m = CPlayerData.GetCardMarketPrice(card);
            }
            catch { return 0; }
            if (m <= 0f || float.IsNaN(m))
                return 0;
            cards++;
            return m;
        }
    }
}
