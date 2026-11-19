using System;
using System.Collections.Generic;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeWasteNearbyDispatch
    {
        private const int MaxRequests = 2048;
        private const int MaxFacilities = 64;
        private const int MaxDispatches = 12;
        private const float MaxDistance = 500;
        private readonly DateTime stopAt;
        private DateTime nextCheck;
        private readonly Dictionary<ushort, Request> requests = new Dictionary<ushort, Request>();
        private readonly Dictionary<ushort, Request> facilities = new Dictionary<ushort, Request>();
        private readonly Dictionary<ushort, DateTime> nextFacilityDispatch = new Dictionary<ushort, DateTime>();
        private readonly Dictionary<ushort, DateTime> nextTargetDispatch = new Dictionary<ushort, DateTime>();
        private readonly List<object> dispatches = new List<object>();

        private struct Request
        {
            public TransferManager.TransferOffer Offer;
            public DateTime Observed;
            public uint Generation;
        }

        internal NativeWasteNearbyDispatch(DateTime startsAt)
        {
            stopAt = startsAt.AddHours(96);
            nextCheck = startsAt.AddHours(2);
        }

        internal void Observe(TransferManager.TransferOffer offer, bool facility)
        {
            DateTime now = SimulationManager.instance.m_currentGameTime;
            if (now >= stopAt || dispatches.Count >= MaxDispatches || facility != offer.Active || offer.Amount <= 0) return;
            ushort id = offer.Building;
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            if (id == 0 || id >= buildings.Length) return;
            NativeBuilding building = buildings[id];
            if (!IsUsable(building) || (facility && !(building.Info.m_buildingAI is LandfillSiteAI))) return;
            if (!facility && (building.m_problems & Notification.Problem1.Garbage).IsNone) return;
            Dictionary<ushort, Request> queue = facility ? facilities : requests;
            int capacity = facility ? MaxFacilities : MaxRequests;
            if (queue.Count >= capacity && !queue.ContainsKey(id)) return;
            queue[id] = new Request {Offer = offer, Observed = now, Generation = building.m_buildIndex};
        }

        internal void Tick()
        {
            SimulationManager simulation = SimulationManager.instance;
            DateTime now = simulation.m_currentGameTime;
            if (simulation.SimulationPaused || now < nextCheck || now >= stopAt || dispatches.Count >= MaxDispatches) return;
            nextCheck = now.AddHours(2);
            Expire(facilities, now);
            Expire(requests, now);
            if (EconomyManager.instance.InternalCashAmount < 1000000L || facilities.Count == 0 || requests.Count == 0) return;

            var occupiedTargets = new HashSet<ushort>();
            var assignedFleet = new Dictionary<ushort, int>();
            Vehicle[] vehicles = VehicleManager.instance.m_vehicles.m_buffer;
            for (int i = 1; i < vehicles.Length; i++)
            {
                Vehicle truck = vehicles[i];
                if ((truck.m_flags & (Vehicle.Flags.Created | Vehicle.Flags.Deleted)) != Vehicle.Flags.Created ||
                    truck.Info == null || !(truck.Info.m_vehicleAI is GarbageTruckAI)) continue;
                int count;
                assignedFleet.TryGetValue(truck.m_sourceBuilding, out count);
                assignedFleet[truck.m_sourceBuilding] = count + 1;
                if (truck.m_targetBuilding != 0) occupiedTargets.Add(truck.m_targetBuilding);
            }

            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            var sources = new List<ushort>(facilities.Keys);
            sources.Sort();
            foreach (ushort sourceId in sources)
            {
                if (dispatches.Count >= MaxDispatches) break;
                Request source = facilities[sourceId];
                NativeBuilding building = buildings[sourceId];
                if (!IsUsable(building) || building.m_buildIndex != source.Generation || building.m_productionRate == 0 ||
                    building.m_accessSegment == 0 || !IsDue(nextFacilityDispatch, sourceId, now)) continue;
                LandfillSiteAI ai = building.Info.m_buildingAI as LandfillSiteAI;
                if (ai == null) continue;
                int budget = Math.Min(100, EconomyManager.instance.GetBudget(building.Info.m_class));
                int count;
                assignedFleet.TryGetValue(sourceId, out count);
                if (count >= ai.m_garbageTruckCount * budget * Math.Min(100, (int)building.m_productionRate) / 10000) continue;

                ushort target = ChooseTarget(buildings, sourceId, occupiedTargets, now);
                if (target == 0) continue;
                Request requested = requests[target];
                var offer = requested.Offer;
                offer.Position = buildings[target].m_position;
                offer.Amount = 1;
                ai.StartTransfer(sourceId, ref buildings[sourceId], TransferManager.TransferReason.Garbage, offer);
                occupiedTargets.Add(target);
                nextTargetDispatch[target] = now.AddHours(36);
                nextFacilityDispatch[sourceId] = now.AddHours(12);
                dispatches.Add(new Dictionary<string, object> {
                    {"facility",sourceId}, {"facilityGeneration",source.Generation}, {"facilityOfferTime",source.Observed.ToString("s")},
                    {"target",target}, {"targetGeneration",requested.Generation}, {"targetOfferTime",requested.Observed.ToString("s")},
                    {"gameTime",now.ToString("s")}, {"garbageAtAssignment",buildings[target].m_garbageBuffer},
                    {"distanceMeters",(double)(buildings[target].m_position - building.m_position).magnitude}
                });
            }
        }

        internal Dictionary<string, object> Snapshot()
        {
            return new Dictionary<string, object> {{"stopAt",stopAt.ToString("s")}, {"dispatches",dispatches}};
        }

        private ushort ChooseTarget(NativeBuilding[] buildings, ushort source, HashSet<ushort> occupied, DateTime now)
        {
            ushort selected = 0;
            float bestDistance = MaxDistance * MaxDistance;
            int bestPriority = -1;
            foreach (KeyValuePair<ushort, Request> entry in requests)
            {
                ushort id = entry.Key;
                if (id == source || occupied.Contains(id) || !IsDue(nextTargetDispatch, id, now)) continue;
                NativeBuilding building = buildings[id];
                if (!IsUsable(building) || building.m_buildIndex != entry.Value.Generation ||
                    (building.m_flags & NativeBuilding.Flags.Active) == 0 ||
                    building.m_garbageBuffer == 0 || (building.m_problems & Notification.Problem1.Garbage).IsNone) continue;
                float distance = (building.m_position - buildings[source].m_position).sqrMagnitude;
                int priority = entry.Value.Offer.Priority;
                if (distance > bestDistance ||
                    (distance == bestDistance && (priority < bestPriority || (priority == bestPriority && id > selected)))) continue;
                selected = id;
                bestDistance = distance;
                bestPriority = priority;
            }
            return selected;
        }

        private static void Expire(Dictionary<ushort, Request> queue, DateTime now)
        {
            var stale = new List<ushort>();
            foreach (KeyValuePair<ushort, Request> entry in queue)
                if (entry.Value.Observed.AddHours(12) < now) stale.Add(entry.Key);
            foreach (ushort id in stale) queue.Remove(id);
        }

        private static bool IsDue(Dictionary<ushort, DateTime> schedule, ushort id, DateTime now)
        {
            DateTime next;
            return !schedule.TryGetValue(id, out next) || now >= next;
        }

        private static bool IsUsable(NativeBuilding building)
        {
            const NativeBuilding.Flags present = NativeBuilding.Flags.Created | NativeBuilding.Flags.Completed;
            const NativeBuilding.Flags invalid = NativeBuilding.Flags.Deleted | NativeBuilding.Flags.Abandoned | NativeBuilding.Flags.Collapsed;
            return (building.m_flags & (present | invalid)) == present && building.Info != null;
        }
    }
}
