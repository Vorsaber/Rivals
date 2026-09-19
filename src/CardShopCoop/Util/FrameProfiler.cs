using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// fv-877: a per-frame cost census behind <c>Diagnostics.PerfDebug</c>. Off (the default)
    /// it installs nothing - no patch, no component, no cost. On, it times:
    ///  - every per-frame entry point this plugin owns (Update / OnGUI / the lobby tick / the
    ///    stock-value walk), keyed by method;
    ///  - the three engine calls that turned out to be where solo-play frames go on Dan's host
    ///    (2026-09-19 live sample: ~55% of a 50 ms frame): <c>GameObject.SetActive</c> when it
    ///    really flips state, <c>Canvas.ForceUpdateCanvases</c> and
    ///    <c>LayoutRebuilder.ForceRebuildLayoutImmediate</c> - each attributed to the MANAGED
    ///    CALLER that asked for it (vanilla, another mod or us), so the log names the script.
    /// Every 5 s one <c>[profile]</c> line: frames, ms/frame, then the top costs per frame.
    /// Nested SetActive calls (OnEnable chains) are charged to the outermost caller only.
    /// </summary>
    internal static class FrameProfiler
    {
        private sealed class Acc
        {
            public long Ticks;
            public int Calls;
        }

        private const float ReportEvery = 5f;
        private static readonly Dictionary<string, Acc> s_costs = new Dictionary<string, Acc>();
        private static readonly Dictionary<MethodBase, string> s_names = new Dictionary<MethodBase, string>();
        private static int s_setActiveDepth;
        private static bool s_installed;

        public static bool Installed => s_installed;

        public static void Install(Harmony h)
        {
            if (s_installed)
                return;
            s_installed = true;
            int patched = 0;

            // engine calls, attributed to whoever asked
            patched += Attributed(h, typeof(GameObject), "SetActive", nameof(SetActivePre), nameof(SetActivePost), typeof(bool));
            patched += Attributed(h, typeof(Canvas), "ForceUpdateCanvases", nameof(TimedPre), nameof(AttributedPost));
            patched += Attributed(h, typeof(UnityEngine.UI.LayoutRebuilder), "ForceRebuildLayoutImmediate", nameof(TimedPre), nameof(AttributedPost), typeof(RectTransform));

            // our own per-frame entry points, keyed by method
            var ours = new[]
            {
                (typeof(CoopCore), "Update"), (typeof(CoopCore), "OnGUI"),
                (typeof(Sync.EconSync), "Update"),
                (typeof(Sync.Rivals.RivalsLobby), "Update"),
                (typeof(Sync.Rivals.StockValue), "Compute"),
                (typeof(Sync.Rivals.LeagueDaySync), "Tick"),
                (typeof(Sync.Rivals.LeagueSession), "Tick"),
                (typeof(Sync.Rivals.VisitorBag), "Tick"),
                (typeof(UI.BagOverlay), "Update"), (typeof(UI.BagOverlay), "OnGUI"),
                (typeof(UI.DaySyncCard), "Update"), (typeof(UI.DaySyncCard), "OnGUI"),
                (typeof(UI.PhoneApps), "Update"), (typeof(UI.PhoneApps), "OnGUI"),
                (typeof(UI.CheatMenu), "Update"), (typeof(UI.CheatMenu), "OnGUI"),
                (typeof(UI.ChatOverlay), "Update"), (typeof(UI.ChatOverlay), "OnGUI"),
                (typeof(UI.PurchaseConfirm), "Update"), (typeof(UI.PurchaseConfirm), "OnGUI"),
            };
            foreach (var (type, method) in ours)
                patched += Timed(h, type, method);
            // the companions, if present (no hard reference)
            foreach (var tn in new[] { "TcgDifficulty.Plugin, TcgDifficulty", "TcgEconomy.Plugin, TcgEconomy" })
            {
                Type t = null;
                try { t = Type.GetType(tn, false); } catch (Exception) { }
                if (t != null)
                    patched += Timed(h, t, "Update");
            }

            var go = new GameObject("CardShopCoop.FrameProfiler");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Reporter>();
            CoopPlugin.Log.LogInfo("[profile] frame profiler on (Diagnostics.PerfDebug): " + patched
                + " timers installed; one [profile] line every " + ReportEvery + " s");
        }

        // ------------------------------------------------------------------ patch plumbing

        private static int Timed(Harmony h, Type type, string method)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                    return 0;
                s_names[original] = type.Name + "." + method;
                h.Patch(original,
                    prefix: new HarmonyMethod(typeof(FrameProfiler), nameof(TimedPre)),
                    postfix: new HarmonyMethod(typeof(FrameProfiler), nameof(TimedPost)));
                return 1;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("[profile] cannot time " + type.Name + "." + method + ": " + e.Message);
                return 0;
            }
        }

        private static int Attributed(Harmony h, Type type, string method, string pre, string post, params Type[] args)
        {
            try
            {
                var original = AccessTools.Method(type, method, args.Length > 0 ? args : null);
                if (original == null)
                    return 0;
                s_names[original] = type.Name + "." + method;
                h.Patch(original,
                    prefix: new HarmonyMethod(typeof(FrameProfiler), pre),
                    postfix: new HarmonyMethod(typeof(FrameProfiler), post));
                return 1;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("[profile] cannot attribute " + type.Name + "." + method + ": " + e.Message);
                return 0;
            }
        }

        public static void TimedPre(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void TimedPost(long __state, MethodBase __originalMethod)
        {
            Add(Name(__originalMethod), Stopwatch.GetTimestamp() - __state);
        }

        public static void AttributedPost(long __state, MethodBase __originalMethod)
        {
            Add(Name(__originalMethod) + " <- " + Caller(), Stopwatch.GetTimestamp() - __state);
        }

        // SetActive: only a REAL flip costs anything (Awake/OnEnable chains, canvas
        // re-registration, layout dirtying); a no-op call is a few ns and is not counted.
        public static void SetActivePre(GameObject __instance, bool value, out long __state)
        {
            __state = 0L;
            try
            {
                if (__instance == null || __instance.activeSelf == value)
                    return;
            }
            catch (Exception) { return; }
            s_setActiveDepth++;
            __state = Stopwatch.GetTimestamp();
        }

        public static void SetActivePost(GameObject __instance, bool value, long __state)
        {
            if (__state == 0L)
                return;
            long dt = Stopwatch.GetTimestamp() - __state;
            s_setActiveDepth--;
            if (s_setActiveDepth > 0)
                return; // charged to the outermost flip
            string who = Caller();
            Add("SetActive(" + (value ? "on" : "off") + ") <- " + who, dt);
        }

        private static string Name(MethodBase m)
        {
            return m != null && s_names.TryGetValue(m, out var n) ? n : (m != null ? m.Name : "?");
        }

        /// <summary>The first managed frame above the patched method that is not us, not
        /// Harmony's stub, not UnityEngine's own wrapper: the script that made the call.</summary>
        private static string Caller()
        {
            try
            {
                var st = new StackTrace(2, false);
                for (int i = 0; i < st.FrameCount; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    var t = m?.DeclaringType;
                    if (t == null)
                        continue;
                    if (t == typeof(FrameProfiler) || t == typeof(GameObject) || t == typeof(Canvas)
                        || t == typeof(UnityEngine.UI.LayoutRebuilder))
                        continue;
                    string ns = t.Namespace ?? "";
                    if (ns.StartsWith("HarmonyLib", StringComparison.Ordinal) || ns.StartsWith("MonoMod", StringComparison.Ordinal))
                        continue;
                    if (m.Name.StartsWith("DMD<", StringComparison.Ordinal))
                        continue;
                    return t.FullName + "." + m.Name;
                }
            }
            catch (Exception) { }
            return "?";
        }

        private static void Add(string key, long ticks)
        {
            if (!s_costs.TryGetValue(key, out var a))
                s_costs[key] = a = new Acc();
            a.Ticks += ticks;
            a.Calls++;
        }

        // ------------------------------------------------------------------ the report

        private sealed class Reporter : MonoBehaviour
        {
            private float _windowStart = -1f;
            private int _frames;
            private double _frameMsSum;
            private double _frameMsMax;

            private void LateUpdate()
            {
                float now = Time.unscaledTime;
                if (_windowStart < 0f)
                {
                    _windowStart = now;
                    return;
                }
                _frames++;
                s_setActiveDepth = 0; // a SetActive that threw never reached its postfix
                double ms = Time.unscaledDeltaTime * 1000.0;
                _frameMsSum += ms;
                if (ms > _frameMsMax)
                    _frameMsMax = ms;
                if (now - _windowStart < ReportEvery)
                    return;
                Report(now - _windowStart);
                _windowStart = now;
                _frames = 0;
                _frameMsSum = 0;
                _frameMsMax = 0;
                s_costs.Clear();
            }

            private void Report(float seconds)
            {
                if (_frames == 0)
                    return;
                var sb = new StringBuilder();
                sb.Append("[profile] ").Append(seconds.ToString("F1")).Append(" s, ").Append(_frames).Append(" frames, ")
                  .Append((_frameMsSum / _frames).ToString("F1")).Append(" ms/frame avg, max ")
                  .Append(_frameMsMax.ToString("F0")).Append(" ms");
                var rows = new List<KeyValuePair<string, Acc>>(s_costs);
                rows.Sort((x, y) => y.Value.Ticks.CompareTo(x.Value.Ticks));
                double perTick = 1000.0 / Stopwatch.Frequency;
                int shown = 0;
                foreach (var kv in rows)
                {
                    double msPerFrame = kv.Value.Ticks * perTick / _frames;
                    if (msPerFrame < 0.05 || shown >= 10)
                        break;
                    shown++;
                    sb.Append(" | ").Append(kv.Key).Append(' ')
                      .Append(msPerFrame.ToString("F2")).Append(" ms/f (")
                      .Append(((double)kv.Value.Calls / _frames).ToString("F1")).Append(" calls/f)");
                }
                if (shown == 0)
                    sb.Append(" | nothing timed above 0.05 ms/frame");
                CoopPlugin.Log.LogInfo(sb.ToString());
            }
        }
    }
}
