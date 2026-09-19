using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Two apps on the in-game phone - TRADE (the trade window) and DECKS (the deck builder,
    /// no workbench needed). They register through Phone Overhaul's API by reflection (no hard
    /// reference: a PC without it just has no tiles), and draw as an IMGUI panel over the
    /// phone while it is open - the phone stays up (SetCanClosePhone false, tiles unclickable)
    /// until Back. The deck builder also opens anywhere with Keys.DeckBuilderKey, phone or not.
    /// </summary>
    public sealed class PhoneApps : MonoBehaviour
    {
        public enum App
        {
            None, Trade, Decks, Shop, Coop, League,
            // --- fv-685 cheat-app begin
            Cheats,
            // --- fv-685 cheat-app end
            // --- fv-684 econ-app begin
            Econ,
            // --- fv-684 econ-app end
        }

        public static App Current
        {
            get; private set;
        }
        private static bool s_onPhone;         // opened from a phone tile (phone held open)
        private static bool s_registered;
        private static float s_nextTry;
        private static int s_tries;
        private static Vector2 s_scroll;
        private static MethodInfo s_raycast;
        private static float s_lastGoodRepaint = -1f;   // watchdog: an app that stops drawing gets closed
        private static float s_lastGuiError = -100f;

        private const string TradeId = "CoopTrade";
        private const string DecksId = "CoopDecks";
        private const string ShopId = "CoopShop";
        private const string CoopId = "CoopTeam";
        private const string LeagueId = "CoopLeague";
        // --- fv-685 cheat-app begin
        private const string CheatsId = "CoopCheats";
        // --- fv-685 cheat-app end
        // --- fv-684 econ-app begin
        private const string EconId = "CoopEcon";
        // --- fv-684 econ-app end

        private void Update()
        {
            if (!s_registered && Time.unscaledTime >= s_nextTry && s_tries < 40)
            {
                s_nextTry = Time.unscaledTime + 3f;
                s_tries++;
                TryRegister();
            }
            // the deck builder anywhere: a key toggles it outside the phone
            var key = CoopPlugin.DeckBuilderKey != null ? CoopPlugin.DeckBuilderKey.Value : KeyCode.None;
            if (key != KeyCode.None && Input.GetKeyDown(key) && !TypingSomewhere())
            {
                if (Current == App.Decks)
                    Close();
                else if (Current == App.None)
                    Open(App.Decks, false);
            }
            // the phone went away under us
            if (Current != App.None && s_onPhone && !PhoneIsUp())
                Close();
            // Esc always closes the app (the phone's own close is held off while it is open)
            if (Current != App.None && Input.GetKeyDown(KeyCode.Escape))
                Close();
            // watchdog: if the app has not completed a repaint for 2 s (a layout fault, an
            // exception outside the guarded region), close it rather than hold the phone
            if (Current != App.None && s_lastGoodRepaint >= 0f && Time.unscaledTime - s_lastGoodRepaint > 2f)
            {
                CoopPlugin.Log.LogWarning("PhoneApps: " + Current + " app stopped drawing - closed by the watchdog");
                Sync.HostOnlyFeatures.Notice("That app stopped responding and was closed");
                Close();
            }
        }

        private static bool TypingSomewhere()
        {
            try
            {
                return GUI.GetNameOfFocusedControl().Length > 0;
            }
            catch { return false; }
        }

        // ================================================================ Phone Overhaul

        private static void TryRegister()
        {
            try
            {
                Type api = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        api = asm.GetType("PhoneOverhaul.API.PhoneOverhaulAPI");
                    }
                    catch { }
                    if (api != null)
                        break;
                }
                if (api == null)
                {
                    if (s_tries == 1)
                        CoopPlugin.Log.LogInfo("PhoneApps: Phone Overhaul not loaded - no phone tiles (deck builder: " + (CoopPlugin.DeckBuilderKey != null ? CoopPlugin.DeckBuilderKey.Value.ToString() : "key unset") + ", trade: F2 > RIVALS)");
                    s_tries = 40; // stop trying
                    return;
                }
                object registry = api.GetProperty("Registry", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (registry == null)
                    return; // not ready yet
                var specType = api.Assembly.GetType("PhoneOverhaul.API.AppSpec");
                var register = registry.GetType().GetMethod("Register", new[] { specType });
                if (specType == null || register == null)
                {
                    CoopPlugin.Log.LogWarning("PhoneApps: Phone Overhaul API shape unknown - no phone tiles");
                    s_tries = 40;
                    return;
                }
                register.Invoke(registry, new[] { MakeSpec(specType, TradeId, "Trade", () => Open(App.Trade, true)) });
                register.Invoke(registry, new[] { MakeSpec(specType, DecksId, "Decks", () => Open(App.Decks, true)) });
                register.Invoke(registry, new[] { MakeSpec(specType, ShopId, "Shop", () => Open(App.Shop, true)) });
                register.Invoke(registry, new[] { MakeSpec(specType, CoopId, "Co-op", () => Open(App.Coop, true)) });
                register.Invoke(registry, new[] { MakeSpec(specType, LeagueId, "League", () => Open(App.League, true)) });
                // --- fv-685 cheat-app begin
                register.Invoke(registry, new[] { MakeSpec(specType, CheatsId, "Cheats", () => Open(App.Cheats, true)) });
                // --- fv-685 cheat-app end
                // --- fv-684 econ-app begin
                register.Invoke(registry, new[] { MakeSpec(specType, EconId, "Tuning", () => Open(App.Econ, true)) });
                // --- fv-684 econ-app end
                s_registered = true;
                CoopPlugin.Log.LogInfo("PhoneApps: registered Trade, Decks, Shop, Co-op and League on the phone");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("PhoneApps register: " + e.Message);
            }
        }

        private static object MakeSpec(Type specType, string id, string name, Action onClick)
        {
            object spec = Activator.CreateInstance(specType);
            specType.GetField("AppId")?.SetValue(spec, id);
            specType.GetField("DisplayName")?.SetValue(spec, name);
            specType.GetField("Icon")?.SetValue(spec, "Icon");
            specType.GetField("InnerBackground")?.SetValue(spec, "IBG");
            specType.GetField("OuterBackground")?.SetValue(spec, "OBG");
            specType.GetField("OnClick")?.SetValue(spec, onClick);
            return spec;
        }

        // ================================================================ open / close

        private static bool PhoneIsUp()
        {
            try
            {
                var pm = CSingleton<PhoneManager>.Instance;
                var screen = pm != null ? pm.m_UI_PhoneScreen : null;
                return screen != null && screen.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        private static void PhoneTiles(bool enable)
        {
            try
            {
                var pm = CSingleton<PhoneManager>.Instance;
                var screen = pm != null ? pm.m_UI_PhoneScreen : null;
                if (screen == null)
                    return;
                if (s_raycast == null)
                    s_raycast = AccessTools.Method(typeof(UI_PhoneScreen), "SetPhoneButtonRaycastEnable");
                s_raycast?.Invoke(screen, new object[] { enable });
            }
            catch { }
        }

        public static void Open(App app, bool onPhone)
        {
            var gm = CSingleton<CGameManager>.Instance;
            if (gm == null || !gm.m_IsGameLevel)
                return;
            if (Current != App.None)
                Close();
            Current = app;
            s_onPhone = onPhone && PhoneIsUp();
            s_scroll = Vector2.zero;
            s_lastGoodRepaint = Time.unscaledTime;
            CoopPlugin.Log.LogInfo("PhoneApps: opened " + app + (s_onPhone ? " (phone)" : ""));
            if (s_onPhone)
            {
                try
                {
                    SoundManager.PlayAudio("SFX_ButtonLightTap", 0.15f, 1f);
                }
                catch { }
                PhoneManager.SetCanClosePhone(false);
                PhoneTiles(false);
            }
        }

        public static void Close()
        {
            if (Current == App.None)
                return;
            var was = Current;
            Current = App.None;
            try
            {
                if (was == App.Decks)
                    DeckPanel.Close();
                // --- fv-684 econ-app begin
                if (was == App.Econ)
                    EconPanel.Close();
                // --- fv-684 econ-app end
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PhoneApps close " + was + ": " + e.Message); }
            // the phone is released FIRST and unconditionally - whatever else fails
            if (s_onPhone)
            {
                s_onPhone = false;
                try
                {
                    PhoneManager.SetCanClosePhone(true);
                }
                catch { }
                PhoneTiles(true);
            }
            CoopPlugin.Log.LogInfo("PhoneApps: closed " + was);
        }

        /// <summary>The phone's own close (Tab / Esc) while our app is up: close the app FIRST -
        /// which gives the phone back its CanClosePhone and its tiles - and then let the phone
        /// close as normal. The previous postfix ran after ExitPhoneMode had already been
        /// refused by CanClosePhone=false and dropped the app without restoring either flag:
        /// phone frozen on screen, no way out (2026-09-16, "New deck" refusal then Tab).</summary>
        public static void ExitPhonePrefix()
        {
            try
            {
                if (Current != App.None && s_onPhone)
                {
                    CoopPlugin.Log.LogInfo("PhoneApps: phone closing with " + Current + " open - closing the app first");
                    Close();
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PhoneApps exit: " + e.Message); }
        }

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(PhoneManager), "ExitPhoneMode");
                if (m != null)
                    h.Patch(m, prefix: new HarmonyMethod(typeof(PhoneApps), nameof(ExitPhonePrefix)) { priority = Priority.First });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PhoneApps patches: " + e.Message); }
        }

        // ================================================================ end-of-day overlay

        private static Vector2 s_dayScroll;

        /// <summary>While the vanilla end-of-day report is up and the league has reports, the
        /// standings table sits beside it.</summary>
        private static void DrawDayOverlay()
        {
            try
            {
                // --- fv-871 league-day-sync begin
                // in a league the card is up whenever the report is (its READY advances the day)
                if ((Sync.Rivals.LeagueDay.Reports.Count == 0 && !Sync.Rivals.LeagueDaySync.InLeague) || !EndOfDayReportScreen.IsActive())
                    return;
                // --- fv-871 league-day-sync end
            }
            catch { return; }
            CoopTheme.EnsureBuilt();
            float w = Mathf.Min(760f, Screen.width * 0.46f);
            float h = Mathf.Min(420f, Screen.height * 0.5f);
            var rect = new Rect(Screen.width - w - 16f, Screen.height - h - 24f, w, h);
            CoopTheme.DrawWindowShadow(rect);
            GUI.Box(rect, GUIContent.none, CoopTheme.Window);
            GUILayout.BeginArea(rect);
            CoopTheme.DrawWindowChrome(new Rect(0f, 0f, w, h), "LEAGUE - END OF DAY", "");
            GUILayout.Space(34f);
            GUILayout.BeginVertical(CoopTheme.ContentPanel);
            s_dayScroll = GUILayout.BeginScrollView(s_dayScroll);
            try
            {
                // --- fv-871 league-day-sync begin
                DaySyncCard.DrawReadyBlock(true); // who is ready to advance; READY
                GUILayout.Space(6f);
                // --- fv-871 league-day-sync end
                LeaguePanel.DrawDayTable(w - 24f, false);
            }
            catch (Exception e)
            {
                if (Event.current.type != EventType.Layout)
                    CoopPlugin.Log.LogWarning("LeagueDay overlay: " + e.Message);
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        // ================================================================ draw

        private void OnGUI()
        {
            try
            {
                DrawDayOverlay();
            }
            catch (Exception e)
            {
                if (Event.current.type == EventType.Repaint && Time.unscaledTime - s_lastGuiError > 5f)
                {
                    s_lastGuiError = Time.unscaledTime;
                    CoopPlugin.Log.LogWarning("LeagueDay overlay: " + e.Message);
                }
            }
            if (Current == App.None)
                return;
            try
            {
                DrawApp();
                if (Event.current.type == EventType.Repaint)
                    s_lastGoodRepaint = Time.unscaledTime;
            }
            catch (Exception e)
            {
                // anything that escapes the panel guard (a group left open, a chrome fault):
                // close the app so the phone is never held - and say what happened
                if (Time.unscaledTime - s_lastGuiError > 5f)
                {
                    s_lastGuiError = Time.unscaledTime;
                    CoopPlugin.Log.LogWarning("PhoneApps: " + Current + " frame threw (" + Event.current.type + ") - closing: " + e);
                }
                Sync.HostOnlyFeatures.Notice("That app hit a snag and closed");
                Close();
            }
        }

        private void DrawApp()
        {
            CoopTheme.EnsureBuilt();
            float w = Mathf.Min(560f, Screen.width - 32f);
            float h = Mathf.Min(s_onPhone ? Screen.height * 0.8f : 760f, Screen.height - 40f);
            var rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            CoopTheme.DrawWindowShadow(rect);
            GUI.Box(rect, GUIContent.none, CoopTheme.Window);
            GUILayout.BeginArea(rect);
            string title = Current == App.Trade ? "TRADE" : Current == App.Decks ? "DECKS" : Current == App.Shop ? "SHOP" : Current == App.Coop ? "CO-OP" : "LEAGUE";
            // --- fv-685 cheat-app begin
            if (Current == App.Cheats)
                title = "CHEATS";
            // --- fv-685 cheat-app end
            // --- fv-684 econ-app begin
            if (Current == App.Econ)
                title = "TUNING";
            // --- fv-684 econ-app end
            CoopTheme.DrawWindowChrome(new Rect(0f, 0f, w, h), title, "");
            if (GUI.Button(new Rect(w - 74f, 4f, 66f, 22f), "Back", CoopTheme.ButtonSecondary))
            {
                Close();
                GUILayout.EndArea();
                return;
            }
            GUILayout.Space(34f);
            GUILayout.BeginVertical(CoopTheme.ContentPanel);
            s_scroll = GUILayout.BeginScrollView(s_scroll);
            // an exception inside a panel used to leave IMGUI's layout stack unbalanced: the
            // phone then drew nothing and could not be closed (2026-09-16, DECKS > New deck as a
            // visitor). Catch it, close the app cleanly, and say so.
            try
            {
                var core = CoopCore.Instance;
                if (Current == App.Trade)
                {
                    if (core == null || CoopCore.Role == CoopRole.None)
                        GUILayout.Label("Trading happens in a co-op session: a visitor with the shop they're in (RIVALS league). Nobody to trade with right now.", CoopTheme.LabelDim);
                    else
                    {
                        bool visitor = CoopCore.Role == CoopRole.Client && CoopCore.IsVisiting;
                        if (CoopCore.Role == CoopRole.Host && core.Visitors().Count == 0 && !Sync.Rivals.TradeSync.Open)
                            GUILayout.Label("No visitor in the shop right now. When a rival drops in, pick them here to trade.", CoopTheme.LabelDim);
                        else if (CoopCore.Role == CoopRole.Client && !visitor)
                            GUILayout.Label("You share this shop's till and album - nothing to trade with your own team. Visit a rival to trade.", CoopTheme.LabelDim);
                        TradePanel.Draw(core, w - 24f);
                    }
                }
                else if (Current == App.Decks)
                    DeckPanel.Draw(w - 24f);
                else if (Current == App.Shop)
                    ShopPanel.Draw(w - 24f);
                else if (Current == App.Coop)
                    CoopPanel.Draw(core, w - 24f);
                else if (Current == App.League)
                    LeaguePanel.Draw(w - 24f);
                // --- fv-685 cheat-app begin
                else if (Current == App.Cheats)
                    CheatPanel.Draw(w - 24f);
                // --- fv-685 cheat-app end
                // --- fv-684 econ-app begin
                else if (Current == App.Econ)
                    EconPanel.Draw(core, w - 24f);
                // --- fv-684 econ-app end
            }
            catch (Exception e)
            {
                if (Event.current.type != EventType.Layout)
                {
                    CoopPlugin.Log.LogWarning("PhoneApps: " + Current + " app threw - closing it: " + e);
                    Sync.HostOnlyFeatures.Notice("That app hit a snag and closed");
                    Close();
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }
}
