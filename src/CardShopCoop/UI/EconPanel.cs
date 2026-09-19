using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using CardShopCoop.Sync;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>The TUNING app: the two companion plugins' settings - TcgEconomy's profile and
    /// its five Custom factors, TcgDifficulty's crowd profile, per-player growth, staff cost
    /// growth and its three Custom multipliers. The shop's owner (solo or host) edits them
    /// live - a profile button switches at once, a slider is written when it is let go, the
    /// plugin applies where the value is read (prices) or on the next crowd evaluation. A
    /// guest reads the host's values off <see cref="EconSync.Remote"/> and edits nothing.
    /// Without a plugin its section says so.</summary>
    internal static class EconPanel
    {
        public static bool Owner => CoopCore.Role != CoopRole.Client;

        private static string s_status = "";
        private static float s_statusAt = -100f;
        private static float s_openedAt = -1f;

        // slider drags: value held here until the mouse is let go, then written once
        private static readonly Dictionary<string, float> s_pending = new Dictionary<string, float>();

        private struct Knob
        {
            public string Key, Label;
            public float Min, Max;
            public Knob(string key, string label, float min, float max)
            {
                Key = key;
                Label = label;
                Min = min;
                Max = max;
            }
        }

        // ranges are the plugins' own clamps (EconomyTuning.LocalFactors / Difficulty.Effective)
        private static readonly Knob[] EconKnobs =
        {
            new Knob("CustomMarginScale", "margin", 0.05f, 3f),
            new Knob("CustomCardValueScale", "card value", 0.05f, 3f),
            new Knob("CustomPickiness", "pickiness", 0.2f, 10f),
            new Knob("CustomStockCostScale", "stock cost", 0.2f, 5f),
            new Knob("CustomBillScale", "bills", 0.1f, 10f),
        };
        private static readonly Knob[] DiffKnobs =
        {
            new Knob("PerPlayerScale", "crowd per extra player", 0f, 2f),
            new Knob("StaffCostPerPlayer", "staff cost per extra player", 0f, 10f),
        };
        private static readonly Knob[] DiffCustomKnobs =
        {
            new Knob("CustomCustomers", "customers", 0.1f, 5f),
            new Knob("CustomArrivalRate", "arrivals", 0.1f, 5f),
            new Knob("CustomWallet", "wallets", 0.1f, 5f),
            // fv-681 (Difficulty v2) keys: absent on an older TcgDifficulty, so the row just does not draw
            new Knob("CustomPatience", "patience", 0.25f, 4f),
            new Knob("CustomDriftSpeed", "price drift speed", 0f, 5f),
            new Knob("CustomAiStrength", "AI strength", 0.25f, 4f),
        };

        public static void Draw(CoopCore core, float width)
        {
            if (s_openedAt < 0f)
                s_openedAt = Time.unscaledTime;
            if (Owner)
                DrawOwner(width);
            else
                DrawGuest(width);
            if (Time.unscaledTime - s_statusAt < 6f && s_status.Length > 0)
                GUILayout.Label(s_status, CoopTheme.LabelDimWrap);
        }

        /// <summary>PhoneApps calls this when the app closes so the next open starts clean.</summary>
        public static void Close()
        {
            s_pending.Clear();
            s_openedAt = -1f;
        }

        private static void Say(string text)
        {
            s_status = text;
            s_statusAt = Time.unscaledTime;
        }

        // ================================================================ owner

        private static void DrawOwner(float width)
        {
            bool inSession = CoopCore.Role == CoopRole.Host;
            GUILayout.Label(inSession
                ? "Your settings apply to the whole session; guests read them on their own TUNING app."
                : "Solo: these are this PC's TcgEconomy / TcgDifficulty settings. In a session the host's apply to everyone.", CoopTheme.LabelDimWrap);
            GUILayout.Space(4f);

            // ---------------- economy
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("ECONOMY", CoopTheme.SectionHeader);
            if (!Companions.Economy.Present)
                GUILayout.Label("TcgEconomy.dll is not installed on this PC - vanilla prices.", CoopTheme.LabelDimWrap);
            else
            {
                GUILayout.Label(Companions.Economy.Describe(), CoopTheme.LabelWrap);
                if (EconSync.Econ.HasOverride)
                    GUILayout.Label("A league override is in force: the league host's factors apply while it runs. Your profile below counts outside the league.", CoopTheme.LabelWarn);
                int profile = Companions.Economy.LocalProfile();
                int picked = ProfileRow(Companions.Economy.Profiles, profile);
                if (picked >= 0 && picked != profile)
                {
                    Companions.Economy.SetProfile(picked);
                    EconSync.MarkDirty();
                    Say("economy: " + Companions.Economy.Describe());
                }
                Companions.Economy.LocalFactors(out float m, out float c, out float p, out float k, out float b);
                float[] cur = { m, c, p, k, b };
                bool custom = profile == 3;
                for (int i = 0; i < EconKnobs.Length; i++)
                {
                    if (custom)
                    {
                        if (KnobRow(Companions.Economy.Guid, "Economy", EconKnobs[i], width))
                        {
                            EconSync.MarkDirty();
                            Say("economy: " + Companions.Economy.Describe());
                        }
                    }
                    else
                        ValueRow(EconKnobs[i].Label, cur[i]);
                }
                if (!custom)
                    GUILayout.Label("<size=10>pick Custom to set each factor</size>", CoopTheme.LabelDim);
            }
            GUILayout.EndVertical();
            GUILayout.Space(6f);

            // ---------------- difficulty
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("DIFFICULTY", CoopTheme.SectionHeader);
            if (!Companions.Difficulty.Present)
                GUILayout.Label("TcgDifficulty.dll is not installed on this PC - the game's own crowd.", CoopTheme.LabelDimWrap);
            else
            {
                GUILayout.Label(Companions.Difficulty.Describe(), CoopTheme.LabelWrap);
                if (EconSync.Diff.HasOverride)
                    GUILayout.Label("A league override is in force: the league host's profile and growth apply while it runs. Edits below take effect once it ends.", CoopTheme.LabelWarn);
                Companions.Difficulty.LocalSettings(out int profile, out _, out _);
                int picked = ProfileRow(Companions.Difficulty.Profiles, profile);
                // --- fv-874 difficulty-log-line begin
                // every click goes through SetProfile, even on the profile already selected:
                // it is idempotent and it is what writes the 'Difficulty: ...' log line
                if (picked >= 0)
                {
                    Companions.Difficulty.SetProfile(picked);
                    if (picked != profile)
                        EconSync.MarkDirty();
                    Say("difficulty: " + Companions.Difficulty.Describe());
                }
                // --- fv-874 difficulty-log-line end
                bool changed = false;
                for (int i = 0; i < DiffKnobs.Length; i++)
                    changed |= KnobRow(Companions.Difficulty.Guid, "Difficulty", DiffKnobs[i], width);
                if (profile == 5)
                {
                    GUILayout.Label("<size=10>Custom base multipliers (at one player)</size>", CoopTheme.LabelDim);
                    for (int i = 0; i < DiffCustomKnobs.Length; i++)
                        changed |= KnobRow(Companions.Difficulty.Guid, "Difficulty", DiffCustomKnobs[i], width);
                }
                if (changed)
                {
                    Companions.Difficulty.Reapply();
                    Companions.Difficulty.ApplyStaffCosts();
                    EconSync.MarkDirty();
                    Say("difficulty: " + Companions.Difficulty.Describe());
                }
            }
            GUILayout.EndVertical();
            GUILayout.Space(4f);
            GUILayout.Label("<size=10>Changes apply live: prices where they are read, the crowd on its next evaluation, staff costs at once. Both plugins write their own config file.</size>", CoopTheme.LabelDimWrap);
        }

        /// <summary>A row of profile buttons; the current one drawn as primary. Returns the
        /// pressed index or -1.</summary>
        private static int ProfileRow(string[] names, int current)
        {
            int picked = -1;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < names.Length; i++)
                if (GUILayout.Button(names[i], i == current ? CoopTheme.ButtonPrimary : CoopTheme.ButtonSecondary))
                    picked = i;
            GUILayout.EndHorizontal();
            return picked;
        }

        private static void ValueRow(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, CoopTheme.LabelDim, GUILayout.Width(200f));
            GUILayout.Label($"x{value:0.00}", CoopTheme.Label);
            GUILayout.EndHorizontal();
        }

        /// <summary>A slider over one config float of a companion plugin. The drag is held in
        /// s_pending and written once when the mouse is let go (a config write saves the
        /// plugin's file; per frame would thrash it). Returns true on the write.</summary>
        private static bool KnobRow(string guid, string section, Knob k, float width)
        {
            string id = guid + "/" + k.Key;
            float stored;
            if (!Companions.TryGetFloat(guid, section, k.Key, out stored))
                return false;
            float shown = s_pending.TryGetValue(id, out float pend) ? pend : stored;
            GUILayout.BeginHorizontal();
            GUILayout.Label(k.Label, CoopTheme.LabelDim, GUILayout.Width(200f));
            float v = GUILayout.HorizontalSlider(shown, k.Min, k.Max);
            GUILayout.Label($"x{v:0.00}", CoopTheme.Label, GUILayout.Width(56f));
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(v, shown))
                s_pending[id] = v;
            bool wrote = false;
            if (s_pending.ContainsKey(id) && !Input.GetMouseButton(0) && Event.current.type == EventType.Repaint)
            {
                float commit = s_pending[id];
                s_pending.Remove(id);
                if (!Mathf.Approximately(commit, stored))
                    wrote = Companions.SetFloat(guid, section, k.Key, commit);
            }
            return wrote;
        }

        // ================================================================ guest

        private static void DrawGuest(float width)
        {
            var r = EconSync.Remote;
            GUILayout.Label("The host's settings - they apply to this shop. Only the host edits them.", CoopTheme.LabelDimWrap);
            GUILayout.Space(4f);
            if (r == null)
            {
                bool late = Time.unscaledTime - s_openedAt > EconSync.ResendSeconds + 5f;
                GUILayout.Label(late
                    ? "Nothing from the host yet. Their CardShopCoop may predate the TUNING app - what is applied here is still their economy:"
                    : "Waiting for the host's settings...", CoopTheme.LabelDimWrap);
                if (late && Companions.Economy.Present)
                    GUILayout.Label("economy: " + Companions.Economy.Describe(), CoopTheme.LabelWrap);
                return;
            }
            float age = Time.unscaledTime - EconSync.RemoteAt;

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("ECONOMY", CoopTheme.SectionHeader);
            if (!r.EconPresent)
                GUILayout.Label("The host has no TcgEconomy.dll - vanilla prices.", CoopTheme.LabelDimWrap);
            else
            {
                GUILayout.Label(r.EconText, CoopTheme.LabelWrap);
                if (r.EconOverride)
                    GUILayout.Label("league override in force on the host", CoopTheme.LabelWarn);
                ChipRow(Companions.Economy.Profiles, r.EconProfile);
                float[] cur = { r.EconMargin, r.EconCard, r.EconPick, r.EconCost, r.EconBill };
                for (int i = 0; i < EconKnobs.Length; i++)
                    ValueRow(EconKnobs[i].Label, cur[i]);
            }
            GUILayout.EndVertical();
            GUILayout.Space(6f);

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("DIFFICULTY", CoopTheme.SectionHeader);
            if (!r.DiffPresent)
                GUILayout.Label("The host has no TcgDifficulty.dll - the game's own crowd.", CoopTheme.LabelDimWrap);
            else
            {
                GUILayout.Label(r.DiffText, CoopTheme.LabelWrap);
                if (r.DiffOverride)
                    GUILayout.Label("league override in force on the host", CoopTheme.LabelWarn);
                ChipRow(Companions.Difficulty.Profiles, r.DiffProfile);
                ValueRow(DiffKnobs[0].Label, r.PerPlayer);
                ValueRow(DiffKnobs[1].Label, r.Staff);
                if (r.DiffProfile == 5)
                {
                    ValueRow("custom customers", r.CustomCap);
                    ValueRow("custom arrivals", r.CustomRate);
                    ValueRow("custom wallets", r.CustomWallet);
                }
                GUILayout.Label($"<size=10>{r.Players} player{(r.Players == 1 ? "" : "s")} counted</size>", CoopTheme.LabelDim);
            }
            GUILayout.EndVertical();
            GUILayout.Space(4f);
            GUILayout.Label($"<size=10>updated {Mathf.RoundToInt(age)} s ago</size>", CoopTheme.LabelDim);
        }

        private static void ChipRow(string[] names, int current)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < names.Length; i++)
                CoopTheme.Chip(names[i], i == current ? CoopTheme.ChipSuccess : CoopTheme.ChipInfo);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
    }
}
