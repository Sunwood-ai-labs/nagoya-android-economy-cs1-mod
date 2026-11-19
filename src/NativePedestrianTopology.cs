using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    internal static class NativePedestrianTopology
    {
        internal static string Write(string caseId)
        {
            NetManager net = NetManager.instance;
            NetSegment[] segments = net.m_segments.m_buffer;
            NetNode[] nodes = net.m_nodes.m_buffer;
            NetLane[] lanes = net.m_lanes.m_buffer;
            string file = "native-employment-" + caseId + "-walk-network.tsv";
            string path = Path.Combine(NativeCityRoadBuilder.Root, file);
            string temporary = path + ".tmp";
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("segment\tstartNode\tendNode\tservice\tprefab\tlength\twalkLanes\tvehicleLanes\tstartX\tstartY\tstartZ\tendX\tendY\tendZ");
                for (int id = 1; id < segments.Length; id++)
                {
                    NetSegment road = segments[id];
                    if ((road.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created ||
                        road.m_startNode == 0 || road.m_endNode == 0)
                        continue;
                    NetInfo info = road.Info;
                    if (info == null || info.m_lanes == null) continue;
                    int walk = 0, vehicles = 0;
                    uint lane = road.m_lanes;
                    for (int index = 0; index < info.m_lanes.Length && lane != 0 && lane < lanes.Length; index++)
                    {
                        if (lanes[lane].m_segment != id) break;
                        NetInfo.LaneType type = info.m_lanes[index].m_laneType;
                        if ((type & NetInfo.LaneType.Pedestrian) != 0) walk++;
                        if ((type & NetInfo.LaneType.Vehicle) != 0) vehicles++;
                        lane = lanes[lane].m_nextLane;
                    }
                    ItemClass.Service service = info.GetService();
                    if (walk == 0 && service != ItemClass.Service.Road) continue;
                    Vector3 start = nodes[road.m_startNode].m_position;
                    Vector3 end = nodes[road.m_endNode].m_position;
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1}\t{2}\t{3}\t{4}\t{5:R}\t{6}\t{7}\t{8:R}\t{9:R}\t{10:R}\t{11:R}\t{12:R}\t{13:R}",
                        id, road.m_startNode, road.m_endNode, service, info.name,
                        road.m_averageLength > 0 ? road.m_averageLength : Vector3.Distance(start, end),
                        walk, vehicles, start.x, start.y, start.z, end.x, end.y, end.z));
                }
            }
            File.Move(temporary, path);
            return file;
        }
    }
}
