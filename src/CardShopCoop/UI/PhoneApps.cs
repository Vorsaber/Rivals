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
            None, Trade, Decks, Shop
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

        private const string TradeId = "CoopTrade";
        private const string DecksId = "CoopDecks";
        private const string ShopId = "CoopShop";

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
                s_registered = true;
                CoopPlugin.Log.LogInfo("PhoneApps: registered Trade, Decks and Shop on the phone");
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
            if (Current == App.Decks)
                DeckPanel.Close();
            Current = App.None;
            if (s_onPhone)
            {
                s_onPhone = false;
                PhoneManager.SetCanClosePhone(true);
                PhoneTiles(true);
            }
        }

        /// <summary>The phone closed (Esc / the close key): our app goes with it.</summary>
        public static void ExitPhonePostfix()
        {
            if (Current != App.None && s_onPhone)
            {
                if (Current == App.Decks)
                    DeckPanel.Close();
                Current = App.None;
                s_onPhone = false;
            }
        }

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(PhoneManager), "ExitPhoneMode");
                if (m != null)
                    h.Patch(m, postfix: new HarmonyMethod(typeof(PhoneApps), nameof(ExitPhonePostfix)));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PhoneApps patches: " + e.Message); }
        }

        // ================================================================ draw

        private void OnGUI()
        {
            if (Current == App.None)
                return;
            CoopTheme.EnsureBuilt();
            float w = Mathf.Min(560f, Screen.width - 32f);
            float h = Mathf.Min(s_onPhone ? Screen.height * 0.8f : 760f, Screen.height - 40f);
            var rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            CoopTheme.DrawWindowShadow(rect);
            GUI.Box(rect, GUIContent.none, CoopTheme.Window);
            GUILayout.BeginArea(rect);
            string title = Current == App.Trade ? "TRADE" : Current == App.Decks ? "DECKS" : "SHOP";
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
            else
                ShopPanel.Draw(w - 24f);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }
}
