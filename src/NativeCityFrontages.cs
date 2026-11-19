using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeCityFrontages
    {
        private const float MaximumTerrainAdjustment = .25f;
        private readonly HashSet<string> wards;
        private readonly int maximumPerWard;
        private readonly bool apply;
        private readonly uint buildIndex;
        private readonly int segments;
        private readonly MethodInfo findRoad = typeof(CommonBuildingAI).GetMethod("FindRoadAccess",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new Type[] { typeof(Vector3), typeof(ushort).MakeByRefType() }, null);

        internal NativeCityFrontages(string[] targetWards, int limit, bool applyRepairs, uint expectedBuildIndex, int expectedSegments)
        {
            wards = new HashSet<string>(targetWards, StringComparer.Ordinal);
            maximumPerWard = limit;
            apply = applyRepairs;
            buildIndex = expectedBuildIndex;
            segments = expectedSegments;
            if (wards.Count == 0 || wards.Count > 16 || wards.Contains("") || limit < 1 || limit > 50 ||
                buildIndex == 0 || segments <= 0 || findRoad == null)
                throw new ArgumentException("Invalid native frontage request");
        }

        internal static float StraightAngle(ZoneBlock block)
        {
            return block.m_angle + Mathf.PI * .5f;
        }

        internal static Vector3 Center(ZoneBlock block, int row, int width, int length)
        {
            Vector3 across = new Vector3(Mathf.Cos(block.m_angle), 0, Mathf.Sin(block.m_angle)) * 8;
            Vector3 along = new Vector3(across.z, 0, -across.x);
            return block.m_position + across * ((length - 1) * .5f - 3.5f) + along * (row + (width - 1) * .5f - 3.5f);
        }

        internal Dictionary<string, object> Run()
        {
            SimulationManager sim = SimulationManager.instance;
            NetManager net = NetManager.instance;
            if (!sim.SimulationPaused || sim.m_currentBuildIndex != buildIndex || net.m_segmentCount != segments)
                throw new InvalidOperationException("The paused frontage checkpoint changed");

            NativeCityLedger.Road[] roads;
            NativeCityLedger.Lot[] lots;
            NativeCityLedger.Snapshot(out roads, out lots);
            var usableRoads = new HashSet<ushort>();
            foreach (NativeCityLedger.Road road in roads)
                if (NativeCityLedger.HasActiveRoad(road)) usableRoads.Add(road.Id);

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            var records = new List<object>();
            var networkHits = new ulong[(net.m_segments.m_buffer.Length + 63) / 64];
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            var buildingHits = new ulong[(buildings.Length + 63) / 64];
            // Shops and factories determine whether a delivery can use both ends of a route.
            Array.Sort(lots, delegate(NativeCityLedger.Lot a, NativeCityLedger.Lot b) {
                int priority = RolePriority(a, buildings).CompareTo(RolePriority(b, buildings));
                return priority != 0 ? priority : a.Id.CompareTo(b.Id);
            });
            foreach (NativeCityLedger.Lot lot in lots)
            {
                if (!wards.Contains(lot.Ward) || !NativeCityLedger.HasActiveZone(lot) || !usableRoads.Contains(lot.Road))
                    continue;
                Dictionary<string, object> record;
                int used;
                counts.TryGetValue(lot.Ward, out used);
                string status = Inspect(lot, used < maximumPerWard, networkHits, buildingHits, out record);
                int total;
                totals.TryGetValue(status, out total);
                totals[status] = total + 1;
                if (record != null) records.Add(record);
                if (status == "repaired" || status == "eligible") counts[lot.Ward] = used + 1;
                if (status == "identity changed") break;
            }
            return new Dictionary<string, object> {
                {"gameTime",sim.m_currentGameTime.ToString("s")}, {"buildIndex",sim.m_currentBuildIndex},
                {"networkSegments",net.m_segmentCount}, {"applied",apply}, {"complete",!totals.ContainsKey("identity changed")},
                {"counts",totals}, {"selectedByWard",counts}, {"sites",records}
            };
        }

        private static int RolePriority(NativeCityLedger.Lot lot, NativeBuilding[] buildings)
        {
            if (lot.Zone == ItemClass.Zone.Industrial) return 1;
            if ((lot.Zone == ItemClass.Zone.CommercialLow || lot.Zone == ItemClass.Zone.CommercialHigh) &&
                lot.Id < buildings.Length && buildings[lot.Id].Info != null)
            {
                CommercialBuildingAI shop = buildings[lot.Id].Info.m_buildingAI as CommercialBuildingAI;
                return shop != null && shop.m_incomingResource == TransferManager.TransferReason.Goods ? 0 : 3;
            }
            return 2;
        }

        private string Inspect(NativeCityLedger.Lot lot, bool withinLimit, ulong[] networkHits, ulong[] buildingHits,
            out Dictionary<string, object> record)
        {
            record = null;
            BuildingManager manager = BuildingManager.instance;
            NativeBuilding old = manager.m_buildings.m_buffer[lot.Id];
            BuildingInfo info = old.Info;
            if (info == null || old.m_parentBuilding != 0 || old.m_subBuilding != 0 ||
                old.Width != lot.Width || old.Length != lot.Length || info.m_zoningMode != BuildingInfo.ZoningMode.Straight ||
                (info.m_subBuildings != null && info.m_subBuildings.Length != 0) ||
                (old.m_flags & (NativeBuilding.Flags.Abandoned | NativeBuilding.Flags.Deleted | NativeBuilding.Flags.Collapsed)) != 0 ||
                !(info.m_buildingAI is PrivateBuildingAI))
                return "unsupported or changed footprint";

            ZoneBlock block = ZoneManager.instance.m_blocks.m_buffer[lot.ZoneBlock];
            Vector3 offset = Center(block, lot.Row, lot.Width, lot.Length) - old.m_position;
            if (offset.x * offset.x + offset.z * offset.z > .25f)
                return "position does not match zoning cells";
            if (Mathf.Abs(Mathf.DeltaAngle(old.m_angle * Mathf.Rad2Deg, block.m_angle * Mathf.Rad2Deg)) > .5f)
                return "already aligned or different angle";
            NativeBuilding proposed = old;
            proposed.m_angle = StraightAngle(block);
            ushort beforeRoad = ProbeRoad(info.m_buildingAI, old);
            ushort afterRoad = ProbeRoad(info.m_buildingAI, proposed);
            if (afterRoad != lot.Road) return "corrected entrance does not meet source road";
            if (beforeRoad == afterRoad) return "source road already accessible";
            if (!withinLimit) return "remaining candidate";
            float expectedHeight = TerrainHeight(proposed, info);
            if (Mathf.Abs(expectedHeight - old.m_position.y) > MaximumTerrainAdjustment)
                return "native terrain height differs";
            Array.Clear(networkHits, 0, networkHits.Length);
            Array.Clear(buildingHits, 0, buildingHits.Length);
            ToolBase.ToolErrors collision = BuildingTool.CheckSpace(info, info.m_placementMode, lot.Id,
                old.m_position, old.m_position.y, old.m_position.y + Mathf.Max(1, info.m_collisionHeight),
                proposed.m_angle, old.Width, old.Length, true, networkHits, buildingHits);
            record = new Dictionary<string, object> {
                {"buildingId",lot.Id}, {"group",lot.Group}, {"buildIndex",lot.BuildIndex}, {"ward",lot.Ward}, {"zone",lot.Zone.ToString()},
                {"prefab",info.name}, {"position",new float[] {old.m_position.x, old.m_position.y, old.m_position.z}},
                {"beforeAngleDegrees",old.m_angle * Mathf.Rad2Deg}, {"targetAngleDegrees",proposed.m_angle * Mathf.Rad2Deg},
                {"recordedRoad",lot.Road}, {"beforeRoadProbe",beforeRoad}, {"targetRoadProbe",afterRoad},
                {"beforeAccessSegment",old.m_accessSegment}, {"citizenUnitsBefore",old.m_citizenUnits},
                {"expectedTerrainHeight",expectedHeight}, {"placementCheck",collision.ToString()}
            };
            if (collision != ToolBase.ToolErrors.None)
            {
                record["status"] = "placement blocked";
                return "placement blocked";
            }
            if (!apply)
            {
                record["status"] = "eligible";
                return "eligible";
            }

            manager.RelocateBuilding(lot.Id, old.m_position, proposed.m_angle);
            NativeBuilding actual = manager.m_buildings.m_buffer[lot.Id];
            Vector3 shift = actual.m_position - old.m_position;
            bool same = actual.m_buildIndex == lot.BuildIndex && actual.m_citizenUnits == old.m_citizenUnits &&
                actual.Info == old.Info && shift.x * shift.x + shift.z * shift.z <= .0001f &&
                Mathf.Abs(shift.y) <= MaximumTerrainAdjustment && NativeCityLedger.HasActiveZone(lot);
            record["sameIdentityAndUnits"] = same;
            record["buildIndexAfter"] = actual.m_buildIndex;
            record["terrainHeightAdjustment"] = shift.y;
            record["afterAngleDegrees"] = actual.m_angle * Mathf.Rad2Deg;
            record["citizenUnitsAfter"] = actual.m_citizenUnits;
            if (!same)
            {
                record["status"] = "identity changed";
                return "identity changed";
            }
            ((PrivateBuildingAI)info.m_buildingAI).CheckRoadAccess(lot.Id, ref manager.m_buildings.m_buffer[lot.Id]);
            ushort access = manager.m_buildings.m_buffer[lot.Id].m_accessSegment;
            record["afterAccessSegment"] = access;
            if (access != lot.Road)
            {
                manager.RelocateBuilding(lot.Id, old.m_position, old.m_angle);
                ((PrivateBuildingAI)info.m_buildingAI).CheckRoadAccess(lot.Id, ref manager.m_buildings.m_buffer[lot.Id]);
                record["status"] = "access verification failed and rotation reverted";
                return "access verification failed and rotation reverted";
            }
            record["status"] = "repaired";
            return "repaired";
        }

        private ushort ProbeRoad(BuildingAI ai, NativeBuilding building)
        {
            object[] parameters = {building.CalculateSidewalkPosition(0, 4), (ushort)0};
            return (bool)findRoad.Invoke(ai, parameters) ? (ushort)parameters[1] : (ushort)0;
        }

        private static float TerrainHeight(NativeBuilding building, BuildingInfo info)
        {
            if ((building.m_flags & NativeBuilding.Flags.FixedHeight) != 0) return building.m_position.y;
            float minimum, maximum, height, water = -1;
            Vector3 mesh = NativeBuilding.CalculateMeshPosition(info, building.m_position, building.m_angle, building.Length);
            NativeBuilding.SampleBuildingHeight(mesh, building.m_angle, building.Width, building.Length, info,
                out minimum, out maximum, out height, ref water);
            return height;
        }
    }
}
