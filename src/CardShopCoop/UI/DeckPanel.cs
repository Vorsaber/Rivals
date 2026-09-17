using System;
using System.Collections.Generic;
using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>The deck builder, anywhere: the same deck list the workbench edits, drawn in
    /// IMGUI (phone app or F2). The rules are the game's own - 50 cards, four of a monster,
    /// cards move between the album and the deck, the deck you are entered in a tournament with
    /// is frozen - and in co-op the workbench's editor lock is taken first (DeckSync), so two
    /// players never edit at once and a guest's list streams up to the host as it changes.</summary>
    internal static class DeckPanel
    {
        private static int s_deck = -1;          // deck being edited (-1 = the list)
        private static bool s_locked;            // we hold the editor
        private static string s_filter = "";
        private static string s_rename;
        private static Vector2 s_scroll;
        private static string s_status = "";
        private static bool s_picker;

        public static bool Editing => s_deck >= 0;

        private static List<DeckCompactCardDataList> Decks => CPlayerData.m_DeckCompactCardDataList;

        /// <summary>Closing the app: release the lock, leave the list as it is (edits are live).</summary>
        public static void Close()
        {
            s_deck = -1;
            s_picker = false;
            if (s_locked)
            {
                s_locked = false;
                DeckSync.EndRemoteEdit();
            }
        }

        public static void Draw(float width)
        {
            var decks = Decks;
            var gm = CSingleton<CGameManager>.Instance;
            if (decks == null || gm == null || !gm.m_IsGameLevel)
            {
                GUILayout.Label("Load your shop first.", CoopTheme.LabelDim);
                return;
            }
            if (!string.IsNullOrEmpty(s_status))
                GUILayout.Label(s_status, CoopTheme.LabelDim);
            if (s_deck < 0 || s_deck >= decks.Count)
                DrawList(decks);
            else
                DrawDeck(decks, s_deck, width);
        }

        // ================================================================ the list

        private static void DrawList(List<DeckCompactCardDataList> decks)
        {
            int selected = CPlayerData.m_CurrentSelectedDeckIndex;
            int max = GameInstance.GetMaxDeckCardCount();
            s_scroll = GUILayout.BeginScrollView(s_scroll);
            for (int i = 0; i < decks.Count; i++)
            {
                var d = decks[i];
                if (d == null)
                    continue;
                int count = d.GetTotalCardCount();
                bool active = i == selected;
                GUILayout.BeginHorizontal(i % 2 == 0 ? CoopTheme.RowEven : CoopTheme.RowOdd);
                GUILayout.Label($"{(active ? "<color=#7CFC00>*</color> " : "")}{d.deckName}  <size=10>{count}/{max}{(count < max ? " (incomplete)" : "")}</size>", CoopTheme.Label);
                GUILayout.FlexibleSpace();
                if (!CoopCore.IsVisiting && GUILayout.Button("Edit", CoopTheme.ButtonSecondary, GUILayout.Width(50f)))
                    OpenDeck(i);
                GUI.enabled = !active && count >= max;
                if (GUILayout.Button("Use", CoopTheme.ButtonPrimary, GUILayout.Width(46f)))
                    SetActive(i);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (CoopCore.IsVisiting)
                GUILayout.Label("<size=10>Visiting: these are the shop's decks (read-only). Yours is the one marked \u2708 - pick it with Use.</size>", CoopTheme.LabelDim);
            else if (decks.Count < 30 && GUILayout.Button("+ New deck", CoopTheme.ButtonSecondary))
                NewDeck();
            GUILayout.Label("<size=10>* = the deck you battle with. A deck needs all " + max + " cards before it can be used.</size>", CoopTheme.LabelDim);
        }

        private static bool TournamentFrozen(int index)
        {
            try
            {
                var td = CPlayerData.m_TournamentData;
                var pd = CPlayerData.m_PlayerTournamentData;
                return CPlayerData.m_CurrentSelectedDeckIndex == index && td != null && pd != null
                    && td.m_IsTournamentDay && !td.m_IsTournamentDayOver && pd.m_IsTournamentCustomer
                    && (td.m_TournamentCurrentRound > 0 || pd.m_HasRegisteredTournamentResult);
            }
            catch { return false; }
        }

        private static void OpenDeck(int index)
        {
            if (TournamentFrozen(index))
            {
                s_status = "that deck is in a tournament right now";
                return;
            }
            if (!s_locked)
            {
                bool ok = DeckSync.BeginRemoteEdit(() =>
                {
                    s_locked = true;
                    s_deck = index;
                    s_rename = null;
                    s_status = "";
                });
                if (!ok)
                    s_status = "deck editor busy";
                else if (!s_locked)
                    s_status = "asking the host for the deck editor...";
                return;
            }
            s_deck = index;
            s_rename = null;
            s_status = "";
        }

        private static void SetActive(int index)
        {
            var decks = Decks;
            if (index < 0 || index >= decks.Count)
                return;
            if (TournamentFrozen(CPlayerData.m_CurrentSelectedDeckIndex))
            {
                s_status = "you're in a tournament with your current deck";
                return;
            }
            if (decks[index].GetTotalCardCount() < GameInstance.GetMaxDeckCardCount())
            {
                s_status = "that deck is incomplete";
                return;
            }
            CPlayerData.m_CurrentSelectedDeckIndex = index;
            DeckSync.RememberSelection();
            s_status = "battling with " + decks[index].deckName;
        }

        private static void NewDeck()
        {
            if (!s_locked)
            {
                bool ok = DeckSync.BeginRemoteEdit(() =>
                {
                    s_locked = true;
                    NewDeck();
                });
                if (!ok)
                    s_status = "deck editor busy";
                return;
            }
            var decks = Decks;
            var d = new DeckCompactCardDataList
            {
                compactCardDataAmountList = new List<CompactCardDataAmount>(),
                deckName = "Deck " + (decks.Count + 1),
            };
            decks.Add(d);
            s_deck = decks.Count - 1;
            s_rename = null;
            s_status = "";
        }

        // ================================================================ one deck

        private static void DrawDeck(List<DeckCompactCardDataList> decks, int index, float width)
        {
            var d = decks[index];
            int max = GameInstance.GetMaxDeckCardCount();
            int total = d.GetTotalCardCount();
            GUILayout.BeginHorizontal();
            bool back = GUILayout.Button("< decks", CoopTheme.ButtonSecondary, GUILayout.Width(70f));
            if (back)
            {
                // finish this row's group before leaving: an early return here unbalanced
                // IMGUI's layout stack and blanked the phone (2026-09-16)
                GUILayout.EndHorizontal();
                s_deck = -1;
                s_picker = false;
                return;
            }
            if (s_rename == null)
                s_rename = d.deckName ?? "";
            GUI.SetNextControlName("coop_deck_name");
            s_rename = GUILayout.TextField(s_rename, 24, GUILayout.Width(width * 0.4f));
            if (s_rename != d.deckName && GUILayout.Button("Rename", CoopTheme.ButtonSecondary, GUILayout.Width(64f)))
                d.deckName = s_rename;
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<b>{total}/{max}</b>", CoopTheme.Label);
            GUILayout.EndHorizontal();

            s_scroll = GUILayout.BeginScrollView(s_scroll);
            var lines = d.compactCardDataAmountList;
            for (int i = 0; lines != null && i < lines.Count; i++)
            {
                var c = lines[i];
                if (c == null || c.amount <= 0)
                    continue;
                CardData cd;
                try
                {
                    cd = CPlayerData.GetCardData(c.cardSaveIndex, c.expansionType, c.isDestiny);
                }
                catch { continue; }
                GUILayout.BeginHorizontal(i % 2 == 0 ? CoopTheme.RowEven : CoopTheme.RowOdd);
                GUILayout.Label($"{c.amount} x {Sync.Rivals.TradeSync.Label(cd)}", CoopTheme.Label);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("-", CoopTheme.ButtonSecondary, GUILayout.Width(28f)))
                    Remove(d, i, 1);
                if (GUILayout.Button("+", CoopTheme.ButtonSecondary, GUILayout.Width(28f)))
                    Add(d, cd);
                GUILayout.EndHorizontal();
            }
            if (lines == null || lines.Count == 0)
                GUILayout.Label("<size=10>empty - add cards from your album below</size>", CoopTheme.LabelDim);
            GUILayout.EndScrollView();

            if (GUILayout.Button(s_picker ? "close album" : "+ add from album", CoopTheme.ButtonSecondary))
                s_picker = !s_picker;
            if (s_picker)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("find", CoopTheme.LabelDim, GUILayout.Width(36f));
                GUI.SetNextControlName("coop_deck_filter");
                s_filter = GUILayout.TextField(s_filter ?? "", 30);
                GUILayout.EndHorizontal();
                var found = SearchAlbum(s_filter, 20);
                if (found.Count == 0)
                    GUILayout.Label("<size=10>type part of a card name (2+ letters); only cards in the album show</size>", CoopTheme.LabelDim);
                foreach (var (cd, label, have) in found)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"<size=11>{label}  (album {have})</size>", CoopTheme.LabelDim);
                    if (GUILayout.Button("+", CoopTheme.ButtonSecondary, GUILayout.Width(28f)))
                        Add(d, cd);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Delete deck", CoopTheme.ButtonDanger, GUILayout.Width(100f)))
                Delete(index);
            GUILayout.Label("<size=10>50 cards, at most 4 of any monster; cards move between the album and the deck as you add them</size>", CoopTheme.LabelDim);
            GUILayout.EndHorizontal();
        }

        private static int SameMonsterCount(DeckCompactCardDataList d, EMonsterType monster)
        {
            int n = 0;
            var lines = d.compactCardDataAmountList;
            for (int i = 0; lines != null && i < lines.Count; i++)
            {
                var c = lines[i];
                if (c == null)
                    continue;
                try
                {
                    if (CPlayerData.GetMonsterTypeFromCardSaveIndex(c.cardSaveIndex, c.expansionType) == monster)
                        n += c.amount;
                }
                catch { }
            }
            return n;
        }

        private static void Add(DeckCompactCardDataList d, CardData cd)
        {
            if (cd == null)
                return;
            if (TournamentFrozen(s_deck))
            {
                s_status = "that deck is in a tournament right now";
                return;
            }
            int max = GameInstance.GetMaxDeckCardCount();
            if (d.GetTotalCardCount() >= max)
            {
                s_status = "the deck is full";
                return;
            }
            if (SameMonsterCount(d, cd.monsterType) >= 4)
            {
                s_status = "only four copies of each monster";
                return;
            }
            if (CPlayerData.GetCardAmount(cd) <= 0)
            {
                s_status = "none left in the album";
                return;
            }
            int index = CPlayerData.GetCardSaveIndex(cd);
            var line = d.compactCardDataAmountList.Find(c => c != null && c.cardSaveIndex == index && c.expansionType == cd.expansionType && c.isDestiny == cd.isDestiny && c.gradedCardIndex == 0);
            CPlayerData.ReduceCard(cd, 1);
            if (line != null)
                line.amount++;
            else
                d.compactCardDataAmountList.Add(new CompactCardDataAmount { cardSaveIndex = index, expansionType = cd.expansionType, isDestiny = cd.isDestiny, amount = 1 });
            s_status = "";
        }

        private static void Remove(DeckCompactCardDataList d, int lineIndex, int amount)
        {
            if (TournamentFrozen(s_deck))
            {
                s_status = "that deck is in a tournament right now";
                return;
            }
            var lines = d.compactCardDataAmountList;
            if (lineIndex < 0 || lineIndex >= lines.Count)
                return;
            var c = lines[lineIndex];
            int n = Math.Min(amount, c.amount);
            try
            {
                var cd = CPlayerData.GetCardData(c.cardSaveIndex, c.expansionType, c.isDestiny);
                CPlayerData.AddCard(cd, n);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("DeckPanel remove: " + e.Message); }
            c.amount -= n;
            if (c.amount <= 0)
                lines.RemoveAt(lineIndex);
        }

        private static void Delete(int index)
        {
            var decks = Decks;
            if (index < 0 || index >= decks.Count)
                return;
            if (TournamentFrozen(index))
            {
                s_status = "that deck is in a tournament right now";
                return;
            }
            var d = decks[index];
            try
            {
                foreach (var c in d.compactCardDataAmountList)
                    if (c != null && c.amount > 0)
                        CPlayerData.AddCard(CPlayerData.GetCardData(c.cardSaveIndex, c.expansionType, c.isDestiny), c.amount);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("DeckPanel delete: " + e.Message); }
            decks.RemoveAt(index);
            if (CPlayerData.m_CurrentSelectedDeckIndex > index)
                CPlayerData.m_CurrentSelectedDeckIndex--;
            else if (CPlayerData.m_CurrentSelectedDeckIndex == index)
                CPlayerData.m_CurrentSelectedDeckIndex = 0;
            s_deck = -1;
            s_picker = false;
            s_status = "deck deleted - its cards are back in the album";
        }

        private static readonly (ECardExpansionType, bool)[] Lists =
        {
            (ECardExpansionType.Tetramon, false),
            (ECardExpansionType.Destiny, false),
            (ECardExpansionType.Ghost, true),
            (ECardExpansionType.Ghost, false),
            (ECardExpansionType.Megabot, false),
            (ECardExpansionType.FantasyRPG, false),
            (ECardExpansionType.CatJob, false),
            (ECardExpansionType.Ascension, false),
        };

        private static List<(CardData cd, string label, int have)> SearchAlbum(string filter, int max)
        {
            var result = new List<(CardData, string, int)>();
            filter = (filter ?? "").Trim().ToLowerInvariant();
            if (filter.Length < 2)
                return result;
            try
            {
                foreach (var (exp, destiny) in Lists)
                {
                    List<int> list;
                    try
                    {
                        list = CPlayerData.GetCardCollectedList(exp, destiny);
                    }
                    catch { continue; }
                    for (int i = 0; list != null && i < list.Count && result.Count < max; i++)
                    {
                        if (list[i] <= 0)
                            continue;
                        var cd = CPlayerData.GetCardData(i, exp, destiny);
                        string label = Sync.Rivals.TradeSync.Label(cd);
                        if (label.ToLowerInvariant().Contains(filter))
                            result.Add((cd, label, list[i]));
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("DeckPanel search: " + e.Message); }
            return result;
        }
    }
}
