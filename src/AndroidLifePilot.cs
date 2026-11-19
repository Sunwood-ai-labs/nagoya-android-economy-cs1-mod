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
        private const string RenderPatchId = "sunwood.nagoya.android-residence-render";
        private static AndroidLifePilot renderer;
        private static AndroidLifePilot active;
        private Harmony renderPatch;
        private Harmony lifecyclePatch;
        private NativeAndroidDeliveries localDeliveries;
        private NativePopulationCard populationCard;
        private readonly Dictionary<uint, Resident> cohort = new Dictionary<uint, Resident>();
        private volatile uint[] residents = new uint[0];
        private readonly Dictionary<uint, object> visuals = new Dictionary<uint, object>();
        private readonly List<uint> releasedVisuals = new List<uint>();
        private CitywideScene city;
        private string root, runId, caseId, error, visualError;
        private bool attempted;
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
            public int HomeSamples, WorkSamples, MovingSamples, ChargeStarts;
            public int CommutesStarted, ReturnsStarted, CommuteSamples;
            public DateTime LastCommuteDay, LastReturnDay;
            public bool Charging;
        }

        private sealed class Sample
        {
            public string Json, Hud, PopulationText;
        }

        internal void Initialize(CitywideScene host, string data, string id)
        {
            city = host;
            root = data;
            runId = id;
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
                try { localDeliveries = new NativeAndroidDeliveries(); }
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
            if (life.localDeliveries != null) life.localDeliveries.EndTrip(__0);
            life.UpdateResidentIds();
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
            if (Time.realtimeSinceStartup < nextSample || Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            nextSample = Time.realtimeSinceStartup + 4;
            SimulationManager.instance.AddAction(delegate {
                try
                {
                    CreateRequestedResidents();
                    Interlocked.Exchange<Sample>(ref sample, Observe());
                }
                catch (Exception ex) { Debug.LogException(ex); }
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
                double count = (double)config["androidResidents"];
                int limit = root == NativeCityRoadBuilder.Root ? NativeCityLedger.MaxResidents : PilotMaximumAndroids;
                if (caseId.Length < 1 || caseId.Length > 60 || count != Math.Floor(count) || count < 0 || count > limit)
                    throw new InvalidDataException("Scenario case or cohort size");
                int requested = Math.Max(0, (int)count - cohort.Count);
                CitizenManager citizens = CitizenManager.instance;
                NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
                Randomizer random = new Randomizer(5102026);
                List<uint> added = new List<uint>();
                foreach (int i in HomeOrder(buildings.Length))
                {
                    if (added.Count >= requested) break;
                    NativeBuilding building = buildings[i];
                    BuildingInfo info = building.Info;
                    if ((building.m_flags & (NativeBuilding.Flags.Created | NativeBuilding.Flags.Abandoned)) != NativeBuilding.Flags.Created ||
                        info == null || info.m_class.m_service != ItemClass.Service.Residential) continue;
                    uint unitId = building.m_citizenUnits;
                    for (int u = 0; unitId != 0 && unitId < citizens.m_units.m_buffer.Length && u < MaximumUnitsPerBuilding && added.Count < requested; u++)
                    {
                        CitizenUnit unit = citizens.m_units.m_buffer[unitId];
                        if ((unit.m_flags & CitizenUnit.Flags.Home) != 0 && unit.m_building == i && Empty(unit))
                        {
                            uint citizenId;
                            Citizen.Gender gender = (added.Count & 1) == 0 ? Citizen.Gender.Male : Citizen.Gender.Female;
                            if (!citizens.CreateCitizen(out citizenId, 120, 0, ref random, gender))
                                throw new InvalidOperationException("CS1 could not create a resident");
                            citizens.m_citizens.m_buffer[citizenId].SetHome(citizenId, (ushort)i, unitId);
                            if (citizens.m_citizens.m_buffer[citizenId].m_homeBuilding != i || !Contains(citizens.m_units.m_buffer[unitId], citizenId))
                            {
                                citizens.ReleaseCitizen(citizenId);
                                throw new InvalidOperationException("CS1 refused the vacant household seat");
                            }
                            citizens.m_citizens.m_buffer[citizenId].CurrentLocation = Citizen.Location.Home;
                            cohort.Add(citizenId, new Resident { Home = (ushort)i, HomeUnit = unitId });
                            added.Add(citizenId);
                            NativeCityLedger.Record(new NativeCityLedger.Android {
                                Id=citizenId, Home=(ushort)i, HomeUnit=unitId, Battery=65
                            });
                            if (root == NativeCityRoadBuilder.Root) break;
                        }
                        unitId = unit.m_nextUnit;
                    }
                }
                if (added.Count < requested) error = "Vacant residential units limited the experiment to " + cohort.Count;
            }
            catch (Exception ex) { attempted = true; error = ex.GetBaseException().Message; }
            finally
            {
                UpdateResidentIds();
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
            CitizenManager manager = CitizenManager.instance;
            DateTime now = SimulationManager.instance.m_currentGameTime;
            bool clockRunning = !SimulationManager.instance.SimulationPaused;
            if (localDeliveries != null && cohort.Count > 0) localDeliveries.BeginSample();
            double minutes = lastGameTime == DateTime.MinValue ? 0 : Math.Max(0, Math.Min(180, (now - lastGameTime).TotalMinutes));
            lastGameTime = now;
            int humans = 0, humanEmployed = 0, androidEmployed = 0, atHome = 0, atWork = 0, moving = 0;
            int androidHomeMembers = 0, androidWorkMembers = 0, charging = 0, workplaceCharging = 0;
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
                bool jobMember = employed && InWorkUnit(manager, data.m_workBuilding, (uint)i);
                if (jobMember) { androidEmployed++; androidWorkMembers++; }
                bool delivering = clockRunning && jobMember && localDeliveries != null && member.Battery >= 30 &&
                    localDeliveries.Dispatch((uint)i, ref people[i], now);
                if (delivering || (clockRunning && jobMember && ScheduleShift((uint)i, ref people[i], member, now)))
                    data = people[i];
                bool home = data.CurrentLocation == Citizen.Location.Home;
                bool work = data.CurrentLocation == Citizen.Location.Work;
                bool running = data.CurrentLocation == Citizen.Location.Moving;
                if (home) { atHome++; member.HomeSamples++; }
                if (work) { atWork++; member.WorkSamples++; }
                if (running) { moving++; member.MovingSamples++; }
                // Reserve charging lets an employee with a flat battery leave an actual powered workplace.
                bool atPoweredWork = work && jobMember && member.Battery < 45 && HasElectricity(data.m_workBuilding);
                bool charge = (home && member.Battery < 100 && (data.m_flags & Citizen.Flags.NoElectricity) == 0) || atPoweredWork;
                if (atPoweredWork) workplaceCharging++;
                if (charge) charging++;
                if (charge && !member.Charging) member.ChargeStarts++;
                member.Charging = charge;
                if (charge) member.Battery = Math.Min(100, member.Battery + minutes * .25);
                CitizenInstance instance = data.m_instance > 0 ? manager.m_instances.m_buffer[data.m_instance] : new CitizenInstance();
                if (running && (instance.m_path != 0 || data.m_vehicle != 0))
                    member.Battery = Math.Max(0, member.Battery - minutes * .02);
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
                    {"charging",charge}, {"chargeStarts",member.ChargeStarts}, {"battery",member.Battery},
                    {"instanceId",data.m_instance}, {"pathId",instance.m_path}, {"sourceBuilding",instance.m_sourceBuilding}, {"targetBuilding",instance.m_targetBuilding}
                });
            }
            Dictionary<string, object> report = new Dictionary<string, object> {
                {"utc",DateTime.UtcNow.ToString("o")}, {"gameTime",now.ToString("s")}, {"runId",runId}, {"caseId",caseId},
                {"status",error == null ? (caseId != null ? "running" : "baseline") : "limited"}, {"error",error},
                {"nativeReleaseHookActive",lifecyclePatch != null}, {"androidIdentities",cohort.Count},
                {"nativeDeliveries",localDeliveries == null ? null : localDeliveries.Snapshot()},
                {"androidRenderHookActive",renderer == this}, {"visualError",visualError},
                {"nativeHumanResidents",humans}, {"humansWithWorkplace",humanEmployed}, {"androidHomeMembers",androidHomeMembers},
                {"androidWorkMembers",androidWorkMembers}, {"androidAtHome",atHome}, {"androidAtWork",atWork}, {"androidMoving",moving},
                {"workplaceReserveCharging",workplaceCharging},
                {"androids",rows}
            };
            return new Sample {
                Json = JsonData.Stringify(report),
                Hud = "ANDROID RESIDENCE / CS1\nHumans with jobs " + humanEmployed + " | Androids with jobs " + androidEmployed +
                    "\nAndroid residents " + androidHomeMembers + " | Home " + atHome + " | Moving " + moving + " | At work " + atWork,
                PopulationText = "入居　人 " + humans + " / アンドロイド " + androidHomeMembers +
                    "\n就業　人 " + humanEmployed + " / アンドロイド " + androidEmployed +
                    "\nアンドロイド　移動 " + moving + " / 充電 " + charging +
                    "\n店舗への配送　" + (localDeliveries == null ? "―" : localDeliveries.Delivered.ToString())
            };
        }

        private static bool HasElectricity(ushort id)
        {
            NativeBuilding building = BuildingManager.instance.m_buildings.m_buffer[id];
            const NativeBuilding.Flags required = NativeBuilding.Flags.Active | NativeBuilding.Flags.Created;
            return (building.m_flags & required) == required &&
                (building.m_problems & (Notification.Problem1.Electricity | Notification.Problem1.ElectricityNotConnected)).IsNone;
        }

        private bool ConfirmHome(CitizenManager manager, uint id, ushort home, Resident member)
        {
            CitizenUnit[] units = manager.m_units.m_buffer;
            if (member.Home == home && member.HomeUnit < units.Length && Contains(units[member.HomeUnit], id)) return true;
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

        private static bool ScheduleShift(uint id, ref Citizen citizen, Resident member, DateTime gameTime)
        {
            bool goingToWork = citizen.CurrentLocation == Citizen.Location.Home && gameTime.Hour >= 7 &&
                gameTime.Hour < 16 && member.Battery >= 30 && member.LastCommuteDay != gameTime.Date;
            bool goingHome = citizen.CurrentLocation == Citizen.Location.Work && gameTime.Hour >= 18 &&
                member.LastReturnDay != gameTime.Date;
            if (!goingToWork && !goingHome) return false;
            CitizenInfo info = citizen.GetCitizenInfo(id);
            HumanAI ai = info == null ? null : info.GetAI() as HumanAI;
            if (ai == null) return false;
            ushort from = goingToWork ? citizen.m_homeBuilding : citizen.m_workBuilding;
            ushort to = goingToWork ? citizen.m_workBuilding : citizen.m_homeBuilding;
            if (!ai.StartMoving(id, ref citizen, from, to)) return false;
            citizen.CurrentLocation = Citizen.Location.Moving;
            if (goingToWork)
            {
                member.LastCommuteDay = gameTime.Date;
                member.CommutesStarted++;
            }
            else
            {
                member.LastReturnDay = gameTime.Date;
                member.ReturnsStarted++;
            }
            return true;
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

        private static bool Empty(CitizenUnit unit)
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
            if (active == this) active = null;
            if (renderer == this) renderer = null;
            if (renderPatch != null) renderPatch.UnpatchAll(RenderPatchId);
            if (lifecyclePatch != null) lifecyclePatch.UnpatchAll(RenderPatchId + ".lifecycle");
            if (localDeliveries != null) localDeliveries.Dispose();
            if (populationCard != null) populationCard.Dispose();
        }
    }
}
