using System;
using System.Collections.Generic;
using CitywideRouting;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    // Orders candidates using the live CS1 street network. ResidentAI still resolves the actual trip.
    internal sealed class NativeDeliveryStreetReach
    {
        private readonly NetNode[] nodes = NetManager.instance.m_nodes.m_buffer;
        private readonly NetSegment[] segments = NetManager.instance.m_segments.m_buffer;
        private readonly Dictionary<ushort, double> distance = new Dictionary<ushort, double>();
        private readonly ushort sourceRoad;
        private readonly Vector3 sourcePosition;
        private readonly double radius;

        internal NativeDeliveryStreetReach(ushort road, Vector3 position, double maxMeters)
        {
            sourceRoad = road;
            sourcePosition = position;
            radius = maxMeters;
            if (!HasSidewalk(road)) return;

            var open = new RoutingGraph.MinHeap();
            NetSegment start = segments[road];
            Add(start.m_startNode, Vector3.Distance(position, nodes[start.m_startNode].m_position), open);
            Add(start.m_endNode, Vector3.Distance(position, nodes[start.m_endNode].m_position), open);
            while (open.Count > 0)
            {
                RoutingGraph.HeapItem current = open.Pop();
                ushort id = (ushort)current.Node;
                if (current.Priority > distance[id]) continue;
                NetNode node = nodes[id];
                for (int edge = 0; edge < 8; edge++)
                {
                    ushort segmentId = node.GetSegment(edge);
                    if (!HasSidewalk(segmentId)) continue;
                    NetSegment segment = segments[segmentId];
                    ushort next = segment.m_startNode == id ? segment.m_endNode : segment.m_startNode;
                    double length = Vector3.Distance(node.m_position, nodes[next].m_position);
                    Add(next, current.Priority + length, open);
                }
            }
        }

        private void Add(ushort node, double meters, RoutingGraph.MinHeap open)
        {
            double previous;
            if (meters <= radius && (!distance.TryGetValue(node, out previous) || meters < previous))
            {
                distance[node] = meters;
                open.Push(node, meters);
            }
        }

        internal double DistanceTo(ushort road, Vector3 position)
        {
            if (distance.Count == 0 || !HasSidewalk(road)) return double.PositiveInfinity;
            if (road == sourceRoad) return Vector3.Distance(sourcePosition, position);
            NetSegment target = segments[road];
            return Math.Min(Through(target.m_startNode, position), Through(target.m_endNode, position));
        }

        private double Through(ushort node, Vector3 position)
        {
            double meters;
            if (!distance.TryGetValue(node, out meters)) return double.PositiveInfinity;
            return meters + Vector3.Distance(position, nodes[node].m_position);
        }

        private bool HasSidewalk(ushort id)
        {
            if (id == 0 || id >= segments.Length) return false;
            NetSegment segment = segments[id];
            if ((segment.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created ||
                segment.m_lanes == 0 || segment.m_startNode == 0 || segment.m_endNode == 0 ||
                segment.Info == null || segment.Info.m_lanes == null) return false;
            foreach (NetInfo.Lane lane in segment.Info.m_lanes)
                if ((lane.m_laneType & NetInfo.LaneType.Pedestrian) != 0) return true;
            return false;
        }
    }
}
