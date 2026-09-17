using System;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>The SHOP app: the save holder's own settings - the shop's name (the billboard
    /// rename sometimes never comes up on a league save, Dan 2026-09-16), travel cash, the PvP
    /// ante, and a readout of where the shop stands in its league. Owners only (solo or host);
    /// a guest sees the readout.</summary>
    internal static class ShopPanel
    {
        private static string s_name, s_bag, s_ante;
        private static string s_status = "";

        public static bool Owner => CoopCore.Role != CoopRole.Client;

        /// <summary>Rename the shop: the save's name, the billboard's text and the rename screen's
        /// own fields, so the next time it opens it shows the new name.</summary>
        public static bool SetShopName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 40)
            {
                s_status = "1-40 characters";
                return false;
            }
            if (!Owner)
            {
                s_status = "only the shop's owner can rename it";
                return false;
            }
            try
            {
                CPlayerData.PlayerName = name;
                var r = UnityEngine.Object.FindObjectOfType<ShopRenamer>();
                if (r != null)
                {
                    if (r.m_ShopName != null)
                        r.m_ShopName.text = name;
                    if (r.m_SetName != null)
                        r.m_SetName.text = name;
                    if (r.m_SetNameInputDisplay != null)
                        r.m_SetNameInputDisplay.text = name;
                    if (r.m_SetNameInput != null)
                        r.m_SetNameInput.text = name;
                }
                s_status = "shop renamed to " + name;
                CoopPlugin.Log.LogInfo("Shop renamed to '" + name + "'");
                return true;
            }
            catch (Exception e)
            {
                s_status = "rename failed: " + e.Message;
                CoopPlugin.Log.LogWarning("ShopPanel rename: " + e.Message);
                return false;
            }
        }

        public static void Draw(float width)
        {
            var gm = CSingleton<CGameManager>.Instance;
            if (gm == null || !gm.m_IsGameLevel)
            {
                GUILayout.Label("Load your shop first.", CoopTheme.LabelDim);
                return;
            }
            if (!string.IsNullOrEmpty(s_status))
                GUILayout.Label(s_status, CoopTheme.LabelDim);
            string current = "";
            try
            {
                current = CPlayerData.PlayerName ?? "";
            }
            catch { }

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("SHOP NAME", CoopTheme.SectionHeader);
            GUILayout.Label("Now: <b>" + (string.IsNullOrEmpty(current) ? "(unnamed)" : current) + "</b>", CoopTheme.Label);
            if (Owner)
            {
                GUILayout.BeginHorizontal();
                if (s_name == null)
                    s_name = current;
                GUI.SetNextControlName("coop_shop_name");
                s_name = GUILayout.TextField(s_name ?? "", 40, GUILayout.Width(width * 0.6f));
                if (GUILayout.Button("Rename", CoopTheme.ButtonPrimary, GUILayout.Width(80f)))
                    SetShopName(s_name);
                GUILayout.EndHorizontal();
                GUILayout.Label("<size=10>the name on the billboard, the board and the league</size>", CoopTheme.LabelDim);
            }
            else
                GUILayout.Label("<size=10>the shop's owner names it</size>", CoopTheme.LabelDim);
            GUILayout.EndVertical();

            if (Owner)
            {
                GUILayout.BeginVertical(CoopTheme.SectionBox);
                GUILayout.Label("RIVALS SETTINGS", CoopTheme.SectionHeader);
                if (CoopPlugin.RivalsBagMoney != null)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Cash for a visit", CoopTheme.Label, GUILayout.Width(130f));
                    if (s_bag == null)
                        s_bag = CoopPlugin.RivalsBagMoney.Value.ToString("0.##");
                    GUI.SetNextControlName("coop_shop_bag");
                    s_bag = GUILayout.TextField(s_bag, 10, GUILayout.Width(80f));
                    if (GUILayout.Button("Set", CoopTheme.ButtonSecondary, GUILayout.Width(50f)))
                    {
                        if (float.TryParse(s_bag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
                            CoopPlugin.RivalsBagMoney.Value = Mathf.Clamp(b, 0f, 1000000f);
                        s_bag = CoopPlugin.RivalsBagMoney.Value.ToString("0.##");
                    }
                    GUILayout.Label($"<size=10>leaves the till when you visit; the rest comes home (till {GameInstance.GetPriceString(CPlayerData.m_CoinAmountDouble)})</size>", CoopTheme.LabelDim);
                    GUILayout.EndHorizontal();
                }
                if (CoopPlugin.RivalsPvpAnte != null)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("PvP ante for visitors", CoopTheme.Label, GUILayout.Width(130f));
                    if (s_ante == null)
                        s_ante = CoopPlugin.RivalsPvpAnte.Value.ToString("0.##");
                    GUI.SetNextControlName("coop_shop_ante");
                    s_ante = GUILayout.TextField(s_ante, 8, GUILayout.Width(80f));
                    if (GUILayout.Button("Set", CoopTheme.ButtonSecondary, GUILayout.Width(50f)))
                    {
                        if (float.TryParse(s_ante, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float a))
                            CoopPlugin.RivalsPvpAnte.Value = Mathf.Clamp(a, 0f, 100000f);
                        s_ante = CoopPlugin.RivalsPvpAnte.Value.ToString("0.##");
                    }
                    GUILayout.Label("<size=10>" + (CoopPlugin.RivalsPvpAnte.Value > 0 ? "each side stakes it, winner takes both" : "free matches") + "</size>", CoopTheme.LabelDim);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("LEAGUE", CoopTheme.SectionHeader);
            var R = Sync.Rivals.RivalsLobby.Role;
            if (R == Sync.Rivals.RivalsLobby.LobbyRole.None)
                GUILayout.Label("Not in a league lobby (F2 > RIVALS).", CoopTheme.LabelDim);
            else
            {
                GUILayout.Label(Sync.Rivals.RivalsLobby.Status, CoopTheme.Label);
                if (Sync.Rivals.LeagueSession.Active)
                    GUILayout.Label($"League {Sync.Rivals.LeagueSession.Id} - save slot {Sync.Rivals.LeagueSession.Slot}", CoopTheme.LabelDim);
                if (Sync.Rivals.RivalsLobby.MyPriceRank >= 0)
                    GUILayout.Label($"price rank {Sync.Rivals.RivalsLobby.MyPriceRank + 1} of {Sync.Rivals.RivalsLobby.Board.Shops.Count} -> customers x{Sync.Rivals.RivalsLobby.CrowdMultiplier:0.00}", CoopTheme.Label);
                var board = Sync.Rivals.RivalsLobby.Board;
                var shops = new System.Collections.Generic.List<Net.Messages.RivalsShop>(board.Shops);
                shops.Sort((a, b) => b.ShopValue.CompareTo(a.ShopValue));
                for (int i = 0; i < shops.Count; i++)
                {
                    var s = shops[i];
                    string price = s.PriceRank < 0 ? "no prices" : (s.PriceRank == 0 ? "CHEAPEST" : "price #" + (s.PriceRank + 1)) + $" x{s.AvgMarkup:0.00}";
                    GUILayout.Label($"<size=11>{i + 1}. {s.Name} - lvl {s.Level}, {GameInstance.GetPriceString(s.Money)}, day {s.Day}  |  {price}</size>", CoopTheme.LabelDim);
                }
            }
            GUILayout.EndVertical();
        }
    }
}
