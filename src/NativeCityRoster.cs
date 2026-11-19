using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    public sealed class NativeCityRoster : MonoBehaviour
    {
        private sealed class Ward
        {
            public int Lots, Homes, Workplaces, HumanResidents, AndroidResidents, HumanWorkers, AndroidWorkers;
            public int HumanCommutes, AndroidCommutes;
        }

        private sealed class Capacity
        {
            public int ReadyHomes, EmptyHouseholds, HomesWithVacancy;
            public int AndroidOnlyHouseholds, MixedHouseholds, SoloAndroidHouseholds;
            public int FreePlacesInAndroidOnlyHouseholds, FreePlacesInMixedHouseholds;
            public readonly Dictionary<string, JobCapacity> Jobs = new Dictionary<string, JobCapacity>(StringComparer.Ordinal);
        }

        private sealed class JobCapacity
        {
            public int Buildings, Active, Positions, Workers, Vacancies, NoWorkerWarning;
        }

        internal static object AuditCapacity()
        {
            NativeCityLedger.Road[] roads;
            NativeCityLedger.Lot[] lots;
            NativeCityLedger.Snapshot(out roads, out lots);
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            CitizenManager citizens = CitizenManager.instance;
            CitizenUnit[] units = citizens.m_units.m_buffer;
            var wards = new SortedDictionary<string, Capacity>(StringComparer.Ordinal);
            foreach (NativeCityLedger.Lot lot in lots)
            {
                if (!NativeCityLedger.HasActiveZone(lot)) continue;
                global::Building building = buildings[lot.Id];
                if (building.Info == null || (building.m_flags & (global::Building.Flags.Deleted | global::Building.Flags.Abandoned)) != 0)
                    continue;
                ItemClass.Service service = building.Info.GetService();
                bool home = service == ItemClass.Service.Residential;
                if (home && !AndroidLifePilot.HasResidentialUtilities(lot.Id, building)) continue;
                PrivateBuildingAI employer = building.Info.m_buildingAI as PrivateBuildingAI;
                if (!home && (employer == null || (service != ItemClass.Service.Office && service != ItemClass.Service.Commercial &&
                    service != ItemClass.Service.Industrial))) continue;
                Capacity ward;
                if (!wards.TryGetValue(lot.Ward, out ward))
                {
                    ward = new Capacity();
                    wards.Add(lot.Ward, ward);
                }
                int seats = 0;
                uint next = building.m_citizenUnits;
                for (int step = 0; step < 2048 && next != 0 && next < units.Length; step++)
                {
                    CitizenUnit unit = units[next];
                    next = unit.m_nextUnit;
                    if (unit.m_building != lot.Id) continue;
                    if (home)
                    {
                        if ((unit.m_flags & CitizenUnit.Flags.Home) == 0) continue;
                        if (AndroidLifePilot.Empty(unit)) seats++;
                        else AuditOccupiedHousehold(unit, lot.Id, citizens, ward);
                        continue;
                    }
                    if ((unit.m_flags & CitizenUnit.Flags.Work) == 0) continue;
                    for (int slot = 0; slot < 5; slot++)
                    {
                        uint id = unit.GetCitizen(slot);
                        if (id == 0 || id >= citizens.m_citizens.m_buffer.Length) continue;
                        Citizen person = citizens.m_citizens.m_buffer[id];
                        if ((person.m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead | Citizen.Flags.Student)) == Citizen.Flags.Created &&
                            person.m_workBuilding == lot.Id) seats++;
                    }
                }
                if (home)
                {
                    ward.ReadyHomes++;
                    ward.EmptyHouseholds += seats;
                    if (seats > 0) ward.HomesWithVacancy++;
                    continue;
                }
                string key = service.ToString();
                JobCapacity jobs;
                if (!ward.Jobs.TryGetValue(key, out jobs))
                {
                    jobs = new JobCapacity();
                    ward.Jobs.Add(key, jobs);
                }
                int low, first, second, high;
                employer.CalculateWorkplaceCount((ItemClass.Level)building.m_level, new ColossalFramework.Math.Randomizer(lot.Id),
                    building.Width, building.Length, out low, out first, out second, out high);
                int positions = low + first + second + high;
                jobs.Buildings++;
                if ((building.m_flags & global::Building.Flags.Active) != 0) jobs.Active++;
                jobs.Positions += positions;
                jobs.Workers += seats;
                jobs.Vacancies += Math.Max(0, positions - seats);
                if (!(building.m_problems & (Notification.Problem1.NoWorkers | Notification.Problem1.NoEducatedWorkers)).IsNone)
                    jobs.NoWorkerWarning++;
            }
            var report = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var item in wards)
            {
                Capacity ward = item.Value;
                var jobs = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var entry in ward.Jobs)
                {
                    JobCapacity use = entry.Value;
                    jobs.Add(entry.Key, new Dictionary<string, object> {
                        {"registeredSites",use.Buildings}, {"activeSites",use.Active}, {"nativeCapacity",use.Positions},
                        {"workersInNativeWorkUnits",use.Workers}, {"vacanciesFromNativeCapacity",use.Vacancies},
                        {"sitesWithWorkerWarning",use.NoWorkerWarning}
                    });
                }
                report.Add(item.Key, new Dictionary<string, object> {
                    {"servicedResidentialSites",ward.ReadyHomes}, {"vacantHouseholds",ward.EmptyHouseholds},
                    {"sitesWithVacantHouseholds",ward.HomesWithVacancy}, {"workByUse",jobs},
                    {"androidHouseholds",new Dictionary<string, object> {
                        {"androidOnly",ward.AndroidOnlyHouseholds}, {"mixedWithHumans",ward.MixedHouseholds},
                        {"singleAndroid",ward.SoloAndroidHouseholds},
                        {"emptyPlacesInAndroidOnly",ward.FreePlacesInAndroidOnlyHouseholds},
                        {"emptyPlacesInMixed",ward.FreePlacesInMixedHouseholds}
                    }}
                });
            }
            return report;
        }

        private static void AuditOccupiedHousehold(CitizenUnit unit, ushort address, CitizenManager citizens, Capacity ward)
        {
            const int capacity = 5;
            int androids = 0, humans = 0, free = 0;
            for (int slot = 0; slot < capacity; slot++)
            {
                uint id = unit.GetCitizen(slot);
                if (id == 0) { free++; continue; }
                if (id >= citizens.m_citizens.m_buffer.Length) continue;
                Citizen person = citizens.m_citizens.m_buffer[id];
                if ((person.m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead)) != Citizen.Flags.Created ||
                    person.m_homeBuilding != address) continue;
                if (AndroidLifePilot.IsAndroidCitizen(id)) androids++;
                else humans++;
            }
            if (androids == 0 || androids + humans + free != capacity) return;
            if (humans == 0)
            {
                ward.AndroidOnlyHouseholds++;
                ward.FreePlacesInAndroidOnlyHouseholds += free;
                if (androids == 1) ward.SoloAndroidHouseholds++;
                return;
            }
            ward.MixedHouseholds++;
            ward.FreePlacesInMixedHouseholds += free;
        }

        private readonly string runId = Guid.NewGuid().ToString("N");
        private float nextRead;
        private int queued;
        private string report;
        private volatile bool stopping;
        private int reboundLots;
        private readonly List<Dictionary<string, object>> recentLotRebindings = new List<Dictionary<string, object>>();

        public void Update()
        {
            string ready = Interlocked.Exchange<string>(ref report, null);
            if (ready != null)
            {
                try
                {
                    string path = Path.Combine(NativeCityRoadBuilder.Root, "native-city-roster.json");
                    string pending = path + ".tmp";
                    File.WriteAllText(pending, ready, new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(pending, path, null);
                    else File.Move(pending, path);
                }
                catch (Exception error) { Debug.LogException(error); }
            }
            if (Time.realtimeSinceStartup < nextRead || Interlocked.CompareExchange(ref queued, 1, 0) != 0)
                return;
            nextRead = Time.realtimeSinceStartup + 8f;
            SimulationManager.instance.AddAction(delegate {
                try
                {
                    if (!stopping)
                        Interlocked.Exchange<string>(ref report, Sample());
                }
                catch (Exception error) { Debug.LogException(error); }
                finally { Interlocked.Exchange(ref queued, 0); }
            });
        }

        private string Sample()
        {
            NativeCityLedger.Road[] roads;
            NativeCityLedger.Lot[] lots;
            NativeCityLedger.Snapshot(out roads, out lots);
            int activeRoads = 0;
            HashSet<ushort> connectedRoads = new HashSet<ushort>();
            foreach (NativeCityLedger.Road road in roads)
                if (NativeCityLedger.HasActiveRoad(road))
                {
                    activeRoads++;
                    connectedRoads.Add(road.Id);
                }
            List<Dictionary<string, object>> rebindings = NativeCityLotRebinding.Apply(lots, connectedRoads);
            if (rebindings.Count > 0)
            {
                reboundLots += rebindings.Count;
                recentLotRebindings.AddRange(rebindings);
                if (recentLotRebindings.Count > 64) recentLotRebindings.RemoveRange(0, recentLotRebindings.Count - 64);
                NativeCityLedger.Snapshot(out roads, out lots);
            }
            SortedDictionary<string, Ward> wards = new SortedDictionary<string, Ward>(StringComparer.Ordinal);
            HashSet<ushort> activeSites = new HashSet<ushort>();
            HashSet<string> activeGroups = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> operationalGroups = new HashSet<string>(StringComparer.Ordinal);
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            const NativeBuilding.Flags operating = NativeBuilding.Flags.Completed | NativeBuilding.Flags.Active;
            const NativeBuilding.Flags unusable = NativeBuilding.Flags.Deleted | NativeBuilding.Flags.Abandoned |
                NativeBuilding.Flags.Collapsed | NativeBuilding.Flags.BurnedDown;
            int operationalLots = 0, abandonedLots = 0, constructionLots = 0, inactiveLots = 0;
            int missingLots = 0;
            foreach (NativeCityLedger.Lot lot in lots)
            {
                if (!connectedRoads.Contains(lot.Road) || !NativeCityLedger.HasActiveZone(lot))
                {
                    missingLots++;
                    continue;
                }
                activeSites.Add(lot.Id);
                activeGroups.Add(lot.Group);
                NativeBuilding.Flags flags = buildings[lot.Id].m_flags;
                if ((flags & (operating | unusable)) == operating)
                {
                    operationalLots++;
                    operationalGroups.Add(lot.Group);
                }
                else if ((flags & NativeBuilding.Flags.Abandoned) != 0) abandonedLots++;
                else if ((flags & (NativeBuilding.Flags.Completed | unusable)) == 0) constructionLots++;
                else inactiveLots++;
                Ward row;
                string label = String.IsNullOrEmpty(lot.Ward) ? "unassigned" : lot.Ward;
                if (!wards.TryGetValue(label, out row))
                    wards.Add(label, row = new Ward());
                row.Lots++;
            }
            foreach (NativeCityLedger.Lot lot in lots)
            {
                if (!activeSites.Contains(lot.Id)) continue;
                string label = String.IsNullOrEmpty(lot.Ward) ? "unassigned" : lot.Ward;
                ObserveLot(lot, wards[label], activeSites);
            }
            Dictionary<string, object> byWard = new Dictionary<string, object>();
            int workingWards = 0;
            foreach (KeyValuePair<string, Ward> entry in wards)
            {
                Ward row = entry.Value;
                if (entry.Key != "unassigned" && row.HumanResidents > 0 && row.AndroidResidents > 0 &&
                    row.HumanWorkers > 0 && row.AndroidWorkers > 0)
                    workingWards++;
                byWard.Add(entry.Key, new Dictionary<string, object> {
                    {"activeSites", row.Lots}, {"homeUnits", row.Homes}, {"workUnits", row.Workplaces},
                    {"humanResidents", row.HumanResidents}, {"androidResidents", row.AndroidResidents},
                    {"humanWorkers", row.HumanWorkers}, {"androidWorkers", row.AndroidWorkers},
                    {"humanCommutesOnNativePath", row.HumanCommutes}, {"androidCommutesOnNativePath", row.AndroidCommutes}
                });
            }
            List<string> groupIds = new List<string>(activeGroups);
            groupIds.Sort(StringComparer.Ordinal);
            List<string> operationalGroupIds = new List<string>(operationalGroups);
            operationalGroupIds.Sort(StringComparer.Ordinal);
            return JsonData.Stringify(new Dictionary<string, object> {
                {"runId",runId}, {"city",SimulationManager.instance.m_metaData.m_CityName},
                {"savedRoadReferences",roads.Length}, {"activeNativeRoads",activeRoads},
                {"savedParcelReferences",lots.Length}, {"activeNativeZonedSites",activeSites.Count},
                {"activeNativeSiteGroups",groupIds},
                {"operationalNativeZonedSites",operationalLots}, {"operationalNativeSiteGroups",operationalGroupIds},
                {"nativeZonedSiteStatuses",new Dictionary<string, int> {
                    {"operational",operationalLots}, {"underConstruction",constructionLots},
                    {"abandoned",abandonedLots}, {"inactive",inactiveLots}
                }},
                {"invalidOrLostNativeSites",missingLots}, {"wardsWithHumanAndAndroidPopulationAndJobs",workingWards},
                {"reboundNativeLotsSinceLoad",reboundLots}, {"recentLotRebindings",recentLotRebindings},
                {"nativeTraffic",CountTraffic()},
                {"wards",byWard}, {"utc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)}
            });
        }

        private static Dictionary<string, object> CountTraffic()
        {
            int cars = 0, trucks = 0, buses = 0, other = 0, withPath = 0;
            var policeVehicles = new List<object>();
            Vehicle[] vehicles = VehicleManager.instance.m_vehicles.m_buffer;
            Vehicle.Flags required = Vehicle.Flags.Created | Vehicle.Flags.Spawned;
            for (int i = 1; i < vehicles.Length; i++)
            {
                Vehicle vehicle = vehicles[i];
                if ((vehicle.m_flags & required) != required || vehicle.m_leadingVehicle != 0 || vehicle.Info == null) continue;
                VehicleAI ai = vehicle.Info.m_vehicleAI;
                if (ai is PassengerCarAI) cars++;
                else if (ai is CargoTruckAI) trucks++;
                else if (ai is BusAI) buses++;
                else
                {
                    other++;
                    if (ai is PoliceCarAI && vehicle.m_path != 0)
                        policeVehicles.Add(new Dictionary<string, object> {
                            {"vehicleId",i}, {"station",vehicle.m_sourceBuilding}, {"destination",vehicle.m_targetBuilding},
                            {"pathId",vehicle.m_path}, {"position",new float[] {vehicle.m_frame0.m_position.x, vehicle.m_frame0.m_position.z}}
                        });
                    continue;
                }
                if (vehicle.m_path != 0) withPath++;
            }
            return new Dictionary<string, object> {{"passengerCars",cars}, {"cargoTrucks",trucks},
                {"buses",buses}, {"otherVehicles",other}, {"roadVehiclesWithNativePath",withPath}, {"policeVehiclesWithNativePath",policeVehicles}};
        }

        private static void ObserveLot(NativeCityLedger.Lot lot, Ward ward, HashSet<ushort> activeSites)
        {
            global::Building building = BuildingManager.instance.m_buildings.m_buffer[lot.Id];
            CitizenManager citizens = CitizenManager.instance;
            CitizenUnit[] units = citizens.m_units.m_buffer;
            uint next = building.m_citizenUnits;
            for (int step = 0; step < 2048 && next != 0 && next < units.Length; step++)
            {
                CitizenUnit unit = units[next];
                next = unit.m_nextUnit;
                bool home = (unit.m_flags & CitizenUnit.Flags.Home) != 0;
                bool job = (unit.m_flags & CitizenUnit.Flags.Work) != 0;
                if ((!home && !job) || unit.m_building != lot.Id) continue;
                if (home) ward.Homes++;
                if (job) ward.Workplaces++;
                for (int slot = 0; slot < 5; slot++)
                {
                    uint id = unit.GetCitizen(slot);
                    if (id == 0 || id >= citizens.m_citizens.m_buffer.Length) continue;
                    Citizen citizen = citizens.m_citizens.m_buffer[id];
                    if ((citizen.m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead | Citizen.Flags.Tourist)) != Citizen.Flags.Created)
                        continue;
                    bool android = AndroidLifePilot.IsAndroidCitizen(id);
                    if (home && citizen.m_homeBuilding == lot.Id)
                    {
                        if (android) ward.AndroidResidents++; else ward.HumanResidents++;
                        if (citizen.m_instance == 0 || citizen.m_instance >= citizens.m_instances.m_buffer.Length ||
                            citizen.m_workBuilding == 0 || (citizen.m_flags & Citizen.Flags.Student) != 0)
                            continue;
                        CitizenInstance instance = citizens.m_instances.m_buffer[citizen.m_instance];
                        if (instance.m_citizen == id && instance.m_path != 0 && instance.m_sourceBuilding == lot.Id &&
                            instance.m_targetBuilding == citizen.m_workBuilding && activeSites.Contains(instance.m_targetBuilding))
                        {
                            if (android) ward.AndroidCommutes++; else ward.HumanCommutes++;
                        }
                    }
                    if (job && citizen.m_workBuilding == lot.Id && (citizen.m_flags & Citizen.Flags.Student) == 0)
                    {
                        if (android) ward.AndroidWorkers++; else ward.HumanWorkers++;
                    }
                }
            }
        }

        public void OnDestroy() { stopping = true; }
    }
}
