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
            /// <summary>Opened by a co-op GUEST: home is the team's shop (the world it plays
            /// in), not a save of its own; applied by handing the bag to that shop's host.</summary>
            public bool HomeIsTeam;
            /// <summary>Kept by the league lobby server as the TEAM's bag; this copy is a mirror.
            /// Ops go to the server, the server's state comes back.</summary>
            public bool Shared;
            public string Key = "";
            public List<int> Out = new List<int>();   // server: members currently out with it
            public string HomeShop = "";
            public string VisitingShop = "";
            public double MoneyAtDeparture;
            public double Spent;
            public double Earned;
            public List<Item> Items = new List<Item>();
            public List<Card> Cards = new List<Card>();
            public List<string> Log = new List<string>();
            public string OpenedAt = "";

            /// <summary>A detached copy: the lobby server's ledger and its own mirror must
            /// never be the same object (an op would count twice).</summary>
            public State Clone()
            {
                return JsonUtility.FromJson<State>(JsonUtility.ToJson(this)) ?? new State();
            }
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
                HomeIsTeam = CoopCore.Role == CoopRole.Client,
                HomeShop = SafeShopName(),
                VisitingShop = visitingShop ?? "",
                MoneyAtDeparture = SafeMoney(),
                OpenedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            };
            // in a league: the lobby keeps ONE bag per team - a teammate already out carries it,
            // we piggyback (same balance, same haul); otherwise ours becomes the team's
            if (RivalsLobby.ShareBag(Current))
                Current.Shared = true;
            Save();
            CoopPlugin.Log.LogInfo($"VisitorBag: opened{(Current.Shared ? " (team bag via the lobby)" : "")} - home {(Current.HomeIsTeam ? "team shop '" + Current.HomeShop + "'" : "slot " + Current.HomeSaveIndex)}, wallet {Current.MoneyAtDeparture:0.00}, visiting {Current.VisitingShop}");
        }

        /// <summary>The lobby's copy of the team bag arrived: mirror it (Open=false clears).</summary>
        public static void ApplyShared(State s)
        {
            if (s == null)
                return;
            if (!s.Open)
            {
                if (IsOpen && Current.Shared)
                {
                    Current = new State();
                    Save();
                }
                return;
            }
            s.Shared = true;
            Current = s;
            Save();
        }

        /// <summary>The lobby delivers the team's bag to us (the captain): everyone is home.
        /// Applied to the world we are in - or the next one we load - as long as it is ours.</summary>
        public static void Deliver(State s)
        {
            if (s == null)
                return;
            s.Shared = false;
            s.HomeIsTeam = false;
            s.HomeSaveIndex = -1; // any world of our own
            s.Open = true;
            Current = s;
            Save();
            CoopPlugin.Log.LogInfo($"VisitorBag: team bag delivered - net {s.Earned - s.Spent:0.00}, {s.Cards.Count} card line(s), {s.Items.Count} item line(s)");
            ApplyIfHome();
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
            if (Current.Shared)
                RivalsLobby.SendBagOp(new Net.Messages.RivalsBagMessage { Op = "spend", Amount = amount, What = what ?? "" });
            return true;
        }

        public static void Earn(double amount, string what)
        {
            if (!IsOpen || amount <= 0)
                return;
            Current.Earned += amount;
            Current.Log.Add($"earned {amount:0.00} from {what}");
            Save();
            if (Current.Shared)
                RivalsLobby.SendBagOp(new Net.Messages.RivalsBagMessage { Op = "earn", Amount = amount, What = what ?? "" });
        }

        public static void AddItem(EItemType type, int count, float paid)
        {
            if (!IsOpen || count <= 0)
                return;
            AddItemTo(Current, (int)type, count, paid);
            Current.Log.Add($"item {type} x{count}");
            Save();
            if (Current.Shared)
                RivalsLobby.SendBagOp(new Net.Messages.RivalsBagMessage { Op = "item", ItemType = (int)type, Count = count, Paid = paid });
        }

        public static void AddItemTo(State s, int type, int count, float paid)
        {
            var existing = s.Items.Find(i => i.ItemType == type);
            if (existing != null)
            {
                existing.Count += count;
                existing.Paid += paid;
            }
            else
                s.Items.Add(new Item { ItemType = type, Count = count, Paid = paid });
        }

        public static void AddCardTo(State s, int expansion, int index, bool destiny, int amount, float paid)
        {
            var existing = s.Cards.Find(c => c.Expansion == expansion && c.Index == index && c.IsDestiny == destiny);
            if (existing != null)
            {
                existing.Amount += amount;
                existing.Paid += paid;
            }
            else
                s.Cards.Add(new Card { Expansion = expansion, Index = index, IsDestiny = destiny, Amount = amount, Paid = paid });
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
            AddCardTo(Current, (int)cd.expansionType, index, cd.isDestiny, amount, paid);
            Current.Log.Add($"card {cd.monsterType} ({cd.expansionType}) x{amount}");
            Save();
            if (Current.Shared)
                RivalsLobby.SendBagOp(new Net.Messages.RivalsBagMessage { Op = "card", Expansion = (int)cd.expansionType, Index = index, IsDestiny = cd.isDestiny, Count = amount, Paid = paid });
        }

        /// <summary>Home again, own save loaded: apply everything once and close the bag.
        /// Items arrive as a delivery box at the door (the game has no player inventory for
        /// items); cards go straight into the collection; the wallet takes the net.</summary>
        public static void ApplyIfHome()
        {
            if (!IsOpen)
                return;
            if (CoopCore.IsVisiting)
                return; // the rival's world, not home
            if (Current.Shared)
            {
                // the team's bag lives on the lobby: tell it we are home; it delivers to the
                // captain when the last of us is
                if (RivalsLobby.SendBagBack())
                    CoopPlugin.Log.LogInfo("VisitorBag: home - told the lobby");
                else
                {
                    // no lobby to report to: the server delivers on its own when it saw us
                    // leave; a mirror of the team's bag must not be applied a second time here
                    CoopPlugin.Log.LogWarning("VisitorBag: home without the lobby - dropping the mirror of the team bag (the lobby delivers it)");
                    Current = new State();
                    Save();
                }
                return;
            }
            if (Current.HomeIsTeam)
            {
                DepositToTeam();
                return;
            }
            var gmh = CSingleton<CGameManager>.Instance;
            if (gmh == null || !gmh.m_IsGameLevel)
                return;
            int slot = SafeSaveIndex();
            if (Current.HomeSaveIndex >= 0 && slot != Current.HomeSaveIndex)
            {
                CoopPlugin.Log.LogInfo($"VisitorBag: save slot {slot} loaded, bag belongs to slot {Current.HomeSaveIndex} - waiting");
                return;
            }
            if (CoopCore.Role == CoopRole.Client)
                return; // the rival's world in the scratch slot, not home
            try
            {
                ApplyContents(Current.Earned - Current.Spent, Current.Cards, Current.Items, SafeShopName(), Current.VisitingShop);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag apply: " + e.Message); }
            Current = new State();
            Save();
        }

        /// <summary>Put a bag's contents into the running world: the wallet takes the net,
        /// cards go into the collection, items arrive as a delivery box at the door.</summary>
        private static void ApplyContents(double net, List<Card> cards, List<Item> items, string who, string visited)
        {
            if (net > 0.005)
                CEventManager.QueueEvent(new CEventPlayer_AddCoin((float)net));
            else if (net < -0.005)
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)(-net)));
            int nCards = 0;
            foreach (var c in cards)
            {
                var cd = CPlayerData.GetCardData(c.Index, (ECardExpansionType)c.Expansion, c.IsDestiny);
                if (cd == null || c.Amount <= 0)
                    continue;
                CPlayerData.AddCard(cd, c.Amount);
                nCards += c.Amount;
            }
            int nItems = 0;
            foreach (var it in items)
            {
                if (it.Count <= 0)
                    continue;
                try
                {
                    RestockManager.SpawnPackageBoxItem((EItemType)it.ItemType, it.Count, false);
                    nItems += it.Count;
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning($"VisitorBag: item {(EItemType)it.ItemType} could not be delivered: {e.Message}"); }
            }
            string summary = $"{who} back from {visited}: wallet {(net >= 0 ? "+" : "")}{net:0.00}, {nCards} card(s), {nItems} item(s) at the door";
            CoopPlugin.Log.LogInfo("VisitorBag: applied - " + summary);
            HostOnlyFeatures.Notice("Co-op: " + summary);
        }

        /// <summary>A co-op guest's bag: once we stand in our team's shop again (a client world
        /// whose shop name is the one we left), hand it to the host and close it.</summary>
        private static void DepositToTeam()
        {
            if (CoopCore.Role != CoopRole.Client || CoopCore.Instance == null || CoopCore.IsVisiting)
                return;
            string shop = SafeShopName();
            if (!string.IsNullOrEmpty(Current.HomeShop) && shop != Current.HomeShop)
            {
                CoopPlugin.Log.LogInfo($"VisitorBag: in '{shop}', bag belongs to team shop '{Current.HomeShop}' - waiting");
                return;
            }
            var dep = new Net.Messages.BagDepositMessage
            {
                From = CoopCore.Instance.EffectivePlayerName,
                VisitedShop = Current.VisitingShop,
                Net = Current.Earned - Current.Spent,
            };
            foreach (var it in Current.Items)
            {
                dep.ItemTypes.Add(it.ItemType);
                dep.ItemCounts.Add(it.Count);
            }
            foreach (var c in Current.Cards)
            {
                dep.CardExpansions.Add(c.Expansion);
                dep.CardIndices.Add(c.Index);
                dep.CardDestiny.Add(c.IsDestiny);
                dep.CardAmounts.Add(c.Amount);
            }
            CoopCore.Instance.SendBagDeposit(dep);
            CoopPlugin.Log.LogInfo($"VisitorBag: handed to the team shop - net {dep.Net:0.00}, {Current.Cards.Count} card line(s), {Current.Items.Count} item line(s)");
            HostOnlyFeatures.Notice($"Co-op: your bag from {Current.VisitingShop} went to the shop");
            Current = new State();
            Save();
        }

        /// <summary>Host: a teammate's bag arrives - apply it to this shop.</summary>
        public static void HostApplyDeposit(Net.Messages.BagDepositMessage dep)
        {
            try
            {
                var cards = new List<Card>();
                for (int i = 0; i < dep.CardIndices.Count; i++)
                    cards.Add(new Card
                    {
                        Expansion = i < dep.CardExpansions.Count ? dep.CardExpansions[i] : 0,
                        Index = dep.CardIndices[i],
                        IsDestiny = i < dep.CardDestiny.Count && dep.CardDestiny[i],
                        Amount = i < dep.CardAmounts.Count ? dep.CardAmounts[i] : 0,
                    });
                var items = new List<Item>();
                for (int i = 0; i < dep.ItemTypes.Count; i++)
                    items.Add(new Item { ItemType = dep.ItemTypes[i], Count = i < dep.ItemCounts.Count ? dep.ItemCounts[i] : 0 });
                ApplyContents(dep.Net, cards, items, dep.From ?? "a teammate", dep.VisitedShop ?? "a rival");
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag deposit: " + e.Message); }
        }

        /// <summary>The save SLOT the game last loaded or saved (CSaveLoad.Load/Save patches).
        /// Not CPlayerData.m_SaveIndex, which is a save-generation counter for cloud conflict
        /// resolution, not a slot.</summary>
        public static int LastSlot = -1;
        private static bool s_applyPending;
        private static float s_levelUpAt = -1f;

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
                if (gm == null || !gm.m_IsGameLevel || !GameInstance.m_FinishedSavefileLoading)
                    return;
                if (s_levelUpAt < 0f)
                {
                    s_levelUpAt = Time.unscaledTime;
                    return;
                }
                if (Time.unscaledTime - s_levelUpAt < 2f)
                    return; // let the world settle (a client's shop name arrives with the load)
                s_applyPending = false;
                s_levelUpAt = -1f;
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
