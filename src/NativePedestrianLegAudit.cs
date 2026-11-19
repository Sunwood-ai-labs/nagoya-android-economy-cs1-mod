using System;
using System.Collections.Generic;
using ColossalFramework.Math;

namespace NagoyaRoadDelivery
{
    internal sealed class NativePedestrianLegAudit : IDisposable
    {
        internal const int MaxLegs = 24;
        private readonly List<Leg> legs = new List<Leg>();

        private sealed class Leg
        {
            public ushort From, To;
            public uint Path;
            public string UnqueuedReason;
        }

        internal NativePedestrianLegAudit(ushort[][] pairs)
        {
            try
            {
                foreach (ushort[] pair in pairs) Queue(pair[0], pair[1]);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Queue(ushort from, ushort to)
        {
            var leg = new Leg { From=from, To=to };
            legs.Add(leg);
            PathUnit.Position a, b, c, d;
            if (!Endpoints(from, from == to ? (byte)32 : (byte)128, out a, out b) ||
                !Endpoints(to, from == to ? (byte)224 : (byte)128, out c, out d))
            {
                leg.UnqueuedReason = "missingPedestrianLane";
                return;
            }
            var random = new Randomizer((uint)from * 65536u + to);
            if (!PathManager.instance.CreatePath(out leg.Path, ref random, SimulationManager.instance.m_currentBuildIndex,
                a, b, c, d, NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None, VehicleInfo.VehicleCategory.None, 20000f))
                leg.UnqueuedReason = "notQueued";
        }

        private static bool Endpoints(ushort id, byte offset, out PathUnit.Position first, out PathUnit.Position second)
        {
            first = second = default(PathUnit.Position);
            NetSegment[] segments = NetManager.instance.m_segments.m_buffer;
            if (!NativeDeliveryStreetReach.HasSidewalk(segments,id)) return false;
            NetSegment segment = segments[id];
            NetLane[] liveLanes = NetManager.instance.m_lanes.m_buffer;
            uint lane = segment.m_lanes;
            for (int i = 0; i < segment.Info.m_lanes.Length && i <= byte.MaxValue && lane != 0 && lane < liveLanes.Length; i++)
            {
                if ((segment.Info.m_lanes[i].m_laneType & NetInfo.LaneType.Pedestrian) != 0)
                {
                    var found = new PathUnit.Position {m_segment=id,m_lane=(byte)i,m_offset=offset};
                    if (first.m_segment == 0) first = found;
                    else { second = found; break; }
                }
                lane = liveLanes[lane].m_nextLane;
            }
            return first.m_segment != 0;
        }

        internal object Finish()
        {
            var results = new List<object>();
            foreach (Leg leg in legs) results.Add(new Dictionary<string, object> {
                {"fromSegment",leg.From}, {"toSegment",leg.To},
                {"path",NativeCommuteRouteAudit.Describe(leg.Path, leg.UnqueuedReason)}
            });
            Dispose();
            return results;
        }

        public void Dispose()
        {
            foreach (Leg leg in legs)
            {
                if (leg.Path != 0) PathManager.instance.ReleasePath(leg.Path);
                leg.Path = 0;
            }
        }
    }
}
