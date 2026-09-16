using System;
using System.Collections.Generic;
using UnityEngine;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Test-rig cheat menu (default key F4, config Keys.CheatMenuKey; Cheats.Enabled to
    /// hide it). Every action runs on the HOST through the game's own path - the same
    /// events, spawners and card writers a purchase or a pack opening would use - so what it
    /// gives the shop reaches the guest through the existing syncs (wallet events, AddCard
    /// forward, box presence protocol, furniture spawn postfix). On a guest every button is a
    /// CheatRequest the host runs (Cheats.AllowGuestRequests to refuse); nothing here is ever
    /// executed on a client, whose writes would die with the scratch save.
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

        /// <summary>Every button is one of these, so a guest's press can travel to the host
        /// as a <see cref="CheatRequestMessage"/> and run there through the same code.</summary>
        internal enum Op
        {
            None = 0,
            AddMoney,        // A = amount
            LevelAdd,        // A = delta
            LevelSet,        // A = level
            Licenses,
            Rooms,           // A = max, B = 1 warehouse
            Warehouse,
            Deco,            // A = category
            TableFees,       // A = 0 free, 1 market
            FreeTables,
            Tutorial,
            GiveSet,         // A = expansion, B = amount
            GiveBase,        // A = expansion, B = amount
            StarterDeck,
            Deliver,         // A = restock index, B = count
            Furniture,       // A = EObjectType
            Difficulty,      // A = DifficultyProfile
            Economy,         // A = EconomyProfile
            DuplicateDeck,   // A = deck index (-1 = the host's selected)
        }

        // set by StarterDeck / DuplicateDeck: the index of the deck just made, for the requester
        private int _madeDeckIndex = -1;

        // set by CoopCore: the guest's requests go up, the host's answers come back
        public static Action<INetMessage> SendToHost;
        public static Action<int, INetMessage> SendToClient;
        public static Func<int, string> PeerName;
        public static Func<int, (bool ok, Vector3 pos, Vector3 fwd)> PeerPose;
        private static CheatMenu s_instance;

        // while a guest's request runs on the host, "in front of you" means in front of them
        private static bool s_hasRemotePose;
        private static bool s_remoteRequest;    // a guest asked for this (not the host pressing)
        private static Vector3 s_remotePos;
        private static Vector3 s_remoteFwd;

        private void Awake()
        {
            s_instance = this;
        }

        /// <summary>Run an action here (host / solo) or ship it to the host (guest).</summary>
        private void Do(Op op, int a = 0, int b = 0)
        {
            if (CoopCore.Role == CoopRole.Client)
            {
                if (SendToHost == null)
                {
                    Say("not connected");
                    return;
                }
                SendToHost(new CheatRequestMessage { Op = (int)op, A = a, B = b });
                Say("asked the host: " + op);
                return;
            }
            Run(op, a, b);
        }

        /// <summary>Host: a guest pressed a button. Same code path as a local press.</summary>
        public static void HostApplyRequest(CheatRequestMessage msg, int conn)
        {
            var me = s_instance;
            if (me == null)
                return;
            string text;
            int madeDeck = -1;
            try
            {
                if (CoopPlugin.CheatsEnabled == null || !CoopPlugin.CheatsEnabled.Value)
                    text = "the host has cheats turned off";
                else if (CoopPlugin.CheatsForGuests != null && !CoopPlugin.CheatsForGuests.Value)
                    text = "the host has guest cheats turned off (Cheats > AllowGuestRequests)";
                else if (!InGame())
                    text = "the host has no save loaded";
                else
                {
                    s_hasRemotePose = false;
                    if (PeerPose != null)
                    {
                        var pose = PeerPose(conn);
                        s_hasRemotePose = pose.ok;
                        s_remotePos = pose.pos;
                        s_remoteFwd = pose.fwd;
                    }
                    me._madeDeckIndex = -1;
                    s_remoteRequest = true;
                    try
                    {
                        me.Run((Op)msg.Op, msg.A, msg.B);
                    }
                    finally
                    {
                        s_hasRemotePose = false;
                        s_remoteRequest = false;
                    }
                    text = me._status;
                    madeDeck = me._madeDeckIndex;
                    string who = PeerName?.Invoke(conn);
                    CoopPlugin.Log.LogInfo($"CheatMenu: {(string.IsNullOrEmpty(who) ? "a guest" : who)} asked for {(Op)msg.Op} {msg.A} {msg.B} -> {text}");
                }
            }
            catch (Exception e)
            {
                text = "failed: " + e.Message;
                CoopPlugin.Log.LogWarning("CheatMenu request: " + e);
            }
            SendToClient?.Invoke(conn, new CheatResultMessage { Text = text ?? "", SelectDeck = madeDeck });
        }

        public static void ClientApplyResult(CheatResultMessage msg)
        {
            s_instance?.Say("host: " + (msg.Text ?? ""));
            if (msg.SelectDeck >= 0)
                Sync.DeckSync.PendingSelect = msg.SelectDeck; // lands with the next deck mirror
        }

        private void Run(Op op, int a, int b)
        {
            switch (op)
            {
                case Op.AddMoney:
                    AddMoney(a);
                    break;
                case Op.LevelAdd:
                    SetLevel(CPlayerData.m_ShopLevel + a);
                    break;
                case Op.LevelSet:
                    SetLevel(a);
                    break;
                case Op.Licenses:
                    UnlockLicenses();
                    break;
                case Op.Rooms:
                    UnlockRooms(a, warehouse: b == 1);
                    break;
                case Op.Warehouse:
                    UnlockWarehouse();
                    break;
                case Op.Deco:
                    UnlockDeco(a);
                    break;
                case Op.TableFees:
                    SetTableFees(a == 0 ? 0f : -1f);
                    break;
                case Op.FreeTables:
                    Say($"{Sync.GuestBattle.HostFreeAllTables()} table(s) stood down");
                    break;
                case Op.Tutorial:
                    FinishTutorial();
                    break;
                case Op.GiveSet:
                    GiveSet((ECardExpansionType)a, Math.Max(1, b));
                    break;
                case Op.GiveBase:
                    GiveBase((ECardExpansionType)a, Math.Max(1, b));
                    break;
                case Op.StarterDeck:
                    GiveStarterDeck();
                    break;
                case Op.Deliver:
                    DeliverIndex(a, Math.Max(1, b));
                    break;
                case Op.Furniture:
                    SpawnFurniture((EObjectType)a);
                    break;
                case Op.Difficulty:
                    Util.Companions.Difficulty.SetProfile(a);
                    Say("difficulty: " + Util.Companions.Difficulty.Describe());
                    break;
                case Op.Economy:
                    Util.Companions.Economy.SetProfile(a);
                    Say("economy: " + Util.Companions.Economy.Describe());
                    break;
                case Op.DuplicateDeck:
                    DuplicateDeck(a);
                    break;
                default:
                    Say("unknown cheat " + op);
                    break;
            }
        }

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

        /// <summary>Same test the co-op window uses (CGameManager.m_IsGameLevel): the shop scene
        /// is up. InteractionPlayerController.m_Instance is NOT reliable here - it stayed null on a
        /// loaded save (2026-09-16, "Load a save first" while standing in the shop).</summary>
        private static bool InGame()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel;
        }

        private static InteractionPlayerController Player()
        {
            var ipc = InteractionPlayerController.m_Instance;
            if (ipc == null)
                ipc = CSingleton<InteractionPlayerController>.Instance;
            return ipc;
        }

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
                var ipc = Player();
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
                GUILayout.Label("Guest: every button here is a REQUEST to the host, who runs it on the real shop. The host can turn these off (Cheats > AllowGuestRequests).");
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
                Do(Op.AddMoney, 10000);
            if (GUILayout.Button("+ $100,000"))
                Do(Op.AddMoney, 100000);
            if (GUILayout.Button("+ $1,000,000"))
                Do(Op.AddMoney, 1000000);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label("Shop level");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1"))
                Do(Op.LevelAdd, 1);
            if (GUILayout.Button("+5"))
                Do(Op.LevelAdd, 5);
            if (GUILayout.Button("Set 10"))
                Do(Op.LevelSet, 10);
            if (GUILayout.Button("Set 20"))
                Do(Op.LevelSet, 20);
            if (GUILayout.Button("Set 40"))
                Do(Op.LevelSet, 40);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            if (GUILayout.Button("Unlock every item license"))
                Do(Op.Licenses);
            GUILayout.Space(4);
            GUILayout.Label("Shop expansion (mirrored to guests by the shop-state sync)");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Next shop room"))
                Do(Op.Rooms, 1, 0);
            if (GUILayout.Button("All shop rooms"))
                Do(Op.Rooms, int.MaxValue, 0);
            if (GUILayout.Button("Unlock warehouse"))
                Do(Op.Warehouse);
            if (GUILayout.Button("All warehouse rooms"))
                Do(Op.Rooms, int.MaxValue, 1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Own every wallpaper"))
                Do(Op.Deco, 0);
            if (GUILayout.Button("Own every floor"))
                Do(Op.Deco, 1);
            if (GUILayout.Button("Own every ceiling"))
                Do(Op.Deco, 2);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            GUILayout.Label("Difficulty (TcgDifficulty plugin): " + Util.Companions.Difficulty.Describe());
            if (Util.Companions.Difficulty.Present)
            {
                GUILayout.BeginHorizontal();
                var dp = Util.Companions.Difficulty.Profiles;
                for (int i = 0; i < dp.Length; i++)
                    if (GUILayout.Button(dp[i]))
                        Do(Op.Difficulty, i);
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("Economy (TcgEconomy plugin): " + Util.Companions.Economy.Describe());
            if (Util.Companions.Economy.Present)
            {
                GUILayout.BeginHorizontal();
                var ep = Util.Companions.Economy.Profiles;
                for (int i = 0; i < ep.Length; i++)
                    if (GUILayout.Button(ep[i]))
                        Do(Op.Economy, i);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(4);
            GUILayout.Label("Play table fee (a new shop has no review rating, so customers read a market fee as 10x market and refuse to sit)");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Fee = $0 (everyone plays)"))
                Do(Op.TableFees, 0);
            if (GUILayout.Button("Fee = market"))
                Do(Op.TableFees, 1);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            if (GUILayout.Button("Free all play tables (evict, forget guest seats)"))
                Do(Op.FreeTables);
            if (GUILayout.Button("Finish the tutorial"))
                Do(Op.Tutorial);
        }

        private void FinishTutorial()
        {
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

        private void UnlockLicenses()
        {
            int n = 0;
            var lic = CPlayerData.m_IsItemLicenseUnlocked;
            // the save list is padded well past the restock catalog; stay inside the
            // catalog so the co-op license forward has a real product to name
            int count = lic != null ? lic.Count : 0;
            try
            {
                count = Math.Min(count, CSingleton<InventoryBase>.Instance.m_StockItemData_SO.m_RestockDataList.Count);
            }
            catch { }
            for (int i = 0; i < count; i++)
                if (!lic[i])
                {
                    CPlayerData.SetUnlockItemLicense(i);
                    n++;
                }
            Say($"unlocked {n} licenses (reopen the phone shop to see them)");
        }

        private void UnlockWarehouse()
        {
            var urm = RoomManager();
            if (urm == null)
                Say("no UnlockRoomManager in the scene");
            else if (CPlayerData.m_IsWarehouseRoomUnlocked)
                Say("warehouse already unlocked");
            else
            {
                urm.SetUnlockWarehouseRoom(true);
                Say("warehouse unlocked");
            }
        }

        /// <summary>Every game-event format's fee (CPlayerData.m_SetGameEventPriceList, the same
        /// list the play table's own fee screen writes). -1 = back to the market price. The
        /// list rides MarketSync, so the guest sees the same fee.</summary>
        private void SetTableFees(float fee)
        {
            int n = 0;
            foreach (EGameEventFormat f in Enum.GetValues(typeof(EGameEventFormat)))
            {
                string name = f.ToString();
                if (name == "None" || name == "MAX")
                    continue;
                try
                {
                    float v = fee < 0f ? PriceChangeManager.GetGameEventMarketPrice(f) : fee;
                    PriceChangeManager.SetGameEventPrice(f, v);
                    n++;
                }
                catch { }
            }
            Say(fee < 0f ? $"{n} table fees reset to market" : $"{n} table fees set to {GameInstance.GetPriceString(fee)}");
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
            GUILayout.Label("Add cards to the collection (CPlayerData.AddCard, so the guest gets them too). Go easy: the deck editor and binder build UI from the whole collection - 5x of everything is ~34,000 cards and stalls them.");
            foreach (var exp in Expansions)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(exp.ToString(), GUILayout.Width(110));
                if (GUILayout.Button("full set x1"))
                    Do(Op.GiveSet, (int)exp, 1);
                if (GUILayout.Button("base cards x1"))
                    Do(Op.GiveBase, (int)exp, 1);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);
            GUILayout.Label("Starter deck: 50 different Tetramon base cards, added to the collection and saved as a new deck, then selected.");
            if (GUILayout.Button("Give and select a 50-card starter deck"))
                Do(Op.StarterDeck);
            GUILayout.Space(4);
            GUILayout.Label("Duplicate a deck (its cards are added to the collection so the copy is real). On a guest the copy is selected for you.");
            var dl = CPlayerData.m_DeckCompactCardDataList;
            for (int i = 0; dl != null && i < dl.Count && i < 12; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label((dl[i] != null ? dl[i].deckName : "?") + (i == CPlayerData.m_CurrentSelectedDeckIndex ? "  (selected)" : ""), GUILayout.Width(260));
                if (GUILayout.Button("duplicate", GUILayout.Width(90)))
                    Do(Op.DuplicateDeck, i);
                GUILayout.EndHorizontal();
            }
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

        /// <summary>Only the base-border, non-foil card of each monster (one per monster).</summary>
        private void GiveBase(ECardExpansionType exp, int amount)
        {
            var monsters = InventoryBase.GetShownMonsterList(exp);
            int per = CPlayerData.GetCardAmountPerMonsterType(exp);
            if (monsters == null || per <= 0)
            {
                Say("no card list for " + exp);
                return;
            }
            int n = 0;
            for (int m = 0; m < monsters.Count; m++)
            {
                var cd = CPlayerData.GetCardData(m * per, exp, isDestiny: false);
                if (cd == null)
                    continue;
                CPlayerData.AddCard(cd, amount);
                n++;
            }
            Say($"{exp}: {n} base cards x{amount} added");
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
            _madeDeckIndex = CPlayerData.m_DeckCompactCardDataList.Count - 1;
            if (!s_remoteRequest)
                CPlayerData.m_CurrentSelectedDeckIndex = _madeDeckIndex; // a guest's request selects it for THEM, not the host
            Say($"deck '{deck.deckName}' ({added} cards) created" + (s_remoteRequest ? " for the guest" : " and selected"));
        }

        /// <summary>Copy a deck as a new one, adding its cards to the collection first (a deck
        /// holds cards taken OUT of the collection, so a copy needs its own).</summary>
        private void DuplicateDeck(int index)
        {
            var decks = CPlayerData.m_DeckCompactCardDataList;
            if (decks == null || decks.Count == 0)
            {
                Say("no decks to copy");
                return;
            }
            if (index < 0)
                index = CPlayerData.m_CurrentSelectedDeckIndex;
            if (index < 0 || index >= decks.Count || decks[index] == null)
            {
                Say("no such deck");
                return;
            }
            var src = decks[index];
            var copy = new DeckCompactCardDataList
            {
                deckName = (src.deckName ?? "Deck") + " copy",
                deckBoxIndex = src.deckBoxIndex,
                playmatIndex = src.playmatIndex,
            };
            int added = 0;
            var cards = src.compactCardDataAmountList;
            for (int i = 0; cards != null && i < cards.Count; i++)
            {
                var c = cards[i];
                if (c == null)
                    continue;
                var cd = CPlayerData.GetCardData(c.cardSaveIndex, c.expansionType, c.isDestiny);
                if (cd != null && c.amount > 0)
                {
                    CPlayerData.AddCard(cd, c.amount);
                    added += c.amount;
                }
                copy.compactCardDataAmountList.Add(new CompactCardDataAmount
                {
                    cardSaveIndex = c.cardSaveIndex,
                    expansionType = c.expansionType,
                    amount = c.amount,
                    gradedCardIndex = c.gradedCardIndex,
                    isDestiny = c.isDestiny,
                });
            }
            decks.Add(copy);
            _madeDeckIndex = decks.Count - 1;
            if (!s_remoteRequest)
                CPlayerData.m_CurrentSelectedDeckIndex = _madeDeckIndex;
            Say($"deck '{copy.deckName}' ({added} cards) created" + (s_remoteRequest ? " for the guest" : " and selected"));
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
                    Do(Op.Deliver, i, 1);
                if (GUILayout.Button("+5", GUILayout.Width(40)))
                    Do(Op.Deliver, i, 5);
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

        private void DeliverIndex(int restockIndex, int count)
        {
            var inv = CSingleton<InventoryBase>.Instance;
            var list = inv != null && inv.m_StockItemData_SO != null ? inv.m_StockItemData_SO.m_RestockDataList : null;
            if (list == null || restockIndex < 0 || restockIndex >= list.Count || list[restockIndex] == null)
            {
                Say("no such restock item");
                return;
            }
            RestockManager.SpawnPackageBoxItemMultipleFrame(restockIndex, count);
            Say($"delivering {count} x {ItemName(list[restockIndex])}");
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
                    Do(Op.Furniture, (int)s_objValues[i]);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void SpawnFurniture(EObjectType type)
        {
            Vector3 origin, fwd;
            if (s_hasRemotePose)
            {
                origin = s_remotePos;
                fwd = s_remoteFwd;
            }
            else
            {
                var ipc = Player();
                var t = ipc != null && ipc.m_PlayerCollider != null ? ipc.m_PlayerCollider.transform : null;
                if (t == null)
                {
                    Say("no player");
                    return;
                }
                origin = t.position;
                fwd = t.forward;
            }
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f)
                fwd = Vector3.forward;
            fwd.Normalize();
            var pos = origin + fwd * 1.5f + Vector3.up * 0.5f;
            ShelfManager.SpawnInteractableObjectInPackageBox(type, pos, Quaternion.LookRotation(-fwd, Vector3.up));
            Say($"boxed {type} dropped in front of {(s_hasRemotePose ? "the guest" : "you")}");
        }

        private static UnlockRoomManager s_rooms;

        /// <summary>Never <c>CSingleton&lt;UnlockRoomManager&gt;.Instance</c>: it mints a bare one
        /// when called early and the real one's blockers never move.</summary>
        private static UnlockRoomManager RoomManager()
        {
            if (s_rooms == null)
                s_rooms = UnityEngine.Object.FindObjectOfType<UnlockRoomManager>();
            return s_rooms;
        }

        private void UnlockRooms(int max, bool warehouse)
        {
            var urm = RoomManager();
            if (urm == null)
            {
                Say("no UnlockRoomManager in the scene");
                return;
            }
            if (warehouse && !CPlayerData.m_IsWarehouseRoomUnlocked)
                urm.SetUnlockWarehouseRoom(true);
            int n = 0;
            try
            {
                for (int guard = 0; guard < 64 && n < max; guard++)
                {
                    int before = warehouse ? CPlayerData.m_UnlockWarehouseRoomCount : CPlayerData.m_UnlockRoomCount;
                    if (warehouse)
                        urm.StartUnlockNextWarehouseRoom();
                    else
                        urm.StartUnlockNextRoom();
                    int after = warehouse ? CPlayerData.m_UnlockWarehouseRoomCount : CPlayerData.m_UnlockRoomCount;
                    if (after == before)
                        break; // every blocker is down
                    n++;
                }
            }
            catch (Exception e) { Say("room unlock failed: " + e.Message); return; }
            string kind = warehouse ? "warehouse" : "shop";
            int now = warehouse ? CPlayerData.m_UnlockWarehouseRoomCount : CPlayerData.m_UnlockRoomCount;
            Say(n == 0 ? $"every {kind} room is already open" : $"opened {n} {kind} room(s) - now {now}");
        }

        private void UnlockDeco(int category)
        {
            var list = category == 0 ? CPlayerData.m_UnlockedDecoWallList
                     : category == 1 ? CPlayerData.m_UnlockedDecoFloorList
                     : CPlayerData.m_UnlockedDecoCeilingList;
            int n = 0;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                if (list[i])
                    continue;
                if (category == 0)
                    CPlayerData.SetUnlockDecoWall(i, true);
                else if (category == 1)
                    CPlayerData.SetUnlockDecoFloor(i, true);
                else
                    CPlayerData.SetUnlockDecoCeiling(i, true);
                n++;
            }
            string kind = category == 0 ? "wallpaper" : category == 1 ? "floor" : "ceiling";
            Say($"owned {n} new {kind} option(s) (reopen the deco shop to see them)");
        }

        private void Say(string s)
        {
            _status = s;
            _statusAt = Time.unscaledTime;
            CoopPlugin.Log.LogInfo("CheatMenu: " + s);
        }
    }
}
