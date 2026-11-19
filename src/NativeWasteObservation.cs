using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeWasteObservation
    {
        private const string PatchId = "sunwood.nagoya.native-waste-observation";
        private static volatile NativeWasteObservation current;
        private readonly Dictionary<ushort, uint> buildings = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, object> pickups = new Dictionary<ushort, object>();
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

            const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
            const BindingFlags callback = BindingFlags.NonPublic | BindingFlags.Static;
            Type[] arguments = {typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(Vehicle.Frame).MakeByRefType(),
                typeof(ushort), typeof(NativeBuilding).MakeByRefType()};
            MethodInfo method = typeof(GarbageTruckAI).GetMethod("TryCollectGarbage", instance, null, arguments, null);
            if (method == null) throw new MissingMethodException("GarbageTruckAI", "TryCollectGarbage");
            var prefix = new HarmonyMethod(typeof(NativeWasteObservation).GetMethod("BeforePickup", callback));
            var postfix = new HarmonyMethod(typeof(NativeWasteObservation).GetMethod("AfterPickup", callback));
            harmony = new Harmony(PatchId);
            try
            {
                harmony.Patch(method, prefix, postfix, null, null);
                current = this;
            }
            catch
            {
                harmony.UnpatchAll(PatchId);
                throw;
            }
        }

        private struct Pickup
        {
            public bool Tracked;
            public uint BuildIndex;
            public ushort Garbage;
            public ushort Load;
        }

        private static void BeforePickup(ref Vehicle __1, ushort __3, ref NativeBuilding __4, out Pickup __state)
        {
            __state = new Pickup();
            NativeWasteObservation watch = current;
            uint index;
            if (watch == null || !watch.buildings.TryGetValue(__3, out index) || index != __4.m_buildIndex ||
                watch.pickups.ContainsKey(__3)) return;
            __state = new Pickup {Tracked = true, BuildIndex = index, Garbage = __4.m_garbageBuffer, Load = __1.m_transferSize};
        }

        private static void AfterPickup(ushort __0, ref Vehicle __1, ushort __3, ref NativeBuilding __4, Pickup __state)
        {
            NativeWasteObservation watch = current;
            if (watch == null || !__state.Tracked || __state.BuildIndex != __4.m_buildIndex ||
                __1.m_transferSize <= __state.Load || __4.m_garbageBuffer >= __state.Garbage) return;
            watch.pickups[__3] = new Dictionary<string, object> {
                {"gameTime",SimulationManager.instance.m_currentGameTime.ToString("s")},
                {"vehicle",__0}, {"sourceFacility",__1.m_sourceBuilding},
                {"garbageBefore",__state.Garbage}, {"garbageAfter",__4.m_garbageBuffer},
                {"truckLoadBefore",__state.Load}, {"truckLoadAfter",__1.m_transferSize}
            };
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

        public void Stop()
        {
            if (current == this) current = null;
            harmony.UnpatchAll(PatchId);
        }
    }
}
