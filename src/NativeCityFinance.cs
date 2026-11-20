using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeCityFinance : MonoBehaviour
    {
        private static readonly string Root = NativeCityRoadBuilder.Root;
        private string city, runId;
        private DateTime lastRequest;
        private float nextRead;
        private int queued;
        private volatile bool stopped;
        private NativeWasteObservation wasteWatch;

        public void Start()
        {
            if (!File.Exists(Path.Combine(Root, "enabled.txt"))) { enabled = false; return; }
            city = SimulationManager.instance.m_metaData.m_CityName;
            runId = Guid.NewGuid().ToString("N");
            Write("finance-runtime.json", Status(null, "ready", null, null));
        }

        public void Update()
        {
            if (Time.realtimeSinceStartup < nextRead || Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            nextRead = Time.realtimeSinceStartup + 1;
            bool scheduled = false;
            string commandId = null;
            try
            {
                string path = Path.Combine(Root, "finance-command.json");
                DateTime changed = File.GetLastWriteTimeUtc(path);
                if (changed == lastRequest) return;
                lastRequest = changed;
                if (!File.Exists(path)) return;
                if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Finance request is too large");
                var request = (Dictionary<string, object>)JsonData.Parse(File.ReadAllText(path));
                if ((string)request["runId"] != runId) return;
                commandId = (string)request["commandId"];
                if (!Regex.IsMatch(commandId, @"\A[a-f0-9]{32}\z") || (string)request["cityName"] != city)
                    throw new InvalidDataException("Finance request identity differs from the loaded city");
                uint build = Convert.ToUInt32(request["buildIndex"]);
                int segments = Convert.ToInt32(request["segments"]);
                SimulationManager.instance.AddAction(delegate { Collect(commandId, build, segments, request); });
                scheduled = true;
            }
            catch (Exception error) { Write("finance-runtime.json", Status(commandId, "rejected", null, error.Message)); }
            finally { if (!scheduled) Interlocked.Exchange(ref queued, 0); }
        }

        private void Collect(string id, uint build, int segments, Dictionary<string, object> request)
        {
            const ItemClass.Service electricity = ItemClass.Service.Electricity;
            const ItemClass.Service garbage = ItemClass.Service.Garbage;
            EconomyManager economy = null;
            ItemClass.Service changeService = ItemClass.Service.None;
            bool changed = false;
            bool startedWatch = false;
            int day = 0, night = 0;
            NativeWaterQuality.IntakeChange intakeChange = null;
            try
            {
                if (stopped) return;
                SimulationManager simulation = SimulationManager.instance;
                if (!simulation.SimulationPaused || simulation.m_metaData.m_CityName != city ||
                    simulation.m_currentBuildIndex != build || NetManager.instance.m_segmentCount != segments)
                    throw new InvalidOperationException("Finance sampling requires the checked paused city");
                economy = EconomyManager.instance;
                string file = id + "-finance.json";
                if (File.Exists(Path.Combine(Root, file))) throw new IOException("Finance receipt already exists");
                object powerBudget, wasteBudget, targets, waterIntake, localWasteAssignments;
                bool changePower = request.TryGetValue("electricityBudget", out powerBudget);
                bool changeWaste = request.TryGetValue("garbageBudget", out wasteBudget);
                bool startWatch = request.TryGetValue("wasteBuildingIds", out targets);
                bool dispatchLocalWaste = request.TryGetValue("localWasteAssignments", out localWasteAssignments);
                bool dispatchNearby = request.ContainsKey("nearbyWasteDispatch") && Convert.ToBoolean(request["nearbyWasteDispatch"]);
                bool finishWatch = request.ContainsKey("finishWasteWatch") && Convert.ToBoolean(request["finishWasteWatch"]);
                bool changeIntake = request.TryGetValue("waterIntake", out waterIntake);
                bool sampleWater = request.ContainsKey("sampleWaterQuality") && Convert.ToBoolean(request["sampleWaterQuality"]);
                bool sampleHousing = request.ContainsKey("sampleHousingCapacity") && Convert.ToBoolean(request["sampleHousingCapacity"]);
                if ((changePower && changeWaste) || (changeIntake && (changePower || changeWaste || startWatch || finishWatch)))
                    throw new InvalidDataException("Select one city operation per trial");
                if (startWatch && (wasteWatch != null || finishWatch))
                    throw new InvalidOperationException("Finish the existing observation before selecting new buildings");
                if (dispatchLocalWaste && (!startWatch || changeWaste || changePower || changeIntake))
                    throw new InvalidOperationException("Local waste assignments need a separate collection trial");
                if (dispatchNearby && (!startWatch || dispatchLocalWaste || changeWaste || changePower || changeIntake))
                    throw new InvalidOperationException("Nearby waste dispatch needs a separate collection trial");
                int powerDay = Budget(economy, electricity, false), powerNight = Budget(economy, electricity, true);
                int wasteDay = Budget(economy, garbage, false), wasteNight = Budget(economy, garbage, true);
                var services = new List<object>();
                foreach (ItemClass.Service service in Enum.GetValues(typeof(ItemClass.Service)))
                    if (service != ItemClass.Service.None) services.Add(Account(economy, service, ItemClass.SubService.None));
                var transport = new List<object>();
                foreach (ItemClass.SubService subService in Enum.GetValues(typeof(ItemClass.SubService)))
                    if (subService.ToString().StartsWith("PublicTransport", StringComparison.Ordinal))
                        transport.Add(Account(economy, ItemClass.Service.PublicTransport, subService));
                if (startWatch)
                {
                    wasteWatch = new NativeWasteObservation((List<object>)targets, dispatchNearby);
                    startedWatch = true;
                    if (dispatchLocalWaste) wasteWatch.DispatchLocal((List<object>)localWasteAssignments);
                }
                if (changePower || changeWaste)
                {
                    changeService = changePower ? electricity : garbage;
                    day = changePower ? powerDay : wasteDay;
                    night = changePower ? powerNight : wasteNight;
                    changed = ApplyBudget(economy, changeService,
                        (Dictionary<string, object>)(changePower ? powerBudget : wasteBudget), day, night);
                }
                if (changeIntake)
                {
                    intakeChange = new NativeWaterQuality.IntakeChange((Dictionary<string, object>)waterIntake);
                    intakeChange.Apply();
                }
                Write(file, new Dictionary<string, object> {
                    {"runId",runId}, {"commandId",id}, {"cityName",city}, {"buildIndex",build}, {"segments",segments},
                    {"gameTime",simulation.m_currentGameTime.ToString("s")}, {"internalCash",economy.InternalCashAmount},
                    {"lastCash",economy.LastCashAmount}, {"lastCashDelta",economy.LastCashDelta},
                    {"loanExpenses",economy.GetLoanExpenses()}, {"policyExpenses",economy.GetPolicyExpenses()},
                    {"services",services}, {"publicTransport",transport}, {"writesToCity",changed || changeIntake || dispatchLocalWaste},
                    {"waterQuality",sampleWater || changeIntake ? NativeWaterQuality.Capture() : null},
                    {"housingCapacityByWard",sampleHousing ? NativeCityRoster.AuditCapacity() : null},
                    {"networkMaintenance", NetworkMaintenanceSnapshot()},
                    {"electricity",PowerSnapshot(economy, powerDay, powerNight)},
                    {"garbage",WasteSnapshot(economy, wasteDay, wasteNight)}
                });
                changed = false;
                startedWatch = false;
                intakeChange = null;
                if (finishWatch) StopWasteWatch();
                Write("finance-runtime.json", Status(id, "complete", file, null));
            }
            catch (Exception error)
            {
                if (changed) SetBudget(economy, changeService, day, night);
                if (intakeChange != null) intakeChange.Rollback();
                if (startedWatch) StopWasteWatch();
                Write("finance-runtime.json", Status(id, "rejected", null, error.Message));
            }
            finally { Interlocked.Exchange(ref queued, 0); }
        }

        private static int Budget(EconomyManager economy, ItemClass.Service service, bool night)
        {
            return economy.GetBudget(service, ItemClass.SubService.None, night);
        }

        private static void SetBudget(EconomyManager economy, ItemClass.Service service, int day, int night)
        {
            economy.SetBudget(service, ItemClass.SubService.None, day, false);
            economy.SetBudget(service, ItemClass.SubService.None, night, true);
        }

        private static bool ApplyBudget(EconomyManager economy, ItemClass.Service service, Dictionary<string, object> request, int day, int night)
        {
            if (Convert.ToInt32(request["expectedDay"]) != day || Convert.ToInt32(request["expectedNight"]) != night)
                throw new InvalidOperationException("The city's " + service + " budget changed before the trial");
            int next = Convert.ToInt32(request["percent"]);
            if (next < 50 || next > 150) throw new InvalidDataException("Budget must be 50 to 150 percent");
            if (day == next && night == next) return false;

            try
            {
                SetBudget(economy, service, next, next);
                if (Budget(economy, service, false) != next || Budget(economy, service, true) != next)
                    throw new InvalidOperationException("The game did not retain the requested " + service + " budget");
                return true;
            }
            catch
            {
                SetBudget(economy, service, day, night);
                throw;
            }
        }

        private static Dictionary<string, object> PowerSnapshot(EconomyManager economy, int beforeDay, int beforeNight)
        {
            District citywide = DistrictManager.instance.m_districts.m_buffer[0];
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            var existingByUse = new Dictionary<string, object>();
            var activeByUse = new Dictionary<string, object>();
            var problemsByUse = new Dictionary<string, object>();
            var producers = new List<object>();
            for (int index = 1; index < buildings.Length; index++)
            {
                NativeBuilding building = buildings[index];
                const NativeBuilding.Flags created = NativeBuilding.Flags.Created;
                if ((building.m_flags & (created | NativeBuilding.Flags.Deleted)) != created || building.Info == null) continue;
                ItemClass.Service service = building.Info.GetService();
                if (service == ItemClass.Service.Electricity && building.Info.m_buildingAI is PlayerBuildingAI)
                {
                    PlayerBuildingAI ai = (PlayerBuildingAI)building.Info.m_buildingAI;
                    int production, maximum;
                    ai.GetElectricityProduction(out production, out maximum);
                    if (production > 0 || maximum > 0)
                        producers.Add(new Dictionary<string, object> {
                            {"id",index}, {"prefab",building.Info.name}, {"generation",building.m_buildIndex},
                            {"active",(building.m_flags & NativeBuilding.Flags.Active) != 0},
                            {"electricityRate",ai.GetElectricityRate((ushort)index, ref building)},
                            {"finalMaintenance",ai.GetFinalMaintenanceCost((ushort)index, ref building)}
                        });
                }
                if (service != ItemClass.Service.Residential && service != ItemClass.Service.Commercial &&
                    service != ItemClass.Service.Industrial && service != ItemClass.Service.Office) continue;
                string use = service.ToString();
                Increment(existingByUse, use);
                if ((building.m_flags & NativeBuilding.Flags.Active) != 0) Increment(activeByUse, use);
                if (!(building.m_problems & (Notification.Problem1.Electricity | Notification.Problem1.ElectricityNotConnected)).IsNone)
                    Increment(problemsByUse, use);
            }
            return new Dictionary<string, object> {
                {"previousDayBudgetPercent", beforeDay}, {"previousNightBudgetPercent", beforeNight},
                {"dayBudgetPercent", Budget(economy, ItemClass.Service.Electricity, false)},
                {"nightBudgetPercent", Budget(economy, ItemClass.Service.Electricity, true)},
                {"capacityKW", citywide.GetElectricityCapacity()}, {"consumptionKW", citywide.GetElectricityConsumption()},
                {"producers",producers}, {"availableStockProducers",StockPowerPrefabs()},
                {"existingBuildingsByUse", existingByUse}, {"activeBuildingsByUse", activeByUse},
                {"buildingsWithElectricityProblemByUse", problemsByUse}
            };
        }

        private static List<object> StockPowerPrefabs()
        {
            var result = new List<object>();
            string[] names = {"Coal Power Plant", "Oil Power Plant", "Solar Power Plant", "Nuclear Power Plant",
                "Wind Turbine", "Advanced Wind Turbine", "Fusion Power Plant"};
            foreach (string name in names)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(name);
                PlayerBuildingAI ai = info == null ? null : info.m_buildingAI as PlayerBuildingAI;
                if (ai == null) continue;
                int production, maximum;
                ai.GetElectricityProduction(out production, out maximum);
                result.Add(new Dictionary<string, object> {
                    {"prefab",name}, {"minNominalOutput",production}, {"maxNominalOutput",maximum},
                    {"maintenance",ai.GetMaintenanceCost()}, {"construction",ai.GetConstructionCost()}
                });
            }
            return result;
        }

        private sealed class NetworkCost
        {
            public int Segments, SegmentsWithTraffic;
            public double LengthMeters;
            public long PrefabMaintenance;
        }

        private static Dictionary<string, object> NetworkMaintenanceSnapshot()
        {
            var byService = new Dictionary<string, Dictionary<string, NetworkCost>>(StringComparer.Ordinal);
            NetManager net = NetManager.instance;
            NetSegment[] segments = net.m_segments.m_buffer;
            NetNode[] nodes = net.m_nodes.m_buffer;
            for (int i = 1; i < segments.Length; i++)
            {
                NetSegment segment = segments[i];
                if ((segment.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created) continue;
                NetInfo info = segment.Info;
                if (info == null || !(info.m_netAI is PlayerNetAI) || segment.m_startNode == 0 || segment.m_endNode == 0) continue;
                ItemClass.Service service = info.GetService();
                if (service != ItemClass.Service.Road && service != ItemClass.Service.Water && service != ItemClass.Service.Electricity)
                    continue;
                string key = service.ToString();
                Dictionary<string, NetworkCost> prefabs;
                if (!byService.TryGetValue(key, out prefabs))
                {
                    prefabs = new Dictionary<string, NetworkCost>(StringComparer.Ordinal);
                    byService.Add(key, prefabs);
                }
                NetworkCost cost;
                if (!prefabs.TryGetValue(info.name, out cost))
                {
                    cost = new NetworkCost();
                    prefabs.Add(info.name, cost);
                }
                cost.Segments++;
                if (segment.m_trafficDensity > 0) cost.SegmentsWithTraffic++;
                Vector3 start = nodes[segment.m_startNode].m_position;
                Vector3 end = nodes[segment.m_endNode].m_position;
                cost.LengthMeters += segment.m_averageLength > 0 ? segment.m_averageLength : Vector3.Distance(start, end);
                cost.PrefabMaintenance += info.m_netAI.GetMaintenanceCost(start, end);
            }
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var service in byService)
            {
                var prefabs = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var prefab in service.Value)
                {
                    NetworkCost cost = prefab.Value;
                    prefabs.Add(prefab.Key, new Dictionary<string, object> {
                        {"segments",cost.Segments}, {"segmentsWithNonzeroTrafficDensity",cost.SegmentsWithTraffic},
                        {"lengthMeters",cost.LengthMeters}, {"sumNativePrefabMaintenance",cost.PrefabMaintenance}
                    });
                }
                result.Add(service.Key, prefabs);
            }
            return result;
        }

        private Dictionary<string, object> WasteSnapshot(EconomyManager economy, int beforeDay, int beforeNight)
        {
            District citywide = DistrictManager.instance.m_districts.m_buffer[0];
            return new Dictionary<string, object> {
                {"previousDayBudgetPercent", beforeDay}, {"previousNightBudgetPercent", beforeNight},
                {"dayBudgetPercent", Budget(economy, ItemClass.Service.Garbage, false)},
                {"nightBudgetPercent", Budget(economy, ItemClass.Service.Garbage, true)},
                {"productionPerWeek", citywide.GetGarbageAccumulation()},
                {"processingCapacityPerWeek", citywide.GetIncinerationCapacity()},
                {"landfillCapacity", citywide.GetGarbageCapacity()}, {"landfillAmount", citywide.GetGarbageAmount()},
                {"fleet", NativeWasteObservation.FleetSnapshot()},
                {"monitoredBuildings", wasteWatch == null ? null : wasteWatch.Snapshot()},
                {"dispatchObservation", wasteWatch == null ? null : wasteWatch.DispatchSnapshot()}
            };
        }

        private void StopWasteWatch()
        {
            if (wasteWatch == null) return;
            wasteWatch.Stop();
            wasteWatch = null;
        }

        private static void Increment(Dictionary<string, object> counts, string key)
        {
            object count;
            counts[key] = counts.TryGetValue(key, out count) ? (int)count + 1 : 1;
        }

        private static Dictionary<string, object> Account(EconomyManager economy, ItemClass.Service service,
            ItemClass.SubService subService)
        {
            long income, expenses;
            economy.GetIncomeAndExpenses(service, subService, ItemClass.Level.None, out income, out expenses);
            return new Dictionary<string, object> { {"service",service.ToString()}, {"subService",subService.ToString()},
                {"income",income}, {"expenses",expenses} };
        }

        private Dictionary<string, object> Status(string id, string phase, string file, string error)
        {
            return new Dictionary<string, object> { {"runId",runId}, {"commandId",id}, {"cityName",city},
                {"phase",phase}, {"file",file}, {"error",error}, {"utc",DateTime.UtcNow.ToString("o")} };
        }

        private static void Write(string name, Dictionary<string, object> data)
        {
            string target = Path.Combine(Root, name);
            string pending = target + ".tmp";
            File.WriteAllText(pending, JsonData.Stringify(data), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(pending, target, null);
            else File.Move(pending, target);
        }

        public void OnDestroy()
        {
            stopped = true;
            StopWasteWatch();
        }
    }
}
