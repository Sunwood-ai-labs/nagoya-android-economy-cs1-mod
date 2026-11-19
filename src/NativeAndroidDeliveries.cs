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
        private const uint RouteLossGrace = 48;
        private const uint StallGraceFrames = 8192;
        private const double StreetRadiusMeters = 2400;
        private const float MaximumFactoryDistanceFromHome = 900;
        private const float MaximumShopDistanceFromFactory = 1100;
        private const uint GoodsOfferWindowFrames = 256;
        private const double RecentDemandBonusMeters = 150;
        private const double ExistingDeliveryPenaltyMeters = 120;
        private static NativeAndroidDeliveries active;
        private Harmony harmony;
        private readonly Dictionary<uint, Trip> trips = new Dictionary<uint, Trip>();
        private readonly Dictionary<ushort, NativeCityLedger.Lot> sites = new Dictionary<ushort, NativeCityLedger.Lot>();
        private readonly List<NativeCityLedger.Lot> shops = new List<NativeCityLedger.Lot>();
        private readonly Dictionary<ushort, NativeCityLedger.Road> accessRoads = new Dictionary<ushort, NativeCityLedger.Road>();
        private readonly Dictionary<ushort, NativeDeliveryStreetReach> factoryReach = new Dictionary<ushort, NativeDeliveryStreetReach>();
        private readonly HashSet<ushort> reservedShops = new HashSet<ushort>();
        private readonly HashSet<ushort> reservedFactories = new HashSet<ushort>();
        private readonly Dictionary<uint, DateTime> retryAfter = new Dictionary<uint, DateTime>();
        private readonly Dictionary<uint, DateTime> failedRoutes = new Dictionary<uint, DateTime>();
        private readonly Dictionary<string, int> deliveredByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> stockedFactoriesByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> activeShopsByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> shopsWithSpaceByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, ShopInventory> shopInventoryByWard = new Dictionary<string, ShopInventory>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, GoodsRequest> goodsRequests = new Dictionary<ushort, GoodsRequest>();
        private readonly Dictionary<string, int> activeDeliveriesByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> requestingShopsByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Queue<uint> readyAtFactory = new Queue<uint>();
        private readonly Queue<uint> arrivedAtWork = new Queue<uint>();
        private readonly Queue<object> recent = new Queue<object>();
        private readonly Queue<object> failures = new Queue<object>();
        private int pickupStarts, pickupArrivals, started, delivered, goods, stockedFactories, shopsWithSpace, workersSeenAtFactory, failedLegs, expiredLegs;
        private int blockedByStreetReach, observedGoodsOffers;
        internal int Delivered { get { return delivered; } }

        private sealed class Trip
        {
            public ushort Supplier, Target, From;
            public uint SupplierGeneration, TargetGeneration;
            public string SupplierWard, TargetWard;
            public uint Frame, LastRouteFrame, LastProgressFrame;
            public GoodsRequest Demand;
            public Vector3 LastPosition;
            public bool Collecting, HasPosition;
        }

        private struct GoodsRequest
        {
            public uint Frame, BuildIndex;
            public int Priority, Amount;
        }

        private sealed class ShopInventory
        {
            public int WithoutGoodsBuffer, Full, BelowFullParcel, FullParcel;
            public readonly Dictionary<string, int> IncomingMaterials = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private struct Arrival
        {
            public uint Citizen, Path;
            public ushort Target;
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
            MethodInfo incomingOffer = typeof(TransferManager).GetMethod("AddIncomingOffer",
                new Type[] {typeof(TransferManager.TransferReason), typeof(TransferManager.TransferOffer)});
            if (incomingOffer == null) throw new MissingMethodException("TransferManager.AddIncomingOffer");
            MethodInfo afterOffer = typeof(NativeAndroidDeliveries).GetMethod("AfterIncomingOffer", BindingFlags.Static | BindingFlags.NonPublic);
            harmony = new Harmony(PatchId);
            try
            {
                harmony.Patch(target, new HarmonyMethod(prefix), new HarmonyMethod(postfix), null, null);
                harmony.Patch(step, null, new HarmonyMethod(afterStep), null, null);
                harmony.Patch(incomingOffer, null, new HarmonyMethod(afterOffer), null, null);
            }
            catch
            {
                harmony.UnpatchAll(PatchId);
                throw;
            }
            active = this;
        }

        internal bool IsDelivering(uint id)
        {
            return trips.ContainsKey(id);
        }

        internal void BeginSample(DateTime gameTime)
        {
            CitizenManager manager = CitizenManager.instance;
            uint frame = SimulationManager.instance.m_currentFrameIndex;
            var expired = new List<uint>();
            foreach (var trip in trips)
            {
                Trip leg = trip.Value;
                Vector3 position;
                if (TryGetTravelPosition(manager, trip.Key, out position))
                {
                    leg.LastRouteFrame = frame;
                    if (!leg.HasPosition || (position - leg.LastPosition).sqrMagnitude >= 1)
                    {
                        leg.HasPosition = true;
                        leg.LastPosition = position;
                        leg.LastProgressFrame = frame;
                    }
                    if (unchecked(frame - leg.LastProgressFrame) < StallGraceFrames) continue;
                }
                else if (unchecked(frame - leg.LastRouteFrame) < RouteLossGrace) continue;
                expired.Add(trip.Key);
            }
            foreach (uint id in expired)
            {
                expiredLegs++;
                Vector3 ignored;
                string reason = TryGetTravelPosition(manager, id, out ignored) ? "native movement stalled" : "native route lost";
                uint path = CurrentPath(manager, id);
                FailLeg(id, trips[id], path, reason, gameTime);
            }
            var elapsedRoutes = new List<uint>();
            foreach (var route in failedRoutes)
                if (route.Value <= gameTime) elapsedRoutes.Add(route.Key);
            foreach (uint route in elapsedRoutes) failedRoutes.Remove(route);
            var elapsedRetries = new List<uint>();
            foreach (var retry in retryAfter)
                if (retry.Value <= gameTime) elapsedRetries.Add(retry.Key);
            foreach (uint id in elapsedRetries) retryAfter.Remove(id);
            sites.Clear();
            shops.Clear();
            accessRoads.Clear();
            factoryReach.Clear();
            stockedFactories = 0;
            shopsWithSpace = 0;
            stockedFactoriesByWard.Clear();
            activeShopsByWard.Clear();
            shopsWithSpaceByWard.Clear();
            shopInventoryByWard.Clear();
            requestingShopsByWard.Clear();
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            NativeCityLedger.Road[] roads;
            NativeCityLedger.Lot[] lots;
            NativeCityLedger.Snapshot(out roads, out lots);
            foreach (NativeCityLedger.Road road in roads)
                if (NativeCityLedger.HasActiveRoad(road)) accessRoads[road.Id] = road;
            foreach (NativeCityLedger.Lot lot in lots)
            {
                if (!NativeCityLedger.HasActiveZone(lot)) continue;
                sites[lot.Id] = lot;
                if (lot.Zone != ItemClass.Zone.Industrial && lot.Zone != ItemClass.Zone.CommercialLow && lot.Zone != ItemClass.Zone.CommercialHigh) continue;
                BuildingAI ai = ActiveAI(ref buildings[lot.Id]);
                if (lot.Zone == ItemClass.Zone.Industrial)
                {
                    if (ai is IndustrialBuildingAI && Stock(ai, lot.Id, ref buildings[lot.Id]) >= ParcelSize)
                    {
                        stockedFactories++;
                        AddWardCount(stockedFactoriesByWard, lot.Ward);
                    }
                    continue;
                }
                CommercialBuildingAI commercial = ai as CommercialBuildingAI;
                if (commercial == null) continue;
                AddWardCount(activeShopsByWard, lot.Ward);
                int stock, capacity;
                commercial.GetMaterialAmount(lot.Id, ref buildings[lot.Id], TransferManager.TransferReason.Goods, out stock, out capacity);
                int space = Math.Max(0, capacity - stock);
                ObserveShop(lot.Ward, commercial.m_incomingResource, capacity, space);
                if (space == 0) continue;
                shopsWithSpace++;
                AddWardCount(shopsWithSpaceByWard, lot.Ward);
                GoodsRequest request;
                if (TryGetGoodsRequest(lot, frame, out request)) AddWardCount(requestingShopsByWard, lot.Ward);
                shops.Add(lot);
            }
        }

        internal bool Dispatch(uint id, ref Citizen citizen, DateTime gameTime)
        {
            if (gameTime.Hour < 7 || gameTime.Hour >= 17) return false;
            bool atWork = citizen.CurrentLocation == Citizen.Location.Work;
            bool atHome = citizen.CurrentLocation == Citizen.Location.Home;
            NativeCityLedger.Lot supplier;
            DateTime next;
            if ((!atWork && !atHome) || trips.ContainsKey(id) ||
                !sites.TryGetValue(citizen.m_workBuilding, out supplier) || supplier.Zone != ItemClass.Zone.Industrial ||
                reservedFactories.Contains(supplier.Id) || (retryAfter.TryGetValue(id, out next) && next > gameTime)) return false;
            if (atWork) workersSeenAtFactory++;
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            BuildingAI industry = ActiveAI(ref buildings[supplier.Id]);
            if (!(industry is IndustrialBuildingAI) || buildings[supplier.Id].m_buildIndex != supplier.BuildIndex ||
                !accessRoads.ContainsKey(supplier.Road) ||
                Stock(industry, supplier.Id, ref buildings[supplier.Id]) < ParcelSize) return false;
            Vector3 source = buildings[supplier.Id].m_position;
            ushort from = atHome ? citizen.m_homeBuilding : supplier.Id;
            if (atHome && ((buildings[from].m_position - source).sqrMagnitude > MaximumFactoryDistanceFromHome * MaximumFactoryDistanceFromHome ||
                IsRouteCoolingDown(from, supplier.Id, gameTime))) return false;
            NativeDeliveryStreetReach reach;
            if (!factoryReach.TryGetValue(supplier.Id, out reach))
            {
                reach = new NativeDeliveryStreetReach(supplier.Road, source, StreetRadiusMeters);
                factoryReach.Add(supplier.Id, reach);
            }
            NativeCityLedger.Lot home;
            if (atHome && sites.TryGetValue(from, out home) && accessRoads.ContainsKey(home.Road) &&
                reach.DistanceTo(home.Road, buildings[from].m_position) > StreetRadiusMeters)
            {
                blockedByStreetReach++;
                return false;
            }
            NativeCityLedger.Lot target = new NativeCityLedger.Lot();
            GoodsRequest targetRequest = new GoodsRequest();
            double best = double.PositiveInfinity;
            uint frame = SimulationManager.instance.m_currentFrameIndex;
            foreach (NativeCityLedger.Lot shop in shops)
            {
                float distance = (buildings[shop.Id].m_position - source).sqrMagnitude;
                if (reservedShops.Contains(shop.Id) || distance < 12 * 12 || distance > MaximumShopDistanceFromFactory * MaximumShopDistanceFromFactory ||
                    !accessRoads.ContainsKey(shop.Road) || buildings[shop.Id].m_buildIndex != shop.BuildIndex ||
                    IsRouteCoolingDown(supplier.Id, shop.Id, gameTime)) continue;
                double distanceAlongStreets = reach.DistanceTo(shop.Road, buildings[shop.Id].m_position);
                if (distanceAlongStreets >= StreetRadiusMeters) continue;
                BuildingAI ai = ActiveAI(ref buildings[shop.Id]);
                if (!(ai is CommercialBuildingAI)) continue;
                int space = Space(ai, shop.Id, ref buildings[shop.Id]);
                if (space == 0) continue;
                GoodsRequest request;
                bool hasRequest = TryGetGoodsRequest(shop, frame, out request);
                int deliveredHere;
                deliveredByWard.TryGetValue(shop.Ward, out deliveredHere);
                int inFlightHere;
                activeDeliveriesByWard.TryGetValue(shop.Ward, out inFlightHere);
                double score = distanceAlongStreets + Math.Min(3, deliveredHere + inFlightHere) * ExistingDeliveryPenaltyMeters +
                    (ParcelSize - Math.Min(ParcelSize, space));
                if (hasRequest) score -= RecentDemandBonusMeters;
                if (score >= best) continue;
                target = shop;
                targetRequest = hasRequest ? request : new GoodsRequest();
                best = score;
            }
            if (target.Id == 0)
            {
                blockedByStreetReach++;
                return false;
            }
            Trip trip = new Trip {Supplier=supplier.Id, Target=target.Id, From=from,
                SupplierGeneration=buildings[supplier.Id].m_buildIndex, TargetGeneration=buildings[target.Id].m_buildIndex,
                SupplierWard=supplier.Ward, TargetWard=target.Ward,
                Frame=frame, LastRouteFrame=frame, LastProgressFrame=frame,
                Collecting=atHome, Demand=targetRequest};
            trips.Add(id, trip);
            reservedShops.Add(target.Id);
            reservedFactories.Add(supplier.Id);
            AddWardCount(activeDeliveriesByWard, target.Ward);
            if (!StartLeg(id, ref citizen, from, atHome ? supplier.Id : target.Id, !atHome))
            {
                if (trips.ContainsKey(id)) FailLeg(id, trip, 0, "native path rejected at departure", gameTime);
                return false;
            }
            if (atHome) pickupStarts++; else started++;
            return true;
        }

        private static uint RouteKey(ushort from, ushort to)
        {
            return ((uint)from << 16) | to;
        }

        private static void AfterIncomingOffer(TransferManager.TransferReason __0, TransferManager.TransferOffer __1)
        {
            NativeAndroidDeliveries delivery = active;
            if (delivery == null || __0 != TransferManager.TransferReason.Goods || __1.Building == 0 || __1.Amount <= 0) return;
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            if (!(ActiveAI(ref buildings[__1.Building]) is CommercialBuildingAI)) return;
            delivery.goodsRequests[__1.Building] = new GoodsRequest {Frame=SimulationManager.instance.m_currentFrameIndex,
                BuildIndex=buildings[__1.Building].m_buildIndex, Priority=__1.Priority, Amount=__1.Amount};
            delivery.observedGoodsOffers++;
        }

        private bool TryGetGoodsRequest(NativeCityLedger.Lot lot, uint frame, out GoodsRequest request)
        {
            return goodsRequests.TryGetValue(lot.Id, out request) && request.BuildIndex == lot.BuildIndex &&
                unchecked(frame - request.Frame) <= GoodsOfferWindowFrames;
        }

        private bool IsRouteCoolingDown(ushort from, ushort to, DateTime now)
        {
            DateTime until;
            return failedRoutes.TryGetValue(RouteKey(from, to), out until) && until > now;
        }

        private static uint CurrentPath(CitizenManager manager, uint id)
        {
            Citizen citizen = manager.m_citizens.m_buffer[id];
            return citizen.m_instance == 0 ? 0 : manager.m_instances.m_buffer[citizen.m_instance].m_path;
        }

        private static bool TryGetTravelPosition(CitizenManager manager, uint id, out Vector3 position)
        {
            position = Vector3.zero;
            Citizen citizen = manager.m_citizens.m_buffer[id];
            if (citizen.CurrentLocation != Citizen.Location.Moving) return false;
            if (citizen.m_vehicle != 0)
            {
                Vehicle vehicle = VehicleManager.instance.m_vehicles.m_buffer[citizen.m_vehicle];
                if ((vehicle.m_flags & Vehicle.Flags.Spawned) != 0)
                {
                    position = vehicle.m_frame0.m_position;
                    return true;
                }
            }
            if (citizen.m_instance == 0) return false;
            CitizenInstance instance = manager.m_instances.m_buffer[citizen.m_instance];
            if (instance.m_citizen != id || instance.m_path == 0) return false;
            position = instance.m_frame0.m_position;
            return true;
        }

        private static bool StartLeg(uint id, ref Citizen citizen, ushort from, ushort to, bool visit)
        {
            ushort previousVisit = citizen.m_visitBuilding;
            if (visit)
            {
                citizen.SetVisitplace(id, to, 0);
                if (citizen.m_visitBuilding != to) return false;
            }
            if (!AndroidLifePilot.TryStartNativeMovement(id, ref citizen, from, to))
            {
                if (visit && citizen.m_visitBuilding == to) citizen.SetVisitplace(id, previousVisit, 0);
                return false;
            }
            return true;
        }

        private static void BeforeArrival(ref CitizenInstance __1, out Arrival __state)
        {
            __state = new Arrival {Citizen=__1.m_citizen, Target=__1.m_targetBuilding, Path=__1.m_path};
        }

        private static void AfterArrival(bool __2, Arrival __state)
        {
            if (active != null) active.ObserveArrival(__state, __2);
        }

        private void ObserveArrival(Arrival arrival, bool success)
        {
            uint id = arrival.Citizen;
            Trip trip;
            if (!trips.TryGetValue(id, out trip))
            {
                if (arrival.Target != 0 && AndroidLifePilot.IsAndroidCitizen(id))
                {
                    AndroidLifePilot.ObserveNativeHomeArrival(id, arrival.Target, success);
                    if (success)
                    {
                        Citizen citizen = CitizenManager.instance.m_citizens.m_buffer[id];
                        if (citizen.CurrentLocation == Citizen.Location.Work && citizen.m_workBuilding == arrival.Target)
                            arrivedAtWork.Enqueue(id);
                    }
                }
                return;
            }
            ushort expected = trip.Collecting ? trip.Supplier : trip.Target;
            if (arrival.Target != expected) return;
            if (!success)
            {
                FailLeg(id, trip, arrival.Path, "native arrival failed", SimulationManager.instance.m_currentGameTime);
                return;
            }
            if (!trip.Collecting)
            {
                TransferOnArrival(id, arrival.Target);
                return;
            }
            readyAtFactory.Enqueue(id);
        }

        private static void AfterCitizenStep()
        {
            NativeAndroidDeliveries delivery = active;
            if (delivery == null) return;
            int count = delivery.readyAtFactory.Count;
            for (int i = 0; i < count; i++) delivery.LeaveFactory(delivery.readyAtFactory.Dequeue());
            int arrivals = delivery.arrivedAtWork.Count;
            if (arrivals == 0) return;
            Citizen[] people = CitizenManager.instance.m_citizens.m_buffer;
            DateTime now = SimulationManager.instance.m_currentGameTime;
            for (int i = 0; i < arrivals; i++)
            {
                uint id = delivery.arrivedAtWork.Dequeue();
                if (AndroidLifePilot.CanDeliverAtWork(id, ref people[id]))
                    delivery.Dispatch(id, ref people[id], now);
            }
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
            trip.From = trip.Supplier;
            trip.Frame = SimulationManager.instance.m_currentFrameIndex;
            trip.LastRouteFrame = trip.Frame;
            trip.LastProgressFrame = trip.Frame;
            trip.HasPosition = false;
            trip.Collecting = false;
            if (!StartLeg(id, ref people[id], trip.Supplier, trip.Target, true))
            {
                if (trips.ContainsKey(id))
                    FailLeg(id, trip, 0, "native path rejected at departure", SimulationManager.instance.m_currentGameTime);
                return;
            }
            started++;
        }

        private void FailLeg(uint id, Trip trip, uint path, string reason, DateTime now)
        {
            ushort target = trip.Collecting ? trip.Supplier : trip.Target;
            failedLegs++;
            if (failures.Count == 12) failures.Dequeue();
            failures.Enqueue(new Dictionary<string, object> {{"android",id}, {"collecting",trip.Collecting},
                {"from",trip.From}, {"target",target}, {"pathId",path}, {"expectedTarget",target}, {"reason",reason}});
            failedRoutes[RouteKey(trip.From, target)] = now.AddHours(12);
            retryAfter[id] = now.AddHours(2);
            EndTrip(id);
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
            AddWardCount(deliveredByWard, trip.TargetWard);
            if (recent.Count == 64) recent.Dequeue();
            recent.Enqueue(new Dictionary<string, object> {{"android",id}, {"from",trip.Supplier}, {"to",target},
                {"ward",trip.TargetWard}, {"sourceWard",trip.SupplierWard}, {"stockAdded",credit}, {"factoryBefore",factoryBefore},
                {"factoryAfter",Stock(source, trip.Supplier, ref buildings[trip.Supplier])}, {"shopBefore",shopBefore}, {"nativeRequest",RequestSnapshot(trip.Demand)},
                {"shopAfter",Stock(destination, target, ref buildings[target])}, {"gameTime",SimulationManager.instance.m_currentGameTime.ToString("s")}});
        }

        private static void AddWardCount(Dictionary<string, int> counts, string ward)
        {
            int count;
            counts.TryGetValue(ward, out count);
            counts[ward] = count + 1;
        }

        private void ObserveShop(string ward, TransferManager.TransferReason material, int capacity, int space)
        {
            if (String.IsNullOrEmpty(ward)) ward = "unassigned";
            ShopInventory inventory;
            if (!shopInventoryByWard.TryGetValue(ward, out inventory))
                shopInventoryByWard.Add(ward, inventory = new ShopInventory());
            AddWardCount(inventory.IncomingMaterials, material.ToString());
            if (capacity <= 0) inventory.WithoutGoodsBuffer++;
            else if (space == 0) inventory.Full++;
            else if (space < ParcelSize) inventory.BelowFullParcel++;
            else inventory.FullParcel++;
        }

        internal void EndTrip(uint id)
        {
            Trip trip;
            if (!trips.TryGetValue(id, out trip)) return;
            trips.Remove(id);
            reservedShops.Remove(trip.Target);
            reservedFactories.Remove(trip.Supplier);
            int remaining = activeDeliveriesByWard[trip.TargetWard] - 1;
            if (remaining == 0) activeDeliveriesByWard.Remove(trip.TargetWard);
            else activeDeliveriesByWard[trip.TargetWard] = remaining;
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

        private static object RequestSnapshot(GoodsRequest request)
        {
            return request.Amount > 0 ? new Dictionary<string, object> {{"frame",request.Frame}, {"priority",request.Priority}, {"amount",request.Amount}} : null;
        }

        internal object Snapshot()
        {
            var shopInventory = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in shopInventoryByWard)
            {
                ShopInventory inventory = pair.Value;
                shopInventory.Add(pair.Key, new Dictionary<string, object> {
                    {"noGoodsBuffer",inventory.WithoutGoodsBuffer}, {"full",inventory.Full},
                    {"roomForPartialParcel",inventory.BelowFullParcel}, {"roomForFullParcel",inventory.FullParcel},
                    {"incomingMaterials",inventory.IncomingMaterials}});
            }
            var inFlight = new List<object>();
            foreach (var live in trips)
            {
                Trip leg = live.Value;
                inFlight.Add(new Dictionary<string, object> {{"android",live.Key}, {"from",leg.From},
                    {"target",leg.Collecting ? leg.Supplier : leg.Target}, {"deliveryBuilding",leg.Target}, {"destinationWard",leg.TargetWard},
                    {"frame",leg.Frame}, {"lastProgressFrame",leg.LastProgressFrame}, {"nativeRequest",RequestSnapshot(leg.Demand)},
                    {"position",leg.HasPosition ? new float[] {leg.LastPosition.x, leg.LastPosition.y, leg.LastPosition.z} : null}});
            }
            return new Dictionary<string, object> {{"active",trips.Count}, {"started",started}, {"delivered",delivered},
                {"stockAdded",goods}, {"stockedFactories",stockedFactories}, {"shopsWithSpace",shopsWithSpace},
                {"stockedFactoriesByWard",stockedFactoriesByWard}, {"activeShopsByWard",activeShopsByWard}, {"shopsWithSpaceByWard",shopsWithSpaceByWard},
                {"shopInventoryByWard",shopInventory},
                {"observedGoodsOffers",observedGoodsOffers}, {"requestingShopsByWard",requestingShopsByWard}, {"activeDeliveriesByWard",activeDeliveriesByWard},
                {"workersSeenAtFactory",workersSeenAtFactory}, {"pickupStarts",pickupStarts}, {"pickupArrivals",pickupArrivals},
                {"failedLegs",failedLegs}, {"expiredLegs",expiredLegs}, {"deliveriesByWard",deliveredByWard},
                {"noEligibleStreetCandidate",blockedByStreetReach},
                {"inFlight",inFlight}, {"recentFailures",failures.ToArray()}, {"recent",recent.ToArray()}};
        }

        public void Dispose()
        {
            if (active == this) active = null;
            if (harmony != null) harmony.UnpatchAll(PatchId);
        }
    }
}
