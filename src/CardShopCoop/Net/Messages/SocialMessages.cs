namespace CardShopCoop.Net.Messages
{
    public enum SocialKind : byte
    {
        Chat = 0,   // Text
        Ping = 1,   // Text = where the sender is ("the register", "play table 2")
    }

    /// <summary>Both ways: a chat line or a "come here" ping. A guest sends it to the host,
    /// who fills <c>From</c> and relays to everyone else; the host's own go straight out.</summary>
    [NetworkMessage(MsgType.Social, Policy = MessagePolicy.Any)]
    public sealed class SocialMessage : INetMessage
    {
        public SocialKind Kind;
        public string From = "";
        public string Text = "";
        public MsgType Type
        {
            get
            {
                return MsgType.Social;
            }
        }
    }

    /// <summary>Both ways, on change: what a player is doing, plus their session counters.
    /// From the host it also carries the difficulty / economy readout for guests' windows.</summary>
    [NetworkMessage(MsgType.Activity2, Policy = MessagePolicy.Any)]
    public sealed class ActivityStatusMessage : INetMessage
    {
        public string From = "";
        public string Status = "";      // "in a battle", "editing decks", ...
        public int Packs;               // session counters, this player
        public int Battles;
        public int Sales;
        public string HostInfo = "";    // host only: "difficulty: ... | economy: ..."
        public MsgType Type
        {
            get
            {
                return MsgType.Activity2;
            }
        }
    }
}
