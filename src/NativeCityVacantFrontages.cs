using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    internal static class NativeCityVacantFrontages
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly ItemClass.Zone[] Uses = {
            ItemClass.Zone.ResidentialLow, ItemClass.Zone.ResidentialHigh,
            ItemClass.Zone.CommercialLow, ItemClass.Zone.CommercialHigh, ItemClass.Zone.Industrial, ItemClass.Zone.Office
        };

        private sealed class Footprint
        {
            internal int Width, Length, Mask;
        }

        internal static Dictionary<string, object> Export(string root, string commandId, uint expectedBuildIndex, int expectedSegments)
        {
            SimulationManager sim = SimulationManager.instance;
            NetManager net = NetManager.instance;
            if (!sim.SimulationPaused || sim.m_currentBuildIndex != expectedBuildIndex || net.m_segmentCount != expectedSegments ||
                GameAreaManager.instance.m_areaCount != 25)
                throw new InvalidOperationException("The paused vacancy survey checkpoint changed");
            string filename = commandId + "-vacancies.tsv";
            string destination = Path.Combine(root, filename);
            string temporary = destination + ".tmp";
            if (File.Exists(destination) || File.Exists(temporary)) throw new IOException("Vacancy survey output already exists");

            NativeCityLedger.Road[] roads;
            NativeCityLedger.Lot[] lots;
            NativeCityLedger.Snapshot(out roads, out lots);
            var sizes = Footprints();
            var visitedBlocks = new HashSet<ushort>();
            ZoneManager zones = ZoneManager.instance;
            int records = 0, registeredRoads = 0;
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                using (var stream = new StreamWriter(temporary, false, new UTF8Encoding(false), 65536))
                {
                    stream.NewLine = "\n";
                    stream.WriteLine("source\troadId\tbuildIndex\tblock\trow\twidth\tlength\tx\ty\tz\tuseMask");
                    foreach (NativeCityLedger.Road reference in roads)
                    {
                        if (elapsed.Elapsed.TotalSeconds > 25) throw new TimeoutException("Native vacancy survey exceeded its time limit");
                        if (!NativeCityLedger.HasActiveRoad(reference)) continue;
                        registeredRoads++;
                        NetSegment road = net.m_segments.m_buffer[reference.Id];
                        if (road.Info == null || (road.Info.name != "Basic Road" && road.Info.name != "Medium Road")) continue;
                        ushort[] blocks = {road.m_blockStartLeft, road.m_blockStartRight, road.m_blockEndLeft, road.m_blockEndRight};
                        foreach (ushort id in blocks)
                            if (id != 0 && visitedBlocks.Add(id))
                                records += ReadBlock(zones, reference, id, sizes, stream);
                    }
                }
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new Dictionary<string, object> {
                {"file", filename}, {"buildIndex", expectedBuildIndex}, {"segments", expectedSegments},
                {"registeredRoads", registeredRoads}, {"blocks", visitedBlocks.Count}, {"records", records},
                {"zoneMaskOrder", Array.ConvertAll(Uses, delegate(ItemClass.Zone zone) { return zone.ToString(); })},
                {"milliseconds", elapsed.ElapsedMilliseconds}, {"writesToCity", false}
            };
        }

        private static List<Footprint> Footprints()
        {
            var bySize = new Dictionary<int, Footprint>();
            for (int i = 0; i < Uses.Length; i++)
                foreach (BuildingInfo info in NativeCityZoneBuilder.SelectPrefabs(NativeCityZoneBuilder.ZoneService(Uses[i]), false))
                {
                    int width = info.GetWidth(), length = info.GetLength(), key = width * 5 + length;
                    Footprint size;
                    if (!bySize.TryGetValue(key, out size))
                        bySize.Add(key, size = new Footprint {Width=width, Length=length});
                    size.Mask |= 1 << i;
                }
            var sorted = new List<Footprint>(bySize.Values);
            sorted.Sort(delegate(Footprint a, Footprint b) {
                int area = (a.Width * a.Length).CompareTo(b.Width * b.Length);
                return area != 0 ? area : a.Width.CompareTo(b.Width);
            });
            return sorted;
        }

        private static int ReadBlock(ZoneManager zones, NativeCityLedger.Road road, ushort id,
            List<Footprint> sizes, TextWriter output)
        {
            ZoneBlock block = zones.m_blocks.m_buffer[id];
            if ((block.m_flags & ZoneBlock.FLAG_CREATED) == 0) return 0;
            int records = 0;
            for (int row = 0; row < block.RowCount; row++)
            {
                int remaining = (1 << Uses.Length) - 1;
                foreach (Footprint size in sizes)
                {
                    int mask = size.Mask & remaining;
                    if (mask == 0 || row + size.Width > block.RowCount) continue;
                    ItemClass.Zone old;
                    if (!NativeCityZoneBuilder.ReadVacantCells(block, row, size.Width, size.Length, out old)) continue;
                    if (old != ItemClass.Zone.Unzoned)
                    {
                        int index = Array.IndexOf(Uses, old);
                        if (index < 0) continue;
                        mask &= 1 << index;
                    }
                    Vector3 center;
                    float angle;
                    if (mask == 0 || !NativeCityZoneBuilder.FindSpace(zones, block, row, size.Width, size.Length, out center, out angle)) continue;
                    if (Mathf.Abs(center.y - 140) > .2f || Mathf.Abs(center.x) > 4789f || Mathf.Abs(center.z) > 4789f) continue;
                    output.WriteLine(String.Format(Invariant, "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7:F3}\t{8:F3}\t{9:F3}\t{10}",
                        road.Source, road.Id, road.BuildIndex, id, row, size.Width, size.Length, center.x, center.y, center.z, mask));
                    records++;
                    remaining &= ~mask;
                    if (remaining == 0) break;
                }
            }
            return records;
        }
    }
}
