using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CardShopCoop.Sync.Rivals;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// R11 (fv-688): a VISITOR's collection album is the rival shop's collection - that is
    /// intended (the album reads CPlayerData, and CPlayerData is the mirrored world). What the
    /// visitor actually holds is the carry-out bag (<see cref="VisitorBag"/>), which the album
    /// knows nothing about. Two things fix the confusion without touching what the album shows:
    ///
    ///  1. A "YOUR BAG" panel drawn beside the open album (IMGUI, the in-world overlay style of
    ///     ChatOverlay/PurchaseConfirm): every card line in the bag, the open album's expansion
    ///     first, plus the balance and the item count. Rebuilt only when the bag changes, since
    ///     OnGUI runs more than once a frame.
    ///  2. A "+N bag" badge on the album page itself: a postfix on BinderPageGrp.SetCard /
    ///     SetSingleCard appends it to the vanilla "X n" count text of any card the bag also
    ///     holds, so the page and the panel agree. Text only - the card list, sort and page
    ///     maths are the game's.
    ///
    /// Shown only while visiting with an open bag and the book open; at home, or as a plain
    /// co-op guest, nothing of this exists.
    /// </summary>
    public sealed class BagOverlay : MonoBehaviour
    {
        // private binder state (CollectionBinderFlipAnimCtrl) - AccessTools-cached, same as CoopCore.Cards
        private static readonly FieldInfo FiBookOpen = AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_IsBookOpen");
        private static readonly FieldInfo FiExpansion = AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_ExpansionType");
        private static readonly FieldInfo FiGraded = AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_IsGradedCardAlbum");

        private const string BadgeColor = "#47B29E"; // CoopTheme.PrimaryHover

        private sealed class Line
        {
            public ECardExpansionType Expansion;
            public string Text;
        }

        private bool _show;
        private ECardExpansionType _albumExpansion = ECardExpansionType.None;
        private bool _albumGraded;
        private string _signature = "";
        private readonly List<Line> _lines = new List<Line>();
        private int _cardTotal;
        private int _itemTotal;
        private Vector2 _scroll;

        /// <summary>The overlay applies while a visitor with an open bag is in the rival's world.</summary>
        public static bool Applies()
        {
            return CoopCore.IsVisiting && VisitorBag.IsOpen;
        }

        // fv-872: InteractionPlayerController.m_Instance is a dead static - the game declares it
        // and never assigns it (CoopCore.ResolvePlayer / CheatMenu.InGame say the same). Reading
        // it here left Binder() null on every frame, so the panel never drew. Find the controller
        // in the scene instead and cache it; a scene change destroys it (Unity == null) and the
        // next look-up re-finds it. Not CSingleton<>.Instance - that spawns one when none exists.
        private static InteractionPlayerController _ipc;
        private static float _nextIpcLookup;

        private static CollectionBinderFlipAnimCtrl Binder()
        {
            if (_ipc == null && Time.unscaledTime >= _nextIpcLookup)
            {
                _nextIpcLookup = Time.unscaledTime + 0.5f; // a scene scan, not a per-frame one
                _ipc = InteractionPlayerController.m_Instance;
                if (_ipc == null)
                    _ipc = FindObjectOfType<InteractionPlayerController>();
            }
            return _ipc != null ? _ipc.m_CollectionBinderFlipAnimCtrl : null;
        }

        // fv-872: why the panel is (not) drawing, logged once per change of answer - never per
        // frame. "album closed" <-> "drawing" is the normal pair while visiting; anything else
        // names the gate that failed, which is what the silent no-panel bug lacked.
        private string _why = "";

        private void Why(string why)
        {
            if (why == _why)
                return;
            _why = why;
            CoopPlugin.Log.LogInfo("BagOverlay: " + why);
        }

        private void Update()
        {
            try
            {
                _show = false;
                if (!Applies())
                {
                    Why(CoopCore.IsVisiting ? "not drawing - visiting, but the bag is not open" : "not drawing - not visiting");
                    return;
                }
                var gm = CSingleton<CGameManager>.Instance;
                if (gm == null || !gm.m_IsGameLevel)
                {
                    Why("not drawing - not in the shop scene");
                    return;
                }
                var ctrl = Binder();
                if (ctrl == null)
                {
                    Why("not drawing - no player controller / binder in the scene");
                    return;
                }
                if (FiBookOpen == null)
                {
                    Why("not drawing - CollectionBinderFlipAnimCtrl.m_IsBookOpen not found (game update?)");
                    return;
                }
                if (!(bool)FiBookOpen.GetValue(ctrl))
                {
                    Why("not drawing - album closed");
                    return;
                }
                _albumExpansion = FiExpansion != null ? (ECardExpansionType)FiExpansion.GetValue(ctrl) : ECardExpansionType.None;
                _albumGraded = FiGraded != null && (bool)FiGraded.GetValue(ctrl);
                Rebuild();
                // fv-831: the sort / expansion selector is drawn over the album's right side, where the
                // panel sits. Hide the panel while either is up; the page badges are static patches and
                // do not depend on _show, so they are untouched.
                if (SelectorOpen(ctrl))
                {
                    Why("hidden - sort / expansion selector is up");
                    return;
                }
                Why($"drawing - album {(_albumGraded ? "graded" : _albumExpansion.ToString())}, bag {_cardTotal} card(s) / {_itemTotal} item(s)");
                _show = true;
            }
            catch (Exception e)
            {
                _show = false;
                CoopPlugin.Log.LogWarning("BagOverlay: " + e.Message);
            }
        }

        /// <summary>True while the album's sort screen or expansion select screen is showing.</summary>
        private static bool SelectorOpen(CollectionBinderFlipAnimCtrl ctrl)
        {
            var ui = ctrl.m_CollectionBinderUI;
            if (ui == null)
                return false;
            return (ui.m_SortAlbumScreen != null && ui.m_SortAlbumScreen.activeSelf)
                || (ui.m_ExpansionSelectScreen != null && ui.m_ExpansionSelectScreen.activeSelf);
        }

        /// <summary>Rebuild the card lines when the bag (or the open album) changed.</summary>
        private void Rebuild()
        {
            var bag = VisitorBag.Current;
            var sig = new StringBuilder();
            sig.Append(_albumExpansion).Append('|').Append(_albumGraded ? 1 : 0).Append('|');
            sig.Append(bag.Cards.Count).Append('|').Append(bag.Items.Count).Append('|');
            sig.Append(VisitorBag.Balance.ToString("0.00")).Append('|');
            foreach (var c in bag.Cards)
                sig.Append(c.Expansion).Append(':').Append(c.Index).Append(':').Append(c.IsDestiny ? 1 : 0).Append(':').Append(c.Amount).Append(',');
            foreach (var it in bag.Items)
                sig.Append(it.ItemType).Append(':').Append(it.Count).Append(',');
            string s = sig.ToString();
            if (s == _signature)
                return;
            _signature = s;
            _lines.Clear();
            _cardTotal = 0;
            _itemTotal = 0;
            foreach (var c in bag.Cards)
            {
                if (c.Amount <= 0)
                    continue;
                _cardTotal += c.Amount;
                _lines.Add(new Line
                {
                    Expansion = (ECardExpansionType)c.Expansion,
                    Text = $"{Escape(Label(c))}  <color={BadgeColor}>x{c.Amount}</color>",
                });
            }
            // the open album's expansion first, then the rest in bag order (stable sort)
            var here = new List<Line>();
            var elsewhere = new List<Line>();
            foreach (var l in _lines)
                (l.Expansion == _albumExpansion && !_albumGraded ? here : elsewhere).Add(l);
            _lines.Clear();
            _lines.AddRange(here);
            _lines.AddRange(elsewhere);
            foreach (var it in bag.Items)
                if (it.Count > 0)
                    _itemTotal += it.Count;
        }

        private static string Label(VisitorBag.Card c)
        {
            try
            {
                var cd = CPlayerData.GetCardData(c.Index, (ECardExpansionType)c.Expansion, c.IsDestiny);
                return TradeSync.Label(cd);
            }
            catch { return $"card {c.Index}/{c.Expansion}"; }
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("<", "‹").Replace(">", "›");
        }

        private void OnGUI()
        {
            if (!_show)
                return;
            float w = Mathf.Min(360f, Screen.width * 0.28f);
            float lineH = 20f;
            int shown = Mathf.Min(_lines.Count, 14);
            float h = 92f + Mathf.Max(1, shown) * lineH + 12f;
            h = Mathf.Min(h, Screen.height * 0.7f);
            var rect = new Rect(Screen.width - w - 16f, Screen.height * 0.12f, w, h);
            GUI.Box(rect, GUIContent.none);
            GUI.Box(rect, GUIContent.none); // twice: the default box is translucent
            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, rect.height - 16f));
            var title = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16, fontStyle = FontStyle.Bold };
            var detail = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, wordWrap = true };
            var row = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13 };
            string shop = string.IsNullOrEmpty(VisitorBag.Current.VisitingShop) ? "the shop" : Escape(VisitorBag.Current.VisitingShop);
            GUILayout.Label($"<color={BadgeColor}>YOUR BAG</color>  <color=#bbbbbb>{_cardTotal} card(s), {_itemTotal} item(s)</color>", title);
            GUILayout.Label($"<color=#bbbbbb>This album is {shop}'s collection. What you bought or won here is in your bag and goes home with you.</color>", detail);
            GUILayout.Label($"Balance  <color={BadgeColor}>{GameInstance.GetPriceString((float)VisitorBag.Balance)}</color>", row);
            if (_lines.Count == 0)
                GUILayout.Label("<color=#bbbbbb>No cards in the bag yet.</color>", row);
            else
            {
                _scroll = GUILayout.BeginScrollView(_scroll);
                bool sectioned = false;
                for (int i = 0; i < _lines.Count; i++)
                {
                    var l = _lines[i];
                    bool here = l.Expansion == _albumExpansion && !_albumGraded;
                    if (!sectioned && !here && i > 0)
                    {
                        GUILayout.Label("<color=#bbbbbb>other albums</color>", detail);
                        sectioned = true;
                    }
                    GUILayout.Label((here ? "" : "<color=#9a9a9a>") + l.Text + (here ? "" : "</color>"), row, GUILayout.MinHeight(lineH));
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------------------
        // Album page badge: "+N bag" after the vanilla count text of a card the bag also holds.

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                var setCard = AccessTools.Method(typeof(BinderPageGrp), "SetCard");
                if (setCard != null)
                    h.Patch(setCard, postfix: new HarmonyMethod(typeof(BagOverlay), nameof(SetCardPostfix)));
                var setSingle = AccessTools.Method(typeof(BinderPageGrp), "SetSingleCard");
                if (setSingle != null)
                    h.Patch(setSingle, postfix: new HarmonyMethod(typeof(BagOverlay), nameof(SetSingleCardPostfix)));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BagOverlay patches: " + e.Message); }
        }

        /// <summary>Whole page laid out (default sort): badge every visible slot.</summary>
        public static void SetCardPostfix(BinderPageGrp __instance)
        {
            if (!Applies() || __instance == null || __instance.m_CardList == null)
                return;
            try
            {
                for (int i = 0; i < __instance.m_CardList.Count; i++)
                    Badge(__instance.m_CardList[i]);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BagOverlay badge: " + e.Message); }
        }

        /// <summary>One slot laid out (sorted albums, close-up return): badge that slot.</summary>
        public static void SetSingleCardPostfix(BinderPageGrp __instance, int cardIndex, int cardCount)
        {
            if (!Applies() || cardCount <= 0 || __instance == null || __instance.m_CardList == null)
                return;
            try
            {
                if (cardIndex >= 0 && cardIndex < __instance.m_CardList.Count)
                    Badge(__instance.m_CardList[cardIndex]);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BagOverlay badge: " + e.Message); }
        }

        private static void Badge(Card3dUIGroup slot)
        {
            if (slot == null || slot.m_CardUI == null || slot.m_CardCountText == null || !slot.gameObject.activeSelf)
                return;
            var cd = slot.m_CardUI.GetCardData();
            if (cd == null || cd.cardGrade > 0)
                return; // graded rows have no count text
            int inBag = InBag(cd);
            if (inBag <= 0)
                return;
            string text = slot.m_CardCountText.text ?? "";
            if (text.Contains(" bag"))
                return; // already badged (vanilla rewrites the text before each layout, so this is belt and braces)
            slot.m_CardCountText.text = text + $"  <color={BadgeColor}>+{inBag} bag</color>";
        }

        private static int InBag(CardData cd)
        {
            var bag = VisitorBag.Current;
            if (bag == null || bag.Cards.Count == 0)
                return 0;
            int index;
            try
            {
                index = CPlayerData.GetCardSaveIndex(cd);
            }
            catch { return 0; }
            int exp = (int)cd.expansionType;
            int total = 0;
            foreach (var c in bag.Cards)
                if (c.Expansion == exp && c.Index == index && c.IsDestiny == cd.isDestiny)
                    total += c.Amount;
            return total;
        }
    }
}
