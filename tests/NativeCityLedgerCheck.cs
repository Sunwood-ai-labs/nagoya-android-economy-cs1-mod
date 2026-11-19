using System;
using System.IO;
using NagoyaRoadDelivery;

internal static class NativeCityLedgerCheck
{
    private const string City = "NagoyaCitywide-Construction";

    private static void Main()
    {
        NativeCityLedger.Enter(City);
        for (ushort id = 1; id <= 13677; id++)
            NativeCityLedger.Record(new NativeCityLedger.Road {
                Source="osm:"+id, Tag="basic", Id=id, Start=id, End=(ushort)(id+1), BuildIndex=(uint)(id+100)
            });
        NativeCityLedger.Record(new NativeCityLedger.Lot {
            Group="site_1", Ward="中村区", Id=24, Road=5, ZoneBlock=8, BuildIndex=316,
            Zone=ItemClass.Zone.ResidentialHigh, Members=151, Row=2, Width=2, Length=3
        });
        NativeCityLedger.Record(new NativeCityLedger.Android {Id=71, Home=24, HomeUnit=19, Battery=37.5f});
        byte[] data = NativeCityLedger.Save();
        NativeCityLedger.Leave();
        NativeCityLedger.Load(data);
        NativeCityLedger.Enter("NagoyaCitywide-Other");
        NativeCityLedger.Road[] roads;
        NativeCityLedger.Lot[] lots;
        NativeCityLedger.Snapshot(out roads, out lots);
        Require(roads.Length == 0 && lots.Length == 0 && NativeCityLedger.SavedAndroids().Length == 0,
            "The saved city's associations leaked into another city");

        NativeCityLedger.Load(data);
        NativeCityLedger.Enter(City);
        NativeCityLedger.Snapshot(out roads, out lots);
        NativeCityLedger.Android[] residents = NativeCityLedger.SavedAndroids();
        Require(roads.Length == 13677 && lots.Length == 1 && residents.Length == 1,
            "Round-trip record counts changed");
        Require(lots[0].Ward == "中村区" && lots[0].Id == 24 &&
            lots[0].Zone == ItemClass.Zone.ResidentialHigh && lots[0].Length == 3 && lots[0].Members == 151,
            "Round-trip parcel identity changed");
        Require(residents[0].Id == 71 && residents[0].Home == 24 && residents[0].Battery == 37.5f,
            "Round-trip Android identity or charge changed");

        NativeCityLedger.RemoveAndroid(71);
        Require(NativeCityLedger.SavedAndroids().Length == 0, "Android release was not persisted in the ledger");
        Array.Resize<byte>(ref data, data.Length - 1);
        try
        {
            NativeCityLedger.Load(data);
            throw new InvalidOperationException("A truncated payload was accepted");
        }
        catch (EndOfStreamException) { }
        Console.WriteLine("13,677 native road associations, one lot and one Android round-tripped and remained isolated by city.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
