using System;
using HarmonyLib;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Host-only gate for the game-1.0 features that have no co-op sync yet: the Workbench
    /// deck editor and signing the player up for their own tournament. The playable TCG
    /// itself is guest-playable through <see cref="GuestBattle"/>; the RMB prefix here only
    /// routes the guest's click there (and falls back to a notice if that is unavailable).
    ///
    /// Why a gate rather than a sync: both run on LOCAL state. Decks live in
    /// <c>CPlayerData.m_DeckCompactCardDataList</c>, which nothing mirrors, and tournament
    /// sign-up writes <see cref="CPlayerData"/> tournament data the host's bracket reads. On
    /// the guest every one of those writes lands in the scratch slot and is thrown away. So
    /// until each has a real sync, the HOST does it and the guest is told why, in the game's
    /// own popup.
    ///
    /// Also here: <see cref="IsHostBattleTable"/>, the guard PlayTableSync uses to refuse a
    /// guest's table-kick intent while anyone is in a battle at that table (StopTableGame
    /// under a live PlayTableGame strands the battle UI).
    /// </summary>
    internal static class HostOnlyFeatures
    {
        private const string BattleNotice = "Co-op: only the host can play the card game for now";
        private const string DeckNotice = "Co-op: only the host can edit decks for now";
        private const string TournamentNotice = "Co-op: only the host can join the tournament for now";
        private static string s_lastNotice;
        private static float s_lastNoticeAt = -10f;

        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(InteractablePlayTable), "OnRightMouseButtonUp",
                new HarmonyMethod(typeof(HostOnlyFeatures), nameof(PlayTableRightClickPrefix)));
            Try(h, typeof(PlayCardGameManager), "OpenDeckListScreen",
                new HarmonyMethod(typeof(HostOnlyFeatures), nameof(OpenDeckListPrefix)));
            Try(h, typeof(HostTournamentScreen), "OnPressPlayerSignUpTournament",
                new HarmonyMethod(typeof(HostOnlyFeatures), nameof(PlayerSignUpPrefix)));
        }

        /// <summary>True when the HOST is in a battle at this table, so nothing may stop the
        /// table game from under it. <c>GetHasStartPlayerPlayCard</c> is the game's own flag
        /// for "a player sat here", set by <see cref="InteractablePlayTable.StartPlayerCardGame"/>
        /// and cleared by StopTableGame.</summary>
        internal static bool IsHostBattleTable(InteractablePlayTable table)
        {
            try
            {
                return table != null && table.GetHasStartPlayerPlayCard();
            }
            catch { return false; }
        }

        // ---------------- guest gates ----------------

        /// <summary>Vanilla RMB on a play table is the ONLY entry into a battle
        /// (InteractablePlayTable.OnRightMouseButtonUp -> PlayCardGameManager.SetPlayTable).
        /// Skipping the whole method also skips the base call, which for this class only
        /// forwards the click to the generic object handler - nothing a guest loses.</summary>
        public static bool PlayTableRightClickPrefix(InteractablePlayTable __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            if (!GuestBattle.ClientRequestSit(__instance))
                Notice(BattleNotice);
            return false;
        }

        /// <summary>The Workbench "deck" button routes through this static; the deck list
        /// screen itself is the only way into DeckEditScreen.</summary>
        public static bool OpenDeckListPrefix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            Notice(DeckNotice);
            return false;
        }

        public static bool PlayerSignUpPrefix()
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            Notice(TournamentNotice);
            return false;
        }

        // ---------------- helpers ----------------

        /// <summary>Show free text in the game's own "not enough resource" popup. Vanilla
        /// <see cref="NotEnoughResourceTextPopup.ShowText"/> only takes an enum (a localized
        /// string index), so this mirrors its body with our text instead. Same slot rotation,
        /// same fade timer - it just says something the game has no enum for.</summary>
        internal static void Notice(string text)
        {
            try
            {
                // vanilla dedupes a repeated enum for 2s (m_CurrentNotEnoughResourceText +
                // m_ResetTimer, both private); mirror that so holding RMB doesn't fill every slot
                float now = UnityEngine.Time.unscaledTime;
                if (text == s_lastNotice && now - s_lastNoticeAt < 2f)
                    return;
                s_lastNotice = text;
                s_lastNoticeAt = now;
                var popup = CSingleton<NotEnoughResourceTextPopup>.Instance;
                if (popup == null || popup.m_ShowTextGameObjectList == null || popup.m_ShowTextList == null)
                {
                    CoopPlugin.Log.LogInfo(text);
                    return;
                }
                for (int i = 0; i < popup.m_ShowTextGameObjectList.Count && i < popup.m_ShowTextList.Count; i++)
                {
                    var go = popup.m_ShowTextGameObjectList[i];
                    if (go == null || go.activeSelf)
                        continue;
                    popup.m_ShowTextList[i].text = text;
                    go.SetActive(true);
                    return;
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("HostOnlyFeatures notice failed: " + e.Message);
            }
        }

        private static void Try(Harmony h, Type type, string method, HarmonyMethod prefix)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"HostOnlyFeatures patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"HostOnlyFeatures patch failed for {type.Name}.{method}: {e.Message}");
            }
        }
    }
}
