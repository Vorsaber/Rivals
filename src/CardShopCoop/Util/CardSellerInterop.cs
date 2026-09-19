using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CardShopCoop.Util
{
    /// <summary>
    /// fv-914: lets the third-party <b>CardSeller</b> mod (io.helwig.tcgcss.CardSeller, decompiled
    /// 1.0.6 under audit/mods/CardSeller/) put GRADED cards out on the shelves too, behind
    /// <see cref="CoopPlugin.CardSellerIncludeGraded"/> (default off = CardSeller untouched).
    ///
    /// What CardSeller does today: <c>PatchIt.GetCompatibleCards(expansion, ghostDimension)</c>
    /// walks <c>CPlayerData.GetCardCollectedList</c> - the UNGRADED per-expansion count arrays -
    /// so a graded card, which lives in <c>m_GradedCardInventoryList</c> instead, is never a
    /// candidate. Its placing coroutine then, per card: spawns a Card3d, <c>SetCardUI(val)</c>,
    /// <c>SetCardOnShelf</c>, <c>CPlayerData.ReduceCard(val, 1)</c>, and drops the card from its
    /// list when <c>GetCardAmount(val) == KeepCardQty</c>. Never a modified CardSeller DLL: the
    /// graded path is four Harmony patches on OUR side, all keyed by the identity of the CardData
    /// objects THIS class minted, so nothing else in the game sees a changed ReduceCard or
    /// GetCardAmount:
    ///  1. prefix  <c>PatchIt.GetCompatibleCards()</c> (the async stub, MAIN thread): build the
    ///     graded candidates - one CardData per album slab (CPlayerData.GetGradedCardData, so
    ///     cardGrade is the encoded grade and gradedCardIndex the exact row), priced by
    ///     <c>CPlayerData.GetCardMarketPrice(CardData)</c> = the game's graded market price with
    ///     Grading Overhaul's registry/company multiplier postfixes already on it - and filtered by
    ///     CardSeller's own SellOnlyGreaterThan/LessThan band. Done here, not in the Task.Run
    ///     worker CardSeller scans on, so GO's non-thread-safe dictionaries are only touched from
    ///     the main thread.
    ///  2. postfix <c>PatchIt.GetCompatibleCards(ECardExpansionType, bool)</c> (worker thread):
    ///     append the candidates of that expansion / ghost dimension to CardSeller's list, so its
    ///     per-expansion Filters toggles and its price-descending sort apply to slabs unchanged.
    ///  3. prefix  <c>CPlayerData.ReduceCard</c>: for a minted card ONLY, the album exit is
    ///     <c>CPlayerData.RemoveGradedCard</c> (the graded album is a separate list ReduceCard
    ///     never touches - it would decrement the ungraded stack instead), and the original is
    ///     skipped. RemoveGradedCard is what the binder itself calls when the player lifts a slab
    ///     out (CollectionBinderFlipAnimCtrl.OnRightMouseButtonUp, decompiled :225), and it is
    ///     the co-op mirror point too: GamePatches.RemoveGradedCardPostfix -> ForwardGradedRemoval
    ///     takes the slab out of every peer's album by its encoded identity. Under Grading Overhaul
    ///     that IS the cert release the game has: the cert stays burned+bound in GO's save (GO has
    ///     no unbind - a sold slab's serial is spent, exactly as after a hand-placed sale) and the
    ///     card leaves the album so no duplicate-cert scan can ever see it twice.
    ///  4. prefix  <c>CPlayerData.GetCardAmount</c>: a minted card counts as exactly one
    ///     (KeepCardQty + 1) before it is placed, and as KeepCardQty right after, so CardSeller's
    ///     total is right and the slab leaves its list after ONE placement. KeepCardQty is a
    ///     duplicates rule for ungraded stacks; every slab is one card, so it does not hold any
    ///     back.
    /// GamePatches.ReduceCardPostfix skips a minted card (ConsumeReduceSkip) so the co-op
    /// CardDelta mirror does not ALSO ship an ungraded "-1" for it - the GradedRemove message
    /// from step 3 is the one true mirror.
    /// Everything CardSeller-typed is reflection; when the mod is absent nothing is patched.
    /// </summary>
    internal static class CardSellerInterop
    {
        public const string PluginGuid = "io.helwig.tcgcss.CardSeller";
        private const string AssemblyName = "CardSeller";

        private static Type s_patchIt, s_plugin;
        private static FieldInfo s_keepQty, s_greaterThan, s_lessThan, s_isRunning;
        private static bool s_patched;

        /// <summary>True once the CardSeller members resolved and our patches went in.</summary>
        public static bool Present => s_patched;

        public static bool IncludeGraded =>
            CoopPlugin.CardSellerIncludeGraded != null && CoopPlugin.CardSellerIncludeGraded.Value;

        /// <summary>CardSeller is loaded (its plugin type resolved), whether or not our patches
        /// went in - for the TUNING app's line.</summary>
        public static bool Installed
        {
            get
            {
                try { return BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(PluginGuid); }
                catch (Exception e) { Swallow.Log(e); return false; }
            }
        }

        private sealed class Minted
        {
            public int EncodedGrade;
            public int GradedCardIndex;
            public float Price;
            public ECardExpansionType Expansion;
            public bool IsDestiny;
        }

        private static readonly object Gate = new object();
        // every CardData this class minted for the run in progress, by reference
        private static readonly Dictionary<CardData, Minted> s_minted = new Dictionary<CardData, Minted>(ReferenceEq.Instance);
        // the one CardSeller just placed: its next GetCardAmount answers KeepCardQty
        private static CardData s_placed;
        // the one whose ReduceCard we replaced: ReduceCardPostfix must not mirror it
        private static CardData s_reduceSkip;
        private static int s_runCandidates, s_runPlaced;

        private sealed class ReferenceEq : IEqualityComparer<CardData>
        {
            public static readonly ReferenceEq Instance = new ReferenceEq();
            public bool Equals(CardData a, CardData b) => ReferenceEquals(a, b);
            public int GetHashCode(CardData o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>Called from GamePatches.ApplyAll. Soft dependency: CoopPlugin declares
        /// [BepInDependency(PluginGuid, SoftDependency)] so CardSeller's assembly is loaded
        /// before ours when it is installed at all.</summary>
        public static void ApplyPatches(Harmony h)
        {
            if (!Installed)
            {
                CoopPlugin.Log.LogInfo("CardSeller not installed - CardSeller.IncludeGraded has nothing to do");
                return;
            }
            try
            {
                s_patchIt = ModParity.ResolveType("CardSeller.PatchIt", AssemblyName);
                s_plugin = ModParity.ResolveType("CardSeller.Plugin", AssemblyName);
                if (s_patchIt == null || s_plugin == null)
                {
                    CoopPlugin.Log.LogWarning("CardSeller is installed but CardSeller.PatchIt / CardSeller.Plugin did not resolve - IncludeGraded inert");
                    return;
                }
                const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                s_keepQty = s_plugin.GetField("m_ConfigKeepCardQty", F);
                s_greaterThan = s_plugin.GetField("m_ConfigSellOnlyGreaterThanMP", F);
                s_lessThan = s_plugin.GetField("m_ConfigSellOnlyLessThanMP", F);
                s_isRunning = s_patchIt.GetField("isRunning", F);
                // the async stub (Task<List<CardData>> GetCompatibleCards()) and the per-expansion scan
                MethodInfo scanAll = s_patchIt.GetMethod("GetCompatibleCards", F, null, Type.EmptyTypes, null);
                MethodInfo scanOne = s_patchIt.GetMethod("GetCompatibleCards", F, null, new[] { typeof(ECardExpansionType), typeof(bool) }, null);
                if (scanAll == null || scanOne == null || s_keepQty == null)
                {
                    CoopPlugin.Log.LogWarning("CardSeller.PatchIt.GetCompatibleCards / Plugin.m_ConfigKeepCardQty not found (CardSeller version changed?) - IncludeGraded inert");
                    return;
                }
                h.Patch(scanAll, prefix: new HarmonyMethod(typeof(CardSellerInterop), nameof(ScanAllPrefix)));
                h.Patch(scanOne, postfix: new HarmonyMethod(typeof(CardSellerInterop), nameof(ScanOnePostfix)));
                h.Patch(AccessTools.Method(typeof(CPlayerData), "ReduceCard", new[] { typeof(CardData), typeof(int) }),
                    prefix: new HarmonyMethod(typeof(CardSellerInterop), nameof(ReduceCardPrefix)));
                h.Patch(AccessTools.Method(typeof(CPlayerData), "GetCardAmount", new[] { typeof(CardData) }),
                    prefix: new HarmonyMethod(typeof(CardSellerInterop), nameof(GetCardAmountPrefix)));
                s_patched = true;
                CoopPlugin.Log.LogInfo("CardSeller detected - graded cards " + (IncludeGraded ? "INCLUDED" : "excluded") + " (config CardSeller.IncludeGraded)");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("CardSellerInterop.ApplyPatches: " + e.Message + " - IncludeGraded inert");
            }
        }

        private static int KeepQty
        {
            get
            {
                try
                {
                    var entry = s_keepQty?.GetValue(null) as BepInEx.Configuration.ConfigEntry<int>;
                    return entry != null ? entry.Value : 0;
                }
                catch (Exception e) { Swallow.Log(e); return 0; }
            }
        }

        private static float Band(FieldInfo fi, float fallback)
        {
            try
            {
                var entry = fi?.GetValue(null) as BepInEx.Configuration.ConfigEntry<float>;
                return entry != null ? entry.Value : fallback;
            }
            catch (Exception e) { Swallow.Log(e); return fallback; }
        }

        // ------------------------------------------------------------------ 1. candidates (main thread)

        /// <summary>Prefix on CardSeller's async GetCompatibleCards() stub - runs on the main
        /// thread, before its Task.Run worker starts. Mints one CardData per album slab.</summary>
        public static void ScanAllPrefix()
        {
            lock (Gate)
            {
                s_minted.Clear();
                s_placed = null;
                s_reduceSkip = null;
                s_runCandidates = 0;
                s_runPlaced = 0;
            }
            if (!IncludeGraded)
                return;
            try
            {
                float lo = Band(s_greaterThan, 0.5f), hi = Band(s_lessThan, 100f);
                var album = CPlayerData.m_GradedCardInventoryList;
                if (album == null)
                    return;
                int skippedBand = 0, skippedFake = 0;
                var minted = new List<KeyValuePair<CardData, Minted>>();
                for (int i = 0; i < album.Count; i++)
                {
                    var row = album[i];
                    if (row == null || row.amount <= 0)
                        continue;
                    // a FAKE-stamped slab (GO's +1e9 anti-cheat encoding) is not merchandise
                    if (GradingInterop.Present && GradingInterop.CheatFlagged(row.amount))
                    {
                        skippedFake++;
                        continue;
                    }
                    CardData cd = CPlayerData.GetGradedCardData(row);
                    if (cd == null || (int)cd.monsterType == 0)
                        continue;
                    float price = CPlayerData.GetCardMarketPrice(cd);
                    if (!(price > lo && price < hi))
                    {
                        skippedBand++;
                        continue;
                    }
                    minted.Add(new KeyValuePair<CardData, Minted>(cd, new Minted
                    {
                        EncodedGrade = row.amount,
                        GradedCardIndex = row.gradedCardIndex,
                        Price = price,
                        Expansion = row.expansionType,
                        IsDestiny = row.isDestiny,
                    }));
                }
                lock (Gate)
                {
                    foreach (var kv in minted)
                        s_minted[kv.Key] = kv.Value;
                    s_runCandidates = minted.Count;
                }
                CoopPlugin.Log.LogInfo($"CardSeller graded: {minted.Count} slab(s) eligible of {album.Count} in the album (band {lo}-{hi}: {skippedBand} outside, {skippedFake} FAKE-flagged skipped)");
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardSellerInterop.ScanAllPrefix: " + e.Message); }
        }

        // ------------------------------------------------------------------ 2. splice (worker thread)

        /// <summary>Postfix on GetCompatibleCards(ECardExpansionType expansionType, bool
        /// findGhostDimensionCards): append this expansion's slabs to CardSeller's list. Only
        /// reads what step 1 minted; no game or GO call happens on this thread.</summary>
        public static void ScanOnePostfix(ECardExpansionType __0, bool __1, List<CardData> __result)
        {
            if (__result == null || !IncludeGraded)
                return;
            try
            {
                int added = 0;
                lock (Gate)
                {
                    foreach (var kv in s_minted)
                    {
                        var m = kv.Value;
                        if (m.Expansion != __0)
                            continue;
                        // the ghost dimension is the isDestiny bit on a Ghost row; the other
                        // expansions do not split on it (CardSeller passes false for them)
                        if (__0 == ECardExpansionType.Ghost && m.IsDestiny != __1)
                            continue;
                        __result.Add(kv.Key);
                        added++;
                    }
                }
                if (added > 0)
                    CoopPlugin.Log.LogInfo($"CardSeller graded: +{added} slab(s) for {__0}{(__1 ? " (dimension)" : "")}");
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardSellerInterop.ScanOnePostfix: " + e.Message); }
        }

        // ------------------------------------------------------------------ 3. album exit

        /// <summary>Prefix on CPlayerData.ReduceCard(cardData, reduceAmount). A minted slab leaves
        /// through RemoveGradedCard instead; anything else is untouched.</summary>
        public static bool ReduceCardPrefix(CardData cardData)
        {
            if (cardData == null || s_minted.Count == 0)
                return true;
            Minted m;
            lock (Gate)
            {
                if (!s_minted.TryGetValue(cardData, out m))
                    return true;
                s_minted.Remove(cardData);
                s_placed = cardData;
                s_reduceSkip = cardData;
                s_runPlaced++;
            }
            try
            {
                // SetCardUI clamps an encoded grade to 10 and GO's display swaps restore it
                // asynchronously, so re-assert the exact album identity for the match and put
                // back whatever the live object held - the shelf's copy is not ours to mutate.
                int liveGrade = cardData.cardGrade, liveIdx = cardData.gradedCardIndex;
                cardData.cardGrade = m.EncodedGrade;
                cardData.gradedCardIndex = m.GradedCardIndex;
                try
                {
                    int before = CPlayerData.m_GradedCardInventoryList.Count;
                    CPlayerData.RemoveGradedCard(cardData);
                    if (CPlayerData.m_GradedCardInventoryList.Count == before)
                    {
                        // gradedCardIndex is not unique after removals (AddCard numbers it
                        // Count+1, decompiled :1609) - fall back to the identity match
                        CPlayerData.RemoveGradedCard(cardData, ignoreGradedCardIndex: true);
                    }
                    if (CPlayerData.m_GradedCardInventoryList.Count == before)
                        CoopPlugin.Log.LogWarning($"CardSeller graded: {Ident(cardData)} (grade {m.EncodedGrade}) was NOT in the album when placed - it left between the scan and the shelf (another player took it?); the shelf now holds a copy the album no longer backs");
                    else
                        CoopPlugin.Log.LogInfo($"CardSeller graded: {Ident(cardData)} grade {GradeText(m.EncodedGrade)} out of the album onto the shelf at market {m.Price:0.00}");
                }
                finally
                {
                    cardData.cardGrade = liveGrade;
                    cardData.gradedCardIndex = liveIdx;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardSellerInterop.ReduceCardPrefix: " + e.Message); }
            return false; // never let ReduceCard touch the ungraded stack for a slab
        }

        /// <summary>GamePatches.ReduceCardPostfix: true exactly once for the slab whose ReduceCard
        /// this class replaced, so the CardDelta mirror stays quiet for it.</summary>
        public static bool ConsumeReduceSkip(CardData cardData)
        {
            if (cardData == null)
                return false;
            lock (Gate)
            {
                if (!ReferenceEquals(s_reduceSkip, cardData))
                    return false;
                s_reduceSkip = null;
                return true;
            }
        }

        // ------------------------------------------------------------------ 4. counting

        /// <summary>Prefix on CPlayerData.GetCardAmount(cardData). A minted slab is one card:
        /// KeepQty+1 while it waits (CardSeller's total counts it once), KeepQty once placed
        /// (CardSeller drops it from its list). Any other CardData is untouched.</summary>
        public static bool GetCardAmountPrefix(CardData cardData, ref int __result)
        {
            // hot path (binder pages call this per card): nothing minted, nothing to do - the
            // unlocked reads are safe because a run mints on the main thread before its worker
            // starts and consumes on the main thread again
            if (cardData == null || (s_placed == null && s_minted.Count == 0))
                return true;
            lock (Gate)
            {
                if (ReferenceEquals(s_placed, cardData))
                {
                    s_placed = null;
                    __result = KeepQty;
                    return false;
                }
                if (s_minted.ContainsKey(cardData))
                {
                    __result = KeepQty + 1;
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ status (TUNING app)

        /// <summary>True while CardSeller's placing coroutine runs (its private isRunning).</summary>
        public static bool Running
        {
            get
            {
                try { return s_isRunning != null && (bool)s_isRunning.GetValue(null); }
                catch (Exception e) { Swallow.Log(e); return false; }
            }
        }

        /// <summary>One line for the TUNING app: what the last run did with slabs.</summary>
        public static string Describe()
        {
            if (!Installed)
                return "CardSeller is not installed on this PC.";
            if (!s_patched)
                return "CardSeller is installed but its version did not match - graded selling inert (see the log).";
            int cand, placed;
            lock (Gate)
            {
                cand = s_runCandidates;
                placed = s_runPlaced;
            }
            string state = Running ? "placing now" : "idle";
            return IncludeGraded
                ? $"graded cards included - last run: {placed} of {cand} eligible slab(s) placed ({state})"
                : "graded cards excluded - CardSeller sells ungraded cards only (vanilla CardSeller)";
        }

        private static string Ident(CardData cd)
        {
            try { return $"{cd.expansionType}/{cd.monsterType}/{cd.borderType}{(cd.isFoil ? " foil" : "")}{(cd.isDestiny ? " dim" : "")}"; }
            catch (Exception e) { Swallow.Log(e); return "?"; }
        }

        private static string GradeText(int encoded)
        {
            if (encoded <= 10)
                return encoded.ToString();
            int actual = GradingInterop.Present ? GradingInterop.Actual(encoded) : encoded;
            return $"{actual} (encoded {encoded})";
        }
    }
}
