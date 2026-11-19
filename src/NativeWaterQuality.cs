using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    internal static class NativeWaterQuality
    {
        private const int ProductionTicksPerWeek = 16;
        private const NativeBuilding.Flags Live = NativeBuilding.Flags.Created | NativeBuilding.Flags.Deleted;

        internal sealed class IntakeChange
        {
            private readonly ushort id;
            private readonly byte previousRate, nextRate;
            private readonly WaterFacilityAI ai;

            internal IntakeChange(Dictionary<string, object> request)
            {
                id = Convert.ToUInt16(request["buildingId"]);
                previousRate = Convert.ToByte(request["expectedRate"]);
                nextRate = Convert.ToByte(request["rate"]);
                NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
                if (id == 0 || id >= buildings.Length || (previousRate != 0 && previousRate != 100) || nextRate != 100 - previousRate)
                    throw new InvalidDataException("Water trial requires one valid intake and a 0/100 production toggle");
                NativeBuilding building = buildings[id];
                ai = building.Info == null ? null : building.Info.m_buildingAI as WaterFacilityAI;
                if ((building.m_flags & Live) != NativeBuilding.Flags.Created || ai == null || !ai.m_useGroundWater ||
                    ai.m_waterIntake <= 0 || ai.m_waterOutlet != 0 || ai.m_sewageOutlet != 0 || building.m_netNode == 0 ||
                    building.m_productionRate != previousRate || building.m_buildIndex != Convert.ToUInt32(request["expectedGeneration"]))
                    throw new InvalidOperationException("The expected groundwater intake was not found in the same state");
                if (nextRate == 0)
                {
                    if (building.m_waterPollution == 0 || GroundPollution(ai, building) == 0 || (building.m_flags & NativeBuilding.Flags.Active) == 0)
                        throw new InvalidOperationException("The intake is not delivering polluted groundwater");
                    District citywide = DistrictManager.instance.m_districts.m_buffer[0];
                    int remaining = citywide.GetWaterCapacity() - ai.GetWaterRate(id, ref building) * ProductionTicksPerWeek;
                    int required = citywide.GetWaterConsumption();
                    if (remaining <= 0 || required <= 0 || remaining * 10L < required * 11L)
                        throw new InvalidOperationException("Remaining intake capacity is below 110% of current consumption");
                }
            }

            internal void Apply()
            {
                SetRate(nextRate);
            }

            internal void Rollback()
            {
                if (BuildingManager.instance.m_buildings.m_buffer[id].m_productionRate != previousRate)
                    SetRate(previousRate);
            }

            private void SetRate(byte rate)
            {
                NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
                ai.SetProductionRate(id, ref buildings[id], rate);
                if (buildings[id].m_productionRate != rate)
                    throw new InvalidOperationException("The native water facility did not retain its production rate");
            }
        }

        internal static Dictionary<string, object> Capture()
        {
            District citywide = DistrictManager.instance.m_districts.m_buffer[0];
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            var facilities = new List<object>();
            var sampledResidences = new List<object>();
            int waterWarnings = 0, sewageWarnings = 0, dirtyWarnings = 0;
            int pollutedConnections = 0, activeResidences = 0, waterConnections = 0, sewerConnections = 0;
            for (int index = 1; index < buildings.Length; index++)
            {
                NativeBuilding building = buildings[index];
                if ((building.m_flags & Live) != NativeBuilding.Flags.Created || building.Info == null) continue;
                bool active = (building.m_flags & NativeBuilding.Flags.Active) != 0;
                WaterFacilityAI ai = building.Info.m_buildingAI as WaterFacilityAI;
                if (ai != null)
                {
                    facilities.Add(new Dictionary<string, object> {
                        {"id",index}, {"generation",building.m_buildIndex}, {"prefab",building.Info.name}, {"active",active},
                        {"productionRate",building.m_productionRate}, {"waterNode",building.m_netNode},
                        {"groundwater",ai.m_useGroundWater}, {"nominalWaterIntake",ai.m_waterIntake}, {"nominalSewageOutlet",ai.m_sewageOutlet},
                        {"netWaterRate",ai.GetWaterRate((ushort)index, ref building)}, {"groundPollution",GroundPollution(ai, building)},
                        {"waterPollution",building.m_waterPollution}, {"position",new float[] {building.m_position.x, building.m_position.y, building.m_position.z}}
                    });
                }
                if (building.Info.GetService() != ItemClass.Service.Residential || !active) continue;
                activeResidences++;
                bool water, sewage;
                byte pollution;
                WaterManager.instance.CheckWater(building.m_position, out water, out sewage, out pollution);
                if (water) waterConnections++;
                if (sewage) sewerConnections++;
                if (water && pollution != 0) pollutedConnections++;
                bool dirty = !(building.m_problems & Notification.Problem1.DirtyWater).IsNone;
                bool waterWarning = !(building.m_problems & (Notification.Problem1.Water | Notification.Problem1.WaterNotConnected)).IsNone;
                bool sewageWarning = !(building.m_problems & Notification.Problem1.Sewage).IsNone;
                if (dirty) dirtyWarnings++;
                if (waterWarning) waterWarnings++;
                if (sewageWarning) sewageWarnings++;
                sampledResidences.Add(new Dictionary<string, object> {
                    {"id",index}, {"generation",building.m_buildIndex}, {"dirtyWarning",dirty},
                    {"waterWarning",waterWarning}, {"sewageWarning",sewageWarning},
                    {"water",water}, {"sewage",sewage}, {"networkPollution",pollution}
                });
            }
            return new Dictionary<string, object> {
                {"capacity",citywide.GetWaterCapacity()}, {"consumption",citywide.GetWaterConsumption()},
                {"sewageCapacity",citywide.GetSewageCapacity()}, {"sewageLoad",citywide.GetSewageAccumulation()},
                {"districtPollution",citywide.GetWaterPollution()}, {"sickResidents",citywide.GetSickCount()},
                {"activeResidences",activeResidences}, {"waterConnections",waterConnections}, {"sewerConnections",sewerConnections},
                {"pollutedConnections",pollutedConnections}, {"dirtyWaterWarnings",dirtyWarnings},
                {"waterWarnings",waterWarnings}, {"sewageWarnings",sewageWarnings},
                {"facilities",facilities}, {"residences",sampledResidences}
            };
        }

        private static byte GroundPollution(WaterFacilityAI ai, NativeBuilding building)
        {
            byte result;
            NaturalResourceManager.instance.CheckPollution(building.CalculatePosition(ai.m_waterLocationOffset), out result);
            return result;
        }
    }
}
