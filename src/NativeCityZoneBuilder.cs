using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ColossalFramework.Math;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    public sealed class NativeCityZoneBuilder : MonoBehaviour
    {
        private const string Root = NativeCityRoadBuilder.Root;
        private const int MaxSites = 3000;
        private const int MaxFrontagesPerSite = 8;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private sealed class Site
        {
            public string Group, Ward, Error;
            public Frontage[] Frontages;
            public int Members, Row, Width, Length;
            public ItemClass.Zone Zone;
            public Vector3 Position;
            public float Angle;
            public ushort Road, Block, Building;
            public uint Generation;
        }

        private struct Frontage
        {
            public string RoadSource;
            public Vector3 Position;
        }

        private struct RoadReceipt
        {
            public ushort Id;
            public uint Generation;
        }

        private readonly List<Site> sites = new List<Site>();
        private readonly Dictionary<string, List<RoadReceipt>> roads = new Dictionary<string, List<RoadReceipt>>();
        private readonly Dictionary<ItemClass.Zone, List<BuildingInfo>> prefabs = new Dictionary<ItemClass.Zone, List<BuildingInfo>>();
        private readonly HashSet<string> existingGroups = new HashSet<string>(StringComparer.Ordinal);
        private string runId, commandId, city, phase, failure;
        private NativeCityFrontages frontageRepair;
        private bool attempted, requireGoodsShops;
        private volatile bool stopped;
        private int busy, cursor, placed;
        private float nextUpdate, finishBy;
        private string readyStatus;

        public void Start()
        {
            if (!File.Exists(Path.Combine(Root, "enabled.txt")))
            {
                enabled = false;
                return;
            }
            runId = Guid.NewGuid().ToString("N");
            city = SimulationManager.instance.m_metaData.m_CityName;
            phase = "ready";
            Publish("zone-runtime.json", Status());
        }

        public void Update()
        {
            if (Time.realtimeSinceStartup < nextUpdate ||
                Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                return;
            nextUpdate = Time.realtimeSinceStartup + .15f;
            string ready = Interlocked.Exchange<string>(ref readyStatus, null);
            if (ready != null)
                Publish("zone-runtime.json", ready);
            if (!attempted)
            {
                try
                {
                    string path = Path.Combine(Root, "zone-command.json");
                    if (File.Exists(path))
                    {
                        Dictionary<string, object> command = (Dictionary<string, object>)JsonData.Parse(ReadFile(path, 4096));
                        if ((string)command["runId"] == runId)
                        {
                            attempted = true;
                            LoadCommand(command);
                            finishBy = Time.realtimeSinceStartup + 240f;
                            phase = "placing";
                        }
                    }
                }
                catch (Exception error)
                {
                    attempted = true;
                    phase = "rejected";
                    failure = error.GetBaseException().Message;
                    Publish("zone-runtime.json", Status());
                }
            }

            if (phase != "placing")
            {
                Interlocked.Exchange(ref busy, 0);
                return;
            }
            bool timeout = Time.realtimeSinceStartup > finishBy;
            SimulationManager.instance.AddAction(delegate {
                if (frontageRepair == null) PlaceBatch(timeout);
                else RepairFrontages();
            });
        }

        private void LoadCommand(Dictionary<string, object> command)
        {
            commandId = FileToken((string)command["commandId"]);
            if (city != (string)command["expectedCityName"] || !city.StartsWith("NagoyaCitywide-", StringComparison.Ordinal) ||
                GameAreaManager.instance.m_areaCount != 25 ||
                File.Exists(Path.Combine(Root, commandId + "-zones.tsv")) ||
                File.Exists(Path.Combine(Root, commandId + "-frontages.json")))
                throw new InvalidDataException("The city or zoning command differs from the isolated construction run");

            object repair;
            if (command.TryGetValue("repairFrontages", out repair))
            {
                var data = (Dictionary<string, object>)repair;
                var wards = ((List<object>)data["wards"]).ConvertAll(delegate(object ward) { return (string)ward; });
                frontageRepair = new NativeCityFrontages(wards.ToArray(), Convert.ToInt32(data["limitPerWard"]),
                    (bool)data["apply"], Convert.ToUInt32(data["expectedBuildIndex"]), Convert.ToInt32(data["expectedSegments"]));
                return;
            }

            string roadCommand = FileToken((string)command["roadCommandId"]);
            string roadFile = Path.Combine(Root, roadCommand + "-roads.tsv");
            string planFile = Path.Combine(Root, FileToken((string)command["plan"]));
            object goodsShops;
            requireGoodsShops = command.TryGetValue("requireGoodsShops", out goodsShops) && (bool)goodsShops;
            CheckDigest(roadFile, (string)command["roadSha256"]);
            CheckDigest(planFile, (string)command["planSha256"]);
            using (StringReader lines = new StringReader(ReadFile(roadFile, 8 * 1024 * 1024)))
            {
                if (lines.ReadLine() != "source\tordinal\tnativeId\tbuildIndex\tstartNode\tendNode\tprefabTag\terror")
                    throw new InvalidDataException("Unexpected native road receipt format");
                string line;
                while ((line = lines.ReadLine()) != null)
                {
                    string[] row = line.Split('\t');
                    if (row.Length != 8)
                        throw new InvalidDataException("Invalid native road receipt row");
                    ushort id = UInt16.Parse(row[2], Invariant);
                    if (id == 0 || (row[6] != "basic" && row[6] != "arterial"))
                        continue;
                    List<RoadReceipt> group;
                    if (!roads.TryGetValue(row[0], out group))
                        roads.Add(row[0], group = new List<RoadReceipt>());
                    group.Add(new RoadReceipt {Id=id, Generation=UInt32.Parse(row[3], Invariant)});
                }
            }
            if (roads.Count == 0)
                throw new InvalidDataException("Receipt contains no zoning-enabled road segments");

            HashSet<string> siteKeys = new HashSet<string>();
            using (StringReader lines = new StringReader(ReadFile(planFile, 4 * 1024 * 1024)))
            {
                string line;
                while ((line = lines.ReadLine()) != null)
                {
                    string[] row = line.Split('\t');
                    if (row.Length < 8 || (row.Length - 8) % 3 != 0 || (row.Length - 8) / 3 >= MaxFrontagesPerSite ||
                        row[0] != "S" || row[1].Length > 140 || !siteKeys.Add(row[1]) ||
                        sites.Count >= MaxSites || row[7].Length > 40)
                        throw new InvalidDataException("Invalid aggregate zoning site row");
                    ItemClass.Zone zone = (ItemClass.Zone)Enum.Parse(typeof(ItemClass.Zone), row[5]);
                    ItemClass.SubService service = ZoneService(zone);
                    int members = Int32.Parse(row[6], Invariant);
                    if (members <= 0 || members > 738178)
                        throw new InvalidDataException("Invalid building count for aggregate zoning site");
                    if (!prefabs.ContainsKey(zone))
                        prefabs.Add(zone, SelectPrefabs(service, requireGoodsShops));
                    Frontage[] frontages = new Frontage[1 + (row.Length - 8) / 3];
                    for (int i = 0; i < frontages.Length; i++)
                    {
                        int start = i == 0 ? 2 : 8 + (i - 1) * 3;
                        if (row[start].Length == 0) throw new InvalidDataException("Empty zoning frontage road source");
                        frontages[i] = new Frontage {RoadSource=row[start],
                            Position=new Vector3(Coordinate(row[start + 1]), 140, Coordinate(row[start + 2]))};
                    }
                    sites.Add(new Site {Group=row[1], Frontages=frontages, Ward=row[7], Zone=zone, Members=members});
                }
            }
            if (sites.Count == 0)
                throw new InvalidDataException("Empty aggregate zoning site plan");
        }

        private void RepairFrontages()
        {
            try
            {
                if (stopped) return;
                Dictionary<string, object> report = frontageRepair.Run();
                Publish(commandId + "-frontages.json", JsonData.Stringify(report));
                phase = (bool)report["complete"] ? "frontages_checked" : "frontages_interrupted";
            }
            catch (Exception error)
            {
                failure = error.GetBaseException().Message;
                phase = "frontages_interrupted";
            }
            finally
            {
                Interlocked.Exchange<string>(ref readyStatus, Status());
                Interlocked.Exchange(ref busy, 0);
            }
        }

        private static ItemClass.SubService ZoneService(ItemClass.Zone zone)
        {
            switch (zone)
            {
                case ItemClass.Zone.ResidentialLow: return ItemClass.SubService.ResidentialLow;
                case ItemClass.Zone.ResidentialHigh: return ItemClass.SubService.ResidentialHigh;
                case ItemClass.Zone.CommercialLow: return ItemClass.SubService.CommercialLow;
                case ItemClass.Zone.CommercialHigh: return ItemClass.SubService.CommercialHigh;
                case ItemClass.Zone.Office: return ItemClass.SubService.OfficeGeneric;
                case ItemClass.Zone.Industrial: return ItemClass.SubService.IndustrialGeneric;
                default: throw new InvalidDataException("Unsupported native zoning type");
            }
        }

        private static List<BuildingInfo> SelectPrefabs(ItemClass.SubService service, bool requireGoodsShops)
        {
            Dictionary<int, BuildingInfo> sizes = new Dictionary<int, BuildingInfo>();
            bool commercial = service == ItemClass.SubService.CommercialLow || service == ItemClass.SubService.CommercialHigh;
            for (uint i = 0; i < PrefabCollection<BuildingInfo>.LoadedCount(); i++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(i);
                if (info == null || info.m_class.m_subService != service ||
                    info.m_class.m_level != ItemClass.Level.Level1 || info.m_zoningMode != BuildingInfo.ZoningMode.Straight ||
                    info.name.StartsWith("Custom", StringComparison.Ordinal))
                    continue;
                int width = info.GetWidth(), length = info.GetLength();
                if (requireGoodsShops && commercial)
                {
                    CommercialBuildingAI ai = info.m_buildingAI as CommercialBuildingAI;
                    if (ai == null || ai.m_incomingResource != TransferManager.TransferReason.Goods || width * length < 2) continue;
                }
                if (width > 0 && width <= 4 && length > 0 && length <= 4 && !sizes.ContainsKey(width * 5 + length))
                    sizes.Add(width * 5 + length, info);
            }
            if (sizes.Count == 0)
                throw new InvalidDataException("No loaded level-one growables for " + service);
            return new List<BuildingInfo>(sizes.Values);
        }

        private void PlaceBatch(bool timeout)
        {
            try
            {
                if (stopped) return;
                if (timeout) throw new TimeoutException("Timed out while assigning native zone parcels");
                if (cursor == 0)
                {
                    NativeCityLedger.Road[] roads;
                    NativeCityLedger.Lot[] lots;
                    NativeCityLedger.Snapshot(out roads, out lots);
                    foreach (NativeCityLedger.Lot lot in lots)
                        if (NativeCityLedger.HasActiveZone(lot)) existingGroups.Add(lot.Group);
                }
                int end = Math.Min(cursor + 24, sites.Count);
                for (; cursor < end; cursor++)
                {
                    Site site = sites[cursor];
                    site.Error = Place(site);
                    if (site.Building != 0) placed++;
                }
                if (cursor == sites.Count)
                {
                    SaveResults();
                    phase = placed == cursor ? "built" : "built_partial";
                }
            }
            catch (Exception error)
            {
                failure = error.GetBaseException().Message;
                phase = placed > 0 ? "stopped_partial" : "rejected";
                if (placed > 0)
                {
                    try { SaveResults(); }
                    catch (Exception receipt) { failure += " / receipt: " + receipt.GetBaseException().Message; }
                }
            }
            finally
            {
                Interlocked.Exchange<string>(ref readyStatus, Status());
                Interlocked.Exchange(ref busy, 0);
            }
        }

        private string Place(Site site)
        {
            if (existingGroups.Contains(site.Group))
                return "The source group already has a native building and zone";
            ZoneManager zoning = ZoneManager.instance;
            BuildingInfo choice = null;
            bool hasRoadReceipt = false;
            foreach (Frontage frontage in site.Frontages)
            {
                List<RoadReceipt> segments;
                if (!roads.TryGetValue(frontage.RoadSource, out segments)) continue;
                hasRoadReceipt = true;
                choice = FindPlacement(site, zoning, segments, frontage.Position);
                if (choice != null) break;
            }
            if (choice == null)
                return hasRoadReceipt ? "No free native zone cells on the source frontage" : "No active compiled road for this source";
            BuildingManager buildings = BuildingManager.instance;
            SimulationManager sim = SimulationManager.instance;
            Randomizer random = sim.m_randomizer;
            ushort nativeId;
            bool created = buildings.CreateBuilding(out nativeId, ref random, choice, site.Position,
                site.Angle, site.Length, sim.m_currentBuildIndex);
            sim.m_randomizer = random;
            if (!created)
                return "Native building allocation failed";
            sim.m_currentBuildIndex++;
            site.Building = nativeId;
            site.Generation = buildings.m_buildings.m_buffer[nativeId].m_buildIndex;
            for (int row = site.Row; row < site.Row + site.Width; row++)
                for (int column = 0; column < site.Length; column++)
                    zoning.m_blocks.m_buffer[site.Block].SetZone(column, row, site.Zone);
            zoning.m_blocks.m_buffer[site.Block].RefreshZoning(site.Block);
            zoning.UpdateBlock(site.Block);
            NativeCityLedger.Record(new NativeCityLedger.Lot {
                Group=site.Group, Ward=site.Ward, Id=site.Building, Road=site.Road, ZoneBlock=site.Block,
                BuildIndex=site.Generation, Zone=site.Zone, Members=site.Members,
                Row=site.Row, Width=site.Width, Length=site.Length
            });
            return null;
        }

        private BuildingInfo FindPlacement(Site site, ZoneManager zoning, List<RoadReceipt> segments, Vector3 requested)
        {
            NetManager net = NetManager.instance;
            BuildingInfo choice = null;
            float best = 48f * 48f;
            foreach (RoadReceipt receipt in segments)
            {
                NetSegment road = net.m_segments.m_buffer[receipt.Id];
                if ((road.m_flags & NetSegment.Flags.Created) == 0 || road.m_buildIndex != receipt.Generation ||
                    road.Info == null || (road.Info.name != "Basic Road" && road.Info.name != "Medium Road"))
                    continue;
                ushort[] blocks = {road.m_blockStartLeft, road.m_blockStartRight, road.m_blockEndLeft, road.m_blockEndRight};
                foreach (ushort id in blocks)
                {
                    if (id == 0) continue;
                    ZoneBlock block = zoning.m_blocks.m_buffer[id];
                    foreach (BuildingInfo prefab in prefabs[site.Zone])
                    {
                        int width = prefab.GetWidth(), length = prefab.GetLength();
                        for (int row = 0; row + width <= block.RowCount; row++)
                        {
                            Vector3 center;
                            float angle;
                            if (!Available(zoning, block, row, width, length, site.Zone, out center, out angle))
                                continue;
                            float distance = (center - requested).sqrMagnitude;
                            if (distance >= best) continue;
                            best = distance;
                            choice = prefab;
                            site.Block = id;
                            site.Road = receipt.Id;
                            site.Row = row;
                            site.Width = width;
                            site.Length = length;
                            site.Position = center;
                            site.Angle = angle;
                        }
                    }
                }
            }
            return choice;
        }

        private static bool Available(ZoneManager manager, ZoneBlock block, int row, int width, int length,
            ItemClass.Zone use, out Vector3 center, out float angle)
        {
            center = NativeCityFrontages.Center(block, row, width, length);
            angle = NativeCityFrontages.StraightAngle(block);
            for (int y = row; y < row + width; y++)
                for (int x = 0; x < length; x++)
                {
                    ulong flag = 1UL << (y * 8 + x);
                    ItemClass.Zone old = block.GetZone(x, y);
                    if ((block.m_valid & flag) == 0 || ((block.m_shared | block.m_occupied1 | block.m_occupied2) & flag) != 0 ||
                        (old != ItemClass.Zone.Unzoned && old != use))
                        return false;
                }
            for (int direction = 0; direction < 4; direction++)
            {
                int offset;
                if (manager.CheckSpace(center, angle, width, length, out offset))
                    return true;
                angle += Mathf.PI / 2;
            }
            return false;
        }

        private void SaveResults()
        {
            StringBuilder report = new StringBuilder("group\tward\tzone\tmodelMembers\tbuildingId\tbuildIndex\troadId\tzoneBlock\tzoneCells\terror\n");
            ZoneManager manager = ZoneManager.instance;
            for (int i = 0; i < cursor; i++)
            {
                Site site = sites[i];
                int count = 0;
                if (site.Building != 0)
                    for (int row = site.Row; row < site.Row + site.Width; row++)
                        for (int x = 0; x < site.Length; x++)
                            if (manager.m_blocks.m_buffer[site.Block].GetZone(x, row) == site.Zone) count++;
                report.Append(site.Group).Append('\t').Append(site.Ward).Append('\t').Append(site.Zone).Append('\t')
                    .Append(site.Members).Append('\t').Append(site.Building).Append('\t').Append(site.Generation).Append('\t')
                    .Append(site.Road).Append('\t').Append(site.Block).Append('\t').Append(count).Append('\t').Append(site.Error).Append('\n');
            }
            Publish(commandId + "-zones.tsv", report.ToString());
        }

        private string Status()
        {
            return JsonData.Stringify(new Dictionary<string, object> {
                {"runId",runId}, {"commandId",commandId}, {"loadedCity",city}, {"phase",phase},
                {"sites",sites.Count}, {"processed",cursor}, {"buildingsPlaced",placed}, {"error",failure},
                {"utc",DateTime.UtcNow.ToString("o",Invariant)}
            });
        }

        private static string FileToken(string value)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[a-zA-Z0-9_.-]{1,100}\z") ||
                value == "." || value == "..")
                throw new InvalidDataException("Invalid site or receipt filename");
            return value;
        }

        private static float Coordinate(string raw)
        {
            float number = Single.Parse(raw, NumberStyles.Float, Invariant);
            if (Single.IsNaN(number) || Single.IsInfinity(number) || Mathf.Abs(number) > 4790f)
                throw new InvalidDataException("Zoning site outside stock buildable tiles");
            return number;
        }

        private static string ReadFile(string path, int limit)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > limit)
                throw new InvalidDataException("File outside zoning import bounds: " + file.Name);
            return File.ReadAllText(path, new UTF8Encoding(false, true));
        }

        private static void CheckDigest(string path, string expected)
        {
            using (FileStream file = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                if (!String.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""), expected,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Zoning dependency SHA-256 differs: " + Path.GetFileName(path));
            }
        }

        private static void Publish(string name, string content)
        {
            string path = Path.Combine(Root, name), temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public void OnDestroy() { stopped = true; }
    }
}
