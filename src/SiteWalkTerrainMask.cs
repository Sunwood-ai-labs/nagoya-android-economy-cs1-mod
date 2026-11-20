using System;
using System.IO;
using System.Collections.Generic;
using ColossalFramework;
using HarmonyLib;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    // Pure bounded rasterization; terrain cell originals are never modified.
    internal sealed class PublicRealmMaskCells
    {
        public readonly HashSet<int> Cells=new HashSet<int>();
        public int Regions;
        public bool Urban;
        public float MinX=float.MaxValue,MinZ=float.MaxValue,MaxX=float.MinValue,MaxZ=float.MinValue;
        private static double Number(Dictionary<string,object> row,string key)
        {
            object value;if(!row.TryGetValue(key,out value)||!(value is double))throw new FormatException("Mask coordinate must be numeric: "+key);
            double n=(double)value;if(double.IsNaN(n)||double.IsInfinity(n)||Math.Abs(n)>7900)throw new FormatException("Mask coordinate outside map");return n;
        }
        public static PublicRealmMaskCells Parse(string text)
        {
            if(text==null||text.Length>4096)throw new FormatException("Public realm mask JSON budget");
            Dictionary<string,object> root=JsonData.Parse(text) as Dictionary<string,object>;if(root==null)throw new FormatException("Mask object required");
            foreach(string key in root.Keys)if(key!="enabled"&&key!="revision"&&key!="regions"&&key!="profile")throw new FormatException("Unknown mask field");
            if(!root.ContainsKey("enabled")||!(root["enabled"] is bool))throw new FormatException("Mask enabled boolean required");
            PublicRealmMaskCells result=new PublicRealmMaskCells();if(!(bool)root["enabled"])return result;
            string profile=root.ContainsKey("profile")?root["profile"] as string:"";
            if(profile!=""&&profile!="urban-station-paving-v1")throw new FormatException("Unknown pavement profile");
            bool urban=profile=="urban-station-paving-v1";
            result.Urban=urban;
            List<object> regions=root.ContainsKey("regions")?root["regions"] as List<object>:null;
            if(regions==null||regions.Count<1||regions.Count>(urban?8:2))throw new FormatException("Public realm region budget");
            HashSet<string> ids=new HashSet<string>();
            foreach(object item in regions)
            {
                Dictionary<string,object> row=item as Dictionary<string,object>;if(row==null||row.Count!=5)throw new FormatException("Invalid mask region fields");
                foreach(string key in row.Keys)if(key!="id"&&key!="minX"&&key!="minZ"&&key!="maxX"&&key!="maxZ")throw new FormatException("Unknown mask region field");
                string id=row.ContainsKey("id")?row["id"] as string:null;
                bool known=id=="PR1"||id=="PR2";
                if(urban)for(int region=1;region<=8;region++)if(id=="URBAN"+region)known=true;
                if(!known||!ids.Add(id))throw new FormatException("Duplicate or unknown public realm ID");
                double ax=Number(row,"minX"),az=Number(row,"minZ"),bx=Number(row,"maxX"),bz=Number(row,"maxZ");
                double maximumSide=urban?3000:100;
                if(bx<=ax||bz<=az||bx-ax>maximumSide||bz-az>maximumSide)throw new FormatException("Public realm rectangle size bound");
                int x0=Math.Max(0,(int)Math.Floor((ax-2)/4)+2160),x1=Math.Min(4319,(int)Math.Ceiling((bx-2)/4)+2160);
                int z0=Math.Max(0,(int)Math.Floor((az-2)/4)+2160),z1=Math.Min(4319,(int)Math.Ceiling((bz-2)/4)+2160);
                for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)
                {
                    double wx=(x-2160)*4+2,wz=(z-2160)*4+2;
                    if(wx>=ax&&wx<=bx&&wz>=az&&wz<=bz)result.Cells.Add(z*4320+x);
                }
                if(result.Cells.Count>(urban?400000:2000))throw new FormatException("Public realm surface cell budget");
                result.MinX=Math.Min(result.MinX,(float)ax);result.MinZ=Math.Min(result.MinZ,(float)az);
                result.MaxX=Math.Max(result.MaxX,(float)bx);result.MaxZ=Math.Max(result.MaxZ,(float)bz);result.Regions++;
            }
            if(result.Cells.Count==0)throw new FormatException("No public realm surface cells");return result;
        }
        public static PublicRealmMaskCells Read(string path)
        {
            if(string.IsNullOrEmpty(path))return new PublicRealmMaskCells();
            string full=Path.GetFullPath(path);
            if(!string.Equals(full,RuntimePaths.Data(@"simulation\publicrealm-mask.json"),StringComparison.OrdinalIgnoreCase))throw new FormatException("Unexpected public realm mask path");
            if(!File.Exists(full))return new PublicRealmMaskCells();
            if(new FileInfo(full).Length>4096)throw new FormatException("Public realm mask file budget");
            FileSystemInfo current=new FileInfo(full);
            for(int depth=0;current!=null&&depth<32;depth++)
            {
                if((current.Attributes&FileAttributes.ReparsePoint)!=0)throw new FormatException("Linked mask path rejected");
                FileInfo file=current as FileInfo;DirectoryInfo dir=current as DirectoryInfo;
                current=file!=null?file.Directory:dir.Parent;
            }
            return Parse(File.ReadAllText(full));
        }
    }
    internal static class SiteWalkTerrainMask
    {
        private const string Owner="sunwood.nagoya.sitewalk-terrain";
        private static volatile HashSet<int> cells=new HashSet<int>();
        private static Harmony harmony;
        public static volatile bool UpdateCompleted;
        public static volatile string Error="";
        private static float minX,minZ,maxX,maxZ;
        public static int CellCount { get { return cells.Count; } }
        public static int PublicRealmRegionCount { get; private set; }
        // Unique cells selected by the public rectangles, including any overlap with walk cells.
        public static int PublicRealmCellCount { get; private set; }
        public static int ChargerApronCellCount { get; private set; }
        public static void Install(Graph graph) { Install(graph,null); }
        public static void Install(Graph graph,string publicRealmMaskFile)
        { Install(graph,publicRealmMaskFile,null); }
        public static void Install(Graph graph,string publicRealmMaskFile,IEnumerable<Charger> chargers)
        {
            PublicRealmMaskCells realm=PublicRealmMaskCells.Read(publicRealmMaskFile);
            HashSet<int> next=new HashSet<int>();float ax=float.MaxValue,az=float.MaxValue,bx=float.MinValue,bz=float.MinValue;
            if(realm.Regions>0)
            {
                next.UnionWith(realm.Cells);ax=Math.Min(ax,realm.MinX);az=Math.Min(az,realm.MinZ);bx=Math.Max(bx,realm.MaxX);bz=Math.Max(bz,realm.MaxZ);
            }
            // The native surface grid is4m. Select only the cells touched by
            // each existing1.8m pad plus10cm margin, not the entire walk graph.
            HashSet<int> apron=new HashSet<int>();int chargerCount=0;
            if(chargers!=null)foreach(Charger charger in chargers)
            {
                if(++chargerCount>10)throw new InvalidOperationException("Charger apron count budget");
                Point p=graph.Nodes[charger.NodeId];
                int x0=(int)Math.Floor((p.X-1)/4)+2160,x1=(int)Math.Floor((p.X+1)/4)+2160;
                int z0=(int)Math.Floor((p.Z-1)/4)+2160,z1=(int)Math.Floor((p.Z+1)/4)+2160;
                if(x0<0||z0<0||x1>4319||z1>4319)throw new InvalidOperationException("Charger apron outside native surface");
                for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)apron.Add(z*4320+x);
                ax=Math.Min(ax,(x0-2160)*4);az=Math.Min(az,(z0-2160)*4);
                bx=Math.Max(bx,(x1-2160+1)*4);bz=Math.Max(bz,(z1-2160+1)*4);
            }
            next.UnionWith(apron);ChargerApronCellCount=apron.Count;
            if(next.Count==0){Clear();return;}if(next.Count>400000)throw new InvalidOperationException("Pavement mask area budget");
            minX=ax;minZ=az;maxX=bx;maxZ=bz;UpdateCompleted=false;Error="";
            Harmony candidate=new Harmony(Owner);
            candidate.Patch(typeof(TerrainManager).GetMethod("GetSurfaceCell",new Type[]{typeof(int),typeof(int)}),null,
                new HarmonyMethod(typeof(SiteWalkTerrainMask).GetMethod("SurfacePostfix",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)),null,null);
            harmony=candidate;cells=next;PublicRealmRegionCount=realm.Regions;PublicRealmCellCount=realm.Cells.Count;
            if(realm.Urban)UrbanTerrainAppearance.Install();else UrbanTerrainAppearance.Clear();
            Refresh(ax,az,bx,bz);
        }
        private static void SurfacePostfix(int x,int z,ref TerrainManager.SurfaceCell __result)
        {
            if(!cells.Contains(z*4320+x))return;
            __result.m_pavementA=255;__result.m_pavementB=0;__result.m_ruined=0;__result.m_gravel=0;__result.m_field=0;
        }
        private static void Refresh(float ax,float az,float bx,float bz)
        {
            Singleton<SimulationManager>.instance.AddAction(delegate
            {
                try
                {
                    TerrainModify.BeginUpdateArea();
                    try{TerrainModify.UpdateArea(ax-8,az-8,bx+8,bz+8,false,true,false);}
                    finally{TerrainModify.EndUpdateArea();}
                    UpdateCompleted=true;
                }
                catch(Exception ex){Error=ex.Message;Debug.LogException(ex);}
            });
        }
        public static void Clear()
        {
            UrbanTerrainAppearance.Clear();
            bool hadMask=cells.Count>0;cells=new HashSet<int>();PublicRealmRegionCount=0;PublicRealmCellCount=0;ChargerApronCellCount=0;
            if(harmony!=null){harmony.UnpatchAll(Owner);harmony=null;}
            if(hadMask&&Singleton<SimulationManager>.exists)Refresh(minX,minZ,maxX,maxZ);
            UpdateCompleted=false;Error="";
        }
    }
}
