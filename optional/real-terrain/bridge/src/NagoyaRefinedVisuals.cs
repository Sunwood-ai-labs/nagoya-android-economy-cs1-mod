using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SkylinesAgentBridge
{
    internal static class NagoyaRefinedVisuals
    {
        private const string Root = @"H:\CS1-PLATEAU\nagoya-station\real-terrain-20261120";
        private static GameObject host;
        private static string result;

        internal static CommandResult Apply()
        {
            if (!NagoyaMapJob.CanEditNativeMap)
                return CommandResult.Fail("No verified Nagoya map is loaded.");
            if (host != null && result != null)
                return CommandResult.FromJson(result);
            bool fullCity = NagoyaMapJob.UseCitywideVisuals;
            bool station = NagoyaMapJob.UseStationPrecinct;
            string manifest = Path.Combine(Root, station ? "refined-visuals-citywide-station.tsv"
                : fullCity ? "refined-visuals-citywide.tsv" : "refined-visuals.tsv");
            if (!File.Exists(manifest))
                return CommandResult.Fail("The aligned PLATEAU manifest was not found.");
            int rows = File.ReadAllLines(manifest).Length - 1;
            if (rows < 1 || rows > (fullCity ? 2000 : 500))
                return CommandResult.Fail("The PLATEAU manifest has an invalid size.");

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("NagoyaStationBlock.Controller", false);
                if (type == null)
                    continue;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var load = type.GetMethod("LoadDistrict", flags, null,
                    new[] { typeof(string), typeof(float), typeof(float), typeof(int) }, null);
                var count = type.GetField("sourceBuildingCount", flags);
                var faces = type.GetField("loadedTriangles", flags);
                if (load == null || count == null || faces == null)
                    continue;
                host = new GameObject("Georeferenced PLATEAU buildings");
                try
                {
                    var controller = (Behaviour)host.AddComponent(type);
                    controller.enabled = false;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    load.Invoke(controller, new object[] { manifest, 0f, 0f, rows });
                    result = "{\"ok\":true,\"renderObjects\":" + rows + ",\"plateauSourceBuildings\":"
                        + count.GetValue(controller) + ",\"triangles\":" + faces.GetValue(controller)
                        + ",\"loadMilliseconds\":" + timer.ElapsedMilliseconds
                        + ",\"stationPrecinct\":" + JsonUtil.Bool(station) + "}";
                    Debug.Log("[NagoyaRefinedVisuals] " + result);
                    return CommandResult.FromJson(result);
                }
                catch (Exception error)
                {
                    Release();
                    return CommandResult.Fail(error.GetBaseException().Message);
                }
            }
            return CommandResult.Fail("The installed Nagoya Station scenery loader is unavailable or incompatible.");
        }

        internal static void Release()
        {
            if (host != null)
                UnityEngine.Object.Destroy(host);
            host = null;
            result = null;
        }
    }
}
