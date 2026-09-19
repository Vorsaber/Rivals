using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// The trade window (competitive mode, phase 2): a VISITOR and the SHOP swap money and
    /// cards. The shop side offers from the till and the collection; the visitor offers from
    /// the carry-out bag (its balance and the cards in it - a visitor's local collection is the
    /// host's, not theirs). Either side edits its offer, both confirm, the host executes:
    /// its own side against the live world, the visitor's against the bag (a "done" tells the
    /// visitor's PC exactly what to move). Any change to either offer clears both confirmations.
    /// One trade at a time, host and visitor only - a teammate shares the till, so there is
    /// nothing to trade.
    /// </summary>
    public static class TradeSync
    {
        public static bool Open
        {
            get; private set;
        }
        public static string PartnerName = "";
        public static string Status = "";
        public static TradeOffer Mine = new TradeOffer();
        public static TradeOffer Theirs = new TradeOffer();
        private static int s_partnerConn = -1;   // host
        private static float s_lastSend;

        public static bool IHost => CoopCore.Role == CoopRole.Host;
        public static int PartnerConn => s_partnerConn;

        // ================================================================ open / close

        /// <summary>Host: start (or restart) a trade with this visitor.</summary>
        public static void HostOpen(int conn, string name)
        {
            if (!IHost)
                return;
            Reset();
            Open = true;
            s_partnerConn = conn;
            PartnerName = name ?? ("player " + conn);
            Status = "trading with " + PartnerName;
            SendState("open");
        }

        /// <summary>Visitor: ask the shop for a trade window.</summary>
        public static void GuestOpen()
        {
            if (CoopCore.Role != CoopRole.Client || !CoopCore.IsVisiting)
                return;
            Reset();
            Open = true;
            PartnerName = "the shop";
            Status = "asking the shop to trade...";
            CoopCore.Instance?.SendTrade(new TradeMessage { Op = "open" });
        }

        public static void Cancel(string why)
        {
            if (!Open)
                return;
            var msg = new TradeMessage { Op = "cancel" };
            if (IHost)
                CoopCore.Instance?.SendTradeTo(s_partnerConn, msg);
            else
                CoopCore.Instance?.SendTrade(msg);
            Reset();
            Status = "trade cancelled" + (string.IsNullOrEmpty(why) ? "" : " - " + why);
        }

        private static void Reset()
        {
            Open = false;
            s_partnerConn = -1;
            PartnerName = "";
            Mine = new TradeOffer();
            Theirs = new TradeOffer();
        }

        /// <summary>Host: the visitor left.</summary>
        public static void HostPeerGone(int conn)
        {
            if (Open && IHost && conn == s_partnerConn)
            {
                Reset();
                Status = "trade cancelled - they left";
            }
        }

        // ================================================================ my offer

        public static void SetMoney(double money)
        {
            if (!Open)
                return;
            money = Math.Max(0, Math.Round(money, 2));
            double cap = IHost ? CPlayerData.m_CoinAmountDouble : VisitorBag.Balance;
            if (money > cap)
            {
                Status = $"you only have {GameInstance.GetPriceString(cap)}";
                money = cap;
            }
            Mine.Money = money;
            Changed();
        }

        public static void AddCard(int exp, int index, bool destiny, int amount)
        {
            AddCard(exp, index, destiny, amount, 0);
        }

        // --- fv-908 grading-overhaul-fake begin
        /// <summary>Offer a card; grade > 0 offers that exact slab (one per line).</summary>
        public static void AddCard(int exp, int index, bool destiny, int amount, int grade)
        {
            if (!Open || amount <= 0)
                return;
            int have = Owned(exp, index, destiny, grade);
            var line = Mine.Cards.Find(c => c.Exp == exp && c.Index == index && c.Destiny == destiny && c.Grade == grade);
            int already = line != null ? line.Amount : 0;
            if (already + amount > have)
            {
                Status = grade > 0 ? "that slab is already on offer" : "you don't have that many";
                amount = have - already;
                if (amount <= 0)
                    return;
            }
            if (line != null)
                line.Amount += amount;
            else
                Mine.Cards.Add(new TradeCard { Exp = exp, Index = index, Destiny = destiny, Amount = amount, Grade = grade });
            Changed();
        }
        // --- fv-908 grading-overhaul-fake end

        public static void RemoveCard(int exp, int index, bool destiny, int amount, int grade = 0)
        {
            if (!Open)
                return;
            var line = Mine.Cards.Find(c => c.Exp == exp && c.Index == index && c.Destiny == destiny && c.Grade == grade);
            if (line == null)
                return;
            line.Amount -= amount;
            if (line.Amount <= 0)
                Mine.Cards.Remove(line);
            Changed();
        }

        public static void Confirm(bool on)
        {
            if (!Open)
                return;
            if (on && !ValidateMine(out string why))
            {
                Status = why;
                return;
            }
            Mine.Confirmed = on;
            Status = on ? "confirmed - waiting for " + PartnerName : "trading with " + PartnerName;
            if (IHost)
            {
                SendState("state");
                TryExecute();
            }
            else
                CoopCore.Instance?.SendTrade(new TradeMessage { Op = "confirm", GuestConfirmed = on });
        }

        /// <summary>An offer changed: both confirmations are void.</summary>
        private static void Changed()
        {
            Mine.Confirmed = false;
            Theirs.Confirmed = false;
            Status = "trading with " + PartnerName;
            if (IHost)
                SendState("state");
            else
                CoopCore.Instance?.SendTrade(new TradeMessage { Op = "offer", GuestMoney = Mine.Money, GuestCards = new List<TradeCard>(Mine.Cards) });
        }

        /// <summary>How many of this card I can offer: the collection (shop) or the bag (visitor).</summary>
        public static int Owned(int exp, int index, bool destiny)
        {
            return Owned(exp, index, destiny, 0);
        }

        // --- fv-908 grading-overhaul-fake begin
        /// <summary>grade > 0: how many slabs with exactly that encoded grade (normally 1 - the
        /// serial is unique) sit in the shop's graded album / the visitor's bag.</summary>
        public static int Owned(int exp, int index, bool destiny, int grade)
        {
            try
            {
                if (IHost)
                {
                    if (grade <= 0)
                        return CPlayerData.GetCardAmountByIndex(index, (ECardExpansionType)exp, destiny);
                    int n = 0;
                    var album = CPlayerData.m_GradedCardInventoryList;
                    for (int i = 0; album != null && i < album.Count; i++)
                    {
                        var e = album[i];
                        if (e != null && e.cardSaveIndex == index && (int)e.expansionType == exp && e.isDestiny == destiny && e.amount == grade)
                            n++;
                    }
                    return n;
                }
                var line = VisitorBag.Current.Cards.Find(c => c.Expansion == exp && c.Index == index && c.IsDestiny == destiny && c.Grade == grade);
                return line != null ? line.Amount : 0;
            }
            catch { return 0; }
        }
        // --- fv-908 grading-overhaul-fake end

        private static bool ValidateMine(out string why)
        {
            why = "";
            double cap = IHost ? CPlayerData.m_CoinAmountDouble : VisitorBag.Balance;
            if (Mine.Money > cap + 0.005)
            {
                why = $"you can't cover {GameInstance.GetPriceString(Mine.Money)}";
                return false;
            }
            foreach (var c in Mine.Cards)
                if (Owned(c.Exp, c.Index, c.Destiny, c.Grade) < c.Amount)
                {
                    why = "you no longer have " + Label(c);
                    return false;
                }
            if (Mine.Money <= 0 && Mine.Cards.Count == 0 && Theirs.Money <= 0 && Theirs.Cards.Count == 0)
            {
                why = "nothing on the table";
                return false;
            }
            return true;
        }

        // ================================================================ wire

        private static void SendState(string op)
        {
            if (!IHost || s_partnerConn < 0)
                return;
            CoopCore.Instance?.SendTradeTo(s_partnerConn, new TradeMessage
            {
                Op = op,
                HostMoney = Mine.Money,
                HostCards = new List<TradeCard>(Mine.Cards),
                HostConfirmed = Mine.Confirmed,
                GuestMoney = Theirs.Money,
                GuestCards = new List<TradeCard>(Theirs.Cards),
                GuestConfirmed = Theirs.Confirmed,
            });
            s_lastSend = Time.unscaledTime;
        }

        public static void HostOnMessage(int conn, TradeMessage m, string name)
        {
            if (!IHost)
                return;
            switch (m.Op)
            {
                case "open":
                    if (Open && conn != s_partnerConn)
                    {
                        CoopCore.Instance?.SendTradeTo(conn, new TradeMessage { Op = "cancel" });
                        return;
                    }
                    HostOpen(conn, name);
                    HostOnlyFeatures.Notice(PartnerName + " wants to trade - see the RIVALS tab (F2)");
                    break;
                case "offer":
                    if (!Open || conn != s_partnerConn)
                        return;
                    Theirs.Money = Math.Max(0, m.GuestMoney);
                    Theirs.Cards = m.GuestCards ?? new List<TradeCard>();
                    Mine.Confirmed = false;
                    Theirs.Confirmed = false;
                    Status = "trading with " + PartnerName + " (offer changed)";
                    SendState("state");
                    break;
                case "confirm":
                    if (!Open || conn != s_partnerConn)
                        return;
                    Theirs.Confirmed = m.GuestConfirmed;
                    SendState("state");
                    TryExecute();
                    break;
                case "cancel":
                    if (Open && conn == s_partnerConn)
                    {
                        Reset();
                        Status = "trade cancelled by " + (name ?? "the visitor");
                    }
                    break;
            }
        }

        public static void GuestOnMessage(TradeMessage m)
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            switch (m.Op)
            {
                case "open":
                case "state":
                    if (!Open)
                    {
                        Reset();
                        Open = true;
                        PartnerName = "the shop";
                        HostOnlyFeatures.Notice("The shop opened a trade - see the RIVALS tab (F2)");
                    }
                    Theirs.Money = m.HostMoney;
                    Theirs.Cards = m.HostCards ?? new List<TradeCard>();
                    Theirs.Confirmed = m.HostConfirmed;
                    Mine.Confirmed = m.GuestConfirmed; // the host's view of us is the truth
                    Status = Mine.Confirmed && Theirs.Confirmed ? "executing..." : Mine.Confirmed ? "confirmed - waiting for the shop" : "trading with the shop";
                    break;
                case "cancel":
                    if (Open)
                    {
                        Reset();
                        Status = "trade cancelled by the shop";
                        HostOnlyFeatures.Notice("Trade cancelled");
                    }
                    break;
                case "done":
                    ApplyDone(m);
                    break;
            }
        }

        // ================================================================ execute

        /// <summary>Host: both confirmed - move the shop's side for real and tell the visitor
        /// what to move in the bag. Validated once more right here.</summary>
        private static void TryExecute()
        {
            if (!IHost || !Open || !Mine.Confirmed || !Theirs.Confirmed)
                return;
            if (!ValidateMine(out string why))
            {
                Mine.Confirmed = false;
                Status = why;
                SendState("state");
                return;
            }
            var done = new TradeMessage
            {
                Op = "done",
                HostMoney = Mine.Money,
                HostCards = new List<TradeCard>(Mine.Cards),
                GuestMoney = Theirs.Money,
                GuestCards = new List<TradeCard>(Theirs.Cards),
                HostConfirmed = true,
                GuestConfirmed = true,
            };
            try
            {
                if (Mine.Money > 0.005)
                    CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)Mine.Money));
                if (Theirs.Money > 0.005)
                    CEventManager.QueueEvent(new CEventPlayer_AddCoin((float)Theirs.Money));
                foreach (var c in Mine.Cards)
                {
                    var cd = CPlayerData.GetCardData(c.Index, (ECardExpansionType)c.Exp, c.Destiny);
                    if (cd == null || c.Amount <= 0)
                        continue;
                    // fv-908: a slab lives in the graded album, keyed by its encoded grade -
                    // ReduceCard would miss it and decrement the ungraded stack instead
                    if (c.Grade > 0)
                    {
                        cd.cardGrade = c.Grade;
                        for (int n = 0; n < c.Amount && CPlayerData.HasGradedCardInAlbum(cd); n++)
                            CPlayerData.RemoveGradedCard(cd, ignoreGradedCardIndex: true);
                        continue;
                    }
                    CPlayerData.ReduceCard(cd, c.Amount);
                }
                foreach (var c in Theirs.Cards)
                {
                    var cd = CPlayerData.GetCardData(c.Index, (ECardExpansionType)c.Exp, c.Destiny);
                    if (cd == null || c.Amount <= 0)
                        continue;
                    // fv-908: a slab from the visitor's bag was certified in THEIR shop - register
                    // or re-slab it here before AddCard, or GO's anti-cheat stamps it FAKE
                    if (c.Grade > 0)
                    {
                        for (int n = 0; n < c.Amount; n++)
                        {
                            var slab = n == 0 ? cd : CPlayerData.GetCardData(c.Index, (ECardExpansionType)c.Exp, c.Destiny);
                            slab.cardGrade = c.Grade;
                            Util.GradingInterop.AdoptForeign(slab, PartnerName);
                            CPlayerData.AddCard(slab, 1);
                        }
                        continue;
                    }
                    CPlayerData.AddCard(cd, c.Amount);
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeSync execute: " + e.Message); }
            CoopCore.Instance?.SendTradeTo(s_partnerConn, done);
            string summary = Summary(Mine, Theirs);
            CoopPlugin.Log.LogInfo("rivals: trade with " + PartnerName + " done - " + summary);
            HostOnlyFeatures.Notice("Trade with " + PartnerName + " done: " + summary);
            Reset();
            Status = "trade done";
        }

        /// <summary>Visitor: the shop executed - mirror it in the bag.</summary>
        private static void ApplyDone(TradeMessage m)
        {
            try
            {
                if (m.GuestMoney > 0.005)
                    VisitorBag.TrySpend(m.GuestMoney, "trade with the shop", true);
                if (m.HostMoney > 0.005)
                    VisitorBag.Earn(m.HostMoney, "trade with the shop");
                if (m.GuestCards != null)
                    foreach (var c in m.GuestCards)
                        VisitorBag.RemoveCard(c.Exp, c.Index, c.Destiny, c.Amount, c.Grade); // fv-908: the slab's line
                if (m.HostCards != null)
                    foreach (var c in m.HostCards)
                    {
                        var cd = CPlayerData.GetCardData(c.Index, (ECardExpansionType)c.Exp, c.Destiny);
                        if (cd != null && c.Amount > 0)
                        {
                            cd.cardGrade = c.Grade; // fv-908: the shop's slab travels home with its grade + serial
                            VisitorBag.AddCard(cd, c.Amount, 0f);
                        }
                    }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeSync apply: " + e.Message); }
            var mine = new TradeOffer { Money = m.GuestMoney, Cards = m.GuestCards ?? new List<TradeCard>() };
            var theirs = new TradeOffer { Money = m.HostMoney, Cards = m.HostCards ?? new List<TradeCard>() };
            string summary = Summary(mine, theirs);
            CoopPlugin.Log.LogInfo("rivals: trade done - " + summary);
            HostOnlyFeatures.Notice("Trade done: " + summary + $" (bag balance {GameInstance.GetPriceString(VisitorBag.Balance)})");
            Reset();
            Status = "trade done";
        }

        private static string Summary(TradeOffer mine, TradeOffer theirs)
        {
            int gave = 0, got = 0;
            foreach (var c in mine.Cards)
                gave += c.Amount;
            foreach (var c in theirs.Cards)
                got += c.Amount;
            return $"gave {GameInstance.GetPriceString(mine.Money)} + {gave} card(s), got {GameInstance.GetPriceString(theirs.Money)} + {got} card(s)";
        }

        // ================================================================ picker support

        public static string Label(TradeCard c)
        {
            try
            {
                var cd = CPlayerData.GetCardData(c.Index, (ECardExpansionType)c.Exp, c.Destiny);
                cd.cardGrade = c.Grade; // fv-908
                return Label(cd);
            }
            catch { return $"card {c.Index}/{c.Exp}"; }
        }

        public static string Label(CardData cd)
        {
            if (cd == null)
                return "?";
            string name = null;
            try
            {
                var md = InventoryBase.GetMonsterData(cd.monsterType);
                name = md != null ? md.GetName() : null;
            }
            catch { }
            if (string.IsNullOrEmpty(name))
                name = cd.monsterType.ToString();
            string border = cd.borderType == ECardBorderType.Base ? "" : " " + cd.borderType;
            return $"{name}{border}{(cd.isFoil ? " foil" : "")}{(cd.isDestiny ? " destiny" : "")} [{cd.expansionType}]{GradeSuffix(cd.cardGrade)}";
        }

        // --- fv-908 grading-overhaul-fake begin
        /// <summary>" grade N #serial" for a slab (serial only when Grading Overhaul decodes
        /// it), "" for an ungraded card.</summary>
        public static string GradeSuffix(int grade)
        {
            if (grade <= 0)
                return "";
            int company, cert;
            string serial = Util.GradingInterop.DecodeCert(grade, out company, out cert) ? " #" + cert.ToString("D7") : "";
            return " grade " + Util.GradingInterop.Actual(grade) + serial;
        }
        // --- fv-908 grading-overhaul-fake end

        /// <summary>What I can offer, filtered by name: the collection (shop) or the bag (visitor).</summary>
        public static List<(TradeCard card, string label, int have)> Search(string filter, int max)
        {
            var result = new List<(TradeCard, string, int)>();
            filter = (filter ?? "").Trim().ToLowerInvariant();
            try
            {
                if (IHost)
                {
                    if (filter.Length < 2)
                        return result;
                    foreach (var (exp, destiny) in Lists())
                    {
                        List<int> list;
                        try
                        {
                            list = CPlayerData.GetCardCollectedList(exp, destiny);
                        }
                        catch { continue; }
                        for (int i = 0; list != null && i < list.Count && result.Count < max; i++)
                        {
                            int have = list[i];
                            if (have <= 0)
                                continue;
                            var cd = CPlayerData.GetCardData(i, exp, destiny);
                            string label = Label(cd);
                            if (label.ToLowerInvariant().Contains(filter))
                                result.Add((new TradeCard { Exp = (int)exp, Index = i, Destiny = destiny, Amount = 1 }, label, have));
                        }
                    }
                    // --- fv-908 grading-overhaul-fake begin
                    // the graded album: every slab is its own line (its serial is unique)
                    var album = CPlayerData.m_GradedCardInventoryList;
                    for (int i = 0; album != null && i < album.Count && result.Count < max; i++)
                    {
                        var e = album[i];
                        if (e == null || e.amount <= 0)
                            continue;
                        var tc = new TradeCard { Exp = (int)e.expansionType, Index = e.cardSaveIndex, Destiny = e.isDestiny, Amount = 1, Grade = e.amount };
                        string label = Label(tc);
                        if (label.ToLowerInvariant().Contains(filter))
                            result.Add((tc, label, Owned(tc.Exp, tc.Index, tc.Destiny, tc.Grade)));
                    }
                    // --- fv-908 grading-overhaul-fake end
                }
                else
                {
                    foreach (var c in VisitorBag.Current.Cards)
                    {
                        if (c.Amount <= 0)
                            continue;
                        var tc = new TradeCard { Exp = c.Expansion, Index = c.Index, Destiny = c.IsDestiny, Amount = 1, Grade = c.Grade }; // fv-908
                        string label = Label(tc);
                        if (filter.Length == 0 || label.ToLowerInvariant().Contains(filter))
                            result.Add((tc, label, c.Amount));
                        if (result.Count >= max)
                            break;
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TradeSync search: " + e.Message); }
            return result;
        }

        private static (ECardExpansionType, bool)[] Lists() => new[]
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
    }
}
