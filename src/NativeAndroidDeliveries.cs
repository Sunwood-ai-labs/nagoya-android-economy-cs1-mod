using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeAndroidDeliveries : IDisposable
    {
        private const string PatchId = "sunwood.nagoya.android-goods-delivery";
        private const int ParcelSize = 100;
        private const uint TripFrameLimit = 262144;
        private static NativeAndroidDeliveries active;
        private Harmony harmony;
        private readonly Dictionary<uint, Trip> trips = new Dictionary<uint, Trip>();
        private readonly Dictionary<ushort, NativeCityLedger.Lot> sites = new Dictionary<ushort, NativeCityLedger.Lot>();
        private readonly Dictionary<string, List<ushort>> shops = new Dictionary<string, List<ushort>>(StringComparer.Ordinal);
        private readonly HashSet<ushort> reservedShops = new HashSet<ushort>();
        private readonly HashSet<string> busyWards = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<uint, DateTime> retryAfter = new Dictionary<uint, DateTime>();
        private readonly Queue<uint> readyAtFactory = new Queue<uint>();
        private readonly Queue<object> recent = new Queue<object>();
        private readonly Queue<object> failures = new Queue<object>();
        private int pickupStarts, pickupArrivals, started, delivered, goods, stockedFactories, shopsWithSpace, workersSeenAtFactory, failedLegs;
        internal int Delivered { get { return delivered; } }

        private struct Trip
        {
            public ushort Supplier, Target;
            public uint SupplierGeneration, TargetGeneration;
            public string Ward;
            public uint Frame;
            public bool Collecting;
        }

        private struct Arrival
        {
            public uint Citizen, Path;
            public ushort Source, Target;
        }

        internal NativeAndroidDeliveries()
        {
            MethodInfo target = typeof(HumanAI).GetMethod("ArriveAtDestination",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new Type[] {typeof(ushort), typeof(CitizenInstance).MakeByRefType(), typeof(bool)}, null);
            if (target == null) throw new MissingMethodException("HumanAI.ArriveAtDestination");
            MethodInfo postfix = typeof(NativeAndroidDeliveries).GetMethod("AfterArrival", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo prefix = typeof(NativeAndroidDeliveries).GetMethod("BeforeArrival", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo step = typeof(CitizenManager).GetMethod("SimulationStepImpl", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new Type[] {typeof(int)}, null);
            if (step == null) throw new MissingMethodException("CitizenManager.SimulationStepImpl");
            MethodInfo afterStep = typeof(NativeAndroidDeliveries).GetMethod("AfterCitizenStep", BindingFlags.Static | BindingFlags.NonPublic);
            harmony = new Harmony(PatchId);
            try
            {
                harmony.Patch(target, new HarmonyMethod(prefix), new HarmonyMethod(postfix), null, null);
                harmony.Patch(step, null, new HarmonyMethod(afterStep), null, null);
            }
            catch
            {
                harmony.UnpatchAll(PatchId);
                throw;
            }
            active = this;
        }

        internal void BeginSample()
        {
            uint frame = SimulationManager.instance.m_currentFrameIndex;
            var expired = new List<uint>();
            foreach (var trip in trips)
                if (unchecked(frame - trip.Value.Frame) >= TripFrameLimit) expired.Add(trip.Key);
            foreach (uint id in expired) EndTrip(id);
            sites.Clear();
            shops.Clear();
            stockedFactories = 0;
            shopsWithSpace = 0;
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            NativeCityLedger.Road[] roads;
            NativeCityLedger.Lot[] lots;
            NativeCityLedger.Snapshot(out roads, out lots);
            foreach (NativeCityLedger.Lot lot in lots)
            {
                if (lot.Zone != ItemClass.Zone.Industrial && lot.Zone != ItemClass.Zone.CommercialLow && lot.Zone != ItemClass.Zone.CommercialHigh) continue;
                if (!NativeCityLedger.HasActiveZone(lot)) continue;
                sites[lot.Id] = lot;
                BuildingAI ai = ActiveAI(ref buildings[lot.Id]);
                if (lot.Zone == ItemClass.Zone.Industrial)
                {
                    if (ai is IndustrialBuildingAI && Stock(ai, lot.Id, ref buildings[lot.Id]) >= ParcelSize) stockedFactories++;
                    continue;
                }
                if (ai is CommercialBuildingAI && Space(ai, lot.Id, ref buildings[lot.Id]) >= ParcelSize) shopsWithSpace++;
                List<ushort> list;
                if (!shops.TryGetValue(lot.Ward, out list)) shops.Add(lot.Ward, list = new List<ushort>());
                list.Add(lot.Id);
            }
        }

        internal bool Dispatch(uint id, ref Citizen citizen, DateTime gameTime)
        {
            bool atWork = citizen.CurrentLocation == Citizen.Location.Work;
            bool atHome = citizen.CurrentLocation == Citizen.Location.Home && gameTime.Hour >= 7 && gameTime.Hour < 17;
            NativeCityLedger.Lot supplier;
            DateTime next;
            if ((!atWork && !atHome) || trips.ContainsKey(id) ||
                !sites.TryGetValue(citizen.m_workBuilding, out supplier) || supplier.Zone != ItemClass.Zone.Industrial ||
                busyWards.Contains(supplier.Ward) || (retryAfter.TryGetValue(id, out next) && next > gameTime)) return false;
            if (atWork) workersSeenAtFactory++;
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            BuildingAI industry = ActiveAI(ref buildings[supplier.Id]);
            List<ushort> candidates;
            if (!(industry is IndustrialBuildingAI) || !shops.TryGetValue(supplier.Ward, out candidates) ||
                Stock(industry, supplier.Id, ref buildings[supplier.Id]) < ParcelSize) return false;
            Vector3 source = buildings[supplier.Id].m_position;
            if (atHome && (buildings[citizen.m_homeBuilding].m_position - source).sqrMagnitude > 650 * 650) return false;
            ushort target = 0;
            float best = 700 * 700;
            foreach (ushort shop in candidates)
            {
                float distance = (buildings[shop].m_position - source).sqrMagnitude;
                if (reservedShops.Contains(shop) || distance < 12 * 12 || distance >= best) continue;
                BuildingAI ai = ActiveAI(ref buildings[shop]);
                if (!(ai is CommercialBuildingAI) || Space(ai, shop, ref buildings[shop]) < ParcelSize) continue;
                target = shop;
                best = distance;
            }
            if (target == 0) return false;
            Trip trip = new Trip {Supplier=supplier.Id, Target=target, SupplierGeneration=buildings[supplier.Id].m_buildIndex,
                TargetGeneration=buildings[target].m_buildIndex, Ward=supplier.Ward,
                Frame=SimulationManager.instance.m_currentFrameIndex, Collecting=atHome};
            if (atHome ? !StartLeg(id, ref citizen, citizen.m_homeBuilding, supplier.Id, false) :
                !StartLeg(id, ref citizen, supplier.Id, target, true)) return false;
            trips.Add(id, trip);
            reservedShops.Add(target);
            busyWards.Add(supplier.Ward);
            if (atHome) pickupStarts++; else started++;
            return true;
        }

        private static bool StartLeg(uint id, ref Citizen citizen, ushort from, ushort to, bool visit)
        {
            CitizenInfo info = citizen.GetCitizenInfo(id);
            HumanAI actor = info == null ? null : info.GetAI() as HumanAI;
            if (actor == null || from == 0 || to == 0) return false;
            if (visit)
            {
                citizen.SetVisitplace(id, to, 0);
                if (citizen.m_visitBuilding != to) return false;
            }
            if (!actor.StartMoving(id, ref citizen, from, to))
            {
                if (visit) citizen.SetVisitplace(id, 0, 0);
                return false;
            }
            citizen.CurrentLocation = Citizen.Location.Moving;
            return true;
        }

        private static void BeforeArrival(ref CitizenInstance __1, out Arrival __state)
        {
            __state = new Arrival {Citizen=__1.m_citizen, Source=__1.m_sourceBuilding, Target=__1.m_targetBuilding, Path=__1.m_path};
        }

        private static void AfterArrival(bool __2, Arrival __state)
        {
            if (active != null) active.ObserveArrival(__state, __2);
        }

        private void ObserveArrival(Arrival arrival, bool success)
        {
            uint id = arrival.Citizen;
            Trip trip;
            if (!trips.TryGetValue(id, out trip)) return;
            if (!success)
            {
                failedLegs++;
                if (failures.Count == 12) failures.Dequeue();
                failures.Enqueue(new Dictionary<string, object> {{"android",id}, {"collecting",trip.Collecting},
                    {"from",arrival.Source}, {"target",arrival.Target}, {"pathId",arrival.Path},
                    {"expectedTarget",trip.Collecting ? trip.Supplier : trip.Target}});
                retryAfter[id] = SimulationManager.instance.m_currentGameTime.AddHours(18);
                EndTrip(id);
                return;
            }
            if (!trip.Collecting)
            {
                TransferOnArrival(id, arrival.Target);
                return;
            }
            if (arrival.Target == trip.Supplier && readyAtFactory.Count < 32) readyAtFactory.Enqueue(id);
        }

        private static void AfterCitizenStep()
        {
            NativeAndroidDeliveries delivery = active;
            if (delivery == null || delivery.readyAtFactory.Count == 0) return;
            int count = delivery.readyAtFactory.Count;
            for (int i = 0; i < count; i++) delivery.LeaveFactory(delivery.readyAtFactory.Dequeue());
        }

        private void LeaveFactory(uint id)
        {
            Trip trip;
            if (!trips.TryGetValue(id, out trip) || !trip.Collecting) return;
            Citizen[] people = CitizenManager.instance.m_citizens.m_buffer;
            if (people[id].CurrentLocation != Citizen.Location.Work || people[id].m_workBuilding != trip.Supplier ||
                !HasPlannedBuildings(trip))
            {
                EndTrip(id);
                return;
            }
            pickupArrivals++;
            if (!StartLeg(id, ref people[id], trip.Supplier, trip.Target, true))
            {
                failedLegs++;
                EndTrip(id);
                return;
            }
            trip.Collecting = false;
            trips[id] = trip;
            started++;
        }

        private static bool HasPlannedBuildings(Trip trip)
        {
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            return buildings[trip.Supplier].m_buildIndex == trip.SupplierGeneration && buildings[trip.Target].m_buildIndex == trip.TargetGeneration;
        }

        private void TransferOnArrival(uint id, ushort target)
        {
            Trip trip;
            if (!trips.TryGetValue(id, out trip) || target != trip.Target) return;
            Citizen citizen = CitizenManager.instance.m_citizens.m_buffer[id];
            if (citizen.CurrentLocation != Citizen.Location.Visit || citizen.m_visitBuilding != target) return;
            EndTrip(id);
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            if (!HasPlannedBuildings(trip)) return;
            BuildingAI source = ActiveAI(ref buildings[trip.Supplier]);
            BuildingAI destination = ActiveAI(ref buildings[target]);
            if (!(source is IndustrialBuildingAI) || !(destination is CommercialBuildingAI)) return;
            int factoryBefore = Stock(source, trip.Supplier, ref buildings[trip.Supplier]);
            int shopBefore = Stock(destination, target, ref buildings[target]);
            int requested = Math.Min(ParcelSize, Math.Min(factoryBefore,
                Space(destination, target, ref buildings[target])));
            if (requested <= 0) return;
            int debit = -requested;
            source.ModifyMaterialBuffer(trip.Supplier, ref buildings[trip.Supplier], TransferManager.TransferReason.Goods, ref debit);
            if (debit >= 0) return;
            int credit = -debit;
            destination.ModifyMaterialBuffer(target, ref buildings[target], TransferManager.TransferReason.Goods, ref credit);
            int refund = -debit - credit;
            if (refund > 0)
                source.ModifyMaterialBuffer(trip.Supplier, ref buildings[trip.Supplier], TransferManager.TransferReason.Goods, ref refund);
            if (credit <= 0) return;
            delivered++;
            goods += credit;
            if (recent.Count == 64) recent.Dequeue();
            recent.Enqueue(new Dictionary<string, object> {{"android",id}, {"from",trip.Supplier}, {"to",target},
                {"ward",trip.Ward}, {"stockAdded",credit}, {"factoryBefore",factoryBefore},
                {"factoryAfter",Stock(source, trip.Supplier, ref buildings[trip.Supplier])}, {"shopBefore",shopBefore},
                {"shopAfter",Stock(destination, target, ref buildings[target])}, {"gameTime",SimulationManager.instance.m_currentGameTime.ToString("s")}});
        }

        internal void EndTrip(uint id)
        {
            Trip trip;
            if (!trips.TryGetValue(id, out trip)) return;
            trips.Remove(id);
            reservedShops.Remove(trip.Target);
            busyWards.Remove(trip.Ward);
        }

        private static BuildingAI ActiveAI(ref global::Building building)
        {
            global::Building.Flags required = global::Building.Flags.Created | global::Building.Flags.Active;
            if ((building.m_flags & required) != required || building.Info == null) return null;
            return building.Info.m_buildingAI;
        }

        private static int Stock(BuildingAI ai, ushort id, ref global::Building building)
        {
            int stock, capacity;
            ai.GetMaterialAmount(id, ref building, TransferManager.TransferReason.Goods, out stock, out capacity);
            return Math.Max(0, stock);
        }

        private static int Space(BuildingAI ai, ushort id, ref global::Building building)
        {
            int stock, capacity;
            ai.GetMaterialAmount(id, ref building, TransferManager.TransferReason.Goods, out stock, out capacity);
            return Math.Max(0, capacity - stock);
        }

        internal object Snapshot()
        {
            return new Dictionary<string, object> {{"active",trips.Count}, {"started",started}, {"delivered",delivered},
                {"stockAdded",goods}, {"stockedFactories",stockedFactories}, {"shopsWithSpace",shopsWithSpace},
                {"workersSeenAtFactory",workersSeenAtFactory}, {"pickupStarts",pickupStarts}, {"pickupArrivals",pickupArrivals},
                {"failedLegs",failedLegs}, {"recentFailures",failures.ToArray()}, {"recent",recent.ToArray()}};
        }

        public void Dispose()
        {
            if (active == this) active = null;
            if (harmony != null) harmony.UnpatchAll(PatchId);
        }
    }
}
