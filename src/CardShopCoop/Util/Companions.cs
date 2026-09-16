using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Soft bridge to the standalone companion plugins (TcgEconomy, TcgDifficulty). They are
    /// separate BepInEx mods with no dependency on this one; this mod finds them by GUID at
    /// runtime and drives their small public static API by reflection, so a PC without them
    /// runs co-op exactly as before. Everything here is null-safe and never throws out.
    /// </summary>
    internal static class Companions
    {
        /// <summary>Read / write a float entry in a companion plugin's own config file
        /// (BaseUnityPlugin.Config), so the cheat menu can offer live sliders for it.</summary>
        internal static bool TryGetFloat(string guid, string section, string key, out float value)
        {
            value = 0f;
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info.Instance == null)
                    return false;
                if (info.Instance.Config.TryGetEntry<float>(section, key, out var entry))
                {
                    value = entry.Value;
                    return true;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"companion {guid} {section}.{key}: {e.Message}"); }
            return false;
        }

        internal static bool SetFloat(string guid, string section, string key, float value)
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info.Instance == null)
                    return false;
                if (info.Instance.Config.TryGetEntry<float>(section, key, out var entry))
                {
                    entry.Value = value;
                    return true;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"companion {guid} {section}.{key}: {e.Message}"); }
            return false;
        }

        // ------------------------------------------------------------ Economy (TcgEconomy)

        internal static class Economy
        {
            public const string Guid = "com.vorsaber.tcgeconomy";
            private static bool s_resolved;
            private static Type s_type;
            private static MethodInfo s_local, s_setOverride, s_clearOverride, s_setProfile, s_describe;

            public static bool Present
            {
                get
                {
                    Resolve();
                    return s_type != null;
                }
            }

            private static void Resolve()
            {
                if (s_resolved)
                    return;
                try
                {
                    // plugin load order is not ours to pick: keep looking until it shows up
                    if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info.Instance == null)
                        return;
                    s_resolved = true;
                    s_type = info.Instance.GetType().Assembly.GetType("TcgEconomy.EconomyTuning");
                    if (s_type == null)
                        return;
                    s_local = s_type.GetMethod("LocalFactors", BindingFlags.Public | BindingFlags.Static);
                    s_setOverride = s_type.GetMethod("SetOverride", BindingFlags.Public | BindingFlags.Static);
                    s_clearOverride = s_type.GetMethod("ClearOverride", BindingFlags.Public | BindingFlags.Static);
                    s_setProfile = s_type.GetMethod("SetProfile", BindingFlags.Public | BindingFlags.Static);
                    s_describe = s_type.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static);
                    CoopPlugin.Log.LogInfo("companion: TcgEconomy found");
                }
                catch (Exception e)
                {
                    s_type = null;
                    CoopPlugin.Log.LogWarning("companion TcgEconomy: " + e.Message);
                }
            }

            /// <summary>This PC's configured factors (1s when the plugin is absent).</summary>
            public static void LocalFactors(out float margin, out float card, out float pick, out float cost, out float bill)
            {
                margin = card = pick = cost = bill = 1f;
                if (!Present || s_local == null)
                    return;
                try
                {
                    var args = new object[5];
                    s_local.Invoke(null, args);
                    margin = (float)args[0];
                    card = (float)args[1];
                    pick = (float)args[2];
                    cost = (float)args[3];
                    bill = (float)args[4];
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("companion TcgEconomy.LocalFactors: " + e.Message); }
            }

            public static void SetOverride(float margin, float card, float pick, float cost, float bill)
            {
                if (!Present || s_setOverride == null)
                    return;
                try
                {
                    s_setOverride.Invoke(null, new object[] { margin, card, pick, cost, bill });
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("companion TcgEconomy.SetOverride: " + e.Message); }
            }

            public static void ClearOverride()
            {
                if (!Present || s_clearOverride == null)
                    return;
                try
                {
                    s_clearOverride.Invoke(null, null);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("companion TcgEconomy.ClearOverride: " + e.Message); }
            }

            public static void SetProfile(int profile)
            {
                if (!Present || s_setProfile == null)
                    return;
                try
                {
                    s_setProfile.Invoke(null, new object[] { profile });
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("companion TcgEconomy.SetProfile: " + e.Message); }
            }

            public static string Describe()
            {
                if (!Present)
                    return "TcgEconomy.dll not installed";
                try
                {
                    return s_describe != null ? (string)s_describe.Invoke(null, null) : "?";
                }
                catch (Exception e) { return "error: " + e.Message; }
            }

            public static readonly string[] Profiles = { "Vanilla", "Tight", "Harsh", "Custom" };

            /// <summary>Rivals: extra pickiness for this shop (1 = none). No-op without the plugin.</summary>
            public static void SetExternalPickiness(float value)
            {
                if (!Present)
                    return;
                try
                {
                    var f = s_type.GetField("ExternalPickiness", BindingFlags.Public | BindingFlags.Static);
                    f?.SetValue(null, value);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("companion TcgEconomy.ExternalPickiness: " + e.Message); }
            }
        }

        // ------------------------------------------------------------ Difficulty (TcgDifficulty)

        internal static class Difficulty
        {
            public const string Guid = "com.vorsaber.tcgdifficulty";
            private static bool s_resolved;
            private static Type s_type;
            private static FieldInfo s_players, s_authority;
            private static MethodInfo s_setProfile, s_describe, s_reapply, s_staff;

            public static bool Present
            {
                get
                {
                    Resolve();
                    return s_type != null;
                }
            }

            private static void Resolve()
            {
                if (s_resolved)
                    return;
                try
                {
                    // plugin load order is not ours to pick: keep looking until it shows up
                    if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info.Instance == null)
                        return;
                    s_resolved = true;
                    s_type = info.Instance.GetType().Assembly.GetType("TcgDifficulty.Difficulty");
                    if (s_type == null)
                        return;
                    s_players = s_type.GetField("PlayerCountProvider", BindingFlags.Public | BindingFlags.Static);
                    s_authority = s_type.GetField("AuthorityProvider", BindingFlags.Public | BindingFlags.Static);
                    s_setProfile = s_type.GetMethod("SetProfile", BindingFlags.Public | BindingFlags.Static);
                    s_describe = s_type.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static);
                    s_reapply = s_type.GetMethod("Reapply", BindingFlags.Public | BindingFlags.Static);
                    s_staff = s_type.GetMethod("ApplyStaffCosts", BindingFlags.Public | BindingFlags.Static);
                    CoopPlugin.Log.LogInfo("companion: TcgDifficulty found");
                }
                catch (Exception e)
                {
                    s_type = null;
                    CoopPlugin.Log.LogWarning("companion TcgDifficulty: " + e.Message);
                }
            }

            /// <summary>Tell the plugin how many people are playing and whether this PC runs
            /// the crowd (host / solo) or mirrors it (guest).</summary>
            public static void SetProviders(Func<int> players, Func<bool> authority)
            {
                if (!Present)
                    return;
                try
                {
                    s_players?.SetValue(null, players);
                    s_authority?.SetValue(null, authority);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("companion TcgDifficulty.SetProviders: " + e.Message); }
            }

            public static void SetProfile(int profile)
            {
                if (!Present || s_setProfile == null)
                    return;
                try
                {
                    s_setProfile.Invoke(null, new object[] { profile });
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("companion TcgDifficulty.SetProfile: " + e.Message); }
            }

            public static void Reapply()
            {
                if (!Present || s_reapply == null)
                    return;
                try
                {
                    s_reapply.Invoke(null, null);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("companion TcgDifficulty.Reapply: " + e.Message); }
            }

            public static void ApplyStaffCosts()
            {
                if (!Present || s_staff == null)
                    return;
                try
                {
                    s_staff.Invoke(null, null);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("companion TcgDifficulty.ApplyStaffCosts: " + e.Message); }
            }

            public static string Describe()
            {
                if (!Present)
                    return "TcgDifficulty.dll not installed";
                try
                {
                    return s_describe != null ? (string)s_describe.Invoke(null, null) : "?";
                }
                catch (Exception e) { return "error: " + e.Message; }
            }

            public static readonly string[] Profiles = { "Off", "Relaxed", "Normal", "Busy", "Chaos", "Custom" };
        }
    }
}
