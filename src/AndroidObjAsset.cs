using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace NagoyaRoadDelivery
{
    internal sealed class AndroidPartData
    {
        internal string Name;
        internal float[] Pivot,Vertices,Normals,UV;
        internal int[] Triangles;
    }
    internal sealed class AndroidAssetData
    {
        internal string Sha256;
        internal byte[] Texture;
        internal int Width,Height,TriangleCount,CornerCount;
        internal float AssembledWidth,AssembledHeight,FeetMinY;
        internal readonly List<AndroidPartData> Parts=new List<AndroidPartData>();
    }
    internal static class AndroidObjAsset
    {
        private static readonly string[] Names={"body","left-leg","right-leg","parcel"};
        private static float N(string s)
        {
            float x;if(!float.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out x)||float.IsNaN(x)||float.IsInfinity(x)||Math.Abs(x)>10)throw new FormatException("Nonfinite or excessive OBJ coordinate");return x;
        }
        private static string SafePath(string root,string leaf)
        {
            if(string.IsNullOrEmpty(leaf)||Path.IsPathRooted(leaf)||Path.GetFileName(leaf)!=leaf||leaf=="."||leaf==".."||leaf.IndexOf(':')>=0)throw new FormatException("Asset path must be a filename");
            return Path.Combine(root,leaf);
        }
        private static byte[] Read(string path,int max)
        {
            FileInfo f=new FileInfo(path);if(!f.Exists||f.Length==0||f.Length>max||(f.Attributes&FileAttributes.ReparsePoint)!=0)throw new FormatException("Asset file boundary: "+Path.GetFileName(path));
            byte[] b=File.ReadAllBytes(path);if(b.Length==0||b.Length>max)throw new FormatException("Asset file changed size");return b;
        }
        private static int Index(string s,int count)
        {
            int i;if(!int.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out i)||i==0)throw new FormatException("OBJ index");i=i<0?count+i:i-1;if(i<0||i>=count)throw new FormatException("OBJ index outside array");return i;
        }
        internal static AndroidPartData ParsePart(string name,float[] pivot,string text)
        {
            List<float[]> vs=new List<float[]>(),ns=new List<float[]>(),uvs=new List<float[]>();List<float> vertices=new List<float>(),normals=new List<float>(),uv=new List<float>();List<int> triangles=new List<int>();
            using(StringReader sr=new StringReader(text))
            {
                string line;int lineCount=0;
                while((line=sr.ReadLine())!=null)
                {
                    if(++lineCount>40000||line.Length>4096)throw new FormatException("OBJ line budget");int comment=line.IndexOf('#');if(comment>=0)line=line.Substring(0,comment);
                    string[] w=line.Split(new char[]{' ','\t'},StringSplitOptions.RemoveEmptyEntries);if(w.Length==0)continue;
                    if(w[0]=="v"||w[0]=="vn")
                    {
                        if(w.Length!=4)throw new FormatException("OBJ xyz required");float[] v={N(w[1]),N(w[2]),-N(w[3])};
                        if(w[0]=="v")vs.Add(v);else{double l=Math.Sqrt(v[0]*v[0]+v[1]*v[1]+v[2]*v[2]);if(l<.001)throw new FormatException("Zero OBJ normal");for(int k=0;k<3;k++)v[k]/=(float)l;ns.Add(v);}
                    }
                    else if(w[0]=="vt")
                    {
                        if(w.Length!=3&&w.Length!=4)throw new FormatException("OBJ UV required");float u=N(w[1]),v=N(w[2]);if(u<0||u>1||v<0||v>1)throw new FormatException("Atlas UV outside 0..1");if(w.Length==4)N(w[3]);uvs.Add(new float[]{u,v});
                    }
                    else if(w[0]=="f")
                    {
                        if(w.Length!=4)throw new FormatException("OBJ must be triangulated");if(triangles.Count>=24000)throw new FormatException("Triangle budget");int start=vertices.Count/3;
                        for(int k=1;k<=3;k++){string[] f=w[k].Split('/');if(f.Length!=3||f[1].Length==0||f[2].Length==0)throw new FormatException("Full v/vt/vn corners required");vertices.AddRange(vs[Index(f[0],vs.Count)]);uv.AddRange(uvs[Index(f[1],uvs.Count)]);normals.AddRange(ns[Index(f[2],ns.Count)]);}
                        float ax=vertices[start*3+3]-vertices[start*3],ay=vertices[start*3+4]-vertices[start*3+1],az=vertices[start*3+5]-vertices[start*3+2];float bx=vertices[start*3+6]-vertices[start*3],by=vertices[start*3+7]-vertices[start*3+1],bz=vertices[start*3+8]-vertices[start*3+2];
                        float cx=ay*bz-az*by,cy=az*bx-ax*bz,cz=ax*by-ay*bx;if(cx*cx+cy*cy+cz*cz<1e-16)throw new FormatException("Degenerate OBJ triangle");
                        triangles.Add(start);triangles.Add(start+2);triangles.Add(start+1);
                    }
                    else if(w[0]!="o"&&w[0]!="g"&&w[0]!="s"&&w[0]!="usemtl"&&w[0]!="mtllib")throw new FormatException("Unsupported OBJ statement: "+w[0]);
                    if(vs.Count>24000||ns.Count>24000||uvs.Count>24000)throw new FormatException("OBJ element budget");
                }
            }
            if(triangles.Count==0)throw new FormatException("Empty OBJ part");return new AndroidPartData{Name=name,Pivot=pivot,Vertices=vertices.ToArray(),Normals=normals.ToArray(),UV=uv.ToArray(),Triangles=triangles.ToArray()};
        }
        private static int Big(byte[] b,int offset){return (b[offset]<<24)|(b[offset+1]<<16)|(b[offset+2]<<8)|b[offset+3];}
        internal static AndroidAssetData Load(string manifestPath,string allowedRoot)
        {
            string allowed=Path.GetFullPath(allowedRoot).TrimEnd('\\')+"\\",full=Path.GetFullPath(manifestPath);
            if(!full.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(full)!="manifest.json")throw new FormatException("Android manifest outside allowed district root");
            DirectoryInfo dir=new DirectoryInfo(Path.GetDirectoryName(full));for(DirectoryInfo d=dir;d!=null;d=d.Parent)if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new FormatException("Asset directory reparse point");
            byte[] manifest=Read(full,65536);Dictionary<string,object> m=JsonData.Parse(Encoding.UTF8.GetString(manifest).TrimStart('\ufeff')) as Dictionary<string,object>;if(m==null)throw new FormatException("Asset manifest object required");
            List<object> entries=m["parts"] as List<object>;if(entries==null||entries.Count!=4)throw new FormatException("Exactly four Android parts required");
            string texture=m.ContainsKey("texture")?m["texture"] as string:null;if(texture==null||!texture.EndsWith(".png",StringComparison.OrdinalIgnoreCase))throw new FormatException("PNG atlas required");
            AndroidAssetData a=new AndroidAssetData();a.Texture=Read(SafePath(dir.FullName,texture),8*1024*1024);
            byte[] b=a.Texture;if(b.Length<33||b[0]!=137||b[1]!=80||b[2]!=78||b[3]!=71||b[4]!=13||b[5]!=10||b[6]!=26||b[7]!=10||Encoding.ASCII.GetString(b,12,4)!="IHDR")throw new FormatException("Invalid PNG header");
            a.Width=Big(b,16);a.Height=Big(b,20);if(a.Width<1||a.Height<1||a.Width>2048||a.Height>2048||b[24]!=8||(b[25]!=2&&b[25]!=6))throw new FormatException("Atlas must be RGB/RGBA 8-bit and <=2048");
            HashSet<string> seen=new HashSet<string>();float minX=10,maxX=-10,minY=10,maxY=-10;List<byte[]> digestInputs=new List<byte[]>();digestInputs.Add(manifest);digestInputs.Add(a.Texture);
            foreach(object entry in entries)
            {
                Dictionary<string,object> e=entry as Dictionary<string,object>;if(e==null)throw new FormatException("Invalid part manifest");string name=e["name"] as string;if(Array.IndexOf(Names,name)<0||!seen.Add(name))throw new FormatException("Missing/duplicate/unknown Android part");
                List<object> pp=e["pivotUnity"] as List<object>;if(pp==null||pp.Count!=3)throw new FormatException("Unity pivot xyz required");float[] pivot=new float[3];for(int i=0;i<3;i++){if(!(pp[i] is double))throw new FormatException("Numeric pivot required");pivot[i]=N(((double)pp[i]).ToString("R",CultureInfo.InvariantCulture));if(Math.Abs(pivot[i])>2)throw new FormatException("Pivot outside actor bounds");}
                string file=e["obj"] as string;if(file==null||!file.EndsWith(".obj",StringComparison.OrdinalIgnoreCase))throw new FormatException("OBJ part required");byte[] bytes=Read(SafePath(dir.FullName,file),2*1024*1024);digestInputs.Add(bytes);
                AndroidPartData part=ParsePart(name,pivot,Encoding.UTF8.GetString(bytes).TrimStart('\ufeff'));a.Parts.Add(part);a.TriangleCount+=part.Triangles.Length/3;a.CornerCount+=part.Vertices.Length/3;
                if(name!="parcel")for(int i=0;i<part.Vertices.Length;i+=3){float x=part.Vertices[i]+pivot[0],y=part.Vertices[i+1]+pivot[1];minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}
            }
            if(a.TriangleCount>8000||a.CornerCount>24000)throw new FormatException("Android total mesh budget exceeded");
            if(maxX-minX>.6501f||maxX-minX<.15f||maxY-minY<1.4f||maxY-minY>1.95f||minY<-.05f||minY>.1f)throw new FormatException("Android metre scale/feet/width validation failed");
            a.AssembledWidth=maxX-minX;a.AssembledHeight=maxY-minY;a.FeetMinY=minY;
            using(MemoryStream ms=new MemoryStream()){foreach(byte[] input in digestInputs){byte[] length=BitConverter.GetBytes(input.Length);ms.Write(length,0,length.Length);ms.Write(input,0,input.Length);}using(SHA256 hash=SHA256.Create())a.Sha256=BitConverter.ToString(hash.ComputeHash(ms.ToArray())).Replace("-","").ToLowerInvariant();}return a;
        }
    }
}
