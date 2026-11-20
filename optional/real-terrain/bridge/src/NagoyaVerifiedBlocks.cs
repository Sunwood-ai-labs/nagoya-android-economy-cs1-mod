using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SkylinesAgentBridge
{
    // A deliberately small bridge around the already-installed station loader.  The
    // loader owns mesh/material/texture destruction in Controller.OnDestroy -> Release;
    // this class owns only the per-block GameObject and never calls the base loader.
    internal static class NagoyaVerifiedBlocks
    {
        private const string Root = @"H:\CS1-PLATEAU\nagoya-station\real-terrain-20261120";
        private const string BlocksRoot = Root + @"\station-precinct\blocks";
        private const string ManifestHeader = "id\tassetDirectory\tassetName\texpectedTriangles\tx\tz\tdiffuseMap";
        private const int MinimumBlock = 1;
        private const int MaximumBlock = 12;
        private const int MaximumRows = 512;
        private const long MaximumManifestBytes = 1024L * 1024L;

        private sealed class BlockState
        {
            internal string Block;
            internal int Rows;
            internal int Triangles;
            internal GameObject Host;
        }

        private static readonly Dictionary<string, BlockState> Applied =
            new Dictionary<string, BlockState>(StringComparer.Ordinal);

        internal static CommandResult Apply(string requestedBlock)
        {
            if (!NagoyaMapJob.CanEditNativeMap)
                return CommandResult.Fail("No verified Nagoya terrain is loaded.");
            if (!NagoyaMapJob.UseStationPrecinct)
                return CommandResult.Fail("Verified station blocks require the StationPrecinct map session.");

            string block;
            if (!TryNormalizeBlock(requestedBlock, out block))
                return CommandResult.Fail("Use block=01 through block=12.");

            BlockState prior;
            if (Applied.TryGetValue(block, out prior))
            {
                // Unity objects compare equal to null after destruction.  Drop a stale
                // entry so a later request can safely rebuild after an external unload.
                if (prior.Host != null)
                    return Result(block, prior.Rows, prior.Triangles, true);
                Applied.Remove(block);
            }

            string manifest = Path.Combine(BlocksRoot, "block-" + block + ".tsv");
            int rows;
            string validationError = ValidateManifest(manifest, out rows);
            if (validationError != null)
                return CommandResult.Fail(validationError);

            Type controllerType;
            MethodInfo load;
            FieldInfo triangles;
            if (!FindLoader(out controllerType, out load, out triangles))
                return CommandResult.Fail("The installed Nagoya Station scenery loader is unavailable or incompatible.");

            GameObject host = null;
            try
            {
                host = new GameObject("Nagoya verified station block " + block);
                Behaviour controller = host.AddComponent(controllerType) as Behaviour;
                if (controller == null)
                    throw new InvalidOperationException("Nagoya Station block controller is not a Behaviour.");
                controller.enabled = false;

                load.Invoke(controller, new object[] { manifest, 0f, 0f, rows });
                int loadedTriangles = Convert.ToInt32(triangles.GetValue(controller), CultureInfo.InvariantCulture);
                if (loadedTriangles < 1)
                    throw new InvalidOperationException("The station block loader reported no triangles.");

                BlockState state = new BlockState();
                state.Block = block;
                state.Rows = rows;
                state.Triangles = loadedTriangles;
                state.Host = host;
                Applied.Add(block, state);
                return Result(block, rows, loadedTriangles, false);
            }
            catch (Exception error)
            {
                // LoadDistrict can allocate several assets before a later row fails.
                // Destroying the host is intentional: the installed controller's
                // OnDestroy invokes its private Release destructor for those assets.
                DestroyHost(host);
                return CommandResult.Fail(Unwrap(error).Message);
            }
        }

        internal static void ReleaseAll()
        {
            foreach (BlockState state in new List<BlockState>(Applied.Values))
                DestroyHost(state.Host);
            Applied.Clear();
        }

        internal static CommandResult Status()
        {
            var blocks = new List<string>();
            foreach (BlockState state in Applied.Values)
            {
                if (state.Host != null)
                    blocks.Add(Result(state.Block, state.Rows, state.Triangles, true).Json);
            }
            blocks.Sort(StringComparer.Ordinal);
            return CommandResult.FromJson("{\"ok\":true,\"blocks\":[" + string.Join(",", blocks.ToArray()) + "]}");
        }

        internal static void RestoreRecorded()
        {
            string path = Path.Combine(BlocksRoot, "verified-blocks.txt");
            if (!File.Exists(path))
                return;
            try
            {
                if (new FileInfo(path).Length > MaximumBlock * 6)
                    throw new InvalidDataException("Recorded station block list is too large.");
                string[] entries = File.ReadAllLines(path);
                if (entries.Length > MaximumBlock)
                    throw new InvalidDataException("Recorded station block list has too many entries.");
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in entries)
                {
                    string block;
                    if (!TryNormalizeBlock(line, out block) || !unique.Add(block))
                        throw new InvalidDataException("Recorded station block list has an invalid or duplicate entry.");
                }
                foreach (string block in entries)
                {
                    CommandResult restored = Apply(block);
                    if (!restored.Ok)
                        Debug.LogError("[NagoyaVerifiedBlocks] " + block + " " + restored.Error);
                }
            }
            catch (Exception error)
            {
                Debug.LogError("[NagoyaVerifiedBlocks] " + error.Message);
            }
        }

        private static CommandResult Result(string block, int rows, int triangles, bool alreadyApplied)
        {
            return CommandResult.FromJson("{\"ok\":true,\"block\":\"" + block
                + "\",\"renderObjects\":" + rows
                + ",\"triangles\":" + triangles
                + ",\"alreadyApplied\":" + JsonUtil.Bool(alreadyApplied) + "}");
        }

        private static bool TryNormalizeBlock(string value, out string block)
        {
            block = null;
            if (value == null || value.Length != 2 || value[0] < '0' || value[0] > '9'
                || value[1] < '0' || value[1] > '9')
                return false;
            int number = (value[0] - '0') * 10 + value[1] - '0';
            if (number < MinimumBlock || number > MaximumBlock)
                return false;
            block = value;
            return true;
        }

        private static string ValidateManifest(string path, out int rows)
        {
            rows = 0;
            try
            {
                string fullRoot = Path.GetFullPath(BlocksRoot).TrimEnd(Path.DirectorySeparatorChar);
                string fullPath = Path.GetFullPath(path);
                if (!string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase)
                    || !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "The station block manifest is outside the fixed whitelist directory.";
                if (!File.Exists(fullPath))
                    return "The requested station block manifest was not found.";
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    return "Linked station block manifests are not allowed.";
                FileInfo info = new FileInfo(fullPath);
                if (info.Length < 1 || info.Length > MaximumManifestBytes)
                    return "The station block manifest exceeds the size bound.";

                string[] lines = File.ReadAllLines(fullPath);
                if (lines.Length < 2 || lines.Length > MaximumRows + 1 || lines[0] != ManifestHeader)
                    return "The station block manifest must have the verified header and 1..512 rows.";
                rows = lines.Length - 1;
                return null;
            }
            catch (Exception error)
            {
                return "The station block manifest could not be read: " + error.GetBaseException().Message;
            }
        }

        private static bool FindLoader(out Type controllerType, out MethodInfo load, out FieldInfo triangles)
        {
            controllerType = null;
            load = null;
            triangles = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate = assembly.GetType("NagoyaStationBlock.Controller", false);
                if (candidate == null || !typeof(Component).IsAssignableFrom(candidate))
                    continue;
                MethodInfo candidateLoad = candidate.GetMethod("LoadDistrict", flags, null,
                    new[] { typeof(string), typeof(float), typeof(float), typeof(int) }, null);
                FieldInfo candidateTriangles = candidate.GetField("loadedTriangles", flags);
                if (candidateLoad == null || candidateTriangles == null)
                    continue;
                controllerType = candidate;
                load = candidateLoad;
                triangles = candidateTriangles;
                return true;
            }
            return false;
        }

        private static Exception Unwrap(Exception error)
        {
            TargetInvocationException invocation = error as TargetInvocationException;
            return invocation != null && invocation.InnerException != null
                ? invocation.InnerException.GetBaseException() : error.GetBaseException();
        }

        private static void DestroyHost(GameObject host)
        {
            if (host != null)
                UnityEngine.Object.Destroy(host);
        }
    }
}
