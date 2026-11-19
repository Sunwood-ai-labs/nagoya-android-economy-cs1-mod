using System;
using System.Collections.Generic;
using System.IO;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeWasteLocalTrial
    {
        private readonly List<Assignment> assignments = new List<Assignment>();
        private const int MaximumAssignments = 6;
        private const float MaximumDistance = 500;

        private struct Assignment
        {
            public ushort Facility, Target;
            public LandfillSiteAI FacilityAI;
        }

        internal NativeWasteLocalTrial(List<object> requested, NativeWasteObservation observation)
        {
            if (requested.Count < 1 || requested.Count > MaximumAssignments) throw new InvalidDataException("Select one to six local waste assignments");
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            var targets = new HashSet<ushort>();
            var reserved = new Dictionary<ushort, int>();
            var fleet = new Dictionary<ushort, int>();
            Vehicle[] vehicles = VehicleManager.instance.m_vehicles.m_buffer;
            for (int i = 1; i < vehicles.Length; i++)
            {
                Vehicle truck = vehicles[i];
                if ((truck.m_flags & (Vehicle.Flags.Created | Vehicle.Flags.Deleted)) != Vehicle.Flags.Created ||
                    truck.m_sourceBuilding == 0 || truck.Info == null || !(truck.Info.m_vehicleAI is GarbageTruckAI)) continue;
                int count;
                fleet.TryGetValue(truck.m_sourceBuilding, out count);
                fleet[truck.m_sourceBuilding] = count + 1;
                if (truck.m_targetBuilding != 0) targets.Add(truck.m_targetBuilding);
            }
            foreach (object item in requested)
            {
                var row = (Dictionary<string, object>)item;
                ushort source = Convert.ToUInt16(row["facility"]);
                ushort target = Convert.ToUInt16(row["target"]);
                ushort road = Convert.ToUInt16(row["sourceRoad"]);
                uint sourceGeneration = Convert.ToUInt32(row["facilityGeneration"]);
                uint targetGeneration = Convert.ToUInt32(row["targetGeneration"]);
                if (source == 0 || target == 0 || source == target || source >= buildings.Length || target >= buildings.Length ||
                    road == 0 || !observation.IsWatching(target, targetGeneration) || !targets.Add(target))
                    throw new InvalidDataException("Local waste assignment identity changed or the target already has a truck");
                NativeBuilding from = buildings[source], to = buildings[target];
                LandfillSiteAI ai = from.Info == null ? null : from.Info.m_buildingAI as LandfillSiteAI;
                if (!IsCompleted(from) || from.m_buildIndex != sourceGeneration || from.m_accessSegment != road ||
                    from.m_productionRate == 0 || ai == null || ai.m_garbageTruckCount == 0)
                    throw new InvalidDataException("Local waste source facility differs from the snapshot");
                if (!IsCompleted(to) || to.m_buildIndex != targetGeneration ||
                    (to.m_flags & NativeBuilding.Flags.Active) == 0 || to.m_garbageBuffer == 0 ||
                    (to.m_problems & Notification.Problem1.Garbage).IsNone ||
                    (to.m_position - from.m_position).sqrMagnitude > MaximumDistance * MaximumDistance)
                    throw new InvalidDataException("Local waste destination is inactive, too far away, or no longer needs collection");
                int available, held;
                fleet.TryGetValue(source, out available);
                reserved.TryGetValue(source, out held);
                if (held >= 2 || available + held >= ai.m_garbageTruckCount)
                    throw new InvalidDataException("Local waste assignment exceeds the facility's trial fleet");
                reserved[source] = held + 1;
                assignments.Add(new Assignment {Facility = source, Target = target, FacilityAI = ai});
            }
        }

        internal void Dispatch()
        {
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            foreach (Assignment assignment in assignments)
            {
                var offer = new TransferManager.TransferOffer {
                    Building = assignment.Target, Position = buildings[assignment.Target].m_position,
                    Amount = 1, Active = false
                };
                assignment.FacilityAI.StartTransfer(assignment.Facility, ref buildings[assignment.Facility],
                    TransferManager.TransferReason.Garbage, offer);
            }
        }

        private static bool IsCompleted(NativeBuilding building)
        {
            const NativeBuilding.Flags present = NativeBuilding.Flags.Created | NativeBuilding.Flags.Completed;
            const NativeBuilding.Flags invalid = NativeBuilding.Flags.Deleted | NativeBuilding.Flags.Abandoned | NativeBuilding.Flags.Collapsed;
            return (building.m_flags & (present | invalid)) == present;
        }
    }
}
