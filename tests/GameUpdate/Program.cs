using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using CardShopCoop.Sync;
using CardShopCoop;
using CardShopCoop.Patches;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Pass the installed game directory. Runs managed checks only; no game is started.");
            return 2;
        }
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var folder in new[] { "Card Shop Simulator_Data/Managed", "BepInEx/core" })
            {
                var path = Path.Combine(args[0], folder, name);
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
            return null;
        };
        try
        {
            Run();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static object Market(string name, params object[] args) =>
        typeof(MarketSync).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new Exception(description);
        Console.WriteLine("PASS " + description);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Check((bool)Market("IsVanillaCardExpansion", ECardExpansionType.Ascension),
            "Ascension uses full snapshots rather than transient modded deltas");
        var message = new MarketStateMessage { RollGen = 7 };
        var host = new List<MarketPrice>
        {
            new MarketPrice { generatedMarketPrice = 123.5f, pricePercentChangeList = -12.25f },
            new MarketPrice { generatedMarketPrice = 0f, pricePercentChangeList = 200f },
        };
        Market("FillMarket", message.GenCardMarketPriceListAscension, host);
        var checksum = (int)Market("WireChecksum", message);
        var payload = WireCodec.Serialize(message);
        var received = (MarketStateMessage)WireCodec.Deserialize(typeof(MarketStateMessage), payload);
        Check(received.RollGen == 7 && received.GenCardMarketPriceListAscension.Count == 2,
            "Ascension table survives production wire serialization");
        var row = new MarketPrice { generatedMarketPrice = 9f, pricePercentChangeList = 9f };
        var guest = new List<MarketPrice> { row };
        Market("ReadMarketInto", received.GenCardMarketPriceListAscension, guest);
        Check(ReferenceEquals(row, guest[0]) && guest.Count == 2 && row.generatedMarketPrice == 123.5f
            && row.pricePercentChangeList == -12.25f && guest[1].generatedMarketPrice == 0f,
            "Snapshot repairs stale and missing rows while preserving live row references");
        Check(checksum == (int)Market("WireChecksum", received), "Wire round trip preserves market checksum");
        received.GenCardMarketPriceListAscension[0].Percent += 1;
        Check(checksum != (int)Market("WireChecksum", received), "Ascension percentage changes affect market checksum");
        var tournament = new TournamentStateMessage();
        tournament.Bracket.Add(new TournamentBracketEntry { SortedIndex = 3, ModelIndex = -1, WinPoints = 9 });
        var decoded = (TournamentStateMessage)WireCodec.Deserialize(typeof(TournamentStateMessage), WireCodec.Serialize(tournament));
        Check(decoded.Bracket[0].SortedIndex == 3 && decoded.Bracket[0].ModelIndex == -1 && decoded.Bracket[0].WinPoints == 9,
            "Player tournament entry retains its slot, player icon sentinel, and score on the wire");
        var role = typeof(CoopCore).GetProperty("Role").GetSetMethod(true);
        try
        {
            foreach (var current in new[] { CoopRole.None, CoopRole.Host, CoopRole.Client })
            {
                role.Invoke(null, new object[] { current });
                Check(GamePatches.HostDeckPrefix() == (current != CoopRole.Client)
                    && GamePatches.HostBattlePrefix() == (current != CoopRole.Client),
                    "New activity entry guards enforce the expected policy for " + current);
            }
        }
        finally
        {
            role.Invoke(null, new object[] { CoopRole.None });
        }
    }
}
