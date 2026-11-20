using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ColossalFramework.Math;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    // One explicitly targeted import per level load. All NetManager mutations
    // are queued on the CS1 simulation thread.
    public sealed class NativeCityRoadBuilder : MonoBehaviour
    {
        internal static readonly string Root = RuntimePaths.Data(@"citywide-zoning-v3");
        private const int BatchSize = 96;
        private const int MaxRoads = 32000;
        private const int MaxFileBytes = 12 * 1024 * 1024;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly Dictionary<string, string> PrefabNames = new Dictionary<string, string> {
            {"basic", "Basic Road"}, {"arterial", "Medium Road"}, {"highway", "Highway"},
            {"elevated", "Basic Road Elevated"}, {"bridge", "Basic Road Bridge"},
            {"tunnel", "Basic Road Tunnel"}, {"slope", "Basic Road Slope"},
            {"highway_elevated", "Highway Elevated"}, {"highway_bridge", "Highway Bridge"},
            {"highway_tunnel", "Highway Tunnel"}, {"highway_slope", "Highway Slope"},
            {"arterial_elevated", "Medium Road Elevated"}, {"arterial_bridge", "Medium Road Bridge"},
            {"arterial_tunnel", "Medium Road Tunnel"}, {"arterial_slope", "Medium Road Slope"},
            {"water", "Water Pipe"}, {"power", "Power Line"}
        };
        internal bool Infrastructure;
        private string Channel { get { return Infrastructure ? "utility" : "road"; } }
        private string ReceiptSuffix { get { return Infrastructure ? "-utilities.tsv" : "-roads.tsv"; } }

        private sealed class Junction
        {
            public Vector3 Position;
            public ushort NativeId, AnchorNode, AnchorSegment;
            public uint AnchorGeneration;
            public int Degree;
            public NetInfo UtilityPrefab;
        }

        private sealed class Road
        {
            public string Source, Tag, Error;
            public Junction Start, End;
            public NetInfo Prefab;
            public uint BuildIndex;
            public ushort NativeId;
        }

        private readonly List<Road> roads = new List<Road>();
        private readonly Dictionary<string, Junction> junctions = new Dictionary<string, Junction>();
        private string runId, commandId, planDigest, phase, error, cityName;
        private int queued, cursor, created, startNodes, startSegments, expectedNodes, expectedSegments;
        private int endNodes, endSegments;
        private bool attempted, checkedWorld, groundAdjusted, execute;
        private volatile bool stopping;
        private float nextRequest, deadline;
        private DateTime lastRequestWriteUtc;
        private string readyReport;
        private string loadedCity;

        public void Start()
        {
            if (!File.Exists(Path.Combine(Root, "enabled.txt")))
            {
                enabled = false;
                return;
            }
            runId = Guid.NewGuid().ToString("N");
            phase = "ready";
            loadedCity = SimulationManager.instance.m_metaData.m_CityName;
            startNodes = NetManager.instance.m_nodeCount;
            startSegments = NetManager.instance.m_segmentCount;
            Write(Channel + "-runtime.json", Status());
        }

        public void Update()
        {
            string report = Interlocked.Exchange<string>(ref readyReport, null);
            if (report != null)
                Write(Channel + "-runtime.json", report);
            if (Time.realtimeSinceStartup < nextRequest ||
                Interlocked.CompareExchange(ref queued, 1, 0) != 0)
                return;

            nextRequest = Time.realtimeSinceStartup + .12f;
            if (!attempted || phase == "preflight_passed")
            {
                string path = Path.Combine(Root, Channel + "-command.json");
                DateTime write = File.GetLastWriteTimeUtc(path);
                if (write == lastRequestWriteUtc || !File.Exists(path))
                {
                    Interlocked.Exchange(ref queued, 0);
                    return;
                }
                try
                {
                    lastRequestWriteUtc = write;
                    Dictionary<string, object> cmd = ReadCommand(path);
                    if ((string)cmd["runId"] != runId)
                    {
                        Interlocked.Exchange(ref queued, 0);
                        return;
                    }
                    string nextCommand = Token((string)cmd["commandId"]);
                    if (File.Exists(Path.Combine(Root, nextCommand + ReceiptSuffix)))
                        throw new InvalidOperationException("Command ID already has a road receipt");
                    if (attempted)
                    {
                        if (!(bool)cmd["execute"] || nextCommand == commandId ||
                            expectedNodes != Count(cmd, "expectedNodes") ||
                            expectedSegments != Count(cmd, "expectedSegments") ||
                            cityName != (string)cmd["expectedCityName"] ||
                            !String.Equals(planDigest, (string)cmd["sha256"], StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The execution command differs from the verified native road preflight");
                        execute = true;
                        checkedWorld = false;
                    }
                    else
                    {
                        if ((bool)cmd["execute"])
                            throw new InvalidDataException("Read-only native preflight is required before construction");
                        attempted = true;
                        expectedNodes = Count(cmd, "expectedNodes");
                        expectedSegments = Count(cmd, "expectedSegments");
                        cityName = (string)cmd["expectedCityName"];
                        ReadRoads(Token((string)cmd["plan"]), (string)cmd["sha256"]);
                    }
                    commandId = nextCommand;
                    deadline = Time.realtimeSinceStartup + 300f;
                    phase = "preflight";
                }
                catch (Exception failure)
                {
                    attempted = true;
                    phase = "rejected";
                    error = failure.GetBaseException().Message;
                    Write(Channel + "-runtime.json", Status());
                }
            }

            if (phase != "preflight" && phase != "building")
            {
                Interlocked.Exchange(ref queued, 0);
                return;
            }
            bool expired = Time.realtimeSinceStartup > deadline;
            SimulationManager.instance.AddAction(delegate { ApplyBatch(expired); });
        }

        private void ApplyBatch(bool expired)
        {
            try
            {
                if (stopping)
                    return;
                NetManager net = NetManager.instance;
                if (!checkedWorld)
                {
                    CheckWorld(net);
                    checkedWorld = true;
                    if (!execute)
                    {
                        phase = "preflight_passed";
                        return;
                    }
                    phase = "building";
                }
                if (expired)
                    throw new TimeoutException("Road import passed its five-minute runtime budget");
                int stop = Math.Min(cursor + BatchSize, roads.Count);
                for (; cursor < stop; cursor++)
                {
                    Road road = roads[cursor];
                    road.Error = Create(net, road);
                    if (road.NativeId != 0)
                    {
                        created++;
                        if (!Infrastructure)
                            NativeCityLedger.Record(new NativeCityLedger.Road {
                                Source=road.Source, Tag=road.Tag, Id=road.NativeId, BuildIndex=road.BuildIndex,
                                Start=road.Start.NativeId, End=road.End.NativeId
                            });
                    }
                }
                if (cursor == roads.Count)
                {
                    endNodes = net.m_nodeCount;
                    endSegments = net.m_segmentCount;
                    SaveReceipt();
                    phase = created == roads.Count ? "built" : "built_partial";
                }
            }
            catch (Exception failure)
            {
                phase = created > 0 ? "stopped_partial" : "rejected";
                error = failure.GetBaseException().Message;
                if (created > 0)
                {
                    try { SaveReceipt(); }
                    catch (Exception issue) { error += " / receipt: " + issue.GetBaseException().Message; }
                }
            }
            finally
            {
                Interlocked.Exchange<string>(ref readyReport, Status());
                Interlocked.Exchange(ref queued, 0);
            }
        }

        private void CheckWorld(NetManager net)
        {
            startNodes = net.m_nodeCount;
            startSegments = net.m_segmentCount;
            string liveName = SimulationManager.instance.m_metaData.m_CityName;
            if (liveName != cityName || startNodes != expectedNodes || startSegments != expectedSegments)
                throw new InvalidOperationException("Loaded city's name or native network differs from the staged snapshot");
            if (GameAreaManager.instance.m_areaCount != 25)
                throw new InvalidOperationException("The isolated construction save needs all 25 tiles unlocked");
            int anchorCount = 0;
            foreach (Junction junction in junctions.Values)
                if (junction.AnchorNode != 0) anchorCount++;
            if (startNodes + junctions.Count - anchorCount >= net.m_nodes.m_buffer.Length - 1 ||
                startSegments + roads.Count >= net.m_segments.m_buffer.Length - 1)
                throw new InvalidOperationException("The native road or junction budget would be exceeded");
            // Validate every junction before touching the save.
            HashSet<ushort> anchorIds = new HashSet<ushort>();
            foreach (Junction junction in junctions.Values)
            {
                float ground = TerrainManager.instance.SampleRawHeightSmoothWithWater(junction.Position, false, 0f);
                if (Mathf.Abs(ground - 140f) > .5f)
                    throw new InvalidOperationException("Road plan requires the verified flat 140m terrain");
                if (!groundAdjusted) junction.Position.y += ground;
                if (junction.AnchorNode == 0) continue;
                if (!anchorIds.Add(junction.AnchorNode) || junction.AnchorNode >= net.m_nodes.m_buffer.Length ||
                    junction.AnchorSegment >= net.m_segments.m_buffer.Length)
                    throw new InvalidOperationException("Invalid or duplicate existing road junction");
                NetNode actual = net.m_nodes.m_buffer[junction.AnchorNode];
                NetSegment receipt = net.m_segments.m_buffer[junction.AnchorSegment];
                bool matchingNetwork = Infrastructure ? actual.Info == junction.UtilityPrefab && receipt.Info == junction.UtilityPrefab :
                    actual.Info != null && actual.Info.m_class.m_service == ItemClass.Service.Road;
                if ((actual.m_flags & NetNode.Flags.Created) == 0 || (receipt.m_flags & NetSegment.Flags.Created) == 0 ||
                    receipt.m_buildIndex != junction.AnchorGeneration || (receipt.m_startNode != junction.AnchorNode && receipt.m_endNode != junction.AnchorNode) ||
                    !matchingNetwork ||
                    (actual.m_position - junction.Position).sqrMagnitude > .01f || actual.CountSegments() + junction.Degree > 8)
                    throw new InvalidOperationException("Existing road junction no longer matches its native construction receipt: " + junction.AnchorNode);
                junction.NativeId = junction.AnchorNode;
            }
            groundAdjusted = true;
        }

        private static string Create(NetManager net, Road road)
        {
            SimulationManager sim = SimulationManager.instance;
            Randomizer random = sim.m_randomizer;
            bool startNew = road.Start.NativeId == 0;
            bool endNew = road.End.NativeId == 0;
            try
            {
                if (!ResolveNode(net, sim, ref random, road.Start, road.Prefab) ||
                    !ResolveNode(net, sim, ref random, road.End, road.Prefab))
                    return "Native junction allocation refused";
                Vector3 dir = road.End.Position - road.Start.Position;
                dir.y = 0f;
                dir.Normalize();
                if (!net.CreateSegment(out road.NativeId, ref random, road.Prefab, road.Start.NativeId,
                    road.End.NativeId, dir, -dir, sim.m_currentBuildIndex, sim.m_currentBuildIndex, false))
                {
                    road.NativeId = 0;
                    return "Native road segment allocation refused";
                }
                sim.m_currentBuildIndex += 2;
                road.BuildIndex = net.m_segments.m_buffer[road.NativeId].m_buildIndex;
                return null;
            }
            finally
            {
                sim.m_randomizer = random;
                if (road.NativeId == 0)
                {
                    if (startNew) DropUnusedNode(net, road.Start);
                    if (endNew) DropUnusedNode(net, road.End);
                }
            }
        }

        private static bool ResolveNode(NetManager net, SimulationManager sim, ref Randomizer random,
            Junction junction, NetInfo info)
        {
            if (junction.NativeId != 0)
                return true;
            ushort id;
            if (!net.CreateNode(out id, ref random, info, junction.Position, sim.m_currentBuildIndex))
                return false;
            junction.NativeId = id;
            sim.m_currentBuildIndex++;
            return true;
        }

        private static void DropUnusedNode(NetManager net, Junction junction)
        {
            if (junction.NativeId != 0 && net.m_nodes.m_buffer[junction.NativeId].CountSegments() == 0)
            {
                net.ReleaseNode(junction.NativeId);
                junction.NativeId = 0;
            }
        }

        private void ReadRoads(string file, string expectedHash)
        {
            string path = Path.Combine(Root, file);
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes)
                throw new InvalidDataException("Invalid compiled road plan size");
            byte[] data = File.ReadAllBytes(path);
            using (SHA256 sha = SHA256.Create())
                planDigest = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
            if (!String.Equals(planDigest, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Compiled road plan SHA-256 mismatch");
            Dictionary<string, NetInfo> prefabs = new Dictionary<string, NetInfo>();
            HashSet<string> pairs = new HashSet<string>();
            using (StringReader lines = new StringReader(new UTF8Encoding(false, true).GetString(data)))
            {
                for (int number = 1; number <= MaxRoads * 2 + 1; number++)
                {
                    string line = lines.ReadLine();
                    if (line == null)
                        break;
                    string[] row = line.Split('\t');
                    try
                    {
                        bool anchor = row[0] == "A" && row.Length == 8;
                        if (roads.Count == 0 && ((row[0] == "N" && row.Length == 5) || anchor))
                        {
                            Junction junction = new Junction {
                                Position = new Vector3(Measure(row[2], 4790f), Measure(row[4], 32f), Measure(row[3], 4790f))
                            };
                            if (anchor)
                            {
                                junction.AnchorNode = UInt16.Parse(row[5], Invariant);
                                junction.AnchorSegment = UInt16.Parse(row[6], Invariant);
                                junction.AnchorGeneration = UInt32.Parse(row[7], Invariant);
                                if (junction.AnchorNode == 0 || junction.AnchorSegment == 0 || junction.AnchorGeneration == 0)
                                    throw new InvalidDataException("Existing junction requires a complete native receipt");
                            }
                            junctions.Add(Token(row[1]), junction);
                            continue;
                        }
                        if (row[0] != "E" || row.Length != 7 || (row[6] != "0" && row[6] != "1") ||
                            roads.Count >= MaxRoads)
                            throw new InvalidDataException("Invalid road row");
                        if (Infrastructure != (row[4] == "water" || row[4] == "power"))
                            throw new InvalidDataException("Prefab does not belong to this construction channel");
                        string a = Token(row[2]), b = Token(row[3]);
                        string pair = String.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
                        Junction from = junctions[a], to = junctions[b];
                        Vector3 delta = from.Position - to.Position;
                        float heightDifference = Mathf.Abs(delta.y);
                        delta.y = 0f;
                        if (!pairs.Add(pair) || delta.sqrMagnitude < 16f)
                            throw new InvalidDataException("Duplicate or shorter-than-4m road segment");
                        if (heightDifference > delta.magnitude * .16f)
                            throw new InvalidDataException("Native road gradient exceeds 16 percent");
                        if (++from.Degree > 8 || ++to.Degree > 8)
                            throw new InvalidDataException("A junction has more than eight roads");
                        NetInfo prefab;
                        if (!prefabs.TryGetValue(row[4], out prefab))
                        {
                            prefab = PrefabCollection<NetInfo>.FindLoaded(PrefabNames[row[4]]);
                            if (prefab == null)
                                throw new InvalidDataException("Required CS1 road prefab is unavailable: " + row[4]);
                            prefabs.Add(row[4], prefab);
                        }
                        if (Infrastructure)
                        {
                            if ((from.UtilityPrefab != null && from.UtilityPrefab != prefab) ||
                                (to.UtilityPrefab != null && to.UtilityPrefab != prefab))
                                throw new InvalidDataException("Water pipes and power lines cannot share a junction");
                            from.UtilityPrefab = prefab;
                            to.UtilityPrefab = prefab;
                        }
                        roads.Add(new Road { Source = Token(row[1]), Tag = row[4], Prefab = prefab,
                            Start = row[6] == "1" ? from : to, End = row[6] == "1" ? to : from });
                    }
                    catch (Exception issue) { throw new InvalidDataException("Road row " + number + ": " + issue.Message); }
                }
                if (lines.Peek() != -1)
                    throw new InvalidDataException("Compiled road plan exceeds line budget");
            }
            if (roads.Count == 0)
                throw new InvalidDataException("Empty road plan");
            CheckAnchoredComponents();
        }

        private void CheckAnchoredComponents()
        {
            var adjacent = new Dictionary<Junction, List<Junction>>();
            var passed = new HashSet<Junction>();
            var queue = new Queue<Junction>();
            foreach (Junction junction in junctions.Values)
            {
                adjacent.Add(junction, new List<Junction>());
                if (junction.AnchorNode != 0) { passed.Add(junction); queue.Enqueue(junction); }
            }
            if (queue.Count == 0) return;
            foreach (Road road in roads)
            {
                adjacent[road.Start].Add(road.End);
                adjacent[road.End].Add(road.Start);
            }
            while (queue.Count > 0)
                foreach (Junction next in adjacent[queue.Dequeue()])
                    if (passed.Add(next)) queue.Enqueue(next);
            if (passed.Count != junctions.Count)
                throw new InvalidDataException("Road extension contains a component without an existing native junction");
        }

        private static Dictionary<string, object> ReadCommand(string path)
        {
            if (new FileInfo(path).Length > 4096)
                throw new InvalidDataException("Road command exceeds limit");
            return (Dictionary<string, object>)JsonData.Parse(File.ReadAllText(path));
        }

        private static int Count(Dictionary<string, object> command, string key)
        {
            double value = (double)command[key];
            if (value < 0 || value > 40000 || Math.Floor(value) != value)
                throw new InvalidDataException("Invalid native network baseline: " + key);
            return (int)value;
        }

        private static float Measure(string raw, float limit)
        {
            float value = Single.Parse(raw, NumberStyles.Float, Invariant);
            if (Single.IsNaN(value) || Single.IsInfinity(value) || Mathf.Abs(value) > limit)
                throw new InvalidDataException("Road coordinate outside stock build area");
            return value;
        }

        private static string Token(string value)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[a-zA-Z0-9_:\-.]{1,100}\z"))
                throw new InvalidDataException("Invalid road plan token");
            return value;
        }

        private void SaveReceipt()
        {
            StringBuilder lines = new StringBuilder("source\tordinal\tnativeId\tbuildIndex\tstartNode\tendNode\tprefabTag\terror\n");
            for (int i = 0; i < cursor; i++)
            {
                Road road = roads[i];
                lines.Append(road.Source).Append('\t').Append(i).Append('\t').Append(road.NativeId).Append('\t')
                    .Append(road.BuildIndex).Append('\t').Append(road.Start.NativeId).Append('\t').Append(road.End.NativeId)
                    .Append('\t').Append(road.Tag).Append('\t').Append(road.Error).Append('\n');
            }
            Write(commandId + ReceiptSuffix, lines.ToString());
        }

        private string Status()
        {
            return JsonData.Stringify(new Dictionary<string, object> {
                {"runId", runId}, {"commandId", commandId}, {"phase", phase}, {"planSha256", planDigest},
                {"requestedRoads", roads.Count}, {"requestedJunctions", junctions.Count}, {"processedRoads", cursor},
                {"createdRoads", created}, {"baselineNodes", startNodes}, {"baselineSegments", startSegments},
                {"nativeNodes", endNodes}, {"nativeSegments", endSegments}, {"error", error},
                {"channel", Channel},
                {"loadedCity", loadedCity}, {"mvid", typeof(NativeCityRoadBuilder).Assembly.ManifestModule.ModuleVersionId.ToString()},
                {"utc", DateTime.UtcNow.ToString("o", Invariant)}
            });
        }

        private static void Write(string file, string content)
        {
            string path = Path.Combine(Root, file), temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public void OnDestroy() { stopping = true; }
    }
}
