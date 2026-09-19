using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// fv-871: league shops close and open the day TOGETHER. Every shop in a league runs its own
    /// clock, so without this shops ended and started days minutes apart and the day board
    /// compared shops at different points. Two ready-ups on the LEAGUE CARD fix it (Dan, mid-test:
    /// "the advance the day should require everyone hit ready on the league card" / "when they go
    /// to flip the sign they get a league card that has a ready up menu"):
    ///   ADVANCE - the recap opens as always (Enter at closing time; co-op's sleep vote applies),
    ///             but nothing on it advances the day any more. The league card beside it has
    ///             READY; the day advances on every shop at once when the last shop is ready.
    ///   OPEN    - next morning the OPEN sign does not open the shop: it raises the league card
    ///             with the same READY; the signs flip open on every shop at once when the last
    ///             shop is ready. Later flips that day (close, reopen) are free.
    /// The captain's PC casts the shop's vote; a teammate's sign click reaches it as before.
    ///
    /// The lobby server never keeps a "phase": each playing captain reports (Day, Stage, Ready)
    /// once a second when it changes, and a shop that is ready is RELEASED when every other
    /// playing shop stands at or past the same point of the same day. That is what makes forces,
    /// late joiners and shops on different day numbers come out right: a shop released early
    /// (host forced) simply runs ahead, and the laggard is released the moment it asks, because
    /// everyone else is already past it. The host's timeout (Rivals.DayEndTimeoutSec, default
    /// 120 s, 0 = never) and the Force button release whoever is waiting right now.
    /// </summary>
    public static class LeagueDaySync
    {
        public const string StageMorning = "morning";   // day started, OPEN sign not yet flipped (clock frozen)
        public const string StageTrading = "trading";   // shop was opened today, clock running
        public const string StageClosed = "closed";     // 21:00 reached, recap not open
        public const string StageReport = "report";     // the end-of-day recap is up

        // ---------------------------------------------------------------- everyone: what the lobby last said
        /// <summary>Every playing shop's day state as the lobby server last sent it.</summary>
        public static readonly List<RivalsDayState> Shops = new List<RivalsDayState>();
        public static long Deadline;
        public static float ReceivedAt = -100f;

        // ---------------------------------------------------------------- me (a captain running the league save)
        private static bool s_ready;
        private static string s_readyKey = "";      // (day|stage) the ready vote belongs to
        private static string s_sentKey = "";
        private static float s_sentAt = -100f;
        private static float s_pumpTimer;
        private static string s_firedKey = "";      // (day|stage) an auto-release was already acted on
        private static float s_firedAt = -100f;
        private static InteractableOpenCloseSign s_sign;

        public static bool Playing => LeagueSession.Active && LeagueSession.IsCaptain && InGame();
        /// <summary>Anyone in a league shop (captain or teammate) with a lobby: the card is shown.</summary>
        public static bool InLeague => LeagueSession.Active && RivalsLobby.Role != RivalsLobby.LobbyRole.None && InGame();
        public static bool MyReady => s_ready && s_readyKey == Key(MyDay(), MyStage());

        private static readonly System.Reflection.FieldInfo FiLoadingNextDay = AccessTools.Field(typeof(EndOfDayReportScreen), "m_IsLoadingNextDay");

        private static bool InGame()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel && GameInstance.m_FinishedSavefileLoading;
        }

        /// <summary>The recap's "next day" loading curtain (0.5 s before GoNextDay, 7.5 s after).
        /// While it is up the shop is still "on the report" of the day that ended.</summary>
        private static bool LoadingNextDay()
        {
            try
            {
                var scr = CSingleton<EndOfDayReportScreen>.Instance;
                return scr != null && FiLoadingNextDay != null && (bool)FiLoadingNextDay.GetValue(scr);
            }
            catch { return false; }
        }

        /// <summary>The day my shop is on (the game shows m_CurrentDay + 1). Behind the loading
        /// curtain the day has already ticked over: report it as the day that ended.</summary>
        public static int MyDay()
        {
            int day = CPlayerData.m_CurrentDay + 1;
            try
            {
                if (LoadingNextDay() && !EndOfDayReportScreen.IsActive())
                    day--;
            }
            catch { }
            return day;
        }

        public static string MyStage()
        {
            try
            {
                if (!InGame())
                    return "";
                if (EndOfDayReportScreen.IsActive() || LoadingNextDay())
                    return StageReport;
                if (LightManager.GetHasDayEnded())
                    return StageClosed;
                return CPlayerData.m_IsShopOnceOpen ? StageTrading : StageMorning;
            }
            catch { return ""; }
        }

        private static string Key(int day, string stage) => day + "|" + stage;
        private static bool IsWaitingStage(string stage) => stage == StageReport || stage == StageMorning;

        /// <summary>My row of the last sync, if it still describes where I stand.</summary>
        public static RivalsDayState Mine()
        {
            int id = RivalsLobby.MyId;
            var me = Shops.Find(s => s.Id == id);
            if (me == null || me.Day != MyDay() || me.Stage != MyStage())
                return null;
            return me;
        }

        /// <summary>The row that speaks for MY SHOP: mine when I run it, my team's captain's when
        /// I am a teammate in it (the card shows the same picture on every PC of the shop).</summary>
        public static RivalsDayState MyShopRow()
        {
            if (Playing)
                return Mine();
            int team = RivalsLobby.MyTeam;
            var cap = RivalsLobby.Roster.Find(m => m.Team == team && m.Captain);
            return cap != null ? Shops.Find(s => s.Id == cap.Id) : null;
        }

        /// <summary>The point a shop's READY at its current stage stands for (Pos with the vote cast).</summary>
        public static double ReadyPoint(RivalsDayState s)
        {
            return Pos(new RivalsDayState { Day = s.Day, Stage = s.Stage, Ready = true });
        }

        /// <summary>The lobby has released me at my current point (every shop is here, or the
        /// host forced it).</summary>
        public static bool Released()
        {
            var me = Mine();
            return me != null && me.Released;
        }

        /// <summary>Shops the lobby is still waiting for before I may go on ("" = none).</summary>
        public static string WaitingFor()
        {
            var me = Mine();
            if (me == null)
                return "";
            var names = new List<string>();
            foreach (var s in Shops)
                if (s.Id != me.Id && Pos(s) < Pos(me))
                    names.Add(s.Name);
            return string.Join(", ", names);
        }

        public static void Reset()
        {
            Shops.Clear();
            Deadline = 0;
            s_ready = false;
            s_readyKey = s_sentKey = s_firedKey = "";
            s_sign = null;
            ServerReset();
        }

        // ---------------------------------------------------------------- the two gates (patched in)

        private static readonly System.Reflection.FieldInfo FiIsLerping = AccessTools.Field(typeof(EndOfDayReportScreen), "m_IsLerpingNumber");

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                var sign = AccessTools.Method(typeof(InteractableOpenCloseSign), "OnMouseButtonUp");
                if (sign != null)
                    h.Patch(sign, prefix: new HarmonyMethod(typeof(LeagueDaySync), nameof(SignPrefix)));
                var btn = AccessTools.Method(typeof(EndOfDayReportScreen), "OnPressGoNextButton");
                if (btn != null)
                    h.Patch(btn, prefix: new HarmonyMethod(typeof(LeagueDaySync), nameof(NextButtonPrefix)));
                var next = AccessTools.Method(typeof(EndOfDayReportScreen), "OnPressGoNextDay");
                if (next != null)
                    h.Patch(next, prefix: new HarmonyMethod(typeof(LeagueDaySync), nameof(NextDayPrefix)));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("LeagueDaySync patch: " + e.Message); }
        }

        /// <summary>A gate applies on this PC: a captain running the league save, in a lobby.</summary>
        private static bool Gated => Playing && RivalsLobby.Role != RivalsLobby.LobbyRole.None && CoopCore.Role != CoopRole.Client;

        /// <summary>The recap's click-anywhere / Enter: the count-up fast-forward stays; the
        /// advance waits for the league card's READY (a guest's copy is ReportSync's business).</summary>
        public static bool NextButtonPrefix(EndOfDayReportScreen __instance)
        {
            try
            {
                if (!Gated || MyStage() != StageReport)
                    return true;
                bool lerping = FiIsLerping != null && __instance != null && (bool)FiIsLerping.GetValue(__instance);
                if (lerping)
                    return true;
                if (Released())
                    return true;
                // the keyboard path gets a hint; a mouse click is most likely the card's own button
                if (!MyReady && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyUp(KeyCode.Return) || Input.GetKeyUp(KeyCode.KeypadEnter)))
                    HostOnlyFeatures.Notice("League: press READY on the league card - the day advances when every shop is ready");
                return false;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("LeagueDaySync next: " + e.Message);
                return true;
            }
        }

        /// <summary>The recap's NEXT DAY button (and any other path into the day advance).</summary>
        public static bool NextDayPrefix()
        {
            try
            {
                if (!Gated || MyStage() != StageReport)
                    return true;
                if (Released())
                    return true;
                if (!MyReady)
                    HostOnlyFeatures.Notice("League: press READY on the league card - the day advances when every shop is ready");
                return false;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("LeagueDaySync next-day: " + e.Message);
                return true;
            }
        }

        /// <summary>The OPEN sign was clicked (a teammate's click arrives here on the captain's
        /// PC, as it always did). The first open of a day raises the league card instead.</summary>
        public static bool SignPrefix()
        {
            try
            {
                if (CoopCore.Role == CoopRole.Client)
                {
                    // a teammate: the click is forwarded to the captain's PC (ShopStateSync), where
                    // the gate runs; show the card here too so the click is not a silent nothing
                    if (InLeague && !CPlayerData.m_IsShopOnceOpen && MyStage() == StageMorning)
                        UI.DaySyncCard.Open();
                    return true;
                }
                if (!Gated)
                    return true;
                if (CPlayerData.m_IsShopOnceOpen || MyStage() != StageMorning)
                    return true; // closing / reopening during the day is free
                if (CPlayerData.m_TutorialIndex < 5 && CPlayerData.m_ShopLevel < 1)
                    return true; // vanilla's own "can't open yet" popup
                if (Released())
                    return true;
                UI.DaySyncCard.Open();
                return false;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("LeagueDaySync sign: " + e.Message);
                return true;
            }
        }

        /// <summary>May this PC press READY right now (a captain at a waiting point)?</summary>
        public static bool CanVote => Gated && IsWaitingStage(MyStage());

        /// <summary>The league card's READY button: the shop's vote for its current point.</summary>
        public static void ToggleReady()
        {
            if (!CanVote)
                return;
            string stage = MyStage();
            string what = (stage == StageReport ? "advance to day " + (MyDay() + 1) : "open day " + MyDay());
            string key = Key(MyDay(), stage);
            if (s_readyKey != key)
                s_ready = false;
            s_readyKey = key;
            s_ready = !s_ready;
            s_pumpTimer = 10f; // send now
            Pump();
            if (s_ready)
            {
                string waiting = WaitingFor();
                HostOnlyFeatures.Notice("League: ready to " + what + (waiting.Length > 0 ? " - waiting for " + waiting : " - waiting for the other shops"));
            }
            else
                HostOnlyFeatures.Notice("League: no longer ready to " + what);
            CoopPlugin.Log.LogInfo($"LeagueDaySync: {(s_ready ? "ready" : "unready")} to {what}");
        }

        // ---------------------------------------------------------------- tick (every frame from RivalsLobby)

        public static void Tick()
        {
            if (RivalsLobby.Role == RivalsLobby.LobbyRole.None)
                return;
            s_pumpTimer += Time.unscaledDeltaTime;
            if (s_pumpTimer < 1f)
                return;
            s_pumpTimer = 0f;
            try
            {
                Pump();
                ActOnRelease();
                if (RivalsLobby.Role == RivalsLobby.LobbyRole.Server)
                    ServerTick();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("LeagueDaySync tick: " + e.Message); }
        }

        /// <summary>My (playing, day, stage, ready) to the lobby whenever it changes.</summary>
        private static void Pump()
        {
            bool playing = Playing;
            int day = playing ? MyDay() : 0;
            string stage = playing ? MyStage() : "";
            bool ready = playing && MyReady;
            string key = $"{playing}|{day}|{stage}|{ready}";
            if (key == s_sentKey && Time.unscaledTime - s_sentAt < 10f)
                return; // unchanged: still re-sent every 10 s (a lobby that restarted, a send before the welcome)
            s_sentKey = key;
            s_sentAt = Time.unscaledTime;
            var m = new RivalsLeagueMessage { Op = "daystate", LeagueId = RivalsLobby.LeagueId, Playing = playing, Day = day, Stage = stage, Ready = ready };
            if (RivalsLobby.Role == RivalsLobby.LobbyRole.Server)
                ServerApply(0, m);
            else
                RivalsLobby.SendToLobby(m);
        }

        /// <summary>Released at a waiting point: do what the press would have done. ADVANCE: the
        /// recap's own next-day (vanilla path: close, GoNextDay, the loading curtain; co-op's
        /// report mirror follows the host as always). OPEN: flip the sign (vanilla click:
        /// animation, tutorial task, the clock starts). Retried once a second while the point
        /// holds - the sign's swap debounce may drop the first click.</summary>
        private static void ActOnRelease()
        {
            if (!Gated || !Released())
                return;
            string stage = MyStage();
            string key = Key(MyDay(), stage);
            if (s_firedKey == key && Time.unscaledTime - s_firedAt < 1f)
                return;
            s_firedKey = key;
            s_firedAt = Time.unscaledTime;
            if (stage == StageReport)
            {
                var scr = CSingleton<EndOfDayReportScreen>.Instance;
                if (scr != null && EndOfDayReportScreen.IsActive())
                {
                    CoopPlugin.Log.LogInfo($"LeagueDaySync: released - advancing past day {MyDay()}");
                    HostOnlyFeatures.Notice("League: every shop is ready - day " + (MyDay() + 1) + " begins");
                    scr.OnPressGoNextDay();
                }
            }
            else if (stage == StageMorning)
            {
                if (s_sign == null)
                    s_sign = UnityEngine.Object.FindObjectOfType<InteractableOpenCloseSign>(true);
                if (s_sign != null)
                {
                    CoopPlugin.Log.LogInfo($"LeagueDaySync: released - opening day {MyDay()}");
                    HostOnlyFeatures.Notice("League: every shop is ready - day " + MyDay() + " opens");
                    s_sign.OnMouseButtonUp();
                }
            }
        }

        // ---------------------------------------------------------------- everyone: apply the lobby's sync

        public static void ApplySync(RivalsLeagueMessage m)
        {
            Shops.Clear();
            if (m.Shops != null)
                Shops.AddRange(m.Shops);
            Deadline = m.Deadline;
            ReceivedAt = Time.unscaledTime;
            if (Mine() != null && Released())
                s_pumpTimer = 10f; // act on it now, not next second
        }

        /// <summary>How far along the day a shop is: day, then stage, then its ready vote. A shop
        /// ready at a waiting point is released when nobody playing is behind that point.</summary>
        public static double Pos(RivalsDayState s)
        {
            int idx = s.Stage == StageMorning ? 0 : s.Stage == StageTrading ? 1 : s.Stage == StageClosed ? 2 : s.Stage == StageReport ? 3 : 0;
            double p = s.Day * 4 + idx;
            if (s.Ready && IsWaitingStage(s.Stage))
                p += 0.5;
            return p;
        }

        // ---------------------------------------------------------------- lobby server

        private sealed class ShopDay
        {
            public RivalsDayState State = new RivalsDayState();
            public bool Playing;
            public float WaitingSince = -1f;
            public string ForcedKey = "";
        }

        private static readonly Dictionary<int, ShopDay> s_server = new Dictionary<int, ShopDay>();
        private static string s_lastSync = "";
        private static float s_resendTimer;

        private static int TimeoutSec => CoopPlugin.RivalsDayEndTimeoutSec != null ? Mathf.Max(0, CoopPlugin.RivalsDayEndTimeoutSec.Value) : 120;
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static void ServerReset()
        {
            s_server.Clear();
            s_lastSync = "";
        }

        public static void ServerForget(int conn)
        {
            if (s_server.Remove(conn))
                ServerSync(true);
        }

        /// <summary>A member's "daystate" (the server's own shop comes in as conn 0).</summary>
        public static void ServerApply(int conn, RivalsLeagueMessage m)
        {
            if (RivalsLobby.Role != RivalsLobby.LobbyRole.Server)
                return;
            if (!s_server.TryGetValue(conn, out var s))
                s_server[conn] = s = new ShopDay();
            var st = s.State;
            string before = $"{s.Playing}|{st.Day}|{st.Stage}|{st.Ready}";
            st.Id = conn;
            st.Name = RivalsLobby.NameOfMember(conn);
            st.Day = m.Day;
            st.Stage = m.Stage ?? "";
            st.Ready = m.Ready;
            s.Playing = m.Playing;
            string after = $"{s.Playing}|{st.Day}|{st.Stage}|{st.Ready}";
            bool waiting = s.Playing && st.Ready && IsWaitingStage(st.Stage);
            if (!waiting)
                s.WaitingSince = -1f;
            else if (s.WaitingSince < 0f || before != after)
                s.WaitingSince = Time.unscaledTime;
            if (before != after && waiting)
                RivalsLobby.LobbyChat($"{st.Name} is ready to {(st.Stage == StageReport ? "advance past" : "open")} day {st.Day}");
            ServerSync(before != after);
        }

        /// <summary>The lobby host's Force button: release every shop waiting right now.</summary>
        public static void HostForce()
        {
            if (RivalsLobby.Role != RivalsLobby.LobbyRole.Server)
                return;
            int n = 0;
            foreach (var s in s_server.Values)
                if (s.Playing && s.State.Ready && IsWaitingStage(s.State.Stage))
                {
                    s.ForcedKey = Key(s.State.Day, s.State.Stage);
                    n++;
                }
            if (n > 0)
            {
                RivalsLobby.LobbyChat("the host released the shops that are ready - the rest catch up");
                ServerSync(true);
            }
        }

        private static void ServerTick()
        {
            // the timeout: a shop that has waited long enough goes on without the others
            int timeout = TimeoutSec;
            bool forced = false;
            if (timeout > 0)
                foreach (var s in s_server.Values)
                {
                    if (!s.Playing || !s.State.Ready || !IsWaitingStage(s.State.Stage) || s.WaitingSince < 0f)
                        continue;
                    string key = Key(s.State.Day, s.State.Stage);
                    if (s.ForcedKey != key && Time.unscaledTime - s.WaitingSince >= timeout)
                    {
                        s.ForcedKey = key;
                        forced = true;
                        RivalsLobby.LobbyChat($"{s.State.Name} waited {timeout} s - released; the other shops catch up");
                    }
                }
            s_resendTimer += 1f;
            bool resend = s_resendTimer >= 5f;
            if (resend)
                s_resendTimer = 0f;
            ServerSync(forced || resend);
        }

        /// <summary>Compute every shop's Released and send the picture to everyone when it changed
        /// (or when asked to).</summary>
        private static void ServerSync(bool force)
        {
            var list = new List<RivalsDayState>();
            long deadline = 0;
            int timeout = TimeoutSec;
            foreach (var s in s_server.Values)
            {
                if (!s.Playing)
                    continue;
                var st = s.State;
                st.Released = false;
                if (st.Ready && IsWaitingStage(st.Stage))
                {
                    bool released = s.ForcedKey == Key(st.Day, st.Stage);
                    if (!released)
                    {
                        released = true;
                        double mine = Pos(st);
                        foreach (var o in s_server.Values)
                            if (o != s && o.Playing && Pos(o.State) < mine)
                            {
                                released = false;
                                break;
                            }
                    }
                    st.Released = released;
                    if (!released && timeout > 0 && s.WaitingSince >= 0f)
                    {
                        long at = Now() + (long)Mathf.Max(0f, timeout - (Time.unscaledTime - s.WaitingSince));
                        if (deadline == 0 || at < deadline)
                            deadline = at;
                    }
                }
                list.Add(st);
            }
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            var sb = new System.Text.StringBuilder();
            foreach (var st in list)
                sb.Append(st.Id).Append(':').Append(st.Day).Append(st.Stage).Append(st.Ready ? 'R' : '-').Append(st.Released ? 'G' : '-').Append(' ');
            sb.Append(deadline > 0 ? "D" : "-");
            string key = sb.ToString();
            if (!force && key == s_lastSync)
                return;
            bool changed = key != s_lastSync;
            s_lastSync = key;
            var msg = new RivalsLeagueMessage { Op = "daysync", LeagueId = RivalsLobby.LeagueId, Deadline = deadline };
            foreach (var st in list)
                msg.Shops.Add(new RivalsDayState { Id = st.Id, Name = st.Name, Day = st.Day, Stage = st.Stage, Ready = st.Ready, Released = st.Released });
            RivalsLobby.BroadcastLobby(msg);
            ApplySync(msg); // the server's own shop
            if (changed)
                foreach (var st in list)
                    if (st.Released)
                        RivalsLobby.LobbyChat($"{(st.Stage == StageReport ? "day " + (st.Day + 1) + " begins" : "day " + st.Day + " opens")} for {st.Name}");
        }

        // ---------------------------------------------------------------- UI text

        /// <summary>One line for the LEAGUE box / phone app: my standing in the day sync.</summary>
        public static string StatusLine()
        {
            if (!Playing)
                return "";
            string stage = MyStage();
            int day = MyDay();
            if (stage == StageReport)
            {
                if (Released())
                    return $"day {day}: every shop is ready - day {day + 1} begins";
                if (MyReady)
                    return $"day {day}: READY to advance - waiting for {WaitingFor()}{Countdown()}";
                return $"day {day}: on the report - press READY on the league card; the day advances when every shop is ready";
            }
            if (stage == StageMorning)
            {
                if (Released())
                    return $"day {day}: every shop is ready - opening";
                if (MyReady)
                    return $"day {day}: READY to open - waiting for {WaitingFor()}{Countdown()}";
                return $"day {day}: click the OPEN sign for the league card - the shops open together";
            }
            if (stage == StageClosed)
                return $"day {day}: closing time - Enter opens the report";
            return $"day {day}: trading";
        }

        private static string Countdown()
        {
            if (Deadline <= 0)
                return "";
            long left = Deadline - Now();
            return left > 0 ? $" (released in {left} s)" : " (releasing)";
        }

        public static string StageText(RivalsDayState s)
        {
            switch (s.Stage)
            {
                case StageMorning: return s.Ready ? "READY to open" : "morning, not ready";
                case StageTrading: return "trading";
                case StageClosed: return "closing time";
                case StageReport: return s.Ready ? "READY to advance" : "on the report, not ready";
                default: return s.Stage;
            }
        }
    }
}
