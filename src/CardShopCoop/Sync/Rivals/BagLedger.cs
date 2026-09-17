using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace CardShopCoop.Sync.Rivals
{
    /// <summary>
    /// What this PC has already credited or handed on, by trip id, so a resent bag can never
    /// count twice (fv-682). Three registers, each a bounded most-recent list:
    /// <list type="bullet">
    /// <item><b>AppliedDeposits</b> - team host: BagDeposit ids applied to this shop.</item>
    /// <item><b>AppliedTrips</b> - captain / owner: delivered team bags applied to a world.</item>
    /// <item><b>DeliveredTrips</b> - lobby server: bags the captain acknowledged.</item>
    /// </list>
    /// The two APPLIED registers change the running world (coins, cards, a box at the door), so
    /// they are written to disk only when the game itself saves (<see cref="FlushWithSave"/>):
    /// a crash between the credit and the save loses the credit, and then the ledger on disk
    /// must not claim it - the resend must apply again. DELIVERED changes nothing in a world
    /// and is written at once. Persisted to BepInEx/config/CardShopCoop.bagledger.json.
    /// </summary>
    public static class BagLedger
    {
        private const int Cap = 128;

        [Serializable]
        private sealed class Data
        {
            public List<string> AppliedDeposits = new List<string>();
            public List<string> AppliedTrips = new List<string>();
            public List<string> DeliveredTrips = new List<string>();
        }

        private static Data _d = new Data();
        private static bool _dirty;

        private static string PathOnDisk()
        {
            return Path.Combine(BepInEx.Paths.ConfigPath, "CardShopCoop.bagledger.json");
        }

        public static void Load()
        {
            try
            {
                string p = PathOnDisk();
                if (File.Exists(p))
                    _d = JsonConvert.DeserializeObject<Data>(File.ReadAllText(p)) ?? new Data();
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("BagLedger load: " + e.Message);
                _d = new Data();
            }
        }

        private static void Write()
        {
            try
            {
                File.WriteAllText(PathOnDisk(), JsonConvert.SerializeObject(_d, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BagLedger save: " + e.Message); }
        }

        private static bool Mark(List<string> list, string id)
        {
            if (string.IsNullOrEmpty(id) || list.Contains(id))
                return false;
            list.Add(id);
            while (list.Count > Cap)
                list.RemoveAt(0);
            return true;
        }

        /// <summary>Team host: has this BagDeposit id been applied to the shop already?</summary>
        public static bool DepositApplied(string id) => !string.IsNullOrEmpty(id) && _d.AppliedDeposits.Contains(id);

        public static void MarkDepositApplied(string id)
        {
            if (Mark(_d.AppliedDeposits, id))
                _dirty = true;
        }

        /// <summary>Captain / owner: has this delivered trip been applied to a world already?</summary>
        public static bool TripApplied(string id) => !string.IsNullOrEmpty(id) && _d.AppliedTrips.Contains(id);

        public static void MarkTripApplied(string id)
        {
            if (Mark(_d.AppliedTrips, id))
                _dirty = true;
        }

        /// <summary>Lobby server: did the captain acknowledge this trip?</summary>
        public static bool TripDelivered(string id) => !string.IsNullOrEmpty(id) && _d.DeliveredTrips.Contains(id);

        public static void MarkTripDelivered(string id)
        {
            if (Mark(_d.DeliveredTrips, id))
                Write();
        }

        /// <summary>The game is saving: the applied registers now match the save on disk.</summary>
        public static void FlushWithSave()
        {
            if (_dirty)
                Write();
        }

        /// <summary>A fresh 8-hex trip id.</summary>
        public static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }
}
