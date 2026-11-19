using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ColossalFramework.Math;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    public sealed class NativeWaterConnections : MonoBehaviour
    {
        private readonly string runId = Guid.NewGuid().ToString("N");
        private string result;
        private float nextCheck;
        private bool accepted;

        private struct Link
        {
            public ushort Building, FacilityNode, MainNode, ExistingSegment;
        }

        public void Start()
        {
            Write(new Dictionary<string, object> {{"phase", "ready"}, {"runId", runId}});
        }

        public void Update()
        {
            string ready = Interlocked.Exchange(ref result, null);
            if (ready != null) WriteText(ready);
            if (accepted || Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 2;
            string path = Path.Combine(NativeCityRoadBuilder.Root, "water-connections-command.json");
            if (!File.Exists(path)) return;
            try
            {
                if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Water connections command is too large");
                var request = (Dictionary<string, object>)JsonData.Parse(File.ReadAllText(path));
                if ((string)request["runId"] != runId) return;
                accepted = true;
                SimulationManager.instance.AddAction(delegate { Connect(request); });
            }
            catch (Exception error)
            {
                accepted = true;
                Write(new Dictionary<string, object> {{"phase", "failed"}, {"runId", runId}, {"error", error.Message}});
            }
        }

        private void Connect(Dictionary<string, object> request)
        {
            var records = new List<object>();
            var report = new Dictionary<string, object> {{"runId", runId}, {"links", records}};
            try
            {
                SimulationManager simulation = SimulationManager.instance;
                string city = simulation.m_metaData.m_CityName;
                report["city"] = city;
                if (!simulation.SimulationPaused || GameAreaManager.instance.m_areaCount != 25 ||
                    !city.StartsWith("NagoyaCitywide-", StringComparison.Ordinal) || city != (string)request["city"])
                    throw new InvalidOperationException("Requires the matching paused 25-tile city");
                var ids = (List<object>)request["buildingIds"];
                if (ids.Count == 0 || ids.Count > 12) throw new InvalidDataException("Expected 1 to 12 water facilities");
                NetManager net = NetManager.instance;
                NetInfo pipe = PrefabCollection<NetInfo>.FindLoaded("Water Pipe");
                if (pipe == null || !(pipe.m_netAI is WaterPipeAI)) throw new InvalidOperationException("Water Pipe prefab not loaded");
                var links = new List<Link>();
                var seen = new HashSet<ushort>();
                foreach (object value in ids)
                {
                    double number = (double)value;
                    if (number < 1 || number >= BuildingManager.instance.m_buildings.m_buffer.Length || number != Math.Floor(number) || !seen.Add((ushort)number))
                        throw new InvalidDataException("Invalid or duplicate water facility ID");
                    links.Add(FindLink(net, pipe, (ushort)number));
                }
                if (net.m_segmentCount + links.Count >= net.m_segments.m_buffer.Length - 1)
                    throw new InvalidOperationException("Insufficient native network slots");
                Randomizer random = simulation.m_randomizer;
                try
                {
                    foreach (Link link in links)
                    {
                        ushort segment = link.ExistingSegment;
                        if (segment == 0)
                        {
                            Vector3 direction = net.m_nodes.m_buffer[link.MainNode].m_position - net.m_nodes.m_buffer[link.FacilityNode].m_position;
                            direction.y = 0;
                            direction.Normalize();
                            if (!net.CreateSegment(out segment, ref random, pipe, link.FacilityNode, link.MainNode,
                                direction, -direction, simulation.m_currentBuildIndex, simulation.m_currentBuildIndex, false))
                                throw new InvalidOperationException("Native water connector failed for building " + link.Building);
                            simulation.m_currentBuildIndex += 2;
                        }
                        records.Add(new Dictionary<string, object> {{"buildingId",link.Building}, {"facilityNode",link.FacilityNode},
                            {"networkNode",link.MainNode}, {"segment",segment}, {"created",link.ExistingSegment == 0}});
                    }
                }
                finally { simulation.m_randomizer = random; }
                report["phase"] = "built";
            }
            catch (Exception error)
            {
                report["phase"] = "failed";
                report["error"] = error.Message;
            }
            Interlocked.Exchange(ref result, JsonData.Stringify(report));
        }

        private static Link FindLink(NetManager net, NetInfo pipe, ushort buildingId)
        {
            global::Building building = BuildingManager.instance.m_buildings.m_buffer[buildingId];
            ushort facility = building.m_netNode;
            if ((building.m_flags & global::Building.Flags.Created) == 0 || building.Info == null ||
                !(building.Info.m_buildingAI is WaterFacilityAI) || facility == 0)
                throw new InvalidOperationException("Building lacks a native water facility node: " + buildingId);
            NetNode[] nodes = net.m_nodes.m_buffer;
            NetSegment[] segments = net.m_segments.m_buffer;
            NetNode origin = nodes[facility];
            if ((origin.m_flags & NetNode.Flags.Created) == 0 || origin.Info == null || !(origin.Info.m_netAI is WaterPipeAI))
                throw new InvalidOperationException("Facility water node is inactive: " + buildingId);
            for (int edge = 0; edge < 8; edge++)
            {
                ushort id = origin.GetSegment(edge);
                if (id == 0 || segments[id].Info != pipe) continue;
                ushort neighbor = segments[id].m_startNode == facility ? segments[id].m_endNode : segments[id].m_startNode;
                return new Link { Building = buildingId, FacilityNode = facility, MainNode = neighbor, ExistingSegment = id };
            }
            float bestDistance = 160 * 160;
            ushort best = 0;
            for (int i = 1; i < nodes.Length; i++)
            {
                NetNode candidate = nodes[i];
                if (i == facility || (candidate.m_flags & NetNode.Flags.Created) == 0 || candidate.Info != pipe) continue;
                Vector3 delta = candidate.m_position - origin.m_position;
                if (Mathf.Abs(delta.y) > 16) continue;
                float distance = delta.x * delta.x + delta.z * delta.z;
                if (distance < 16 || distance >= bestDistance) continue;
                int count = candidate.CountSegments();
                if (count == 0 || count >= 7) continue;
                bestDistance = distance;
                best = (ushort)i;
            }
            if (best == 0) throw new InvalidOperationException("No native water main node within 160m of building " + buildingId);
            return new Link { Building = buildingId, FacilityNode = facility, MainNode = best };
        }

        private static void Write(Dictionary<string, object> value) { WriteText(JsonData.Stringify(value)); }

        private static void WriteText(string json)
        {
            string path = Path.Combine(NativeCityRoadBuilder.Root, "water-connections-runtime.json");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
