using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Spectator mirror of the HOST's card battle (game 1.0 playable TCG). The guest cannot
    /// play (<see cref="HostOnlyFeatures"/>); this lets them watch.
    ///
    /// RESEARCH NOTE (decompiled/PlayTableGame.cs, PlayCardSet.cs, PlayCardSetUI.cs): a
    /// battle is PlayTableGame's own prop group <c>m_Grp</c>, moved onto the table by
    /// <c>SetPlayTable</c> (position/rotation copied from the table, +180° yaw when the
    /// player sits on side B, then <c>SetIsSideA</c> on both PlayCardSets). The board
    /// pieces are ordinary InteractableCard3d objects parented to slot transforms
    /// (<c>m_GuardianCardPosList</c>, <c>m_ElementAreaPosList[area].transformList</c>) at
    /// local scale 1.6; numbers are TextMeshPro fields on the world-space
    /// <c>PlayCardSetUI</c>. Every per-frame script under the group early-returns unless
    /// <c>PlayTableGame.IsPlayTableGameMode()</c>, so activating the group on the guest
    /// with the mode flag left false is inert: pure visuals on existing objects, same
    /// principle as PlayTableSync.
    ///
    /// Host: while in battle mode, digest both sides every 0.5s and send on change (heal
    /// every 6s). Leaving battle mode sends one Active=false tombstone.
    /// Client: spawn/replace card3ds per slot using the game's own recipe
    /// (PlayCardSet.SpawnCard3d, lines ~2981-3001), paint the numbers, and tear everything
    /// down on the tombstone or on Reset.
    /// </summary>
    public sealed class BattleSync : TickableCoopModule
    {
        public Action<INetMessage> BroadcastState;

        private readonly SnapshotGate _gate = new SnapshotGate(0.5f, 6f, -3.1f);
        private bool _hostWasActive;

        // ---- client mirror state ----
        private bool _clientActive;
        private int _clientTable = -1;
        private bool _clientSideA;
        private readonly Dictionary<string, InteractableCard3d> _cards = new Dictionary<string, InteractableCard3d>();
        private readonly Dictionary<string, string> _cardSig = new Dictionary<string, string>();
        private readonly List<Item> _giftProps = new List<Item>();
        private string _giftSig = "";
        private bool _loggedNoGame;

        private static readonly FieldInfo FiSpawnedItems = Util.ReflectionSurface.OptionalField(typeof(PlayTableGame), "m_SpawnedItemList");
        private static readonly FieldInfo FiCurrentTable = Util.ReflectionSurface.OptionalField(typeof(PlayTableGame), "m_CurrentInteractablePlayTable");
        private static readonly FieldInfo FiIsSideA = Util.ReflectionSurface.OptionalField(typeof(PlayTableGame), "m_IsSideA");

        public override string Name => nameof(BattleSync);

        public static void ApplyPatches(HarmonyLib.Harmony h)
        { /* no patches: a pure digest */
        }

        private static PlayTableGame Game()
        {
            var mgr = CSingleton<PlayCardGameManager>.Instance;
            return mgr != null ? mgr.m_PlayTableGame : null;
        }

        private static ShelfManager Sm() => CSingleton<ShelfManager>.Instance;

        // ================================================================ host

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame)
                return;
            if (!_gate.Due(dt))
                return;
            Guarded("host", () =>
            {
                var game = Game();
                bool active = game != null && game.IsPlayTableGameMode();
                if (!active)
                {
                    if (_hostWasActive)
                    {
                        _hostWasActive = false;
                        _gate.Force();
                        _gate.ShouldSend(0);
                        BroadcastState?.Invoke(new BattleStateMessage { Active = false });
                    }
                    return;
                }
                _hostWasActive = true;
                var msg = BuildState(game);
                if (msg == null)
                    return;
                if (!_gate.ShouldSend(Hash(msg)))
                    return;
                BroadcastState?.Invoke(msg);
            });
        }

        private static BattleStateMessage BuildState(PlayTableGame game)
        {
            var sm = Sm();
            var table = FiCurrentTable != null ? FiCurrentTable.GetValue(game) as InteractablePlayTable : null;
            int index = (sm != null && sm.m_PlayTableList != null && table != null) ? sm.m_PlayTableList.IndexOf(table) : -1;
            if (index < 0 || index > 255)
                return null; // battle on a table we cannot name: nothing to mirror
            bool sideA = FiIsSideA == null || (bool)FiIsSideA.GetValue(game);
            var msg = new BattleStateMessage
            {
                Active = true,
                TableIndex = (byte)index,
                HostSideA = sideA,
                Host = ReadSide(game.m_PlayCardSetPlayer),
                Enemy = ReadSide(game.m_PlayCardSetEnemy),
            };
            var gifts = FiSpawnedItems != null ? FiSpawnedItems.GetValue(game) as List<Item> : null;
            if (gifts != null)
                for (int i = 0; i < gifts.Count && i < 8; i++)
                    if (gifts[i] != null && gifts[i].gameObject.activeSelf)
                        msg.GiftItems.Add(gifts[i].GetItemType());
            return msg;
        }

        private static BattleSideEntry ReadSide(PlayCardSet set)
        {
            var e = new BattleSideEntry();
            if (set == null)
                return e;
            e.HP = set.GetCurrentHP();
            e.ShieldHP = set.GetTamerShieldHP();
            e.DeckCount = set.m_DeckCardDataList != null ? set.m_DeckCardDataList.Count : 0;
            e.DiscardCount = set.m_DiscardCard3dList != null ? set.m_DiscardCard3dList.Count : 0;
            e.HandCount = set.m_HoldCard3dList != null ? set.m_HoldCard3dList.Count : 0;
            var g = set.m_GuardianCard3dList;
            if (g != null)
                for (int i = 0; i < g.Count && i < 8; i++)
                {
                    var cd = Read(g[i]);
                    if (cd != null)
                        e.Guardians.Add(cd);
                }
            var areas = set.m_ElementAreaCardList;
            if (areas != null)
                for (int a = 0; a < areas.Count && a < 16; a++)
                {
                    var stack = areas[a] != null ? areas[a].cardList : null;
                    if (stack == null || stack.Count == 0)
                        continue;
                    var ae = new BattleAreaEntry { Area = (byte)a };
                    for (int i = 0; i < stack.Count && i < 8; i++)
                    {
                        var cd = Read(stack[i]);
                        if (cd != null)
                            ae.Stack.Add(cd);
                    }
                    if (ae.Stack.Count > 0)
                        e.Areas.Add(ae);
                }
            return e;
        }

        private static CardData Read(InteractableCard3d c)
        {
            try
            {
                if (c == null || c.m_Card3dUI == null || c.m_Card3dUI.m_CardUI == null)
                    return null;
                return c.m_Card3dUI.m_CardUI.GetCardData();
            }
            catch { return null; }
        }

        private static string Sig(CardData c)
        {
            if (c == null)
                return "-";
            return ((int)c.expansionType) + ":" + ((int)c.monsterType) + ":" + ((int)c.borderType)
                + ":" + (c.isFoil ? 1 : 0) + (c.isDestiny ? 1 : 0) + (c.isChampionCard ? 1 : 0);
        }

        private static int Hash(BattleStateMessage m)
        {
            int h = 17;
            h = h * 31 + m.TableIndex;
            h = h * 31 + (m.HostSideA ? 1 : 0);
            h = HashSide(h, m.Host);
            h = HashSide(h, m.Enemy);
            for (int i = 0; i < m.GiftItems.Count; i++)
                h = h * 31 + (int)m.GiftItems[i];
            return h;
        }

        private static int HashSide(int h, BattleSideEntry s)
        {
            h = h * 31 + s.HP;
            h = h * 31 + s.ShieldHP;
            h = h * 31 + s.DeckCount;
            h = h * 31 + s.DiscardCount;
            h = h * 31 + s.HandCount;
            for (int i = 0; i < s.Guardians.Count; i++)
                h = h * 31 + Sig(s.Guardians[i]).GetHashCode();
            for (int a = 0; a < s.Areas.Count; a++)
            {
                h = h * 31 + s.Areas[a].Area;
                for (int i = 0; i < s.Areas[a].Stack.Count; i++)
                    h = h * 31 + Sig(s.Areas[a].Stack[i]).GetHashCode();
            }
            return h;
        }

        // ================================================================ client

        public void ClientApplyState(BattleStateMessage message)
        {
            Guarded("apply", () => ClientApplyInner(message));
        }

        private void ClientApplyInner(BattleStateMessage message)
        {
            var game = Game();
            if (game == null || game.m_Grp == null)
            {
                if (!_loggedNoGame)
                {
                    _loggedNoGame = true;
                    CoopPlugin.Log.LogWarning("BattleSync: PlayTableGame not available on this client; battles will not be mirrored");
                }
                return;
            }
            if (game.IsPlayTableGameMode())
                return; // this client is somehow in its own battle - never touch a live board
            if (!message.Active)
            {
                TearDown(game);
                return;
            }
            var sm = Sm();
            var tables = sm != null ? sm.m_PlayTableList : null;
            if (tables == null || message.TableIndex >= tables.Count || tables[message.TableIndex] == null)
            {
                TearDown(game);
                return;
            }
            var table = tables[message.TableIndex];

            if (!_clientActive || _clientTable != message.TableIndex || _clientSideA != message.HostSideA)
            {
                TearDown(game);
                // SetPlayTable's placement recipe, minus everything that enters battle mode
                game.transform.position = table.transform.position;
                var rot = table.transform.rotation;
                if (!message.HostSideA)
                {
                    var eul = rot.eulerAngles;
                    eul += new Vector3(0f, 180f, 0f);
                    rot.eulerAngles = eul;
                }
                game.transform.rotation = rot;
                if (game.m_PlayCardSetPlayer != null)
                    game.m_PlayCardSetPlayer.SetIsSideA(message.HostSideA);
                if (game.m_PlayCardSetEnemy != null)
                    game.m_PlayCardSetEnemy.SetIsSideA(message.HostSideA);
                game.m_Grp.SetActive(true);
                _clientActive = true;
                _clientTable = message.TableIndex;
                _clientSideA = message.HostSideA;
            }

            ApplySide("h", game.m_PlayCardSetPlayer, message.Host);
            ApplySide("e", game.m_PlayCardSetEnemy, message.Enemy);
            ApplyGifts(game, message.GiftItems);
        }

        private void ApplySide(string key, PlayCardSet set, BattleSideEntry e)
        {
            if (set == null)
                return;
            var ui = set.m_PlayCardSetUI;
            if (ui != null)
            {
                try
                {
                    ui.UpdateHp(e.HP);
                    ui.UpdateShieldHp(e.ShieldHP);
                    ui.SetDeckCountText(e.DeckCount);
                    ui.SetDiscardPileCountText(e.DiscardCount);
                }
                catch (Exception ex) { CoopPlugin.Log.LogWarning("BattleSync ui: " + ex.Message); }
            }
            // guardians
            var gpos = set.m_GuardianCardPosList;
            int gcount = gpos != null ? gpos.Count : 0;
            for (int i = 0; i < gcount; i++)
                Place(key + ":g" + i, i < e.Guardians.Count ? e.Guardians[i] : null, gpos[i]);
            // element areas: the game stacks an evolution chain on one slot list; mirror
            // each card onto its own transform in that list (top of stack = last)
            var areas = set.m_ElementAreaPosList;
            int acount = areas != null ? areas.Count : 0;
            for (int a = 0; a < acount; a++)
            {
                var slots = areas[a] != null ? areas[a].transformList : null;
                int scount = slots != null ? slots.Count : 0;
                BattleAreaEntry ae = null;
                for (int k = 0; k < e.Areas.Count; k++)
                if (e.Areas[k].Area == a)
                {
                    ae = e.Areas[k];
                    break;
                }
                for (int s = 0; s < scount; s++)
                    Place(key + ":a" + a + ":" + s, (ae != null && s < ae.Stack.Count) ? ae.Stack[s] : null, slots[s]);
            }
        }

        /// <summary>Put <paramref name="card"/> (or nothing) on <paramref name="slot"/>,
        /// reusing the existing card3d when it already shows the same card.</summary>
        private void Place(string slotKey, CardData card, Transform slot)
        {
            string want = Sig(card);
            _cardSig.TryGetValue(slotKey, out var have);
            if (have == want)
                return;
            if (_cards.TryGetValue(slotKey, out var old))
            {
                Destroy(old);
                _cards.Remove(slotKey);
            }
            _cardSig[slotKey] = want;
            if (card == null || slot == null)
                return;
            var c3 = Spawn(card, slot);
            if (c3 != null)
                _cards[slotKey] = c3;
        }

        /// <summary>The game's own board-card recipe (PlayCardSet.SpawnCard3d): pooled
        /// Card3dUIGroup + a Card3d interactable, collision off, parented to the slot at
        /// the board's 1.6 scale. Nothing here registers with ShelfManager's save state.</summary>
        private static InteractableCard3d Spawn(CardData card, Transform parent)
        {
            try
            {
                var spawner = CSingleton<Card3dUISpawner>.Instance;
                if (spawner == null)
                    return null;
                Card3dUIGroup cardUI = spawner.GetCardUI();
                var obj = ShelfManager.SpawnInteractableObject(EObjectType.Card3d);
                var c3 = obj != null ? obj.GetComponent<InteractableCard3d>() : null;
                if (cardUI == null || c3 == null)
                    return null;
                cardUI.m_IgnoreCulling = true;
                cardUI.m_CardUI.SetFoilCullListVisibility(isActive: true);
                cardUI.m_CardUI.ResetFarDistanceCull();
                cardUI.m_CardUIAnimGrp.gameObject.SetActive(true);
                cardUI.m_CardUI.SetCardUI(card);
                cardUI.m_CardUI.SetFoilMaterialListFromSettingData(isWorldView: false);
                cardUI.m_CardUI.SetFoilBlendedMaterialListFromSettingData(isWorldView: false);
                cardUI.transform.position = c3.transform.position;
                cardUI.transform.rotation = c3.transform.rotation;
                c3.SetCardUIFollow(cardUI);
                c3.SetEnableCollision(isEnable: false);
                c3.transform.parent = parent;
                c3.transform.localPosition = Vector3.zero;
                c3.transform.localRotation = Quaternion.identity;
                c3.transform.localScale = Vector3.one * 1.6f;
                c3.SetTargetLocalScale(Vector3.one * 1.6f);
                c3.SetTargetLocalPos(Vector3.zero);
                c3.SetTargetRotation(Quaternion.identity);
                return c3;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("BattleSync spawn: " + ex.Message);
                return null;
            }
        }

        private static void Destroy(InteractableCard3d c3)
        {
            try
            {
                if (c3 != null)
                    c3.OnDestroyed();
            }  // the game's own card3d cleanup (PlayCardSet.ResetCards)
            catch (Exception ex) { CoopPlugin.Log.LogWarning("BattleSync destroy: " + ex.Message); }
        }

        /// <summary>Gift packs the host won, lying on the enemy side's item spawn spot until
        /// the host picks them up (then they ride the hand sync like any held item).</summary>
        private void ApplyGifts(PlayTableGame game, List<EItemType> gifts)
        {
            string sig = string.Join(",", gifts.ConvertAll(g => ((int)g).ToString()).ToArray());
            if (sig == _giftSig)
                return;
            _giftSig = sig;
            ClearGifts();
            var anchor = game.m_PlayCardSetEnemy != null ? game.m_PlayCardSetEnemy.m_ItemSpawnPos : null;
            if (anchor == null || gifts.Count == 0)
                return;
            try
            {
                for (int l = 0; l < gifts.Count; l++)
                {
                    // host id -> ours already happened in the DTO deserialize; None = a pack
                    // this PC doesn't have, skipped by value (see AvatarManager's hold props)
                    if (gifts[l] == EItemType.None)
                        continue;
                    var meshData = InventoryBase.GetItemMeshData(gifts[l]);
                    if (meshData == null || meshData.mesh == null)
                        continue;
                    var item = ItemSpawnManager.GetItem(anchor);
                    if (item == null)
                        continue;
                    item.SetMesh(meshData.mesh, meshData.material, gifts[l], meshData.meshSecondary, meshData.materialSecondary);
                    item.transform.localPosition = Vector3.up * 0.025f * (l + 1) + Vector3.right * 0.005f * (l + 1) + Vector3.back * -0.005f * (l + 1);
                    item.transform.localRotation = Quaternion.identity;
                    item.m_Collider.enabled = false;      // a prop, never interactable here
                    item.m_Rigidbody.isKinematic = true;
                    item.gameObject.SetActive(true);
                    _giftProps.Add(item);
                }
            }
            catch (Exception ex) { CoopPlugin.Log.LogWarning("BattleSync gifts: " + ex.Message); }
        }

        private void ClearGifts()
        {
            for (int i = 0; i < _giftProps.Count; i++)
            {
                try
                {
                    if (_giftProps[i] != null)
                        ItemSpawnManager.DisableItem(_giftProps[i]);
                }
                catch { }
            }
            _giftProps.Clear();
        }

        private void TearDown(PlayTableGame game)
        {
            foreach (var kv in _cards)
                Destroy(kv.Value);
            _cards.Clear();
            _cardSig.Clear();
            ClearGifts();
            _giftSig = "";
            if (_clientActive && game != null && game.m_Grp != null)
            {
                try
                {
                    game.m_Grp.SetActive(false);
                }
                catch { }
            }
            _clientActive = false;
            _clientTable = -1;
        }

        public override void Reset()
        {
            _gate.Reset(-3.1f);
            _hostWasActive = false;
            TearDown(Game());
        }

        public override void ForceResend()
        {
            _gate.Force();
        }
    }
}
