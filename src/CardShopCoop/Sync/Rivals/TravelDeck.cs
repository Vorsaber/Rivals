using System;
using System.Collections.Generic;
using System.IO;
using CardShopCoop.Net.Messages;
using Newtonsoft.Json;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// The travel deck: the deck you were battling with at home, carried along on a visit.
    /// A visitor's deck list is the rival's (DeckSync mirrors the shop's decks), so without
    /// this you would fight with their decks. Packed when you leave (a snapshot of the selected
    /// deck, persisted so it survives the title screen), injected as an extra LOCAL deck after
    /// every mirror while visiting, and selected the first time. It never goes up to the shop:
    /// visitors cannot edit decks there, and PvP / battles carry the deck by value anyway.
    /// </summary>
    public static class TravelDeck
    {
        public static DeckEntry Current
        {
            get; private set;
        }
        private static bool s_selectedOnce;
        private static bool s_keepSelected;
        private static int s_injectedIndex = -1;

        private static string PathOnDisk() => Path.Combine(BepInEx.Paths.ConfigPath, "CardShopCoop.traveldeck.json");

        public static void Load()
        {
            try
            {
                string p = PathOnDisk();
                if (File.Exists(p))
                    Current = JsonConvert.DeserializeObject<DeckEntry>(File.ReadAllText(p));
                if (Current != null)
                    CoopPlugin.Log.LogInfo($"TravelDeck: carrying '{Current.Name}' ({Count(Current)} cards)");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("TravelDeck load: " + e.Message);
                Current = null;
            }
        }

        private static void Save()
        {
            try
            {
                string p = PathOnDisk();
                if (Current == null)
                {
                    if (File.Exists(p))
                        File.Delete(p);
                }
                else
                    File.WriteAllText(p, JsonConvert.SerializeObject(Current, Formatting.Indented));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TravelDeck save: " + e.Message); }
        }

        private static int Count(DeckEntry e)
        {
            int n = 0;
            if (e?.Cards != null)
                foreach (var c in e.Cards)
                    n += c.Amount;
            return n;
        }

        /// <summary>Leaving home: snapshot the deck we battle with.</summary>
        public static void Pack()
        {
            try
            {
                var decks = CPlayerData.m_DeckCompactCardDataList;
                int sel = CPlayerData.m_CurrentSelectedDeckIndex;
                if (decks == null || sel < 0 || sel >= decks.Count || decks[sel] == null)
                {
                    CoopPlugin.Log.LogInfo("TravelDeck: no selected deck to pack");
                    return;
                }
                var d = decks[sel];
                var e = new DeckEntry { Name = d.deckName ?? "deck", DeckBox = d.deckBoxIndex, Playmat = d.playmatIndex };
                if (d.compactCardDataAmountList != null)
                    foreach (var c in d.compactCardDataAmountList)
                    {
                        if (c == null || c.amount <= 0)
                            continue;
                        e.Cards.Add(new DeckCardEntry { Expansion = c.expansionType, Index = c.cardSaveIndex, Amount = c.amount, GradedIndex = c.gradedCardIndex, IsDestiny = c.isDestiny });
                    }
                Current = e;
                s_selectedOnce = false;
                s_keepSelected = false;
                s_injectedIndex = -1;
                Save();
                CoopPlugin.Log.LogInfo($"TravelDeck: packed '{e.Name}' ({Count(e)} cards)");
            }
            catch (Exception ex) { CoopPlugin.Log.LogWarning("TravelDeck pack: " + ex.Message); }
        }

        public static void Clear()
        {
            Current = null;
            s_injectedIndex = -1;
            Save();
        }

        /// <summary>DeckSync, just before it rebuilds the mirrored list: was the travel deck the
        /// selected one? (The rebuild clamps the selection; we put it back after.)</summary>
        public static void BeforeApply()
        {
            if (!CoopCore.IsVisiting || Current == null)
                return;
            s_keepSelected = s_injectedIndex >= 0 && CPlayerData.m_CurrentSelectedDeckIndex == s_injectedIndex;
        }

        /// <summary>DeckSync, after the mirror: the travel deck goes on the end of the list and
        /// is selected the first time (and stays selected while the visitor keeps it).</summary>
        public static void Inject(List<DeckCompactCardDataList> decks)
        {
            if (!CoopCore.IsVisiting || Current == null || decks == null)
            {
                s_injectedIndex = -1;
                return;
            }
            try
            {
                var d = new DeckCompactCardDataList
                {
                    deckName = "✈ " + Current.Name,
                    deckBoxIndex = Current.DeckBox,
                    playmatIndex = Current.Playmat,
                    compactCardDataAmountList = new List<CompactCardDataAmount>(),
                };
                foreach (var c in Current.Cards)
                    d.compactCardDataAmountList.Add(new CompactCardDataAmount { expansionType = c.Expansion, cardSaveIndex = c.Index, amount = c.Amount, gradedCardIndex = c.GradedIndex, isDestiny = c.IsDestiny });
                decks.Add(d);
                s_injectedIndex = decks.Count - 1;
                if (!s_selectedOnce || s_keepSelected)
                {
                    CPlayerData.m_CurrentSelectedDeckIndex = s_injectedIndex;
                    if (!s_selectedOnce)
                    {
                        s_selectedOnce = true;
                        HostOnlyFeatures.Notice($"Travel deck '{Current.Name}' selected ({Count(Current)} cards)");
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TravelDeck inject: " + e.Message); }
        }
    }
}
