using System;
using System.IO;
using System.Collections.Generic;
using ICities;

namespace NagoyaRoadDelivery
{
    // Opt-in, exact-city sandbox. Matches the game's bundled Unlimited Money API.
    public sealed class SandboxEconomy : EconomyExtensionBase
    {
        private static readonly string Root =RuntimePaths.Data(@"simulation");
        private readonly object sync=new object();
        private long nextConfigTicks,nextStateTicks,updateCalls;
        private bool configured;
        private string targetCity="",error="";

        private bool Scoped(out string city)
        {
            lock(sync)
            {
                long now=DateTime.UtcNow.Ticks;
                if(now>=nextConfigTicks)
                {
                    nextConfigTicks=now+TimeSpan.TicksPerSecond*5;configured=false;error="";
                    try
                    {
                        string path=Path.Combine(Root,"sandbox-economy.ini");
                        if(File.Exists(path))
                        {
                            if(new FileInfo(path).Length>1024)throw new FormatException("Sandbox config size bound");
                            Dictionary<string,string> cfg=new Dictionary<string,string>();
                            foreach(string line in File.ReadAllLines(path)){string[] p=line.Split(new char[]{'='},2);if(p.Length==2)cfg[p[0].Trim()]=p[1].Trim();}
                            targetCity=cfg.ContainsKey("cityName")?cfg["cityName"]:"";
                            configured=cfg.ContainsKey("enabled")&&cfg["enabled"]=="true"&&targetCity.Length>0&&targetCity.Length<=80;
                        }
                    }
                    catch(Exception ex){error=ex.Message;configured=false;}
                }
                SimulationManager manager=SimulationManager.instance;
                city=manager==null||manager.m_metaData==null?"":manager.m_metaData.m_CityName;
                return configured&&string.Equals(city,targetCity,StringComparison.Ordinal);
            }
        }

        public override long OnUpdateMoneyAmount(long internalMoneyAmount)
        {
            string city;bool active=Scoped(out city);long returned=active?long.MaxValue:internalMoneyAmount;
            lock(sync)
            {
                updateCalls++;long now=DateTime.UtcNow.Ticks;
                if(now>=nextStateTicks)
                {
                    nextStateTicks=now+TimeSpan.TicksPerSecond*5;
                    try{File.WriteAllText(Path.Combine(Root,"sandbox-economy-state.json"),JsonData.Stringify(new Dictionary<string,object>{{"utc",DateTime.UtcNow.ToString("o")},{"status",active?"active_exact_city_sandbox":"inactive"},{"cityName",city},{"configuredCityName",targetCity},{"configuredEnabled",configured},{"nativePeekOverrideEnabled",active},{"updateCalls",updateCalls},{"lastInternalMoneyAmount",internalMoneyAmount.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"lastReturnedMoneyAmount",returned.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"assemblyMvid",GetType().Assembly.ManifestModule.ModuleVersionId.ToString()},{"error",error},{"implementation","Native IEconomy extension; opt-in experiment funds, not a verified fiscal economy"}}));}
                    catch(Exception ex){error=ex.Message;}
                }
            }
            return returned;
        }

        public override bool OverrideDefaultPeekResource {get{string city;return Scoped(out city);}}
        public override int OnPeekResource(EconomyResource resource,int amount){return amount;}
    }
}
