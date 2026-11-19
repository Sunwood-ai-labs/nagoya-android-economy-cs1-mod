using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CitywideRouting
{
    /// <summary>
    /// Small, standalone routing core for the NCG1 source graph.
    /// This is a MOD-owned offline graph and does not call the CS1 native pathfinder.
    /// </summary>
    public struct Point2
    {
        public float X;
        public float Z;

        public Point2(float x, float z)
        {
            X = x;
            Z = z;
        }
    }

    public struct EdgeInfo
    {
        public int FromNode;
        public int ToNode;
        public float DeclaredLengthMeters;
        public float ActualLengthMeters;
        public byte ClassId;
        public byte Flags;
        public short Layer;
        public int PointOffset;
        public int PointCount;

        public bool PedestrianAllowed
        {
            get { return (Flags & 1) != 0; }
        }
    }

    public sealed class RouteResult
    {
        public readonly bool Found;
        public readonly string FailureReason;
        public readonly double TotalLengthMeters;
        public readonly int ExpandedNodeCount;
        public readonly int[] EdgeIndices;
        public readonly bool[] Reversed;
        public readonly Point2[] Waypoints;
        public readonly int[] WaypointEdgeIndices;

        internal RouteResult(bool found, string failureReason, double totalLengthMeters,
            int expandedNodeCount, int[] edgeIndices, bool[] reversed, Point2[] waypoints, int[] waypointEdgeIndices)
        {
            Found = found;
            FailureReason = failureReason;
            TotalLengthMeters = totalLengthMeters;
            ExpandedNodeCount = expandedNodeCount;
            EdgeIndices = edgeIndices;
            Reversed = reversed;
            Waypoints = waypoints; WaypointEdgeIndices = waypointEdgeIndices;
        }

        public static RouteResult NotFound(string reason, int expanded)
        {
            return new RouteResult(false, reason, 0.0, expanded, new int[0], new bool[0], new Point2[0], new int[0]);
        }
    }

    public sealed class RoutingGraph
    {
        private const uint MaxCount = 1000000U;
        private const float CoordinateLimit = 40000.0f;
        private const float MinPolylineLength = 0.0f;
        private const double ImprovementEpsilon = 1.0e-9;

        private readonly Point2[] _nodes;
        private readonly EdgeInfo[] _edges;
        private readonly Point2[] _points;
        private readonly int[] _adjOffsets;
        private readonly int[] _adjTo;
        private readonly int[] _adjEdge;
        private readonly bool[] _adjReversed;

        private RoutingGraph(Point2[] nodes, EdgeInfo[] edges, Point2[] points,
            int[] adjOffsets, int[] adjTo, int[] adjEdge, bool[] adjReversed)
        {
            _nodes = nodes;
            _edges = edges;
            _points = points;
            _adjOffsets = adjOffsets;
            _adjTo = adjTo;
            _adjEdge = adjEdge;
            _adjReversed = adjReversed;
        }

        public int NodeCount { get { return _nodes.Length; } }
        public int EdgeCount { get { return _edges.Length; } }
        public int PedestrianAdjacencyCount { get { return _adjTo.Length; } }

        public static RoutingGraph Load(string path)
        {
            if (path == null) throw new ArgumentNullException("path");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Load(stream);
            }
        }

        public static RoutingGraph Load(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (!stream.CanRead || !stream.CanSeek) throw new InvalidDataException("NCG1 stream must be readable and seekable");
            BinaryReader reader = new BinaryReader(stream, Encoding.ASCII);

            byte[] magic = reader.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != (byte)'N' || magic[1] != (byte)'C' || magic[2] != (byte)'G' || magic[3] != (byte)'1')
                throw new InvalidDataException("NCG1 header mismatch");

            uint nodeCountRaw = ReadCount(reader, "nodeCount");
            uint edgeCountRaw = ReadCount(reader, "edgeCount");
            int nodeCount = checked((int)nodeCountRaw);
            int edgeCount = checked((int)edgeCountRaw);
            Point2[] nodes = new Point2[nodeCount];
            int i;
            for (i = 0; i < nodeCount; i++)
            {
                float x = reader.ReadSingle();
                float z = reader.ReadSingle();
                ValidateCoordinate(x, "node x", i);
                ValidateCoordinate(z, "node z", i);
                nodes[i] = new Point2(x, z);
            }

            EdgeInfo[] edges = new EdgeInfo[edgeCount];
            List<Point2> points = new List<Point2>(checked(edgeCount * 2));
            for (i = 0; i < edgeCount; i++)
            {
                uint fromRaw = reader.ReadUInt32();
                uint toRaw = reader.ReadUInt32();
                if (fromRaw >= nodeCountRaw || toRaw >= nodeCountRaw)
                    throw new InvalidDataException("edge endpoint index out of range at edge " + i);
                float declaredLength = reader.ReadSingle();
                if (float.IsNaN(declaredLength) || float.IsInfinity(declaredLength) || declaredLength <= 0.0f)
                    throw new InvalidDataException("edge length must be finite and > 0 at edge " + i);
                byte classId = reader.ReadByte();
                byte flags = reader.ReadByte();
                short layer = reader.ReadInt16();
                uint pointCountRaw = reader.ReadUInt32();
                if (pointCountRaw < 2U || pointCountRaw > 4096U)
                    throw new InvalidDataException("edge pointCount must be in [2,4096] at edge " + i);
                int pointCount = checked((int)pointCountRaw);
                int pointOffset = points.Count;
                double actual = 0.0;
                Point2 previous = new Point2();
                for (int p = 0; p < pointCount; p++)
                {
                    float x = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    ValidateCoordinate(x, "edge point x", i);
                    ValidateCoordinate(z, "edge point z", i);
                    Point2 point = new Point2(x, z);
                    if (p > 0)
                    {
                        double dx = (double)point.X - previous.X;
                        double dz = (double)point.Z - previous.Z;
                        actual += Math.Sqrt(dx * dx + dz * dz);
                    }
                    points.Add(point);
                    previous = point;
                }
                if (double.IsNaN(actual) || double.IsInfinity(actual) || actual <= MinPolylineLength)
                    throw new InvalidDataException("edge polyline length must be finite and > 0 at edge " + i);
                edges[i] = new EdgeInfo
                {
                    FromNode = checked((int)fromRaw),
                    ToNode = checked((int)toRaw),
                    DeclaredLengthMeters = declaredLength,
                    ActualLengthMeters = (float)actual,
                    ClassId = classId,
                    Flags = flags,
                    Layer = layer,
                    PointOffset = pointOffset,
                    PointCount = pointCount
                };
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException("NCG1 has trailing bytes: " + (stream.Length - stream.Position));

            Point2[] pointArray = points.ToArray();
            int[] degree = new int[nodeCount];
            long adjacencyCountLong = 0;
            for (i = 0; i < edges.Length; i++)
            {
                if (!edges[i].PedestrianAllowed) continue;
                degree[edges[i].FromNode] = checked(degree[edges[i].FromNode] + 1);
                degree[edges[i].ToNode] = checked(degree[edges[i].ToNode] + 1);
                adjacencyCountLong += 2L;
            }
            if (adjacencyCountLong > Int32.MaxValue) throw new InvalidDataException("pedestrian adjacency is too large");
            int adjacencyCount = (int)adjacencyCountLong;
            int[] offsets = new int[nodeCount + 1];
            for (i = 0; i < nodeCount; i++) offsets[i + 1] = checked(offsets[i] + degree[i]);
            int[] cursor = (int[])offsets.Clone();
            int[] adjTo = new int[adjacencyCount];
            int[] adjEdge = new int[adjacencyCount];
            bool[] adjReversed = new bool[adjacencyCount];
            for (i = 0; i < edges.Length; i++)
            {
                if (!edges[i].PedestrianAllowed) continue;
                EdgeInfo edge = edges[i];
                int a = cursor[edge.FromNode]++;
                adjTo[a] = edge.ToNode; adjEdge[a] = i; adjReversed[a] = false;
                int b = cursor[edge.ToNode]++;
                adjTo[b] = edge.FromNode; adjEdge[b] = i; adjReversed[b] = true;
            }
            return new RoutingGraph(nodes, edges, pointArray, offsets, adjTo, adjEdge, adjReversed);
        }

        private static uint ReadCount(BinaryReader reader, string name)
        {
            uint count = reader.ReadUInt32();
            if (count > MaxCount) throw new InvalidDataException(name + " exceeds 1,000,000");
            return count;
        }

        private static void ValidateCoordinate(float value, string what, int index)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > CoordinateLimit)
                throw new InvalidDataException(what + " invalid at index " + index);
        }

        public Point2 GetNodePosition(int node)
        {
            CheckNode(node);
            return _nodes[node];
        }

        public EdgeInfo GetEdgeInfo(int edge)
        {
            CheckEdge(edge);
            return _edges[edge];
        }

        public int GetPedestrianDegree(int node)
        {
            CheckNode(node);
            return _adjOffsets[node + 1] - _adjOffsets[node];
        }

        public int GetPedestrianNeighbor(int node, int ordinal, out int edgeIndex, out bool reversed)
        {
            CheckNode(node);
            int begin = _adjOffsets[node];
            int end = _adjOffsets[node + 1];
            if (ordinal < 0 || begin + ordinal >= end) throw new ArgumentOutOfRangeException("ordinal");
            int i = begin + ordinal;
            edgeIndex = _adjEdge[i];
            reversed = _adjReversed[i];
            return _adjTo[i];
        }

        public Point2[] GetEdgePoints(int edgeIndex, bool reversed)
        {
            CheckEdge(edgeIndex);
            EdgeInfo edge = _edges[edgeIndex];
            Point2[] result = new Point2[edge.PointCount];
            for (int i = 0; i < edge.PointCount; i++)
            {
                int source = reversed ? edge.PointCount - 1 - i : i;
                result[i] = _points[edge.PointOffset + source];
            }
            return result;
        }

        public RouteResult FindRoute(int startNode, int endNode)
        {
            CheckNode(startNode); CheckNode(endNode);
            if (startNode == endNode)
                return new RouteResult(true, null, 0.0, 0, new int[0], new bool[0], new Point2[] { _nodes[startNode] }, new int[] { -1 });

            int n = _nodes.Length;
            double[] g = new double[n];
            double[] f = new double[n];
            int[] parentNode = new int[n];
            int[] parentEdge = new int[n];
            bool[] parentReversed = new bool[n];
            bool[] closed = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = double.PositiveInfinity; f[i] = double.PositiveInfinity; parentNode[i] = -1; parentEdge[i] = -1; }

            MinHeap open = new MinHeap();
            g[startNode] = 0.0;
            f[startNode] = Heuristic(startNode, endNode);
            open.Push(startNode, f[startNode]);
            int expanded = 0;
            bool reached = false;
            while (open.Count > 0)
            {
                HeapItem item = open.Pop();
                if (closed[item.Node]) continue;
                if (item.Priority > f[item.Node] + 1.0e-7) continue;
                closed[item.Node] = true;
                expanded++;
                if (item.Node == endNode) { reached = true; break; }
                if (expanded >= n) break;

                int begin = _adjOffsets[item.Node];
                int end = _adjOffsets[item.Node + 1];
                for (int i = begin; i < end; i++)
                {
                    int next = _adjTo[i];
                    if (closed[next]) continue;
                    int edgeIndex = _adjEdge[i];
                    double candidate = g[item.Node] + _edges[edgeIndex].ActualLengthMeters;
                    if (candidate + ImprovementEpsilon < g[next])
                    {
                        parentNode[next] = item.Node;
                        parentEdge[next] = edgeIndex;
                        parentReversed[next] = _adjReversed[i];
                        g[next] = candidate;
                        f[next] = candidate + Heuristic(next, endNode);
                        open.Push(next, f[next]);
                    }
                }
            }
            if (!reached) return RouteResult.NotFound("unreachable pedestrian graph", expanded);

            List<int> reverseEdges = new List<int>();
            List<bool> reverseFlags = new List<bool>();
            int cursor = endNode;
            while (cursor != startNode)
            {
                if (parentNode[cursor] < 0 || parentEdge[cursor] < 0)
                    return RouteResult.NotFound("route reconstruction failed", expanded);
                reverseEdges.Add(parentEdge[cursor]);
                reverseFlags.Add(parentReversed[cursor]);
                cursor = parentNode[cursor];
            }
            reverseEdges.Reverse(); reverseFlags.Reverse();
            int[] waypointEdges; Point2[] waypoints = BuildWaypoints(reverseEdges, reverseFlags, out waypointEdges);
            return new RouteResult(true, null, g[endNode], expanded, reverseEdges.ToArray(), reverseFlags.ToArray(), waypoints, waypointEdges);
        }

        // API spelling used by the parent delivery code; this remains an offline MOD graph route.
        public RouteResult Route(int startNode, int endNode)
        {
            return FindRoute(startNode, endNode);
        }

        private Point2[] BuildWaypoints(List<int> edges, List<bool> reversed, out int[] waypointEdges)
        {
            List<Point2> result = new List<Point2>(); List<int> labels = new List<int>();
            for (int i = 0; i < edges.Count; i++)
            {
                Point2[] part = GetEdgePoints(edges[i], reversed[i]);
                for (int p = 0; p < part.Length; p++)
                {
                    if (result.Count > 0 && p == 0 && SamePoint(result[result.Count - 1], part[p])) continue;
                    result.Add(part[p]); labels.Add(edges[i]);
                }
            }
            waypointEdges=labels.ToArray(); return result.ToArray();
        }

        private static bool SamePoint(Point2 a, Point2 b)
        {
            return Math.Abs((double)a.X - b.X) <= 0.001 && Math.Abs((double)a.Z - b.Z) <= 0.001;
        }

        private double Heuristic(int from, int to)
        {
            double dx = (double)_nodes[from].X - _nodes[to].X;
            double dz = (double)_nodes[from].Z - _nodes[to].Z;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        private void CheckNode(int node)
        {
            if (node < 0 || node >= _nodes.Length) throw new ArgumentOutOfRangeException("node");
        }

        private void CheckEdge(int edge)
        {
            if (edge < 0 || edge >= _edges.Length) throw new ArgumentOutOfRangeException("edge");
        }

        internal struct HeapItem
        {
            public int Node;
            public double Priority;
            public HeapItem(int node, double priority) { Node = node; Priority = priority; }
        }

        internal sealed class MinHeap
        {
            private readonly List<HeapItem> _items = new List<HeapItem>();
            public int Count { get { return _items.Count; } }
            public void Push(int node, double priority)
            {
                _items.Add(new HeapItem(node, priority));
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_items[p].Priority <= _items[i].Priority) break;
                    HeapItem t = _items[p]; _items[p] = _items[i]; _items[i] = t; i = p;
                }
            }
            public HeapItem Pop()
            {
                if (_items.Count == 0) throw new InvalidOperationException("empty heap");
                HeapItem result = _items[0];
                int last = _items.Count - 1;
                _items[0] = _items[last]; _items.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int left = i * 2 + 1, right = left + 1, best = i;
                    if (left < _items.Count && _items[left].Priority < _items[best].Priority) best = left;
                    if (right < _items.Count && _items[right].Priority < _items[best].Priority) best = right;
                    if (best == i) break;
                    HeapItem t = _items[i]; _items[i] = _items[best]; _items[best] = t; i = best;
                }
                return result;
            }
        }
    }
}
