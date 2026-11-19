using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
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
            foreach (Candidate candidate in candidates)
            {
                ushort from = candidate.From, to = candidate.To;
                var entry = new Dictionary<string, object> {{"fromNode",from},{"toNode",to}};
                attempts.Add(entry);
                if (from >= net.m_nodes.m_buffer.Length || to >= net.m_nodes.m_buffer.Length) continue;
                NetNode first = net.m_nodes.m_buffer[from], last = net.m_nodes.m_buffer[to];
                Vector3 delta = last.m_position - first.m_position;
                if (!RoadAnchor(first) || !RoadAnchor(last) || delta.sqrMagnitude < 144f ||
                    delta.sqrMagnitude > 48400f || Mathf.Abs(delta.y) > .5f)
                {
                    entry["preflight"] = "invalidGroundRoadAnchors";
                    continue;
                }
                Vector3 middlePosition = (first.m_position + last.m_position) * .5f;
                if (candidate.Bend.HasValue)
                {
                    Vector2 bend = candidate.Bend.Value;
                    middlePosition.x = bend.x;
                    middlePosition.z = bend.y;
                    entry["bend"] = new float[] { bend.x, bend.y };
                }
                Vector3 startLeg = middlePosition - first.m_position;
                Vector3 endLeg = last.m_position - middlePosition;
                if (startLeg.sqrMagnitude < 36f || endLeg.sqrMagnitude < 36f ||
                    Vector3.Dot(startLeg, endLeg) <= 0f || startLeg.magnitude + endLeg.magnitude > 325f)
                {
                    entry["preflight"] = "invalidBend";
                    continue;
                }
                NetTool.ControlPoint start = Point(from, first.m_position, startLeg.normalized);
                NetTool.ControlPoint end = Point(to, last.m_position, endLeg.normalized);
                NetTool.ControlPoint middle = Point(0, middlePosition, delta.normalized);
                ushort firstNode, lastNode, segment;
                int cost, production;
                entry["lengthMeters"] = delta.magnitude;
                ToolBase.ToolErrors errors = ProbeLink(link, start, middle, end, buffer, entry);
                if (errors != ToolBase.ToolErrors.None) continue;

                int before = net.m_segmentCount;
                buffer.Clear();
                errors = NetTool.CreateNode(link, start, middle, end, buffer, 8,
                    false, true, false, false, true, false, false, 0,
                    out firstNode, out lastNode, out segment, out cost, out production);
                entry["built"] = errors.ToString();
                entry["actualFirstNode"] = firstNode;
                entry["actualLastNode"] = lastNode;
                entry["segmentId"] = segment;
                entry["segmentsAdded"] = net.m_segmentCount - before;
                return new Dictionary<string, object> {
                    {"connected",errors == ToolBase.ToolErrors.None && firstNode == from && lastNode == to && net.m_segmentCount > before},
                    {"prefab",prefab}, {"attempts",attempts}
                };
            }
            return new Dictionary<string, object> {{"connected",false},{"prefab",prefab},{"attempts",attempts}};
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
