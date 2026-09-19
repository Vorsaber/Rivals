using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>The trade window's face, drawn by the F2 RIVALS tab and by the phone app.</summary>
    internal static class TradePanel
    {
        private static string s_money, s_filter = "";
        private static bool s_picker;

        /// <summary>The TRADE box: a visitor and the shop swap money and cards. Both edit their
        /// offer, both confirm, the host executes (the visitor's side lives in the bag).</summary>
        public static void Draw(CoopCore core, float width)
        {
            bool host = CoopCore.Role == CoopRole.Host;
            bool visitor = CoopCore.Role == CoopRole.Client && CoopCore.IsVisiting;
            var gm = CSingleton<CGameManager>.Instance;
            bool inGame = gm != null && gm.m_IsGameLevel;
            if (!inGame || (!host && !visitor))
                return;
            var visitors = host ? core.Visitors() : null;
            if (host && visitors.Count == 0 && !Sync.Rivals.TradeSync.Open)
                return;
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("TRADE", CoopTheme.SectionHeader);
            if (!string.IsNullOrEmpty(Sync.Rivals.TradeSync.Status))
                GUILayout.Label(Sync.Rivals.TradeSync.Status, CoopTheme.LabelDim);
            if (!Sync.Rivals.TradeSync.Open)
            {
                s_money = null;
                s_picker = false;
                if (host)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("trade with", CoopTheme.Label, GUILayout.Width(80f));
                    foreach (var v in visitors)
                        if (GUILayout.Button(v.Value, CoopTheme.ButtonSecondary))
                            Sync.Rivals.TradeSync.HostOpen(v.Key, v.Value);
                    GUILayout.EndHorizontal();
                }
                else if (GUILayout.Button("Trade with the shop", CoopTheme.ButtonSecondary))
                    Sync.Rivals.TradeSync.GuestOpen();
                GUILayout.EndVertical();
                return;
            }

            var mine = Sync.Rivals.TradeSync.Mine;
            var theirs = Sync.Rivals.TradeSync.Theirs;
            GUILayout.BeginHorizontal();
            // my side
            GUILayout.BeginVertical(GUILayout.Width(width * 0.48f));
            GUILayout.Label("<b>YOU OFFER</b>" + (mine.Confirmed ? "  <color=#7CFC00>confirmed</color>" : ""), CoopTheme.Label);
            GUILayout.BeginHorizontal();
            GUILayout.Label("money", CoopTheme.LabelDim, GUILayout.Width(50f));
            if (s_money == null)
                s_money = mine.Money.ToString("0.##");
            GUI.SetNextControlName("coop_trade_money");
            s_money = GUILayout.TextField(s_money, 10, GUILayout.Width(80f));
            if (GUILayout.Button("Set", CoopTheme.ButtonSecondary, GUILayout.Width(44f)))
            {
                if (double.TryParse(s_money, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m))
                    Sync.Rivals.TradeSync.SetMoney(m);
                s_money = Sync.Rivals.TradeSync.Mine.Money.ToString("0.##");
            }
            GUILayout.Label(GameInstance.GetPriceString(mine.Money), CoopTheme.Label);
            GUILayout.EndHorizontal();
            foreach (var c in new List<Net.Messages.TradeCard>(mine.Cards))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{c.Amount} x {Sync.Rivals.TradeSync.Label(c)}", CoopTheme.Label);
                if (GUILayout.Button("-", CoopTheme.ButtonSecondary, GUILayout.Width(26f)))
                    Sync.Rivals.TradeSync.RemoveCard(c.Exp, c.Index, c.Destiny, 1, c.Grade); // fv-908
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button(s_picker ? "close card list" : "+ add a card", CoopTheme.ButtonSecondary))
                s_picker = !s_picker;
            if (s_picker)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("find", CoopTheme.LabelDim, GUILayout.Width(36f));
                GUI.SetNextControlName("coop_trade_filter");
                s_filter = GUILayout.TextField(s_filter ?? "", 30);
                GUILayout.EndHorizontal();
                var found = Sync.Rivals.TradeSync.Search(s_filter, 24);
                if (found.Count == 0)
                    GUILayout.Label(host ? "<size=10>type part of a card name (2+ letters)</size>" : "<size=10>nothing in your bag to offer</size>", CoopTheme.LabelDim);
                foreach (var (card, label, have) in found)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"<size=11>{label}  (have {have})</size>", CoopTheme.LabelDim);
                    if (GUILayout.Button("+", CoopTheme.ButtonSecondary, GUILayout.Width(26f)))
                        Sync.Rivals.TradeSync.AddCard(card.Exp, card.Index, card.Destiny, 1, card.Grade); // fv-908
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndVertical();
            // their side
            GUILayout.BeginVertical();
            GUILayout.Label($"<b>{Sync.Rivals.TradeSync.PartnerName.ToUpperInvariant()} OFFERS</b>" + (theirs.Confirmed ? "  <color=#7CFC00>confirmed</color>" : ""), CoopTheme.Label);
            GUILayout.Label("money " + GameInstance.GetPriceString(theirs.Money), CoopTheme.Label);
            foreach (var c in theirs.Cards)
                GUILayout.Label($"{c.Amount} x {Sync.Rivals.TradeSync.Label(c)}", CoopTheme.Label);
            if (theirs.Cards.Count == 0 && theirs.Money <= 0)
                GUILayout.Label("<size=10>nothing yet</size>", CoopTheme.LabelDim);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(mine.Confirmed ? "Unconfirm" : "CONFIRM trade", mine.Confirmed ? CoopTheme.ButtonSecondary : CoopTheme.ButtonPrimary, GUILayout.Width(150f)))
                Sync.Rivals.TradeSync.Confirm(!mine.Confirmed);
            if (GUILayout.Button("Cancel", CoopTheme.ButtonDanger, GUILayout.Width(80f)))
                Sync.Rivals.TradeSync.Cancel("");
            GUILayout.Label("<size=10>any change to either offer clears both confirmations</size>", CoopTheme.LabelDim);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

    }
}
