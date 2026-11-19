using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using HarmonyLib;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    internal static class NativePedestrianLink
    {
        internal const int MaxCandidates = 24;
        private const string LimitPatch = "sunwood.nagoya.pedestrian-link-reserve";
        private const int RequiredNodeHeadroom = 48;
        private const int RequiredSegmentHeadroom = 16;
        private const int RequiredLaneHeadroom = 128;
        [ThreadStatic] private static bool allowReservedCapacity;

        internal struct Candidate
        {
            internal ushort From, To;
            internal Vector2? Bend;
            internal float FromFraction, ToFraction;
            internal bool OnSegments { get { return FromFraction > 0f; } }
        }

        internal static object CreateFirstValid(Candidate[] candidates, bool roadWithSidewalks)
        {
            NetManager net = NetManager.instance;
            bool useReserve = !net.CheckLimits() && HasHeadroom(net);
            Harmony harmony = useReserve ? new Harmony(LimitPatch) : null;
            try
            {
                if (harmony != null)
                {
                    MethodInfo postfix = typeof(NativePedestrianLink).GetMethod("AllowOneConstruction", BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(typeof(NetManager).GetMethod("CheckLimits", Type.EmptyTypes), null, new HarmonyMethod(postfix), null, null);
                    allowReservedCapacity = true;
                }
                return new Dictionary<string, object> {
                    {"usedReservedCapacity",useReserve}, {"nodeCountBefore",net.m_nodeCount},
                    {"nodeBufferCapacity",net.m_nodes.m_buffer.Length}, {"result",BuildFirstValid(net,candidates,roadWithSidewalks)}
                };
            }
            finally
            {
                allowReservedCapacity = false;
                if (harmony != null) harmony.UnpatchAll(LimitPatch);
            }
        }

        private static void AllowOneConstruction(NetManager __instance, ref bool __result)
        {
            if (!__result && allowReservedCapacity && HasHeadroom(__instance)) __result = true;
        }

        private static bool HasHeadroom(NetManager net)
        {
            return net.m_nodes.m_buffer.Length - net.m_nodeCount >= RequiredNodeHeadroom &&
                net.m_segments.m_buffer.Length - net.m_segmentCount >= RequiredSegmentHeadroom &&
                net.m_lanes.m_buffer.Length - net.m_laneCount >= RequiredLaneHeadroom;
        }

        private static object BuildFirstValid(NetManager net, Candidate[] candidates, bool roadWithSidewalks)
        {
            string prefab = roadWithSidewalks ? "Basic Road" : "Pedestrian Pavement";
            NetInfo link = PrefabCollection<NetInfo>.FindLoaded(prefab);
            if (link == null) throw new InvalidOperationException("The connection prefab is unavailable: " + prefab);
            var attempts = new List<object>(candidates.Length);
            var buffer = new FastList<NetTool.NodePosition>();
            Dictionary<ushort, NativeCityLedger.Road> roadSources = null;
            HashSet<ushort> protectedRoads = null;
            foreach (Candidate candidate in candidates)
            {
                ushort from = candidate.From, to = candidate.To;
                var entry = new Dictionary<string, object>();
                attempts.Add(entry);
                NetTool.ControlPoint start, end;
                if (candidate.OnSegments)
                {
                    entry["fromSegment"] = from;
                    entry["toSegment"] = to;
                    entry["fractions"] = new float[] {candidate.FromFraction, candidate.ToFraction};
                    if (protectedRoads == null) protectedRoads = ProtectedRoads(out roadSources);
                    if (protectedRoads.Contains(from) || protectedRoads.Contains(to))
                    {
                        entry["preflight"] = "protectedBuildingFrontage";
                        continue;
                    }
                }
                else
                {
                    entry["fromNode"] = from;
                    entry["toNode"] = to;
                }
                string startError = ResolveAnchor(net, from, candidate.FromFraction, out start);
                string endError = ResolveAnchor(net, to, candidate.ToFraction, out end);
                if (startError != null || endError != null)
                {
                    entry["preflight"] = "invalidGroundRoadAnchors";
                    entry["fromAnchorError"] = startError;
                    entry["toAnchorError"] = endError;
                    continue;
                }
                Vector3 delta = end.m_position - start.m_position;
                float maxSpan = candidate.OnSegments ? 300f : 220f;
                if (delta.sqrMagnitude < 144f || delta.sqrMagnitude > maxSpan * maxSpan || Mathf.Abs(delta.y) > .5f)
                {
                    entry["preflight"] = "invalidGroundRoadAnchors";
                    entry["anchorSpanMeters"] = delta.magnitude;
                    entry["anchorVerticalDifferenceMeters"] = Mathf.Abs(delta.y);
                    continue;
                }
                Vector3 middlePosition = (start.m_position + end.m_position) * .5f;
                if (candidate.Bend.HasValue)
                {
                    Vector2 bend = candidate.Bend.Value;
                    middlePosition.x = bend.x;
                    middlePosition.z = bend.y;
                    entry["bend"] = new float[] { bend.x, bend.y };
                }
                Vector3 startLeg = middlePosition - start.m_position;
                Vector3 endLeg = end.m_position - middlePosition;
                if (startLeg.sqrMagnitude < 36f || endLeg.sqrMagnitude < 36f ||
                    Vector3.Dot(startLeg, endLeg) <= 0f || startLeg.magnitude + endLeg.magnitude > 325f)
                {
                    entry["preflight"] = "invalidBend";
                    continue;
                }
                start.m_direction = startLeg.normalized;
                end.m_direction = endLeg.normalized;
                NetTool.ControlPoint middle = Point(0, middlePosition, delta.normalized);
                ushort firstNode, lastNode, segment;
                int cost, production;
                entry["lengthMeters"] = delta.magnitude;
                ToolBase.ToolErrors errors = ProbeLink(link, start, middle, end, buffer, entry);
                if (errors != ToolBase.ToolErrors.None) continue;
                if (candidate.OnSegments && ((List<int>)entry["overlappingBuildings"]).Count != 0)
                {
                    entry["preflight"] = "buildingOverlapInAllowedPlacement";
                    continue;
                }

                int before = net.m_segmentCount;
                NetSegment formerFirst = candidate.OnSegments ? net.m_segments.m_buffer[from] : default(NetSegment);
                NetSegment formerLast = candidate.OnSegments ? net.m_segments.m_buffer[to] : default(NetSegment);
                buffer.Clear();
                errors = NetTool.CreateNode(link, start, middle, end, buffer, 8,
                    false, true, false, false, true, false, false, 0,
                    out firstNode, out lastNode, out segment, out cost, out production);
                entry["built"] = errors.ToString();
                entry["actualFirstNode"] = firstNode;
                entry["actualLastNode"] = lastNode;
                entry["segmentId"] = segment;
                entry["segmentsAdded"] = net.m_segmentCount - before;
                bool connected = errors == ToolBase.ToolErrors.None && net.m_segmentCount > before;
                if (candidate.OnSegments && connected)
                {
                    ushort[] fromParts = SplitParts(net, formerFirst, firstNode);
                    ushort[] toParts = SplitParts(net, formerLast, lastNode);
                    entry["fromParts"] = fromParts;
                    entry["toParts"] = toParts;
                    connected = fromParts != null && toParts != null;
                    if (connected)
                    {
                        RecordSplit(net, roadSources, from, fromParts);
                        RecordSplit(net, roadSources, to, toParts);
                    }
                }
                else if (!candidate.OnSegments)
                    connected = connected && firstNode == from && lastNode == to;
                return new Dictionary<string, object> {
                    {"connected",connected},
                    {"prefab",prefab}, {"attempts",attempts}
                };
            }
            return new Dictionary<string, object> {{"connected",false},{"prefab",prefab},{"attempts",attempts}};
        }

        private static string ResolveAnchor(NetManager net, ushort id, float fraction, out NetTool.ControlPoint point)
        {
            point = default(NetTool.ControlPoint);
            if (fraction == 0f)
            {
                if (id >= net.m_nodes.m_buffer.Length || !RoadAnchor(net.m_nodes.m_buffer[id]))
                    return "invalidRoadNode";
                point.m_node = id;
                point.m_position = net.m_nodes.m_buffer[id].m_position;
                return null;
            }
            if (id >= net.m_segments.m_buffer.Length) return "invalidSegmentId";
            NetSegment road = net.m_segments.m_buffer[id];
            if ((road.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created ||
                road.Info == null || road.Info.name != "Basic Road")
                return "notActiveBasicRoad";
            if (road.m_startNode == 0 || road.m_endNode == 0 ||
                !RoadAnchor(net.m_nodes.m_buffer[road.m_startNode]) || !RoadAnchor(net.m_nodes.m_buffer[road.m_endNode]))
                return "invalidSegmentEndpoint";
            if (OccupiedBlock(road.m_blockStartLeft) || OccupiedBlock(road.m_blockStartRight) ||
                OccupiedBlock(road.m_blockEndLeft) || OccupiedBlock(road.m_blockEndRight))
                return "occupiedZoneBlock";
            Bezier3 curve = road.GenerateBezier(id, road.m_startNode);
            point.m_position = curve.Position(fraction);
            if (Vector3.Distance(point.m_position, net.m_nodes.m_buffer[road.m_startNode].m_position) < 7f ||
                Vector3.Distance(point.m_position, net.m_nodes.m_buffer[road.m_endNode].m_position) < 7f)
                return "splitTooCloseToEndpoint";
            point.m_segment = id;
            return null;
        }

        private static bool OccupiedBlock(ushort id)
        {
            ZoneBlock[] blocks = ZoneManager.instance.m_blocks.m_buffer;
            return id != 0 && id < blocks.Length && (blocks[id].m_occupied1 | blocks[id].m_occupied2) != 0;
        }

        private static HashSet<ushort> ProtectedRoads(out Dictionary<ushort, NativeCityLedger.Road> sources)
        {
            NativeCityLedger.Road[] roads;
            NativeCityLedger.Lot[] lots;
            NativeCityLedger.Snapshot(out roads, out lots);
            sources = new Dictionary<ushort, NativeCityLedger.Road>();
            foreach (NativeCityLedger.Road road in roads)
                if (NativeCityLedger.HasActiveRoad(road)) sources.Add(road.Id, road);
            var protectedRoads = new HashSet<ushort>();
            foreach (NativeCityLedger.Lot lot in lots)
                if (NativeCityLedger.HasActiveZone(lot)) protectedRoads.Add(lot.Road);
            foreach (global::Building building in BuildingManager.instance.m_buildings.m_buffer)
                if ((building.m_flags & (global::Building.Flags.Created | global::Building.Flags.Deleted)) == global::Building.Flags.Created)
                    protectedRoads.Add(building.m_accessSegment);
            return protectedRoads;
        }

        private static ushort[] SplitParts(NetManager net, NetSegment former, ushort middle)
        {
            if (middle == 0 || middle == former.m_startNode || middle == former.m_endNode) return null;
            ushort first = 0, last = 0;
            NetNode node = net.m_nodes.m_buffer[middle];
            for (int i = 0; i < 8; i++)
            {
                ushort id = node.GetSegment(i);
                if (id == 0) continue;
                NetSegment part = net.m_segments.m_buffer[id];
                if (part.Info != former.Info || (part.m_flags & NetSegment.Flags.Created) == 0) continue;
                ushort other = part.m_startNode == middle ? part.m_endNode : part.m_startNode;
                if (other == former.m_startNode) first = id;
                if (other == former.m_endNode) last = id;
            }
            return first != 0 && last != 0 ? new ushort[] {first,last} : null;
        }

        private static void RecordSplit(NetManager net, Dictionary<ushort, NativeCityLedger.Road> sources, ushort former, ushort[] parts)
        {
            NativeCityLedger.Road road;
            if (!sources.TryGetValue(former, out road)) return;
            NativeCityLedger.RemoveRoad(road);
            foreach (ushort id in parts)
            {
                NetSegment native = net.m_segments.m_buffer[id];
                NativeCityLedger.Record(new NativeCityLedger.Road { Id=id, Start=native.m_startNode, End=native.m_endNode,
                    BuildIndex=native.m_buildIndex, Source=road.Source, Tag=road.Tag });
            }
        }

        private static ToolBase.ToolErrors ProbeLink(NetInfo link, NetTool.ControlPoint start,
            NetTool.ControlPoint middle, NetTool.ControlPoint end, FastList<NetTool.NodePosition> buffer,
            Dictionary<string, object> entry)
        {
            ToolController controller = ToolManager.instance.m_properties;
            ulong[] segments, buildings;
            controller.BeginColliding(out segments, out buildings);
            try
            {
                ushort firstNode, lastNode, segment;
                int cost, production;
                ToolBase.ToolErrors errors = NetTool.CreateNode(link, start, middle, end, buffer, 8,
                    true, true, false, false, true, false, false, 0,
                    out firstNode, out lastNode, out segment, out cost, out production);
                entry["preflight"] = errors.ToString();
                entry["estimatedCostRaw"] = cost;
                entry["overlappingSegments"] = ReadCollisionIds(segments);
                entry["overlappingBuildings"] = ReadCollisionIds(buildings);
                return errors;
            }
            finally
            {
                controller.EndColliding();
            }
        }

        private static List<int> ReadCollisionIds(ulong[] words)
        {
            var ids = new List<int>();
            for (int word = 0; word < words.Length; word++)
            {
                ulong mask = words[word];
                if (mask == 0) continue;
                for (int bit = 0; bit < 64; bit++)
                    if ((mask & (1ul << bit)) != 0) ids.Add(word * 64 + bit);
            }
            return ids;
        }

        private static bool RoadAnchor(NetNode node)
        {
            return (node.m_flags & (NetNode.Flags.Created | NetNode.Flags.Deleted)) == NetNode.Flags.Created &&
                node.Info != null && node.Info.m_class.m_service == ItemClass.Service.Road &&
                node.CountSegments() < 8 &&
                Mathf.Abs(TerrainManager.instance.SampleRawHeightSmoothWithWater(node.m_position, false, 0f) - node.m_position.y) <= .5f;
        }

        private static NetTool.ControlPoint Point(ushort node, Vector3 position, Vector3 direction)
        {
            return new NetTool.ControlPoint {m_node=node,m_position=position,m_direction=direction};
        }
    }
}
