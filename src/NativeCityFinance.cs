using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeCityFinance : MonoBehaviour
    {
        private const string Root = NativeCityRoadBuilder.Root;
        private string city, runId;
        private DateTime lastRequest;
        private float nextRead;
        private int queued;
        private volatile bool stopped;

        public void Start()
        {
            if (!File.Exists(Path.Combine(Root, "enabled.txt"))) { enabled = false; return; }
            city = SimulationManager.instance.m_metaData.m_CityName;
            runId = Guid.NewGuid().ToString("N");
            Write("finance-runtime.json", Status(null, "ready", null, null));
        }

        public void Update()
        {
            if (Time.realtimeSinceStartup < nextRead || Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            nextRead = Time.realtimeSinceStartup + 1;
            bool scheduled = false;
            string commandId = null;
            try
            {
                string path = Path.Combine(Root, "finance-command.json");
                DateTime changed = File.GetLastWriteTimeUtc(path);
                if (changed == lastRequest) return;
                lastRequest = changed;
                if (!File.Exists(path)) return;
                if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Finance request is too large");
                var request = (Dictionary<string, object>)JsonData.Parse(File.ReadAllText(path));
                if ((string)request["runId"] != runId) return;
                commandId = (string)request["commandId"];
                if (!Regex.IsMatch(commandId, @"\A[a-f0-9]{32}\z") || (string)request["cityName"] != city)
                    throw new InvalidDataException("Finance request identity differs from the loaded city");
                uint build = Convert.ToUInt32(request["buildIndex"]);
                int segments = Convert.ToInt32(request["segments"]);
                SimulationManager.instance.AddAction(delegate { Collect(commandId, build, segments); });
                scheduled = true;
            }
            catch (Exception error) { Write("finance-runtime.json", Status(commandId, "rejected", null, error.Message)); }
            finally { if (!scheduled) Interlocked.Exchange(ref queued, 0); }
        }

        private void Collect(string id, uint build, int segments)
        {
            try
            {
                if (stopped) return;
                SimulationManager simulation = SimulationManager.instance;
                if (!simulation.SimulationPaused || simulation.m_metaData.m_CityName != city ||
                    simulation.m_currentBuildIndex != build || NetManager.instance.m_segmentCount != segments)
                    throw new InvalidOperationException("Finance sampling requires the checked paused city");
                EconomyManager economy = EconomyManager.instance;
                var services = new List<object>();
                foreach (ItemClass.Service service in Enum.GetValues(typeof(ItemClass.Service)))
                    if (service != ItemClass.Service.None) services.Add(Account(economy, service, ItemClass.SubService.None));
                var transport = new List<object>();
                foreach (ItemClass.SubService subService in Enum.GetValues(typeof(ItemClass.SubService)))
                    if (subService.ToString().StartsWith("PublicTransport", StringComparison.Ordinal))
                        transport.Add(Account(economy, ItemClass.Service.PublicTransport, subService));
                string file = id + "-finance.json";
                if (File.Exists(Path.Combine(Root, file))) throw new IOException("Finance receipt already exists");
                Write(file, new Dictionary<string, object> {
                    {"runId",runId}, {"commandId",id}, {"cityName",city}, {"buildIndex",build}, {"segments",segments},
                    {"gameTime",simulation.m_currentGameTime.ToString("s")}, {"internalCash",economy.InternalCashAmount},
                    {"lastCash",economy.LastCashAmount}, {"lastCashDelta",economy.LastCashDelta},
                    {"loanExpenses",economy.GetLoanExpenses()}, {"policyExpenses",economy.GetPolicyExpenses()},
                    {"services",services}, {"publicTransport",transport}, {"writesToCity",false}
                });
                Write("finance-runtime.json", Status(id, "complete", file, null));
            }
            catch (Exception error) { Write("finance-runtime.json", Status(id, "rejected", null, error.Message)); }
            finally { Interlocked.Exchange(ref queued, 0); }
        }

        private static Dictionary<string, object> Account(EconomyManager economy, ItemClass.Service service,
            ItemClass.SubService subService)
        {
            long income, expenses;
            economy.GetIncomeAndExpenses(service, subService, ItemClass.Level.None, out income, out expenses);
            return new Dictionary<string, object> { {"service",service.ToString()}, {"subService",subService.ToString()},
                {"income",income}, {"expenses",expenses} };
        }

        private Dictionary<string, object> Status(string id, string phase, string file, string error)
        {
            return new Dictionary<string, object> { {"runId",runId}, {"commandId",id}, {"cityName",city},
                {"phase",phase}, {"file",file}, {"error",error}, {"utc",DateTime.UtcNow.ToString("o")} };
        }

        private static void Write(string name, Dictionary<string, object> data)
        {
            string target = Path.Combine(Root, name);
            string pending = target + ".tmp";
            File.WriteAllText(pending, JsonData.Stringify(data), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(pending, target, null);
            else File.Move(pending, target);
        }

        public void OnDestroy() { stopped = true; }
    }
}
