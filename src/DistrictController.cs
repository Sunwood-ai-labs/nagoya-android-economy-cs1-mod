using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using ColossalFramework;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    public sealed class DistrictController : MonoBehaviour
    {
        private const string Root=@"H:\CS1-PLATEAU\nagoya-station";
        private const string Folder=Root+@"\simulation";
        public string Mode;
        private Engine engine;
        private GameObject scene;
        private Mesh siteWalkMesh;
        private ChargerVisuals chargerVisuals;
        private const float SiteWalkWidth=1.4f;
        private readonly List<Visual> visuals=new List<Visual>();
        private readonly List<Material> materials=new List<Material>();
        private DateTime stamp=DateTime.MinValue;
        private DateTime auditStamp=DateTime.MinValue;
        private float nextPoll,nextState;
        private string runId="",graphHash="",error="";
        private string visualWarning="";
        private int validatedSegments;
        private bool halted;
        private DateTime androidVisualStamp=DateTime.MinValue;
        private string androidVisualRevision="",androidVisualType="legacy_primitives",androidVisualError="",androidAssetSha="";
        private AndroidAssetData androidAsset;
        private readonly List<Mesh> androidMeshes=new List<Mesh>();
        private Material androidMaterial;
        private Texture2D androidTexture;
        private sealed class Visual
        {
            public GameObject Body,Parcel,LeftLeg,RightLeg;
            public Point Last;
        }
        private static Dictionary<string,object> Map(object value){Dictionary<string,object> m=value as Dictionary<string,object>;if(m==null)throw new FormatException("Expected object");return m;}
        private static List<object> List(Dictionary<string,object> m,string key){List<object> v=m[key] as List<object>;if(v==null)throw new FormatException("Expected array: "+key);return v;}
        private static string Text(Dictionary<string,object> m,string key){string v=m[key] as string;if(string.IsNullOrEmpty(v)||v.Length>200)throw new FormatException("Invalid text: "+key);return v;}
        private static float Number(Dictionary<string,object> m,string key)
        {
            if(!(m[key] is double))throw new FormatException("Expected number: "+key);double n=(double)m[key];
            if(double.IsNaN(n)||double.IsInfinity(n)||Math.Abs(n)>1000000000)throw new FormatException("Number outside range");return (float)n;
        }
        private static int Integer(Dictionary<string,object> m,string key){float n=Number(m,key);if(n!=(int)n)throw new FormatException("Expected integer");return (int)n;}
        private static Point Position(Dictionary<string,object> m){Point p=new Point(Number(m,"x"),Number(m,"z"));if(Math.Abs(p.X)>8500 || Math.Abs(p.Z)>8500)throw new FormatException("Position outside map");return p;}
        public void Start(){Directory.CreateDirectory(Folder);try{TerrainSampleOnce.Run();PollNativeAudit();Poll();PollAndroidVisual();WriteState();}catch(Exception ex){Fail(ex);}}
        public void Update()
        {
            if(Time.realtimeSinceStartup>=nextPoll)
            {
                nextPoll=Time.realtimeSinceStartup+1;try{PollNativeAudit();Poll();PollAndroidVisual();UrbanTerrainAppearance.Refresh();}catch(Exception ex){Fail(ex);}
            }
            if(engine==null || halted)return;
            try
            {
                if(SiteWalkTerrainMask.Error.Length>0)visualWarning="Sitewalk terrain update: "+SiteWalkTerrainMask.Error;
                SimulationManager sim=Singleton<SimulationManager>.instance;
                float dt=sim.SimulationPaused?0:Mathf.Min(Time.deltaTime,.1f)*Mathf.Clamp(sim.FinalSimulationSpeed,0,3);
                engine.Step(dt);UpdateVisuals();
                if(Time.realtimeSinceStartup>=nextState){nextState=Time.realtimeSinceStartup+1;WriteState();}
            }catch(Exception ex){Fail(ex);}
        }
        private void Poll()
        {
            string ini=Path.Combine(Folder,"district-command.ini");if(!File.Exists(ini))return;
            DateTime changed=File.GetLastWriteTimeUtc(ini);if(changed==stamp)return;stamp=changed;
            Dictionary<string,string> cfg=new Dictionary<string,string>();
            foreach(string raw in File.ReadAllLines(ini)){string[] pair=raw.Split(new char[]{'='},2);if(pair.Length==2)cfg[pair[0].Trim()]=pair[1].Trim();}
            Clear();error="";halted=false;
            if(!cfg.ContainsKey("enabled")||cfg["enabled"]!="true"){WriteState();return;}
            string single=@"H:\CS1-PLATEAU\android\demo-command.ini";
            if(File.Exists(single) && File.ReadAllText(single).IndexOf("enabled=true",StringComparison.OrdinalIgnoreCase)>=0)throw new InvalidOperationException("Disable the single-building android demo first");
            string path=cfg.ContainsKey("graphPath")?cfg["graphPath"]:Root+@"\roads\delivery-graph.json";
            path=Path.GetFullPath(path);if(!path.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Graph must stay under district root");
            if(new FileInfo(path).Length>8*1024*1024 || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Graph file boundary");
            byte[] bytes=File.ReadAllBytes(path);using(SHA256 hash=SHA256.Create())graphHash=BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
            Dictionary<string,object> data=Map(JsonData.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\ufeff')));
            string serviceSet=cfg.ContainsKey("serviceSet")?cfg["serviceSet"]:null;
            bool expandedServiceSet=ServiceSetContract.Expanded(serviceSet);
            if(Integer(data,"schemaVersion")!=1)throw new FormatException("Wrong graph schema");
            Graph graph=new Graph();List<object> nodes=List(data,"nodes");if(nodes.Count>4096)throw new FormatException("Node count");
            foreach(object value in nodes){Dictionary<string,object> n=Map(value);graph.Nodes.Add(Text(n,"id"),Position(n));}
            List<object> edges=List(data,"edges");if(edges.Count>8192)throw new FormatException("Edge count");HashSet<int> referenced=new HashSet<int>();
            foreach(object value in edges)
            {
                Dictionary<string,object> e=Map(value);Edge edge=new Edge();edge.Id=Text(e,"id");edge.From=Text(e,"from");edge.To=Text(e,"to");edge.Kind=Text(e,"kind");
                if(edge.Kind!="sidewalk" && edge.Kind!="crosswalk" && edge.Kind!="access" && edge.Kind!="sitewalk")throw new FormatException("Edge kind");
                foreach(object point in List(e,"points"))edge.Points.Add(Position(Map(point)));
                foreach(object segment in List(e,"nativeSegmentIds")){double n=Convert.ToDouble(segment,CultureInfo.InvariantCulture);if(n!=Math.Floor(n)||n<=0||n>65535)throw new FormatException("Segment ID");edge.NativeSegmentIds.Add((int)n);referenced.Add((int)n);}
                if(edge.Kind!="access" && edge.Kind!="sitewalk" && edge.NativeSegmentIds.Count==0)throw new FormatException("A road edge has no actual native segment");
                if(edge.Kind=="sitewalk" && edge.NativeSegmentIds.Count!=0)throw new FormatException("Authored sitewalk must not claim a native road identity");graph.Edges.Add(edge);
            }
            graph.Seal();ValidateNative(data,referenced);ValidateWalkCorridors(graph);
            List<string> expected=new List<string>();Dictionary<string,object> source=Map(JsonData.Parse(File.ReadAllText(Root+@"\source\district-source-manifest.json")));
            foreach(object value in List(source,"buildings"))expected.Add(Text(Map(value),"gml_id"));
            List<string> selected=new List<string>();
            if(expandedServiceSet)
            {
                Dictionary<string,object> selection=Map(JsonData.Parse(File.ReadAllText(Root+@"\ten-buildings-2050-v1\selection.json")));
                foreach(object value in List(selection,"selected"))selected.Add(Text(Map(value),"gml_id"));
            }
            List<Building> buildings=new List<Building>();HashSet<string> actual=new HashSet<string>();
            foreach(object value in List(data,"buildings"))
            {
                Dictionary<string,object> b=Map(value);Building building=new Building();building.Id=Text(b,"id");building.Name=Text(b,"name");building.NodeId=Text(b,"nodeId");building.Stock=Integer(b,"initialStock");
                buildings.Add(building);if(!actual.Add(building.Id))throw new FormatException("Duplicate building");
            }
            ServiceSetContract.Validate(serviceSet,Text(data,"districtId"),expected,selected,new List<string>(actual));
            List<Charger> chargers=new List<Charger>();foreach(object value in List(data,"chargers")){Dictionary<string,object> c=Map(value);Charger charger=new Charger();charger.Id=Text(c,"id");charger.NodeId=Text(c,"nodeId");chargers.Add(charger);}
            List<string> starts=new List<string>();foreach(object value in List(data,"robotStartNodeIds"))starts.Add((string)value);
            Engine loaded=new Engine(graph,buildings,chargers,starts);loaded.Repeat=cfg.ContainsKey("repeat") && cfg["repeat"]=="true";
            runId=DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ",CultureInfo.InvariantCulture);engine=loaded;engine.OnEvent=RecordEvent;BuildScene();
            RecordEvent(new Event{Kind="started",Round=1,Stored=engine.Stored,Carried=engine.Carried});WriteState();
        }
        private void ValidateNative(Dictionary<string,object> data,HashSet<int> referenced)
        {
            HashSet<int> verified=new HashSet<int>();NetManager net=Singleton<NetManager>.instance;
            foreach(object value in List(data,"nativeSegments"))
            {
                Dictionary<string,object> n=Map(value);int id=Integer(n,"id");if(id<=0||id>=net.m_segments.m_buffer.Length||!verified.Add(id))throw new FormatException("Native segment identity");
                NetSegment segment=net.m_segments.m_buffer[id];if((segment.m_flags&NetSegment.Flags.Created)==0)throw new InvalidOperationException("Native road is absent: "+id);
                if(segment.m_startNode!=Integer(n,"startNodeId")||segment.m_endNode!=Integer(n,"endNodeId"))throw new InvalidOperationException("Native road endpoints changed");
                double build=(double)n["buildIndex"];if(build<0||build>uint.MaxValue||build!=Math.Floor(build))throw new FormatException("Invalid native build index");
                if(segment.m_buildIndex!=(uint)build || segment.Info.name!=Text(n,"prefab"))throw new InvalidOperationException("Native road provenance changed");
                Vector3 a=net.m_nodes.m_buffer[segment.m_startNode].m_position,b=net.m_nodes.m_buffer[segment.m_endNode].m_position;
                if(Point.Distance(new Point(a.x,a.z),new Point(Number(n,"startX"),Number(n,"startZ")))>.5f || Point.Distance(new Point(b.x,b.z),new Point(Number(n,"endX"),Number(n,"endZ")))>.5f)throw new InvalidOperationException("Native road moved");
            }
            if(referenced.Count==0 || !referenced.IsSubsetOf(verified))throw new FormatException("Native road audit incomplete");validatedSegments=referenced.Count;
        }
        private void PollNativeAudit()
        {
            string request=Path.Combine(Folder,"native-audit-request.json");if(!File.Exists(request))return;
            DateTime changed=File.GetLastWriteTimeUtc(request);if(changed==auditStamp)return;auditStamp=changed;
            Dictionary<string,object> output=new Dictionary<string,object>{{"utc",DateTime.UtcNow.ToString("o")},{"mode",Mode},{"status","error"}};
            try
            {
                if(new FileInfo(request).Length>256*1024)throw new FormatException("Native audit request too large");
                Dictionary<string,object> input=Map(JsonData.Parse(File.ReadAllText(request)));output["revision"]=Text(input,"revision");
                List<object> ids=List(input,"ids");if(ids.Count<1||ids.Count>8192)throw new FormatException("Native audit ID count");
                HashSet<int> unique=new HashSet<int>();List<object> records=new List<object>();NetManager net=Singleton<NetManager>.instance;
                foreach(object value in ids)
                {
                    double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);int id=(int)number;
                    if(number!=id||id<=0||id>=net.m_segments.m_buffer.Length||!unique.Add(id))throw new FormatException("Native audit segment ID");
                    NetSegment s=net.m_segments.m_buffer[id];if((s.m_flags&NetSegment.Flags.Created)==0)throw new InvalidOperationException("Segment is not created: "+id);
                    Vector3 a=net.m_nodes.m_buffer[s.m_startNode].m_position,b=net.m_nodes.m_buffer[s.m_endNode].m_position;
                    records.Add(new Dictionary<string,object>{{"id",id},{"buildIndex",s.m_buildIndex},{"prefab",s.Info.name},{"halfWidth",s.Info.m_halfWidth},{"flags",s.m_flags.ToString()},{"startNodeId",s.m_startNode},{"endNodeId",s.m_endNode},{"startX",a.x},{"startY",a.y},{"startZ",a.z},{"endX",b.x},{"endY",b.y},{"endZ",b.z}});
                }
                output["segments"]=records;output["status"]="native fields read back";
            }
            catch(Exception ex){output["error"]=ex.Message;Debug.LogException(ex);}
            AtomicJson(Path.Combine(Folder,"native-road-audit.json"),output);
        }
        private void ValidateWalkCorridors(Graph graph)
        {
            NetManager net=Singleton<NetManager>.instance;int samples=0;DateTime deadline=DateTime.UtcNow.AddSeconds(5);
            foreach(Edge edge in graph.Edges)
            {
                if(edge.Kind=="access" || edge.Kind=="sitewalk")continue;
                for(int i=1;i<edge.Points.Count;i++)
                {
                    Point a=edge.Points[i-1],b=edge.Points[i];int count=Math.Max(1,(int)Math.Ceiling(Point.Distance(a,b)/4));
                    for(int j=0;j<=count;j++)
                    {
                        if(++samples>100000 || DateTime.UtcNow>deadline)throw new InvalidOperationException("Walk corridor audit budget exceeded");
                        Point p=new Point(a.X+(b.X-a.X)*j/count,a.Z+(b.Z-a.Z)*j/count);bool inside=false;
                        foreach(int id in edge.NativeSegmentIds)
                        {
                            NetSegment segment=net.m_segments.m_buffer[id];Vector3 closest=segment.GetClosestPosition(new Vector3(p.X,Height(p),p.Z));
                            if(Point.Distance(p,new Point(closest.x,closest.z))<=segment.Info.m_halfWidth+3){inside=true;break;}
                        }
                        if(!inside)throw new InvalidOperationException("Robot path leaves its native road corridor: "+edge.Id);
                    }
                }
            }
        }
        private Material Material(Color color)
        {
            Shader shader=Shader.Find("Legacy Shaders/Diffuse");if(shader==null)throw new InvalidOperationException("Required robot shader missing");
            Material m=new Material(shader);m.color=color;materials.Add(m);return m;
        }
        private GameObject Shape(string name,Transform parent,Vector3 p,Vector3 scale,Material m)
        {
            GameObject go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.parent=parent;go.transform.localPosition=p;go.transform.localScale=scale;go.GetComponent<Renderer>().sharedMaterial=m;Destroy(go.GetComponent<Collider>());return go;
        }
        private float Height(Point p){return Singleton<TerrainManager>.instance.SampleRawHeightSmoothWithWater(new Vector3(p.X,0,p.Z),false,0);}
        private void BuildSiteWalks(Material pavement)
        {
            List<Vector3> vertices=new List<Vector3>();List<int> triangles=new List<int>();
            foreach(Edge edge in engine.Graph.Edges)
            {
                if(edge.Kind!="sitewalk" && edge.Kind!="access")continue;
                for(int j=1;j<edge.Points.Count;j++)
                {
                    Point a=edge.Points[j-1],b=edge.Points[j];float length=Point.Distance(a,b);if(length<.001f)continue;
                    int pieces=Math.Max(1,(int)Math.Ceiling(length/2));if(pieces>4096)throw new InvalidOperationException("Sitewalk segment length budget");
                    float nx=-(b.Z-a.Z)/length*SiteWalkWidth*.5f,nz=(b.X-a.X)/length*SiteWalkWidth*.5f;
                    for(int k=0;k<pieces;k++)
                    {
                        float t=(float)k/pieces,u=(float)(k+1)/pieces;
                        Point p=new Point(a.X+(b.X-a.X)*t,a.Z+(b.Z-a.Z)*t),q=new Point(a.X+(b.X-a.X)*u,a.Z+(b.Z-a.Z)*u);
                        int n=vertices.Count;
                        AddWalkVertex(vertices,new Point(p.X+nx,p.Z+nz));AddWalkVertex(vertices,new Point(q.X+nx,q.Z+nz));
                        AddWalkVertex(vertices,new Point(p.X-nx,p.Z-nz));AddWalkVertex(vertices,new Point(q.X-nx,q.Z-nz));
                        triangles.Add(n);triangles.Add(n+1);triangles.Add(n+2);triangles.Add(n+1);triangles.Add(n+3);triangles.Add(n+2);
                    }
                }
                foreach(Point p in edge.Points)
                {
                    int n=vertices.Count;AddWalkVertex(vertices,p);
                    for(int k=0;k<12;k++){float angle=k*Mathf.PI/6;AddWalkVertex(vertices,new Point(p.X+Mathf.Cos(angle)*SiteWalkWidth*.5f,p.Z+Mathf.Sin(angle)*SiteWalkWidth*.5f));}
                    for(int k=0;k<12;k++){triangles.Add(n);triangles.Add(n+1+(k+1)%12);triangles.Add(n+1+k);}
                }
            }
            if(vertices.Count==0)return;
            siteWalkMesh=new Mesh();siteWalkMesh.name="Authored 1.4m sitewalk surface";siteWalkMesh.vertices=vertices.ToArray();siteWalkMesh.triangles=triangles.ToArray();siteWalkMesh.RecalculateNormals();siteWalkMesh.RecalculateBounds();
            GameObject walks=new GameObject("Terrain-following sitewalks - authored, not native Citizen paths");walks.transform.parent=scene.transform;
            walks.AddComponent<MeshFilter>().sharedMesh=siteWalkMesh;walks.AddComponent<MeshRenderer>().sharedMaterial=pavement;
        }
        private void AddWalkVertex(List<Vector3> vertices,Point p)
        {
            if(vertices.Count>=60000)throw new InvalidOperationException("Sitewalk mesh vertex budget exceeded");vertices.Add(new Vector3(p.X,Height(p)+.16f,p.Z));
        }
        private void BuildScene()
        {
            scene=new GameObject("Nagoya station road delivery - "+runId);Material white=Material(new Color(.8f,.84f,.87f)),dark=Material(new Color(.06f,.09f,.12f)),cargo=Material(new Color(.88f,.49f,.15f)),green=Material(new Color(.12f,.75f,.32f));
            BuildSiteWalks(Material(new Color(.24f,.26f,.27f)));
            try{SiteWalkTerrainMask.Install(engine.Graph,Path.Combine(Folder,"publicrealm-mask.json"),engine.Chargers);}catch(Exception ex){visualWarning=ex.GetBaseException().Message;Debug.LogException(ex);}
            foreach(Building b in engine.Buildings){Point p=engine.Graph.Nodes[b.NodeId];Shape(b.Name+" service point",scene.transform,new Vector3(p.X,Height(p)+.04f,p.Z),new Vector3(.8f,.06f,.8f),green);}
            List<Vector3> chargerAnchors=new List<Vector3>();List<GameObject> chargerPosts=new List<GameObject>();
            foreach(Charger c in engine.Chargers){Point p=engine.Graph.Nodes[c.NodeId];float ground=Height(p);Shape(c.Id+" charging pad",scene.transform,new Vector3(p.X,ground+.13f,p.Z),new Vector3(1.8f,.06f,1.8f),dark);chargerPosts.Add(Shape(c.Id+" charger",scene.transform,new Vector3(p.X,ground+.6f,p.Z+.7f),new Vector3(.28f,1.2f,.28f),green));chargerAnchors.Add(new Vector3(p.X,ground+.16f,p.Z+.7f));}
            chargerVisuals=scene.AddComponent<ChargerVisuals>();chargerVisuals.Initialize(chargerAnchors,chargerPosts);
            Color[] colors={new Color(.08f,.8f,.95f),new Color(.96f,.53f,.1f),new Color(.7f,.35f,.95f)};
            foreach(Robot r in engine.Robots)
            {
                Visual v=new Visual();v.Last=r.Position;v.Body=new GameObject("Courier A"+r.Id);v.Body.transform.parent=scene.transform;Material accent=Material(colors[r.Id-1]);
                Shape("torso",v.Body.transform,new Vector3(0,1,0),new Vector3(.48f,.58f,.3f),white);
                Shape("head",v.Body.transform,new Vector3(0,1.51f,0),new Vector3(.36f,.33f,.32f),white);
                Shape("visor",v.Body.transform,new Vector3(0,1.54f,.17f),new Vector3(.29f,.09f,.025f),accent);
                Shape("left arm",v.Body.transform,new Vector3(-.32f,.98f,0),new Vector3(.12f,.5f,.13f),dark);
                Shape("right arm",v.Body.transform,new Vector3(.32f,.98f,0),new Vector3(.12f,.5f,.13f),dark);
                v.LeftLeg=Shape("left leg",v.Body.transform,new Vector3(-.13f,.36f,0),new Vector3(.16f,.65f,.19f),dark);
                v.RightLeg=Shape("right leg",v.Body.transform,new Vector3(.13f,.36f,0),new Vector3(.16f,.65f,.19f),dark);
                v.Parcel=Shape("parcel",v.Body.transform,new Vector3(0,.98f,.33f),new Vector3(.39f,.28f,.29f),cargo);visuals.Add(v);
            }UpdateVisuals();
        }
        private void ReleaseAndroidResources()
        {
            foreach(Mesh mesh in androidMeshes)if(mesh!=null)Destroy(mesh);androidMeshes.Clear();
            if(androidMaterial!=null)Destroy(androidMaterial);if(androidTexture!=null)Destroy(androidTexture);
            androidMaterial=null;androidTexture=null;androidAsset=null;androidAssetSha="";androidVisualType="legacy_primitives";
            androidVisualRevision="";androidVisualStamp=DateTime.MinValue;androidVisualError="";
        }
        private void PollAndroidVisual()
        {
            if(engine==null||scene==null)return;
            string ini=Path.Combine(Folder,"android-visual-command.ini");if(!File.Exists(ini))return;
            DateTime changed=File.GetLastWriteTimeUtc(ini);if(changed==androidVisualStamp)return;androidVisualStamp=changed;
            List<Mesh> meshes=new List<Mesh>();List<Visual> candidates=new List<Visual>();Texture2D texture=null;Material material=null;
            try
            {
                FileInfo info=new FileInfo(ini);if(info.Length>4096||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new FormatException("Android visual INI boundary");
                Dictionary<string,string> cfg=new Dictionary<string,string>();foreach(string line in File.ReadAllLines(ini)){string[] pair=line.Split(new char[]{'='},2);if(pair.Length==2)cfg[pair[0].Trim()]=pair[1].Trim();}
                string revision=cfg["revision"];if(revision.Length<1||revision.Length>100)throw new FormatException("Android visual revision required");
                if(revision==androidVisualRevision)return;
                AndroidAssetData asset=AndroidObjAsset.Load(cfg["assetPath"],Root);
                texture=new Texture2D(2,2,TextureFormat.RGBA32,true);if(!texture.LoadImage(asset.Texture)||texture.width!=asset.Width||texture.height!=asset.Height)throw new FormatException("Atlas PNG decode failed");
                foreach(Color32 pixel in texture.GetPixels32())if(pixel.a!=255)throw new FormatException("Android atlas must be opaque");
                texture.wrapMode=TextureWrapMode.Clamp;texture.filterMode=FilterMode.Bilinear;texture.anisoLevel=4;
                Shader shader=Shader.Find("Legacy Shaders/Diffuse");if(shader==null)throw new InvalidOperationException("Legacy Diffuse shader missing");material=new Material(shader);material.color=Color.white;material.mainTexture=texture;
                foreach(AndroidPartData part in asset.Parts)
                {
                    Mesh mesh=new Mesh();meshes.Add(mesh);mesh.name="Nagoya android "+part.Name;int count=part.Vertices.Length/3;Vector3[] vv=new Vector3[count],nn=new Vector3[count];Vector2[] uv=new Vector2[count];
                    for(int i=0;i<count;i++){vv[i]=new Vector3(part.Vertices[i*3],part.Vertices[i*3+1],part.Vertices[i*3+2]);nn[i]=new Vector3(part.Normals[i*3],part.Normals[i*3+1],part.Normals[i*3+2]);uv[i]=new Vector2(part.UV[i*2],part.UV[i*2+1]);}
                    mesh.vertices=vv;mesh.normals=nn;mesh.uv=uv;mesh.triangles=part.Triangles;mesh.RecalculateBounds();
                }
                foreach(Robot robot in engine.Robots)
                {
                    Visual v=new Visual();v.Last=robot.Position;v.Body=new GameObject("Blender courier A"+robot.Id);v.Body.SetActive(false);v.Body.transform.parent=scene.transform;candidates.Add(v);
                    for(int i=0;i<asset.Parts.Count;i++)
                    {
                        AndroidPartData part=asset.Parts[i];GameObject go=new GameObject(part.Name);go.transform.parent=v.Body.transform;go.transform.localPosition=new Vector3(part.Pivot[0],part.Pivot[1],part.Pivot[2]);go.transform.localRotation=Quaternion.identity;go.transform.localScale=Vector3.one;
                        go.AddComponent<MeshFilter>().sharedMesh=meshes[i];go.AddComponent<MeshRenderer>().sharedMaterial=material;
                        if(part.Name=="left-leg")v.LeftLeg=go;else if(part.Name=="right-leg")v.RightLeg=go;else if(part.Name=="parcel")v.Parcel=go;
                    }
                    v.Parcel.SetActive(robot.Cargo);
                    int index=candidates.Count-1;if(visuals.Count>index){v.Body.transform.rotation=visuals[index].Body.transform.rotation;v.Last=visuals[index].Last;}
                    bool parked=robot.State=="charging"||robot.State=="waiting_for_charge"||robot.State=="idle";Vector3 position=new Vector3(robot.Position.X,Height(robot.Position)+(parked?.18f:.02f),robot.Position.Z);if(parked)position.x+=(robot.Id-2)*.65f;v.Body.transform.position=position;
                    float stride=robot.State=="charging"||robot.State=="waiting_for_charge"||robot.State=="idle"||robot.State=="loading"||robot.State=="unloading"?0:Mathf.Sin((float)robot.Meters*5)*24;
                    v.LeftLeg.transform.localRotation=Quaternion.Euler(stride,0,0);v.RightLeg.transform.localRotation=Quaternion.Euler(-stride,0,0);
                }
                foreach(Visual old in visuals){old.Body.SetActive(false);Destroy(old.Body);}visuals.Clear();ReleaseAndroidResources();
                visuals.AddRange(candidates);androidMeshes.AddRange(meshes);androidTexture=texture;androidMaterial=material;androidAsset=asset;androidAssetSha=asset.Sha256;
                androidVisualRevision=revision;androidVisualType="blender_obj_split4";androidVisualError="";
                foreach(Visual v in visuals)v.Body.SetActive(true);
            }
            catch(Exception ex)
            {
                foreach(Visual v in candidates)if(v.Body!=null)Destroy(v.Body);foreach(Mesh mesh in meshes)if(mesh!=null)Destroy(mesh);if(material!=null)Destroy(material);if(texture!=null)Destroy(texture);
                androidVisualError="Visual reload rejected; previous visuals retained: "+ex.GetBaseException().Message;Debug.LogException(ex);
            }
        }
        private void UpdateVisuals()
        {
            for(int i=0;i<engine.Robots.Count;i++)
            {
                Robot r=engine.Robots[i];Visual v=visuals[i];Point delta=new Point(r.Position.X-v.Last.X,r.Position.Z-v.Last.Z);
                bool parked=r.State=="charging"||r.State=="waiting_for_charge"||r.State=="idle";
                Vector3 p=new Vector3(r.Position.X,Height(r.Position)+(androidAsset==null?.15f:parked?.18f:.02f),r.Position.Z);
                if(parked)p.x+=(r.Id-2)*.65f;
                v.Body.transform.position=p;if(Point.Distance(r.Position,v.Last)>.001f)v.Body.transform.rotation=Quaternion.LookRotation(new Vector3(delta.X,0,delta.Z));
                float stride=parked||r.State=="loading"||r.State=="unloading"?0:Mathf.Sin((float)r.Meters*5)*24;
                v.LeftLeg.transform.localRotation=Quaternion.Euler(stride,0,0);v.RightLeg.transform.localRotation=Quaternion.Euler(-stride,0,0);v.Parcel.SetActive(r.Cargo);v.Last=r.Position;
            }
        }
        private void RecordEvent(Event e)
        {
            Dictionary<string,object> row=new Dictionary<string,object>{{"utc",DateTime.UtcNow.ToString("o")},{"runId",runId},{"event",e.Kind},{"round",e.Round},{"robot",e.RobotId},{"jobId",e.JobId},{"from",e.FromBuilding},{"to",e.ToBuilding},{"state",e.State},{"battery",e.Battery},{"x",e.X},{"z",e.Z},{"deliveries",e.Deliveries},{"charges",e.Charges},{"stored",e.Stored},{"carried",e.Carried}};
            File.AppendAllText(Path.Combine(Folder,"events.jsonl"),JsonData.Stringify(row)+"\n");
        }
        private void WriteState()
        {
            Dictionary<string,object> data=new Dictionary<string,object>{{"utc",DateTime.UtcNow.ToString("o")},{"runId",runId},{"mode",Mode},{"assemblyMvid",GetType().Assembly.ManifestModule.ModuleVersionId.ToString()},{"status",halted?"error":engine==null?"disabled":engine.Finished?"finished":"running"},{"error",error},{"graphSha256",graphHash},{"nativeSegmentsValidated",validatedSegments},{"implementation","native CS1 roads; independent rule-based sidewalk graph and parcel inventory; not Citizen AI or standard logistics"}};
            data["androidVisualType"]=androidVisualType;data["androidVisualStatus"]=androidVisualError.Length>0?"reload_rejected_previous_visuals_retained":androidAsset==null?"legacy_primitives":"loaded";
            data["androidVisualError"]=androidVisualError;data["androidVisualRevision"]=androidVisualRevision;data["androidAssetSha256"]=androidAssetSha;
            data["androidAssetParts"]=androidAsset==null?0:androidAsset.Parts.Count;data["androidAssetTriangles"]=androidAsset==null?0:androidAsset.TriangleCount;data["androidAssetCorners"]=androidAsset==null?0:androidAsset.CornerCount;
            data["androidMeshActorCount"]=androidAsset==null?0:visuals.Count;data["androidWidthMeters"]=androidAsset==null?0:androidAsset.AssembledWidth;data["androidHeightMeters"]=androidAsset==null?0:androidAsset.AssembledHeight;data["androidFeetMinY"]=androidAsset==null?0:androidAsset.FeetMinY;
            data["chargerVisuals"]=chargerVisuals==null?null:chargerVisuals.Describe();
            if(engine!=null)
            {
                data["siteWalkWidthMeters"]=SiteWalkWidth;data["siteWalkMeshVertices"]=siteWalkMesh==null?0:siteWalkMesh.vertexCount;data["siteWalkMeshActive"]=siteWalkMesh!=null&&scene!=null&&scene.activeInHierarchy;
                data["siteWalkTerrainMaskCells"]=SiteWalkTerrainMask.CellCount;data["publicRealmMaskRegionCount"]=SiteWalkTerrainMask.PublicRealmRegionCount;data["publicRealmMaskCells"]=SiteWalkTerrainMask.PublicRealmCellCount;data["siteWalkTerrainUpdateCompleted"]=SiteWalkTerrainMask.UpdateCompleted;data["siteWalkTerrainError"]=SiteWalkTerrainMask.Error;
                data["chargerApronSurfaceCells"]=SiteWalkTerrainMask.ChargerApronCellCount;
                data["urbanTerrainAppearanceActive"]=UrbanTerrainAppearance.Active;
                data["urbanTerrainTextureName"]=UrbanTerrainAppearance.TextureName;
                data["urbanTerrainAppearanceMode"]="authored_neutral_concrete_in_native_grass_and_pavement_layers; global theme; original heights unchanged";
                data["urbanTerrainTextureSHA256"]=UrbanTerrainAppearance.TextureSHA256;
                data["urbanTerrainTextureError"]=UrbanTerrainAppearance.TextureError;
                data["visualWarning"]=visualWarning;
                data["deliveredTotal"]=engine.TotalDeliveries;data["completedRounds"]=engine.CompletedRounds;data["stored"]=engine.Stored;data["carried"]=engine.Carried;
                List<object> robots=new List<object>(),buildings=new List<object>(),jobs=new List<object>();
                for(int i=0;i<engine.Robots.Count;i++)
                {
                    Robot r=engine.Robots[i];Vector3 p=visuals.Count>i?visuals[i].Body.transform.position:Vector3.zero;
                    List<object> actualParts=new List<object>();int activeParts=0;
                    if(androidAsset!=null&&visuals.Count>i)
                    {
                        foreach(MeshFilter filter in visuals[i].Body.GetComponentsInChildren<MeshFilter>(true))
                        {
                            bool active=filter.gameObject.activeInHierarchy;Renderer renderer=filter.GetComponent<Renderer>();bool rendered=active&&renderer!=null&&renderer.enabled;
                            if(rendered)activeParts++;Mesh mesh=filter.sharedMesh;actualParts.Add(new Dictionary<string,object>{{"name",filter.gameObject.name},{"active",active},{"rendererEnabled",renderer!=null&&renderer.enabled},{"vertices",mesh==null?0:mesh.vertexCount},{"triangles",mesh==null?0:mesh.triangles.Length/3}});
                        }
                    }
                    robots.Add(new Dictionary<string,object>{{"id",r.Id},{"state",r.State},{"battery",r.Battery},{"cargo",r.Cargo},{"jobId",r.Job==null?null:r.Job.Id},{"x",r.Position.X},{"z",r.Position.Z},{"worldX",p.x},{"worldY",p.y},{"worldZ",p.z},{"visualType",androidVisualType},{"meshPartCount",actualParts.Count},{"activeMeshPartCount",activeParts},{"meshParts",actualParts},{"objectActive",visuals.Count>i&&visuals[i].Body.activeInHierarchy},{"deliveries",r.Deliveries},{"charges",r.Charges},{"meters",r.Meters}});
                }
                foreach(Building b in engine.Buildings)buildings.Add(new Dictionary<string,object>{{"id",b.Id},{"name",b.Name},{"nodeId",b.NodeId},{"stock",b.Stock},{"pickups",b.Pickups},{"deliveries",b.Deliveries}});
                foreach(Job j in engine.Jobs)jobs.Add(new Dictionary<string,object>{{"id",j.Id},{"status",j.Status},{"from",j.From.Id},{"to",j.To.Id},{"robot",j.RobotId}});
                data["robots"]=robots;data["buildings"]=buildings;data["jobs"]=jobs;
            }
            AtomicJson(Path.Combine(Folder,"state.json"),data);
        }
        private static void AtomicJson(string path,object data){string temp=path+".tmp";File.WriteAllText(temp,JsonData.Stringify(data));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
        private void Fail(Exception ex){error=ex.Message;halted=true;Debug.LogException(ex);try{WriteState();}catch(Exception io){Debug.LogException(io);}}
        private void Clear(){ReleaseAndroidResources();SiteWalkTerrainMask.Clear();if(scene!=null)Destroy(scene);scene=null;chargerVisuals=null;if(siteWalkMesh!=null)Destroy(siteWalkMesh);siteWalkMesh=null;visuals.Clear();foreach(Material m in materials)if(m!=null)Destroy(m);materials.Clear();engine=null;validatedSegments=0;}
        public void OnDestroy(){Clear();try{WriteState();}catch(Exception ex){Debug.LogException(ex);}}
        public void OnGUI()
        {
            if(engine==null&&!halted)return;GUI.Box(new Rect(12,90,510,190),"NAGOYA STATION / ROAD DELIVERY EXPERIMENT");
            GUI.Label(new Rect(24,116,480,24),"Native roads / custom robot routes and parcel inventory");
            if(engine!=null)
            {
                GUI.Label(new Rect(24,140,480,24),engine.Buildings.Count+" buildings | deliveries "+engine.TotalDeliveries+" | round "+engine.CompletedRounds+" | parcels "+engine.Stored+" + carried "+engine.Carried);
                for(int i=0;i<engine.Robots.Count;i++){Robot r=engine.Robots[i];GUI.Label(new Rect(24,166+i*24,480,24),"A"+r.Id+" "+r.State+" "+r.Battery.ToString("0.0")+"% D:"+r.Deliveries+" C:"+r.Charges);}
            }
            if(error.Length>0)GUI.Label(new Rect(24,248,480,28),error);else if(androidVisualError.Length>0)GUI.Label(new Rect(24,248,480,28),androidVisualError);
        }
    }
}
