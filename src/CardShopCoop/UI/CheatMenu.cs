using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Test-rig cheat menu (default key F4, config Keys.CheatMenuKey; Cheats.Enabled to
    /// hide it). HOST / single player only: every action goes through the game's own
    /// path - the same events, spawners and card writers a purchase or a pack opening
    /// would use - so what it gives the host reaches the guest through the existing syncs
    /// (wallet events, AddCard forward, box presence protocol, furniture spawn postfix).
    /// On a guest the window only says so; nothing here is ever executed on a client.
    ///
    /// Not a gameplay feature. It exists because a fresh 1.0 save has no money, no
    /// licenses, no cards and no play table, and every co-op test needs all four.
    /// </summary>
    public sealed class CheatMenu : MonoBehaviour
    {
        private bool _visible;
        private Rect _rect = new Rect(40, 40, 470, 620);
        private Vector2 _scroll;
        private string _filter = "";
        private string _status = "";
        private float _statusAt;
        private int _tab;
        private static readonly string[] Tabs = { "Shop", "Cards", "Boxes", "Furniture" };

        // UI-mode ownership, same discipline as the co-op window: only undo what we entered
        private bool _uiModeHeld;
        private InteractionPlayerController _uiModeController;

        private static readonly ECardExpansionType[] Expansions =
        {
            ECardExpansionType.Tetramon, ECardExpansionType.Destiny, ECardExpansionType.Ghost,
            ECardExpansionType.Megabot, ECardExpansionType.FantasyRPG, ECardExpansionType.CatJob,
            ECardExpansionType.Ascension,
        };

        private void Update()
        {
            if (CoopPlugin.CheatsEnabled == null || !CoopPlugin.CheatsEnabled.Value)
                return;
            var key = CoopPlugin.CheatMenuKey != null ? CoopPlugin.CheatMenuKey.Value : KeyCode.F4;
            if (key != KeyCode.None && Input.GetKeyDown(key))
                _visible = !_visible;
            SyncUIMode();
        }

        private static bool InGame() => CSingleton<ShelfManager>.Instance != null && InteractionPlayerController.m_Instance != null;

        private void SyncUIMode()
        {
            bool want = _visible && InGame();
            if (want)
            {
                if (_uiModeHeld && _uiModeController != null)
                {
                    if (!_uiModeController.IsInUIMode())
                        _uiModeController.EnterUIMode();
                    return;
                }
                var ipc = InteractionPlayerController.m_Instance;
                if (ipc == null || ipc.IsInUIMode())
                    return; // a game screen owns UI mode; don't fight it
                ipc.EnterUIMode();
                _uiModeHeld = true;
                _uiModeController = ipc;
                return;
            }
            if (_uiModeHeld && _uiModeController != null)
                _uiModeController.ExitUIMode();
            _uiModeHeld = false;
            _uiModeController = null;
        }

        private void OnGUI()
        {
            if (!_visible)
                return;
            _rect = GUILayout.Window(0x5C4EA7, _rect, DrawWindow, "Co-op test cheats  (" + (CoopPlugin.CheatMenuKey != null ? CoopPlugin.CheatMenuKey.Value.ToString() : "F4") + " to close)");
        }

        private void DrawWindow(int id)
        {
            if (CoopCore.Role == CoopRole.Client)
            {
                GUILayout.Label("Host only. Everything here changes the shop; on a guest it would be thrown away with the scratch save. Ask the host to use theirs.");
                GUI.DragWindow();
                return;
            }
            if (!InGame())
            {
                GUILayout.Label("Load a save first.");
                GUI.DragWindow();
                return;
            }
            _tab = GUILayout.Toolbar(_tab, Tabs);
            GUILayout.Space(6);
            try
            {
                switch (_tab)
                {
                    case 0:
                        DrawShop();
                        break;
                    case 1:
                        DrawCards();
                        break;
                    case 2:
                        DrawBoxes();
                        break;
                    case 3:
                        DrawFurniture();
                        break;
                }
            }
            catch (Exception e)
            {
                Say("error: " + e.Message);
                CoopPlugin.Log.LogWarning("CheatMenu: " + e);
            }
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime - _statusAt < 4f)
                GUILayout.Label(_status);
            GUI.DragWindow();
        }

        // ------------------------------------------------------------------ Shop

        private void DrawShop()
        {
            GUILayout.Label($"Money: {GameInstance.GetPriceString(CPlayerData.m_CoinAmountDouble)}    Level: {CPlayerData.m_ShopLevel}    Tutorial: {(CPlayerData.m_HasFinishedTutorial ? "done" : "step " + CPlayerData.m_TutorialIndex)}");
            GUILayout.Space(4);
            GUILayout.Label("Money");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+ $10,000"))
                AddMoney(10000f);
            if (GUILayout.Button("+ $100,000"))
                AddMoney(100000f);
            if (GUILayout.Button("+ $1,000,000"))
                AddMoney(1000000f);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label("Shop level");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1"))
                SetLevel(CPlayerData.m_ShopLevel + 1);
            if (GUILayout.Button("+5"))
                SetLevel(CPlayerData.m_ShopLevel + 5);
            if (GUILayout.Button("Set 10"))
                SetLevel(10);
            if (GUILayout.Button("Set 20"))
                SetLevel(20);
            if (GUILayout.Button("Set 40"))
                SetLevel(40);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            if (GUILayout.Button("Unlock every item license"))
            {
                int n = 0;
                var lic = CPlayerData.m_IsItemLicenseUnlocked;
                for (int i = 0; lic != null && i < lic.Count; i++)
                    if (!lic[i])
                    {
                        CPlayerData.SetUnlockItemLicense(i);
                        n++;
                    }
                Say($"unlocked {n} licenses (reopen the phone shop to see them)");
            }
            if (GUILayout.Button("Finish the tutorial"))
            {
                CPlayerData.m_HasFinishedTutorial = true;
                CPlayerData.m_TutorialIndex = 99;
                var tm = FindObjectOfType<TutorialManager>();
                if (tm != null)
                {
                    // mark every subgroup's task finished first, otherwise EvaluateTaskVisibility
                    // reopens the first unfinished panel and rewrites the index
                    var fi = HarmonyLib.AccessTools.Field(typeof(TutorialSubGroup), "m_IsTaskFinish");
                    if (tm.m_TutorialSubGroupList != null)
                        foreach (var sg in tm.m_TutorialSubGroupList)
                        {
                            if (sg == null)
                                continue;
                            try
                            {
                                fi?.SetValue(sg, true);
                            }
                            catch { }
                            try
                            {
                                sg.CloseScreen();
                            }
                            catch { }
                        }
                    try
                    {
                        tm.EvaluateTaskVisibility();
                    }
                    catch { }
                    CPlayerData.m_TutorialIndex = 99;
                    if (tm.m_TutorialTargetIndicator != null)
                        tm.m_TutorialTargetIndicator.SetActive(false);
                }
                GameUIScreen.SetGameUIVisible(isVisible: true);
                Say("tutorial marked finished");
            }
        }

        private void AddMoney(float amount)
        {
            CEventManager.QueueEvent(new CEventPlayer_AddCoin(amount));
            Say($"+{GameInstance.GetPriceString(amount)}");
        }

        private void SetLevel(int level)
        {
            level = Mathf.Clamp(level, 1, 99);
            CPlayerData.m_ShopLevel = level;
            CEventManager.QueueEvent(new CEventPlayer_SetShopExp(0));
            CEventManager.QueueEvent(new CEventPlayer_ShopLeveledUp(level));
            Say($"shop level {level}");
        }

        // ------------------------------------------------------------------ Cards

        private void DrawCards()
        {
            GUILayout.Label("Add one of every card in an expansion to the collection (goes through CPlayerData.AddCard, so the guest gets them too).");
            foreach (var exp in Expansions)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(exp.ToString(), GUILayout.Width(110));
                if (GUILayout.Button("full set x1"))
                    GiveSet(exp, 1);
                if (GUILayout.Button("full set x5"))
                    GiveSet(exp, 5);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);
            GUILayout.Label("Starter deck: 50 different Tetramon base cards, added to the collection and saved as a new deck, then selected.");
            if (GUILayout.Button("Give and select a 50-card starter deck"))
                GiveStarterDeck();
        }

        private void GiveSet(ECardExpansionType exp, int amount)
        {
            var monsters = InventoryBase.GetShownMonsterList(exp);
            int per = CPlayerData.GetCardAmountPerMonsterType(exp);
            if (monsters == null || per <= 0)
            {
                Say("no card list for " + exp);
                return;
            }
            int total = monsters.Count * per, n = 0;
            for (int i = 0; i < total; i++)
            {
                var cd = CPlayerData.GetCardData(i, exp, isDestiny: false);
                if (cd == null)
                    continue;
                CPlayerData.AddCard(cd, amount);
                n++;
            }
            Say($"{exp}: {n} cards x{amount} added");
        }

        private void GiveStarterDeck()
        {
            var exp = ECardExpansionType.Tetramon;
            var monsters = InventoryBase.GetShownMonsterList(exp);
            int per = CPlayerData.GetCardAmountPerMonsterType(exp);
            int want = GameInstance.GetMaxDeckCardCount();
            if (monsters == null || monsters.Count == 0 || per <= 0)
            {
                Say("no Tetramon card list");
                return;
            }
            var deck = new DeckCompactCardDataList
            {
                deckName = "Cheat Deck " + (CPlayerData.m_DeckCompactCardDataList.Count + 1),
                deckBoxIndex = 0,
                playmatIndex = 0,
                compactCardDataAmountList = new List<CompactCardDataAmount>(),
            };
            int added = 0;
            for (int m = 0; m < monsters.Count && added < want; m++)
            {
                int saveIndex = m * per; // base border, non-foil
                var cd = CPlayerData.GetCardData(saveIndex, exp, isDestiny: false);
                if (cd == null)
                    continue;
                CPlayerData.AddCard(cd, 1);
                deck.compactCardDataAmountList.Add(new CompactCardDataAmount
                {
                    expansionType = exp,
                    cardSaveIndex = saveIndex,
                    amount = 1,
                    gradedCardIndex = -1,
                    isDestiny = false,
                });
                added++;
            }
            // fewer monsters than 50: pad with second copies
            for (int m = 0; added < want && m < deck.compactCardDataAmountList.Count; m++)
            {
                deck.compactCardDataAmountList[m].amount++;
                CPlayerData.AddCard(CPlayerData.GetCardData(deck.compactCardDataAmountList[m].cardSaveIndex, exp, false), 1);
                added++;
            }
            CPlayerData.m_DeckCompactCardDataList.Add(deck);
            CPlayerData.m_CurrentSelectedDeckIndex = CPlayerData.m_DeckCompactCardDataList.Count - 1;
            Say($"deck '{deck.deckName}' ({added} cards) created and selected");
        }

        // ------------------------------------------------------------------ Boxes

        private void DrawBoxes()
        {
            GUILayout.Label("Deliver a box through the game's own restock spawner (RestockManager.SpawnPackageBoxItemMultipleFrame). Same as a phone order landing.");
            GUILayout.BeginHorizontal();
            GUILayout.Label("filter", GUILayout.Width(40));
            _filter = GUILayout.TextField(_filter);
            GUILayout.EndHorizontal();
            var inv = CSingleton<InventoryBase>.Instance;
            var list = inv != null && inv.m_StockItemData_SO != null ? inv.m_StockItemData_SO.m_RestockDataList : null;
            if (list == null)
            {
                GUILayout.Label("restock catalog not loaded");
                return;
            }
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(400));
            string f = (_filter ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < list.Count; i++)
            {
                var rd = list[i];
                if (rd == null)
                    continue;
                string name = ItemName(rd);
                if (f.Length > 0 && name.ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) < 0)
                    continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(name, GUILayout.Width(280));
                if (GUILayout.Button("+1", GUILayout.Width(40)))
                    Deliver(i, name, 1);
                if (GUILayout.Button("+5", GUILayout.Width(40)))
                    Deliver(i, name, 5);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private static string ItemName(RestockData rd)
        {
            try
            {
                var data = InventoryBase.GetItemData(rd.itemType);
                string n = data != null ? data.GetName() : null;
                if (string.IsNullOrEmpty(n))
                    n = rd.name;
                if (string.IsNullOrEmpty(n))
                    n = rd.itemType.ToString();
                return n + (rd.isBigBox ? "  (big box)" : "");
            }
            catch { return rd.itemType.ToString(); }
        }

        private void Deliver(int restockIndex, string name, int count)
        {
            RestockManager.SpawnPackageBoxItemMultipleFrame(restockIndex, count);
            Say($"delivering {count} x {name}");
        }

        // ------------------------------------------------------------------ Furniture

        private static string[] s_objNames;
        private static EObjectType[] s_objValues;

        private void DrawFurniture()
        {
            GUILayout.Label("Drop a boxed piece of furniture in front of you (ShelfManager.SpawnInteractableObjectInPackageBox - the delivery recipe). Open the box to place it.");
            if (s_objValues == null)
            {
                var vals = (EObjectType[])Enum.GetValues(typeof(EObjectType));
                var names = new List<string>();
                var keep = new List<EObjectType>();
                foreach (var v in vals)
                {
                    string n = v.ToString();
                    if (n == "None" || n == "MAX" || n.StartsWith("Card3d") || n.StartsWith("PackageBox"))
                        continue;
                    names.Add(n);
                    keep.Add(v);
                }
                s_objNames = names.ToArray();
                s_objValues = keep.ToArray();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("filter", GUILayout.Width(40));
            _filter = GUILayout.TextField(_filter);
            GUILayout.EndHorizontal();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(400));
            string f = (_filter ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < s_objNames.Length; i++)
            {
                if (f.Length > 0 && s_objNames[i].ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) < 0)
                    continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(s_objNames[i], GUILayout.Width(300));
                if (GUILayout.Button("spawn", GUILayout.Width(60)))
                    SpawnFurniture(s_objValues[i]);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void SpawnFurniture(EObjectType type)
        {
            var ipc = InteractionPlayerController.m_Instance;
            var t = ipc != null && ipc.m_PlayerCollider != null ? ipc.m_PlayerCollider.transform : null;
            if (t == null)
            {
                Say("no player");
                return;
            }
            var fwd = t.forward;
            fwd.y = 0f;
            fwd.Normalize();
            var pos = t.position + fwd * 1.5f + Vector3.up * 0.5f;
            ShelfManager.SpawnInteractableObjectInPackageBox(type, pos, Quaternion.LookRotation(-fwd, Vector3.up));
            Say($"boxed {type} dropped in front of you");
        }

        private void Say(string s)
        {
            _status = s;
            _statusAt = Time.unscaledTime;
            CoopPlugin.Log.LogInfo("CheatMenu: " + s);
        }
    }
}
