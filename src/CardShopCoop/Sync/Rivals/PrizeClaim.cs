using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// R9 - WHAT an entrant may take off the tournament prize shelf for free: their own
    /// placement's prize list, once. Vanilla hands an NPC winner exactly
    /// <c>m_TournamentData.m_PrizeDataList[group]</c> (group = placement for 1st..3rd, the
    /// 4th list for 4th..8th, nothing past 8th) and the NPC walks to a compartment holding
    /// that card / item type. A human entrant takes their winnings by hand, so this is the
    /// same list kept as a LEDGER: the host holds one per entitled connection and decrements
    /// it as claims land; the client gets a mirror (TournamentPrizeClaimMessage) to refuse a
    /// take locally before it is sent. Beyond the ledger the prize shelf is an ordinary shelf
    /// (visitor buy-by-taking), so nothing here decides money - only whether the take is FREE.
    /// Host is authoritative: a card claim it does not honour is bounced (RefusedKey) and the
    /// client takes the card back out of its bag.
    /// </summary>
    public static class PrizeClaim
    {
        // ================================================================ host

        private sealed class Ledger
        {
            public int Placement;
            public readonly List<TournamentPrizeData> Remaining = new List<TournamentPrizeData>();
        }

        private static readonly Dictionary<int, Ledger> s_host = new Dictionary<int, Ledger>();

        /// <summary>Set by TournamentSync (host -> one client).</summary>
        public static Action<int, INetMessage> SendToClient;

        public static void Reset()
        {
            s_host.Clear();
            s_placement = -1;
            s_remaining.Clear();
            s_claimedKeys.Clear();
        }

        public static bool HostHas(int conn)
        {
            return s_host.ContainsKey(conn);
        }

        /// <summary>The vanilla group for a placement (Customer.OnTournamentEnded), -1 = no prize.</summary>
        public static int GroupOf(int placement)
        {
            if (placement < 0)
                return -1;
            if (placement <= 2)
                return placement;
            if (placement <= 7)
                return 3;
            return -1;
        }

        /// <summary>Host: this connection placed here - build its ledger from the catalog and
        /// tell it. An entrant past the prize table gets an EMPTY ledger (nothing is free).</summary>
        public static void HostGrant(int conn, int placement, TournamentData td)
        {
            var ledger = new Ledger { Placement = placement };
            int group = GroupOf(placement);
            try
            {
                var lists = td != null ? td.m_PrizeDataList : null;
                var inner = group >= 0 && lists != null && group < lists.Count && lists[group] != null ? lists[group].m_PrizeDataList : null;
                if (inner != null)
                    for (int i = 0; i < inner.Count; i++)
                    {
                        var p = inner[i];
                        if (p == null || p.m_Count <= 0 || !(p.IsCard() || p.IsItem()))
                            continue;
                        // a COPY: the catalog is the plan for the next tournament too
                        ledger.Remaining.Add(new TournamentPrizeData { m_CardData = p.IsCard() ? p.m_CardData : null, m_ItemType = p.IsCard() ? EItemType.None : p.m_ItemType, m_Count = p.m_Count });
                    }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PrizeClaim.HostGrant: " + e.Message); }
            s_host[conn] = ledger;
            CoopPlugin.Log.LogInfo($"PrizeClaim: conn {conn} placed #{placement + 1} - entitled to {Describe(ToEntries(ledger.Remaining))}");
            HostSend(conn);
        }

        public static void HostRevoke(int conn)
        {
            if (s_host.Remove(conn))
                CoopPlugin.Log.LogInfo($"PrizeClaim: conn {conn} entitlement cleared");
        }

        /// <summary>Host: the day rolled - every ledger goes, and each entrant's mirror is told
        /// (Placement -1) so the next tournament's grant reads as new on their side.</summary>
        public static void HostRevokeAll()
        {
            if (s_host.Count == 0)
                return;
            CoopPlugin.Log.LogInfo("PrizeClaim: entitlements cleared (day rolled)");
            var conns = new List<int>(s_host.Keys);
            s_host.Clear();
            if (SendToClient == null)
                return;
            for (int i = 0; i < conns.Count; i++)
            {
                try { SendToClient(conns[i], new TournamentPrizeClaimMessage { Placement = -1 }); }
                catch (Exception e) { CoopPlugin.Log.LogWarning("PrizeClaim.HostRevokeAll: " + e.Message); }
            }
        }

        /// <summary>Host: does this card come off the ledger? Consumes one when it does.</summary>
        public static bool HostClaimCard(int conn, CardData card)
        {
            Ledger l;
            if (card == null || !s_host.TryGetValue(conn, out l))
                return false;
            int i = FindCard(l.Remaining, card);
            if (i < 0)
                return false;
            Consume(l.Remaining, i, 1);
            HostSend(conn);
            return true;
        }

        /// <summary>Host: are ALL <paramref name="count"/> of this item on the ledger? Consumes
        /// them when they are. All-or-nothing so both sides make the same free/paid call.</summary>
        public static bool HostClaimItems(int conn, EItemType type, int count)
        {
            Ledger l;
            if (count <= 0 || !s_host.TryGetValue(conn, out l))
                return false;
            int i = FindItem(l.Remaining, type, count);
            if (i < 0)
                return false;
            Consume(l.Remaining, i, count);
            HostSend(conn);
            return true;
        }

        /// <summary>Host: this entrant still has winnings on the ledger.</summary>
        public static bool HostHasRemaining(int conn)
        {
            Ledger l;
            return s_host.TryGetValue(conn, out l) && l.Remaining.Count > 0;
        }

        /// <summary>Host: a prize-shelf take by an entrant that is NOT their prize while they
        /// still have winnings to collect - the client refuses this locally; this is the
        /// authoritative copy of that rule for a stale mirror.</summary>
        public static bool HostShouldRefuseItems(int conn, EItemType type, int count)
        {
            Ledger l;
            if (!s_host.TryGetValue(conn, out l) || l.Remaining.Count == 0)
                return false;
            return FindItem(l.Remaining, type, count) < 0;
        }

        /// <summary>Host: mirror the ledger to its owner. A bounce (refusedKey) goes even
        /// when there is no ledger, so a claim from a non-entrant still gives the card back.</summary>
        public static void HostSend(int conn, int refusedKey = 0)
        {
            Ledger l;
            if (SendToClient == null)
                return;
            bool has = s_host.TryGetValue(conn, out l);
            if (!has && refusedKey == 0)
                return;
            try
            {
                SendToClient(conn, new TournamentPrizeClaimMessage
                {
                    Placement = has ? l.Placement : -1,
                    Remaining = has ? ToEntries(l.Remaining) : new List<TournamentPrizeEntry>(),
                    RefusedKey = refusedKey,
                });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PrizeClaim.HostSend: " + e.Message); }
        }

        /// <summary>Host: repeat every ledger (rides the tournament heal broadcast, so a
        /// rejoined entrant gets theirs back).</summary>
        public static void HostSendAll()
        {
            if (s_host.Count == 0)
                return;
            var conns = new List<int>(s_host.Keys);
            for (int i = 0; i < conns.Count; i++)
                HostSend(conns[i]);
        }

        private static List<TournamentPrizeEntry> ToEntries(List<TournamentPrizeData> list)
        {
            var entries = new List<TournamentPrizeEntry>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                bool hasCard = p.m_CardData != null;
                entries.Add(new TournamentPrizeEntry { HasCard = hasCard, Card = hasCard ? p.m_CardData : null, ItemType = hasCard ? EItemType.None : p.m_ItemType, Count = p.m_Count });
            }
            return entries;
        }

        private static int FindCard(List<TournamentPrizeData> list, CardData card)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].m_CardData != null && list[i].m_Count > 0 && SameCard(list[i].m_CardData, card))
                    return i;
            return -1;
        }

        private static int FindItem(List<TournamentPrizeData> list, EItemType type, int count)
        {
            if (type == EItemType.None)
                return -1;
            for (int i = 0; i < list.Count; i++)
                if (list[i].m_CardData == null && list[i].m_ItemType == type && list[i].m_Count >= count)
                    return i;
            return -1;
        }

        private static void Consume(List<TournamentPrizeData> list, int i, int count)
        {
            list[i].m_Count -= count;
            if (list[i].m_Count <= 0)
                list.RemoveAt(i);
        }

        /// <summary>The identity vanilla uses when its NPC winner looks for its prize on the
        /// shelf (CardShelf.GetCustomerSpecificTargetCardCompartment).</summary>
        public static bool SameCard(CardData a, CardData b)
        {
            if (a == null || b == null)
                return false;
            return a.monsterType == b.monsterType && a.expansionType == b.expansionType && a.borderType == b.borderType
                && a.cardGrade == b.cardGrade && a.isDestiny == b.isDestiny && a.isFoil == b.isFoil;
        }

        // ================================================================ client

        private static int s_placement = -1;
        private static readonly List<TournamentPrizeEntry> s_remaining = new List<TournamentPrizeEntry>();
        // card claims sent up, by display key, so a bounce can take the right card back
        private static readonly Dictionary<int, CardData> s_claimedKeys = new Dictionary<int, CardData>();

        public static bool ClientEntitled => s_placement >= 0;
        public static int ClientPlacement => s_placement;
        public static bool ClientHasRemaining => s_remaining.Count > 0;

        public static void ClientApply(TournamentPrizeClaimMessage msg)
        {
            bool first = s_placement < 0 && msg.Placement >= 0;
            s_placement = msg.Placement;
            s_remaining.Clear();
            if (msg.Remaining != null)
                for (int i = 0; i < msg.Remaining.Count; i++)
                {
                    var e = msg.Remaining[i];
                    if (e != null && e.Count > 0 && (e.HasCard && e.Card != null || e.ItemType != EItemType.None))
                        s_remaining.Add(e);
                }
            if (msg.RefusedKey != 0)
            {
                CardData taken;
                if (s_claimedKeys.TryGetValue(msg.RefusedKey, out taken))
                {
                    s_claimedKeys.Remove(msg.RefusedKey);
                    try
                    {
                        VisitorBag.RemoveCard((int)taken.expansionType, CPlayerData.GetCardSaveIndex(taken), taken.isDestiny, 1, VisitorBag.GradeOf(taken)); // fv-908
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("PrizeClaim bounce: " + e.Message); }
                    HostOnlyFeatures.Notice("Tournament prize: the shop didn't release that card - it's back on the shelf");
                }
                CoopPlugin.Log.LogInfo($"PrizeClaim: card claim (key {msg.RefusedKey:X}) refused by the host");
            }
            if (msg.Placement < 0)
                s_claimedKeys.Clear();
            if (first)
                HostOnlyFeatures.Notice($"Tournament: you placed #{s_placement + 1} - " + (s_remaining.Count > 0 ? "your prize is on the shelf: " + Describe(s_remaining) : "no prize for that placement"));
            CoopPlugin.Log.LogInfo($"PrizeClaim: placed #{s_placement + 1}, remaining {Describe(s_remaining)}");
        }

        public static void ClientClear()
        {
            s_placement = -1;
            s_remaining.Clear();
            s_claimedKeys.Clear();
        }

        /// <summary>Client: this card is my prize - consume it (optimistic; the host confirms
        /// with a fresh mirror or bounces it).</summary>
        public static bool ClientClaimCard(CardData card, int key)
        {
            if (card == null)
                return false;
            for (int i = 0; i < s_remaining.Count; i++)
            {
                var e = s_remaining[i];
                if (!e.HasCard || e.Card == null || !SameCard(e.Card, card))
                    continue;
                if (--e.Count <= 0)
                    s_remaining.RemoveAt(i);
                s_claimedKeys[key] = card;
                return true;
            }
            return false;
        }

        /// <summary>Client: all <paramref name="count"/> of this item are my prize? (check only)</summary>
        public static bool ClientHasItems(EItemType type, int count)
        {
            return count > 0 && type != EItemType.None && IndexOfItem(type, count) >= 0;
        }

        /// <summary>Client: the host accepted the take - consume it if it was my prize.</summary>
        public static bool ClientClaimItems(EItemType type, int count)
        {
            int i = count > 0 && type != EItemType.None ? IndexOfItem(type, count) : -1;
            if (i < 0)
                return false;
            s_remaining[i].Count -= count;
            if (s_remaining[i].Count <= 0)
                s_remaining.RemoveAt(i);
            return true;
        }

        private static int IndexOfItem(EItemType type, int count)
        {
            for (int i = 0; i < s_remaining.Count; i++)
                if (!s_remaining[i].HasCard && s_remaining[i].ItemType == type && s_remaining[i].Count >= count)
                    return i;
            return -1;
        }

        /// <summary>"1 x Foo (foil), 2 x BasicPack" - for the refusal toast.</summary>
        public static string ClientDescribeRemaining()
        {
            return Describe(s_remaining);
        }

        public static string Describe(List<TournamentPrizeEntry> list)
        {
            if (list == null || list.Count == 0)
                return "nothing";
            var parts = new List<string>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                string what;
                if (e.HasCard && e.Card != null)
                    what = e.Card.monsterType + (e.Card.isFoil ? " (foil)" : "") + (e.Card.isDestiny ? " (destiny)" : "");
                else
                    what = e.ItemType.ToString();
                parts.Add($"{e.Count} x {what}");
            }
            return string.Join(", ", parts.ToArray());
        }
    }
}
