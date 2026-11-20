using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CitywideRouting;
using NagoyaRoadDelivery;

internal static class FleetCheck
{
    private sealed class Options
    {
        public int ExpectedRegions = -1;
        public int ExpectedInitialParcels = -1;
        public int[] FleetSizes = new int[] { 1, 2, 3 };
    }

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

    private static Options ParseOptions(string[] args)
    {
        Options options = new Options();
        for (int i = 2; i < args.Length; i++)
        {
            string option = args[i];
            int separator = option.IndexOf('=');
            if (separator <= 2 || !option.StartsWith("--"))
                throw new ArgumentException("Options must use --name=value: " + option);
            string name = option.Substring(2, separator - 2);
            string value = option.Substring(separator + 1);
            if (name == "expected-regions") options.ExpectedRegions = ParsePositive(value, name);
            else if (name == "expected-initial-parcels") options.ExpectedInitialParcels = ParsePositive(value, name);
            else if (name == "fleet-sizes") options.FleetSizes = ParseFleetSizes(value);
            else throw new ArgumentException("Unknown option: " + option);
        }
        if (options.FleetSizes.Length == 0) throw new ArgumentException("fleet-sizes cannot be empty");
        return options;
    }

    private static int ParsePositive(string value, string name)
    {
        int result;
        if (!Int32.TryParse(value, out result) || result < 1)
            throw new ArgumentException(name + " must be a positive integer");
        return result;
    }

    private static int[] ParseFleetSizes(string value)
    {
        string[] parts = value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        int[] result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) result[i] = ParsePositive(parts[i].Trim(), "fleet-sizes");
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FleetCheck error type=" + error.GetType().FullName);
            Console.Error.WriteLine("FleetCheck error=" + error.Message);
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args == null || args.Length < 2) throw new ArgumentException("Usage: FleetCheck.exe graph.ncg regions.json [--expected-regions=N] [--expected-initial-parcels=N] [--fleet-sizes=1,2,3]");
        Options options = ParseOptions(args);
        RoutingGraph graph = RoutingGraph.Load(args[0]);
        List<RegionPlan> regions = new List<RegionPlan>();
        foreach (object value in List(Map(JsonData.Parse(File.ReadAllText(args[1])))["regions"]))
        {
            Dictionary<string, object> row = Map(value);
            List<int> targets = new List<int>();
            foreach (object target in List(row["targets"])) targets.Add(Count(target));
            regions.Add(new RegionPlan(Count(row["id"]), Count(row["hub"]), targets.ToArray(), (string)row["name"]));
        }
        if (options.ExpectedRegions >= 0)
            Require(regions.Count == options.ExpectedRegions, "Region count did not match the explicit expectation");
        int[] deliveries = new int[options.FleetSizes.Length];
        for (int i = 0; i < options.FleetSizes.Length; i++)
            deliveries[i] = Check(graph, regions.ToArray(), options.FleetSizes[i], options.ExpectedInitialParcels);
        for (int i = 1; i < deliveries.Length; i++)
            Require(deliveries[i] >= deliveries[i - 1], "A larger fleet must not reduce completed deliveries");
    }

    private static int Check(RoutingGraph graph, RegionPlan[] regions, int fleetSize, int expectedInitialParcels)
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
            int initial = Count(totals["initial"]);
            Require((bool)totals["conserved"], "Parcel conservation at checkpoint");
            if (expectedInitialParcels >= 0)
                Require(initial == expectedInitialParcels, "Initial parcel count did not match the explicit expectation");
            delivered = Count(totals["delivered"]);
            collected = Count(totals["collected"]);
            HashSet<int> ids = new HashSet<int>();
            int computedParcels = 0;
            foreach (object value in List(state["regions"]))
            {
                Dictionary<string, object> region = Map(value);
                Require(region["error"] == null, "Region error at checkpoint: " + (region["error"] == null ? "unknown" : (string)region["error"]));
                Require(Count(region["hubStock"]) >= 0, "Negative hub inventory");
                List<object> actors = List(region["agents"]);
                Require(actors.Count == fleetSize + 1, "Incorrect regional fleet size");
                int regionParcels = Count(region["hubStock"]);
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
                    regionParcels += inventory;
                    if (step == 1799) Require(Count(record["deliveries"]) > 0 && Count(record["collections"]) > 0, "Target was not served");
                }
                foreach (object actorValue in actors)
                {
                    Dictionary<string, object> actor = Map(actorValue);
                    regionParcels += Count(actor["carried"]);
                }
                computedParcels += regionParcels;
            }
            Require(computedParcels == initial, "Parcel total did not match the region inventories");
        }
        Require(worked.Count == regions.Length * fleetSize, "Each Android must deliver, collect and charge");
        if (fleetSize > 1) Require(waited.Count > 0, "Fleet never exercised the charging queue");
        Console.WriteLine("fleetPerRegion={0} regions={1} workingAndroids={2} deliveries={3} collections={4} observedWaitingAndroids={5} simSeconds=14400 wallMs={6}",
            fleetSize, regions.Length, worked.Count, delivered, collected, waited.Count, timer.ElapsedMilliseconds);
        return delivered;
    }
}
