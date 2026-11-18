using System;
using System.IO;
using System.Text;
using CitywideRouting;

namespace NagoyaRoadDelivery
{
    public sealed class GradeProfile
    {
        private struct Edge
        {
            public byte Status;
            public float Start, End, Deck, RampStart, RampEnd, Length;
        }
        private readonly Edge[] edges;
        public int SupportedRamps { get; private set; }
        public int FlatDecks { get; private set; }
        public int UnsupportedShort { get; private set; }
        public int NegativeUnverified { get; private set; }
        public int Status(int edge) { return edges[edge].Status; }
        private GradeProfile(int count) { edges = new Edge[count]; }
        private static float Number(BinaryReader reader, float low, float high)
        {
            float value = reader.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value) || value < low || value > high)
                throw new InvalidDataException("Grade number bounds");
            return value;
        }
        public static GradeProfile Load(Stream input, RoutingGraph graph)
        {
            using (BinaryReader reader = new BinaryReader(input))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(5)) != "NGRD1") throw new InvalidDataException("Grade magic");
                int nodes = reader.ReadInt32(), count = reader.ReadInt32();
                if (nodes != graph.NodeCount || count != graph.EdgeCount || input.Length != 21L + nodes * 4L + count * 24L)
                    throw new InvalidDataException("Grade graph dimensions");
                float baseY = Number(reader, 139, 141), maximum = Number(reader, .01f, .1f);
                float[] heights = new float[nodes];
                for (int i = 0; i < nodes; i++) heights[i] = Number(reader, 100, 200);
                GradeProfile result = new GradeProfile(count);
                for (int i = 0; i < count; i++)
                {
                    short layer = reader.ReadInt16(); byte status = reader.ReadByte(), reserved = reader.ReadByte();
                    EdgeInfo source = graph.GetEdgeInfo(i);
                    Edge edge = new Edge(); edge.Status = status;
                    edge.Start = Number(reader, 100, 200); edge.End = Number(reader, 100, 200); edge.Deck = Number(reader, 100, 200);
                    edge.RampStart = Number(reader, 0, 2000); edge.RampEnd = Number(reader, 0, 2000); edge.Length = source.ActualLengthMeters;
                    if (layer != source.Layer || status > 4 || reserved != 0 ||
                        Math.Abs(edge.Start - heights[source.FromNode]) > .002f || Math.Abs(edge.End - heights[source.ToNode]) > .002f)
                        throw new InvalidDataException("Grade source identity/endpoints");
                    if ((status == 0 && layer != 0) || (status >= 1 && status <= 3 && layer <= 0) || (status == 4 && layer >= 0))
                        throw new InvalidDataException("Grade status/layer mismatch");
                    if (status == 0 && (Math.Abs(edge.Start - baseY) > .002f || Math.Abs(edge.End - baseY) > .002f))
                        throw new InvalidDataException("Ground grade drift");
                    if (status == 1)
                    {
                        if (edge.RampStart + edge.RampEnd > edge.Length + .003f ||
                            (edge.RampStart == 0 && Math.Abs(edge.Deck - edge.Start) > .002f) ||
                            (edge.RampEnd == 0 && Math.Abs(edge.Deck - edge.End) > .002f) ||
                            (edge.RampStart > 0 && Math.Abs(edge.Deck - edge.Start) / edge.RampStart > maximum + .00002f) ||
                            (edge.RampEnd > 0 && Math.Abs(edge.Deck - edge.End) / edge.RampEnd > maximum + .00002f))
                            throw new InvalidDataException("Unsupported ramp labeled supported");
                        result.SupportedRamps++;
                    }
                    if (status == 2)
                    {
                        if (Math.Abs(edge.Start - edge.Deck) > .002f || Math.Abs(edge.End - edge.Deck) > .002f)
                            throw new InvalidDataException("Flat deck endpoint mismatch");
                        result.FlatDecks++;
                    }
                    if (status == 3) result.UnsupportedShort++;
                    if (status == 4) result.NegativeUnverified++;
                    result.edges[i] = edge;
                }
                return result;
            }
        }
        public bool TryHeight(int edgeIndex, double distance, out float height)
        {
            Edge edge = edges[edgeIndex]; height = 0;
            if (edge.Status == 3 || edge.Status == 4) return false;
            if (edge.Status == 0) { height = edge.Start; return true; }
            if (edge.Status == 2) { height = edge.Deck; return true; }
            double s = Math.Max(0, Math.Min(edge.Length, distance));
            if (edge.RampStart > 0 && s <= edge.RampStart)
                height = (float)(edge.Start + (edge.Deck - edge.Start) * s / edge.RampStart);
            else if (edge.RampEnd > 0 && s >= edge.Length - edge.RampEnd)
                height = (float)(edge.Deck + (edge.End - edge.Deck) * (s - (edge.Length - edge.RampEnd)) / edge.RampEnd);
            else height = edge.Deck;
            return true;
        }
    }
}
