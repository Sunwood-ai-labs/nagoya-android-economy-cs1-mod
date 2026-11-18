using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CitywideRouting;
using NagoyaRoadDelivery;

internal static class FleetCheck
{
    private sealed class VisualProbe : ICitywideVisuals, ICitywideFleetVisuals
    {
        private readonly Dictionary<int, int> _charging = new Dictionary<int, int>();
        public object CreateActor(string kind, int id) { return id; }
        public void PlaceActor(object actor, Point2 point, float heading, bool cargo, double meters) {}
        public void SetCharger(int region, bool occupied) { throw new InvalidOperationException("Missing charger owner"); }
        public void SetChargingActor(int region, int actorId)
        {
            Require(actorId == 0 ? _charging.ContainsKey(region) : !_charging.ContainsKey(region), "Charger overlap or invalid release");
            if (actorId == 0) _charging.Remove(region);
            else _charging.Add(region, actorId);
        }
    }

    private static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
    private static List<object> List(object value) { return (List<object>)value; }
    private static int Count(object value) { return (int)(double)value; }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Main(string[] args)
    {
        RoutingGraph graph = RoutingGraph.Load(args[0]);
        List<RegionPlan> regions = new List<RegionPlan>();
        foreach (object value in List(Map(JsonData.Parse(File.ReadAllText(args[1])))["regions"]))
        {
            Dictionary<string, object> row = Map(value);
            List<int> targets = new List<int>();
            foreach (object target in List(row["targets"])) targets.Add(Count(target));
            regions.Add(new RegionPlan(Count(row["id"]), Count(row["hub"]), targets.ToArray(), (string)row["name"]));
        }
        Require(regions.Count == 56, "Expected all 56 Nagoya regions");
        int baseline = Check(graph, regions.ToArray(), 1);
        int doubled = Check(graph, regions.ToArray(), 2);
        int scaled = Check(graph, regions.ToArray(), 3);
        Require(doubled > baseline && scaled > doubled, "Fleet changes must improve completed deliveries");
    }

    private static int Check(RoutingGraph graph, RegionPlan[] regions, int fleetSize)
    {
        Stopwatch timer = Stopwatch.StartNew();
        CitywideSimulation simulation = new CitywideSimulation(graph, regions, new VisualProbe(), fleetSize);
        HashSet<int> waited = new HashSet<int>();
        HashSet<int> worked = new HashSet<int>();
        int delivered = 0, collected = 0;
        for (int step = 0; step < 1800; step++)
        {
            simulation.Step(8f);
            if (step % 10 != 0 && step != 1799) continue;
            Require(timer.Elapsed.TotalSeconds < 30, "Offline run exceeded wall clock budget");
            Dictionary<string, object> state = Map(JsonData.Parse(simulation.StateJson()));
            Dictionary<string, object> totals = Map(state["parcelTotals"]);
            Require((bool)totals["conserved"] && Count(totals["initial"]) == 168, "Parcel conservation at checkpoint");
            delivered = Count(totals["delivered"]);
            collected = Count(totals["collected"]);
            HashSet<int> ids = new HashSet<int>();
            foreach (object value in List(state["regions"]))
            {
                Dictionary<string, object> region = Map(value);
                Require(region["error"] == null && Count(region["hubStock"]) >= 0, "Region error or negative hub inventory");
                List<object> actors = List(region["agents"]);
                Require(actors.Count == fleetSize + 1, "Incorrect regional fleet size");
                int charging = 0;
                HashSet<int> jobs = new HashSet<int>();
                foreach (object item in actors)
                {
                    Dictionary<string, object> actor = Map(item);
                    int id = Count(actor["id"]);
                    Require(ids.Add(id) && actor["error"] == null && CitywideSimulation.RegionForActor(id) == Count(region["id"]), "Actor error, region mismatch, or duplicate ID");
                    if ((string)actor["kind"] != "android") continue;
                    if (actor["currentJob"] != null) Require(jobs.Add(Count(actor["targetIndex"])), "Duplicate assignment at one target");
                    if ((string)actor["state"] == "waiting_for_charger") waited.Add(id);
                    if ((string)actor["state"] == "charging")
                    {
                        Require(Count(region["chargingActor"]) == id, "Charging actor does not match owner");
                        charging++;
                    }
                    if (Count(actor["deliveries"]) > 0 && Count(actor["collections"]) > 0 && Count(actor["charges"]) > 0) worked.Add(id);
                }
                Require(charging == (Count(region["chargingActor"]) == 0 ? 0 : 1), "Charger capacity");
                foreach (object target in List(region["targets"]))
                {
                    Dictionary<string, object> record = Map(target);
                    int inventory = Count(record["inventory"]);
                    Require(inventory >= 0 && inventory <= 1, "Target inventory out of range");
                    if (step == 1799) Require(Count(record["deliveries"]) > 0 && Count(record["collections"]) > 0, "Target was not served");
                }
            }
        }
        Require(worked.Count == regions.Length * fleetSize, "Each Android must deliver, collect and charge");
        if (fleetSize > 1) Require(waited.Count > 0, "Fleet never exercised the charging queue");
        Console.WriteLine("fleetPerRegion={0} regions={1} workingAndroids={2} deliveries={3} collections={4} observedWaitingAndroids={5} simSeconds=14400 wallMs={6}",
            fleetSize, regions.Length, worked.Count, delivered, collected, waited.Count, timer.ElapsedMilliseconds);
        return delivered;
    }
}
