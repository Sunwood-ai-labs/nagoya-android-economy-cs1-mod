using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeWasteObservation
    {
        private const string PatchId = "sunwood.nagoya.native-waste-observation";
        private const int MaximumDispatchSamples = 128;
        private static volatile NativeWasteObservation current;
        private readonly Dictionary<ushort, uint> buildings = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, object> pickups = new Dictionary<ushort, object>();
        private readonly Dictionary<ushort, int> collectionRequests = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, int> facilityOffers = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, int> facilityAssignments = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, int> truckStarts = new Dictionary<ushort, int>();
        private readonly List<object> dispatchSamples = new List<object>();
        private readonly Dictionary<ushort, Dictionary<string, object>> activeDispatches =
            new Dictionary<ushort, Dictionary<string, object>>();
        private bool assigningLocalTrial;
        private Harmony harmony;

        public NativeWasteObservation(List<object> ids)
        {
            if (ids.Count == 0 || ids.Count > 64) throw new InvalidDataException("Select between 1 and 64 waste collection targets");
            NativeBuilding[] buffer = BuildingManager.instance.m_buildings.m_buffer;
            foreach (object value in ids)
            {
                ushort id = Convert.ToUInt16(value);
                if (id == 0 || id >= buffer.Length ||
                    (buffer[id].m_flags & (NativeBuilding.Flags.Created | NativeBuilding.Flags.Deleted)) != NativeBuilding.Flags.Created)
                    throw new InvalidDataException("Invalid waste collection building");
                buildings.Add(id, buffer[id].m_buildIndex);
            }
            Type[] arguments = {typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(Vehicle.Frame).MakeByRefType(),
                typeof(ushort), typeof(NativeBuilding).MakeByRefType()};
            Type[] offerArguments = {typeof(TransferManager.TransferReason), typeof(TransferManager.TransferOffer)};
            Type[] facilityArguments = {typeof(ushort), typeof(NativeBuilding).MakeByRefType(), offerArguments[0], offerArguments[1]};
            Type[] truckArguments = {typeof(ushort), typeof(Vehicle).MakeByRefType(), offerArguments[0], offerArguments[1]};
            Type[] vehicleArguments = {typeof(ushort), typeof(Vehicle).MakeByRefType()};
            Type[] stepArguments = {vehicleArguments[0], vehicleArguments[1], typeof(UnityEngine.Vector3)};
            harmony = new Harmony(PatchId);
            try
            {
                Install(typeof(GarbageTruckAI), "TryCollectGarbage", arguments, "BeforePickup", "AfterPickup");
                Install(typeof(TransferManager), "AddIncomingOffer", offerArguments, null, "ObserveFacilityOffer");
                Install(typeof(TransferManager), "AddOutgoingOffer", offerArguments, null, "ObserveCollectionRequest");
                Install(typeof(LandfillSiteAI), "StartTransfer", facilityArguments, null, "ObserveAssignment");
                Install(typeof(GarbageTruckAI), "StartTransfer", truckArguments, null, "ObserveTruckStart");
                Install(typeof(CarAI), "SimulationStep", stepArguments, "ObservePathResult", null);
                Install(typeof(CarAI), "TrySpawn", vehicleArguments, null, "ObserveSpawn");
                Install(typeof(GarbageTruckAI), "ArriveAtSource", vehicleArguments, "ObserveReturn", null);
                Install(typeof(GarbageTruckAI), "ReleaseVehicle", vehicleArguments, "ObserveRelease", null);
                current = this;
            }
            catch
            {
                harmony.UnpatchAll(PatchId);
                throw;
            }
        }

        private void Install(Type type, string name, Type[] arguments, string prefix, string postfix)
        {
            const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags callback = BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo target = type.GetMethod(name, instance, null, arguments, null);
            if (target == null || target.DeclaringType != type) throw new MissingMethodException(type.Name, name);
            Type observer = typeof(NativeWasteObservation);
            harmony.Patch(target, prefix == null ? null : new HarmonyMethod(observer.GetMethod(prefix, callback)),
                postfix == null ? null : new HarmonyMethod(observer.GetMethod(postfix, callback)), null, null);
        }

        private static void ObserveFacilityOffer(TransferManager.TransferReason __0, TransferManager.TransferOffer __1)
        {
            NativeWasteObservation watch = current;
            if (watch == null || __0 != TransferManager.TransferReason.Garbage || !__1.Active || __1.Amount <= 0) return;
            ushort id = __1.Building;
            NativeBuilding[] buffer = BuildingManager.instance.m_buildings.m_buffer;
            if (id == 0 || id >= buffer.Length || buffer[id].Info == null ||
                !(buffer[id].Info.m_buildingAI is LandfillSiteAI)) return;
            Count(watch.facilityOffers, id);
        }

        private static void ObserveCollectionRequest(TransferManager.TransferReason __0, TransferManager.TransferOffer __1)
        {
            NativeWasteObservation watch = current;
            if (watch == null || __0 != TransferManager.TransferReason.Garbage || __1.Amount <= 0) return;
            ushort id = __1.Building;
            uint generation;
            if (watch.buildings.TryGetValue(id, out generation) &&
                BuildingManager.instance.m_buildings.m_buffer[id].m_buildIndex == generation)
                Count(watch.collectionRequests, id);
        }

        private static void ObserveAssignment(ushort __0, TransferManager.TransferReason __2)
        {
            NativeWasteObservation watch = current;
            if (watch != null && __2 == TransferManager.TransferReason.Garbage) Count(watch.facilityAssignments, __0);
        }

        private static void ObserveTruckStart(ushort __0, ref Vehicle __1, TransferManager.TransferReason __2,
            TransferManager.TransferOffer __3)
        {
            NativeWasteObservation watch = current;
            if (watch == null || __2 != TransferManager.TransferReason.Garbage || __1.m_sourceBuilding == 0) return;
            ushort source = __1.m_sourceBuilding;
            Count(watch.truckStarts, source);
            watch.activeDispatches.Remove(__0);
            if (watch.dispatchSamples.Count >= MaximumDispatchSamples) return;
            var trace = new Dictionary<string, object> {
                {"gameTime",SimulationManager.instance.m_currentGameTime.ToString("s")},
                {"vehicle",__0}, {"facility",source}, {"offeredTarget",__3.Building}, {"target",__1.m_targetBuilding},
                {"localTrial",watch.assigningLocalTrial},
                {"path",__1.m_path}, {"waitingForPath",(__1.m_flags & Vehicle.Flags.WaitingPath) != 0},
                {"outboundPathResult",null}, {"firstSpawn",null}, {"firstPickup",null}, {"returned",null}, {"released",null}
            };
            PathUnit[] paths = PathManager.instance.m_pathUnits.m_buffer;
            if (__1.m_path != 0 && __1.m_path < paths.Length)
            {
                PathUnit path = paths[__1.m_path];
                var segments = new ushort[4];
                var lanes = new byte[4];
                for (int i = 0; i < segments.Length; i++)
                {
                    PathUnit.Position position = path.GetPosition(i);
                    segments[i] = position.m_segment;
                    lanes[i] = position.m_lane;
                }
                trace.Add("pathRequestSegments", segments);
                trace.Add("pathRequestLanes", lanes);
            }
            watch.dispatchSamples.Add(trace);
            watch.activeDispatches[__0] = trace;
        }

        private static Dictionary<string, object> FindDispatch(ushort id, ushort facility)
        {
            NativeWasteObservation watch = current;
            Dictionary<string, object> trace;
            if (watch != null && watch.activeDispatches.TryGetValue(id, out trace) && (ushort)trace["facility"] == facility)
                return trace;
            return null;
        }

        private static void ObservePathResult(ushort __0, ref Vehicle __1)
        {
            if ((__1.m_flags & Vehicle.Flags.WaitingPath) == 0) return;
            Dictionary<string, object> trace = FindDispatch(__0, __1.m_sourceBuilding);
            if (trace == null || trace["outboundPathResult"] != null || (uint)trace["path"] != __1.m_path || __1.m_path == 0) return;
            PathUnit path = PathManager.instance.m_pathUnits.m_buffer[__1.m_path];
            string result = (path.m_pathFindFlags & PathUnit.FLAG_READY) != 0 ? "ready" :
                (path.m_pathFindFlags & PathUnit.FLAG_FAILED) != 0 ? "failed" : null;
            if (result == null) return;
            bool ready = result == "ready";
            PathUnit.Position first = ready ? path.GetPosition(0) : default(PathUnit.Position);
            trace["outboundPathResult"] = new Dictionary<string, object> {
                {"result",result}, {"gameTime",SimulationManager.instance.m_currentGameTime.ToString("s")},
                {"flags",path.m_pathFindFlags}, {"target",__1.m_targetBuilding}, {"lengthMeters",ready ? (object)path.m_length : null},
                {"firstSegment",ready ? (object)first.m_segment : null}, {"firstLane",ready ? (object)first.m_lane : null}
            };
        }

        private static void ObserveSpawn(ushort __0, ref Vehicle __1, bool __result)
        {
            if (!__result || (__1.m_flags & Vehicle.Flags.Spawned) == 0) return;
            Dictionary<string, object> trace = FindDispatch(__0, __1.m_sourceBuilding);
            if (trace != null && trace["firstSpawn"] == null) trace["firstSpawn"] = VehicleEvent(ref __1);
        }

        private static void ObserveReturn(ushort __0, ref Vehicle __1)
        {
            Dictionary<string, object> trace = FindDispatch(__0, __1.m_sourceBuilding);
            if (trace != null && trace["returned"] == null) trace["returned"] = VehicleEvent(ref __1);
        }

        private static void ObserveRelease(ushort __0, ref Vehicle __1)
        {
            Dictionary<string, object> trace = FindDispatch(__0, __1.m_sourceBuilding);
            if (trace == null) return;
            trace["released"] = VehicleEvent(ref __1);
            current.activeDispatches.Remove(__0);
        }

        private static Dictionary<string, object> VehicleEvent(ref Vehicle vehicle)
        {
            return new Dictionary<string, object> {
                {"gameTime",SimulationManager.instance.m_currentGameTime.ToString("s")},
                {"target",vehicle.m_targetBuilding}, {"path",vehicle.m_path}, {"load",vehicle.m_transferSize},
                {"spawned",(vehicle.m_flags & Vehicle.Flags.Spawned) != 0}
            };
        }

        private static void Count(Dictionary<ushort, int> counts, ushort id)
        {
            int previous;
            counts.TryGetValue(id, out previous);
            counts[id] = previous + 1;
        }

        public Dictionary<string, object> DispatchSnapshot()
        {
            return new Dictionary<string, object> {
                {"collectionRequests",CountsByBuilding(collectionRequests)}, {"facilityOffers",CountsByBuilding(facilityOffers)},
                {"facilityAssignments",CountsByBuilding(facilityAssignments)}, {"truckStarts",CountsByBuilding(truckStarts)},
                {"samples",dispatchSamples}
            };
        }

        internal bool IsWatching(ushort id, uint generation)
        {
            uint recorded;
            return buildings.TryGetValue(id, out recorded) && recorded == generation;
        }

        internal void DispatchLocal(List<object> assignments)
        {
            var trial = new NativeWasteLocalTrial(assignments, this);
            assigningLocalTrial = true;
            try { trial.Dispatch(); }
            finally { assigningLocalTrial = false; }
        }

        private static Dictionary<string, object> CountsByBuilding(Dictionary<ushort, int> counts)
        {
            var snapshot = new Dictionary<string, object>();
            foreach (KeyValuePair<ushort, int> count in counts)
                snapshot.Add(count.Key.ToString(CultureInfo.InvariantCulture), count.Value);
            return snapshot;
        }

        private struct Pickup
        {
            public bool TrackedBuilding;
            public Dictionary<string, object> Dispatch;
            public uint BuildIndex;
            public ushort Garbage;
            public ushort Load;
        }

        private static void BeforePickup(ushort __0, ref Vehicle __1, ushort __3, ref NativeBuilding __4, out Pickup __state)
        {
            __state = new Pickup();
            NativeWasteObservation watch = current;
            if (watch == null) return;
            uint index;
            bool tracked = watch.buildings.TryGetValue(__3, out index) && index == __4.m_buildIndex && !watch.pickups.ContainsKey(__3);
            Dictionary<string, object> dispatch = FindDispatch(__0, __1.m_sourceBuilding);
            if (dispatch != null && dispatch["firstPickup"] != null) dispatch = null;
            if (!tracked && dispatch == null) return;
            __state = new Pickup {TrackedBuilding = tracked, Dispatch = dispatch, BuildIndex = __4.m_buildIndex,
                Garbage = __4.m_garbageBuffer, Load = __1.m_transferSize};
        }

        private static void AfterPickup(ushort __0, ref Vehicle __1, ushort __3, ref NativeBuilding __4, Pickup __state)
        {
            NativeWasteObservation watch = current;
            if (watch == null || (!__state.TrackedBuilding && __state.Dispatch == null) || __state.BuildIndex != __4.m_buildIndex ||
                __1.m_transferSize <= __state.Load || __4.m_garbageBuffer >= __state.Garbage) return;
            var pickup = new Dictionary<string, object> {
                {"gameTime",SimulationManager.instance.m_currentGameTime.ToString("s")},
                {"vehicle",__0}, {"sourceFacility",__1.m_sourceBuilding},
                {"garbageBefore",__state.Garbage}, {"garbageAfter",__4.m_garbageBuffer},
                {"truckLoadBefore",__state.Load}, {"truckLoadAfter",__1.m_transferSize}
            };
            if (__state.TrackedBuilding) watch.pickups[__3] = pickup;
            if (__state.Dispatch != null)
                __state.Dispatch["firstPickup"] = new Dictionary<string, object> {{"building",__3}, {"pickup",pickup}};
        }

        public List<object> Snapshot()
        {
            var rows = new List<object>();
            NativeBuilding[] buffer = BuildingManager.instance.m_buildings.m_buffer;
            foreach (KeyValuePair<ushort, uint> target in buildings)
            {
                NativeBuilding building = buffer[target.Key];
                bool matched = building.m_buildIndex == target.Value &&
                    (building.m_flags & (NativeBuilding.Flags.Created | NativeBuilding.Flags.Deleted)) == NativeBuilding.Flags.Created;
                object pickup;
                pickups.TryGetValue(target.Key, out pickup);
                rows.Add(new Dictionary<string, object> {
                    {"building",target.Key}, {"buildIndex",target.Value}, {"matched",matched},
                    {"garbage",matched ? (object)building.m_garbageBuffer : null},
                    {"warning",matched && !(building.m_problems & Notification.Problem1.Garbage).IsNone},
                    {"firstPickup",pickup}
                });
            }
            return rows;
        }

        internal static Dictionary<string, object> FleetSnapshot()
        {
            var facilities = new List<object>();
            var trucks = new List<object>();
            NativeBuilding[] buildingBuffer = BuildingManager.instance.m_buildings.m_buffer;
            for (int i = 1; i < buildingBuffer.Length; i++)
            {
                NativeBuilding building = buildingBuffer[i];
                if ((building.m_flags & (NativeBuilding.Flags.Created | NativeBuilding.Flags.Deleted)) != NativeBuilding.Flags.Created ||
                    building.Info == null) continue;
                LandfillSiteAI ai = building.Info.m_buildingAI as LandfillSiteAI;
                if (ai == null) continue;
                facilities.Add(new Dictionary<string, object> {
                    {"id",i}, {"generation",building.m_buildIndex}, {"prefab",building.Info.name},
                    {"active",(building.m_flags & NativeBuilding.Flags.Active) != 0}, {"productionRate",building.m_productionRate},
                    {"accessSegment",building.m_accessSegment}, {"buffer",building.m_garbageBuffer},
                    {"nominalTruckCount",ai.m_garbageTruckCount}, {"nominalProcessing",ai.m_garbageConsumption},
                    {"problems",building.m_problems.ToString()}
                });
            }

            Vehicle[] vehicleBuffer = VehicleManager.instance.m_vehicles.m_buffer;
            for (int i = 1; i < vehicleBuffer.Length; i++)
            {
                Vehicle truck = vehicleBuffer[i];
                if ((truck.m_flags & (Vehicle.Flags.Created | Vehicle.Flags.Deleted)) != Vehicle.Flags.Created ||
                    truck.Info == null || !(truck.Info.m_vehicleAI is GarbageTruckAI)) continue;
                trucks.Add(new Dictionary<string, object> {
                    {"id",i}, {"facility",truck.m_sourceBuilding}, {"target",truck.m_targetBuilding}, {"path",truck.m_path},
                    {"load",truck.m_transferSize}, {"spawned",(truck.m_flags & Vehicle.Flags.Spawned) != 0},
                    {"returning",(truck.m_flags & Vehicle.Flags.GoingBack) != 0},
                    {"waitingForPath",(truck.m_flags & Vehicle.Flags.WaitingPath) != 0}
                });
            }
            return new Dictionary<string, object> {{"facilities",facilities}, {"trucks",trucks}};
        }

        public void Stop()
        {
            if (current == this) current = null;
            harmony.UnpatchAll(PatchId);
        }
    }
}
