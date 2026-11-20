using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    public sealed class HumanDistrictController:MonoBehaviour
    {
        public string Mode;
        private static readonly string Root =RuntimePaths.DataRoot;
        private NagoyaStationBlock.HumanCoexistence humans;
        private DateTime stamp=DateTime.MinValue;
        private float nextPoll;
        private sealed class Road {public float X,Z,DX,DZ,Length;}
        public void Update()
        {
            if(Time.realtimeSinceStartup<nextPoll)return;nextPoll=Time.realtimeSinceStartup+1;
            string path=Root+@"\simulation\human-command.ini";if(!File.Exists(path))return;
            DateTime changed=File.GetLastWriteTimeUtc(path);if(changed==stamp)return;stamp=changed;
            try
            {
                Dictionary<string,string> cfg=new Dictionary<string,string>();
                foreach(string line in File.ReadAllLines(path)){string[] pair=line.Split(new char[]{'='},2);if(pair.Length==2)cfg[pair[0].Trim()]=pair[1].Trim();}
                if(!cfg.ContainsKey("enabled")||cfg["enabled"]!="true"){if(humans!=null){humans.enabled=false;Destroy(humans);humans=null;}return;}
                int maxTransitions=NagoyaStationBlock.NativeHumanRouteLoop.DefaultMaxTransitions;
                if(cfg.ContainsKey("maxTransitions"))
                {
                    if(!int.TryParse(cfg["maxTransitions"],NumberStyles.Integer,CultureInfo.InvariantCulture,out maxTransitions)||!NagoyaStationBlock.NativeHumanRouteLoop.IsValidMaxTransitions(maxTransitions))throw new FormatException("maxTransitions must be an integer in [16,4096]");
                }
                if(humans!=null)throw new InvalidOperationException("Disable humans and observe cleanup before reconfiguring");
                if(cfg.ContainsKey("routePairsPath"))
                {
                    string pairPath=Path.GetFullPath(cfg["routePairsPath"]);
                    if(!pairPath.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase))throw new FormatException("Human route pairs outside district");
                    if(new FileInfo(pairPath).Length>1024*1024)throw new FormatException("Human route pair file size");
                    Dictionary<string,object> pairData=(Dictionary<string,object>)JsonData.Parse(File.ReadAllText(pairPath).TrimStart('\ufeff'));
                    List<object> pairs=(List<object>)pairData["pairs"];
                    if(pairs.Count<1||pairs.Count>32)throw new FormatException("Explicit human route count must be in [1,32]");
                    Vector3[] pairStarts=new Vector3[pairs.Count],pairEnds=new Vector3[pairs.Count];
                    for(int i=0;i<pairs.Count;i++)
                    {
                        Dictionary<string,object> pair=(Dictionary<string,object>)pairs[i];
                        pairStarts[i]=ReadRoutePoint((Dictionary<string,object>)pair["start"]);
                        pairEnds[i]=ReadRoutePoint((Dictionary<string,object>)pair["end"]);
                    }
                    humans=gameObject.AddComponent<NagoyaStationBlock.HumanCoexistence>();humans.RuntimeStatePath=Root+@"\simulation\humans-state.json";
                    if(!humans.LoadPairs(pairStarts,pairEnds,maxTransitions)||!humans.SpawnNativeHumans())throw new InvalidOperationException("Explicit native human initialization blocked; inspect humans-state.json");
                    File.WriteAllText(Root+@"\simulation\human-controller.json",JsonData.Stringify(new Dictionary<string,object>{{"utc",DateTime.UtcNow.ToString("o")},{"mode",Mode},{"routeMode","explicit_native_path_pairs"},{"routePairsPath",pairPath},{"status","native spawn queued; actual movement unverified"},{"requested",pairs.Count},{"maxTransitions",maxTransitions}}));
                    return;
                }
                string graphPath=Path.GetFullPath(cfg["graphPath"]);if(!graphPath.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase))throw new FormatException("Human graph outside district");
                if(new FileInfo(graphPath).Length>8*1024*1024)throw new FormatException("Human graph size");
                Dictionary<string,object> data=(Dictionary<string,object>)JsonData.Parse(File.ReadAllText(graphPath).TrimStart('\ufeff'));
                List<Road> roads=new List<Road>();
                foreach(object item in (List<object>)data["nativeSegments"])
                {
                    Dictionary<string,object> n=(Dictionary<string,object>)item;Road r=new Road();r.X=Convert.ToSingle(n["startX"]);r.Z=Convert.ToSingle(n["startZ"]);
                    r.DX=Convert.ToSingle(n["endX"])-r.X;r.DZ=Convert.ToSingle(n["endZ"])-r.Z;r.Length=Mathf.Sqrt(r.DX*r.DX+r.DZ*r.DZ);
                    if(r.Length>=70){r.DX/=r.Length;r.DZ/=r.Length;roads.Add(r);}
                }
                roads.Sort(delegate(Road a,Road b){return b.Length.CompareTo(a.Length);});
                if(roads.Count<5)throw new FormatException("Five sufficiently long native sidewalk corridors required");
                Vector3[] starts=new Vector3[12],ends=new Vector3[12];
                for(int i=0;i<12;i++)
                {
                    Road r=roads[(i/2)%5];float side=(i%2==0)?1:-1;float t0=i<10?.15f:.3f,t1=i<10?.85f:.7f;
                    Point a=new Point(r.X+r.DX*r.Length*t0-r.DZ*7.2f*side,r.Z+r.DZ*r.Length*t0+r.DX*7.2f*side);
                    Point b=new Point(r.X+r.DX*r.Length*t1-r.DZ*7.2f*side,r.Z+r.DZ*r.Length*t1+r.DX*7.2f*side);
                    starts[i]=new Vector3(a.X,Height(a)+.1f,a.Z);ends[i]=new Vector3(b.X,Height(b)+.1f,b.Z);
                    if(i%2==1){Vector3 swap=starts[i];starts[i]=ends[i];ends[i]=swap;}
                }
                humans=gameObject.AddComponent<NagoyaStationBlock.HumanCoexistence>();humans.RuntimeStatePath=Root+@"\simulation\humans-state.json";
                if(!humans.LoadPairs(starts,ends,maxTransitions)||!humans.SpawnNativeHumans())throw new InvalidOperationException("Native human initialization blocked; inspect humans-state.json");
                File.WriteAllText(Root+@"\simulation\human-controller.json",JsonData.Stringify(new Dictionary<string,object>{{"utc",DateTime.UtcNow.ToString("o")},{"mode",Mode},{"status","native spawn queued; actual movement unverified"},{"requested",12},{"maxTransitions",maxTransitions}}));
            }
            catch(Exception error){Debug.LogException(error);File.WriteAllText(Root+@"\simulation\human-controller.json",JsonData.Stringify(new Dictionary<string,object>{{"utc",DateTime.UtcNow.ToString("o")},{"mode",Mode},{"status","blocked"},{"error",error.Message}}));}
        }
        private Vector3 ReadRoutePoint(Dictionary<string,object> point)
        {
            float x=Convert.ToSingle(point["x"]),z=Convert.ToSingle(point["z"]);
            if(float.IsNaN(x)||float.IsInfinity(x)||float.IsNaN(z)||float.IsInfinity(z)||Math.Abs(x)>8600||Math.Abs(z)>8600)throw new FormatException("Explicit human route point outside native map");
            Point p=new Point(x,z);return new Vector3(x,Height(p)+.1f,z);
        }
        private float Height(Point p){return TerrainManager.instance.SampleRawHeightSmoothWithWater(new Vector3(p.X,0,p.Z),false,0);}
    }
}
