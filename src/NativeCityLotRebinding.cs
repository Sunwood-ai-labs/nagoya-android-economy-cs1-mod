using System.Collections.Generic;
using UnityEngine;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    internal static class NativeCityLotRebinding
    {
        private const float PositionTolerance = .15f;
        private const NativeBuilding.Flags Unusable = NativeBuilding.Flags.Deleted | NativeBuilding.Flags.Abandoned |
            NativeBuilding.Flags.Collapsed | NativeBuilding.Flags.BurnedDown;

        private struct Match
        {
            internal NativeCityLedger.Lot Lot;
            internal ushort Building;
        }

        internal static List<Dictionary<string, object>> Apply(NativeCityLedger.Lot[] lots, HashSet<ushort> activeRoads)
        {
            var activeGroups = new HashSet<string>();
            var reservedIds = new HashSet<ushort>();
            var missing = new List<NativeCityLedger.Lot>();
            foreach (NativeCityLedger.Lot lot in lots)
            {
                reservedIds.Add(lot.Id);
                if (!activeRoads.Contains(lot.Road)) continue;
                if (NativeCityLedger.HasActiveZone(lot)) activeGroups.Add(lot.Group);
                else missing.Add(lot);
            }

            var changes = new List<Dictionary<string, object>>();
            if (missing.Count == 0) return changes;
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            var byPosition = MapBuildings(buildings);
            var matches = new List<Match>();
            var seenGroups = new HashSet<string>();
            var seenBuildings = new HashSet<ushort>();
            var ambiguousGroups = new HashSet<string>();
            var ambiguousBuildings = new HashSet<ushort>();
            foreach (NativeCityLedger.Lot lot in missing)
            {
                ZoneBlock block;
                if (activeGroups.Contains(lot.Group) || !TryGetFrontage(lot, out block)) continue;
                ushort id = FindReplacement(lot, block, buildings, byPosition, reservedIds);
                if (id == 0) continue;
                if (!seenGroups.Add(lot.Group)) ambiguousGroups.Add(lot.Group);
                if (!seenBuildings.Add(id)) ambiguousBuildings.Add(id);
                matches.Add(new Match { Lot = lot, Building = id });
            }

            foreach (Match match in matches)
            {
                NativeCityLedger.Lot lot = match.Lot;
                uint generation = buildings[match.Building].m_buildIndex;
                if (ambiguousGroups.Contains(lot.Group) || ambiguousBuildings.Contains(match.Building) ||
                    !NativeCityLedger.TryRebind(lot, match.Building, generation)) continue;
                changes.Add(new Dictionary<string, object> {
                    {"group", lot.Group}, {"roadId", lot.Road}, {"zoneBlock", lot.ZoneBlock},
                    {"previousBuildingId", lot.Id}, {"previousBuildIndex", lot.BuildIndex},
                    {"buildingId", match.Building}, {"buildIndex", generation}
                });
            }
            return changes;
        }

        private static bool TryGetFrontage(NativeCityLedger.Lot lot, out ZoneBlock block)
        {
            if (!NativeCityLedger.TryGetZonedBlock(lot, out block)) return false;
            NetSegment segment = NetManager.instance.m_segments.m_buffer[lot.Road];
            if (lot.ZoneBlock != segment.m_blockStartLeft && lot.ZoneBlock != segment.m_blockStartRight &&
                lot.ZoneBlock != segment.m_blockEndLeft && lot.ZoneBlock != segment.m_blockEndRight) return false;
            for (int y = lot.Row; y < lot.Row + lot.Width; y++)
                for (int x = 0; x < lot.Length; x++)
                {
                    ulong flag = 1UL << (y * 8 + x);
                    if ((block.m_valid & flag) == 0 || (block.m_shared & flag) != 0) return false;
                }
            return true;
        }

        private static Dictionary<long, List<ushort>> MapBuildings(NativeBuilding[] buildings)
        {
            var result = new Dictionary<long, List<ushort>>();
            for (int i = 1; i < buildings.Length; i++)
            {
                NativeBuilding building = buildings[i];
                if ((building.m_flags & (NativeBuilding.Flags.Created | Unusable)) != NativeBuilding.Flags.Created) continue;
                BuildingInfo info = building.Info;
                if (info == null || info.m_zoningMode != BuildingInfo.ZoningMode.Straight) continue;
                Vector3 position = building.m_position;
                long key = Key(Mathf.FloorToInt(position.x), Mathf.FloorToInt(position.z));
                List<ushort> ids;
                if (!result.TryGetValue(key, out ids)) result.Add(key, ids = new List<ushort>());
                ids.Add((ushort)i);
            }
            return result;
        }

        private static ushort FindReplacement(NativeCityLedger.Lot lot, ZoneBlock block, NativeBuilding[] buildings,
            Dictionary<long, List<ushort>> byPosition, HashSet<ushort> reservedIds)
        {
            Vector3 center = NativeCityFrontages.Center(block, lot.Row, lot.Width, lot.Length);
            float angle = NativeCityFrontages.StraightAngle(block);
            ItemClass.SubService use = NativeCityZoneBuilder.ZoneService(lot.Zone);
            ushort match = 0;
            for (int x = Mathf.FloorToInt(center.x - PositionTolerance); x <= Mathf.FloorToInt(center.x + PositionTolerance); x++)
                for (int z = Mathf.FloorToInt(center.z - PositionTolerance); z <= Mathf.FloorToInt(center.z + PositionTolerance); z++)
                {
                    List<ushort> ids;
                    if (!byPosition.TryGetValue(Key(x, z), out ids)) continue;
                    foreach (ushort id in ids)
                    {
                        NativeBuilding building = buildings[id];
                        BuildingInfo info = building.Info;
                        Vector3 offset = building.m_position - center;
                        if (building.m_buildIndex <= lot.BuildIndex || (id != lot.Id && reservedIds.Contains(id)) ||
                            info.m_class.m_subService != use || info.GetWidth() != lot.Width || info.GetLength() != lot.Length ||
                            offset.x * offset.x + offset.z * offset.z > PositionTolerance * PositionTolerance)
                            continue;
                        float turn = building.m_angle - angle;
                        if (Mathf.Abs(Mathf.Sin(turn)) > .01f &&
                            (lot.Width != lot.Length || Mathf.Abs(Mathf.Cos(turn)) > .01f)) continue;
                        if (match != 0) return 0;
                        match = id;
                    }
                }
            return match;
        }

        private static long Key(int x, int z) { return ((long)x << 32) | (uint)z; }
    }
}
