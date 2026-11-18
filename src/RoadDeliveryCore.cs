using System;
using System.Collections.Generic;

namespace NagoyaRoadDelivery
{
    public static class ServiceSetContract
    {
        public static bool Expanded(string serviceSet)
        {
            if(serviceSet==null || serviceSet=="station-original-10")return false;
            if(serviceSet=="station-phase2-20")return true;
            throw new FormatException("Unknown service set");
        }
        public static void Validate(string serviceSet,string districtId,IList<string> original,IList<string> selected,IList<string> actual)
        {
            bool expanded=Expanded(serviceSet);int count=expanded?20:10;
            if(districtId!=(expanded?"nagoya-station-2050-20":"nagoya-station-2050-10"))throw new FormatException("Wrong district graph");
            HashSet<string> expected=new HashSet<string>(original);
            if(original.Count!=10 || expected.Count!=10)throw new FormatException("Invalid original service catalog");
            if(expanded)
            {
                if(selected==null || selected.Count!=10)throw new FormatException("Invalid selected service catalog");
                foreach(string id in selected)if(!expected.Add(id))throw new FormatException("Overlapping service catalog");
            }
            HashSet<string> given=new HashSet<string>(actual);
            if(actual.Count!=count || given.Count!=count || !given.SetEquals(expected))throw new FormatException("Graph service IDs do not match fixed PLATEAU catalogs");
        }
    }
    public struct Point
    {
        public float X, Z;
        public Point(float x, float z) { X = x; Z = z; }
        public static float Distance(Point a, Point b) { float x = a.X-b.X, z = a.Z-b.Z; return (float)Math.Sqrt(x*x+z*z); }
    }
    public sealed class Edge
    {
        public string Id, From, To, Kind;
        public readonly List<Point> Points = new List<Point>();
        public readonly List<int> NativeSegmentIds = new List<int>();
        public float Length;
    }
    public sealed class Route
    {
        public string Target;
        public readonly List<Point> Points = new List<Point>();
        public float Length;
    }
    public sealed class Graph
    {
        public readonly Dictionary<string, Point> Nodes = new Dictionary<string, Point>();
        public readonly List<Edge> Edges = new List<Edge>();
        private readonly Dictionary<string, List<Edge>> adjacent = new Dictionary<string, List<Edge>>();
        private readonly Dictionary<string, Route> cache = new Dictionary<string, Route>();
        public void Seal()
        {
            if (Nodes.Count < 2 || Nodes.Count > 4096 || Edges.Count > 8192) throw new ArgumentException("Graph size");
            adjacent.Clear(); cache.Clear();
            foreach (string node in Nodes.Keys) adjacent.Add(node, new List<Edge>());
            HashSet<string> ids = new HashSet<string>();
            foreach (Edge e in Edges)
            {
                if (!ids.Add(e.Id) || !Nodes.ContainsKey(e.From) || !Nodes.ContainsKey(e.To) || e.From == e.To) throw new ArgumentException("Invalid graph edge identity");
                if (e.Points.Count < 2 || e.Points.Count > 4096) throw new ArgumentException("Polyline size");
                if (Point.Distance(e.Points[0], Nodes[e.From]) > .5f || Point.Distance(e.Points[e.Points.Count-1], Nodes[e.To]) > .5f) throw new ArgumentException("Polyline does not reach its nodes");
                e.Length = 0;
                for (int i=1; i<e.Points.Count; i++) e.Length += Point.Distance(e.Points[i-1], e.Points[i]);
                if (e.Length <= .01f) throw new ArgumentException("Zero length edge");
                if (e.Kind == "access" && e.Length > 80) throw new ArgumentException("Access spur is too long; use the road graph");
                adjacent[e.From].Add(e); adjacent[e.To].Add(e);
            }
        }
        public Route Find(string start, string target)
        {
            if (!Nodes.ContainsKey(start) || !Nodes.ContainsKey(target)) throw new ArgumentException("Unknown route endpoint");
            string key = start + "\0" + target;
            Route saved;
            if (cache.TryGetValue(key, out saved)) return saved;
            Dictionary<string, float> distance = new Dictionary<string, float>();
            Dictionary<string, Edge> previous = new Dictionary<string, Edge>();
            HashSet<string> visited = new HashSet<string>();
            SortedDictionary<float, Queue<string>> frontier = new SortedDictionary<float, Queue<string>>();
            distance[start] = 0; Push(frontier, 0, start);
            // One settled entry per graph node; duplicate heap entries are bounded by edges.
            for (int iteration=0; frontier.Count>0 && iteration <= Edges.Count*2+Nodes.Count; iteration++)
            {
                KeyValuePair<float, Queue<string>> first;
                using (IEnumerator<KeyValuePair<float, Queue<string>>> it = frontier.GetEnumerator()) { it.MoveNext(); first=it.Current; }
                string current = first.Value.Dequeue(); if (first.Value.Count==0) frontier.Remove(first.Key);
                if (!visited.Add(current)) continue;
                if (current == target) break;
                foreach (Edge e in adjacent[current])
                {
                    string next = e.From == current ? e.To : e.From;
                    float candidate = distance[current] + e.Length, old;
                    if (!distance.TryGetValue(next, out old) || candidate < old)
                    {
                        distance[next] = candidate; previous[next] = e; Push(frontier, candidate, next);
                    }
                }
            }
            if (!visited.Contains(target)) { cache[key] = null; return null; }
            List<Edge> reversed = new List<Edge>(); string cursor = target;
            for (int i=0; cursor != start && i<Nodes.Count; i++)
            {
                Edge e = previous[cursor]; reversed.Add(e); cursor = e.From == cursor ? e.To : e.From;
            }
            if (cursor != start) throw new InvalidOperationException("Route reconstruction failed");
            reversed.Reverse(); Route route = new Route(); route.Target=target; route.Points.Add(Nodes[start]); cursor=start;
            foreach (Edge e in reversed)
            {
                bool forward=e.From==cursor;
                for (int i=0; i<e.Points.Count; i++)
                {
                    Point p=e.Points[forward?i:e.Points.Count-1-i];
                    if (Point.Distance(p,route.Points[route.Points.Count-1]) > .001f) route.Points.Add(p);
                }
                cursor=forward?e.To:e.From;
            }
            for (int i=1;i<route.Points.Count;i++) route.Length+=Point.Distance(route.Points[i-1],route.Points[i]);
            cache[key]=route; return route;
        }
        private static void Push(SortedDictionary<float, Queue<string>> frontier, float score, string node)
        {
            Queue<string> queue; if (!frontier.TryGetValue(score,out queue)) { queue=new Queue<string>();frontier.Add(score,queue); } queue.Enqueue(node);
        }
    }
    public sealed class Building
    {
        public string Id, Name, NodeId;
        public int Stock=1, Pickups, Deliveries;
    }
    public sealed class Charger { public string Id, NodeId; public int Occupant; }
    public sealed class Job
    {
        public string Id, Status="pending";
        public Building From, To;
        public int RobotId;
    }
    public sealed class Robot
    {
        public int Id, Deliveries, Charges;
        public string State="idle", CurrentNode;
        public Point Position;
        public float Battery=25, Wait;
        public double Meters;
        public bool Cargo;
        public Job Job;
        public Route Route;
        public int RouteIndex;
        public Charger TargetCharger;
    }
    public sealed class Event
    {
        public string Kind, State, JobId, FromBuilding, ToBuilding;
        public int RobotId, Round, Deliveries, Charges, Stored, Carried;
        public float Battery, X, Z;
    }
    public sealed class Engine
    {
        public readonly Graph Graph;
        public readonly List<Building> Buildings;
        public readonly List<Charger> Chargers;
        public readonly List<Robot> Robots = new List<Robot>();
        public readonly List<Job> Jobs = new List<Job>();
        public Action<Event> OnEvent;
        public float Speed=1.6f, CostPerMeter=.02f, ChargePerSecond=7, Reserve=10;
        public int Round=1, CompletedRounds, TotalDeliveries;
        public bool Repeat, Finished;
        private readonly int initialInventory;

        public Engine(Graph graph, List<Building> buildings, List<Charger> chargers, List<string> startNodes)
        {
            Graph=graph; Buildings=buildings; Chargers=chargers;
            if ((buildings.Count!=10 && buildings.Count!=20) || startNodes.Count!=3 || chargers.Count<1 || chargers.Count>10) throw new ArgumentException("Ten or twenty buildings, three robots and at least one charger required");
            HashSet<string> ids=new HashSet<string>(), chargeNodes=new HashSet<string>();
            foreach (Building b in buildings)
            {
                if (!ids.Add(b.Id) || b.Stock<1 || b.Stock>1000 || !graph.Nodes.ContainsKey(b.NodeId)) throw new ArgumentException("Building identity or inventory");
                initialInventory+=b.Stock;
            }
            foreach (Charger c in chargers)
                if (!chargeNodes.Add(c.NodeId) || !graph.Nodes.ContainsKey(c.NodeId)) throw new ArgumentException("Charging slot identity");
            foreach (Building b in buildings)
                if (graph.Find(chargers[0].NodeId,b.NodeId)==null) throw new ArgumentException("Building has no road route: "+b.Id);
            for(int i=0;i<startNodes.Count;i++)
            {
                if(graph.Find(startNodes[i],chargers[0].NodeId)==null) throw new ArgumentException("Robot start disconnected");
                Robot robot=new Robot();robot.Id=i+1;robot.CurrentNode=startNodes[i];robot.Position=graph.Nodes[startNodes[i]];Robots.Add(robot);
            }
            MakeJobs();
        }
        private void MakeJobs()
        {
            Jobs.Clear();
            for(int i=0;i<Buildings.Count;i++)
            {
                Job job=new Job();job.Id="round-"+Round+"-job-"+(i+1);job.From=Buildings[i];job.To=Buildings[(i+1)%Buildings.Count];Jobs.Add(job);
            }
        }
        public int Stored { get { int n=0;foreach(Building b in Buildings)n+=b.Stock;return n; } }
        public int Carried { get { int n=0;foreach(Robot r in Robots)if(r.Cargo)n++;return n; } }
        public void Step(float dt)
        {
            if(dt<0 || dt>1 || float.IsNaN(dt)) throw new ArgumentException("Simulation delta");
            if(Finished || dt==0)return;
            foreach(Robot r in Robots)
            {
                StepRobot(r,dt);
                if(Stored+Carried!=initialInventory)throw new InvalidOperationException("Inventory conservation failed");
            }
            bool complete=true;
            foreach(Job j in Jobs)if(j.Status!="delivered")complete=false;
            foreach(Robot r in Robots)if(r.State!="idle" || r.Battery<99.99f)complete=false;
            if(complete)
            {
                CompletedRounds++;Emit("round_completed",null,null);
                if(Repeat){Round++;MakeJobs();}else Finished=true;
            }
        }
        private void StepRobot(Robot r,float dt)
        {
            if(r.State=="idle") { Assign(r);return; }
            if(r.State=="to_pickup" || r.State=="to_delivery" || r.State=="to_charge") { Travel(r,dt);return; }
            if(r.State=="waiting_for_charge")
            {
                if(r.TargetCharger.Occupant==0){r.TargetCharger.Occupant=r.Id;r.State="charging";Emit("charging_started",r,r.Job);}return;
            }
            if(r.State=="charging")
            {
                r.Battery=Math.Min(100,r.Battery+dt*ChargePerSecond);
                if(r.Battery>=100){r.Charges++;r.TargetCharger.Occupant=0;r.TargetCharger=null;r.State="idle";Emit("charging_completed",r,r.Job);}return;
            }
            r.Wait-=dt;if(r.Wait>0)return;
            if(r.State=="loading")
            {
                if(r.Job.From.Stock<=0 || r.Cargo)throw new InvalidOperationException("Invalid pickup");
                r.Job.From.Stock--;r.Job.From.Pickups++;r.Cargo=true;Emit("picked_up",r,r.Job);
                Move(r,"to_delivery",RequireRoute(r.CurrentNode,r.Job.To.NodeId));
            }
            else if(r.State=="unloading")
            {
                if(!r.Cargo || r.Job.Status!="assigned")throw new InvalidOperationException("Invalid delivery");
                r.Job.To.Stock++;r.Job.To.Deliveries++;r.Cargo=false;r.Job.Status="delivered";r.Deliveries++;TotalDeliveries++;
                Emit("delivered",r,r.Job);r.Job=null;r.State="idle";
            }
            else throw new InvalidOperationException("Unknown robot state: "+r.State);
        }
        private void Assign(Robot r)
        {
            foreach(Job job in Jobs)
            {
                if(job.Status!="pending" || job.From.Stock<=0)continue;
                Route pickup=RequireRoute(r.CurrentNode,job.From.NodeId), delivery=RequireRoute(job.From.NodeId,job.To.NodeId);
                Charger after;Route returnTrip=NearestCharger(job.To.NodeId,out after);
                float needed=(pickup.Length+delivery.Length+returnTrip.Length)*CostPerMeter+Reserve;
                if(needed>100.001f)throw new InvalidOperationException("Job exceeds battery range: "+job.Id);
                if(r.Battery+0.001f<needed){Charge(r);return;}
                job.Status="assigned";job.RobotId=r.Id;r.Job=job;Emit("job_assigned",r,job);Move(r,"to_pickup",pickup);return;
            }
            bool allDelivered=true;foreach(Job job in Jobs)if(job.Status!="delivered")allDelivered=false;
            if(allDelivered && r.Battery<99.99f)Charge(r);
        }
        private void Charge(Robot r)
        {
            Charger charger;Route route=NearestCharger(r.CurrentNode,out charger);
            if(r.Battery+0.001f<route.Length*CostPerMeter)throw new InvalidOperationException("Insufficient energy to reach a charger");
            r.TargetCharger=charger;Move(r,"to_charge",route);
        }
        private Route NearestCharger(string from,out Charger selected)
        {
            Route best=null;selected=null;
            foreach(Charger charger in Chargers)
            {
                Route route=Graph.Find(from,charger.NodeId);
                if(route!=null && (best==null || route.Length<best.Length)){best=route;selected=charger;}
            }
            if(best==null)throw new InvalidOperationException("No reachable charger");return best;
        }
        private Route RequireRoute(string from,string to)
        {
            Route route=Graph.Find(from,to);if(route==null)throw new InvalidOperationException("No road route: "+from+" -> "+to);return route;
        }
        private void Move(Robot r,string state,Route route)
        {
            r.Route=route;r.RouteIndex=1;r.State=state;Emit(state,r,r.Job);
        }
        private void Travel(Robot r,float dt)
        {
            float remaining=Speed*dt;
            for(int hops=0;hops<r.Route.Points.Count && r.RouteIndex<r.Route.Points.Count;hops++)
            {
                Point target=r.Route.Points[r.RouteIndex];float distance=Point.Distance(r.Position,target);
                float move=Math.Min(distance,remaining),energy=move*CostPerMeter;
                if(r.Battery+.0001f<energy)throw new InvalidOperationException("Battery exhausted while travelling");
                if(distance>.0001f)r.Position=new Point(r.Position.X+(target.X-r.Position.X)*move/distance,r.Position.Z+(target.Z-r.Position.Z)*move/distance);
                r.Battery=Math.Max(0,r.Battery-energy);r.Meters+=move;remaining-=move;
                if(distance<=move+.0001f){r.Position=target;r.RouteIndex++;}else break;
                if(remaining<=.0001f)break;
            }
            if(r.RouteIndex<r.Route.Points.Count)return;
            r.CurrentNode=r.Route.Target;r.Route=null;
            if(r.State=="to_pickup"){r.State="loading";r.Wait=1.5f;Emit("at_pickup",r,r.Job);}
            else if(r.State=="to_delivery"){r.State="unloading";r.Wait=1.5f;Emit("at_delivery",r,r.Job);}
            else {r.State="waiting_for_charge";Emit("at_charger",r,r.Job);}
        }
        private void Emit(string kind,Robot robot,Job job)
        {
            if(OnEvent==null)return;Event e=new Event();e.Kind=kind;e.Round=Round;e.Stored=Stored;e.Carried=Carried;
            if(robot!=null){e.RobotId=robot.Id;e.State=robot.State;e.Battery=robot.Battery;e.X=robot.Position.X;e.Z=robot.Position.Z;e.Deliveries=robot.Deliveries;e.Charges=robot.Charges;}
            if(job!=null){e.JobId=job.Id;e.FromBuilding=job.From.Id;e.ToBuilding=job.To.Id;}OnEvent(e);
        }
    }
}
