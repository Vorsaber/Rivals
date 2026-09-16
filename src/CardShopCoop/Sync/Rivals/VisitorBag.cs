using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// The carry-out bag (competitive mode, phase 2). A VISITOR is a co-op guest in a rival's
    /// shop whose own save sits untouched at home; anything they buy, win or trade there is
    /// recorded here and applied to their REAL save the next time it loads. Money is tracked
    /// against the wallet as it stood when they left home, so a purchase can be refused at
    /// the rival's register rather than discovered at home.
    ///
    /// Persisted to disk on every change (BepInEx/config/CardShopCoop.visitorbag.json) so a
    /// crash mid-visit loses nothing; applied exactly once, on the load of the save slot it
    /// was opened from, then cleared. This is the ONLY thing in the mod that writes a real
    /// save across machines - keep it idempotent and boring.
    /// </summary>
    public static class VisitorBag
    {
        [Serializable]
        public sealed class Item
        {
            public int ItemType;
            public int Count;
            public float Paid;
        }

        [Serializable]
        public sealed class Card
        {
            public int Expansion;
            public int Index;
            public bool IsDestiny;
            public int Amount;
            public float Paid;
        }

        [Serializable]
        public sealed class State
        {
            public bool Open;
            public int HomeSaveIndex = -1;
            public string HomeShop = "";
            public string VisitingShop = "";
            public double MoneyAtDeparture;
            public double Spent;
            public double Earned;
            public List<Item> Items = new List<Item>();
            public List<Card> Cards = new List<Card>();
            public List<string> Log = new List<string>();
            public string OpenedAt = "";
        }

        public static State Current = new State();

        private static string PathOnDisk()
        {
            return Path.Combine(BepInEx.Paths.ConfigPath, "CardShopCoop.visitorbag.json");
        }

        public static void Load()
        {
            try
            {
                string p = PathOnDisk();
                if (File.Exists(p))
                {
                    Current = JsonUtility.FromJson<State>(File.ReadAllText(p)) ?? new State();
                    if (Current.Open)
                        CoopPlugin.Log.LogInfo($"VisitorBag: an open bag from {Current.OpenedAt} (home slot {Current.HomeSaveIndex}, {Current.Items.Count} items, {Current.Cards.Count} cards, spent {Current.Spent:0.00}, earned {Current.Earned:0.00}) - applied when that save loads");
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("VisitorBag load: " + e.Message);
                Current = new State();
            }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(PathOnDisk(), JsonUtility.ToJson(Current, true));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag save: " + e.Message); }
        }

        /// <summary>Called on the visitor's PC as they leave home (before the rival's world
        /// replaces the scratch slot): remember whose save this is and what they had.</summary>
        public static void Open(string visitingShop)
        {
            if (Current.Open)
            {
                CoopPlugin.Log.LogWarning("VisitorBag: opening a new bag over an unapplied one - keeping the old contents");
                Current.VisitingShop = visitingShop ?? "";
                Save();
                return;
            }
            Current = new State
            {
                Open = true,
                HomeSaveIndex = SafeSaveIndex(),
                HomeShop = SafeShopName(),
                VisitingShop = visitingShop ?? "",
                MoneyAtDeparture = SafeMoney(),
                OpenedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            };
            Save();
            CoopPlugin.Log.LogInfo($"VisitorBag: opened - home slot {Current.HomeSaveIndex}, wallet {Current.MoneyAtDeparture:0.00}, visiting {Current.VisitingShop}");
        }

        public static bool IsOpen => Current != null && Current.Open;

        /// <summary>What the visitor can still spend.</summary>
        public static double Balance => IsOpen ? Current.MoneyAtDeparture - Current.Spent + Current.Earned : 0;

        public static bool TrySpend(double amount, string what)
        {
            if (!IsOpen || amount < 0)
                return false;
            if (Balance < amount)
                return false;
            Current.Spent += amount;
            Current.Log.Add($"spent {amount:0.00} on {what}");
            Save();
            return true;
        }

        public static void Earn(double amount, string what)
        {
            if (!IsOpen || amount <= 0)
                return;
            Current.Earned += amount;
            Current.Log.Add($"earned {amount:0.00} from {what}");
            Save();
        }

        public static void AddItem(EItemType type, int count, float paid)
        {
            if (!IsOpen || count <= 0)
                return;
            var existing = Current.Items.Find(i => i.ItemType == (int)type);
            if (existing != null)
            {
                existing.Count += count;
                existing.Paid += paid;
            }
            else
                Current.Items.Add(new Item { ItemType = (int)type, Count = count, Paid = paid });
            Current.Log.Add($"item {type} x{count}");
            Save();
        }

        public static void AddCard(CardData cd, int amount, float paid)
        {
            if (!IsOpen || cd == null || amount <= 0)
                return;
            int index;
            try
            {
                index = CPlayerData.GetCardSaveIndex(cd);
            }
            catch { return; }
            var existing = Current.Cards.Find(c => c.Expansion == (int)cd.expansionType && c.Index == index && c.IsDestiny == cd.isDestiny);
            if (existing != null)
            {
                existing.Amount += amount;
                existing.Paid += paid;
            }
            else
                Current.Cards.Add(new Card { Expansion = (int)cd.expansionType, Index = index, IsDestiny = cd.isDestiny, Amount = amount, Paid = paid });
            Current.Log.Add($"card {cd.monsterType} ({cd.expansionType}) x{amount}");
            Save();
        }

        /// <summary>Home again, own save loaded: apply everything once and close the bag.
        /// Items arrive as a delivery box at the door (the game has no player inventory for
        /// items); cards go straight into the collection; the wallet takes the net.</summary>
        public static void ApplyIfHome()
        {
            if (!IsOpen)
                return;
            int slot = SafeSaveIndex();
            if (slot != Current.HomeSaveIndex)
            {
                CoopPlugin.Log.LogInfo($"VisitorBag: save slot {slot} loaded, bag belongs to slot {Current.HomeSaveIndex} - waiting");
                return;
            }
            if (CoopCore.Role == CoopRole.Client)
                return; // the rival's world in the scratch slot, not home
            try
            {
                double net = Current.Earned - Current.Spent;
                if (net > 0.005)
                    CEventManager.QueueEvent(new CEventPlayer_AddCoin((float)net));
                else if (net < -0.005)
                    CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)(-net)));
                int cards = 0;
                foreach (var c in Current.Cards)
                {
                    var cd = CPlayerData.GetCardData(c.Index, (ECardExpansionType)c.Expansion, c.IsDestiny);
                    if (cd == null || c.Amount <= 0)
                        continue;
                    CPlayerData.AddCard(cd, c.Amount);
                    cards += c.Amount;
                }
                int items = 0;
                foreach (var it in Current.Items)
                {
                    if (it.Count <= 0)
                        continue;
                    try
                    {
                        RestockManager.SpawnPackageBoxItem((EItemType)it.ItemType, it.Count, false);
                        items += it.Count;
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning($"VisitorBag: item {(EItemType)it.ItemType} could not be delivered: {e.Message}"); }
                }
                string summary = $"back from {Current.VisitingShop}: wallet {(net >= 0 ? "+" : "")}{net:0.00}, {cards} card(s), {items} item(s) at the door";
                CoopPlugin.Log.LogInfo("VisitorBag: applied - " + summary);
                HostOnlyFeatures.Notice("Co-op: " + summary);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag apply: " + e.Message); }
            Current = new State();
            Save();
        }

        /// <summary>The save SLOT the game last loaded or saved (CSaveLoad.Load/Save patches).
        /// Not CPlayerData.m_SaveIndex, which is a save-generation counter for cloud conflict
        /// resolution, not a slot.</summary>
        public static int LastSlot = -1;
        private static bool s_applyPending;

        public static void ApplyPatches(HarmonyLib.Harmony h)
        {
            try
            {
                var load = HarmonyLib.AccessTools.Method(typeof(CSaveLoad), "Load", new[] { typeof(int) });
                if (load != null)
                    h.Patch(load, postfix: new HarmonyLib.HarmonyMethod(typeof(VisitorBag), nameof(LoadPostfix)));
                var save = HarmonyLib.AccessTools.Method(typeof(CSaveLoad), "Save", new[] { typeof(int), typeof(bool) });
                if (save != null)
                    h.Patch(save, prefix: new HarmonyLib.HarmonyMethod(typeof(VisitorBag), nameof(SavePrefix)));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag patches: " + e.Message); }
        }

        public static void LoadPostfix(int slotIndex, bool __result)
        {
            if (!__result)
                return;
            LastSlot = slotIndex;
            s_applyPending = IsOpen;
        }

        public static void SavePrefix(int saveSlotIndex)
        {
            if (CoopCore.Role != CoopRole.Client)
                LastSlot = saveSlotIndex;
        }

        /// <summary>Ticked by the lobby MonoBehaviour: once the loaded world is up, apply.</summary>
        public static void Tick()
        {
            if (!s_applyPending)
                return;
            try
            {
                var gm = CSingleton<CGameManager>.Instance;
                if (gm == null || !gm.m_IsGameLevel)
                    return;
                s_applyPending = false;
                ApplyIfHome();
            }
            catch (Exception e)
            {
                s_applyPending = false;
                CoopPlugin.Log.LogWarning("VisitorBag tick: " + e.Message);
            }
        }

        private static int SafeSaveIndex()
        {
            return LastSlot;
        }

        private static double SafeMoney()
        {
            try
            {
                return CPlayerData.m_CoinAmountDouble;
            }
            catch { return 0; }
        }

        private static string SafeShopName()
        {
            try
            {
                return CPlayerData.PlayerName ?? "";
            }
            catch { return ""; }
        }
    }
}
