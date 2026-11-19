using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using ColossalFramework.Math;
using HarmonyLib;
using UnityEngine;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    public sealed class AndroidLifePilot : MonoBehaviour
    {
        private const int MembersPerUnit = 5;
        private const int PilotMaximumAndroids = 512;
        private const int MaximumUnitsPerBuilding = 2048;
        private const double BatteryPerWalkingMeter = .025;
        private static readonly TimeSpan MissingTravelGrace = TimeSpan.FromDays(1);
        private const string RenderPatchId = "sunwood.nagoya.android-residence-render";
        private static AndroidLifePilot renderer;
        private static AndroidLifePilot active;
        private Harmony renderPatch;
        private Harmony lifecyclePatch;
        private NativeAndroidDeliveries localDeliveries;
        private volatile NativeEmploymentTrial employmentTrial;
        private NativePopulationCard populationCard;
        private readonly Dictionary<uint, Resident> cohort = new Dictionary<uint, Resident>();
        private volatile uint[] residents = new uint[0];
        private readonly Dictionary<uint, object> visuals = new Dictionary<uint, object>();
        private readonly List<uint> releasedVisuals = new List<uint>();
        private readonly Queue<object> recentTravelRecoveries = new Queue<object>();
        private readonly List<object> commuteInterruptionExamples = new List<object>();
        private int travelRecoveries;
        private int commutePathFailures, commuteReleasesAfterPathFailure, commuteReleasesWithoutPathFailure;
        private bool commuteReleaseTraceActive;
        private CitywideScene city;
        private string root, runId, caseId, error, visualError;
        private List<object> localDeliveryAdmissions;
        private volatile bool attempted;
        private bool restored;
        private float nextSample;
        private DateTime lastGameTime;
        private int queued;
        private Sample sample;
        private string hud = "Android life / preparing";

        private sealed class Resident
        {
            public ushort Home;
            public uint HomeUnit;
            public double Battery = 65;
            public double WalkedMeters;
            public ushort WalkingInstance, ShopReturnSource, ShopReturnHome;
            public Vector3 WalkingPosition;
            public int ShopReturnsStarted, ShopReturnsArrived;
            public int HomeSamples, WorkSamples, MovingSamples, ChargeStarts;
            public int CommutesStarted, ReturnsStarted, CommuteSamples;
            public int NativeWorkArrivals, NativeWorkArrivalFailures, TravelRecoveries;
            public ushort CommuteInstance;
            public bool CommutePathFailed;
            public int NativeCommutePathFailures, NativeMovingCommuteReleases;
            public DateTime LastCommuteDay, LastReturnDay;
            public DateTime MissingTravelSince;
            public Citizen.Location TravelOrigin;
            public ushort TravelSource, TravelTarget;
            public bool Charging;
        }

        private sealed class Sample
        {
            public string Json, Hud, PopulationText;
        }

        internal void Initialize(CitywideScene host, string data, string id, NativeEmploymentTrial trial)
        {
            city = host;
            root = data;
            runId = id;
            employmentTrial = trial;
            active = this;
            try
            {
                MethodInfo target = typeof(CitizenInstance).GetMethod("RenderInstance",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new Type[] {typeof(RenderManager.CameraInfo), typeof(ushort)}, null);
                MethodInfo prefix = typeof(AndroidLifePilot).GetMethod("KeepNativeSkin", BindingFlags.Static | BindingFlags.NonPublic);
                if (target == null) throw new MissingMethodException("CitizenInstance.RenderInstance");
                renderPatch = new Harmony(RenderPatchId);
                renderPatch.Patch(target, new HarmonyMethod(prefix), null, null, null);
                renderer = this;
            }
            catch (Exception ex)
            {
                visualError = ex.GetBaseException().Message;
                Debug.LogException(ex);
            }
            InstallLifecycleTracking();
            if (root == NativeCityRoadBuilder.Root)
            {
                try
                {
                    localDeliveries = new NativeAndroidDeliveries();
                    if (trial != null) InstallCommuteReleaseTrace();
                    SimulationManager.instance.AddAction(delegate { RestoreSavedResidents(); });
                }
                catch (Exception ex) { Debug.LogException(ex); }
                try { populationCard = new NativePopulationCard(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void InstallLifecycleTracking()
        {
            try
            {
                MethodInfo release = typeof(CitizenManager).GetMethod("ReleaseCitizenImplementation",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new Type[] {typeof(uint), typeof(Citizen).MakeByRefType()}, null);
                if (release == null) throw new MissingMethodException("CitizenManager.ReleaseCitizenImplementation");
                MethodInfo released = typeof(AndroidLifePilot).GetMethod("OnCitizenReleased", BindingFlags.Static | BindingFlags.NonPublic);
                lifecyclePatch = new Harmony(RenderPatchId + ".lifecycle");
                lifecyclePatch.Patch(release, null, new HarmonyMethod(released), null, null);
            }
            catch (Exception ex)
            {
                lifecyclePatch = null;
                Debug.LogException(ex);
            }
        }

        private static void OnCitizenReleased(uint __0)
        {
            AndroidLifePilot life = active;
            if (life == null || !life.cohort.Remove(__0)) return;
            NativeCityLedger.RemoveAndroid(__0);
            if (life.localDeliveries != null) life.localDeliveries.ForgetCitizen(__0);
            life.UpdateResidentIds();
        }

        private void InstallCommuteReleaseTrace()
        {
            if (lifecyclePatch == null) return;
            try
            {
                MethodInfo release = typeof(CitizenManager).GetMethod("ReleaseCitizenInstance", new Type[] {typeof(ushort)});
                if (release == null) throw new MissingMethodException("CitizenManager.ReleaseCitizenInstance");
                MethodInfo prefix = typeof(AndroidLifePilot).GetMethod("BeforeCommuteInstanceRelease", BindingFlags.Static | BindingFlags.NonPublic);
                lifecyclePatch.Patch(release, new HarmonyMethod(prefix), null, null, null);
                commuteReleaseTraceActive = true;
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private static void BeforeCommuteInstanceRelease(ushort __0)
        {
            AndroidLifePilot life = active;
            if (life == null || life.employmentTrial == null || !life.employmentTrial.Running || __0 == 0) return;
            CitizenInstance movement = CitizenManager.instance.m_instances.m_buffer[__0];
            life.ObserveCommuteInterruption(__0, movement, false);
        }

        internal static void ObserveCommutePathFailure(ushort instanceId, CitizenInstance movement)
        {
            AndroidLifePilot life = active;
            if (life == null || life.employmentTrial == null || !life.employmentTrial.Running) return;
            life.ObserveCommuteInterruption(instanceId, movement, true);
        }

        private void ObserveCommuteInterruption(ushort instanceId, CitizenInstance movement, bool pathFailure)
        {
            uint id = movement.m_citizen;
            Resident member;
            if (instanceId == 0 || id == 0 || !cohort.TryGetValue(id, out member) || member.CommuteInstance != instanceId) return;
            Citizen citizen = CitizenManager.instance.m_citizens.m_buffer[id];
            if (citizen.CurrentLocation != Citizen.Location.Moving || citizen.m_instance != instanceId ||
                citizen.m_homeBuilding != member.TravelSource || citizen.m_workBuilding != member.TravelTarget) return;

            int count;
            string kind;
            if (pathFailure)
            {
                member.CommutePathFailed = true;
                member.NativeCommutePathFailures++;
                count = ++commutePathFailures;
                kind = "pathFailure";
            }
            else
            {
                member.CommuteInstance = 0;
                member.NativeMovingCommuteReleases++;
                count = member.CommutePathFailed ? ++commuteReleasesAfterPathFailure : ++commuteReleasesWithoutPathFailure;
                kind = member.CommutePathFailed ? "releaseAfterPathFailure" : "releaseWithoutPathFailure";
            }

            if (count > 8) return;
            var callers = new List<string>();
            var trace = new System.Diagnostics.StackTrace(2, false);
            for (int n = 0; n < trace.FrameCount && n < 16; n++)
            {
                MethodBase method = trace.GetFrame(n).GetMethod();
                if (method != null) callers.Add((method.DeclaringType == null ? "" : method.DeclaringType.Name + ".") + method.Name);
            }
            commuteInterruptionExamples.Add(new Dictionary<string, object> {
                {"kind",kind}, {"citizenId",id}, {"instanceId",instanceId}, {"from",member.TravelSource}, {"to",member.TravelTarget},
                {"reportedTarget",movement.m_targetBuilding}, {"path",movement.m_path}, {"flags",movement.m_flags.ToString()},
                {"frame",SimulationManager.instance.m_currentFrameIndex}, {"callers",callers.ToArray()}
            });
        }

        private static bool KeepNativeSkin(ref CitizenInstance __instance, ref bool __result)
        {
            AndroidLifePilot scene = renderer;
            if (scene == null || Array.BinarySearch(scene.residents, __instance.m_citizen) < 0)
                return true;
            __result = true;
            return false;
        }

        public void Update()
        {
            if (city == null) return;
            Sample ready = Interlocked.Exchange<Sample>(ref sample, null);
            if (ready != null)
            {
                hud = ready.Hud;
                if (populationCard != null) populationCard.Text = ready.PopulationText;
                try { WriteSnapshot(ready.Json); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            UpdateVisuals();
            NativeEmploymentTrial trial = employmentTrial;
            if (trial != null && attempted && (!trial.Running || DateTime.UtcNow < trial.Deadline)) return;
            if (Time.realtimeSinceStartup < nextSample || Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            nextSample = Time.realtimeSinceStartup + 4;
            SimulationManager.instance.AddAction(delegate {
                try
                {
                    if (employmentTrial != null)
                    {
                        if (employmentTrial.Running)
                            ObserveEmploymentTrialFrame();
                        else
                            StartEmploymentTrial();
                    }
                    else
                    {
                        CreateRequestedResidents();
                        Interlocked.Exchange<Sample>(ref sample, Observe());
                    }
                }
                catch (Exception ex)
                {
                    if (employmentTrial != null && employmentTrial.CommuteAudit != null) employmentTrial.CommuteAudit.Dispose();
                    employmentTrial = null;
                    Debug.LogException(ex);
                }
                finally { Interlocked.Exchange(ref queued, 0); }
            });
        }

        private void CreateRequestedResidents()
        {
            if (!restored) RestoreSavedResidents();
            if (attempted) return;
            string request = Path.Combine(root, "android-life-scenario.json");
            if (!File.Exists(request)) return;
            try
            {
                if (new FileInfo(request).Length > 4096) throw new InvalidDataException("Scenario file limit");
                Dictionary<string, object> config = (Dictionary<string, object>)JsonData.Parse(File.ReadAllText(request));
                if ((string)config["runId"] != runId) return;
                attempted = true;
                caseId = (string)config["caseId"];
                if (caseId.Length < 1 || caseId.Length > 60) throw new InvalidDataException("Scenario case name");
                object wards;
                if (config.TryGetValue("localDeliveryWards", out wards))
                {
                    AdmitLocalDeliveryResidents((List<object>)wards, config);
                    return;
                }
                double count = (double)config["androidResidents"];
                int limit = root == NativeCityRoadBuilder.Root ? NativeCityLedger.MaxResidents : PilotMaximumAndroids;
                if (count != Math.Floor(count) || count < 0 || count > limit)
                    throw new InvalidDataException("Scenario case or cohort size");
                PopulateResidents((int)count);
            }
            catch (Exception ex) { attempted = true; error = ex.GetBaseException().Message; }
            finally
            {
                UpdateResidentIds();
            }
        }

        private void AdmitLocalDeliveryResidents(List<object> values, Dictionary<string, object> config)
        {
            SimulationManager sim = SimulationManager.instance;
            int perWard = Convert.ToInt32(config["limitPerWard"]);
            if (localDeliveries == null || !sim.SimulationPaused || perWard < 1 || perWard > 3 ||
                sim.m_currentBuildIndex != Convert.ToUInt32(config["expectedBuildIndex"]) ||
                NetManager.instance.m_segmentCount != Convert.ToInt32(config["expectedSegments"]))
                throw new InvalidDataException("The paused local hiring checkpoint changed");
            var wards = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in values)
            {
                string ward = (string)value;
                if (ward.Length < 2 || ward.Length > 12 || !ward.EndsWith("区", StringComparison.Ordinal) || !wards.Add(ward))
                    throw new InvalidDataException("Invalid delivery ward");
            }
            if (wards.Count == 0 || wards.Count > 16)
                throw new InvalidDataException("Local delivery request is outside the district limit");
            object onsite;
            if (config.TryGetValue("dispatchAtWork", out onsite) && (bool)onsite)
            {
                localDeliveries.BeginSample(sim.m_currentGameTime);
                localDeliveries.PrioritizeOnsiteDeliveries(wards);
                return;
            }
            object onlyWorkers;
            bool existingOnly = config.TryGetValue("existingWorkersOnly", out onlyWorkers) && (bool)onlyWorkers;
            if (!existingOnly && cohort.Count + wards.Count * perWard > NativeCityLedger.MaxResidents)
                throw new InvalidDataException("Local hiring request is outside the district or population limits");
            HashSet<ushort> startupFactories = ReadScenarioFacilities(config, "startupFactoryIds");
            HashSet<ushort> startupShops = ReadScenarioFacilities(config, "startupShopIds");
            HashSet<ushort> startupHomes = ReadScenarioFacilities(config, "startupHomeIds");
            if (existingOnly && (startupHomes.Count != 0 || startupShops.Count == 0))
                throw new InvalidDataException("Existing workers require receiving shops without new housing");
            if (!existingOnly && ((startupFactories.Count == 0) != (startupShops.Count == 0) ||
                (startupHomes.Count != 0 && startupFactories.Count == 0)))
                throw new InvalidDataException("New work sites and their receiving shops must be provided together");
            localDeliveries.BeginSample(sim.m_currentGameTime);
            localDeliveryAdmissions = new List<object>();
            var usedFactories = new HashSet<ushort>();
            var random = new Randomizer(5102026);
            object preferWorkers;
            var workers = existingOnly || (config.TryGetValue("preferExistingWorkers", out preferWorkers) && (bool)preferWorkers)
                ? ConfirmedWorkersByFactory() : new Dictionary<ushort, List<uint>>();
            if (startupHomes.Count > 0 && workers.Count > 0)
                throw new InvalidDataException("Advance registration requires vacant residential and employment units");
            Func<ushort, uint> vacantHome = startupHomes.Count > 0 ? (Func<ushort, uint>)FindEmptyHousehold : FindVacantHomeUnit;
            foreach (string ward in wards)
            {
                for (int attempt = 0; attempt < perWard; attempt++)
                {
                    Dictionary<string, int> counts;
                    NativeAndroidDeliveries.LocalOpening opening = localDeliveries.FindLocalOpening(ward, vacantHome,
                        usedFactories, workers, startupFactories, startupShops, startupHomes, existingOnly, out counts);
                    var record = new Dictionary<string, object> {{"ward",ward}, {"candidateCounts",counts}};
                    localDeliveryAdmissions.Add(record);
                    if (opening.Home == 0)
                    {
                        record["status"] = "no feasible opening";
                        break;
                    }
                    usedFactories.Add(opening.Factory);
                    uint id = opening.Worker;
                    if (id == 0)
                    {
                        Citizen.Gender gender = (cohort.Count & 1) == 0 ? Citizen.Gender.Male : Citizen.Gender.Female;
                        id = CreateResident(opening.Home, opening.HomeUnit, opening.Factory, gender, ref random);
                    }
                    if (id == 0)
                    {
                        record["status"] = "native registration refused";
                        continue;
                    }
                    localDeliveries.RegisterLocalOpening(id, opening, sim.m_currentGameTime);
                    record["status"] = opening.Worker == 0 ? "admitted" : "existing worker scheduled";
                    record["citizenId"] = id;
                    record["home"] = opening.Home;
                    record["homeUnit"] = cohort[id].HomeUnit;
                    record["factory"] = opening.Factory;
                    record["shop"] = opening.Shop;
                    record["usedNewPlacementFrontage"] = startupHomes.Count > 0;
                    if (opening.Worker == 0) record["factoryGoodsBeforeRegistration"] = opening.FactoryGoods;
                    record["homeToFactoryMeters"] = Double.IsInfinity(opening.HomeDistance) ? null : (object)opening.HomeDistance;
                    record["factoryToShopMeters"] = opening.ShopDistance;
                }
            }
        }

        private static HashSet<ushort> ReadScenarioFacilities(Dictionary<string, object> config, string field)
        {
            var ids = new HashSet<ushort>();
            object list;
            if (!config.TryGetValue(field, out list)) return ids;
            foreach (object value in (List<object>)list)
            {
                double number = (double)value;
                if (number < 1 || number > UInt16.MaxValue || number != Math.Floor(number) || !ids.Add((ushort)number) || ids.Count > 48)
                    throw new InvalidDataException("Invalid scenario facility set " + field);
            }
            return ids;
        }

        private Dictionary<ushort, List<uint>> ConfirmedWorkersByFactory()
        {
            var workers = new Dictionary<ushort, List<uint>>();
            CitizenManager citizens = CitizenManager.instance;
            foreach (var member in cohort)
            {
                Citizen citizen = citizens.m_citizens.m_buffer[member.Key];
                ushort workplace = citizen.m_workBuilding;
                if ((citizen.m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead | Citizen.Flags.Student)) != Citizen.Flags.Created ||
                    workplace == 0 || citizen.m_homeBuilding != member.Value.Home ||
                    !InWorkUnit(citizens, workplace, member.Key)) continue;
                List<uint> members;
                if (!workers.TryGetValue(workplace, out members)) workers.Add(workplace, members = new List<uint>());
                members.Add(member.Key);
            }
            return workers;
        }

        private static uint FindVacantHomeUnit(ushort home)
        {
            if (!HasElectricity(home)) return 0;
            return FindEmptyHousehold(home);
        }

        private static uint FindEmptyHousehold(ushort home)
        {
            CitizenUnit[] units = CitizenManager.instance.m_units.m_buffer;
            uint unit = BuildingManager.instance.m_buildings.m_buffer[home].m_citizenUnits;
            for (int i = 0; i < MaximumUnitsPerBuilding && unit != 0 && unit < units.Length; i++)
            {
                CitizenUnit row = units[unit];
                if ((row.m_flags & CitizenUnit.Flags.Home) != 0 && row.m_building == home && Empty(row)) return unit;
                unit = row.m_nextUnit;
            }
            return 0;
        }

        private uint CreateResident(ushort home, uint unit, ushort workplace, Citizen.Gender gender, ref Randomizer random)
        {
            CitizenManager citizens = CitizenManager.instance;
            uint id;
            if (!citizens.CreateCitizen(out id, 120, 0, ref random, gender))
                throw new InvalidOperationException("CS1 could not create a resident");
            Citizen[] people = citizens.m_citizens.m_buffer;
            people[id].SetHome(id, home, unit);
            if (people[id].m_homeBuilding != home || !Contains(citizens.m_units.m_buffer[unit], id))
            {
                citizens.ReleaseCitizen(id);
                return 0;
            }
            if (workplace != 0)
            {
                people[id].SetWorkplace(id, workplace, 0);
                if (people[id].m_workBuilding != workplace || !InWorkUnit(citizens, workplace, id))
                {
                    citizens.ReleaseCitizen(id);
                    return 0;
                }
            }
            people[id].CurrentLocation = Citizen.Location.Home;
            cohort.Add(id, new Resident {Home=home, HomeUnit=unit, Battery=65});
            NativeCityLedger.Record(new NativeCityLedger.Android {Id=id, Home=home, HomeUnit=unit, Battery=65});
            return id;
        }

        private void PopulateResidents(int target)
        {
            int requested = Math.Max(0, target - cohort.Count);
            CitizenManager citizens = CitizenManager.instance;
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            Randomizer random = new Randomizer(5102026);
            List<uint> added = new List<uint>();
            bool citywide = root == NativeCityRoadBuilder.Root;
            int passes = citywide ? 2 : 1;
            for (int pass = 0; pass < passes && added.Count < requested; pass++)
            {
                foreach (int i in HomeOrder(buildings.Length))
                {
                    if (added.Count >= requested) break;
                    NativeBuilding building = buildings[i];
                    BuildingInfo info = building.Info;
                    if ((building.m_flags & (NativeBuilding.Flags.Created | NativeBuilding.Flags.Abandoned)) != NativeBuilding.Flags.Created ||
                        info == null || info.m_class.m_service != ItemClass.Service.Residential ||
                        (citywide && !HasResidentialUtilities((ushort)i, building))) continue;
                    uint unitId = building.m_citizenUnits;
                    for (int u = 0; unitId != 0 && unitId < citizens.m_units.m_buffer.Length && u < MaximumUnitsPerBuilding && added.Count < requested; u++)
                    {
                        CitizenUnit unit = citizens.m_units.m_buffer[unitId];
                        if ((unit.m_flags & CitizenUnit.Flags.Home) != 0 && unit.m_building == i && Empty(unit))
                        {
                            Citizen.Gender gender = (added.Count & 1) == 0 ? Citizen.Gender.Male : Citizen.Gender.Female;
                            uint citizenId = CreateResident((ushort)i, unitId, 0, gender, ref random);
                            if (citizenId == 0)
                                throw new InvalidOperationException("CS1 refused the vacant household seat");
                            added.Add(citizenId);
                            if (citywide && pass == 0) break;
                        }
                        unitId = unit.m_nextUnit;
                    }
                }
            }
            if (added.Count < requested) error = "Vacant residential units limited the experiment to " + cohort.Count;
        }

        private List<Dictionary<string, object>> PopulateSharedAndroidHouseholds(int requested, bool mixed)
        {
            var admissions = new List<Dictionary<string, object>>();
            CitizenManager manager = CitizenManager.instance;
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            CitizenUnit[] units = manager.m_units.m_buffer;
            var androids = new List<uint>(MembersPerUnit);
            var humans = new List<uint>(MembersPerUnit);
            var admittedUnits = new HashSet<uint>();
            Randomizer random = new Randomizer(5102026);
            const NativeBuilding.Flags readyHome = NativeBuilding.Flags.Created | NativeBuilding.Flags.Completed | NativeBuilding.Flags.Active;
            foreach (int i in HomeOrder(buildings.Length))
            {
                if (admissions.Count >= requested) break;
                NativeBuilding building = buildings[i];
                if ((building.m_flags & readyHome) != readyHome || !HasResidentialUtilities((ushort)i, building)) continue;
                uint unitId = building.m_citizenUnits;
                for (int step = 0; unitId != 0 && unitId < units.Length && step < MaximumUnitsPerBuilding && admissions.Count < requested; step++)
                {
                    CitizenUnit unit = units[unitId];
                    if (!admittedUnits.Contains(unitId) && ReadHouseholdMembers(manager, (ushort)i, unitId, unit, androids, humans) &&
                        (mixed ? humans.Count > 0 && androids.Count > 0 && humans.Count + androids.Count < MembersPerUnit :
                         humans.Count == 0 && androids.Count == 1))
                    {
                        Citizen.Gender gender = (admissions.Count & 1) == 0 ? Citizen.Gender.Male : Citizen.Gender.Female;
                        uint newcomer = CreateResident((ushort)i, unitId, 0, gender, ref random);
                        if (newcomer == 0) throw new InvalidOperationException("CS1 refused the shared household seat");
                        if (CountResidentsAtAddress(manager, (ushort)i, unitId, humans) != humans.Count ||
                            CountResidentsAtAddress(manager, (ushort)i, unitId, androids) != androids.Count)
                        {
                            manager.ReleaseCitizen(newcomer);
                            if (cohort.Remove(newcomer)) NativeCityLedger.RemoveAndroid(newcomer);
                            throw new InvalidOperationException("An original household member changed during admission");
                        }
                        admittedUnits.Add(unitId);
                        admissions.Add(new Dictionary<string, object> {
                            {"existingAndroid",androids[0]}, {"admittedAndroid",newcomer}, {"homeBuilding",(ushort)i}, {"homeUnit",unitId},
                            {"ward",NativeCityLedger.RegisteredWard((ushort)i)},
                            {"existingHumanIds",humans.ToArray()}, {"existingAndroidIds",androids.ToArray()},
                            {"existingResidentsPreservedAtAdmission",true}
                        });
                        if (!mixed) break;
                    }
                    unitId = unit.m_nextUnit;
                }
            }
            if (admissions.Count < requested) error = "Shared Android households limited admission to " + admissions.Count;
            return admissions;
        }

        private bool ReadHouseholdMembers(CitizenManager manager, ushort home, uint unitId, CitizenUnit unit,
            List<uint> androids, List<uint> humans)
        {
            if (unit.m_building != home || (unit.m_flags & CitizenUnit.Flags.Home) == 0) return false;
            androids.Clear();
            humans.Clear();
            for (int slot = 0; slot < MembersPerUnit; slot++)
            {
                uint id = unit.GetCitizen(slot);
                if (id == 0) continue;
                if (!HasNativeHome(manager, home, id) || androids.Contains(id) || humans.Contains(id)) return false;
                Resident entry;
                if (cohort.TryGetValue(id, out entry))
                {
                    if (entry.Home != home || entry.HomeUnit != unitId) return false;
                    androids.Add(id);
                }
                else humans.Add(id);
            }
            return true;
        }

        private static bool HasNativeHome(CitizenManager manager, ushort home, uint id)
        {
            if (id >= manager.m_citizens.m_buffer.Length) return false;
            Citizen citizen = manager.m_citizens.m_buffer[id];
            return (citizen.m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead)) == Citizen.Flags.Created && citizen.m_homeBuilding == home;
        }

        internal static int CountResidentsAtAddress(CitizenManager manager, ushort home, uint unitId, IList<uint> ids)
        {
            if (unitId == 0 || unitId >= manager.m_units.m_buffer.Length) return 0;
            CitizenUnit unit = manager.m_units.m_buffer[unitId];
            if (unit.m_building != home || (unit.m_flags & CitizenUnit.Flags.Home) == 0) return 0;
            int count = 0;
            foreach (uint id in ids)
                if (HasNativeHome(manager, home, id) && Contains(unit, id)) count++;
            return count;
        }

        private void StartEmploymentTrial()
        {
            if (attempted) return;
            attempted = true;
            RestoreSavedResidents();
            SimulationManager sim = SimulationManager.instance;
            NativeEmploymentTrial trial = employmentTrial;
            if (!sim.SimulationPaused)
                throw new InvalidOperationException("The employment trial must begin on a paused checkpoint");
            trial.PrepareOpeningEconomy();
            trial.StartFrame = sim.m_currentFrameIndex;
            trial.Start = sim.FrameToTime(trial.StartFrame);
            trial.NextSample = trial.Start.AddHours(2);
            trial.Deadline = DateTime.UtcNow.AddSeconds(trial.NormalEconomy ? 300 : 90);
            caseId = trial.CaseId;
            trial.Baseline = Observe(trial.Start).Json;
            if (trial.PedestrianLinks != null) trial.PedestrianLinkResult = NativePedestrianLink.CreateFirstValid(trial.PedestrianLinks);
            trial.OpeningCapacity = NativeCityRoster.AuditCapacity();
            int additional = Math.Min(NativeCityLedger.MaxResidents - cohort.Count, trial.AdditionalResidents);
            if (trial.ShareAndroidHouseholds || trial.ShareMixedHouseholds)
                trial.SharedHouseholdAdmissions = PopulateSharedAndroidHouseholds(additional, trial.ShareMixedHouseholds);
            else PopulateResidents(cohort.Count + additional);
            UpdateResidentIds();
            trial.AfterAdmissionCapacity = NativeCityRoster.AuditCapacity();
            Sample admitted = Observe(trial.Start);
            trial.AfterAdmission = admitted.Json;
            if (trial.CommuteAuditIds != null)
                trial.CommuteAudit = new NativeCommuteRouteAudit(trial.CommuteAuditIds, trial.TestLongWalkCommutes);
            Interlocked.Exchange<Sample>(ref sample, admitted);
            trial.Running = true;
            sim.ForcedSimulationPaused = false;
            sim.SelectedSimulationSpeed = trial.Speed;
            sim.SimulationPaused = false;
        }

        internal static void ObserveEmploymentTrialFrame()
        {
            AndroidLifePilot life = active;
            if (life == null || life.employmentTrial == null || !life.employmentTrial.Running)
                return;
            NativeEmploymentTrial trial = life.employmentTrial;
            SimulationManager sim = SimulationManager.instance;
            DateTime time = sim.FrameToTime(sim.m_currentFrameIndex);
            try
            {
                bool expired = DateTime.UtcNow >= trial.Deadline;
                if (!expired && time < trial.NextSample && time < trial.Start.AddHours(trial.Hours))
                    return;
                if (expired || time >= trial.Start.AddHours(trial.Hours))
                {
                    trial.Running = false;
                    sim.SimulationPaused = true;
                    trial.End = time;
                    trial.EndFrame = sim.m_currentFrameIndex;
                    Sample terminal = life.Observe(time);
                    trial.SaveResult(terminal.Json, expired ? "timeout" : "completed");
                    Interlocked.Exchange<Sample>(ref life.sample, terminal);
                    life.employmentTrial = null;
                    return;
                }
                trial.NextSample = time.AddHours(2);
                Interlocked.Exchange<Sample>(ref life.sample, life.Observe(time));
            }
            catch (Exception issue)
            {
                trial.Running = false;
                sim.SimulationPaused = true;
                if (trial.CommuteAudit != null) trial.CommuteAudit.Dispose();
                life.employmentTrial = null;
                Debug.LogException(issue);
            }
        }

        private IEnumerable<int> HomeOrder(int nativeLength)
        {
            if (root != NativeCityRoadBuilder.Root)
            {
                for (int i = 1; i < nativeLength; i++) yield return i;
                yield break;
            }
            NativeCityLedger.Road[] roads;
            NativeCityLedger.Lot[] lots;
            NativeCityLedger.Snapshot(out roads, out lots);
            Dictionary<string, Queue<int>> homes = new Dictionary<string, Queue<int>>();
            foreach (NativeCityLedger.Lot lot in lots)
            {
                if ((lot.Zone != ItemClass.Zone.ResidentialLow && lot.Zone != ItemClass.Zone.ResidentialHigh) ||
                    !NativeCityLedger.HasActiveZone(lot)) continue;
                Queue<int> wardHomes;
                if (!homes.TryGetValue(lot.Ward, out wardHomes))
                    homes.Add(lot.Ward, wardHomes = new Queue<int>());
                wardHomes.Enqueue(lot.Id);
            }
            List<string> wards = new List<string>(homes.Keys);
            wards.Sort(StringComparer.Ordinal);
            for (int remaining = lots.Length; remaining > 0;)
            {
                int previous = remaining;
                foreach (string ward in wards)
                {
                    if (homes[ward].Count == 0) continue;
                    remaining--;
                    yield return homes[ward].Dequeue();
                }
                if (remaining == previous) break;
            }
        }

        private void RestoreSavedResidents()
        {
            if (restored) return;
            restored = true;
            if (root != NativeCityRoadBuilder.Root) return;
            CitizenManager manager = CitizenManager.instance;
            Citizen[] stock = manager.m_citizens.m_buffer;
            CitizenUnit[] units = manager.m_units.m_buffer;
            NativeCityLedger.Android[] saved = NativeCityLedger.SavedAndroids();
            foreach (NativeCityLedger.Android resident in saved)
            {
                if (resident.Id >= stock.Length || resident.HomeUnit >= units.Length ||
                    (stock[resident.Id].m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead)) != Citizen.Flags.Created ||
                    stock[resident.Id].m_homeBuilding != resident.Home || !Contains(units[resident.HomeUnit], resident.Id))
                {
                    NativeCityLedger.RemoveAndroid(resident.Id);
                    continue;
                }
                cohort.Add(resident.Id, new Resident {Home=resident.Home, HomeUnit=resident.HomeUnit, Battery=resident.Battery});
            }
            if (cohort.Count > 0)
            {
                caseId = "restored-native-residents";
                UpdateResidentIds();
            }
            if (localDeliveries != null)
                localDeliveries.RestoreCheckpoint(SimulationManager.instance.m_metaData.m_CityName);
        }

        private void UpdateResidentIds()
        {
            uint[] ids = new uint[cohort.Count];
            cohort.Keys.CopyTo(ids, 0);
            Array.Sort(ids);
            residents = ids;
        }

        internal static bool IsAndroidCitizen(uint id)
        {
            AndroidLifePilot runner = active;
            return runner != null && Array.BinarySearch(runner.residents, id) >= 0;
        }

        private Sample Observe()
        {
            return Observe(SimulationManager.instance.m_currentGameTime);
        }

        private Sample Observe(DateTime now)
        {
            CitizenManager manager = CitizenManager.instance;
            bool clockRunning = !SimulationManager.instance.SimulationPaused;
            NativeEmploymentTrial trial = employmentTrial;
            bool fullLifeSimulation = trial == null || trial.NormalEconomy;
            if (localDeliveries != null && cohort.Count > 0) localDeliveries.BeginSample(now);
            double minutes = lastGameTime == DateTime.MinValue ? 0 : Math.Max(0, Math.Min(180, (now - lastGameTime).TotalMinutes));
            lastGameTime = now;
            int humans = 0, humanEmployed = 0, androidEmployed = 0, atHome = 0, atWork = 0, moving = 0, inTransit = 0;
            int androidHomeMembers = 0, androidWorkMembers = 0, charging = 0, workplaceCharging = 0, withoutTravelEntity = 0;
            List<object> rows = new List<object>();
            Citizen[] people = manager.m_citizens.m_buffer;
            for (int i = 1; i < people.Length; i++)
            {
                Citizen data = people[i];
                if ((data.m_flags & (Citizen.Flags.Created | Citizen.Flags.Tourist | Citizen.Flags.Dead | Citizen.Flags.DummyTraffic)) != Citizen.Flags.Created ||
                    data.m_homeBuilding == 0) continue;
                bool employed = data.m_workBuilding != 0 && (data.m_flags & Citizen.Flags.Student) == 0;
                Resident member;
                if (!cohort.TryGetValue((uint)i, out member))
                {
                    humans++;
                    if (employed) humanEmployed++;
                    continue;
                }
                if (!ConfirmHome(manager, (uint)i, data.m_homeBuilding, member)) continue;
                androidHomeMembers++;
                if (data.CurrentLocation != Citizen.Location.Moving) member.ShopReturnHome = 0;
                ObserveWalking(manager, (uint)i, data, member);
                if (clockRunning && fullLifeSimulation && localDeliveries != null && member.Battery >= 30 &&
                    (data.m_flags & Citizen.Flags.Student) == 0 && localDeliveries.HireForLocalDelivery((uint)i, ref people[i], now))
                {
                    data = people[i];
                    employed = true;
                }
                bool jobMember = employed && InWorkUnit(manager, data.m_workBuilding, (uint)i);
                if (jobMember) { androidEmployed++; androidWorkMembers++; }
                if (clockRunning && fullLifeSimulation && localDeliveries != null &&
                    RecoverMissingTravel((uint)i, ref people[i], member, now, jobMember))
                    data = people[i];
                bool delivering = clockRunning && jobMember && localDeliveries != null && member.Battery >= 30 &&
                    localDeliveries.Dispatch((uint)i, ref people[i], now);
                if (delivering || (clockRunning && (localDeliveries == null || !localDeliveries.IsDelivering((uint)i)) &&
                    ScheduleShift((uint)i, ref people[i], member, now, jobMember)))
                    data = people[i];
                bool home = data.CurrentLocation == Citizen.Location.Home;
                bool work = data.CurrentLocation == Citizen.Location.Work;
                bool running = data.CurrentLocation == Citizen.Location.Moving;
                if (home) { atHome++; member.HomeSamples++; }
                if (work) { atWork++; member.WorkSamples++; }
                if (running) { moving++; member.MovingSamples++; }
                // Reserve charging lets an employee with a flat battery leave an actual powered workplace.
                bool atPoweredWork = work && jobMember && member.Battery < 45 && HasElectricity(data.m_workBuilding);
                bool charge = (home && member.Battery < 100 && HasElectricity(data.m_homeBuilding)) || atPoweredWork;
                if (atPoweredWork) workplaceCharging++;
                if (charge) charging++;
                if (charge && !member.Charging) member.ChargeStarts++;
                member.Charging = charge;
                if (charge) member.Battery = Math.Min(100, member.Battery + minutes * .25);
                CitizenInstance instance = data.m_instance > 0 ? manager.m_instances.m_buffer[data.m_instance] : new CitizenInstance();
                if (running && (instance.m_path != 0 || data.m_vehicle != 0))
                    inTransit++;
                if (running && data.m_instance == 0 && data.m_vehicle == 0) withoutTravelEntity++;
                NativeCityLedger.Record(new NativeCityLedger.Android {
                    Id=(uint)i, Home=member.Home, HomeUnit=member.HomeUnit, Battery=(float)member.Battery
                });
                if (jobMember && instance.m_path != 0 && instance.m_sourceBuilding == member.Home &&
                    instance.m_targetBuilding == data.m_workBuilding)
                    member.CommuteSamples++;
                rows.Add(new Dictionary<string, object> {
                    {"citizenId",i}, {"homeBuilding",data.m_homeBuilding}, {"homeUnit",member.HomeUnit},
                    {"workBuilding",data.m_workBuilding}, {"workUnitConfirmed",jobMember}, {"location",data.CurrentLocation.ToString()},
                    {"movingSamples",member.MovingSamples}, {"workSamples",member.WorkSamples}, {"homeSamples",member.HomeSamples},
                    {"commutesStarted",member.CommutesStarted}, {"returnsStarted",member.ReturnsStarted},
                    {"commuteSamples",member.CommuteSamples},
                    {"nativeWorkArrivals",member.NativeWorkArrivals}, {"nativeWorkArrivalFailures",member.NativeWorkArrivalFailures},
                    {"nativeCommutePathFailures",member.NativeCommutePathFailures}, {"nativeMovingCommuteReleases",member.NativeMovingCommuteReleases},
                    {"travelRecoveries",member.TravelRecoveries},
                    {"charging",charge}, {"chargeStarts",member.ChargeStarts}, {"battery",member.Battery},
                    {"walkedMeters",member.WalkedMeters}, {"visitBuilding",data.m_visitBuilding},
                    {"shopReturnsStarted",member.ShopReturnsStarted}, {"shopReturnsArrived",member.ShopReturnsArrived},
                    {"shopReturnSource",member.ShopReturnSource}, {"shopReturnHome",member.ShopReturnHome},
                    {"instanceId",data.m_instance}, {"vehicleId",data.m_vehicle}, {"pathId",instance.m_path},
                    {"sourceBuilding",instance.m_sourceBuilding}, {"targetBuilding",instance.m_targetBuilding}
                });
            }
            Dictionary<string, object> report = new Dictionary<string, object> {
                {"utc",DateTime.UtcNow.ToString("o")}, {"gameTime",now.ToString("s")}, {"runId",runId}, {"caseId",caseId},
                {"status",error == null ? (caseId != null ? "running" : "baseline") : "limited"}, {"error",error},
                {"nativeReleaseHookActive",lifecyclePatch != null}, {"androidIdentities",cohort.Count},
                {"nativeDeliveries",localDeliveries == null ? null : localDeliveries.Snapshot()},
                {"localDeliveryAdmissions",localDeliveryAdmissions},
                {"androidRenderHookActive",renderer == this}, {"visualError",visualError},
                {"nativeHumanResidents",humans}, {"humansWithWorkplace",humanEmployed}, {"androidHomeMembers",androidHomeMembers},
                {"androidWorkMembers",androidWorkMembers}, {"androidAtHome",atHome}, {"androidAtWork",atWork}, {"androidMoving",moving},
                {"androidInTransit",inTransit},
                {"androidMovingWithoutTravelEntity",withoutTravelEntity}, {"androidTravelRecoveries",travelRecoveries},
                {"recentTravelRecoveries",recentTravelRecoveries.ToArray()},
                {"nativeCommuteInterruptions",new Dictionary<string, object> {
                    {"releaseTraceActive",commuteReleaseTraceActive}, {"pathFailures",commutePathFailures},
                    {"releasesAfterPathFailure",commuteReleasesAfterPathFailure}, {"releasesWithoutPathFailure",commuteReleasesWithoutPathFailure},
                    {"examples",commuteInterruptionExamples.ToArray()}
                }},
                {"workplaceReserveCharging",workplaceCharging},
                {"androids",rows}
            };
            return new Sample {
                Json = JsonData.Stringify(report),
                Hud = "ANDROID RESIDENCE / CS1\nHumans with jobs " + humanEmployed + " | Androids with jobs " + androidEmployed +
                    "\nAndroid residents " + androidHomeMembers + " | Home " + atHome + " | In transit " + inTransit + " | At work " + atWork,
                PopulationText = "入居　人 " + humans + " / アンドロイド " + androidHomeMembers +
                    "\n就業　人 " + humanEmployed + " / アンドロイド " + androidEmployed +
                    "\nアンドロイド　移動 " + inTransit + " / 充電 " + charging +
                    "\n店舗への配送　" + (localDeliveries == null ? "―" : localDeliveries.Delivered.ToString())
            };
        }

        private bool RecoverMissingTravel(uint id, ref Citizen citizen, Resident member, DateTime now, bool hasJob)
        {
            if (citizen.CurrentLocation != Citizen.Location.Moving || citizen.m_instance != 0 || citizen.m_vehicle != 0 ||
                localDeliveries.IsDelivering(id))
            {
                member.MissingTravelSince = DateTime.MinValue;
                return false;
            }
            if (member.MissingTravelSince == DateTime.MinValue)
            {
                member.MissingTravelSince = now;
                return false;
            }
            if (now - member.MissingTravelSince < MissingTravelGrace) return false;
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            if ((buildings[member.Home].m_flags & (NativeBuilding.Flags.Created | NativeBuilding.Flags.Deleted)) != NativeBuilding.Flags.Created)
                return false;
            // A missing native traveller has no live position to restore. Retry at the validated departure site or registered home.
            bool resumeAtWork = member.TravelOrigin == Citizen.Location.Work && hasJob && member.TravelSource == citizen.m_workBuilding;
            bool resumeAtVisit = member.TravelOrigin == Citizen.Location.Visit && member.TravelSource != 0 &&
                member.TravelSource == citizen.m_visitBuilding && IsCommercialVisit(member.TravelSource);
            citizen.CurrentLocation = resumeAtWork ? Citizen.Location.Work : resumeAtVisit ? Citizen.Location.Visit : Citizen.Location.Home;
            ushort origin = resumeAtWork || resumeAtVisit ? member.TravelSource : member.Home;
            if (recentTravelRecoveries.Count == 64) recentTravelRecoveries.Dequeue();
            recentTravelRecoveries.Enqueue(new Dictionary<string, object> {{"citizenId",id}, {"retrySite",origin},
                {"retryLocation",citizen.CurrentLocation.ToString()}, {"priorSource",member.TravelSource},
                {"priorTarget",member.TravelTarget}, {"missingSince",member.MissingTravelSince.ToString("s")}, {"gameTime",now.ToString("s")}});
            member.MissingTravelSince = DateTime.MinValue;
            member.ShopReturnHome = 0;
            member.TravelSource = member.TravelTarget = 0;
            member.TravelRecoveries++;
            travelRecoveries++;
            return true;
        }

        private static void ObserveWalking(CitizenManager manager, uint id, Citizen citizen, Resident member)
        {
            if (citizen.CurrentLocation != Citizen.Location.Moving || citizen.m_vehicle != 0 || citizen.m_instance == 0)
            {
                member.WalkingInstance = 0;
                return;
            }
            CitizenInstance instance = manager.m_instances.m_buffer[citizen.m_instance];
            if (instance.m_citizen != id || (instance.m_flags & CitizenInstance.Flags.Character) == 0)
            {
                member.WalkingInstance = 0;
                return;
            }
            Vector3 position = instance.m_frame0.m_position;
            if (member.WalkingInstance == citizen.m_instance)
            {
                float meters = Vector3.Distance(member.WalkingPosition, position);
                member.WalkedMeters += meters;
                member.Battery = Math.Max(0, member.Battery - meters * BatteryPerWalkingMeter);
            }
            member.WalkingInstance = citizen.m_instance;
            member.WalkingPosition = position;
        }

        private static bool HasElectricity(ushort id)
        {
            NativeBuilding building = BuildingManager.instance.m_buildings.m_buffer[id];
            const NativeBuilding.Flags required = NativeBuilding.Flags.Active | NativeBuilding.Flags.Created;
            return (building.m_flags & required) == required &&
                (building.m_problems & (Notification.Problem1.Electricity | Notification.Problem1.ElectricityNotConnected)).IsNone;
        }

        internal static bool HasResidentialUtilities(ushort id, NativeBuilding building)
        {
            if ((building.m_flags & (NativeBuilding.Flags.Abandoned | NativeBuilding.Flags.Deleted)) != 0 ||
                !HasElectricity(id) || !(building.m_problems & Notification.Problem1.DirtyWater).IsNone)
                return false;
            bool water, sewage;
            byte pollution;
            WaterManager.instance.CheckWater(building.m_position, out water, out sewage, out pollution);
            return water && sewage;
        }

        private bool ConfirmHome(CitizenManager manager, uint id, ushort home, Resident member)
        {
            CitizenUnit[] units = manager.m_units.m_buffer;
            if (member.Home == home && member.HomeUnit < units.Length && units[member.HomeUnit].m_building == home &&
                (units[member.HomeUnit].m_flags & CitizenUnit.Flags.Home) != 0 && Contains(units[member.HomeUnit], id)) return true;
            if (lifecyclePatch == null) return false;
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            if (home == 0 || home >= buildings.Length) return false;
            uint unitId = buildings[home].m_citizenUnits;
            for (int step = 0; unitId != 0 && unitId < units.Length && step < MaximumUnitsPerBuilding; step++)
            {
                CitizenUnit candidate = units[unitId];
                if (candidate.m_building == home && (candidate.m_flags & CitizenUnit.Flags.Home) != 0 && Contains(candidate, id))
                {
                    member.Home = home;
                    member.HomeUnit = unitId;
                    return true;
                }
                unitId = candidate.m_nextUnit;
            }
            return false;
        }

        private static bool ScheduleShift(uint id, ref Citizen citizen, Resident member, DateTime gameTime, bool hasJob)
        {
            bool goingToWork = hasJob && citizen.CurrentLocation == Citizen.Location.Home && gameTime.Hour >= 7 &&
                gameTime.Hour < 16 && member.Battery >= 30 && member.LastCommuteDay != gameTime.Date;
            bool atShop = citizen.CurrentLocation == Citizen.Location.Visit && IsCommercialVisit(citizen.m_visitBuilding);
            bool goingHome = (atShop || (hasJob && citizen.CurrentLocation == Citizen.Location.Work && gameTime.Hour >= 18)) &&
                member.LastReturnDay != gameTime.Date;
            if (!goingToWork && !goingHome) return false;
            ushort currentSite = atShop ? citizen.m_visitBuilding : citizen.m_workBuilding;
            ushort from = goingToWork ? citizen.m_homeBuilding : currentSite;
            ushort to = goingToWork ? citizen.m_workBuilding : citizen.m_homeBuilding;
            if (!TryStartNativeMovement(id, ref citizen, from, to)) return false;
            if (goingToWork)
            {
                member.LastCommuteDay = gameTime.Date;
                member.CommutesStarted++;
            }
            else
            {
                member.LastReturnDay = gameTime.Date;
                member.ReturnsStarted++;
                if (atShop)
                {
                    member.ShopReturnSource = from;
                    member.ShopReturnHome = to;
                    member.ShopReturnsStarted++;
                }
            }
            return true;
        }

        private static bool IsCommercialVisit(ushort site)
        {
            if (site == 0) return false;
            NativeBuilding building = BuildingManager.instance.m_buildings.m_buffer[site];
            return (building.m_flags & (NativeBuilding.Flags.Created | NativeBuilding.Flags.Deleted)) == NativeBuilding.Flags.Created &&
                building.Info != null && building.Info.m_class.m_service == ItemClass.Service.Commercial;
        }

        internal static void ObserveNativeHomeArrival(uint id, ushort target, bool success)
        {
            AndroidLifePilot life = active;
            Resident member;
            if (life == null || !life.cohort.TryGetValue(id, out member) || member.ShopReturnHome != target) return;
            if (!success)
            {
                member.ShopReturnHome = 0;
                return;
            }
            Citizen[] people = CitizenManager.instance.m_citizens.m_buffer;
            if (people[id].CurrentLocation != Citizen.Location.Home || people[id].m_homeBuilding != target) return;
            if (people[id].m_visitBuilding == member.ShopReturnSource) people[id].SetVisitplace(id, 0, 0);
            member.ShopReturnHome = 0;
            member.ShopReturnsArrived++;
        }

        internal static bool TryStartNativeMovement(uint id, ref Citizen citizen, ushort from, ushort to)
        {
            if (from == 0 || to == 0 || citizen.m_vehicle != 0) return false;
            CitizenInfo info = citizen.GetCitizenInfo(id);
            HumanAI ai = info == null ? null : info.GetAI() as HumanAI;
            if (ai == null) return false;
            Citizen.Location previous = citizen.CurrentLocation;
            if (!NativeCommuteRouteAudit.StartMovement(ai, id, ref citizen, from, to)) return false;
            CitizenInstance[] instances = CitizenManager.instance.m_instances.m_buffer;
            if (citizen.m_instance != 0 && citizen.m_instance < instances.Length)
            {
                CitizenInstance movement = instances[citizen.m_instance];
                if (movement.m_citizen == id && movement.m_path != 0)
                {
                    if (movement.m_targetBuilding != to) return false;
                    Resident member;
                    if (active != null && active.cohort.TryGetValue(id, out member))
                    {
                        member.TravelOrigin = previous;
                        member.TravelSource = from;
                        member.TravelTarget = to;
                        member.MissingTravelSince = DateTime.MinValue;
                        member.CommuteInstance = previous == Citizen.Location.Home && from == citizen.m_homeBuilding &&
                            to == citizen.m_workBuilding ? citizen.m_instance : (ushort)0;
                        member.CommutePathFailed = false;
                    }
                    return true;
                }
            }
            // StartMoving can report success even when ResidentAI.SetTarget fails to start a path.
            if (citizen.m_vehicle == 0) citizen.CurrentLocation = previous;
            return false;
        }

        internal static void ObserveNativeWorkArrival(uint id, ushort workplace, bool success)
        {
            Resident member;
            CitizenManager manager = CitizenManager.instance;
            if (active == null || !active.cohort.TryGetValue(id, out member)) return;
            Citizen citizen = manager.m_citizens.m_buffer[id];
            if (citizen.m_workBuilding != workplace || !InWorkUnit(manager, workplace, id)) return;
            if (!success) member.NativeWorkArrivalFailures++;
            else if (citizen.CurrentLocation == Citizen.Location.Work) member.NativeWorkArrivals++;
        }

        internal static bool CanDeliverAtWork(uint id, ref Citizen citizen)
        {
            AndroidLifePilot life = active;
            Resident member;
            CitizenManager manager = CitizenManager.instance;
            return life != null && life.cohort.TryGetValue(id, out member) && member.Battery >= 30 &&
                (citizen.m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead | Citizen.Flags.Student)) == Citizen.Flags.Created &&
                citizen.CurrentLocation == Citizen.Location.Work && citizen.m_workBuilding != 0 &&
                life.ConfirmHome(manager, id, citizen.m_homeBuilding, member) && InWorkUnit(manager, citizen.m_workBuilding, id);
        }

        private static bool InWorkUnit(CitizenManager manager, ushort workplace, uint citizen)
        {
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            if (workplace >= buildings.Length || (buildings[workplace].m_flags & NativeBuilding.Flags.Created) == 0) return false;
            uint unit = buildings[workplace].m_citizenUnits;
            for (int i = 0; unit != 0 && unit < manager.m_units.m_buffer.Length && i < MaximumUnitsPerBuilding; i++)
            {
                CitizenUnit data = manager.m_units.m_buffer[unit];
                if ((data.m_flags & CitizenUnit.Flags.Work) != 0 && Contains(data, citizen)) return true;
                unit = data.m_nextUnit;
            }
            return false;
        }

        internal static bool Empty(CitizenUnit unit)
        {
            for (int i = 0; i < MembersPerUnit; i++) if (unit.GetCitizen(i) != 0) return false;
            return true;
        }

        private static bool Contains(CitizenUnit unit, uint citizen)
        {
            for (int i = 0; i < MembersPerUnit; i++) if (unit.GetCitizen(i) == citizen) return true;
            return false;
        }

        private void UpdateVisuals()
        {
            if (renderer != this) return;
            CitizenManager manager = CitizenManager.instance;
            uint[] current = residents;
            releasedVisuals.Clear();
            foreach (KeyValuePair<uint, object> prior in visuals)
                if (Array.BinarySearch(current, prior.Key) < 0)
                    releasedVisuals.Add(prior.Key);
            foreach (uint id in releasedVisuals)
            {
                city.ReleaseResidentAndroid(visuals[id]);
                visuals.Remove(id);
            }
            foreach (uint id in current)
            {
                Citizen data = manager.m_citizens.m_buffer[id];
                ushort iid = data.m_instance;
                CitizenInstance instance = iid == 0 ? new CitizenInstance() : manager.m_instances.m_buffer[iid];
                bool visible = iid != 0 && instance.m_citizen == id && (instance.m_flags & CitizenInstance.Flags.Character) != 0;
                object visual;
                if (!visuals.TryGetValue(id, out visual))
                {
                    if (!visible) continue;
                    visual = city.CreateResidentAndroid((int)id);
                    visuals.Add(id, visual);
                }
                city.PoseResidentAndroid(visual, visible, instance.GetLastFramePosition());
            }
        }

        private void WriteSnapshot(string json)
        {
            string output = Path.Combine(root, "android-life-state.json");
            string temp = output + ".tmp";
            File.WriteAllText(temp,json,new UTF8Encoding(false));
            if (File.Exists(output)) File.Replace(temp,output,null);
            else File.Move(temp,output);
        }

        public void OnGUI()
        {
            if (populationCard != null && populationCard.Visible) return;
            Rect panel = new Rect(Screen.width - 420, Screen.height - 202, 400, 92);
            GUI.Box(panel, "");
            GUI.Label(new Rect(panel.x + 10, panel.y + 6, panel.width - 20, panel.height - 8), hud);
        }

        public void OnDestroy()
        {
            if (employmentTrial != null && employmentTrial.CommuteAudit != null) employmentTrial.CommuteAudit.Dispose();
            if (active == this) active = null;
            if (renderer == this) renderer = null;
            if (renderPatch != null) renderPatch.UnpatchAll(RenderPatchId);
            if (lifecyclePatch != null) lifecyclePatch.UnpatchAll(RenderPatchId + ".lifecycle");
            if (localDeliveries != null) localDeliveries.Dispose();
            if (populationCard != null) populationCard.Dispose();
        }
    }
}
