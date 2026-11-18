using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    // Visual-only replacement. The delivery engine and charger occupancy are unchanged.
    public sealed class ChargerVisuals : MonoBehaviour
    {
        private const string Root=@"H:\CS1-PLATEAU\nagoya-station";
        private readonly List<Vector3> anchors=new List<Vector3>();
        private readonly List<GameObject> legacy=new List<GameObject>();
        private readonly List<GameObject> terminals=new List<GameObject>();
        private Mesh mesh;
        private Material material;
        private Texture2D texture;
        private DateTime stamp=DateTime.MinValue;
        private float nextPoll;
        private string revision="",sha="",error="";
        private int triangles;

        internal void Initialize(List<Vector3> positions,List<GameObject> oldPosts)
        {
            if(positions.Count<1||positions.Count>10||oldPosts.Count!=positions.Count)throw new InvalidOperationException("Charger visuals must match the engine's one-to-ten charger anchors");
            anchors.AddRange(positions);legacy.AddRange(oldPosts);Poll();
        }
        public void Update()
        {
            if(Time.realtimeSinceStartup<nextPoll)return;
            nextPoll=Time.realtimeSinceStartup+1;Poll();
        }
        private static string Leaf(string dir,string name)
        {
            if(string.IsNullOrEmpty(name)||Path.GetFileName(name)!=name||name.IndexOf(':')>=0)throw new FormatException("Charger asset filename required");
            return Path.Combine(dir,name);
        }
        private static byte[] Read(string path,int budget)
        {
            FileInfo info=new FileInfo(path);
            if(!info.Exists||info.Length<1||info.Length>budget||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new FormatException("Charger file boundary");
            byte[] data=File.ReadAllBytes(path);if(data.Length<1||data.Length>budget)throw new FormatException("Charger file size changed");return data;
        }
        private static string Hash(params byte[][] inputs)
        {
            using(MemoryStream stream=new MemoryStream())
            {
                foreach(byte[] input in inputs){byte[] size=BitConverter.GetBytes(input.Length);stream.Write(size,0,size.Length);stream.Write(input,0,input.Length);}
                using(SHA256 hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-","").ToLowerInvariant();
            }
        }
        private void Poll()
        {
            string ini=Path.Combine(Root,@"simulation\charger-visual-command.ini");if(!File.Exists(ini))return;
            DateTime changed=File.GetLastWriteTimeUtc(ini);if(changed==stamp)return;stamp=changed;
            Mesh candidateMesh=null;Material candidateMaterial=null;Texture2D candidateTexture=null;
            List<GameObject> candidates=new List<GameObject>();
            try
            {
                string text=Encoding.UTF8.GetString(Read(ini,4096)).TrimStart('\ufeff');Dictionary<string,string> cfg=new Dictionary<string,string>();
                foreach(string line in text.Split('\n')){string[] pair=line.Split(new char[]{'='},2);if(pair.Length==2)cfg[pair[0].Trim()]=pair[1].Trim();}
                string requested=cfg["revision"];if(requested.Length<1||requested.Length>100)throw new FormatException("Charger revision required");
                if(requested==revision)return;
                string full=Path.GetFullPath(cfg["assetPath"]),prefix=Path.GetFullPath(Root).TrimEnd('\\')+"\\";
                if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(full)!="manifest.json")throw new FormatException("Charger manifest outside district root");
                string dir=Path.GetDirectoryName(full);
                for(DirectoryInfo d=new DirectoryInfo(dir);d!=null;d=d.Parent)if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new FormatException("Charger directory reparse point");
                byte[] manifest=Read(full,65536);Dictionary<string,object> data=JsonData.Parse(Encoding.UTF8.GetString(manifest).TrimStart('\ufeff')) as Dictionary<string,object>;
                if(data==null||!(data["schemaVersion"] is double)||(double)data["schemaVersion"]!=1)throw new FormatException("Charger schema1 required");
                string obj=data["obj"] as string,png=data["texture"] as string;
                if(obj==null||!obj.EndsWith(".obj",StringComparison.OrdinalIgnoreCase)||png==null||!png.EndsWith(".png",StringComparison.OrdinalIgnoreCase))throw new FormatException("Charger OBJ/PNG required");
                byte[] objBytes=Read(Leaf(dir,obj),2*1024*1024),pngBytes=Read(Leaf(dir,png),8*1024*1024);
                if(pngBytes.Length<33||pngBytes[0]!=137||pngBytes[1]!=80||pngBytes[2]!=78||pngBytes[3]!=71||pngBytes[4]!=13||pngBytes[5]!=10||pngBytes[6]!=26||pngBytes[7]!=10)throw new FormatException("Charger PNG signature");
                AndroidPartData part=AndroidObjAsset.ParsePart("charger",new float[]{0,0,0},Encoding.UTF8.GetString(objBytes).TrimStart('\ufeff'));
                int count=part.Vertices.Length/3;if(part.Triangles.Length/3>1800||count>5400)throw new FormatException("Charger mesh budget");
                candidateMesh=new Mesh();candidateMesh.name="Blender2050 charging terminal";
                Vector3[] vv=new Vector3[count],nn=new Vector3[count];Vector2[] uv=new Vector2[count];
                for(int i=0;i<count;i++){vv[i]=new Vector3(part.Vertices[i*3],part.Vertices[i*3+1],part.Vertices[i*3+2]);nn[i]=new Vector3(part.Normals[i*3],part.Normals[i*3+1],part.Normals[i*3+2]);uv[i]=new Vector2(part.UV[i*2],part.UV[i*2+1]);}
                candidateMesh.vertices=vv;candidateMesh.normals=nn;candidateMesh.uv=uv;candidateMesh.triangles=part.Triangles;candidateMesh.RecalculateBounds();
                Bounds bounds=candidateMesh.bounds;Vector3 size=bounds.size;
                if(size.x<.4f||size.x>.6501f||size.y<1.35f||size.y>1.6501f||size.z<.25f||size.z>.5501f||Mathf.Abs(bounds.min.y)>.03f||Mathf.Abs(bounds.center.x)>.05f||Mathf.Abs(bounds.center.z)>.05f)throw new FormatException("Charger metre scale, centred ground pivot or dimensions invalid");
                candidateTexture=new Texture2D(2,2,TextureFormat.RGBA32,true);
                if(!candidateTexture.LoadImage(pngBytes)||candidateTexture.width<1||candidateTexture.width>2048||candidateTexture.height<1||candidateTexture.height>2048)throw new FormatException("Charger texture decode/dimensions");
                foreach(Color32 pixel in candidateTexture.GetPixels32())if(pixel.a!=255)throw new FormatException("Charger atlas must be opaque");
                candidateTexture.wrapMode=TextureWrapMode.Clamp;candidateTexture.filterMode=FilterMode.Bilinear;candidateTexture.anisoLevel=4;
                Shader shader=Shader.Find("Legacy Shaders/Diffuse");if(shader==null)throw new InvalidOperationException("Charger Legacy Diffuse shader unavailable");
                candidateMaterial=new Material(shader);candidateMaterial.color=Color.white;candidateMaterial.mainTexture=candidateTexture;
                for(int i=0;i<anchors.Count;i++)
                {
                    GameObject terminal=new GameObject("Blender charger "+(i+1));terminal.SetActive(false);candidates.Add(terminal);
                    terminal.transform.parent=transform;terminal.transform.position=anchors[i];terminal.transform.rotation=Quaternion.identity;terminal.transform.localScale=Vector3.one;
                    terminal.AddComponent<MeshFilter>().sharedMesh=candidateMesh;terminal.AddComponent<MeshRenderer>().sharedMaterial=candidateMaterial;
                }
                string digest=Hash(manifest,pngBytes,objBytes);
                Release();terminals.AddRange(candidates);mesh=candidateMesh;material=candidateMaterial;texture=candidateTexture;
                foreach(GameObject old in legacy)if(old!=null)old.SetActive(false);
                foreach(GameObject terminal in terminals)terminal.SetActive(true);
                revision=requested;sha=digest;triangles=part.Triangles.Length/3;error="";
            }
            catch(Exception ex)
            {
                foreach(GameObject terminal in candidates)if(terminal!=null)Destroy(terminal);
                if(candidateMesh!=null)Destroy(candidateMesh);if(candidateMaterial!=null)Destroy(candidateMaterial);if(candidateTexture!=null)Destroy(candidateTexture);
                error="Charger visual reload rejected; previous visual retained: "+ex.GetBaseException().Message;Debug.LogException(ex);
            }
        }
        internal Dictionary<string,object> Describe()
        {
            List<object> rows=new List<object>();int oldActive=0;
            foreach(GameObject old in legacy)if(old!=null&&old.activeInHierarchy)oldActive++;
            foreach(GameObject terminal in terminals)
            {
                if(terminal==null)continue;Renderer renderer=terminal.GetComponent<Renderer>();Vector3 p=terminal.transform.position;
                rows.Add(new Dictionary<string,object>{{"name",terminal.name},{"active",terminal.activeInHierarchy},{"rendererEnabled",renderer!=null&&renderer.enabled},{"x",p.x},{"y",p.y},{"z",p.z}});
            }
            return new Dictionary<string,object>{{"status",error.Length>0?"reload_rejected_previous_visuals_retained":mesh==null?"legacy_primitives":"loaded"},{"error",error},{"revision",revision},{"sha256",sha},{"trianglesPerTerminal",triangles},{"legacyActiveCount",oldActive},{"terminals",rows}};
        }
        private void Release()
        {
            foreach(GameObject terminal in terminals)if(terminal!=null){terminal.SetActive(false);Destroy(terminal);}terminals.Clear();
            if(mesh!=null)Destroy(mesh);if(material!=null)Destroy(material);if(texture!=null)Destroy(texture);mesh=null;material=null;texture=null;
        }
        public void OnDestroy(){Release();}
    }
}
