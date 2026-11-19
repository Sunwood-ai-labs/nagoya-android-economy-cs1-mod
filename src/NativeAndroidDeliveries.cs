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
        private static NativeAndroidDeliveries active;
        private Harmony harmony;
        private readonly Dictionary<uint, Trip> trips = new Dictionary<uint, Trip>();
        private readonly Dictionary<ushort, NativeCityLedger.Lot> sites = new Dictionary<ushort, NativeCityLedger.Lot>();
        private readonly Dictionary<string, List<ushort>> shops = new Dictionary<string, List<ushort>>(StringComparer.Ordinal);
        private readonly HashSet<ushort> reservedShops = new HashSet<ushort>();
        private readonly HashSet<string> busyWards = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<object> recent = new Queue<object>();
        private int started, delivered, goods, stockedFactories, shopsWithSpace, workersSeenAtFactory;
        internal int Delivered { get { return delivered; } }

        private struct Trip
        {
            public ushort Supplier, Target;
            public uint SupplierGeneration, TargetGeneration;
            public string Ward;
            public DateTime Expires;
        }

        internal NativeAndroidDeliveries()
        {
            MethodInfo target = typeof(HumanAI).GetMethod("ArriveAtDestination",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new Type[] {typeof(ushort), typeof(CitizenInstance).MakeByRefType(), typeof(bool)}, null);
            if (target == null) throw new MissingMethodException("HumanAI.ArriveAtDestination");
            MethodInfo postfix = typeof(NativeAndroidDeliveries).GetMethod("AfterArrival", BindingFlags.Static | BindingFlags.NonPublic);
            harmony = new Harmony(PatchId);
            harmony.Patch(target, null, new HarmonyMethod(postfix), null, null);
            active = this;
        }

        internal void BeginSample(DateTime now)
        {
            var expired = new List<uint>();
            foreach (var trip in trips)
                if (trip.Value.Expires <= now) expired.Add(trip.Key);
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
            NativeCityLedger.Lot supplier;
            if (citizen.CurrentLocation != Citizen.Location.Work || citizen.m_visitBuilding != 0 || trips.ContainsKey(id) ||
                !sites.TryGetValue(citizen.m_workBuilding, out supplier) || supplier.Zone != ItemClass.Zone.Industrial ||
                busyWards.Contains(supplier.Ward)) return false;
            workersSeenAtFactory++;
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            BuildingAI industry = ActiveAI(ref buildings[supplier.Id]);
            List<ushort> candidates;
            if (!(industry is IndustrialBuildingAI) || !shops.TryGetValue(supplier.Ward, out candidates) ||
                Stock(industry, supplier.Id, ref buildings[supplier.Id]) < ParcelSize) return false;
            ushort target = 0;
            float best = 700 * 700;
            Vector3 source = buildings[supplier.Id].m_position;
            foreach (ushort shop in candidates)
            {
                float distance = (buildings[shop].m_position - source).sqrMagnitude;
                if (reservedShops.Contains(shop) || distance < 40 * 40 || distance >= best) continue;
                BuildingAI ai = ActiveAI(ref buildings[shop]);
                if (!(ai is CommercialBuildingAI) || Space(ai, shop, ref buildings[shop]) < ParcelSize) continue;
                target = shop;
                best = distance;
            }
            if (target == 0) return false;
            CitizenInfo info = citizen.GetCitizenInfo(id);
            HumanAI actor = info == null ? null : info.GetAI() as HumanAI;
            if (actor == null) return false;
            citizen.SetVisitplace(id, target, 0);
            if (citizen.m_visitBuilding != target) return false;
            if (!actor.StartMoving(id, ref citizen, supplier.Id, target))
            {
                citizen.SetVisitplace(id, 0, 0);
                return false;
            }
            citizen.CurrentLocation = Citizen.Location.Moving;
            trips.Add(id, new Trip {Supplier=supplier.Id, Target=target, SupplierGeneration=buildings[supplier.Id].m_buildIndex,
                TargetGeneration=buildings[target].m_buildIndex, Ward=supplier.Ward, Expires=gameTime.AddHours(24)});
            reservedShops.Add(target);
            busyWards.Add(supplier.Ward);
            started++;
            return true;
        }

        private static void AfterArrival(ref CitizenInstance __1, bool __2)
        {
            if (active != null && __2) active.TransferOnArrival(__1.m_citizen, __1.m_targetBuilding);
        }

        private void TransferOnArrival(uint id, ushort target)
        {
            Trip trip;
            if (!trips.TryGetValue(id, out trip) || target != trip.Target) return;
            Citizen citizen = CitizenManager.instance.m_citizens.m_buffer[id];
            if (citizen.CurrentLocation != Citizen.Location.Visit || citizen.m_visitBuilding != target) return;
            EndTrip(id);
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            if (buildings[trip.Supplier].m_buildIndex != trip.SupplierGeneration || buildings[target].m_buildIndex != trip.TargetGeneration) return;
            BuildingAI source = ActiveAI(ref buildings[trip.Supplier]);
            BuildingAI destination = ActiveAI(ref buildings[target]);
            if (!(source is IndustrialBuildingAI) || !(destination is CommercialBuildingAI)) return;
            int requested = Math.Min(ParcelSize, Math.Min(Stock(source, trip.Supplier, ref buildings[trip.Supplier]),
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
                {"ward",trip.Ward}, {"stockAdded",credit}, {"gameTime",SimulationManager.instance.m_currentGameTime.ToString("s")}});
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
                {"workersSeenAtFactory",workersSeenAtFactory}, {"recent",recent.ToArray()}};
        }

        public void Dispose()
        {
            if (active == this) active = null;
            if (harmony != null) harmony.UnpatchAll(PatchId);
        }
    }
}
