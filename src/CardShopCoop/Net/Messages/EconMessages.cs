namespace CardShopCoop.Net.Messages
{
    /// <summary>Host -> clients: what the two companion plugins (TcgEconomy, TcgDifficulty)
    /// are set to on the host, as the TUNING phone app shows it to a guest. The economy
    /// FACTORS already travel in SettingsState and are applied there; this carries the
    /// names and knobs behind them so a guest reads the same screen the host edits.
    /// Display only on a guest - nothing here is applied.</summary>
    [NetworkMessage(MsgType.EconState, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class EconStateMessage : INetMessage
    {
        // TcgEconomy
        public bool EconPresent;
        public int EconProfile;          // EconomyProfile as int (Vanilla/Tight/Harsh/Custom)
        public bool EconOverride;        // the host itself is under a league override
        public float EconMargin = 1f, EconCard = 1f, EconPick = 1f, EconCost = 1f, EconBill = 1f;
        // TcgDifficulty
        public bool DiffPresent;
        public int DiffProfile;          // DifficultyProfile as int (Off..Custom)
        public bool DiffOverride;
        public float PerPlayer = 0.35f;  // crowd growth per extra player
        public float Staff = 1f;         // staff hire + wages growth per extra player
        public float CustomCap = 1f, CustomRate = 1f, CustomWallet = 1f; // the Custom profile's base multipliers
        public int Players = 1;
        public string EconText = "";     // the plugin's own Describe() lines, as the host sees them
        public string DiffText = "";
        public MsgType Type
        {
            get
            {
                return MsgType.EconState;
            }
        }
    }
}
