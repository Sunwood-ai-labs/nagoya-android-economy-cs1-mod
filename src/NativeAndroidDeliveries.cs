using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeAndroidDeliveries : IDisposable
    {
        private const string PatchId = "sunwood.nagoya.android-goods-delivery";
        private const uint CheckpointFormat = 0x3156444e;
        private const int MaxCheckpointBytes = 512 * 1024;
        private const int ParcelSize = 100;
        private const uint RouteLossGrace = 512;
        private const uint StallGraceFrames = 8192;
        private const double StreetRadiusMeters = 2400;
        private const double ScheduledStreetRadiusMeters = 4200;
        private const float MaximumFactoryDistanceFromHome = 900;
        private const float MaximumShopDistanceFromFactory = 1100;
        private const float MaximumScheduledShopDistance = 1800;
        private const uint GoodsOfferWindowFrames = 256;
        private const double RecentDemandBonusMeters = 150;
        private const double ExistingDeliveryPenaltyMeters = 120;
        private const float LocalEmploymentRadiusMeters = 400;
        private const double LocalEmploymentStreetMeters = 700;
        private const int LocalHiresPerReceivingWard = 2;
        private static NativeAndroidDeliveries active;
        private static Checkpoint pending;
        private Harmony harmony;
        private readonly Dictionary<uint, Trip> trips = new Dictionary<uint, Trip>();
        private readonly Dictionary<ushort, NativeCityLedger.Lot> sites = new Dictionary<ushort, NativeCityLedger.Lot>();
        private readonly List<NativeCityLedger.Lot> shops = new List<NativeCityLedger.Lot>();
        private readonly List<NativeCityLedger.Lot> factories = new List<NativeCityLedger.Lot>();
        private readonly Dictionary<ushort, ushort> streetByBuilding = new Dictionary<ushort, ushort>();
        private readonly Dictionary<string, int> differentEntranceRoadsByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> missingEntranceSidewalksByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, NativeDeliveryStreetReach> factoryReach = new Dictionary<ushort, NativeDeliveryStreetReach>();
        private readonly Dictionary<ushort, NativeDeliveryStreetReach> scheduledFactoryReach = new Dictionary<ushort, NativeDeliveryStreetReach>();
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
        private readonly Dictionary<string, int> hiredForWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> hiringRejections = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> failuresByReason = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<uint, ushort> factoryHires = new Dictionary<uint, ushort>();
        private readonly Dictionary<uint, FirstDelivery> firstShopByResident = new Dictionary<uint, FirstDelivery>();
        private readonly Queue<uint> arrivedAtWork = new Queue<uint>();
        private readonly Queue<object> recent = new Queue<object>();
        private readonly Queue<object> failures = new Queue<object>();
        private readonly Queue<object> onsiteDispatches = new Queue<object>();
        private readonly HashSet<string> onsitePriorityWards = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> onsiteStartsByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> onsiteDeliveriesByWard = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<ushort> stockedFactoriesWithWorkerOnsite = new HashSet<ushort>();
        private readonly List<object> localHires = new List<object>();
        private int started, delivered, goods, stockedFactories, shopsWithSpace, workersSeenAtFactory, failedLegs, expiredLegs;
        private int blockedByStreetReach, observedGoodsOffers;
        private int nativePathFailureCallbacks;
        private int restoredTrips, droppedSavedTrips;
        internal int Delivered { get { return delivered; } }

        private sealed class Trip
        {
            public ushort Supplier, Target, SupplierStreet, TargetStreet;
            public uint SupplierGeneration, TargetGeneration;
            public string SupplierWard, TargetWard;
            public uint Frame, MissingRouteSinceFrame, LastProgressFrame;
            public int NativePathFailures;
            public GoodsRequest Demand;
            public Vector3 LastPosition;
            public bool HasPosition, OnsitePriority, NativeRouteAccepted, RouteMissing, RestoredAfterLoad;
        }

        private sealed class Checkpoint
        {
            public string City;
            public int Started, Delivered, Goods;
            public readonly Dictionary<string, int> DeliveriesByWard = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<uint, Trip> Trips = new Dictionary<uint, Trip>();
        }

        private struct GoodsRequest
        {
            public uint Frame, BuildIndex;
            public int Priority, Amount;
        }

        private struct DeliveryChoice
        {
            public NativeCityLedger.Lot Shop;
            public GoodsRequest Request;
        }

        internal struct LocalOpening
        {
            public ushort Home, Factory, Shop;
            public uint HomeUnit, Worker;
            public int FactoryGoods;
            public double HomeDistance, ShopDistance;
        }

        private struct FirstDelivery
        {
            public ushort Factory, Shop;
            public uint ShopGeneration;
            public DateTime Until;
            public bool ExistingWorker;
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
            MethodInfo pathFailure = typeof(HumanAI).GetMethod("PathfindFailure",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new Type[] {typeof(ushort), typeof(CitizenInstance).MakeByRefType()}, null);
            if (pathFailure == null) throw new MissingMethodException("HumanAI.PathfindFailure");
            MethodInfo beforeFailure = typeof(NativeAndroidDeliveries).GetMethod("BeforePathFailure", BindingFlags.Static | BindingFlags.NonPublic);
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
                harmony.Patch(pathFailure, new HarmonyMethod(beforeFailure), null, null, null);
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

        internal static byte[] SaveCheckpoint()
        {
            NativeAndroidDeliveries delivery = active;
            if (delivery == null) return null;
            string city = SimulationManager.instance.m_metaData.m_CityName;
            if (city == null || !city.StartsWith("NagoyaCitywide-", StringComparison.Ordinal)) return null;
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                writer.Write(CheckpointFormat);
                writer.Write(city);
                writer.Write(delivery.started);
                writer.Write(delivery.delivered);
                writer.Write(delivery.goods);
                writer.Write(delivery.deliveredByWard.Count);
                foreach (var ward in delivery.deliveredByWard)
                {
                    writer.Write(ward.Key);
                    writer.Write(ward.Value);
                }
                writer.Write(delivery.trips.Count);
                foreach (var entry in delivery.trips)
                {
                    Trip trip = entry.Value;
                    writer.Write(entry.Key);
                    writer.Write(trip.Supplier);
                    writer.Write(trip.Target);
                    writer.Write(trip.SupplierStreet);
                    writer.Write(trip.TargetStreet);
                    writer.Write(trip.SupplierGeneration);
                    writer.Write(trip.TargetGeneration);
                    writer.Write(trip.SupplierWard);
                    writer.Write(trip.TargetWard);
                    writer.Write(trip.Frame);
                    writer.Write(trip.OnsitePriority);
                    writer.Write(trip.NativeRouteAccepted);
                }
                writer.Flush();
                if (memory.Length > MaxCheckpointBytes) throw new InvalidDataException("Android delivery checkpoint size");
                return memory.ToArray();
            }
        }

        internal static void LoadCheckpoint(byte[] payload)
        {
            pending = null;
            if (payload == null || payload.Length == 0) return;
            if (payload.Length > MaxCheckpointBytes) throw new InvalidDataException("Android delivery checkpoint size");
            using (MemoryStream memory = new MemoryStream(payload, false))
            using (BinaryReader reader = new BinaryReader(memory))
            {
                if (reader.ReadUInt32() != CheckpointFormat) throw new InvalidDataException("Android delivery checkpoint version");
                Checkpoint saved = new Checkpoint {City=reader.ReadString(), Started=reader.ReadInt32(),
                    Delivered=reader.ReadInt32(), Goods=reader.ReadInt32()};
                int wards = reader.ReadInt32();
                if (!saved.City.StartsWith("NagoyaCitywide-", StringComparison.Ordinal) || saved.City.Length > 150 ||
                    saved.Delivered < 0 || saved.Started < saved.Delivered || saved.Goods < saved.Delivered ||
                    saved.Goods > (long)saved.Delivered * ParcelSize || wards < 0 || wards > 32)
                    throw new InvalidDataException("Android delivery checkpoint counters");
                long counted = 0;
                for (int i = 0; i < wards; i++)
                {
                    string ward = reader.ReadString();
                    int count = reader.ReadInt32();
                    if (ward.Length > 40 || count <= 0 || saved.DeliveriesByWard.ContainsKey(ward))
                        throw new InvalidDataException("Android delivery checkpoint ward");
                    saved.DeliveriesByWard.Add(ward, count);
                    counted += count;
                }
                int trips = reader.ReadInt32();
                if (counted != saved.Delivered || trips < 0 || trips > NativeCityLedger.MaxResidents ||
                    trips > saved.Started - saved.Delivered)
                    throw new InvalidDataException("Android delivery checkpoint trip count");
                for (int i = 0; i < trips; i++)
                {
                    uint id = reader.ReadUInt32();
                    Trip trip = new Trip {Supplier=reader.ReadUInt16(), Target=reader.ReadUInt16(),
                        SupplierStreet=reader.ReadUInt16(), TargetStreet=reader.ReadUInt16(),
                        SupplierGeneration=reader.ReadUInt32(), TargetGeneration=reader.ReadUInt32(),
                        SupplierWard=reader.ReadString(), TargetWard=reader.ReadString(), Frame=reader.ReadUInt32(),
                        OnsitePriority=reader.ReadBoolean(), NativeRouteAccepted=reader.ReadBoolean(), RestoredAfterLoad=true};
                    if (id == 0 || saved.Trips.ContainsKey(id) || trip.Supplier == 0 || trip.Target == 0 ||
                        trip.Supplier == trip.Target || trip.SupplierWard.Length > 40 || trip.TargetWard.Length > 40)
                        throw new InvalidDataException("Android delivery checkpoint trip");
                    saved.Trips.Add(id, trip);
                }
                if (memory.Position != memory.Length) throw new InvalidDataException("Trailing Android delivery checkpoint data");
                pending = saved;
            }
        }

        internal void RestoreCheckpoint(string city)
        {
            Checkpoint saved = pending;
            pending = null;
            if (saved == null || saved.City != city) return;
            started = saved.Started;
            delivered = saved.Delivered;
            goods = saved.Goods;
            foreach (var ward in saved.DeliveriesByWard) deliveredByWard.Add(ward.Key, ward.Value);
            CitizenManager manager = CitizenManager.instance;
            uint frame = SimulationManager.instance.m_currentFrameIndex;
            foreach (var entry in saved.Trips)
            {
                uint id = entry.Key;
                Trip trip = entry.Value;
                if (!AndroidLifePilot.IsAndroidCitizen(id) || !trip.NativeRouteAccepted || !HasPlannedBuildings(trip) ||
                    reservedShops.Contains(trip.Target) || reservedFactories.Contains(trip.Supplier))
                {
                    droppedSavedTrips++;
                    continue;
                }
                Citizen citizen = manager.m_citizens.m_buffer[id];
                if (citizen.m_visitBuilding != trip.Target ||
                    (citizen.CurrentLocation != Citizen.Location.Moving && !IsAtDeliveryStore(citizen, trip.Target)))
                {
                    droppedSavedTrips++;
                    continue;
                }
                trip.LastProgressFrame = frame;
                ReserveTrip(id, trip);
                restoredTrips++;
            }
        }

        internal bool IsDelivering(uint id)
        {
            return trips.ContainsKey(id);
        }

        internal void BeginSample(DateTime gameTime)
        {
            CitizenManager manager = CitizenManager.instance;
            uint frame = SimulationManager.instance.m_currentFrameIndex;
            var arrived = new List<uint>();
            var expired = new List<uint>();
            foreach (var trip in trips)
            {
                Trip leg = trip.Value;
                Citizen citizen = manager.m_citizens.m_buffer[trip.Key];
                if (leg.NativeRouteAccepted && IsAtDeliveryStore(citizen, leg.Target))
                {
                    arrived.Add(trip.Key);
                    continue;
                }
                Vector3 position;
                if (TryGetTravelPosition(manager, trip.Key, out position))
                {
                    leg.RouteMissing = false;
                    if (!leg.HasPosition || (position - leg.LastPosition).sqrMagnitude >= 1)
                    {
                        leg.HasPosition = true;
                        leg.LastPosition = position;
                        leg.LastProgressFrame = frame;
                    }
                    if (unchecked(frame - leg.LastProgressFrame) < StallGraceFrames) continue;
                }
                else
                {
                    if (!leg.RouteMissing)
                    {
                        leg.RouteMissing = true;
                        leg.MissingRouteSinceFrame = frame;
                    }
                    if (unchecked(frame - leg.MissingRouteSinceFrame) < RouteLossGrace) continue;
                }
                expired.Add(trip.Key);
            }
            foreach (uint id in arrived)
                TransferOnArrival(id, trips[id].Target, "native visit state");
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
            factories.Clear();
            streetByBuilding.Clear();
            differentEntranceRoadsByWard.Clear();
            missingEntranceSidewalksByWard.Clear();
            factoryReach.Clear();
            scheduledFactoryReach.Clear();
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
            NetSegment[] network = NetManager.instance.m_segments.m_buffer;
            foreach (NativeCityLedger.Lot lot in lots)
            {
                if (!NativeCityLedger.HasActiveZone(lot)) continue;
                sites[lot.Id] = lot;
                ushort street = buildings[lot.Id].m_accessSegment;
                if (NativeDeliveryStreetReach.HasSidewalk(network, street))
                {
                    streetByBuilding[lot.Id] = street;
                    if (street != lot.Road) AddWardCount(differentEntranceRoadsByWard, lot.Ward);
                }
                else AddWardCount(missingEntranceSidewalksByWard, lot.Ward);
                if (lot.Zone != ItemClass.Zone.Industrial && lot.Zone != ItemClass.Zone.CommercialLow && lot.Zone != ItemClass.Zone.CommercialHigh) continue;
                BuildingAI ai = ActiveAI(ref buildings[lot.Id]);
                if (lot.Zone == ItemClass.Zone.Industrial)
                {
                    if (ai is IndustrialBuildingAI && Stock(ai, lot.Id, ref buildings[lot.Id]) >= ParcelSize)
                    {
                        stockedFactories++;
                        AddWardCount(stockedFactoriesByWard, lot.Ward);
                        if (streetByBuilding.ContainsKey(lot.Id)) factories.Add(lot);
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

        internal bool HireForLocalDelivery(uint id, ref Citizen citizen, DateTime gameTime)
        {
            if (gameTime.Hour < 7 || gameTime.Hour >= 16 || citizen.m_workBuilding != 0 ||
                citizen.CurrentLocation != Citizen.Location.Home || citizen.m_vehicle != 0 || factoryHires.ContainsKey(id)) return false;
            Citizen.AgeGroup age = Citizen.GetAgeGroup(citizen.Age);
            if (age != Citizen.AgeGroup.Young && age != Citizen.AgeGroup.Adult) return RejectHire("age " + age);
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            ushort home = citizen.m_homeBuilding;
            ushort homeStreet;
            Vector3 homePosition;
            bool trackedHome = streetByBuilding.TryGetValue(home, out homeStreet);
            if (trackedHome) homePosition = buildings[home].m_position;
            else if (!TryResolveHomeStreet(id, ref citizen, ref buildings[home], out homePosition, out homeStreet))
                return RejectHire("no pedestrian lane at home");
            NativeCityLedger.Lot selectedFactory = new NativeCityLedger.Lot();
            NativeCityLedger.Lot selectedShop = new NativeCityLedger.Lot();
            double shortest = LocalEmploymentStreetMeters;
            int vacancies = 0;
            bool nearbyFactory = false, streetAccess = false, shopAccess = false;
            foreach (NativeCityLedger.Lot factory in factories)
            {
                if ((buildings[factory.Id].m_position - homePosition).sqrMagnitude > LocalEmploymentRadiusMeters * LocalEmploymentRadiusMeters)
                    continue;
                nearbyFactory = true;
                NativeDeliveryStreetReach reach = StreetReach(factory, buildings[factory.Id].m_position, false);
                double walk = reach.DistanceTo(homeStreet, homePosition);
                if (walk >= shortest) continue;
                streetAccess = true;
                DeliveryChoice goods = SelectShop(factory, reach, gameTime, true, 0, false, false);
                if (goods.Shop.Id == 0) continue;
                shopAccess = true;
                IndustrialBuildingAI ai = ActiveAI(ref buildings[factory.Id]) as IndustrialBuildingAI;
                if (ai == null) continue;
                int available = VacantWorkplaces(factory.Id, ai, ref buildings[factory.Id]);
                if (available <= 0) continue;
                selectedFactory = factory;
                selectedShop = goods.Shop;
                shortest = walk;
                vacancies = available;
            }
            if (selectedFactory.Id == 0)
            {
                if (!nearbyFactory) return RejectHire("no stocked factory within home radius");
                if (!streetAccess) return RejectHire("no walkable route to a nearby factory");
                if (!shopAccess) return RejectHire("no suitable receiving shop on connected streets");
                return RejectHire("no vacant factory job");
            }
            citizen.SetWorkplace(id, selectedFactory.Id, 0);
            if (citizen.m_workBuilding != selectedFactory.Id) return RejectHire("native work unit refused");
            factoryHires.Add(id, selectedFactory.Id);
            AddWardCount(hiredForWard, selectedShop.Ward);
            localHires.Add(new Dictionary<string, object> {{"android",id}, {"home",home}, {"homeStreet",homeStreet},
                {"homeStreetSource",trackedHome ? "native building access" : "native pedestrian lane"}, {"factory",selectedFactory.Id},
                {"factoryWard",selectedFactory.Ward}, {"candidateShop",selectedShop.Id}, {"shopWard",selectedShop.Ward},
                {"vacanciesBefore",vacancies}, {"streetMetersFromHome",shortest}, {"gameTime",gameTime.ToString("s")}});
            return true;
        }

        internal LocalOpening FindLocalOpening(string ward, Func<ushort, uint> vacantHomeUnit, HashSet<ushort> admittedFactories,
            Dictionary<ushort, List<uint>> assignedWorkers, HashSet<ushort> startupFactories, HashSet<ushort> startupShops,
            HashSet<ushort> startupHomes, bool existingOnly, out Dictionary<string, int> counts)
        {
            bool advanceRegistration = startupHomes.Count > 0;
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            Dictionary<ushort, ushort> planningRoads = advanceRegistration
                ? NewPlacementRoads(ward, startupHomes, startupFactories, startupShops) : streetByBuilding;
            var homes = new Dictionary<ushort, uint>();
            if (!existingOnly)
                foreach (NativeCityLedger.Lot lot in sites.Values)
                {
                    if (lot.Zone != ItemClass.Zone.ResidentialLow && lot.Zone != ItemClass.Zone.ResidentialHigh) continue;
                    if (!planningRoads.ContainsKey(lot.Id) || (advanceRegistration && !startupHomes.Contains(lot.Id))) continue;
                    uint unit = vacantHomeUnit(lot.Id);
                    if (unit != 0) homes.Add(lot.Id, unit);
                }
            var eligibleShops = advanceRegistration ? NewReceivingStores(startupShops, planningRoads, buildings) : shops;
            var destinations = eligibleShops.FindAll(delegate(NativeCityLedger.Lot shop) {
                return shop.Ward == ward && planningRoads.ContainsKey(shop.Id) &&
                    (startupShops.Count == 0 || startupShops.Contains(shop.Id));
            });
            List<NativeCityLedger.Lot> eligibleFactories = factories;
            if (existingOnly)
                eligibleFactories = factories.FindAll(delegate(NativeCityLedger.Lot factory) {
                    return assignedWorkers.ContainsKey(factory.Id) &&
                        (startupFactories.Count == 0 || startupFactories.Contains(factory.Id));
                });
            else if (startupFactories.Count > 0)
            {
                eligibleFactories = new List<NativeCityLedger.Lot>();
                foreach (ushort id in startupFactories)
                {
                    NativeCityLedger.Lot candidate;
                    if (sites.TryGetValue(id, out candidate) && candidate.Ward == ward && candidate.Zone == ItemClass.Zone.Industrial &&
                        planningRoads.ContainsKey(id) && OpeningAI(ref buildings[id], advanceRegistration) is IndustrialBuildingAI)
                        eligibleFactories.Add(candidate);
                }
            }
            counts = new Dictionary<string, int> {{"shopsWithNativeAccess",destinations.Count},
                {"factoriesWithStockAndRoad",factories.Count}, {"vacantConnectedHomes",homes.Count},
                {"eligibleFactoriesWithNativeRoad",eligibleFactories.Count},
                {"factoriesWithinShopRadius",0}, {"factoriesWithReachableShop",0}, {"factoriesWithVacancy",0},
                {"factoriesWithExistingWorkers",0}};
            var opening = new LocalOpening();
            var existing = new LocalOpening();
            if (destinations.Count == 0) return opening;
            double score = double.PositiveInfinity;
            double workerScore = double.PositiveInfinity;
            foreach (NativeCityLedger.Lot factory in eligibleFactories)
            {
                if (admittedFactories.Contains(factory.Id)) continue;
                Vector3 origin = buildings[factory.Id].m_position;
                List<uint> workers;
                bool hasWorkers = assignedWorkers.TryGetValue(factory.Id, out workers);
                float straightLimit = hasWorkers ? MaximumScheduledShopDistance : MaximumShopDistanceFromFactory;
                double streetLimit = hasWorkers ? ScheduledStreetRadiusMeters : StreetRadiusMeters;
                NativeDeliveryStreetReach reach = null;
                ushort destination = 0;
                double shopDistance = streetLimit;
                foreach (NativeCityLedger.Lot shop in destinations)
                {
                    float direct = (buildings[shop.Id].m_position - origin).sqrMagnitude;
                    if (direct < 12 * 12 || direct > straightLimit * straightLimit) continue;
                    if (reach == null)
                    {
                        reach = advanceRegistration ? new NativeDeliveryStreetReach(planningRoads[factory.Id], origin, streetLimit)
                            : StreetReach(factory, origin, hasWorkers);
                        counts["factoriesWithinShopRadius"]++;
                    }
                    double distance = reach.DistanceTo(planningRoads[shop.Id], buildings[shop.Id].m_position);
                    if (distance >= shopDistance) continue;
                    destination = shop.Id;
                    shopDistance = distance;
                }
                if (destination == 0) continue;
                counts["factoriesWithReachableShop"]++;
                if (hasWorkers)
                {
                    counts["factoriesWithExistingWorkers"]++;
                    foreach (uint worker in workers)
                    {
                        if (trips.ContainsKey(worker) || firstShopByResident.ContainsKey(worker)) continue;
                        Citizen citizen = CitizenManager.instance.m_citizens.m_buffer[worker];
                        double homeDistance = WalkFromWorkerHome(worker, ref citizen, origin, reach, buildings);
                        double commuteCost = citizen.CurrentLocation == Citizen.Location.Work ? 0 : Math.Min(homeDistance, StreetRadiusMeters);
                        double workerDistance = shopDistance + commuteCost;
                        if (workerDistance >= workerScore) continue;
                        existing = new LocalOpening {Home=citizen.m_homeBuilding, Factory=factory.Id, Shop=destination,
                            Worker=worker, HomeDistance=homeDistance, ShopDistance=shopDistance};
                        workerScore = workerDistance;
                    }
                }
                if (existingOnly) continue;
                if (shopDistance >= StreetRadiusMeters || (buildings[destination].m_position - origin).sqrMagnitude >
                    MaximumShopDistanceFromFactory * MaximumShopDistanceFromFactory) continue;
                IndustrialBuildingAI industry = OpeningAI(ref buildings[factory.Id], advanceRegistration) as IndustrialBuildingAI;
                if (industry == null || VacantWorkplaces(factory.Id, industry, ref buildings[factory.Id]) <= 0) continue;
                counts["factoriesWithVacancy"]++;
                int nativeStock = Stock(industry, factory.Id, ref buildings[factory.Id]);
                foreach (var home in homes)
                {
                    Vector3 position = buildings[home.Key].m_position;
                    if ((position - origin).sqrMagnitude >= LocalEmploymentRadiusMeters * LocalEmploymentRadiusMeters) continue;
                    double distance = reach.DistanceTo(planningRoads[home.Key], position);
                    if (distance >= LocalEmploymentStreetMeters || 2 * distance + shopDistance >= score) continue;
                    opening = new LocalOpening {Home=home.Key, HomeUnit=home.Value, Factory=factory.Id, Shop=destination,
                        HomeDistance=distance, ShopDistance=shopDistance, FactoryGoods=nativeStock};
                    score = 2 * distance + shopDistance;
                }
            }
            return existing.Worker != 0 ? existing : opening;
        }

        private Dictionary<ushort, ushort> NewPlacementRoads(string ward, params HashSet<ushort>[] groups)
        {
            var roads = new Dictionary<ushort, ushort>();
            NetSegment[] network = NetManager.instance.m_segments.m_buffer;
            foreach (var group in groups)
                foreach (ushort id in group)
                {
                    NativeCityLedger.Lot site;
                    if (sites.TryGetValue(id, out site) && site.Ward == ward && NativeDeliveryStreetReach.HasSidewalk(network, site.Road))
                        roads[id] = site.Road;
                }
            return roads;
        }

        private List<NativeCityLedger.Lot> NewReceivingStores(HashSet<ushort> ids, Dictionary<ushort, ushort> roads, global::Building[] buildings)
        {
            var stores = new List<NativeCityLedger.Lot>();
            foreach (ushort id in ids)
            {
                if (!roads.ContainsKey(id)) continue;
                CommercialBuildingAI ai = OpeningAI(ref buildings[id], true) as CommercialBuildingAI;
                if (ai != null && ai.m_incomingResource == TransferManager.TransferReason.Goods) stores.Add(sites[id]);
            }
            return stores;
        }

        private static BuildingAI OpeningAI(ref global::Building building, bool advanceRegistration)
        {
            if (!advanceRegistration) return ActiveAI(ref building);
            return (building.m_flags & (global::Building.Flags.Created | global::Building.Flags.Deleted)) == global::Building.Flags.Created &&
                building.Info != null ? building.Info.m_buildingAI : null;
        }

        private double WalkFromWorkerHome(uint worker, ref Citizen citizen, Vector3 factory,
            NativeDeliveryStreetReach reach, global::Building[] buildings)
        {
            ushort home = citizen.m_homeBuilding;
            Vector3 position = buildings[home].m_position;
            if ((position - factory).sqrMagnitude > MaximumFactoryDistanceFromHome * MaximumFactoryDistanceFromHome)
                return double.PositiveInfinity;
            ushort street;
            if (!streetByBuilding.TryGetValue(home, out street) &&
                !TryResolveHomeStreet(worker, ref citizen, ref buildings[home], out position, out street))
                return double.PositiveInfinity;
            return reach.DistanceTo(street, position);
        }

        internal void RegisterLocalOpening(uint id, LocalOpening opening, DateTime now)
        {
            if (opening.Worker == 0) factoryHires.Add(id, opening.Factory);
            firstShopByResident.Add(id, new FirstDelivery {Factory=opening.Factory, Shop=opening.Shop,
                ShopGeneration=BuildingManager.instance.m_buildings.m_buffer[opening.Shop].m_buildIndex, Until=now.AddDays(30),
                ExistingWorker=opening.Worker != 0});
        }

        private static bool TryResolveHomeStreet(uint id, ref Citizen citizen, ref global::Building home,
            out Vector3 entrance, out ushort street)
        {
            entrance = Vector3.zero;
            street = 0;
            BuildingAI homeAI = ActiveAI(ref home);
            CitizenInfo resident = citizen.GetCitizenInfo(id);
            if (homeAI == null || resident == null) return false;
            Vector3 target;
            Vector2 direction;
            CitizenInstance.Flags flags;
            var random = new ColossalFramework.Math.Randomizer(id);
            homeAI.CalculateUnspawnPosition(citizen.m_homeBuilding, ref home, ref random, resident, 0,
                out entrance, out target, out direction, out flags);
            PathUnit.Position path;
            if (!PathManager.FindPathPosition(entrance, ItemClass.Service.Road, NetInfo.LaneType.Pedestrian,
                    VehicleInfo.VehicleType.None, VehicleInfo.VehicleCategory.None, false, false, 40, false, true, out path))
                return false;
            street = path.m_segment;
            return street != 0;
        }

        private bool RejectHire(string reason)
        {
            AddWardCount(hiringRejections, reason);
            return false;
        }

        private static int VacantWorkplaces(ushort id, IndustrialBuildingAI ai, ref global::Building building)
        {
            int low, first, second, high;
            ai.CalculateWorkplaceCount((ItemClass.Level)building.m_level, new ColossalFramework.Math.Randomizer(id),
                building.Width, building.Length, out low, out first, out second, out high);
            int vacancies = low + first + second + high;
            CitizenUnit[] units = CitizenManager.instance.m_units.m_buffer;
            uint unit = building.m_citizenUnits;
            for (int i = 0; i < 2048 && unit > 0 && unit < units.Length; i++)
            {
                CitizenUnit data = units[unit];
                unit = data.m_nextUnit;
                if ((data.m_flags & CitizenUnit.Flags.Work) == 0) continue;
                for (int slot = 0; slot < 5; slot++)
                    if (data.GetCitizen(slot) != 0) vacancies--;
            }
            return vacancies;
        }

        internal void PrioritizeOnsiteDeliveries(IEnumerable<string> wards)
        {
            onsitePriorityWards.UnionWith(wards);
        }

        internal bool Dispatch(uint id, ref Citizen citizen, DateTime gameTime)
        {
            if (citizen.CurrentLocation != Citizen.Location.Work) return false;
            bool normalHours = gameTime.Hour >= 7 && gameTime.Hour < 17;
            bool priorityAtWork = onsitePriorityWards.Count != 0;
            if (!normalHours && !priorityAtWork) return false;
            NativeCityLedger.Lot supplier;
            DateTime next;
            if (trips.ContainsKey(id) ||
                !sites.TryGetValue(citizen.m_workBuilding, out supplier) || supplier.Zone != ItemClass.Zone.Industrial ||
                reservedFactories.Contains(supplier.Id) || (retryAfter.TryGetValue(id, out next) && next > gameTime)) return false;
            workersSeenAtFactory++;
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            BuildingAI industry = ActiveAI(ref buildings[supplier.Id]);
            if (!(industry is IndustrialBuildingAI) || buildings[supplier.Id].m_buildIndex != supplier.BuildIndex ||
                !streetByBuilding.ContainsKey(supplier.Id) ||
                Stock(industry, supplier.Id, ref buildings[supplier.Id]) < ParcelSize) return false;
            Vector3 source = buildings[supplier.Id].m_position;
            ushort firstShop = 0;
            FirstDelivery scheduled;
            if (firstShopByResident.TryGetValue(id, out scheduled))
            {
                if (scheduled.Factory == supplier.Id && scheduled.Until > gameTime &&
                    buildings[scheduled.Shop].m_buildIndex == scheduled.ShopGeneration &&
                    (buildings[scheduled.Shop].m_flags & global::Building.Flags.Created) != 0)
                    firstShop = scheduled.Shop;
                else firstShopByResident.Remove(id);
            }
            bool scheduledShipment = firstShop != 0 && scheduled.ExistingWorker;
            DeliveryChoice choice = new DeliveryChoice();
            if (priorityAtWork)
            {
                stockedFactoriesWithWorkerOnsite.Add(supplier.Id);
                choice = SelectShop(supplier, StreetReach(supplier, source, true), gameTime, false, 0, true, true);
            }
            bool onsiteShipment = choice.Shop.Id != 0;
            if (!onsiteShipment && normalHours)
                choice = SelectShop(supplier, StreetReach(supplier, source, scheduledShipment), gameTime, false, firstShop, scheduledShipment, false);
            NativeCityLedger.Lot target = choice.Shop;
            if (target.Id == 0)
            {
                blockedByStreetReach++;
                return false;
            }
            uint frame = SimulationManager.instance.m_currentFrameIndex;
            Trip trip = new Trip {Supplier=supplier.Id, Target=target.Id,
                SupplierStreet=streetByBuilding[supplier.Id], TargetStreet=streetByBuilding[target.Id],
                SupplierGeneration=buildings[supplier.Id].m_buildIndex, TargetGeneration=buildings[target.Id].m_buildIndex,
                SupplierWard=supplier.Ward, TargetWard=target.Ward,
                Frame=frame, LastProgressFrame=frame,
                Demand=choice.Request, OnsitePriority=onsiteShipment};
            ReserveTrip(id, trip);
            if (!StartDelivery(id, ref citizen, supplier.Id, target.Id))
            {
                if (trips.ContainsKey(id)) FailLeg(id, trip, 0, "native path rejected at departure", gameTime);
                return false;
            }
            trip.NativeRouteAccepted = true;
            started++;
            if (onsiteShipment)
            {
                AddWardCount(onsiteStartsByWard, target.Ward);
                if (onsiteDispatches.Count == 48) onsiteDispatches.Dequeue();
                onsiteDispatches.Enqueue(new Dictionary<string, object> {{"android",id}, {"factory",supplier.Id},
                    {"shop",target.Id}, {"ward",target.Ward}, {"pathId",CurrentPath(CitizenManager.instance,id)},
                    {"gameTime",gameTime.ToString("s")}});
            }
            return true;
        }

        private NativeDeliveryStreetReach StreetReach(NativeCityLedger.Lot supplier, Vector3 position, bool scheduled)
        {
            var cache = scheduled ? scheduledFactoryReach : factoryReach;
            NativeDeliveryStreetReach reach;
            if (!cache.TryGetValue(supplier.Id, out reach))
            {
                reach = new NativeDeliveryStreetReach(streetByBuilding[supplier.Id], position,
                    scheduled ? ScheduledStreetRadiusMeters : StreetRadiusMeters);
                cache.Add(supplier.Id, reach);
            }
            return reach;
        }

        private DeliveryChoice SelectShop(NativeCityLedger.Lot supplier, NativeDeliveryStreetReach reach, DateTime gameTime,
            bool forNewHire, ushort firstShop, bool scheduled, bool onsiteOnly)
        {
            DeliveryChoice choice = new DeliveryChoice();
            double best = double.PositiveInfinity;
            uint frame = SimulationManager.instance.m_currentFrameIndex;
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            Vector3 source = buildings[supplier.Id].m_position;
            float straightLimit = scheduled ? MaximumScheduledShopDistance : MaximumShopDistanceFromFactory;
            double streetLimit = scheduled ? ScheduledStreetRadiusMeters : StreetRadiusMeters;
            foreach (NativeCityLedger.Lot shop in shops)
            {
                if (firstShop != 0 && shop.Id != firstShop) continue;
                if (onsiteOnly && (!onsitePriorityWards.Contains(shop.Ward) || deliveredByWard.ContainsKey(shop.Ward) ||
                    activeDeliveriesByWard.ContainsKey(shop.Ward))) continue;
                float distance = (buildings[shop.Id].m_position - source).sqrMagnitude;
                ushort street;
                if (reservedShops.Contains(shop.Id) || distance < 12 * 12 || distance > straightLimit * straightLimit ||
                    !streetByBuilding.TryGetValue(shop.Id, out street) || buildings[shop.Id].m_buildIndex != shop.BuildIndex ||
                    IsRouteCoolingDown(supplier.Id, shop.Id, gameTime)) continue;
                double distanceAlongStreets = reach.DistanceTo(street, buildings[shop.Id].m_position);
                if (distanceAlongStreets >= streetLimit) continue;
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
                if (forNewHire)
                {
                    int hires;
                    hiredForWard.TryGetValue(shop.Ward, out hires);
                    if (deliveredHere + inFlightHere >= LocalHiresPerReceivingWard || hires >= LocalHiresPerReceivingWard) continue;
                }
                double score = distanceAlongStreets + Math.Min(3, deliveredHere + inFlightHere) * ExistingDeliveryPenaltyMeters +
                    (ParcelSize - Math.Min(ParcelSize, space));
                if (deliveredHere + inFlightHere != 0) score += StreetRadiusMeters;
                if (hasRequest) score -= RecentDemandBonusMeters;
                if (score >= best) continue;
                choice.Shop = shop;
                choice.Request = hasRequest ? request : new GoodsRequest();
                best = score;
            }
            return choice;
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

        private static bool StartDelivery(uint id, ref Citizen citizen, ushort from, ushort to)
        {
            ushort previousVisit = citizen.m_visitBuilding;
            citizen.SetVisitplace(id, to, 0);
            if (citizen.m_visitBuilding != to) return false;
            if (!AndroidLifePilot.TryStartNativeMovement(id, ref citizen, from, to))
            {
                if (citizen.m_visitBuilding == to) citizen.SetVisitplace(id, previousVisit, 0);
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

        private static void BeforePathFailure(ushort __0, ref CitizenInstance __1)
        {
            AndroidLifePilot.ObserveCommutePathFailure(__0, __1);
            NativeAndroidDeliveries delivery = active;
            Trip trip;
            if (delivery == null || !delivery.trips.TryGetValue(__1.m_citizen, out trip) ||
                __1.m_targetBuilding != trip.Target) return;
            trip.NativePathFailures++;
            delivery.nativePathFailureCallbacks++;
        }

        private void ObserveArrival(Arrival arrival, bool success)
        {
            uint id = arrival.Citizen;
            if (arrival.Target != 0 && AndroidLifePilot.IsAndroidCitizen(id))
                AndroidLifePilot.ObserveNativeWorkArrival(id, arrival.Target, success);
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
            if (arrival.Target != trip.Target) return;
            if (!success)
            {
                FailLeg(id, trip, arrival.Path, "native arrival failed", SimulationManager.instance.m_currentGameTime);
                return;
            }
            TransferOnArrival(id, arrival.Target, "native arrival callback");
        }

        private static void AfterCitizenStep()
        {
            NativeAndroidDeliveries delivery = active;
            if (delivery == null) return;
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

        private void FailLeg(uint id, Trip trip, uint path, string reason, DateTime now)
        {
            failedLegs++;
            AddWardCount(failuresByReason, reason);
            if (failures.Count == 12) failures.Dequeue();
            failures.Enqueue(new Dictionary<string, object> {{"android",id},
                {"from",trip.Supplier}, {"target",trip.Target}, {"pathId",path}, {"reason",reason},
                {"nativePathFailureCallbacks",trip.NativePathFailures}});
            failedRoutes[RouteKey(trip.Supplier, trip.Target)] = now.AddHours(12);
            retryAfter[id] = now.AddHours(2);
            EndTrip(id);
        }

        private static bool HasPlannedBuildings(Trip trip)
        {
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            return trip.Supplier < buildings.Length && trip.Target < buildings.Length &&
                (buildings[trip.Supplier].m_flags & global::Building.Flags.Created) != 0 &&
                (buildings[trip.Target].m_flags & global::Building.Flags.Created) != 0 &&
                buildings[trip.Supplier].m_buildIndex == trip.SupplierGeneration && buildings[trip.Target].m_buildIndex == trip.TargetGeneration;
        }

        private static bool IsAtDeliveryStore(Citizen citizen, ushort target)
        {
            return citizen.CurrentLocation == Citizen.Location.Visit && citizen.m_visitBuilding == target;
        }

        private void TransferOnArrival(uint id, ushort target, string arrivalEvidence)
        {
            Trip trip;
            if (!trips.TryGetValue(id, out trip) || target != trip.Target) return;
            Citizen citizen = CitizenManager.instance.m_citizens.m_buffer[id];
            if (!IsAtDeliveryStore(citizen, target)) return;
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
            FirstDelivery planned;
            bool plannedDelivery = firstShopByResident.TryGetValue(id, out planned) && planned.Factory == trip.Supplier && planned.Shop == target;
            if (plannedDelivery) firstShopByResident.Remove(id);
            delivered++;
            goods += credit;
            AddWardCount(deliveredByWard, trip.TargetWard);
            if (trip.OnsitePriority) AddWardCount(onsiteDeliveriesByWard, trip.TargetWard);
            if (recent.Count == 64) recent.Dequeue();
            recent.Enqueue(new Dictionary<string, object> {{"android",id}, {"from",trip.Supplier}, {"to",target},
                {"ward",trip.TargetWard}, {"sourceWard",trip.SupplierWard}, {"stockAdded",credit}, {"factoryBefore",factoryBefore},
                {"factoryAccessStreet",trip.SupplierStreet}, {"shopAccessStreet",trip.TargetStreet},
                {"factoryAfter",Stock(source, trip.Supplier, ref buildings[trip.Supplier])}, {"shopBefore",shopBefore}, {"nativeRequest",RequestSnapshot(trip.Demand)},
                {"shopAfter",Stock(destination, target, ref buildings[target])}, {"localHire",IsLocalHire(id, trip.Supplier)},
                {"firstDeliveryAfterAdmission",plannedDelivery && !planned.ExistingWorker},
                {"firstDeliveryFromExistingWorker",plannedDelivery && planned.ExistingWorker}, {"onsitePriority",trip.OnsitePriority},
                {"departureFrame",trip.Frame}, {"restoredAfterLoad",trip.RestoredAfterLoad},
                {"arrivalEvidence",arrivalEvidence},
                {"gameTime",SimulationManager.instance.m_currentGameTime.ToString("s")}});
        }

        private bool IsLocalHire(uint id, ushort supplier)
        {
            ushort factory;
            return factoryHires.TryGetValue(id, out factory) && factory == supplier;
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

        private void ReserveTrip(uint id, Trip trip)
        {
            trips.Add(id, trip);
            reservedShops.Add(trip.Target);
            reservedFactories.Add(trip.Supplier);
            AddWardCount(activeDeliveriesByWard, trip.TargetWard);
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

        internal void ForgetCitizen(uint id)
        {
            EndTrip(id);
            factoryHires.Remove(id);
            firstShopByResident.Remove(id);
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
                inFlight.Add(new Dictionary<string, object> {{"android",live.Key}, {"from",leg.Supplier},
                    {"target",leg.Target}, {"deliveryBuilding",leg.Target}, {"destinationWard",leg.TargetWard},
                    {"onsitePriority",leg.OnsitePriority},
                    {"factoryAccessStreet",leg.SupplierStreet}, {"shopAccessStreet",leg.TargetStreet},
                    {"frame",leg.Frame}, {"lastProgressFrame",leg.LastProgressFrame}, {"nativeRequest",RequestSnapshot(leg.Demand)},
                    {"position",leg.HasPosition ? new float[] {leg.LastPosition.x, leg.LastPosition.y, leg.LastPosition.z} : null}});
            }
            return new Dictionary<string, object> {{"active",trips.Count}, {"started",started}, {"delivered",delivered},
                {"restoredTrips",restoredTrips}, {"droppedSavedTrips",droppedSavedTrips},
                {"stockAdded",goods}, {"stockedFactories",stockedFactories}, {"shopsWithSpace",shopsWithSpace},
                {"onsitePriorityWards",new List<string>(onsitePriorityWards).ToArray()},
                {"stockedFactoriesWithWorkerOnsite",new List<ushort>(stockedFactoriesWithWorkerOnsite).ToArray()},
                {"onsiteStartsByWard",onsiteStartsByWard}, {"onsiteDeliveriesByWard",onsiteDeliveriesByWard},
                {"recentOnsiteDispatches",onsiteDispatches.ToArray()},
                {"stockedFactoriesByWard",stockedFactoriesByWard}, {"activeShopsByWard",activeShopsByWard}, {"shopsWithSpaceByWard",shopsWithSpaceByWard},
                {"shopInventoryByWard",shopInventory},
                {"nativeAccessRoadDiffersFromLedgerByWard",differentEntranceRoadsByWard},
                {"noNativeAccessSidewalkByWard",missingEntranceSidewalksByWard},
                {"localHires",localHires.ToArray()}, {"localHiresByReceivingWard",hiredForWard}, {"localHiringRejections",hiringRejections},
                {"observedGoodsOffers",observedGoodsOffers}, {"requestingShopsByWard",requestingShopsByWard}, {"activeDeliveriesByWard",activeDeliveriesByWard},
                {"workersSeenAtFactory",workersSeenAtFactory}, {"nativePathFailureCallbacks",nativePathFailureCallbacks},
                {"failedLegs",failedLegs}, {"failuresByReason",failuresByReason},
                {"expiredLegs",expiredLegs}, {"deliveriesByWard",deliveredByWard},
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
