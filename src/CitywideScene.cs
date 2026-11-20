using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using ColossalFramework;
using UnityEngine;
using CitywideRouting;

namespace NagoyaRoadDelivery
{
    // Real source-network scenery and MOD-owned actor rules. This does not create
    // native NetSegments, native citizens or economic BuildingInfo objects.
    public sealed partial class CitywideScene : MonoBehaviour, ICitywideVisuals, ICitywideSurfaceVisuals, ICitywideFleetVisuals
    {
        private static readonly string Root =RuntimePaths.Data(@"citywide-live-v1");
        internal bool NativeConstruction;
        internal NativeEmploymentTrial EmploymentTrial;
        private RoutingGraph graph;
        private SurfaceMetadata surfaceMetadata;
        private GradeProfile gradeProfile;
        private string gradeProfileFile="",gradeProfileHash="";
        private string surfaceMetadataFile="",surfaceMetadataHash="";
        private int widthOverrides,markedCrossings;
        private CitywideSimulation simulation;
        private readonly List<Mesh> ownedMeshes=new List<Mesh>();
        private readonly List<Material> ownedMaterials=new List<Material>();
        private readonly List<Texture2D> ownedTextures=new List<Texture2D>();
        private readonly List<Actor> actors=new List<Actor>();
        private readonly Dictionary<int,GameObject> chargers=new Dictionary<int,GameObject>();
        private readonly Dictionary<int,int> chargingActors=new Dictionary<int,int>();
        private int androidsPerRegion=1;
        private readonly Dictionary<long,List<Span>> spatial=new Dictionary<long,List<Span>>();
        private readonly Dictionary<string,HumanModel> humans=new Dictionary<string,HumanModel>();
        private readonly Dictionary<string,Mesh> androidMeshes=new Dictionary<string,Mesh>();
        private AndroidAssetData android;
        private Material roadMaterial,androidMaterial,chargerMaterial;
        private Mesh chargerMesh;
        private string error="",phase="disabled",runId="",humanSource="",writeError="";
        private float nextState;
        private float nextAppearance;private string appearanceSignature="",lastAppearanceAttempt="",appearanceError="";
        private string cityHud="NAGOYA CITYWIDE / loading";
        private string networkName="original source network",activeRoadIndex="roads-index.json",activeRegionFile="regions.json",activeGraph=RuntimePaths.Data(@"citywide-roads-v1\city-routing.ncg"),activeGraphHash="";
        private readonly Dictionary<int,HubVisual> entranceHubs=new Dictionary<int,HubVisual>();
        private List<object> buildingTargets=new List<object>();
        private sealed class HubVisual {public string Source;public Vector3 Terminal,Dock;public float Yaw,RobotYaw;}

        private int removedHumanAccessoryTriangles;
        private int loadedChunks,expectedChunks,roadTriangles;
        private GameObject scene;
        private readonly Dictionary<int,List<Span>> edgeSurfaces=new Dictionary<int,List<Span>>();
        private struct Span { public Point2 A,B,NormalA,NormalB; public float Width,Y; public bool Foot; public int Edge;public double StartDistance,Length; }
        private sealed class Actor { public GameObject Root,Left,Right,Parcel; public int Id;public string Kind;public int SurfaceEdge=-1;public double LastMeters;public HumanModel Human;public Mesh HumanMesh; }
        private sealed class HumanModel { public Mesh Body;public Material Material;public Vector3[] Vertices,Normals; }
        private static Dictionary<string,object> Map(object x){return (Dictionary<string,object>)x;}
        private static List<object> List(Dictionary<string,object> m,string k){return (List<object>)m[k];}
        private static string Text(Dictionary<string,object> m,string k){return (string)m[k];}
        private static int Int(Dictionary<string,object> m,string k){return checked((int)(double)m[k]);}
        private static string Hash(byte[] data){using(SHA256 h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
        private static long Key(int x,int z){return ((long)x<<32)^(uint)z;}
        private static float Width(byte type){float[] w={2,2,4,8,5,1.5f,3,10,5,5.5f,8,5,4,1.7f,7,4,10,5,6};if(type>=w.Length)throw new FormatException("Unknown road class");return w[type];}
        private static bool Foot(byte type){return type==0||type==1||type==5||type==6||type==13;}
        private static string SafeFile(string relative){string full=Path.GetFullPath(Path.Combine(Root,relative));if(!full.StartsWith(Path.GetFullPath(Root)+"\\",StringComparison.OrdinalIgnoreCase)||!File.Exists(full))throw new FormatException("City asset outside root or missing");return full;}
        private Dictionary<string,object> ReadMap(string relative){return Map(JsonData.Parse(File.ReadAllText(SafeFile(relative))));}
        private static Vector3 WorldPoint(List<object> values)
        {
            if(values.Count!=3)throw new FormatException("Entrance pose dimension");
            float x=(float)(double)values[0],y=(float)(double)values[1],z=(float)(double)values[2];
            if(float.IsNaN(x)||float.IsInfinity(x)||float.IsNaN(y)||float.IsInfinity(y)||float.IsNaN(z)||float.IsInfinity(z)||Mathf.Abs(x)>40000||Mathf.Abs(z)>40000||y<139||y>145)throw new FormatException("Entrance pose bounds");
            return new Vector3(x,y,z);
        }
        private void SelectNetwork()
        {
            if(!File.Exists(Path.Combine(Root,"network.json")))return;
            if(new FileInfo(Path.Combine(Root,"network.json")).Length>65536)throw new FormatException("Network selection size");
            Dictionary<string,object> cfg=ReadMap("network.json");networkName=Text(cfg,"name");
            if(networkName.Length<1||networkName.Length>80)throw new FormatException("Network selection name");
            activeGraph=SafeFile(Text(cfg,"graph"));activeGraphHash=Text(cfg,"graphSHA256");
            object fleetSize;
            if(cfg.TryGetValue("androidsPerRegion",out fleetSize))
            {
                if(!(fleetSize is double) || (double)fleetSize<1 || (double)fleetSize>CitywideSimulation.MaxAndroidsPerRegion || (double)fleetSize!=Math.Floor((double)fleetSize))
                    throw new FormatException("androidsPerRegion must be a whole number from 1 to "+CitywideSimulation.MaxAndroidsPerRegion);
                androidsPerRegion=(int)(double)fleetSize;
            }
            activeRoadIndex=Text(cfg,"roadsIndex");SafeFile(activeRoadIndex);activeRegionFile=Text(cfg,"regions");SafeFile(activeRegionFile);
            object gradeFile;if(cfg.TryGetValue("gradeProfile",out gradeFile)){gradeProfileFile=(string)gradeFile;SafeFile(gradeProfileFile);gradeProfileHash=Text(cfg,"gradeProfileSHA256");}
            object surfaceFile;if(cfg.TryGetValue("surfaceMetadata",out surfaceFile)){surfaceMetadataFile=(string)surfaceFile;SafeFile(surfaceMetadataFile);surfaceMetadataHash=Text(cfg,"surfaceMetadataSHA256");}
            buildingTargets=List(cfg,"buildingTargets");
            if(buildingTargets.Count<10||buildingTargets.Count>168)throw new FormatException("Building target count");
            HashSet<int> nodes=new HashSet<int>();HashSet<string> sources=new HashSet<string>();
            foreach(object item in buildingTargets){Dictionary<string,object> target=Map(item);if(!nodes.Add(Int(target,"node"))||!sources.Add(Text(target,"sourceID")))throw new FormatException("Duplicate entrance target");}
            Dictionary<int,HubVisual> parsed=ParseHubPoses(ReadMap(Text(cfg,"hubVisuals")));
            foreach(KeyValuePair<int,HubVisual> item in parsed)entranceHubs.Add(item.Key,item.Value);
        }
        private Dictionary<int,HubVisual> ParseHubPoses(Dictionary<string,object> poses)
        {
            List<object> hubs=List(poses,"hubs");if(hubs.Count!=4)throw new FormatException("Expected four entrance hub visuals");
            HashSet<string> sources=new HashSet<string>();foreach(object item in buildingTargets)sources.Add(Text(Map(item),"sourceID"));
            Dictionary<int,HubVisual> result=new Dictionary<int,HubVisual>();
            foreach(object item in hubs){Dictionary<string,object> row=Map(item);int id=Int(row,"regionID");float yaw=(float)(double)row["yaw"];object v;float robotYaw=row.TryGetValue("robotYaw",out v)?(float)(double)v:yaw+180;
                if(id<0||id>=56||float.IsNaN(yaw)||float.IsInfinity(yaw)||Mathf.Abs(yaw)>360||float.IsNaN(robotYaw)||float.IsInfinity(robotYaw)||Mathf.Abs(robotYaw)>720||!sources.Contains(Text(row,"sourceID")))throw new FormatException("Entrance hub identity");
                result.Add(id,new HubVisual{Source=Text(row,"sourceID"),Terminal=WorldPoint(List(row,"terminalWorld")),Dock=WorldPoint(List(row,"dockWorld")),Yaw=yaw,RobotYaw=robotYaw});}
            return result;
        }
        private void ApplyHubPoses(Dictionary<int,HubVisual> poses)
        {
            if(entranceHubs.Count!=poses.Count)throw new FormatException("Hub override count changed");
            foreach(int id in poses.Keys)if(!entranceHubs.ContainsKey(id)||entranceHubs[id].Source!=poses[id].Source)throw new FormatException("Hub override source changed");
            foreach(KeyValuePair<int,HubVisual> item in poses){entranceHubs[item.Key]=item.Value;GameObject terminal;if(chargers.TryGetValue(item.Key,out terminal)){terminal.transform.position=item.Value.Terminal;terminal.transform.rotation=Quaternion.Euler(0,item.Value.Yaw,0);}}
            foreach(Actor actor in actors)
            {
                int region=CitywideSimulation.RegionForActor(actor.Id), chargingActor;
                HubVisual pose;
                if(actor.Kind=="android"&&chargingActors.TryGetValue(region,out chargingActor)&&chargingActor==actor.Id&&poses.TryGetValue(region,out pose))
                {
                    actor.Root.transform.position=pose.Dock;
                    actor.Root.transform.rotation=Quaternion.Euler(0,pose.RobotYaw,0);
                }
            }
        }
        private RoutingGraph LoadSelectedGraph()
        {
            byte[] bytes=File.ReadAllBytes(activeGraph);string actual=Hash(bytes);
            if(activeGraphHash.Length>0&&actual!=activeGraphHash.ToLowerInvariant())throw new FormatException("Selected road graph hash mismatch");
            activeGraphHash=actual;return RoutingGraph.Load(new MemoryStream(bytes));
        }
        public void Start()
        {
            if (NativeConstruction)
            {
                scene = new GameObject("Nagoya native resident androids");
                scene.transform.parent = transform;
                try { UrbanTerrainAppearance.Install(); }
                catch (Exception failure) { Debug.LogException(failure); }
                try
                {
                    LoadAndroid();
                    string nativeRun = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
                    scene.AddComponent<AndroidLifePilot>().Initialize(this, NativeCityRoadBuilder.Root, nativeRun, EmploymentTrial);
                }
                catch (Exception failure) { Debug.LogException(failure); }
                return;
            }
            if (!File.Exists(Path.Combine(Root, "enabled.txt"))) return;
            runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            phase = "loading";
            scene = new GameObject("Nagoya Citywide Source Network");
            scene.transform.parent = transform;
            scene.AddComponent<NativeGoodsMonitor>();
            StartCoroutine(Boot());
        }
        private IEnumerator Boot()
        {
            Dictionary<string,object> index=null;bool good=true;
            try{SelectNetwork();index=ReadMap(activeRoadIndex);roadMaterial=TextureMaterial(SafeFile(Text(index,"texture")));expectedChunks=List(index,"chunks").Count;if(expectedChunks<1||expectedChunks>4096)throw new FormatException("City chunk count");}
            catch(Exception e){Fail(e);good=false;}if(!good)yield break;
            float deadline=Time.realtimeSinceStartup+180;
            foreach(object value in List(index,"chunks"))
            {
                try{if(Time.realtimeSinceStartup>deadline)throw new TimeoutException("City mesh load180-second budget");Dictionary<string,object> row=Map(value);byte[] raw=File.ReadAllBytes(SafeFile(Text(row,"file")));if(Hash(raw)!=Text(row,"sha256"))throw new FormatException("City road mesh hash mismatch");Mesh mesh=ReadNCM(raw,Int(row,"triangles"));ownedMeshes.Add(mesh);MakeMesh("City road "+loadedChunks,scene.transform,mesh,roadMaterial,Vector3.zero);loadedChunks++;roadTriangles+=Int(row,"triangles");}
                catch(Exception e){Fail(e);good=false;}if(!good)yield break;
                if(loadedChunks%4==0){WriteState();yield return null;}
            }
            try
            {
                graph=LoadSelectedGraph();LoadSurfaceMetadata();LoadGradeProfile();BuildSpatial();LoadAndroid();LoadCharger();
                List<RegionPlan> plans=new List<RegionPlan>();foreach(object value in List(ReadMap(activeRegionFile),"regions")){Dictionary<string,object> r=Map(value);List<int> targets=new List<int>();foreach(object t in List(r,"targets"))targets.Add(checked((int)(double)t));plans.Add(new RegionPlan(Int(r,"id"),Int(r,"hub"),targets.ToArray(),Text(r,"name")));}
                if(networkName=="nagoya-citywide-building-service-v1") ValidateBuildingService(plans);
                foreach(RegionPlan p in plans){Point2 point=graph.GetNodePosition(p.Hub);GameObject charger=new GameObject("Blender city charger "+p.Id);charger.transform.parent=scene.transform;Vector3 pos=Position(point,p.Id*2+1,140,true);HubVisual custom;if(entranceHubs.TryGetValue(p.Id,out custom)){charger.transform.position=custom.Terminal;charger.transform.rotation=Quaternion.Euler(0,custom.Yaw,0);}else charger.transform.position=pos+new Vector3(1.2f,0,0);MakeMesh("Terminal",charger.transform,chargerMesh,chargerMaterial,Vector3.zero);chargers[p.Id]=charger;}
                simulation=new CitywideSimulation(graph,plans.ToArray(),this,androidsPerRegion);
                scene.AddComponent<AndroidLifePilot>().Initialize(this,Root,runId,null);
                scene.AddComponent<PlateauZoning>().Initialize(Root,runId);
                phase="running";WriteState();
            }
            catch(Exception e){Fail(e);}
        }
        private Mesh ReadNCM(byte[] bytes,int expected)
        {
            if(bytes.Length<12||bytes.Length>1920012)throw new FormatException("City mesh byte budget");using(BinaryReader r=new BinaryReader(new MemoryStream(bytes))){if(Encoding.ASCII.GetString(r.ReadBytes(4))!="NCM1")throw new FormatException("City NCM1 header");int n=r.ReadInt32(),tri=r.ReadInt32();if(n<3||n>60000||n!=tri*3||tri!=expected||bytes.Length!=12+n*32)throw new FormatException("City mesh counts");Vector3[] v=new Vector3[n],norm=new Vector3[n];Vector2[] uv=new Vector2[n];int[] ids=new int[n];for(int i=0;i<n;i++){float x=r.ReadSingle(),y=r.ReadSingle(),z=r.ReadSingle(),nx=r.ReadSingle(),ny=r.ReadSingle(),nz=r.ReadSingle(),u=r.ReadSingle(),vv=r.ReadSingle();if(float.IsNaN(x)||float.IsInfinity(x)||Math.Abs(x)>40000||Math.Abs(z)>40000||y<100||y>200||u<0||u>1||vv<0||vv>1)throw new FormatException("City mesh coordinate/UV");v[i]=new Vector3(x,y,z);norm[i]=new Vector3(nx,ny,nz);uv[i]=new Vector2(u,vv);ids[i]=i;}Mesh mesh=new Mesh();mesh.name="Nagoya source road NCM1";mesh.vertices=v;mesh.normals=norm;mesh.uv=uv;mesh.triangles=ids;mesh.RecalculateBounds();return mesh;}
        }
        private Material TextureMaterial(string path){Texture2D t=new Texture2D(2,2,TextureFormat.RGBA32,true);if(!t.LoadImage(File.ReadAllBytes(path)))throw new FormatException("City PNG decode");t.wrapMode=TextureWrapMode.Clamp;t.filterMode=FilterMode.Bilinear;ownedTextures.Add(t);Material m=new Material(Shader.Find("Legacy Shaders/Diffuse"));m.color=Color.white;m.mainTexture=t;ownedMaterials.Add(m);return m;}
        private GameObject MakeMesh(string name,Transform parent,Mesh mesh,Material material,Vector3 pivot){GameObject o=new GameObject(name);o.transform.parent=parent;o.transform.localPosition=pivot;o.AddComponent<MeshFilter>().sharedMesh=mesh;o.AddComponent<MeshRenderer>().sharedMaterial=material;return o;}
        private Mesh PartMesh(AndroidPartData part){int n=part.Vertices.Length/3;Vector3[] v=new Vector3[n],norm=new Vector3[n];Vector2[] uv=new Vector2[n];for(int i=0;i<n;i++){v[i]=new Vector3(part.Vertices[i*3],part.Vertices[i*3+1],part.Vertices[i*3+2]);norm[i]=new Vector3(part.Normals[i*3],part.Normals[i*3+1],part.Normals[i*3+2]);uv[i]=new Vector2(part.UV[i*2],part.UV[i*2+1]);}Mesh mesh=new Mesh();mesh.vertices=v;mesh.normals=norm;mesh.uv=uv;mesh.triangles=part.Triangles;mesh.RecalculateBounds();ownedMeshes.Add(mesh);return mesh;}
        private void LoadAndroid(){android=AndroidObjAsset.Load(RuntimePaths.Data(@"android-finish-v1\manifest.json"),RuntimePaths.DataRoot);Texture2D t=new Texture2D(2,2,TextureFormat.RGBA32,true);if(!t.LoadImage(android.Texture))throw new FormatException("Android texture");ownedTextures.Add(t);androidMaterial=new Material(Shader.Find("Legacy Shaders/Diffuse"));androidMaterial.mainTexture=t;androidMaterial.color=Color.white;ownedMaterials.Add(androidMaterial);foreach(AndroidPartData p in android.Parts)androidMeshes[p.Name]=PartMesh(p);}
        private void LoadCharger(){string dir=RuntimePaths.Data(@"charger-finish-v2");Dictionary<string,object> m=Map(JsonData.Parse(File.ReadAllText(Path.Combine(dir,"manifest.json"))));AndroidPartData p=AndroidObjAsset.ParsePart("charger",new float[]{0,0,0},File.ReadAllText(Path.Combine(dir,Text(m,"obj"))));chargerMesh=PartMesh(p);chargerMaterial=TextureMaterial(Path.Combine(dir,Text(m,"texture")));}
        private float EdgeWidth(int edge,byte kind){return surfaceMetadata==null?Width(kind):surfaceMetadata.Width(edge);}
        private void LoadSurfaceMetadata()
        {
            if(surfaceMetadataFile.Length==0)return;
            byte[] data=File.ReadAllBytes(SafeFile(surfaceMetadataFile));
            string actual=Hash(data);if(actual!=surfaceMetadataHash.ToLowerInvariant())throw new FormatException("Surface metadata hash mismatch");
            surfaceMetadata=SurfaceMetadata.Load(new MemoryStream(data),graph.EdgeCount);
            for(int e=0;e<graph.EdgeCount;e++){EdgeInfo info=graph.GetEdgeInfo(e);if(info.PedestrianAllowed&&Math.Abs(surfaceMetadata.Width(e)-Width(info.ClassId))>.01f)widthOverrides++;if(surfaceMetadata.MarkedCrossing(e))markedCrossings++;}
        }
        private void LoadGradeProfile()
        {
            if(gradeProfileFile.Length==0)return;
            string path=SafeFile(gradeProfileFile);if(new FileInfo(path).Length>16000000)throw new FormatException("Grade profile size");
            byte[] bytes=File.ReadAllBytes(path);if(Hash(bytes)!=gradeProfileHash.ToLowerInvariant())throw new FormatException("Grade profile hash mismatch");
            gradeProfile=GradeProfile.Load(new MemoryStream(bytes),graph);
        }
        private List<Span> BuildSpans(int edge)
        {
            EdgeInfo info=graph.GetEdgeInfo(edge);Point2[] points=graph.GetEdgePoints(edge,false);List<Span> result=new List<Span>();double distance=0;
            for(int i=1;i<points.Length;i++){
                double dx=(double)points[i].X-points[i-1].X,dz=(double)points[i].Z-points[i-1].Z,length=Math.Sqrt(dx*dx+dz*dz);
                result.Add(new Span{A=points[i-1],B=points[i],NormalA=SurfacePlacementMath.Miter(points,i-1),NormalB=SurfacePlacementMath.Miter(points,i),Width=EdgeWidth(edge,info.ClassId),Y=140.16f+Mathf.Clamp(info.Layer,0,3)*7,Foot=Foot(info.ClassId),Edge=edge,StartDistance=distance,Length=length});distance+=length;
            }
            return result;
        }
        private float SurfaceHeight(Span span,float t)
        {
            float value;if(gradeProfile!=null&&gradeProfile.TryHeight(span.Edge,span.StartDistance+t*span.Length,out value))return value;return span.Y;
        }
        private void BuildSpatial()
        {
            for(int e=0;e<graph.EdgeCount;e++){
                if(!graph.GetEdgeInfo(e).PedestrianAllowed)continue;
                foreach(Span s in BuildSpans(e)){
                    int x0=(int)Math.Floor((Math.Min(s.A.X,s.B.X)-32)/64),x1=(int)Math.Floor((Math.Max(s.A.X,s.B.X)+32)/64),z0=(int)Math.Floor((Math.Min(s.A.Z,s.B.Z)-32)/64),z1=(int)Math.Floor((Math.Max(s.A.Z,s.B.Z)+32)/64);
                    if(x1-x0>50||z1-z0>50)throw new FormatException("City span grid budget");
                    for(int x=x0;x<=x1;x++)for(int z=z0;z<=z1;z++){long key=Key(x,z);List<Span> list;if(!spatial.TryGetValue(key,out list)){list=new List<Span>();spatial.Add(key,list);}list.Add(s);}
                }
            }
        }
        private Vector3 Position(Point2 point,int id,float priorY,bool charger)
        {
            List<Span> list;if(!spatial.TryGetValue(Key((int)Math.Floor(point.X/64),(int)Math.Floor(point.Z/64)),out list))return new Vector3(point.X,140.16f,point.Z);
            return SurfacePosition(list,point,id,priorY,charger,0,false);
        }
        private Vector3 SurfacePosition(List<Span> list,Point2 point,int id,float priorY,bool charger,float heading,bool travelAligned)
        {
            float best=float.MaxValue,take=0;Span chosen=new Span();foreach(Span s in list){float dx=s.B.X-s.A.X,dz=s.B.Z-s.A.Z,l=dx*dx+dz*dz;if(l<.00001)continue;float t=Mathf.Clamp01(((point.X-s.A.X)*dx+(point.Z-s.A.Z)*dz)/l);float px=s.A.X+t*dx,pz=s.A.Z+t*dz;float cost=(point.X-px)*(point.X-px)+(point.Z-pz)*(point.Z-pz)+Mathf.Abs(SurfaceHeight(s,t)-priorY)*.02f;if(cost<best){best=cost;chosen=s;take=t;}}
            if(best==float.MaxValue)return new Vector3(point.X,140.16f,point.Z);
            float offset=chosen.Foot?.3f:chosen.Width/2+.65f;if(charger)offset=chosen.Foot?.8f:chosen.Width/2+1.1f;
            Point2 placed=SurfacePlacementMath.OffsetMiter(chosen.A,chosen.B,chosen.NormalA,chosen.NormalB,take,offset,id,heading,travelAligned);return new Vector3(placed.X,SurfaceHeight(chosen,take),placed.Z);
        }
        private Vector3 RoutedPosition(Point2 point,Actor actor,int edgeIndex,float heading)
        {
            actor.SurfaceEdge=edgeIndex;
            if(edgeIndex<0)return Position(point,actor.Id,actor.Root.transform.position.y==0?140:actor.Root.transform.position.y,false);
            List<Span> spans;if(!edgeSurfaces.TryGetValue(edgeIndex,out spans)){
                if(!graph.GetEdgeInfo(edgeIndex).PedestrianAllowed)throw new FormatException("Nonpedestrian visual surface");spans=BuildSpans(edgeIndex);
                edgeSurfaces.Add(edgeIndex,spans);
            }
            return SurfacePosition(spans,point,actor.Id,actor.Root.transform.position.y,false,heading,true);
        }
        public object CreateActor(string kind,int id)
        {
            Actor a=new Actor{Id=id,Kind=kind,Root=new GameObject("Citywide "+kind+" "+id)};a.Root.transform.parent=scene.transform;
            if(kind=="android")foreach(AndroidPartData p in android.Parts){GameObject o=MakeMesh(p.Name,a.Root.transform,androidMeshes[p.Name],androidMaterial,new Vector3(p.Pivot[0],p.Pivot[1],p.Pivot[2]));if(p.Name=="left-leg")a.Left=o;if(p.Name=="right-leg")a.Right=o;if(p.Name=="parcel")a.Parcel=o;}
            else if(kind=="human")
            {
                HumanModel m=Human(id%4==0?"Bluecollar Female 3":"Bluecollar Male 2");a.Human=m;a.HumanMesh=(Mesh)Instantiate(m.Body);ownedMeshes.Add(a.HumanMesh);MakeMesh("Body",a.Root.transform,a.HumanMesh,m.Material,Vector3.zero);
            }
            else throw new FormatException("City actor kind");actors.Add(a);return a;
        }

        internal object CreateResidentAndroid(int citizenId)
        {
            Actor actor=(Actor)CreateActor("android",citizenId);
            actor.Kind="native_android";
            actor.Root.name="Android resident "+citizenId;
            actor.Root.SetActive(false);
            if(actor.Parcel!=null)actor.Parcel.SetActive(false);
            return actor;
        }

        internal void ReleaseResidentAndroid(object visual)
        {
            Actor actor = (Actor)visual;
            actors.Remove(actor);
            Destroy(actor.Root);
        }

        internal void PoseResidentAndroid(object visual,bool visible,Vector3 position)
        {
            Actor actor=(Actor)visual;
            if(!visible){actor.Root.SetActive(false);return;}
            Vector3 movement=position-actor.Root.transform.position;
            movement.y=0;
            if(actor.Root.activeSelf&&movement.sqrMagnitude>.0001f&&movement.sqrMagnitude<16f)
            {
                actor.Root.transform.rotation=Quaternion.LookRotation(movement);
                actor.LastMeters+=movement.magnitude;
            }
            actor.Root.transform.position=position;
            actor.Root.SetActive(true);
            float stride=Mathf.Sin((float)actor.LastMeters*4)*16;
            if(actor.Left!=null)actor.Left.transform.localRotation=Quaternion.Euler(stride,0,0);
            if(actor.Right!=null)actor.Right.transform.localRotation=Quaternion.Euler(-stride,0,0);
        }
        private static int FindRoot(int[] parent,int index)
        {
            for(int step=0;step<parent.Length;step++){if(parent[index]==index)return index;parent[index]=parent[parent[index]];index=parent[index];}
            throw new InvalidOperationException("Human topology union cycle");
        }
        private HumanModel Human(string name)
        {
            HumanModel cached;
            if(humans.TryGetValue(name,out cached))return cached;
            CitizenInfo info=PrefabCollection<CitizenInfo>.FindLoaded(name);
            if(info==null||info.m_skinRenderer==null)throw new InvalidOperationException("Native citizen renderer missing: "+name);
            Mesh baked=new Mesh();info.m_skinRenderer.BakeMesh(baked);
            Vector3[] vertices=baked.vertices,normals=baked.normals;Vector2[] uv=baked.uv;int[] triangles=baked.triangles;
            Matrix4x4 toPrefab=info.transform.worldToLocalMatrix*info.m_skinRenderer.transform.localToWorldMatrix;
            Bounds bounds=new Bounds();
            for(int i=0;i<vertices.Length;i++)
            {
                vertices[i]=toPrefab.MultiplyPoint3x4(vertices[i]);
                if(i==0)bounds=new Bounds(vertices[i],Vector3.zero);else bounds.Encapsulate(vertices[i]);
                if(normals.Length==vertices.Length)normals[i]=toPrefab.MultiplyVector(normals[i]).normalized;
            }
            if(vertices.Length<10||bounds.size.y<bounds.size.z||bounds.size.y<.01f)throw new InvalidOperationException("Native human is not upright");
            float scale=1.72f/bounds.size.y;Vector3 shift=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
            int[] parent=new int[vertices.Length];Dictionary<string,int> welded=new Dictionary<string,int>();
            for(int i=0;i<vertices.Length;i++)
            {
                vertices[i]=(vertices[i]-shift)*scale;parent[i]=i;
                string key=Mathf.RoundToInt(vertices[i].x*10000)+","+Mathf.RoundToInt(vertices[i].y*10000)+","+Mathf.RoundToInt(vertices[i].z*10000);
                int other;if(welded.TryGetValue(key,out other))parent[i]=FindRoot(parent,other);else welded[key]=i;
            }
            for(int i=0;i<triangles.Length;i+=3)
            {
                int root=FindRoot(parent,triangles[i]);parent[FindRoot(parent,triangles[i+1])]=root;parent[FindRoot(parent,triangles[i+2])]=root;
            }
            Dictionary<int,Bounds> components=new Dictionary<int,Bounds>();
            for(int i=0;i<vertices.Length;i++){int root=FindRoot(parent,i);Bounds b;if(components.TryGetValue(root,out b))b.Encapsulate(vertices[i]);else b=new Bounds(vertices[i],Vector3.zero);components[root]=b;}
            HashSet<int> discard=new HashSet<int>();List<object> audit=new List<object>();
            foreach(KeyValuePair<int,Bounds> item in components)
            {
                Bounds b=item.Value;bool drop=b.max.y<.5f&&b.min.y<.15f&&Mathf.Abs(b.center.x)>.26f;
                if(drop)discard.Add(item.Key);
                audit.Add(new Dictionary<string,object>{{"component",item.Key},{"min",new float[]{b.min.x,b.min.y,b.min.z}},{"max",new float[]{b.max.x,b.max.y,b.max.z}},{"removedDetachedGroundAccessory",drop}});
            }
            List<int> keep=new List<int>();int removed=0;
            for(int i=0;i<triangles.Length;i+=3){if(discard.Contains(FindRoot(parent,triangles[i]))){removed++;continue;}keep.Add(triangles[i]);keep.Add(triangles[i+1]);keep.Add(triangles[i+2]);}
            if(keep.Count<triangles.Length/2)throw new InvalidOperationException("Human accessory filter removed too much geometry");
            removedHumanAccessoryTriangles+=removed;
            Mesh mesh=new Mesh();mesh.name="Native human continuous deform "+name;mesh.vertices=vertices;
            if(normals.Length==vertices.Length)mesh.normals=normals;else {normals=new Vector3[vertices.Length];for(int i=0;i<normals.Length;i++)normals[i]=Vector3.up;mesh.normals=normals;}
            mesh.uv=uv;mesh.triangles=keep.ToArray();mesh.RecalculateBounds();ownedMeshes.Add(mesh);
            Material native=info.m_skinRenderer.sharedMaterial;if(native==null||native.mainTexture==null)throw new InvalidOperationException("Native human albedo missing");
            Material material=new Material(Shader.Find("Legacy Shaders/Diffuse"));material.mainTexture=native.mainTexture;material.color=Color.white;ownedMaterials.Add(material);
            cached=new HumanModel{Body=mesh,Material=material,Vertices=vertices,Normals=normals};humans[name]=cached;
            humanSource="native CitizenInfo BakeMesh with skin-to-prefab transform; continuous weighted procedural gait, MOD movement";
            File.WriteAllText(Path.Combine(Root,"human-topology-"+name.Replace(" ","-")+".json"),JsonData.Stringify(new Dictionary<string,object>{{"source",name},{"trianglesBefore",triangles.Length/3},{"trianglesAfter",keep.Count/3},{"removedDetachedAccessoryTriangles",removed},{"components",audit}}));
            Destroy(baked);return cached;
        }
        private void AnimateHuman(Actor actor,float angle)
        {
            if(actor.Human==null||actor.HumanMesh==null)return;
            Vector3[] source=actor.Human.Vertices,normal=actor.Human.Normals;
            Vector3[] vertices=new Vector3[source.Length],normals=new Vector3[source.Length];
            for(int i=0;i<source.Length;i++)
            {
                Vector3 v=source[i];float weight=Mathf.Clamp01((1.02f-v.y)/.22f);weight=weight*weight*(3-2*weight);
                float side=v.x<0?-1:1;Quaternion rotation=Quaternion.Euler(side*angle*weight,0,0);Vector3 pivot=new Vector3(side*.11f,.82f,0);
                vertices[i]=pivot+rotation*(v-pivot);normals[i]=rotation*normal[i];
            }
            actor.HumanMesh.vertices=vertices;actor.HumanMesh.normals=normals;actor.HumanMesh.RecalculateBounds();
        }
        public void PlaceActor(object value,Point2 point,float headingDegrees,bool cargo,double meters){PlaceActorOnSurface(value,point,headingDegrees,cargo,meters,-1);}
        public void PlaceActorOnSurface(object value,Point2 point,float headingDegrees,bool cargo,double meters,int edgeIndex)
        {
            Actor a=(Actor)value;Vector3 position=RoutedPosition(point,a,edgeIndex,headingDegrees);float yaw=90-headingDegrees;int region=CitywideSimulation.RegionForActor(a.Id);int chargingActor;GameObject charger;
            if(a.Kind=="android"&&chargingActors.TryGetValue(region,out chargingActor)&&chargingActor==a.Id&&chargers.TryGetValue(region,out charger)){
                HubVisual custom;if(entranceHubs.TryGetValue(region,out custom)){position=custom.Dock;yaw=custom.RobotYaw;}else position=charger.transform.position+new Vector3(-.5f,0,0);}
            a.Root.transform.position=position;a.Root.transform.rotation=Quaternion.Euler(0,yaw,0);
            float angle=meters>a.LastMeters?.5f*Mathf.Sin((float)meters*4)*26:0;AnimateHuman(a,angle);if(a.Left!=null)a.Left.transform.localRotation=Quaternion.Euler(angle,0,0);if(a.Right!=null)a.Right.transform.localRotation=Quaternion.Euler(-angle,0,0);if(a.Parcel!=null)a.Parcel.SetActive(cargo);a.LastMeters=meters;
        }
        public void SetCharger(int region,bool value){SetChargingActor(region,value?region*2+1:0);}
        public void SetChargingActor(int region,int actorId){if(actorId==0)chargingActors.Remove(region);else chargingActors[region]=actorId;}
        private void ApplyAppearance()
        {
            if(roadMaterial==null||chargers.Count==0||Time.realtimeSinceStartup<nextAppearance)return;
            nextAppearance=Time.realtimeSinceStartup+.5f;
            string file=Path.Combine(Root,"road-appearance.json");if(!File.Exists(file))return;
            string attempt="";
            try {
                if(new FileInfo(file).Length>2048)throw new FormatException("Road appearance file size");
                byte[] bytes=File.ReadAllBytes(file);attempt=Hash(bytes);if(attempt==lastAppearanceAttempt)return;Dictionary<string,object> cfg=Map(JsonData.Parse(Encoding.UTF8.GetString(bytes)));object value;Dictionary<int,HubVisual> poses=null;string signature=attempt;
                if(cfg.TryGetValue("hubVisualsOverride",out value)){string poseFile=SafeFile((string)value);if(new FileInfo(poseFile).Length>65536)throw new FormatException("Hub override size");byte[] poseBytes=File.ReadAllBytes(poseFile);signature+="|"+Hash(poseBytes);attempt=signature;if(signature==lastAppearanceAttempt)return;lastAppearanceAttempt=signature;poses=ParseHubPoses(Map(JsonData.Parse(Encoding.UTF8.GetString(poseBytes))));}
                else {if(signature==lastAppearanceAttempt)return;lastAppearanceAttempt=signature;}
                if(!cfg.TryGetValue("roadGain",out value)||!(value is double))throw new FormatException("Numeric roadGain required");
                float gain=(float)(double)value;if(float.IsNaN(gain)||float.IsInfinity(gain)||gain<.2f||gain>1.5f)throw new FormatException("Road gain bound");
                if(poses!=null&&chargers.Count>0)ApplyHubPoses(poses);else if(poses!=null)return;
                roadMaterial.color=new Color(gain,gain,gain,1);appearanceSignature=signature;appearanceError="";
            } catch(Exception e) {lastAppearanceAttempt=attempt;appearanceError=e.Message;}
        }
        public void Update(){if(NativeConstruction){UrbanTerrainAppearance.Refresh();return;}ApplyAndroidAppearance();ApplyAppearance();if(simulation!=null&&error.Length==0){SimulationManager s=Singleton<SimulationManager>.instance;float dt=s.SimulationPaused?0:Time.deltaTime*Mathf.Clamp(s.FinalSimulationSpeed,0,3);try{simulation.Step(dt);}catch(Exception e){Fail(e);}}if(runId.Length>0&&Time.realtimeSinceStartup>=nextState){nextState=Time.realtimeSinceStartup+2;WriteState();}}
        private void Fail(Exception e){phase="error";error=e.ToString();Debug.LogException(e);WriteState();}
        private void WriteState(){try{Dictionary<string,object> m=new Dictionary<string,object>{{"utc",DateTime.UtcNow.ToString("o")},{"runId",runId},{"phase",phase},{"error",error},{"roadChunksLoaded",loadedChunks},{"roadChunksExpected",expectedChunks},{"roadTriangles",roadTriangles},{ "roadMaterialGain",roadMaterial!=null?roadMaterial.color.r:0f},{"appearanceError",appearanceError},{"appearanceSignature",appearanceSignature},{"surfacePlacementVersion","route-owned width, travel side and supported grades v3"},{"nativeRoadsCreated",0},{ "networkName",networkName},{"routingGraphSHA256",activeGraphHash},{"entranceBuildingTargets",buildingTargets},{"entranceHubCount",entranceHubs.Count},{"implementation","actual OSM source geometry; MOD-owned roads, routing and actor rules"},{"humanVisualSource",humanSource},{"humanWalkVersion","continuous deformation v2"},{"removedHumanAccessoryTriangles",removedHumanAccessoryTriangles},{"actorObjects",actors.Count},{"chargers",chargers.Count},{"assemblyMvid",GetType().Assembly.ManifestModule.ModuleVersionId.ToString()}};if(simulation!=null){
                m["simulation"]=JsonData.Parse(simulation.StateJson());
                Dictionary<string,object> sim=Map(m["simulation"]),totals=Map(sim["parcelTotals"]);
                CitizenManager citizens=CitizenManager.instance;
                int nativeRecords=citizens.m_citizenCount;
                uint nativeInstances=citizens.m_instances.ItemCount()-1u;
                m["nativeCitizenRecords"]=nativeRecords;
                m["nativeCitizenInstances"]=nativeInstances;
                int regionCount=(int)(double)sim["regionCount"];
                cityHud="NAGOYA / CITYWIDE TRANSFER\n"+regionCount+" regions | "+buildingTargets.Count+" building targets\nAndroid "+(regionCount*androidsPerRegion)+" / MOD pedestrians "+regionCount+"\nCS1 citizen instances "+nativeInstances+"\nDelivered "+totals["delivered"]+" | Collected "+totals["collected"]+"\nParcels conserved: "+totals["conserved"]+" | "+phase;
            }List<object> visuals=new List<object>();foreach(Actor a in actors){Vector3 p=a.Root.transform.position;visuals.Add(new Dictionary<string,object>{{"id",a.Id},{"kind",a.Kind},{"active",a.Root.activeInHierarchy},{"x",p.x},{"y",p.y},{"z",p.z},{"yaw",a.Root.transform.eulerAngles.y},{"surfaceEdge",a.SurfaceEdge},{"gradeStatus",gradeProfile==null||a.SurfaceEdge<0?-1:gradeProfile.Status(a.SurfaceEdge)}});}m["gradeProfileSHA256"]=gradeProfileHash;m["supportedGradeRamps"]=gradeProfile==null?0:gradeProfile.SupportedRamps;m["unsupportedShortGradeEdges"]=gradeProfile==null?0:gradeProfile.UnsupportedShort;m["negativeGradeUnverified"]=gradeProfile==null?0:gradeProfile.NegativeUnverified;m["surfaceMetadataSHA256"]=surfaceMetadataHash;m["surfaceMetadataEdges"]=surfaceMetadata==null?0:surfaceMetadata.EdgeCount;m["pedestrianSourceWidthOverrides"]=widthOverrides;m["sourceMarkedCrossingEdges"]=markedCrossings;m["androidManifest"]=androidManifest;m["androidAssetSHA256"]=android==null?"":android.Sha256;m["androidTriangles"]=android==null?0:android.TriangleCount;m["androidAppearanceError"]=androidAppearanceError;m["materialProfiles"]=JsonData.Parse(NagoyaRuntimeVisuals.RuntimeMaterialProfiles.StateJson());m["visuals"]=visuals;List<object> hubs=new List<object>();foreach(KeyValuePair<int,HubVisual> hub in entranceHubs){Vector3 actual=chargers.ContainsKey(hub.Key)?chargers[hub.Key].transform.position:hub.Value.Terminal;hubs.Add(new Dictionary<string,object>{{"regionID",hub.Key},{"sourceID",hub.Value.Source},{"terminalWorld",new float[]{actual.x,actual.y,actual.z}},{"dockWorld",new float[]{hub.Value.Dock.x,hub.Value.Dock.y,hub.Value.Dock.z}},{"yaw",hub.Value.Yaw},{"robotYaw",hub.Value.RobotYaw}});}m["hubVisuals"]=hubs;string file=Path.Combine(Root,"state.json"),tmp=file+".tmp";File.WriteAllText(tmp,JsonData.Stringify(m),new UTF8Encoding(false));if(File.Exists(file))File.Replace(tmp,file,null);else File.Move(tmp,file);writeError="";}catch(Exception e){writeError=e.Message;}}
        public void OnGUI()
        {
            if(runId.Length==0)return;
            Rect box=new Rect(Screen.width-420,20,400,165);GUI.Box(box,"");
            GUIStyle style=new GUIStyle(GUI.skin.label);style.fontSize=14;style.wordWrap=true;
            GUI.Label(new Rect(box.x+10,box.y+8,box.width-20,box.height-16),cityHud,style);
        }
        public void OnDestroy(){if(NativeConstruction)UrbanTerrainAppearance.Clear();if(scene!=null)Destroy(scene);foreach(Mesh m in ownedMeshes)if(m!=null)Destroy(m);foreach(Material m in ownedMaterials)if(m!=null)Destroy(m);foreach(Texture2D t in ownedTextures)if(t!=null)Destroy(t);}
    }
}
