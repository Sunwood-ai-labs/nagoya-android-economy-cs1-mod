using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    // Initialization is restricted to a newly created city with an explicitly
    // configured construction name. Loading an existing save never flattens it.
    public sealed class NativeCityFlatStart : MonoBehaviour
    {
        private const string Root = NativeCityRoadBuilder.Root;
        internal bool NewGame;
        private string result;

        public void Start()
        {
            string request = Path.Combine(Root, "flat-start-city.txt");
            if (!NewGame || !File.Exists(request) || new FileInfo(request).Length > 150)
            {
                enabled = false;
                return;
            }
            string expected = File.ReadAllText(request).Trim();
            if (!expected.StartsWith("NagoyaCitywide-", StringComparison.Ordinal) ||
                expected != SimulationManager.instance.m_metaData.m_CityName)
            {
                enabled = false;
                return;
            }
            try
            {
                int samples, waterSources;
                PrepareTerrain(out samples, out waterSources);
                SimulationManager.instance.AddAction(delegate {
                    try { OpenTiles(expected, samples, waterSources); }
                    catch (Exception error)
                    {
                        Report(expected, "error", error.GetBaseException().Message, samples, waterSources, 0);
                        Debug.LogException(error);
                    }
                });
            }
            catch (Exception error)
            {
                Report(expected, "error", error.GetBaseException().Message, 0, 0, 0);
                Debug.LogException(error);
            }
        }

        public void Update()
        {
            string ready = Interlocked.Exchange<string>(ref result, null);
            if (ready == null) return;
            string file = Path.Combine(Root, "flat-start-runtime.json");
            string temp = file + ".tmp";
            File.WriteAllText(temp, ready, new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temp, file, null);
            else File.Move(temp, file);
            enabled = false;
        }

        private void PrepareTerrain(out int samples, out int waterSources)
        {
            TerrainManager terrain = TerrainManager.instance;
            ushort[] raw = terrain.RawHeights;
            if (raw == null || raw.Length != 1081 * 1081)
                throw new InvalidDataException("Unexpected native terrain span");
            byte[] former = new byte[raw.Length * 2];
            Buffer.BlockCopy(raw, 0, former, 0, former.Length);
            File.WriteAllBytes(Path.Combine(Root, "terrain-before-" + Guid.NewGuid().ToString("N") + ".bin"), former);

            const ushort encoded = 8960;
            byte[] flat = new byte[former.Length];
            for (int i = 0; i < flat.Length; i += 2)
            {
                flat[i] = (byte)(encoded & 255);
                flat[i + 1] = (byte)(encoded >> 8);
            }
            terrain.SetHeightMap16(flat);
            int uneven = 0;
            foreach (ushort height in terrain.RawHeights)
                if (height != encoded) uneven++;
            if (uneven != 0)
                throw new InvalidOperationException("The new terrain did not read back at 140m");

            samples = terrain.RawHeights.Length;
            waterSources = ClearWater(terrain.WaterSimulation);
        }

        private void OpenTiles(string city, int samples, int waterSources)
        {
            GameAreaManager areas = GameAreaManager.instance;
            areas.m_maxAreaCount = 25;
            for (int pass = 0; pass < 25 && areas.m_areaCount < 25; pass++)
            {
                bool progressed = false;
                for (int tile = 0; tile < 25; tile++)
                {
                    int x, z;
                    areas.GetTileXZ(tile, out x, out z);
                    if (!areas.IsUnlocked(x, z) && areas.UnlockArea(tile))
                        progressed = true;
                }
                if (!progressed) break;
            }
            Report(city, areas.m_areaCount == 25 ? "ready" : "incomplete", null,
                samples, waterSources, areas.m_areaCount);
        }

        private static int ClearWater(WaterSimulation water)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            FieldInfo sourceField = typeof(WaterSimulation).GetField("m_waterSources", flags);
            FieldInfo lockField = typeof(WaterSimulation).GetField("m_simulationBufferLock", flags);
            FieldInfo currentSea = typeof(WaterSimulation).GetField("m_currentSeaLevel", flags);
            FieldInfo nextSea = typeof(WaterSimulation).GetField("m_nextSeaLevel", flags);
            FieldInfo reset = typeof(WaterSimulation).GetField("m_resetWater", flags);
            MethodInfo release = typeof(WaterSimulation).GetMethod("ReleaseWaterSource", flags, null,
                new Type[] {typeof(ushort)}, null);
            if (sourceField == null || lockField == null || currentSea == null || nextSea == null || reset == null || release == null)
                throw new InvalidOperationException("Native water preparation is unavailable");
            object data = sourceField.GetValue(water);
            FieldInfo size = data.GetType().GetField("m_size", flags);
            int count = (int)size.GetValue(data);
            if (count < 0 || count > 65535)
                throw new InvalidDataException("Unexpected number of native water sources");
            for (int i = count; i > 0; i--)
                release.Invoke(water, new object[] {(ushort)i});
            object sync = lockField.GetValue(water);
            if (!Monitor.TryEnter(sync, 2000))
                throw new InvalidOperationException("Native water buffer remained busy");
            try
            {
                currentSea.SetValue(water, 0f);
                nextSea.SetValue(water, 0f);
                reset.SetValue(water, true);
            }
            finally { Monitor.Exit(sync); }
            return count;
        }

        private void Report(string city, string phase, string error, int samples, int waterSources, int tiles)
        {
            Interlocked.Exchange<string>(ref result, JsonData.Stringify(new Dictionary<string, object> {
                {"city",city}, {"phase",phase}, {"error",error}, {"flatHeightMeters",samples > 0 ? 140 : 0},
                {"nativeTerrainSamples",samples}, {"removedWaterSources",waterSources}, {"unlockedTiles",tiles},
                {"utc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)}
            }));
        }
    }
}
