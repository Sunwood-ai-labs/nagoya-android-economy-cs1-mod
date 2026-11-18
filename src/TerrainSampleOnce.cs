using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using ColossalFramework;
using UnityEngine;
namespace NagoyaRoadDelivery
{
    internal static class TerrainSampleOnce
    {
        private static bool attempted;
        internal static void Run()
        {
            if(attempted)return;attempted=true;
            const string root=@"H:\CS1-PLATEAU\nagoya-station\publicrealm-v1";
            string request=Path.Combine(root,"terrain-sample-request.json"),result=Path.Combine(root,"terrain-samples.json");
            if(!File.Exists(request))return;
            Dictionary<string,object> output=new Dictionary<string,object>{{"utc",DateTime.UtcNow.ToString("o")},{"time",DateTime.UtcNow.ToString("o")},{"revision","publicrealm-terrain-start-once-v1"},{"source","CS1 TerrainManager.SampleRawHeightSmoothWithWater main thread Start once"}};
            try
            {
                for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new FormatException("Terrain request directory reparse");
                FileInfo fi=new FileInfo(request);if(fi.Length<1||fi.Length>262144||(fi.Attributes&FileAttributes.ReparsePoint)!=0)throw new FormatException("Terrain request boundary");byte[] bytes=File.ReadAllBytes(request);
                using(SHA256 hash=SHA256.Create())output["requestSha256"]=BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
                Dictionary<string,object> data=JsonData.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\ufeff')) as Dictionary<string,object>;if(data==null)throw new FormatException("Terrain request object required");
                List<object> rows=data["samples"] as List<object>;if(rows==null||rows.Count<1||rows.Count>1000)throw new FormatException("Terrain sample count");
                List<object> samples=new List<object>();HashSet<string> ids=new HashSet<string>();
                foreach(object row in rows)
                {
                    Dictionary<string,object> p=row as Dictionary<string,object>;if(p==null)throw new FormatException("Terrain point object");string id=p["id"] as string;
                    if(string.IsNullOrEmpty(id)||id.Length>100||!ids.Add(id)||!(p["x"] is double)||!(p["z"] is double))throw new FormatException("Terrain point id/coordinate");
                    double x=(double)p["x"],z=(double)p["z"];if(double.IsNaN(x)||double.IsInfinity(x)||double.IsNaN(z)||double.IsInfinity(z)||Math.Abs(x)>8500||Math.Abs(z)>8500)throw new FormatException("Terrain coordinate boundary");
                    float y=Singleton<TerrainManager>.instance.SampleRawHeightSmoothWithWater(new Vector3((float)x,0,(float)z),false,0);if(float.IsNaN(y)||float.IsInfinity(y))throw new FormatException("Nonfinite terrain sample");samples.Add(new Dictionary<string,object>{{"id",id},{"x",x},{"z",z},{"y",y}});
                }
                output["status"]="sampled";output["samples"]=samples;output["sampleCount"]=samples.Count;
            }
            catch(Exception ex){output["status"]="error";output["error"]=ex.GetBaseException().Message;Debug.LogException(ex);}
            try{string temp=result+".tmp";File.WriteAllText(temp,JsonData.Stringify(output));if(File.Exists(result))File.Replace(temp,result,null);else File.Move(temp,result);}catch(Exception ex){Debug.LogException(ex);}
        }
    }
}
