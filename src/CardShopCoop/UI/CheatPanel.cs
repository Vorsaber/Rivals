using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// The cheat menu's buttons, drawn on either surface: the F4 window (default IMGUI skin,
    /// draggable, as it always was) or the CHEATS phone tile (CoopTheme, inside the phone app
    /// frame). One body, two skins; every button goes through <see cref="CheatMenu.Request"/>,
    /// so a guest's press still travels to the host as a CheatRequest and the host's answer
    /// still comes back as the status line. The ops themselves live in CheatMenu.
    /// </summary>
    internal static class CheatPanel
    {
        private static int s_tab;
        private static string s_filter = "";
        private static Vector2 s_scroll;
        private static readonly string[] Tabs = { "Shop", "Cards", "Boxes", "Furniture" };

        // slider scratch (drawn every frame; applied on the button)
        private static float s_slCap = -1f, s_slRate = -1f;

        private static readonly ECardExpansionType[] Expansions =
        {
            ECardExpansionType.Tetramon, ECardExpansionType.Destiny, ECardExpansionType.Ghost,
            ECardExpansionType.Megabot, ECardExpansionType.FantasyRPG, ECardExpansionType.CatJob,
            ECardExpansionType.Ascension,
        };

        private static string[] s_objNames;
        private static EObjectType[] s_objValues;

        // the skin in force for this frame: GUI.skin.* on F4, CoopTheme.* on the phone
        private static GUIStyle Btn, Lbl, LblDim, Field;
        private static bool s_themed;

        // ================================================================ phone entry

        /// <summary>The CHEATS app: the same guards the F4 key applies (cheats on, not visiting,
        /// a save loaded), then the body in the phone's theme.</summary>
        public static void Draw(float width)
        {
            if (CoopPlugin.CheatsEnabled == null || !CoopPlugin.CheatsEnabled.Value)
            {
                GUILayout.Label("Cheats are turned off in this shop's config (Cheats > Enabled).", CoopTheme.LabelDim);
                return;
            }
            if (CoopCore.IsVisiting)
            {
                GUILayout.Label("No cheats in a rival's shop. They work at home (and for the shop you are a teammate of).", CoopTheme.LabelDim);
                return;
            }
            if (!CheatMenu.InGame())
            {
                GUILayout.Label("Load a save first.", CoopTheme.LabelDim);
                return;
            }
            string key = CoopPlugin.CheatMenuKey != null ? CoopPlugin.CheatMenuKey.Value.ToString() : "F4";
            GUILayout.Label("Test-rig cheats - the same buttons as the " + key + " window. Everything runs on the host through the game's own path and reaches guests through the usual syncs.", CoopTheme.LabelDim);
            DrawBody(true);
        }

        // ================================================================ shared body

        /// <summary>Guest note, tabs, the selected tab, the status line. The caller has already
        /// checked that a save is loaded.</summary>
        internal static void DrawBody(bool themed)
        {
            s_themed = themed;
            if (themed)
            {
                Btn = CoopTheme.ButtonSecondary;
                Lbl = CoopTheme.LabelWrap;
                LblDim = CoopTheme.LabelDimWrap;
                Field = CoopTheme.TextField;
            }
            else
            {
                Btn = GUI.skin.button;
                Lbl = GUI.skin.label;
                LblDim = GUI.skin.label;
                Field = GUI.skin.textField;
            }
            // --- fv-875 guest-cheats-toggle begin
            // Host / solo: the switch that used to live only in the F1 config. Guest: the host's
            // answer, and the buttons grey out while it is off (the host refuses them anyway).
            bool guestLocked = false;
            if (CoopCore.Role == CoopRole.Client)
            {
                bool? allowed = CheatMenu.GuestCheatsAllowed;
                GUILayout.Label("Guest: every button here is a REQUEST to the host, who runs it on the real shop.", LblDim);
                if (allowed == null)
                    GUILayout.Label("Cheats for guests: asking the host...", LblDim);
                else if (allowed.Value)
                    GUILayout.Label("Cheats for guests: ON", themed ? CoopTheme.LabelBold : GUI.skin.label);
                else
                {
                    GUILayout.Label("Cheats for guests: OFF - the host has turned guest requests off.", themed ? CoopTheme.LabelBold : GUI.skin.label);
                    guestLocked = true;
                }
            }
            else
            {
                bool? on = CheatMenu.HostGuestCheats;
                if (on != null)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Cheats for guests: " + (on.Value ? "ON" : "OFF"), themed ? CoopTheme.LabelBold : GUI.skin.label, GUILayout.Width(170));
                    if (GUILayout.Button(on.Value ? "Turn OFF" : "Turn ON", themed ? CoopTheme.ButtonPrimary : GUI.skin.button, GUILayout.Width(90)))
                        CheatMenu.SetGuestCheats(!on.Value);
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Label(on.Value
                        ? "Guests' cheat buttons are live: their requests run here, on the real shop."
                        : "Guests' cheat buttons are greyed out and their requests are refused.", LblDim);
                }
            }
            // --- fv-875 guest-cheats-toggle end
            int tab = s_tab;
            if (themed)
            {
                GUILayout.BeginHorizontal();
                for (int i = 0; i < Tabs.Length; i++)
                    if (GUILayout.Button(Tabs[i].ToUpperInvariant(), i == s_tab ? CoopTheme.TabSelected : CoopTheme.Tab, GUILayout.Width(96f)))
                        tab = i;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else
                tab = GUILayout.Toolbar(s_tab, Tabs);
            if (tab != s_tab)
            {
                s_tab = tab;
                s_scroll = Vector2.zero;
            }
            GUILayout.Space(6);
            bool wasEnabled = GUI.enabled;   // fv-875: guest with the switch off -> everything below is greyed out
            if (guestLocked)
                GUI.enabled = false;
            try
            {
                switch (s_tab)
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
                if (Event.current.type != EventType.Layout)
                {
                    CheatMenu.Note("error: " + e.Message);
                    CoopPlugin.Log.LogWarning("CheatMenu: " + e);
                }
            }
            finally
            {
                GUI.enabled = wasEnabled;   // fv-875
            }
            string status = CheatMenu.Status;
            if (!string.IsNullOrEmpty(status))
            {
                GUILayout.Space(4);
                GUILayout.Label(status, themed ? CoopTheme.LabelBold : GUI.skin.label);
            }
        }

        private static void Do(CheatMenu.Op op, int a = 0, int b = 0)
        {
            CheatMenu.Request(op, a, b);
        }

        private static bool Button(string text)
        {
            return GUILayout.Button(text, Btn);
        }

        private static bool Button(string text, float width)
        {
            return GUILayout.Button(text, Btn, GUILayout.Width(width));
        }

        private static void Section(string title)
        {
            if (s_themed)
                GUILayout.Label(title.ToUpperInvariant(), CoopTheme.SectionHeader);
            else
                GUILayout.Label(title);
        }

        // ------------------------------------------------------------------ Shop

        private static void DrawShop()
        {
            GUILayout.Label($"Money: {GameInstance.GetPriceString(CPlayerData.m_CoinAmountDouble)}    Level: {CPlayerData.m_ShopLevel}    Tutorial: {(CPlayerData.m_HasFinishedTutorial ? "done" : "step " + CPlayerData.m_TutorialIndex)}", Lbl);
            GUILayout.Space(4);
            Section("Money");
            GUILayout.BeginHorizontal();
            if (Button("+ $10,000"))
                Do(CheatMenu.Op.AddMoney, 10000);
            if (Button("+ $100,000"))
                Do(CheatMenu.Op.AddMoney, 100000);
            if (Button("+ $1,000,000"))
                Do(CheatMenu.Op.AddMoney, 1000000);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            Section("Shop level");
            GUILayout.BeginHorizontal();
            if (Button("+1"))
                Do(CheatMenu.Op.LevelAdd, 1);
            if (Button("+5"))
                Do(CheatMenu.Op.LevelAdd, 5);
            if (Button("Set 10"))
                Do(CheatMenu.Op.LevelSet, 10);
            if (Button("Set 20"))
                Do(CheatMenu.Op.LevelSet, 20);
            if (Button("Set 40"))
                Do(CheatMenu.Op.LevelSet, 40);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            if (Button("Unlock every item license"))
                Do(CheatMenu.Op.Licenses);
            GUILayout.Space(4);
            Section("Shop expansion");
            GUILayout.Label("Mirrored to guests by the shop-state sync.", LblDim);
            GUILayout.BeginHorizontal();
            if (Button("Next shop room"))
                Do(CheatMenu.Op.Rooms, 1, 0);
            if (Button("All shop rooms"))
                Do(CheatMenu.Op.Rooms, int.MaxValue, 0);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (Button("Unlock warehouse"))
                Do(CheatMenu.Op.Warehouse);
            if (Button("All warehouse rooms"))
                Do(CheatMenu.Op.Rooms, int.MaxValue, 1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (Button("Own every wallpaper"))
                Do(CheatMenu.Op.Deco, 0);
            if (Button("Own every floor"))
                Do(CheatMenu.Op.Deco, 1);
            if (Button("Own every ceiling"))
                Do(CheatMenu.Op.Deco, 2);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            // fv-828: the Economy / Difficulty profile buttons and the TcgDifficulty sliders left
            // this menu; the TUNING phone app is their one home. What stays is CardShopCoop's own
            // population override (customer cap / arrivals), which TUNING does not carry.
            Section("Live tuning");
            GUILayout.Label("Host config; applies without a relaunch. Economy and difficulty profiles + their knobs: TUNING phone app.", LblDim);
            if (s_slCap < 0f)
            {
                s_slCap = CoopPlugin.MaxCustomers != null ? CoopPlugin.MaxCustomers.Value : 0;
                s_slRate = CoopPlugin.SpawnRateMultiplier != null ? CoopPlugin.SpawnRateMultiplier.Value : 1f;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label($"customer cap {(s_slCap < 0.5f ? "auto" : Mathf.RoundToInt(s_slCap).ToString())}", Lbl, GUILayout.Width(150));
            s_slCap = GUILayout.HorizontalSlider(s_slCap, 0f, 100f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"arrivals x{s_slRate:0.00}", Lbl, GUILayout.Width(150));
            s_slRate = GUILayout.HorizontalSlider(s_slRate, 0.25f, 4f);
            if (Button("Apply", 60))
                Do(CheatMenu.Op.Population, Mathf.RoundToInt(s_slCap), Mathf.RoundToInt(s_slRate * 100f));
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            Section("Play table fee");
            GUILayout.Label("A new shop has no review rating, so customers read a market fee as 10x market and refuse to sit.", LblDim);
            GUILayout.BeginHorizontal();
            if (Button("Fee = $0 (everyone plays)"))
                Do(CheatMenu.Op.TableFees, 0);
            if (Button("Fee = market"))
                Do(CheatMenu.Op.TableFees, 1);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            if (Button("Free all play tables (evict, forget guest seats)"))
                Do(CheatMenu.Op.FreeTables);
            if (Button("Finish the tutorial"))
                Do(CheatMenu.Op.Tutorial);
        }

        // ------------------------------------------------------------------ Cards

        private static void DrawCards()
        {
            GUILayout.Label("Add cards to the collection (CPlayerData.AddCard, so the guest gets them too). Go easy: the deck editor and binder build UI from the whole collection - 5x of everything is ~34,000 cards and stalls them.", LblDim);
            foreach (var exp in Expansions)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(exp.ToString(), Lbl, GUILayout.Width(110));
                if (Button("full set x1"))
                    Do(CheatMenu.Op.GiveSet, (int)exp, 1);
                if (Button("base cards x1"))
                    Do(CheatMenu.Op.GiveBase, (int)exp, 1);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);
            GUILayout.Label("Starter deck: 50 different Tetramon base cards, added to the collection and saved as a new deck, then selected.", LblDim);
            if (Button("Give and select a 50-card starter deck"))
                Do(CheatMenu.Op.StarterDeck);
            GUILayout.Space(4);
            GUILayout.Label("Duplicate a deck (its cards are added to the collection so the copy is real). On a guest the copy is selected for you.", LblDim);
            var dl = CPlayerData.m_DeckCompactCardDataList;
            for (int i = 0; dl != null && i < dl.Count && i < 12; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label((dl[i] != null ? dl[i].deckName : "?") + (i == CPlayerData.m_CurrentSelectedDeckIndex ? "  (selected)" : ""), Lbl, GUILayout.Width(260));
                if (Button("duplicate", 90))
                    Do(CheatMenu.Op.DuplicateDeck, i);
                GUILayout.EndHorizontal();
            }
        }

        // ------------------------------------------------------------------ Boxes

        private static void DrawBoxes()
        {
            GUILayout.Label("Deliver a box through the game's own restock spawner (RestockManager.SpawnPackageBoxItemMultipleFrame). Same as a phone order landing.", LblDim);
            GUILayout.BeginHorizontal();
            GUILayout.Label("filter", Lbl, GUILayout.Width(40));
            s_filter = GUILayout.TextField(s_filter, Field);
            GUILayout.EndHorizontal();
            var inv = CSingleton<InventoryBase>.Instance;
            var list = inv != null && inv.m_StockItemData_SO != null ? inv.m_StockItemData_SO.m_RestockDataList : null;
            if (list == null)
            {
                GUILayout.Label("restock catalog not loaded", LblDim);
                return;
            }
            s_scroll = GUILayout.BeginScrollView(s_scroll, GUILayout.Height(400));
            string f = (s_filter ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < list.Count; i++)
            {
                var rd = list[i];
                if (rd == null)
                    continue;
                string name = CheatMenu.ItemName(rd);
                if (f.Length > 0 && name.ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) < 0)
                    continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(name, Lbl, GUILayout.Width(280));
                if (Button("+1", 40))
                    Do(CheatMenu.Op.Deliver, i, 1);
                if (Button("+5", 40))
                    Do(CheatMenu.Op.Deliver, i, 5);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ Furniture

        private static void DrawFurniture()
        {
            GUILayout.Label("Drop a boxed piece of furniture in front of you (ShelfManager.SpawnInteractableObjectInPackageBox - the delivery recipe). Open the box to place it.", LblDim);
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
            GUILayout.Label("filter", Lbl, GUILayout.Width(40));
            s_filter = GUILayout.TextField(s_filter, Field);
            GUILayout.EndHorizontal();
            s_scroll = GUILayout.BeginScrollView(s_scroll, GUILayout.Height(400));
            string f = (s_filter ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < s_objNames.Length; i++)
            {
                if (f.Length > 0 && s_objNames[i].ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) < 0)
                    continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(s_objNames[i], Lbl, GUILayout.Width(300));
                if (Button("spawn", 60))
                    Do(CheatMenu.Op.Furniture, (int)s_objValues[i]);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }
    }
}
