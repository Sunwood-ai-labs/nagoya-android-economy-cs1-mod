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
        private const int MaximumAndroids = 32;
        private const int MaximumUnitsPerBuilding = 2048;
        private const string RenderPatchId = "sunwood.nagoya.android-residence-render";
        private static AndroidLifePilot renderer;
        private Harmony renderPatch;
        private readonly Dictionary<uint, Resident> cohort = new Dictionary<uint, Resident>();
        private volatile uint[] residents = new uint[0];
        private readonly Dictionary<uint, object> visuals = new Dictionary<uint, object>();
        private CitywideScene city;
        private string root, runId, caseId, error;
        private bool attempted;
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
            public string Json, Hud;
        }

        internal void Initialize(CitywideScene host, string data, string id)
        {
            city = host;
            root = data;
            runId = id;
            try
            {
                MethodInfo target = typeof(CitizenInstance).GetMethod("RenderInstance", new Type[] {
                    typeof(RenderManager.CameraInfo), typeof(CitizenInfo), typeof(Vector3)
                });
                MethodInfo prefix = typeof(AndroidLifePilot).GetMethod("KeepNativeSkin", BindingFlags.Static | BindingFlags.NonPublic);
                if (target == null) throw new MissingMethodException("CitizenInstance.RenderInstance");
                renderPatch = new Harmony(RenderPatchId);
                renderPatch.Patch(target, new HarmonyMethod(prefix), null, null, null);
                renderer = this;
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private static bool KeepNativeSkin(ref CitizenInstance __instance)
        {
            AndroidLifePilot scene = renderer;
            return scene == null || Array.IndexOf(scene.residents, __instance.m_citizen) < 0;
        }

        public void Update()
        {
            if (city == null) return;
            Sample ready = Interlocked.Exchange<Sample>(ref sample, null);
            if (ready != null)
            {
                hud = ready.Hud;
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
            if (attempted) return;
            string request = Path.Combine(root, "android-life-scenario.json");
            if (!File.Exists(request)) return;
            attempted = true;
            try
            {
                if (new FileInfo(request).Length > 4096) throw new InvalidDataException("Scenario file limit");
                Dictionary<string, object> config = (Dictionary<string, object>)JsonData.Parse(File.ReadAllText(request));
                if ((string)config["runId"] != runId) throw new InvalidDataException("Scenario belongs to a different game run");
                caseId = (string)config["caseId"];
                double count = (double)config["androidResidents"];
                if (caseId.Length < 1 || caseId.Length > 60 || count != Math.Floor(count) || count < 0 || count > MaximumAndroids)
                    throw new InvalidDataException("Scenario case or cohort size");
                int requested = (int)count;
                CitizenManager citizens = CitizenManager.instance;
                NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
                Randomizer random = new Randomizer(5102026);
                List<uint> added = new List<uint>();
                for (int i = 1; i < buildings.Length && added.Count < requested; i++)
                {
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
                            residents = added.ToArray();
                        }
                        unitId = unit.m_nextUnit;
                    }
                }
                if (added.Count < requested) error = "Vacant residential units limited the experiment to " + added.Count;
            }
            catch (Exception ex) { error = ex.GetBaseException().Message; }
        }

        private Sample Observe()
        {
            CitizenManager manager = CitizenManager.instance;
            DateTime now = SimulationManager.instance.m_currentGameTime;
            double minutes = lastGameTime == DateTime.MinValue ? 0 : Math.Max(0, Math.Min(180, (now - lastGameTime).TotalMinutes));
            lastGameTime = now;
            int humans = 0, humanEmployed = 0, androidEmployed = 0, atHome = 0, atWork = 0, moving = 0;
            int androidHomeMembers = 0, androidWorkMembers = 0;
            List<object> rows = new List<object>();
            Citizen[] people = manager.m_citizens.m_buffer;
            for (int i = 1; i < people.Length; i++)
            {
                Citizen data = people[i];
                if ((data.m_flags & (Citizen.Flags.Created | Citizen.Flags.Tourist | Citizen.Flags.Dead | Citizen.Flags.DummyTraffic)) != Citizen.Flags.Created ||
                    data.m_homeBuilding == 0) continue;
                bool employed = data.m_workBuilding != 0 && (data.m_flags & Citizen.Flags.Student) == 0;
                Resident member;
                if (!cohort.TryGetValue((uint)i, out member) || member.Home != data.m_homeBuilding ||
                    !Contains(manager.m_units.m_buffer[member.HomeUnit], (uint)i))
                {
                    humans++;
                    if (employed) humanEmployed++;
                    continue;
                }
                androidHomeMembers++;
                bool jobMember = employed && InWorkUnit(manager, data.m_workBuilding, (uint)i);
                if (jobMember) { androidEmployed++; androidWorkMembers++; }
                if (jobMember && ScheduleShift((uint)i, ref people[i], member, now))
                    data = people[i];
                bool home = data.CurrentLocation == Citizen.Location.Home;
                bool work = data.CurrentLocation == Citizen.Location.Work;
                bool running = data.CurrentLocation == Citizen.Location.Moving;
                if (home) { atHome++; member.HomeSamples++; }
                if (work) { atWork++; member.WorkSamples++; }
                if (running) { moving++; member.MovingSamples++; }
                bool charge = home && member.Battery < 100 && (data.m_flags & Citizen.Flags.NoElectricity) == 0;
                if (charge && !member.Charging) member.ChargeStarts++;
                member.Charging = charge;
                if (charge) member.Battery = Math.Min(100, member.Battery + minutes * .25);
                if (work || running) member.Battery = Math.Max(0, member.Battery - minutes * .10);
                CitizenInstance instance = data.m_instance > 0 ? manager.m_instances.m_buffer[data.m_instance] : new CitizenInstance();
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
                {"status",error == null ? (attempted ? "running" : "baseline") : "limited"}, {"error",error},
                {"nativeHumanResidents",humans}, {"humansWithWorkplace",humanEmployed}, {"androidHomeMembers",androidHomeMembers},
                {"androidWorkMembers",androidWorkMembers}, {"androidAtHome",atHome}, {"androidAtWork",atWork}, {"androidMoving",moving},
                {"androids",rows}
            };
            return new Sample {
                Json = JsonData.Stringify(report),
                Hud = "ANDROID RESIDENCE / CS1\nHumans with jobs " + humanEmployed + " | Androids with jobs " + androidEmployed +
                    "\nAndroid residents " + androidHomeMembers + " | Home " + atHome + " | Moving " + moving + " | At work " + atWork
            };
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
            if (goingToWork) member.LastCommuteDay = gameTime.Date;
            else member.LastReturnDay = gameTime.Date;
            ushort from = goingToWork ? citizen.m_homeBuilding : citizen.m_workBuilding;
            ushort to = goingToWork ? citizen.m_workBuilding : citizen.m_homeBuilding;
            if (!ai.StartMoving(id, ref citizen, from, to)) return false;
            citizen.CurrentLocation = Citizen.Location.Moving;
            if (goingToWork) member.CommutesStarted++;
            else member.ReturnsStarted++;
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
            CitizenManager manager = CitizenManager.instance;
            foreach (uint id in residents)
            {
                object visual;
                if (!visuals.TryGetValue(id, out visual))
                {
                    visual = city.CreateResidentAndroid((int)id);
                    visuals.Add(id, visual);
                }
                Citizen data = manager.m_citizens.m_buffer[id];
                ushort iid = data.m_instance;
                CitizenInstance instance = iid == 0 ? new CitizenInstance() : manager.m_instances.m_buffer[iid];
                bool visible = iid != 0 && instance.m_citizen == id && (instance.m_flags & CitizenInstance.Flags.Character) != 0;
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
            Rect panel = new Rect(Screen.width - 420, Screen.height - 202, 400, 92);
            GUI.Box(panel, "");
            GUI.Label(new Rect(panel.x + 10, panel.y + 6, panel.width - 20, panel.height - 8), hud);
        }

        public void OnDestroy()
        {
            if (renderer == this) renderer = null;
            if (renderPatch != null) renderPatch.UnpatchAll(RenderPatchId);
        }
    }
}
