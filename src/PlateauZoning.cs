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
    public sealed class PlateauZoning : MonoBehaviour
    {
        private const string PatchId = "sunwood.nagoya.plateau-building-render";
        private static PlateauZoning controller;
        private Harmony patch;
        private string root, runId, report;
        private bool attempted;
        private string setupError;
        private int queued;
        private float nextSample;
        private readonly List<Binding> bindings = new List<Binding>();
        private volatile Binding[] drawable = new Binding[0];

        private sealed class Binding
        {
            public string Source, Prefab, Error;
            public ItemClass.Zone Zone;
            public ushort Road, NativeId, ZoneId;
            public uint BuildIndex;
            public Vector3 Requested, Position;
            public float Angle;
            public Bounds Model;
            public Renderer Mesh;
            public int Row, Width, Length;
        }

        internal void Initialize(string data, string run)
        {
            root = data;
            runId = run;
            MethodInfo target = typeof(NativeBuilding).GetMethod("RenderInstance", new Type[] {
                typeof(RenderManager.CameraInfo), typeof(ushort), typeof(int)
            });
            MethodInfo prefix = typeof(PlateauZoning).GetMethod("ShowNativeMesh", BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                if (target == null) throw new MissingMethodException("Building.RenderInstance");
                patch = new Harmony(PatchId);
                patch.Patch(target, new HarmonyMethod(prefix), null, null, null);
                controller = this;
            }
            catch (Exception error)
            {
                setupError = error.GetBaseException().Message;
                Debug.LogException(error);
            }
        }

        private static bool ShowNativeMesh(ref NativeBuilding __instance, ushort __1)
        {
            PlateauZoning owner = controller;
            if (owner == null) return true;
            foreach (Binding binding in owner.drawable)
                if (__1 == binding.NativeId && __instance.m_buildIndex == binding.BuildIndex)
                    return false;
            return true;
        }

        public void Update()
        {
            if (root == null) return;
            string ready = Interlocked.Exchange<string>(ref report, null);
            if (ready != null)
            {
                string file = Path.Combine(root, "plateau-zoned-state.json");
                string temp = file + ".tmp";
                File.WriteAllText(temp, ready, new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
                drawable = bindings.FindAll(delegate(Binding site) {
                    return site.NativeId != 0 && site.Mesh != null && site.Mesh.enabled && site.Mesh.gameObject.activeInHierarchy;
                }).ToArray();
            }
            if (Time.realtimeSinceStartup < nextSample || Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            nextSample = Time.realtimeSinceStartup + 4;
            Binding[] requested = null;
            if (!attempted)
            {
                string path = Path.Combine(root, "plateau-zoning-plan.json");
                if (File.Exists(path))
                {
                    attempted = true;
                    try
                    {
                        if (setupError == null) requested = ReadPlan(path);
                    }
                    catch (Exception error) { setupError = error.GetBaseException().Message; }
                }
            }
            if (requested == null && drawable.Length == 0 && !attempted)
            {
                Interlocked.Exchange(ref queued, 0);
                return;
            }
            SimulationManager.instance.AddAction(delegate {
                try
                {
                    if (requested != null)
                    {
                        foreach (Binding candidate in requested)
                        {
                            try { Build(candidate); }
                            catch (Exception error) { candidate.Error = error.GetBaseException().Message; }
                            bindings.Add(candidate);
                        }
                    }
                    Interlocked.Exchange<string>(ref report, State());
                }
                catch (Exception error) { Debug.LogException(error); }
                finally { Interlocked.Exchange(ref queued, 0); }
            });
        }

        private Binding[] ReadPlan(string path)
        {
            if (new FileInfo(path).Length > 16000) throw new InvalidDataException("Zoning plan file limit");
            Dictionary<string, object> plan = (Dictionary<string, object>)JsonData.Parse(File.ReadAllText(path));
            if ((string)plan["runId"] != runId) throw new InvalidDataException("Zoning plan refers to a different game run");
            List<object> rows = (List<object>)plan["bindings"];
            if (rows.Count < 1 || rows.Count > 12) throw new InvalidDataException("Zoning plan count");
            List<Binding> result = new List<Binding>();
            HashSet<string> ids = new HashSet<string>();
            foreach (object value in rows)
            {
                Dictionary<string, object> row = (Dictionary<string, object>)value;
                string source = (string)row["source"];
                if (!System.Text.RegularExpressions.Regex.IsMatch(source, @"\Abldg_[0-9a-f-]{36}\z") || !ids.Add(source))
                    throw new InvalidDataException("Invalid individual PLATEAU model ID");
                GameObject original = GameObject.Find("PLATEAU_Station_" + source);
                Renderer mesh = original == null ? null : original.GetComponent<MeshRenderer>();
                if (mesh == null || !mesh.enabled || !original.activeInHierarchy)
                    throw new InvalidDataException("PLATEAU mesh unavailable: " + source);
                ItemClass.Zone zone = (ItemClass.Zone)Enum.Parse(typeof(ItemClass.Zone), (string)row["zone"]);
                if (zone != ItemClass.Zone.ResidentialLow && zone != ItemClass.Zone.ResidentialHigh &&
                    zone != ItemClass.Zone.CommercialLow && zone != ItemClass.Zone.CommercialHigh)
                    throw new InvalidDataException("Expected a residential or commercial pilot zone");
                result.Add(new Binding {
                    Source = source, Prefab = (string)row["prefab"], Road = checked((ushort)(double)row["road"]),
                    Zone = zone, Requested = new Vector3((float)(double)row["x"],140,(float)(double)row["z"]),
                    Angle = (float)(double)row["angle"] * Mathf.Deg2Rad, Model = mesh.bounds, Mesh = mesh
                });
            }
            return result.ToArray();
        }

        private static void Build(Binding site)
        {
            BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(site.Prefab);
            ItemClass.SubService expected = (ItemClass.SubService)Enum.Parse(typeof(ItemClass.SubService), site.Zone.ToString());
            if (info == null || info.m_class.m_subService != expected)
                throw new InvalidOperationException("Native building prefab and zone differ");
            site.Width = info.GetWidth();
            site.Length = info.GetLength();
            NetSegment road = NetManager.instance.m_segments.m_buffer[site.Road];
            if ((road.m_flags & NetSegment.Flags.Created) == 0 || road.Info == null || road.Info.name != "Basic Road")
                throw new InvalidOperationException("Native frontage road identity differs");
            ushort[] options = {road.m_blockStartLeft,road.m_blockStartRight,road.m_blockEndLeft,road.m_blockEndRight};
            ZoneManager zoning = ZoneManager.instance;
            float best = 36f;
            foreach (ushort id in options)
            {
                if (id == 0) continue;
                ZoneBlock block = zoning.m_blocks.m_buffer[id];
                for (int row = 0; row + site.Width <= block.RowCount; row++)
                {
                    Vector3 center;
                    if (!Available(block, row, site, out center)) continue;
                    float distance = (center - site.Requested).sqrMagnitude;
                    int offset;
                    if (distance >= best || !zoning.CheckSpace(center, site.Angle, site.Width, site.Length, out offset)) continue;
                    best = distance;
                    site.ZoneId = id;
                    site.Row = row;
                    site.Position = center;
                }
            }
            if (site.ZoneId == 0) throw new InvalidOperationException("No available native frontage cells fit the PLATEAU footprint");

            BuildingManager buildings = BuildingManager.instance;
            SimulationManager sim = SimulationManager.instance;
            Randomizer random = sim.m_randomizer;
            ushort nativeId;
            if (!buildings.CreateBuilding(out nativeId, ref random, info, site.Position, site.Angle, site.Length, sim.m_currentBuildIndex))
                throw new InvalidOperationException("CS1 refused to create the zoned building");
            sim.m_randomizer = random;
            sim.m_currentBuildIndex++;
            site.NativeId = nativeId;
            site.BuildIndex = buildings.m_buildings.m_buffer[nativeId].m_buildIndex;
            for (int y = site.Row; y < site.Row + site.Width; y++)
                for (int x = 0; x < site.Length; x++)
                    zoning.m_blocks.m_buffer[site.ZoneId].SetZone(x, y, site.Zone);
            zoning.m_blocks.m_buffer[site.ZoneId].RefreshZoning(site.ZoneId);
            zoning.UpdateBlock(site.ZoneId);
        }

        private static bool Available(ZoneBlock block, int row, Binding site, out Vector3 center)
        {
            Vector3 across = new Vector3(Mathf.Cos(block.m_angle),0,Mathf.Sin(block.m_angle))*8;
            Vector3 along = new Vector3(across.z,0,-across.x);
            center = block.m_position + across * ((site.Length-1)*.5f-3.5f) + along * (row+(site.Width-1)*.5f-3.5f);
            center.y = site.Requested.y;
            for (int y = row; y < row + site.Width; y++)
                for (int x = 0; x < site.Length; x++)
                {
                    ulong flag = 1UL << (y * 8 + x);
                    if ((block.m_valid & flag) == 0 || ((block.m_shared | block.m_occupied1 | block.m_occupied2) & flag) != 0)
                        return false;
                    ItemClass.Zone old = block.GetZone(x, y);
                    if (old != ItemClass.Zone.Unzoned && old != site.Zone) return false;
                    Vector3 spot = block.m_position + across * (x-3.5f) + along * (y-3.5f);
                    if (spot.x < site.Model.min.x || spot.x > site.Model.max.x || spot.z < site.Model.min.z || spot.z > site.Model.max.z)
                        return false;
                }
            return true;
        }

        private string State()
        {
            List<object> results = new List<object>();
            foreach (Binding site in bindings)
            {
                Dictionary<string, object> row = new Dictionary<string, object> {
                    {"source",site.Source},{"zone",site.Zone.ToString()},{"prefab",site.Prefab},{"buildingId",site.NativeId},
                    {"buildIndex",site.BuildIndex},{"road",site.Road},{"zoneBlock",site.ZoneId},{"x",site.Position.x},{"z",site.Position.z},{"error",site.Error}
                };
                if (site.NativeId != 0)
                {
                    NativeBuilding building = BuildingManager.instance.m_buildings.m_buffer[site.NativeId];
                    row["nativeIdentity"] = (building.m_flags & NativeBuilding.Flags.Created) != 0 && building.m_buildIndex == site.BuildIndex;
                    row["flags"] = building.m_flags.ToString();
                    int cells = 0, homes = 0, workers = 0, residents = 0;
                    ZoneBlock block = ZoneManager.instance.m_blocks.m_buffer[site.ZoneId];
                    for (int y = site.Row; y < site.Row+site.Width; y++)
                        for (int x = 0; x < site.Length; x++)
                            if (block.GetZone(x,y) == site.Zone) cells++;
                    CitizenUnit[] units = CitizenManager.instance.m_units.m_buffer;
                    uint id = building.m_citizenUnits;
                    for (int step=0; id!=0 && id<units.Length && step<256; step++)
                    {
                        CitizenUnit unit = units[id];
                        if ((unit.m_flags & CitizenUnit.Flags.Home) != 0) homes++;
                        for (int slot=0;slot<5;slot++)
                            if (unit.GetCitizen(slot)!=0)
                            {
                                if ((unit.m_flags & CitizenUnit.Flags.Home)!=0) residents++;
                                if ((unit.m_flags & CitizenUnit.Flags.Work)!=0) workers++;
                            }
                        id = unit.m_nextUnit;
                    }
                    row["nativeZoneCells"] = cells;
                    row["nativeHomeUnits"] = homes;
                    row["nativeResidents"] = residents;
                    row["nativeWorkers"] = workers;
                }
                results.Add(row);
            }
            return JsonData.Stringify(new Dictionary<string,object> {
                {"utc",DateTime.UtcNow.ToString("o")},{"runId",runId},{"error",setupError},{"bindings",results}
            });
        }

        public void OnDestroy()
        {
            if (controller == this) controller = null;
            if (patch != null) patch.UnpatchAll(PatchId);
        }
    }
}
