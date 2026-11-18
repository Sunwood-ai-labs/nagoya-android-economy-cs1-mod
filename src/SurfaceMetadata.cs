using System;
using System.IO;
using System.Text;
using CitywideRouting;

namespace NagoyaRoadDelivery
{
    public sealed class SurfaceMetadata
    {
        private readonly float[] widths;
        private readonly uint[] flags;
        public int EdgeCount { get { return widths.Length; } }
        public float Width(int edge) { return widths[edge]; }
        public bool MarkedCrossing(int edge) { return (flags[edge] & 1) != 0; }
        private SurfaceMetadata(float[] values, uint[] attributes) { widths = values; flags = attributes; }
        public static SurfaceMetadata Load(Stream input, int expectedEdges)
        {
            if (input == null || !input.CanRead || !input.CanSeek) throw new ArgumentException("Readable seekable metadata required");
            using (BinaryReader reader = new BinaryReader(input))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "NSM1") throw new InvalidDataException("Surface metadata magic");
                int count = reader.ReadInt32();
                if (count < 1 || count > 500000 || count != expectedEdges || input.Length != 8L + count * 8L)
                    throw new InvalidDataException("Surface metadata count/length");
                float[] widths = new float[count]; uint[] flags = new uint[count];
                for (int i = 0; i < count; i++)
                {
                    float width = reader.ReadSingle(); uint bits = reader.ReadUInt32();
                    if (float.IsNaN(width) || float.IsInfinity(width) || width < 1 || width > 30 || (bits & ~3u) != 0)
                        throw new InvalidDataException("Surface metadata width/flags");
                    widths[i] = width; flags[i] = bits;
                }
                return new SurfaceMetadata(widths, flags);
            }
        }
    }

    public static class SurfacePlacementMath
    {
        public static Point2 Miter(Point2[] points, int index)
        {
            int before = index - 1, after = index + 1;
            for (; before >= 0 && points[before].X == points[index].X && points[before].Z == points[index].Z; before--) { }
            for (; after < points.Length && points[after].X == points[index].X && points[after].Z == points[index].Z; after++) { }
            if (before < 0 && after >= points.Length) return new Point2(0, 0);
            double ax, az, bx, bz;
            if (before < 0) { ax = points[after].X - points[index].X; az = points[after].Z - points[index].Z; }
            else { ax = points[index].X - points[before].X; az = points[index].Z - points[before].Z; }
            if (after >= points.Length) { bx = ax; bz = az; }
            else { bx = points[after].X - points[index].X; bz = points[after].Z - points[index].Z; }
            double al = Math.Sqrt(ax * ax + az * az), bl = Math.Sqrt(bx * bx + bz * bz);
            ax /= al; az /= al; bx /= bl; bz /= bl;
            double nx = -az - bz, nz = ax + bx, length = Math.Sqrt(nx * nx + nz * nz);
            if (length < .000001) return new Point2((float)-bz, (float)bx);
            nx /= length; nz /= length;
            double dot = nx * -bz + nz * bx;
            double scale = Math.Min(2, 1 / Math.Max(.000001, dot));
            return new Point2((float)(nx * scale), (float)(nz * scale));
        }
        public static Point2 OffsetMiter(Point2 a, Point2 b, Point2 normalA, Point2 normalB,
            float t, float offset, int actorId, float headingDegrees, bool travelAligned)
        {
            double side = actorId % 2 == 0 ? -1 : 1;
            double angle = headingDegrees * Math.PI / 180;
            if (travelAligned && (b.X - a.X) * Math.Cos(angle) + (b.Z - a.Z) * Math.Sin(angle) < 0) side = -side;
            double nx = normalA.X + ((double)normalB.X - normalA.X) * t;
            double nz = normalA.Z + ((double)normalB.Z - normalA.Z) * t;
            return new Point2((float)(a.X + ((double)b.X - a.X) * t + nx * offset * side),
                (float)(a.Z + ((double)b.Z - a.Z) * t + nz * offset * side));
        }
        // Keep a side relative to travel, independent of the source edge's digitizing direction.
        public static Point2 Offset(Point2 a, Point2 b, float t, float offset, int actorId, float headingDegrees, bool travelAligned)
        {
            double dx = b.X - a.X, dz = b.Z - a.Z, length = Math.Sqrt(dx * dx + dz * dz);
            double x = a.X + dx * t, z = a.Z + dz * t;
            if (length <= 0.00001) return new Point2((float)x, (float)z);
            double side = actorId % 2 == 0 ? -1 : 1;
            if (travelAligned)
            {
                double angle = headingDegrees * Math.PI / 180;
                if (dx * Math.Cos(angle) + dz * Math.Sin(angle) < 0) side = -side;
            }
            return new Point2((float)(x - dz / length * offset * side), (float)(z + dx / length * offset * side));
        }
    }
}
