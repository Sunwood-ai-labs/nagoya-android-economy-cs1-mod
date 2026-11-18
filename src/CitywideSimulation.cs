using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CitywideRouting;

namespace NagoyaRoadDelivery
{
    internal interface ICitywideFleetVisuals
    {
        void SetChargingActor(int region, int actorId);
    }

    public sealed class CitywideSimulation
    {
        private const float WalkSpeed = 1.6f;
        private const double BatteryPerMeter = 0.02;
        private const double BatteryReserve = 10.0;
        private const float ChargeRate = 7.0f;
        private const float Quantum = 0.25f;
        private const int MaxSubsteps = 32;
        private const int InitialParcelsPerRegion = 3;
        private const int TargetCount = 3;
        public const int MaxAndroidsPerRegion = TargetCount;
        private readonly int _androidsPerRegion;
        private readonly RoutingGraph _graph;
        private readonly ICitywideVisuals _visuals;
        private readonly RegionRuntime[] _regions;
        private double _elapsed;
        private double _carry;

        private sealed class RegionRuntime
        {
            public readonly RegionPlan Plan;
            public Agent[] Androids;
            public Agent Human;
            public Agent ChargerOwner;
            public string Error;
            public int HubStock = InitialParcelsPerRegion;
            public readonly int[] TargetStock = new int[TargetCount];
            public readonly int[] TargetDeliveries = new int[TargetCount];
            public readonly int[] TargetCollections = new int[TargetCount];
            public int DeliveryCount;
            public int CollectionCount;
            public readonly bool[] BusyTargets = new bool[TargetCount];
            public int ReservedHubStock;
            public int DispatchCursor;
            public int HumanCursor;
            public bool CollectionPhase;
            public int CompletedPhaseJobs;
            public RegionRuntime(RegionPlan plan) { Plan = plan; }
        }

        private sealed class Agent
        {
            public readonly string Kind;
            public readonly int Id;
            public readonly int Region;
            public readonly object Visual;
            public readonly bool CargoCapable;
            public Point2 Point;
            public double Battery;
            public int Deliveries;
            public int Collections;
            public int Charges;
            public double Meters;
            public int Carried;
            public string State = "hub";
            public string Error;
            public string CurrentJob;
            public int Leg;
            public MovementPath[] Outbound;
            public MovementPath[] Return;
            public MovementPath Current;
            public int TargetIndex;
            public bool Returning;
            public double DistanceAlong;
            public bool AtHub = true;
            public bool ChargeSignalled;
            public Agent(string kind, int id, int region, bool cargo, object visual)
            {
                Kind = kind; Id = id; Region = region; CargoCapable = cargo; Visual = visual;
                Battery = cargo ? 25.0 : 100.0;
            }
        }

        private sealed class MovementPath
        {
            public readonly Point2[] Points;
            public readonly double[] Cum;
            public readonly double Length;
            public readonly int[] SurfaceEdges;
            public MovementPath(Point2[] points, int[] surfaceEdges)
            {
                if (points == null || points.Length < 2) throw new InvalidDataException("route has fewer than two waypoints");
                if(surfaceEdges==null||surfaceEdges.Length!=points.Length)throw new InvalidDataException("route surface labels");
                SurfaceEdges=surfaceEdges;Points = points; Cum = new double[points.Length];
                double total = 0.0;
                for (int i = 1; i < points.Length; i++)
                {
                    double dx = points[i].X - points[i - 1].X, dz = points[i].Z - points[i - 1].Z;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (double.IsNaN(d) || double.IsInfinity(d)) throw new InvalidDataException("invalid route waypoint");
                    total += d; Cum[i] = total;
                }
                if (total <= 0.0) throw new InvalidDataException("zero-length route");
                Length = total;
            }
            public Point2 Sample(double distance, out float heading)
            {
                if (distance <= 0.0) { heading = Heading(Points[0], Points[1]); return Points[0]; }
                if (distance >= Length) { heading = Heading(Points[Points.Length - 2], Points[Points.Length - 1]); return Points[Points.Length - 1]; }
                int i = 1; while (i < Cum.Length && Cum[i] < distance) i++;
                double prior = Cum[i - 1], seg = Cum[i] - prior, t = seg <= 0.0 ? 0.0 : (distance - prior) / seg;
                Point2 a = Points[i - 1], b = Points[i]; heading = Heading(a, b);
                return new Point2((float)(a.X + (b.X - a.X) * t), (float)(a.Z + (b.Z - a.Z) * t));
            }
            public int SurfaceEdge(double distance)
            {
                int i=1;while(i<Cum.Length-1&&Cum[i]<distance)i++;
                return SurfaceEdges[i];
            }
            private static float Heading(Point2 a, Point2 b) { return (float)(Math.Atan2(b.Z - a.Z, b.X - a.X) * 180.0 / Math.PI); }
        }

        public CitywideSimulation(RoutingGraph graph, RegionPlan[] regions, ICitywideVisuals visuals)
            : this(graph, regions, visuals, 1)
        {
        }

        public CitywideSimulation(RoutingGraph graph, RegionPlan[] regions, ICitywideVisuals visuals, int androidsPerRegion)
        {
            if (graph == null) throw new ArgumentNullException("graph");
            if (androidsPerRegion < 1 || androidsPerRegion > MaxAndroidsPerRegion) throw new ArgumentOutOfRangeException("androidsPerRegion");
            _androidsPerRegion = androidsPerRegion;
            _graph = graph; _visuals = visuals; _regions = new RegionRuntime[regions == null ? 0 : regions.Length];
            HashSet<int> ids = new HashSet<int>();
            for (int i = 0; i < _regions.Length; i++)
            {
                RegionPlan plan = regions[i] ?? new RegionPlan(i, -1, new int[0], "null-region");
                RegionRuntime r = new RegionRuntime(plan); _regions[i] = r;
                if (!ids.Add(plan.Id)) r.Error = JoinError(r.Error, "duplicate region id");
                BuildRegion(r);
            }
        }

        public static int RegionForActor(int id)
        {
            return id < 1000 ? (id - 1) / 2 : (id - 1000) / (MaxAndroidsPerRegion - 1);
        }

        private void BuildRegion(RegionRuntime r)
        {
            RegionPlan p = r.Plan;
            if (p.Targets == null || p.Targets.Length != TargetCount) r.Error = JoinError(r.Error, "requires exactly 3 targets");
            if (p.Hub < 0 || p.Hub >= _graph.NodeCount) r.Error = JoinError(r.Error, "hub out of range");
            object hv = CreateVisual(r, "human", p.Id * 2 + 2);
            r.Human = new Agent("human", p.Id * 2 + 2, p.Id, false, hv);
            r.Androids = new Agent[_androidsPerRegion];
            for (int slot = 0; slot < r.Androids.Length; slot++)
            {
                int id = slot == 0 ? p.Id * 2 + 1 : 1000 + p.Id * (MaxAndroidsPerRegion - 1) + slot - 1;
                r.Androids[slot] = new Agent("android", id, p.Id, true, CreateVisual(r, "android", id));
            }
            if (p.Hub >= 0 && p.Hub < _graph.NodeCount)
            {
                r.Human.Point = _graph.GetNodePosition(p.Hub);
                foreach (Agent android in r.Androids) android.Point = r.Human.Point;
            }
            if (r.Error == null)
            {
                try
                {
                    r.Human.Outbound = new MovementPath[TargetCount]; r.Human.Return = new MovementPath[TargetCount];
                    for (int i = 0; i < TargetCount; i++)
                    {
                        int target = p.Targets[i];
                        if (target < 0 || target >= _graph.NodeCount) throw new InvalidDataException("target out of range: " + i);
                        r.Human.Outbound[i] = BuildPath(p.Hub, target, "outbound target " + i);
                        r.Human.Return[i] = BuildPath(target, p.Hub, "return target " + i);
                        if (r.Human.Outbound[i].Length + r.Human.Return[i].Length > (100.0 - BatteryReserve) / BatteryPerMeter)
                            throw new InvalidDataException("target " + i + " round trip exceeds full-battery reserve");
                    }
                    foreach (Agent android in r.Androids)
                    {
                        android.Outbound = r.Human.Outbound;
                        android.Return = r.Human.Return;
                        Place(android, android.Point, 0.0f);
                    }
                    Place(r.Human, r.Human.Point, 0.0f);
                }
                catch (Exception ex) { r.Error = JoinError(r.Error, "route error: " + ex.Message); }
            }
            if (r.Error != null)
            {
                foreach (Agent android in r.Androids) { android.State = "error"; android.Error = r.Error; }
                r.Human.State = "error"; r.Human.Error = r.Error;
            }
        }

        private object CreateVisual(RegionRuntime r, string kind, int id)
        {
            if (_visuals == null) { r.Error = JoinError(r.Error, kind + " visual null; actor hidden"); return null; }
            try { object actor = _visuals.CreateActor(kind, id); if (actor == null) r.Error = JoinError(r.Error, kind + " visual null; actor hidden"); return actor; }
            catch (Exception ex) { r.Error = JoinError(r.Error, kind + " visual error: " + ex.Message); return null; }
        }

        private MovementPath BuildPath(int from, int to, string label)
        {
            RouteResult route = _graph.Route(from, to);
            if (!route.Found) throw new InvalidDataException(label + " unreachable: " + route.FailureReason);
            return new MovementPath(route.Waypoints,route.WaypointEdgeIndices);
        }

        private static string JoinError(string oldText, string next) { return String.IsNullOrEmpty(oldText) ? next : oldText + "; " + next; }

        public void Step(float dt)
        {
            if (float.IsNaN(dt) || float.IsInfinity(dt) || dt <= 0.0f) return;
            _carry += dt; int steps = 0;
            while (_carry >= Quantum && steps < MaxSubsteps) { Advance(Quantum); _carry -= Quantum; steps++; }
            if (_carry > 0.0 && steps < MaxSubsteps) { float part = (float)_carry; _carry = 0.0; Advance(part); }
        }

        private void Advance(float dt)
        {
            _elapsed += dt;
            foreach (RegionRuntime region in _regions)
            {
                foreach (Agent android in region.Androids) AdvanceAgent(region, android, dt);
                AdvanceAgent(region, region.Human, dt);
            }
        }

        private void AdvanceAgent(RegionRuntime r, Agent a, float dt)
        {
            if (a == null || a.State == "error" || a.State == "unsupported") return;
            double left = dt; int transitions = 0;
            while (left > 1.0e-7 && transitions++ < 24)
            {
                if (a.Current == null)
                {
                    if (a.CurrentJob == null && !SelectJob(r, a)) return;
                    if (a.CargoCapable)
                    {
                        double required = BatteryReserve + (a.Outbound[a.TargetIndex].Length + a.Return[a.TargetIndex].Length) * BatteryPerMeter;
                        if (required > 100.0) { a.State = "unsupported"; a.Error = "unsupported: route needs more than full battery"; r.Error = JoinError(r.Error, a.Error); return; }
                        if (a.Battery + 1.0e-8 < required)
                        {
                            if (r.ChargerOwner != null && r.ChargerOwner != a) { a.State = "waiting_for_charger"; return; }
                            a.State = "charging"; SignalCharger(r, a, true); Place(a, _graph.GetNodePosition(r.Plan.Hub), 0.0f);
                            double use = Math.Min(left, (required - a.Battery) / ChargeRate); a.Battery = Math.Min(100.0, a.Battery + use * ChargeRate); left -= use;
                            Place(a, _graph.GetNodePosition(r.Plan.Hub), 0.0f);
                            if (a.Battery + 1.0e-7 < required) return;
                            SignalCharger(r, a, false); a.State = "hub"; continue;
                        }
                    }
                    if (a.CargoCapable && a.CurrentJob == "delivery")
                    {
                        r.HubStock--;
                        r.ReservedHubStock--;
                        a.Carried = 1;
                    }
                    a.Current = a.Outbound[a.TargetIndex]; a.DistanceAlong = 0.0; a.Returning = false; a.AtHub = false; a.Leg = 1; a.State = a.CargoCapable ? (a.CurrentJob == "collection" ? "collection_to_target" : "delivery_to_target") : "human_to_target";
                }
                double move = Math.Min(WalkSpeed * left, a.Current.Length - a.DistanceAlong);
                if (a.CargoCapable)
                {
                    double maxMove = Math.Max(0.0, (a.Battery - BatteryReserve) / BatteryPerMeter);
                    if (move > maxMove + 1.0e-7) { a.State = "unsupported"; a.Error = "unsupported: reserve would be breached"; r.Error = JoinError(r.Error, a.Error); return; }
                    a.Battery -= move * BatteryPerMeter;
                }
                a.DistanceAlong += move; a.Meters += move; left -= move;
                float heading; Place(a, a.Current.Sample(a.DistanceAlong, out heading), heading);
                if (a.DistanceAlong + 1.0e-6 < a.Current.Length) continue;
                if (!a.Returning)
                {
                    if (a.CargoCapable && a.CurrentJob == "delivery") { r.TargetStock[a.TargetIndex]++; r.TargetDeliveries[a.TargetIndex]++; r.DeliveryCount++; a.Deliveries++; a.Carried = 0; }
                    else if (a.CargoCapable && a.CurrentJob == "collection") { if (r.TargetStock[a.TargetIndex] <= 0) { a.State = "error"; a.Error = "target stock underflow"; r.Error = JoinError(r.Error, a.Error); return; } r.TargetStock[a.TargetIndex]--; r.TargetCollections[a.TargetIndex]++; r.CollectionCount++; a.Collections++; a.Carried = 1; }
                    a.Current = a.Return[a.TargetIndex]; a.DistanceAlong = 0.0; a.Returning = true; a.Leg = 2; a.State = a.CargoCapable ? (a.CurrentJob == "collection" ? "collection_return" : "delivery_return") : "human_return";
                }
                else
                {
                    if (a.CargoCapable && a.CurrentJob == "collection") { r.HubStock += a.Carried; a.Carried = 0; }
                    a.Current = null; a.DistanceAlong = 0.0; a.Returning = false; a.AtHub = true; a.Leg = 0; a.State = "hub"; CompleteJob(r, a); Place(a, _graph.GetNodePosition(r.Plan.Hub), 0.0f);
                }
            }
        }

        private bool SelectJob(RegionRuntime r, Agent a)
        {
            if (!a.CargoCapable) { a.TargetIndex = (r.HumanCursor + 1) % TargetCount; a.CurrentJob = "human"; return true; }
            for (int i = 0; i < TargetCount; i++)
            {
                int index = (r.DispatchCursor + i) % TargetCount;
                if (r.BusyTargets[index]) continue;
                if (r.CollectionPhase ? r.TargetStock[index] == 0 : r.TargetStock[index] != 0 || r.ReservedHubStock >= r.HubStock) continue;
                r.BusyTargets[index] = true;
                r.DispatchCursor = (index + 1) % TargetCount;
                a.TargetIndex = index;
                a.CurrentJob = r.CollectionPhase ? "collection" : "delivery";
                if (!r.CollectionPhase) r.ReservedHubStock++;
                return true;
            }
            a.State = "awaiting_work";
            return false;
        }

        private void CompleteJob(RegionRuntime r, Agent a)
        {
            if (a.CargoCapable)
            {
                r.BusyTargets[a.TargetIndex] = false;
                r.CompletedPhaseJobs++;
                if (r.CompletedPhaseJobs == TargetCount)
                {
                    r.CollectionPhase = !r.CollectionPhase;
                    r.CompletedPhaseJobs = 0;
                    r.DispatchCursor = 0;
                }
            }
            else r.HumanCursor = (r.HumanCursor + 1) % TargetCount;
            a.CurrentJob = null;
        }

        private void Place(Agent a, Point2 point, float heading)
        {
            a.Point = point;
            if (a.Visual == null) { a.Error = JoinError(a.Error, "visual null; actor hidden"); return; }
            try { ICitywideSurfaceVisuals surface=_visuals as ICitywideSurfaceVisuals;if(surface!=null)surface.PlaceActorOnSurface(a.Visual,point,heading,a.Carried>0,a.Meters,a.Current==null?-1:a.Current.SurfaceEdge(a.DistanceAlong));else _visuals.PlaceActor(a.Visual, point, heading, a.Carried > 0, a.Meters); }
            catch (Exception ex) { a.Error = JoinError(a.Error, "visual placement error: " + ex.Message); }
        }

        private void SignalCharger(RegionRuntime r, Agent a, bool occupied)
        {
            if (a.ChargeSignalled == occupied) return;
            a.ChargeSignalled = occupied; if (occupied) a.Charges++;
            r.ChargerOwner = occupied ? a : null;
            if (_visuals == null) return;
            try
            {
                ICitywideFleetVisuals fleet = _visuals as ICitywideFleetVisuals;
                if (fleet == null) _visuals.SetCharger(r.Plan.Id, occupied);
                else fleet.SetChargingActor(r.Plan.Id, occupied ? a.Id : 0);
            }
            catch (Exception ex) { a.Error = JoinError(a.Error, "charger visual error: " + ex.Message); }
        }

        public string StateJson()
        {
            StringBuilder b = new StringBuilder(); b.Append("{\"elapsed\":").Append(_elapsed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"pendingDt\":").Append(_carry.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"regionCount\":").Append(_regions.Length).Append(",\"androidsPerRegion\":").Append(_androidsPerRegion).Append(",\"regions\":[");
            int hub = 0, targets = 0, carried = 0, deliveries = 0, collections = 0;
            for (int i = 0; i < _regions.Length; i++)
            {
                RegionRuntime r = _regions[i]; hub += r.HubStock; deliveries += r.DeliveryCount; collections += r.CollectionCount;
                foreach (Agent android in r.Androids) carried += android.Carried;
                int regionTargetStock = 0; for (int t = 0; t < TargetCount; t++) regionTargetStock += r.TargetStock[t]; targets += regionTargetStock;
                if (i > 0) b.Append(','); b.Append("{\"id\":").Append(r.Plan.Id).Append(",\"name\":\"").Append(Escape(r.Plan.Name)).Append("\",\"hub\":").Append(r.Plan.Hub).Append(",\"phase\":\"").Append(r.CollectionPhase ? "collection" : "delivery").Append("\",\"error\":").Append(JsonNullable(r.Error)).Append(",\"hubStock\":").Append(r.HubStock).Append(",\"chargingActor\":").Append(r.ChargerOwner == null ? 0 : r.ChargerOwner.Id).Append(",\"deliveryCount\":").Append(r.DeliveryCount).Append(",\"collectionCount\":").Append(r.CollectionCount).Append(",\"targets\":[");
                for (int t = 0; t < TargetCount; t++) { if (t > 0) b.Append(','); b.Append("{\"node\":").Append(r.Plan.Targets != null && t < r.Plan.Targets.Length ? r.Plan.Targets[t] : -1).Append(",\"inventory\":").Append(r.TargetStock[t]).Append(",\"deliveries\":").Append(r.TargetDeliveries[t]).Append(",\"collections\":").Append(r.TargetCollections[t]).Append('}'); }
                b.Append("],\"agents\":["); foreach (Agent android in r.Androids) { AppendAgent(b, android); b.Append(','); } AppendAgent(b, r.Human); b.Append("]}");
            }
            int initial = _regions.Length * InitialParcelsPerRegion;
            b.Append("],\"parcelTotals\":{\"initial\":").Append(initial).Append(",\"hubStock\":").Append(hub).Append(",\"targetStock\":").Append(targets).Append(",\"carried\":").Append(carried).Append(",\"delivered\":").Append(deliveries).Append(",\"collected\":").Append(collections).Append(",\"conserved\":").Append(hub + targets + carried == initial ? "true" : "false").Append("}}");
            return b.ToString();
        }

        private static void AppendAgent(StringBuilder b, Agent a)
        {
            b.Append("{\"kind\":\"").Append(a.Kind).Append("\",\"id\":").Append(a.Id).Append(",\"region\":").Append(a.Region).Append(",\"state\":\"").Append(Escape(a.State)).Append("\",\"currentJob\":").Append(JsonNullable(a.CurrentJob)).Append(",\"leg\":").Append(a.Leg).Append(",\"targetIndex\":").Append(a.TargetIndex).Append(",\"point\":{\"x\":").Append(a.Point.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"z\":").Append(a.Point.Z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append("},\"battery\":").Append(a.Battery.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"deliveries\":").Append(a.Deliveries).Append(",\"collections\":").Append(a.Collections).Append(",\"charges\":").Append(a.Charges).Append(",\"meters\":").Append(a.Meters.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"carried\":").Append(a.Carried).Append(",\"error\":").Append(JsonNullable(a.Error)).Append('}');
        }

        private static string JsonNullable(string s) { return String.IsNullOrEmpty(s) ? "null" : "\"" + Escape(s) + "\""; }
        private static string Escape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n"); }
    }
}
