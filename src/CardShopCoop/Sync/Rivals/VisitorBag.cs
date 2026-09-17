using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// The carry-out bag (competitive mode, phase 2). A VISITOR is a co-op guest in a rival's
    /// shop whose own save sits untouched at home; anything they buy, win or trade there is
    /// recorded here and applied to their REAL save the next time it loads. Money is tracked
    /// against the wallet as it stood when they left home, so a purchase can be refused at
    /// the rival's register rather than discovered at home.
    ///
    /// Persisted to disk on every change (BepInEx/config/CardShopCoop.visitorbag.json) so a
    /// crash mid-visit loses nothing; applied exactly once, on the load of the save slot it
    /// was opened from, then cleared. This is the ONLY thing in the mod that writes a real
    /// save across machines - keep it idempotent and boring.
    /// </summary>
    public static class VisitorBag
    {
        [Serializable]
        public sealed class Item
        {
            public int ItemType;
            public int Count;
            public float Paid;
        }

        [Serializable]
        public sealed class Card
        {
            public int Expansion;
            public int Index;
            public bool IsDestiny;
            public int Amount;
            public float Paid;
        }

        [Serializable]
        public sealed class State
        {
            public bool Open;
            public int HomeSaveIndex = -1;
            /// <summary>Opened by a co-op GUEST: home is the team's shop (the world it plays
            /// in), not a save of its own; applied by handing the bag to that shop's host.</summary>
            public bool HomeIsTeam;
            /// <summary>Kept by the league lobby server as the TEAM's bag; this copy is a mirror.
            /// Ops go to the server, the server's state comes back.</summary>
            public bool Shared;
            public string Key = "";
            /// <summary>A shared bag we applied at home WITHOUT the lobby: told to the server on
            /// the next connect so it drops any copy it kept.</summary>
            public string AppliedOffline = "";
            public List<int> Out = new List<int>();   // server: members currently out with it
            public string HomeShop = "";
            public string VisitingShop = "";
            /// <summary>The cash carried. With <see cref="Withdrawn"/> it LEFT the till at
            /// departure and comes back (less what was spent, plus what was earned) at home.</summary>
            public double MoneyAtDeparture;
            public bool Withdrawn;
            public double Spent;
            public double Earned;
            public List<Item> Items = new List<Item>();
            public List<Card> Cards = new List<Card>();
            public List<string> Log = new List<string>();
            public string OpenedAt = "";
            // --- fv-682 b5-ledger-hardening begin
            /// <summary>Identity of this trip (8 hex, minted at Open; a piggybacker adopts the
            /// server's). Every write that could be resent - a deposit, a delivery, a recovery
            /// from a mirror - is applied once per TripId (<see cref="BagLedger"/>).</summary>
            public string TripId = "";
            /// <summary>Guest: handed to the team host (BagDeposit) and not yet acknowledged -
            /// kept, and resent, until the host's <see cref="Net.Messages.BagDepositAckMessage"/>.</summary>
            public bool DepositPending;
            /// <summary>Captain: this came from the lobby's "deliver" - acknowledge it once applied.</summary>
            public bool Delivered;
            /// <summary>Server: everyone out with it dropped their connection at this unix time
            /// (0 = not orphaned); delivered to the captain once the grace runs out.</summary>
            public long OrphanedAtUnix;
            /// <summary>Server: sent to this member (conn id, -1 = none) at this unix time and
            /// waiting for its "delivered"; resent until it comes.</summary>
            public int DeliverTo = -1;
            public long DeliverAtUnix;
            /// <summary>Captain: other delivered trips folded into this bag before it could be
            /// applied - each is marked applied and acknowledged with it.</summary>
            public List<string> MergedTrips = new List<string>();
            // --- fv-682 b5-ledger-hardening end

            /// <summary>A detached copy: the lobby server's ledger and its own mirror must
            /// never be the same object (an op would count twice).</summary>
            public State Clone()
            {
                // Json.NET, not JsonUtility: JsonUtility silently dropped the Items and Cards
                // lists of this nested type (2026-09-16: a bag came home with the money and
                // none of the five purchases) - the wire codec is Json.NET and proven
                return JsonConvert.DeserializeObject<State>(JsonConvert.SerializeObject(this)) ?? new State();
            }
        }

        public static State Current = new State();

        private static string PathOnDisk()
        {
            return Path.Combine(BepInEx.Paths.ConfigPath, "CardShopCoop.visitorbag.json");
        }

        public static void Load()
        {
            try
            {
                string p = PathOnDisk();
                if (File.Exists(p))
                {
                    Current = JsonConvert.DeserializeObject<State>(File.ReadAllText(p)) ?? new State();
                    if (Current.Open)
                        CoopPlugin.Log.LogInfo($"VisitorBag: an open bag from {Current.OpenedAt} (home slot {Current.HomeSaveIndex}, {Current.Items.Count} items, {Current.Cards.Count} cards, spent {Current.Spent:0.00}, earned {Current.Earned:0.00}) - applied when that save loads");
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("VisitorBag load: " + e.Message);
                Current = new State();
            }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(PathOnDisk(), JsonConvert.SerializeObject(Current, Formatting.Indented));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag save: " + e.Message); }
        }

        /// <summary>Called on the visitor's PC as they leave home (before the rival's world
        /// replaces the scratch slot): remember whose save this is and what they had.</summary>
        public static void Open(string visitingShop)
        {
            Open(visitingShop, 0, false);
        }

        /// <summary>Leave with <paramref name="cash"/> in the bag. Withdrawn = it has already
        /// left the till (the owner queued the ReduceCoin, or the host did for a teammate).</summary>
        public static void Open(string visitingShop, double cash, bool withdrawn)
        {
            if (Current.Open)
            {
                CoopPlugin.Log.LogWarning("VisitorBag: opening a new bag over an unapplied one - keeping the old contents");
                Current.VisitingShop = visitingShop ?? "";
                if (withdrawn && cash > 0)
                {
                    Current.MoneyAtDeparture += cash;
                    Current.Withdrawn = true;
                }
                Save();
                return;
            }
            Current = new State
            {
                Open = true,
                HomeSaveIndex = SafeSaveIndex(),
                HomeIsTeam = CoopCore.Role == CoopRole.Client,
                HomeShop = SafeShopName(),
                VisitingShop = visitingShop ?? "",
                MoneyAtDeparture = Math.Max(0, cash),
                Withdrawn = withdrawn,
                OpenedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                TripId = BagLedger.NewId(),
            };
            // in a league: the lobby keeps ONE bag per team - a teammate already out carries it,
            // we piggyback (same balance, same haul); otherwise ours becomes the team's
            Current.Shared = true; // fv-682: before the share - the server's own echo arrives synchronously
            if (!RivalsLobby.ShareBag(Current))
                Current.Shared = false;
            Save();
            CoopPlugin.Log.LogInfo($"VisitorBag: opened{(Current.Shared ? " (team bag via the lobby)" : "")} - home {(Current.HomeIsTeam ? "team shop '" + Current.HomeShop + "'" : "slot " + Current.HomeSaveIndex)}, cash {Current.MoneyAtDeparture:0.00}{(Current.Withdrawn ? " (taken from the till)" : "")}, visiting {Current.VisitingShop}");
        }

        /// <summary>The lobby's copy of the team bag arrived: mirror it (Open=false clears).</summary>
        public static void ApplyShared(State s)
        {
            if (s == null)
                return;
            if (!s.Open)
            {
                if (IsOpen && Current.Shared)
                {
                    Current = new State();
                    s_backPending = false;
                    Save();
                }
                return;
            }
            if (IsOpen && !Current.Shared)
            {
                // ours is a private bag (opened without the lobby, or waiting on a deposit ack):
                // the team's mirror must not paper over it
                CoopPlugin.Log.LogInfo($"VisitorBag: team bag state for trip {s.TripId} ignored - holding a private bag (trip {Current.TripId})");
                return;
            }
            s.Shared = true;
            Current = s;
            Save();
        }

        /// <summary>The lobby delivers the team's bag to us (the captain): everyone is home.
        /// Applied to the world we are in - or the next one we load - as long as it is ours.</summary>
        public static void Deliver(State s)
        {
            if (s == null)
                return;
            // fv-682: the server resends "deliver" until we say "delivered" - a copy of a trip
            // we already put into a world is acknowledged again and otherwise ignored
            if (BagLedger.TripApplied(s.TripId) || (IsOpen && Current.MergedTrips.Contains(s.TripId)))
            {
                CoopPlugin.Log.LogInfo($"VisitorBag: trip {s.TripId} delivered again - already {(BagLedger.TripApplied(s.TripId) ? "applied, acknowledging" : "merged into the bag we hold")}");
                if (BagLedger.TripApplied(s.TripId))
                    RivalsLobby.SendBagDelivered(s.TripId);
                return;
            }
            s_backPending = false;
            s.Shared = false;
            s.HomeIsTeam = false;
            s.HomeSaveIndex = -1; // any world of our own
            s.Open = true;
            s.Delivered = true;
            s.DepositPending = false;
            bool sameTrip = IsOpen && !string.IsNullOrEmpty(s.TripId) && Current.TripId == s.TripId;
            if (!sameTrip && IsOpen && !Current.Shared && !Current.DepositPending)
            {
                // a private bag of ours is still unapplied: the delivery rides in with it, one apply
                CoopPlugin.Log.LogInfo($"VisitorBag: trip {s.TripId} delivered onto our unapplied bag (trip {Current.TripId}) - merged");
                MergeInto(Current, s);
                if (Current.Delivered && !string.IsNullOrEmpty(Current.TripId))
                    Current.MergedTrips.Add(Current.TripId); // an earlier delivery, acked with this one
                Current.MergedTrips.AddRange(s.MergedTrips);
                Current.TripId = s.TripId;
                Current.Delivered = true;
                Current.HomeIsTeam = false;
                Current.HomeSaveIndex = -1;
            }
            else
                Current = s;
            Save();
            CoopPlugin.Log.LogInfo($"VisitorBag: team bag delivered (trip {s.TripId}) - net {s.Earned - s.Spent:0.00}, {s.Cards.Count} card line(s), {s.Items.Count} item line(s)");
            ApplyIfHome();
        }

        /// <summary>Everything in <paramref name="from"/> goes into <paramref name="into"/>:
        /// cash, spend, earnings, every card and item line, the log.</summary>
        public static void MergeInto(State into, State from)
        {
            if (from.Withdrawn && from.MoneyAtDeparture > 0)
            {
                into.MoneyAtDeparture += from.MoneyAtDeparture;
                into.Withdrawn = true;
            }
            into.Spent += from.Spent;
            into.Earned += from.Earned;
            foreach (var it in from.Items)
                AddItemTo(into, it.ItemType, it.Count, it.Paid);
            foreach (var c in from.Cards)
                AddCardTo(into, c.Expansion, c.Index, c.IsDestiny, c.Amount, c.Paid);
            into.Log.AddRange(from.Log);
        }

        public static bool IsOpen => Current != null && Current.Open;

        /// <summary>What the visitor can still spend.</summary>
        public static double Balance => IsOpen ? Current.MoneyAtDeparture - Current.Spent + Current.Earned : 0;

        public static bool TrySpend(double amount, string what, bool force = false)
        {
            if (!IsOpen || amount < 0)
                return false;
            if (!force && Balance < amount)
                return false;
            Current.Spent += amount;
            Current.Log.Add($"spent {amount:0.00} on {what}");
            Save();
            if (Current.Shared)
                RivalsLobby.SendBagOp(new Net.Messages.RivalsBagMessage { Op = "spend", Amount = amount, What = what ?? "" });
            return true;
        }

        public static void Earn(double amount, string what)
        {
            if (!IsOpen || amount <= 0)
                return;
            Current.Earned += amount;
            Current.Log.Add($"earned {amount:0.00} from {what}");
            Save();
            if (Current.Shared)
                RivalsLobby.SendBagOp(new Net.Messages.RivalsBagMessage { Op = "earn", Amount = amount, What = what ?? "" });
        }

        public static void AddItem(EItemType type, int count, float paid)
        {
            if (!IsOpen || count <= 0)
                return;
            AddItemTo(Current, (int)type, count, paid);
            Current.Log.Add($"item {type} x{count}");
            Save();
            if (Current.Shared)
                RivalsLobby.SendBagOp(new Net.Messages.RivalsBagMessage { Op = "item", ItemType = (int)type, Count = count, Paid = paid });
        }

        public static void AddItemTo(State s, int type, int count, float paid)
        {
            var existing = s.Items.Find(i => i.ItemType == type);
            if (existing != null)
            {
                existing.Count += count;
                existing.Paid += paid;
            }
            else
                s.Items.Add(new Item { ItemType = type, Count = count, Paid = paid });
        }

        public static void AddCardTo(State s, int expansion, int index, bool destiny, int amount, float paid)
        {
            var existing = s.Cards.Find(c => c.Expansion == expansion && c.Index == index && c.IsDestiny == destiny);
            if (existing != null)
            {
                existing.Amount += amount;
                existing.Paid += paid;
                if (existing.Amount <= 0)
                    s.Cards.Remove(existing); // a trade gave it away
            }
            else if (amount > 0)
                s.Cards.Add(new Card { Expansion = expansion, Index = index, IsDestiny = destiny, Amount = amount, Paid = paid });
        }

        /// <summary>A card leaves the bag (traded away to the shop).</summary>
        public static void RemoveCard(int expansion, int index, bool destiny, int amount)
        {
            if (!IsOpen || amount <= 0)
                return;
            AddCardTo(Current, expansion, index, destiny, -amount, 0f);
            Current.Log.Add($"card {index}/{expansion} x{amount} traded away");
            Save();
            if (Current.Shared)
                RivalsLobby.SendBagOp(new Net.Messages.RivalsBagMessage { Op = "card", Expansion = expansion, Index = index, IsDestiny = destiny, Count = -amount, Paid = 0f });
        }

        public static void AddCard(CardData cd, int amount, float paid)
        {
            if (!IsOpen || cd == null || amount <= 0)
                return;
            int index;
            try
            {
                index = CPlayerData.GetCardSaveIndex(cd);
            }
            catch { return; }
            AddCardTo(Current, (int)cd.expansionType, index, cd.isDestiny, amount, paid);
            Current.Log.Add($"card {cd.monsterType} ({cd.expansionType}) x{amount}");
            Save();
            if (Current.Shared)
                RivalsLobby.SendBagOp(new Net.Messages.RivalsBagMessage { Op = "card", Expansion = (int)cd.expansionType, Index = index, IsDestiny = cd.isDestiny, Count = amount, Paid = paid });
        }

        /// <summary>Home again, own save loaded: apply everything once and close the bag.
        /// Items arrive as a delivery box at the door (the game has no player inventory for
        /// items); cards go straight into the collection; the wallet takes the net.</summary>
        public static void ApplyIfHome()
        {
            if (!IsOpen)
                return;
            if (CoopCore.IsVisiting)
                return; // the rival's world, not home
            if (Current.Shared)
            {
                // the team's bag lives on the lobby: tell it we are home; it delivers to the
                // captain when the last of us is
                if (RivalsLobby.SendBagBack())
                {
                    s_backPending = true;
                    s_backSentAt = Time.unscaledTime;
                    CoopPlugin.Log.LogInfo($"VisitorBag: home - told the lobby (trip {Current.TripId})");
                    return;
                }
                // no lobby: this mirror is the only copy we can be sure of - apply it here and
                // tell the server on the next connect (it drops any copy it kept)
                CoopPlugin.Log.LogWarning("VisitorBag: home without the lobby - applying the mirror here");
                Current.Shared = false;
                if (Current.HomeIsTeam)
                {
                    DepositToTeam(); // fv-682: the bag stays until acked; the ack records the offline apply
                    return;
                }
                // fall through: the owner's own world takes it
            }
            if (Current.HomeIsTeam)
            {
                DepositToTeam();
                return;
            }
            var gmh = CSingleton<CGameManager>.Instance;
            if (gmh == null || !gmh.m_IsGameLevel)
                return;
            int slot = SafeSaveIndex();
            if (Current.HomeSaveIndex >= 0 && slot != Current.HomeSaveIndex)
            {
                CoopPlugin.Log.LogInfo($"VisitorBag: save slot {slot} loaded, bag belongs to slot {Current.HomeSaveIndex} - waiting");
                return;
            }
            if (CoopCore.Role == CoopRole.Client)
                return; // the rival's world in the scratch slot, not home
            string trip = Current.TripId ?? "";
            string appliedKey = Current.Delivered ? "" : trip; // fv-682: an offline apply is reported by trip; a delivery is acked instead
            bool delivered = Current.Delivered;
            var merged = new List<string>(Current.MergedTrips);
            try
            {
                ApplyContents(HomeNet(Current), Current.Cards, Current.Items, SafeShopName(), Current.VisitingShop);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag apply: " + e.Message); }
            BagLedger.MarkTripApplied(trip); // written with the next game save
            foreach (string t in merged)
                BagLedger.MarkTripApplied(t);
            Current = new State { AppliedOffline = appliedKey };
            Save();
            if (delivered)
            {
                RivalsLobby.SendBagDelivered(trip);
                foreach (string t in merged)
                    RivalsLobby.SendBagDelivered(t);
            }
        }

        /// <summary>Lobby just connected: if we applied a shared bag while it was unreachable,
        /// say so (by trip id), so the server's kept copy is not delivered on top.</summary>
        public static void ReportOfflineApply()
        {
            if (Current == null || string.IsNullOrEmpty(Current.AppliedOffline))
                return;
            if (RivalsLobby.SendBagApplied(Current.AppliedOffline))
            {
                Current.AppliedOffline = "";
                Save();
            }
        }

        /// <summary>What the till gets back: the cash taken along (if it left the till) less
        /// what was spent, plus what was earned.</summary>
        public static double HomeNet(State s)
        {
            return (s.Withdrawn ? s.MoneyAtDeparture : 0) + s.Earned - s.Spent;
        }

        /// <summary>Put a bag's contents into the running world: the wallet takes the net,
        /// cards go into the collection, items arrive as a delivery box at the door.</summary>
        private static void ApplyContents(double net, List<Card> cards, List<Item> items, string who, string visited)
        {
            if (net > 0.005)
                CEventManager.QueueEvent(new CEventPlayer_AddCoin((float)net));
            else if (net < -0.005)
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)(-net)));
            int nCards = 0;
            foreach (var c in cards)
            {
                var cd = CPlayerData.GetCardData(c.Index, (ECardExpansionType)c.Expansion, c.IsDestiny);
                if (cd == null || c.Amount <= 0)
                    continue;
                CPlayerData.AddCard(cd, c.Amount);
                nCards += c.Amount;
            }
            int nItems = 0;
            foreach (var it in items)
            {
                if (it.Count <= 0)
                    continue;
                try
                {
                    RestockManager.SpawnPackageBoxItem((EItemType)it.ItemType, it.Count, false);
                    nItems += it.Count;
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning($"VisitorBag: item {(EItemType)it.ItemType} could not be delivered: {e.Message}"); }
            }
            string summary = $"{who} back from {visited}: till {(net >= 0 ? "+" : "")}{net:0.00} (cash back incl.), {nCards} card(s) into the collection, {nItems} item(s) in a box at the door";
            CoopPlugin.Log.LogInfo("VisitorBag: applied - " + summary);
            HostOnlyFeatures.Notice("Co-op: " + summary);
        }

        /// <summary>A co-op guest's bag: once we stand in our team's shop again (a client world
        /// whose shop name is the one we left), hand it to the host and close it.</summary>
        private static void DepositToTeam()
        {
            if (CoopCore.Role != CoopRole.Client || CoopCore.Instance == null || CoopCore.IsVisiting)
                return;
            string shop = SafeShopName();
            if (!string.IsNullOrEmpty(Current.HomeShop) && shop != Current.HomeShop)
            {
                CoopPlugin.Log.LogInfo($"VisitorBag: in '{shop}', bag belongs to team shop '{Current.HomeShop}' - waiting");
                return;
            }
            if (Current.DepositPending && Time.unscaledTime - s_depositSentAt < DepositRetrySec)
                return; // sent, waiting for the ack
            if (string.IsNullOrEmpty(Current.TripId))
                Current.TripId = BagLedger.NewId(); // a bag from before trips had ids
            bool resend = Current.DepositPending;
            var dep = new Net.Messages.BagDepositMessage
            {
                From = CoopCore.Instance.EffectivePlayerName,
                VisitedShop = Current.VisitingShop,
                Net = HomeNet(Current),
                DepositId = Current.TripId,
            };
            foreach (var it in Current.Items)
            {
                dep.ItemTypes.Add(it.ItemType);
                dep.ItemCounts.Add(it.Count);
            }
            foreach (var c in Current.Cards)
            {
                dep.CardExpansions.Add(c.Expansion);
                dep.CardIndices.Add(c.Index);
                dep.CardDestiny.Add(c.IsDestiny);
                dep.CardAmounts.Add(c.Amount);
            }
            CoopCore.Instance.SendBagDeposit(dep);
            s_depositSentAt = Time.unscaledTime;
            // fv-682: the bag stays until the host acknowledges this DepositId - a session that
            // drops between here and the host's apply used to lose it (the bag was cleared on send)
            if (!resend)
            {
                Current.DepositPending = true;
                Save();
                CoopPlugin.Log.LogInfo($"VisitorBag: handed to the team shop (deposit {dep.DepositId}) - net {dep.Net:0.00}, {Current.Cards.Count} card line(s), {Current.Items.Count} item line(s) - waiting for the ack");
            }
            else
                CoopPlugin.Log.LogInfo($"VisitorBag: deposit {dep.DepositId} resent - no ack yet");
        }

        // --- fv-682 b5-ledger-hardening begin
        private const float DepositRetrySec = 5f;
        private const float BackRetrySec = 10f;
        private static float s_depositSentAt = -100f;
        private static bool s_backPending;
        private static float s_backSentAt;

        /// <summary>Guest: the host applied (or had applied) our deposit - now the bag may go.</summary>
        public static void OnDepositAck(Net.Messages.BagDepositAckMessage ack)
        {
            if (ack == null || !IsOpen || !Current.DepositPending)
                return;
            if (Current.TripId != ack.DepositId)
            {
                CoopPlugin.Log.LogInfo($"VisitorBag: ack for deposit {ack.DepositId} ignored - ours is {Current.TripId}");
                return;
            }
            CoopPlugin.Log.LogInfo($"VisitorBag: deposit {ack.DepositId} acknowledged by the shop{(ack.Applied ? "" : " (not applied)")} - bag closed");
            HostOnlyFeatures.Notice($"Co-op: your bag from {Current.VisitingShop} went to the shop");
            // a team bag deposited without the lobby: the lobby learns on the next connect that this trip is done
            Current = new State { AppliedOffline = !string.IsNullOrEmpty(Current.Key) ? ack.DepositId : "" };
            Save();
        }

        /// <summary>Not on the way out and not in a rival's world: "home" for the purposes of
        /// resending a "back" or a deposit.</summary>
        private static bool AtHome()
        {
            return !CoopCore.IsVisiting && !CoopCore.JoiningAsVisitor && !RivalsLobby.Departing;
        }

        /// <summary>Lobby (re)connected: say what the lobby may have missed - an offline apply,
        /// a trip we are still out on (rejoin, same TripId), or that we are home.</summary>
        public static void OnLobbyConnected()
        {
            ReportOfflineApply();
            if (!IsOpen || !Current.Shared)
                return;
            if (!AtHome())
            {
                if (RivalsLobby.ShareBag(Current))
                    CoopPlugin.Log.LogInfo($"VisitorBag: lobby back - rejoined trip {Current.TripId}");
                return;
            }
            if (RivalsLobby.SendBagBack())
            {
                s_backPending = true;
                s_backSentAt = Time.unscaledTime;
                CoopPlugin.Log.LogInfo($"VisitorBag: lobby back - told it we are home (trip {Current.TripId})");
            }
        }

        /// <summary>Every tick: a deposit or a "back" the other side has not answered goes again.</summary>
        private static void TickRetries()
        {
            if (!IsOpen)
            {
                s_backPending = false;
                return;
            }
            if (Current.DepositPending && !Current.Shared && CoopCore.Role == CoopRole.Client && AtHome())
            {
                DepositToTeam(); // rate-limited inside
                return;
            }
            if (s_backPending && Current.Shared && AtHome() && Time.unscaledTime - s_backSentAt >= BackRetrySec)
            {
                s_backSentAt = Time.unscaledTime;
                if (RivalsLobby.SendBagBack())
                    CoopPlugin.Log.LogInfo($"VisitorBag: 'back' resent for trip {Current.TripId} - no answer yet");
            }
        }
        // --- fv-682 b5-ledger-hardening end

        /// <summary>Host: a teammate's bag arrives - apply it to this shop.</summary>
        public static void HostApplyDeposit(Net.Messages.BagDepositMessage dep, Action<Net.Messages.BagDepositAckMessage> ack = null)
        {
            // fv-682: the guest resends until acknowledged - one apply per DepositId, an ack every time
            string id = dep.DepositId ?? "";
            if (BagLedger.DepositApplied(id))
            {
                CoopPlugin.Log.LogInfo($"VisitorBag: deposit {id} from {dep.From} arrived again - already applied, acknowledging");
                ack?.Invoke(new Net.Messages.BagDepositAckMessage { DepositId = id, Applied = true });
                return;
            }
            try
            {
                var cards = new List<Card>();
                for (int i = 0; i < dep.CardIndices.Count; i++)
                    cards.Add(new Card
                    {
                        Expansion = i < dep.CardExpansions.Count ? dep.CardExpansions[i] : 0,
                        Index = dep.CardIndices[i],
                        IsDestiny = i < dep.CardDestiny.Count && dep.CardDestiny[i],
                        Amount = i < dep.CardAmounts.Count ? dep.CardAmounts[i] : 0,
                    });
                var items = new List<Item>();
                for (int i = 0; i < dep.ItemTypes.Count; i++)
                    items.Add(new Item { ItemType = dep.ItemTypes[i], Count = i < dep.ItemCounts.Count ? dep.ItemCounts[i] : 0 });
                ApplyContents(dep.Net, cards, items, dep.From ?? "a teammate", dep.VisitedShop ?? "a rival");
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag deposit: " + e.Message); }
            BagLedger.MarkDepositApplied(id); // written with the next game save
            ack?.Invoke(new Net.Messages.BagDepositAckMessage { DepositId = id, Applied = true });
        }

        /// <summary>The save SLOT the game last loaded or saved (CSaveLoad.Load/Save patches).
        /// Not CPlayerData.m_SaveIndex, which is a save-generation counter for cloud conflict
        /// resolution, not a slot.</summary>
        public static int LastSlot = -1;
        private static bool s_applyPending;
        private static float s_levelUpAt = -1f;

        public static void ApplyPatches(HarmonyLib.Harmony h)
        {
            try
            {
                var load = HarmonyLib.AccessTools.Method(typeof(CSaveLoad), "Load", new[] { typeof(int) });
                if (load != null)
                    h.Patch(load, postfix: new HarmonyLib.HarmonyMethod(typeof(VisitorBag), nameof(LoadPostfix)));
                var save = HarmonyLib.AccessTools.Method(typeof(CSaveLoad), "Save", new[] { typeof(int), typeof(bool) });
                if (save != null)
                    h.Patch(save, prefix: new HarmonyLib.HarmonyMethod(typeof(VisitorBag), nameof(SavePrefix)));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag patches: " + e.Message); }
        }

        public static void LoadPostfix(int slotIndex, bool __result)
        {
            if (!__result)
                return;
            LastSlot = slotIndex;
            s_applyPending = IsOpen;
        }

        public static void SavePrefix(int saveSlotIndex)
        {
            if (CoopCore.Role != CoopRole.Client)
                LastSlot = saveSlotIndex;
            BagLedger.FlushWithSave(); // fv-682: the applied registers land with the save they describe
        }

        /// <summary>Ticked by the lobby MonoBehaviour: once the loaded world is up, apply.</summary>
        public static void Tick()
        {
            try { TickRetries(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("VisitorBag retry: " + e.Message); }
            if (!s_applyPending)
                return;
            try
            {
                var gm = CSingleton<CGameManager>.Instance;
                if (gm == null || !gm.m_IsGameLevel || !GameInstance.m_FinishedSavefileLoading)
                    return;
                if (s_levelUpAt < 0f)
                {
                    s_levelUpAt = Time.unscaledTime;
                    return;
                }
                if (Time.unscaledTime - s_levelUpAt < 2f)
                    return; // let the world settle (a client's shop name arrives with the load)
                s_applyPending = false;
                s_levelUpAt = -1f;
                ApplyIfHome();
            }
            catch (Exception e)
            {
                s_applyPending = false;
                CoopPlugin.Log.LogWarning("VisitorBag tick: " + e.Message);
            }
        }

        private static int SafeSaveIndex()
        {
            return LastSlot;
        }

        private static double SafeMoney()
        {
            try
            {
                return CPlayerData.m_CoinAmountDouble;
            }
            catch { return 0; }
        }

        private static string SafeShopName()
        {
            try
            {
                return CPlayerData.PlayerName ?? "";
            }
            catch { return ""; }
        }
    }
}
