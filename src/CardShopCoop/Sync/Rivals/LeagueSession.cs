using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// A LEAGUE SAVE: a shop that exists only inside its league. The lobby host starts the
    /// league (everyone readied up at the title screen), every team captain gets a new game at
    /// the same moment - tutorial finished, the host's world settings, the host's market - and
    /// from then on that shop lives in a save slot single player never touches
    /// (<see cref="Slot"/>, default 8: the game lists 0-3, co-op borrows 7). While the session
    /// is live every save and load the game issues is redirected there, so Continue / autosave /
    /// quit-save all land in the league slot and never in the player's own slots.
    ///
    /// Reloading is gated by the same mechanism: the slot is only ever read when a START comes
    /// down from the originating lobby (<see cref="Begin"/> refuses without a lobby connection),
    /// and nothing in the game's own UI can reach slot 8. Not tamper-proof - a friend group
    /// doesn't need that - but the game will not stumble into it.
    ///
    /// One league per slot at a time: starting a different league archives the slot's files to
    /// CardShopCoop.leagues/&lt;old id&gt;/ and restores that league's files if they were archived
    /// before, so several leagues can coexist on one PC.
    /// </summary>
    public static class LeagueSession
    {
        public static bool Active
        {
            get; private set;
        }
        public static string Id
        {
            get; private set;
        } = "";
        public static string Name = "";
        public static bool IsCaptain;
        /// <summary>This START created the save (fresh game: tutorial gets finished).</summary>
        public static bool NewGame;
        public static string Status = "";
        /// <summary>Only the save HOLDER's game is redirected; a teammate borrows the
        /// captain's world through co-op and never saves (SaveGuardPrefix).</summary>
        public static bool Redirecting => Active && IsCaptain;

        private static bool s_tutorialPending, s_hostPending, s_viaSteam, s_cloudBefore;
        private static float s_levelUpAt = -1f;
        private static int s_tutorialPasses;

        public static int Slot => CoopPlugin.RivalsSaveSlot != null ? CoopPlugin.RivalsSaveSlot.Value : 8;
        private static string Root => Application.persistentDataPath;
        private static string SlotFile(string ext) => Root + "/savedGames_Release" + Slot + ext;
        private static string BackupFile => Root + "/savedGames_ReleaseBackupFile" + Slot + ".json";
        private static string Marker => Root + "/savedGames_Release" + Slot + ".league";
        private static string ArchiveDir(string id) => Root + "/CardShopCoop.leagues/" + id;

        private static readonly string[] SlotExts = { ".json", ".gd" };

        // ================================================================ files

        /// <summary>The league id whose save currently sits in the slot ("" = none).</summary>
        public static string SlotOwner()
        {
            try
            {
                if (File.Exists(Marker))
                    return File.ReadAllText(Marker).Trim();
            }
            catch { }
            return "";
        }

        /// <summary>Do we hold a save for this league - in the slot or archived?</summary>
        public static bool HasSave(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            try
            {
                if (SlotOwner() == id && File.Exists(SlotFile(".json")))
                    return true;
                return File.Exists(Path.Combine(ArchiveDir(id), "savedGames_Release" + Slot + ".json"));
            }
            catch { return false; }
        }

        /// <summary>Put league <paramref name="id"/>'s files in the slot: archive whatever
        /// other league is there, restore this one's if archived.</summary>
        private static void SwapIn(string id)
        {
            string owner = SlotOwner();
            if (owner == id)
                return;
            if (!string.IsNullOrEmpty(owner))
            {
                string dir = ArchiveDir(owner);
                Directory.CreateDirectory(dir);
                foreach (string ext in SlotExts)
                    MoveIf(SlotFile(ext), Path.Combine(dir, Path.GetFileName(SlotFile(ext))));
                MoveIf(BackupFile, Path.Combine(dir, Path.GetFileName(BackupFile)));
                CoopPlugin.Log.LogInfo($"League: archived league {owner}'s save from slot {Slot}");
            }
            else
            {
                // an unmarked file in the slot is nobody's: keep it out of the way
                foreach (string ext in SlotExts)
                    MoveIf(SlotFile(ext), SlotFile(ext + ".stray"));
                MoveIf(BackupFile, BackupFile + ".stray");
            }
            string mine = ArchiveDir(id);
            if (Directory.Exists(mine))
            {
                foreach (string ext in SlotExts)
                    MoveIf(Path.Combine(mine, Path.GetFileName(SlotFile(ext))), SlotFile(ext));
                MoveIf(Path.Combine(mine, Path.GetFileName(BackupFile)), BackupFile);
                CoopPlugin.Log.LogInfo($"League: restored league {id}'s save into slot {Slot}");
            }
            File.WriteAllText(Marker, id);
        }

        private static void MoveIf(string from, string to)
        {
            try
            {
                if (!File.Exists(from))
                    return;
                if (File.Exists(to))
                    File.Delete(to);
                File.Move(from, to);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("League: move " + Path.GetFileName(from) + ": " + e.Message); }
        }

        // ================================================================ session

        /// <summary>The lobby's START landed. A captain loads the league save (or a new game
        /// when there is none); a teammate just marks the session and waits for the captain's
        /// shop to open (RivalsLobby joins it). Only ever called with a lobby connection up.</summary>
        public static bool Begin(string id, string name, bool captain, bool viaSteam)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            if (RivalsLobby.Role == RivalsLobby.LobbyRole.None)
            {
                Status = "league saves need the league lobby";
                return false;
            }
            var gm = CSingleton<CGameManager>.Instance;
            if (gm == null || gm.m_IsGameLevel || CoopCore.Role != CoopRole.None)
            {
                Status = "go to the title screen first";
                return false;
            }
            try
            {
                if (captain)
                    SwapIn(id);
                Id = id;
                Name = name ?? "";
                IsCaptain = captain;
                Active = true;
                s_viaSteam = viaSteam;
                s_cloudBefore = gm.m_ForceNoCloudSaveLoad;
                gm.m_ForceNoCloudSaveLoad = true; // the league slot is never a cloud conflict
                s_levelUpAt = -1f;
                s_tutorialPasses = 0;
                if (captain)
                {
                    bool resume = File.Exists(SlotFile(".json"));
                    NewGame = !resume;
                    s_tutorialPending = true; // a league shop never runs the tutorial - new OR resumed
                    s_hostPending = true;
                    typeof(CGameManager).GetField("m_InitLoaded", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
                    gm.m_CurrentSaveLoadSlotSelectedIndex = 0; // redirected to the league slot
                    Status = resume ? $"resuming league {id} (slot {Slot})" : $"new league game {id} (slot {Slot}, tutorial off)";
                    CoopPlugin.Log.LogInfo("League: " + Status);
                    gm.LoadMainLevelAsync("Start", resume ? 0 : -1);
                }
                else
                {
                    NewGame = false;
                    s_tutorialPending = false;
                    s_hostPending = false;
                    Status = "waiting for your captain's shop to open...";
                    CoopPlugin.Log.LogInfo("League: teammate in league " + id + " - " + Status);
                }
                return true;
            }
            catch (Exception e)
            {
                Status = "could not start: " + e.Message;
                CoopPlugin.Log.LogWarning("League: " + Status);
                Active = false;
                return false;
            }
        }

        /// <summary>Back at the title screen: the session is over until the next START.</summary>
        public static void End()
        {
            if (!Active)
                return;
            Active = false;
            IsCaptain = false;
            NewGame = false;
            s_tutorialPending = s_hostPending = false;
            try
            {
                var gm = CSingleton<CGameManager>.Instance;
                if (gm != null)
                    gm.m_ForceNoCloudSaveLoad = s_cloudBefore;
            }
            catch { }
            Status = $"league {Id} save kept in slot {Slot} - it loads again when the lobby host starts the league";
            CoopPlugin.Log.LogInfo("League: session ended (title screen)");
            // --- fv-873 ready-after-title begin
            // The captain's shop was HOSTED (Tick opened it to the team and visitors). Quitting
            // to the title only ends a CLIENT session (CoopCore.OnSceneLoaded); a host reaching
            // the title kept Role = Host with the world gone behind it, and everything that
            // wants the title screen - Ready up, Return home, the next START - is gated on
            // Role == None, so the lobby was dead until a game restart. The shop closed with
            // the scene: close the session it was hosting too.
            try
            {
                var core = CoopCore.Instance;
                if (core != null && CoopCore.Role == CoopRole.Host)
                {
                    core.Disconnect();
                    CoopPlugin.Log.LogInfo("League: the shop's co-op session closed with it (title screen) - the lobby can ready up again");
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("League: closing the shop's session at the title: " + e.Message); }
            // --- fv-873 ready-after-title end
        }

        /// <summary>Ticked from RivalsLobby.Update (before its own early-outs): finish the
        /// tutorial on a fresh league game and open the captain's shop for the team.</summary>
        public static void Tick()
        {
            if (!Active || !IsCaptain)
                return;
            try
            {
                var gm = CSingleton<CGameManager>.Instance;
                if (gm == null || !gm.m_IsGameLevel || !GameInstance.m_FinishedSavefileLoading)
                    return;
                if (s_levelUpAt < 0f)
                    s_levelUpAt = Time.unscaledTime;
                float since = Time.unscaledTime - s_levelUpAt;
                // the tutorial re-arms its first panel a moment after load: two passes
                if (s_tutorialPending && ((s_tutorialPasses == 0 && since > 1f) || (s_tutorialPasses == 1 && since > 5f)))
                {
                    s_tutorialPasses++;
                    Util.TutorialSkip.Finish();
                    if (s_tutorialPasses >= 2)
                        s_tutorialPending = false;
                }
                if (s_hostPending && since > 2f && CoopCore.Role == CoopRole.None && CoopCore.Instance != null)
                {
                    s_hostPending = false;
                    if (s_viaSteam && CoopCore.Instance.Steam != null)
                        CoopCore.Instance.StartHostingSteam(false, Name + " - " + RivalsLobby.MyShopNameForLeague(), "");
                    else
                        CoopCore.Instance.StartHosting();
                    if (CoopCore.Role == CoopRole.Host)
                    {
                        Status = $"league {Id} live - shop open to the team and visitors";
                        RivalsLobby.MarketDirty = true; // the league host's market goes out at once
                    }
                    else
                        Status = "league live but the shop could not open: " + CoopCore.Instance.ErrorLine;
                    CoopPlugin.Log.LogInfo("League: " + Status);
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("League tick: " + e.Message); }
        }

        // ================================================================ patches

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                var pre = new HarmonyMethod(typeof(LeagueSession), nameof(SlotPrefix)) { priority = Priority.High };
                foreach (var (name, args) in new[]
                {
                    ("Save", new[] { typeof(int), typeof(bool) }),
                    ("Load", new[] { typeof(int) }),
                    ("LoadBackup", new[] { typeof(int) }),
                    ("HasSaveFile", new[] { typeof(int) }),
                    ("LoadSavedSlotData", new[] { typeof(int) }),
                })
                {
                    var m = AccessTools.Method(typeof(CSaveLoad), name, args);
                    if (m != null)
                        h.Patch(m, prefix: pre);
                    else
                        CoopPlugin.Log.LogWarning("League: CSaveLoad." + name + " not found");
                }
                var save = AccessTools.Method(typeof(CSaveLoad), "Save", new[] { typeof(int), typeof(bool) });
                if (save != null)
                    h.Patch(save, postfix: new HarmonyMethod(typeof(LeagueSession), nameof(SavePostfix)));
                var scene = AccessTools.Method(typeof(CGameManager), "OnLevelFinishedLoading");
                if (scene != null)
                    h.Patch(scene, postfix: new HarmonyMethod(typeof(LeagueSession), nameof(ScenePostfix)));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("League patches: " + e.Message); }
        }

        /// <summary>Every slot the game names becomes the league slot while a session is live -
        /// except co-op's own two: the joiner's scratch slot (a teammate borrowing the captain's
        /// world loads it through the same CSaveLoad.Load) and the host's snapshot slot (the
        /// world a captain ships to a joiner is saved and read back from it).</summary>
        public static void SlotPrefix(ref int __0)
        {
            if (Redirecting && __0 != SaveTransfer.CoopSlot && __0 != SaveTransfer.HostSnapshotSlot)
                __0 = Slot;
        }

        /// <summary>The game only lights the "saved" indicator for slot 0; our redirected
        /// autosave is slot 0 in spirit.</summary>
        public static void SavePostfix(int __0)
        {
            if (!Redirecting || __0 != Slot)
                return;
            try
            {
                CEventManager.QueueEvent(new CEventPlayer_OnSaveStatusUpdated(isSuccess: true, isAutosaving: false));
            }
            catch { }
        }

        public static void ScenePostfix(Scene scene)
        {
            if (scene.name == "Title")
                End();
        }
    }
}
