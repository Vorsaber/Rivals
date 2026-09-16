using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Read-only mirror of the shop's card-game decks (game 1.0). Decks live in
    /// <c>CPlayerData.m_DeckCompactCardDataList</c> plus <c>m_CurrentSelectedDeckIndex</c>,
    /// which nothing else syncs. The guest starts with the host's save, so decks match at
    /// join; this keeps them matching when the host edits one at the Workbench mid-session,
    /// so the guest sits down to a battle (<see cref="GuestBattle"/>) with the same deck the
    /// host would. The guest never writes decks (the editor is host-only).
    ///
    /// Host: hash the deck list every 2s, broadcast on change (heal every 20s).
    /// Client: replace the list in place - never while in a battle, since the engine reads
    /// the selected deck at SetPlayTable and again at DelayStart.
    /// </summary>
    public sealed class DeckSync : TickableCoopModule
    {
        public Action<INetMessage> BroadcastState;

        private readonly SnapshotGate _gate = new SnapshotGate(2f, 20f, -1.3f);

        public override string Name => nameof(DeckSync);

        public static void ApplyPatches(HarmonyLib.Harmony h)
        { /* no patches: a pure digest */
        }

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            if (!_gate.Due(dt))
                return;
            Guarded("host", () =>
            {
                var decks = CPlayerData.m_DeckCompactCardDataList;
                if (decks == null)
                    return;
                if (!_gate.ShouldSend(Hash(decks, CPlayerData.m_CurrentSelectedDeckIndex)))
                    return;
                BroadcastState?.Invoke(BuildState(decks, CPlayerData.m_CurrentSelectedDeckIndex));
            });
        }

        public override void OnFullyJoin(int connId)
        {
            _gate.Force();
        }

        public override void ForceResend()
        {
            _gate.Force();
        }

        public override void Reset()
        {
            _gate.Reset(-1.3f);
        }

        private static DeckStateMessage BuildState(List<DeckCompactCardDataList> decks, int selected)
        {
            var msg = new DeckStateMessage { SelectedIndex = selected };
            for (int i = 0; i < decks.Count && i < 32; i++)
            {
                var d = decks[i];
                var e = new DeckEntry();
                if (d != null)
                {
                    e.Name = d.deckName ?? "";
                    e.DeckBox = d.deckBoxIndex;
                    e.Playmat = d.playmatIndex;
                    var cards = d.compactCardDataAmountList;
                    if (cards != null)
                        for (int c = 0; c < cards.Count && c < 128; c++)
                        {
                            var cd = cards[c];
                            if (cd == null)
                                continue;
                            e.Cards.Add(new DeckCardEntry
                            {
                                Expansion = cd.expansionType,
                                Index = cd.cardSaveIndex,
                                Amount = cd.amount,
                                GradedIndex = cd.gradedCardIndex,
                                IsDestiny = cd.isDestiny,
                            });
                        }
                }
                msg.Decks.Add(e);
            }
            return msg;
        }

        private static int Hash(List<DeckCompactCardDataList> decks, int selected)
        {
            int h = 17;
            h = h * 31 + selected;
            h = h * 31 + decks.Count;
            for (int i = 0; i < decks.Count && i < 32; i++)
            {
                var d = decks[i];
                if (d == null)
                {
                    h = h * 31;
                    continue;
                }
                h = h * 31 + (d.deckName ?? "").GetHashCode();
                h = h * 31 + d.deckBoxIndex;
                h = h * 31 + d.playmatIndex;
                var cards = d.compactCardDataAmountList;
                if (cards == null)
                    continue;
                for (int c = 0; c < cards.Count && c < 128; c++)
                {
                    var cd = cards[c];
                    if (cd == null)
                        continue;
                    h = h * 31 + (int)cd.expansionType;
                    h = h * 31 + cd.cardSaveIndex;
                    h = h * 31 + cd.amount;
                    h = h * 31 + cd.gradedCardIndex;
                    h = h * 31 + (cd.isDestiny ? 1 : 0);
                }
            }
            return h;
        }

        // ================================================================ client

        public void ClientApplyState(DeckStateMessage message)
        {
            Guarded("apply", () =>
            {
                var mgr = CSingleton<PlayCardGameManager>.Instance;
                if (mgr != null && mgr.m_PlayTableGame != null && mgr.m_PlayTableGame.IsPlayTableGameMode())
                    return; // mid-battle: the engine is reading the selected deck
                var decks = CPlayerData.m_DeckCompactCardDataList;
                if (decks == null)
                    CPlayerData.m_DeckCompactCardDataList = decks = new List<DeckCompactCardDataList>();
                decks.Clear();
                for (int i = 0; i < message.Decks.Count; i++)
                {
                    var e = message.Decks[i];
                    var d = new DeckCompactCardDataList
                    {
                        deckName = e.Name ?? "",
                        deckBoxIndex = e.DeckBox,
                        playmatIndex = e.Playmat,
                    };
                    for (int c = 0; c < e.Cards.Count; c++)
                    {
                        var ce = e.Cards[c];
                        d.compactCardDataAmountList.Add(new CompactCardDataAmount
                        {
                            expansionType = ce.Expansion,
                            cardSaveIndex = ce.Index,
                            amount = ce.Amount,
                            gradedCardIndex = ce.GradedIndex,
                            isDestiny = ce.IsDestiny,
                        });
                    }
                    decks.Add(d);
                }
                int sel = message.SelectedIndex;
                CPlayerData.m_CurrentSelectedDeckIndex = (sel >= 0 && sel < decks.Count) ? sel : 0;
            });
        }
    }
}
