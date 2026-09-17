using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>The CO-OP app (P1): who is here and what they are doing, chat, wave / ping,
    /// the sleep vote, and the difficulty / economy readout - the F2 session panel, on the phone.</summary>
    internal static class CoopPanel
    {
        private static string s_chat = "";

        public static void Draw(CoopCore core, float width)
        {
            if (core == null || CoopCore.Role == CoopRole.None)
            {
                GUILayout.Label("Not in a co-op session. Host or join one on F2 > OG CO-OP.", CoopTheme.LabelDim);
                return;
            }

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label(CoopCore.Role == CoopRole.Host ? "YOUR SHOP - PLAYERS" : "PLAYERS", CoopTheme.SectionHeader);
            GUILayout.Label($"you - {Social.LocalStatus()}   <size=10>packs {Social.Packs} · battles {Social.Battles}</size>", CoopTheme.Label);
            foreach (var kv in Social.Others)
            {
                var ps = kv.Value;
                GUILayout.Label($"{ps.Name} - {ps.Status}   <size=10>packs {ps.Packs} · battles {ps.Battles} · sales {ps.Sales}</size>", CoopTheme.Label);
            }
            if (CoopCore.Role == CoopRole.Client && !string.IsNullOrEmpty(Social.HostInfo))
                GUILayout.Label("<size=10>host " + Social.HostInfo + "</size>", CoopTheme.LabelDim);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Wave", CoopTheme.ButtonSecondary))
                core.SendEmote();
            if (GUILayout.Button("Need you here", CoopTheme.ButtonSecondary))
                Social.SendPing();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("END OF DAY", CoopTheme.SectionHeader);
            if (CoopCore.Role == CoopRole.Client)
            {
                GUILayout.Label("The host ends the day. Say you're ready and they get told when everyone is.", CoopTheme.LabelDim);
                if (GUILayout.Button("Ready to sleep  (toggle)", CoopTheme.ButtonPrimary))
                    SleepVote.ClientPressed();
            }
            else
                GUILayout.Label("Press Enter at closing time as usual; with guests still busy the first press waits for their votes, the second goes anyway.", CoopTheme.LabelDim);
            GUILayout.EndVertical();

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("TUNING", CoopTheme.SectionHeader);
            string diff = null, econ = null;
            try
            {
                diff = Util.Companions.Difficulty.Present ? Util.Companions.Difficulty.Describe() : null;
            }
            catch { }
            try
            {
                econ = Util.Companions.Economy.Present ? Util.Companions.Economy.Describe() : null;
            }
            catch { }
            GUILayout.Label("difficulty: " + (diff ?? "TcgDifficulty not installed"), CoopTheme.Label);
            GUILayout.Label("economy: " + (econ ?? "TcgEconomy not installed"), CoopTheme.Label);
            if (Sync.Rivals.RivalsLobby.MyPriceRank >= 0)
                GUILayout.Label($"league price rank {Sync.Rivals.RivalsLobby.MyPriceRank + 1} -> customers x{Sync.Rivals.RivalsLobby.CrowdMultiplier:0.00}", CoopTheme.LabelDim);
            GUILayout.Label("<size=10>host edits on F4 (cheats) / F2 > SETTINGS; guests see the host's values</size>", CoopTheme.LabelDim);
            GUILayout.EndVertical();

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("CHAT", CoopTheme.SectionHeader);
            var lines = Social.Lines;
            for (int i = Mathf.Max(0, lines.Count - 10); i < lines.Count; i++)
            {
                var l = lines[i];
                GUILayout.Label((l.IsPing ? "! " : "") + l.From + ": " + l.Text, l.IsPing ? CoopTheme.LabelWarn : CoopTheme.Label);
            }
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("coop_phone_chat");
            s_chat = GUILayout.TextField(s_chat ?? "", 200);
            bool enter = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == "coop_phone_chat";
            if (GUILayout.Button("Send", CoopTheme.ButtonSecondary, GUILayout.Width(60f)) || enter)
            {
                if (!string.IsNullOrWhiteSpace(s_chat))
                    Social.SendChat(s_chat);
                s_chat = "";
                if (enter)
                    Event.current.Use();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }
    }
}
