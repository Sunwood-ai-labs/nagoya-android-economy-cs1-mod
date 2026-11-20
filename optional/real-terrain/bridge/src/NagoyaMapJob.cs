using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.Packaging;
using ICities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SkylinesAgentBridge
{
    public sealed class NagoyaMapJob : MonoBehaviour
    {
        private const string LaunchFlag = "-nagoya-real-terrain";
        private const int Resolution = 1081;
        private const int SampleCount = Resolution * Resolution;
        private const string Root = @"H:\CS1-PLATEAU\nagoya-station\real-terrain-20261120";
        private static bool booted;
        private static bool targetRequested;
        private static bool targetLoaded;
        private static string terrainHash;
        private static string requestedMap;
        private static bool unlimitedUtilitiesRequested;
        internal static bool UseCitywideVisuals { get; private set; }
        internal static bool UseStationPrecinct { get; private set; }
        private static readonly Dictionary<string, ushort> PlannedNodes = new Dictionary<string, ushort>();
        private float expiry;

        internal static void StartIfRequested()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (booted || Array.IndexOf(args, LaunchFlag) < 0)
                return;

            foreach (string arg in args)
            {
                if (arg.StartsWith("-nagoya-load=", StringComparison.Ordinal))
                    requestedMap = arg.Substring("-nagoya-load=".Length);
            }
            UseCitywideVisuals = Array.IndexOf(args, "-nagoya-full-buildings") >= 0;
            UseStationPrecinct = UseCitywideVisuals && requestedMap == "Nagoya-Real-Citywide-Neighborhoods-20261120"
                && Array.IndexOf(args, "-nagoya-station-precinct") >= 0;
            unlimitedUtilitiesRequested = Array.IndexOf(args, "-nagoya-unlimited-utilities") >= 0;
            booted = true;
            var host = new GameObject("Nagoya command-line map job");
            DontDestroyOnLoad(host);
            host.AddComponent<NagoyaMapJob>();
        }

        private void Awake()
        {
            expiry = Time.realtimeSinceStartup + 60f;
        }

        private void Update()
        {
            if (!HasLoadedMainMenu())
            {
                if (Time.realtimeSinceStartup > expiry)
                {
                    Debug.LogError("[NagoyaMapJob] Main menu did not load within 60 seconds. Active scene: "
                        + SceneManager.GetActiveScene().name);
                    Destroy(gameObject);
                }
                return;
            }

            Destroy(gameObject);
            var metadata = new SimulationMetaData();
            metadata.m_environment = "Sunny";
            metadata.m_invertTraffic = SimulationMetaData.MetaBool.True;
            metadata.m_gameInstanceIdentifier = Guid.NewGuid().ToString("N");
            metadata.m_updateMode = SimulationManager.UpdateMode.NewMap;
            Package.Asset data = null;
            if (!string.IsNullOrEmpty(requestedMap))
            {
                if (!IsMapName(requestedMap))
                {
                    Debug.LogError("[NagoyaMapJob] Invalid map name.");
                    return;
                }
                data = FindMapData(requestedMap);
                if (data == null)
                {
                    Debug.LogError("[NagoyaMapJob] Map package was not found: " + requestedMap);
                    return;
                }
                metadata.m_updateMode = SimulationManager.UpdateMode.LoadMap;
            }
            targetRequested = true;
            Debug.Log("[NagoyaMapJob] Loading local editor map: " + (requestedMap ?? "new map"));
            LoadingManager.instance.LoadLevel(data, "MapEditor", "InMapEditor", metadata, false);
        }

        private static bool HasLoadedMainMenu()
        {
            var sceneByName = SceneManager.GetSceneByName("MainMenu");
            if (sceneByName.IsValid() && sceneByName.isLoaded)
                return true;
            foreach (MainMenu menu in Resources.FindObjectsOfTypeAll(typeof(MainMenu)))
            {
                var scene = menu.gameObject.scene;
                if (scene.IsValid() && scene.isLoaded)
                    return true;
            }
            return false;
        }

        internal static void LevelLoaded(LoadMode mode)
        {
            targetLoaded = targetRequested && (mode == LoadMode.NewMap || mode == LoadMode.LoadMap);
            targetRequested = false;
            if (targetLoaded && mode == LoadMode.LoadMap)
            {
                VerifyReloadedTerrain();
                if (unlimitedUtilitiesRequested && CanEditNativeMap)
                {
                    var utilities = NagoyaUnlimitedUtilities.SetEnabled(true);
                    if (!utilities.Ok)
                        Debug.LogError("[NagoyaUnlimitedUtilities] " + utilities.Error);
                }
                if ((requestedMap == "Nagoya-Real-Citywide-Refined-20261120"
                    || requestedMap == "Nagoya-Real-Citywide-Ramps-20261120"
                    || requestedMap == "Nagoya-Real-Citywide-Streets-20261120"
                    || requestedMap == "Nagoya-Real-Citywide-Neighborhoods-20261120") && CanEditNativeMap)
                {
                    CommandResult loaded = NagoyaRefinedVisuals.Apply();
                    if (!loaded.Ok)
                        Debug.LogError("[NagoyaRefinedVisuals] " + loaded.Error);
                    else if (UseStationPrecinct)
                        NagoyaVerifiedBlocks.RestoreRecorded();
                }
            }
        }

        internal static void LevelUnloading()
        {
            NagoyaUnlimitedUtilities.Disable();
            NagoyaVerifiedBlocks.ReleaseAll();
            targetLoaded = false;
            terrainHash = null;
            PlannedNodes.Clear();
            NagoyaRefinedVisuals.Release();
        }

        private static bool IsTarget()
        {
            var simulation = SimulationManager.instance;
            return targetLoaded && simulation != null && simulation.m_metaData != null
                && (simulation.m_metaData.m_updateMode == SimulationManager.UpdateMode.NewMap
                    || simulation.m_metaData.m_updateMode == SimulationManager.UpdateMode.LoadMap);
        }

        internal static bool CanEditNativeMap
        {
            get { return IsTarget() && terrainHash != null; }
        }

        internal static CommandResult BuildBatch(string body)
        {
            return BatchCommands.Execute(body, PlannedNodes);
        }

        internal static CommandResult ImportTerrain(string body)
        {
            if (!IsTarget())
                return CommandResult.Fail("Launch the command-line Nagoya map job first.");
            if (NetManager.instance.m_segmentCount != 0 || BuildingManager.instance.m_buildingCount != 0)
                return CommandResult.Fail("Terrain import requires the empty editor map.");

            string expectedHash = JsonUtil.GetString(body, "sha256", "");
            string path = Path.Combine(Root, "height-north-up.bin");
            if (new FileInfo(path).Length != SampleCount * 2)
                return CommandResult.Fail("Unexpected height grid size.");
            byte[] input = File.ReadAllBytes(path);
            if (input.Length != SampleCount * 2 || Hash(input) != expectedHash)
                return CommandResult.Fail("Height grid content hash does not match.");

            var terrain = TerrainManager.instance;
            terrain.SetRawHeightMap(input);
            if (!ReadbackMatches(terrain, input))
                return CommandResult.Fail("Terrain readback differs from the north-up source.");
            terrainHash = expectedHash;
            return TerrainSample(0, 0);
        }

        private static void VerifyReloadedTerrain()
        {
            string path = Path.Combine(Root, "height-north-up.bin");
            if (!File.Exists(path) || new FileInfo(path).Length != SampleCount * 2)
                return;
            byte[] input = File.ReadAllBytes(path);
            if (!ReadbackMatches(TerrainManager.instance, input))
            {
                Debug.LogError("[NagoyaMapJob] Reloaded raw elevations differ from the original DEM.");
                return;
            }
            terrainHash = Hash(input);
        }

        private static bool ReadbackMatches(TerrainManager terrain, byte[] input)
        {
            ushort[] output = terrain.RawHeights;
            if (output == null || output.Length != SampleCount)
                return false;

            byte[] actual = new byte[input.Length];
            Buffer.BlockCopy(output, 0, actual, 0, actual.Length);
            for (int row = 0; row < Resolution; row++)
            {
                int inputRow = (Resolution - 1 - row) * Resolution * 2;
                int outputRow = row * Resolution * 2;
                for (int col = 0; col < Resolution * 2; col++)
                {
                    if (input[inputRow + col] != actual[outputRow + col])
                        return false;
                }
            }
            File.WriteAllBytes(Path.Combine(Root, "native-heights-south-up.bin"), actual);
            return true;
        }

        internal static CommandResult TerrainSample(float x, float z)
        {
            if (!IsTarget() || terrainHash == null)
                return CommandResult.Fail("No verified Nagoya terrain in this map session.");
            if (!Coordinate(x) || !Coordinate(z))
                return CommandResult.Fail("Sample is outside the map.");
            float ground = TerrainManager.instance.SampleRawHeightSmooth(new Vector3(x, 0, z));
            return CommandResult.FromJson("{\"ok\":true,\"mode\":\"" + SimulationManager.instance.m_metaData.m_updateMode
                + "\",\"nativeHeightCount\":" + SampleCount
                + ",\"sourceSha256\":\"" + terrainHash + "\",\"x\":" + JsonUtil.Number(x)
                + ",\"z\":" + JsonUtil.Number(z) + ",\"ground\":" + JsonUtil.Number(ground) + "}");
        }

        internal static CommandResult PlaceProp(string body)
        {
            if (!IsTarget() || terrainHash == null)
                return CommandResult.Fail("No verified Nagoya terrain in this map session.");
            var point = new Vector3(JsonUtil.GetNumber(body, "x", float.NaN), 0,
                JsonUtil.GetNumber(body, "z", float.NaN));
            if (!Coordinate(point.x) || !Coordinate(point.z))
                return CommandResult.Fail("Prop is outside the map.");
            string name = JsonUtil.GetString(body, "prefab", "");
            PropInfo prefab = PrefabCollection<PropInfo>.FindLoaded(name);
            if (prefab == null)
                return CommandResult.Fail("Prop prefab unavailable: " + name);
            point.y = TerrainManager.instance.SampleRawHeightSmooth(point);
            if (JsonUtil.GetBool(body, "dryRun", false))
                return CommandResult.FromJson("{\"ok\":true,\"dryRun\":true}");
            float angle = JsonUtil.GetNumber(body, "angleDegrees", 0) * Mathf.Deg2Rad;
            Randomizer randomizer = SimulationManager.instance.m_randomizer;
            ushort id;
            if (!PropManager.instance.CreateProp(out id, ref randomizer, prefab, point, angle, true))
                return CommandResult.Fail("Native prop placement failed.");
            SimulationManager.instance.m_randomizer = randomizer;
            return ReadProp(id);
        }

        internal static CommandResult PlaceTree(string body)
        {
            if (!CanEditNativeMap)
                return CommandResult.Fail("No verified Nagoya terrain in this map session.");
            var point = new Vector3(JsonUtil.GetNumber(body, "x", float.NaN), 0,
                JsonUtil.GetNumber(body, "z", float.NaN));
            if (!Coordinate(point.x) || !Coordinate(point.z))
                return CommandResult.Fail("Tree is outside the map.");
            string name = JsonUtil.GetString(body, "prefab", "Tree2Variant");
            TreeInfo prefab = PrefabCollection<TreeInfo>.FindLoaded(name);
            if (prefab == null)
            {
                int count = PrefabCollection<TreeInfo>.LoadedCount();
                for (uint i = 0; i < count; ++i)
                {
                    var candidate = PrefabCollection<TreeInfo>.GetLoaded(i);
                    if (candidate != null && candidate.name == "Tree2")
                    {
                        prefab = candidate;
                        break;
                    }
                }
            }
            if (prefab == null)
                return CommandResult.Fail("A supported native tree prefab was not found.");
            point.y = TerrainManager.instance.SampleRawHeightSmooth(point);
            Randomizer randomizer = SimulationManager.instance.m_randomizer;
            uint id;
            if (!TreeManager.instance.CreateTree(out id, ref randomizer, prefab, point, false))
                return CommandResult.Fail("Native tree placement failed.");
            SimulationManager.instance.m_randomizer = randomizer;
            return CommandResult.FromJson("{\"ok\":true,\"id\":" + id + ",\"prefab\":\""
                + JsonUtil.Escape(prefab.name) + "\",\"x\":" + JsonUtil.Number(point.x)
                + ",\"z\":" + JsonUtil.Number(point.z) + "}");
        }

        internal static CommandResult ReadProp(int id)
        {
            if (!CanEditNativeMap || id <= 0 || id >= PropManager.instance.m_props.m_buffer.Length)
                return CommandResult.Fail("Invalid native prop id for this map.");
            PropInstance prop = PropManager.instance.m_props.m_buffer[id];
            if ((prop.m_flags & (ushort)PropInstance.Flags.Created) == 0 || prop.Info == null)
                return CommandResult.Fail("No created native prop with this id.");
            Vector3 placed = prop.Position;
            return CommandResult.FromJson("{\"ok\":true,\"id\":" + id + ",\"prefab\":\""
                + JsonUtil.Escape(prop.Info.name) + "\",\"x\":" + JsonUtil.Number(placed.x)
                + ",\"y\":" + JsonUtil.Number(placed.y) + ",\"z\":" + JsonUtil.Number(placed.z) + "}");
        }

        internal static CommandResult PropPrefabs(string query)
        {
            if (!IsTarget())
                return CommandResult.Fail("No dedicated Nagoya map job is loaded.");
            var names = new List<string>();
            int count = PrefabCollection<PropInfo>.LoadedCount();
            for (uint i = 0; i < count && names.Count < 50; i++)
            {
                PropInfo prefab = PrefabCollection<PropInfo>.GetLoaded(i);
                if (prefab != null && prefab.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    names.Add("\"" + JsonUtil.Escape(prefab.name) + "\"");
            }
            return CommandResult.FromJson("{\"ok\":true,\"names\":[" + string.Join(",", names.ToArray()) + "]}");
        }

        internal static CommandResult Capture(string body)
        {
            if (!IsTarget() || terrainHash == null)
                return CommandResult.Fail("No verified Nagoya terrain in this map session.");
            string name = JsonUtil.GetString(body, "name", "");
            float x = JsonUtil.GetNumber(body, "x", float.NaN);
            float z = JsonUtil.GetNumber(body, "z", float.NaN);
            float zoom = JsonUtil.GetNumber(body, "zoom", 800f);
            float pitch = JsonUtil.GetNumber(body, "pitch", 52f);
            if (!name.StartsWith("nagoya-", StringComparison.Ordinal) || !name.EndsWith(".png", StringComparison.Ordinal)
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Coordinate(x) || !Coordinate(z)
                || zoom < 45 || zoom > 18000 || pitch < 15 || pitch > 89)
                return CommandResult.Fail("Invalid screenshot coordinates or filename.");
            var camera = ToolsModifierControl.cameraController;
            if (camera == null)
                return CommandResult.Fail("The native map camera is unavailable.");
            var focus = new Vector3(x, 0, z);
            focus.y = TerrainManager.instance.SampleRawHeightSmooth(focus);
            camera.ClearTarget();
            camera.SetOverrideModeOff();
            camera.DisableFocusEffects();
            camera.m_targetPosition = camera.m_currentPosition = focus;
            camera.m_targetSize = camera.m_currentSize = zoom;
            camera.m_targetHeight = camera.m_currentHeight = 60f;
            var angle = new Vector2(JsonUtil.GetNumber(body, "yaw", 35f), pitch);
            camera.m_targetAngle = camera.m_currentAngle = angle;
            string screenshot = Path.Combine(Root, name);
            camera.StartCoroutine(CaptureAfterCamera(screenshot));
            return CommandResult.FromJson("{\"ok\":true,\"path\":\"" + JsonUtil.Escape(screenshot) + "\"}");
        }

        private static IEnumerator CaptureAfterCamera(string path)
        {
            yield return null;
            yield return null;
            Application.CaptureScreenshot(path, 1);
        }

        internal static CommandResult Save(string body)
        {
            if (!IsTarget() || terrainHash == null)
                return CommandResult.Fail("No verified Nagoya terrain in this map session.");
            string name = JsonUtil.GetString(body, "name", "");
            if (!IsMapName(name))
                return CommandResult.Fail("Use a unique Nagoya-Real- map name.");
            string maps = Path.Combine(ColossalFramework.IO.DataLocation.localApplicationData, "Maps");
            string path = Path.Combine(maps, name + ".crp");
            if (File.Exists(path))
                return CommandResult.Fail("The map already exists.");

            SimulationMetaData metadata = SimulationManager.instance.m_metaData;
            if (string.IsNullOrEmpty(metadata.m_gameInstanceIdentifier))
                metadata.m_gameInstanceIdentifier = Guid.NewGuid().ToString("N");
            var panels = Resources.FindObjectsOfTypeAll(typeof(SaveMapPanel));
            foreach (var candidate in panels)
            {
                var panel = candidate as SaveMapPanel;
                if (panel == null || !panel.gameObject.activeInHierarchy)
                    continue;
                return SaveWithPanel(panel, name, path);
            }

            var modal = ColossalFramework.UI.UIView.library.ShowModal<SaveMapPanel>("SaveMapPanel");
            if (modal == null)
                return CommandResult.Fail("Native map save panel is unavailable.");
            return SaveWithPanel(modal, name, path);
        }

        private static CommandResult SaveWithPanel(SaveMapPanel panel, string name, string path)
        {
            var method = typeof(SaveMapPanel).GetMethod("SaveRoutine", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(string), typeof(bool) }, null);
            if (method == null)
                return CommandResult.Fail("Native SaveMapPanel.SaveRoutine signature changed.");
            method.Invoke(panel, new object[] { name, name, false });
            return CommandResult.FromJson("{\"ok\":true,\"path\":\"" + JsonUtil.Escape(path) + "\"}");
        }

        private static bool Coordinate(float value)
        {
            return !float.IsNaN(value) && value >= -8640 && value <= 8640;
        }

        private static bool IsMapName(string name)
        {
            return name.StartsWith("Nagoya-Real-", StringComparison.Ordinal) && name.Length <= 60
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static Package.Asset FindMapData(string name)
        {
            string maps = Path.Combine(ColossalFramework.IO.DataLocation.localApplicationData, "Maps");
            string path = Path.GetFullPath(Path.Combine(maps, name + ".crp"));
            foreach (Package package in PackageManager.allPackages)
            {
                if (string.IsNullOrEmpty(package.packagePath)
                    || !string.Equals(Path.GetFullPath(package.packagePath), path, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (Package.Asset asset in package)
                {
                    if (asset.name == name + "_Data")
                        return asset;
                }
            }
            return null;
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
