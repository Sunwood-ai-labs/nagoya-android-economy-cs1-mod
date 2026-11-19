using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    public sealed class NativeCityRoster : MonoBehaviour
    {
        private sealed class Ward
        {
            public int Lots, Homes, Workplaces, HumanResidents, AndroidResidents, HumanWorkers, AndroidWorkers;
            public int HumanCommutes, AndroidCommutes;
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
            return JsonData.Stringify(new Dictionary<string, object> {
                {"runId",runId}, {"city",SimulationManager.instance.m_metaData.m_CityName},
                {"savedRoadReferences",roads.Length}, {"activeNativeRoads",activeRoads},
                {"savedParcelReferences",lots.Length}, {"activeNativeZonedSites",activeSites.Count},
                {"activeNativeSiteGroups",groupIds},
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
