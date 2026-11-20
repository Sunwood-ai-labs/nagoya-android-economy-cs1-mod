using System;
using System.Threading;
using ColossalFramework;
using UnityEngine;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// Metro-only bridge for the simulation-thread-sensitive build path.
    /// This deliberately bypasses AgentBridge.Queue: the HTTP thread waits on
    /// the simulation action, while the Unity/main-thread queue never waits.
    /// </summary>
    public static class MetroNetworkCommands
    {
        private const int WaitTimeoutMs = 8000;
        private const string MetroTrackPrefab = "Metro Track";

        public static CommandResult BuildMetroNetwork(string body)
        {
            if (body == null)
            {
                return Failure("invalid_request", "Request body is required.");
            }

            if (!AgentBridge.Instance.LevelLoaded)
            {
                return Failure("not_loaded", "No city is loaded.");
            }

            string prefab = JsonUtil.GetString(body, "roadPrefab", "");
            if (!string.Equals(prefab, MetroTrackPrefab, StringComparison.Ordinal))
            {
                return Failure("invalid_prefab", "Only the native Metro Track prefab is allowed.");
            }

            float heightOffset = JsonUtil.GetNumber(body, "heightOffset", float.NaN);
            if (float.IsNaN(heightOffset) || heightOffset < -64f || heightOffset > -8f)
            {
                return Failure("invalid_height_offset", "heightOffset must be within [-64,-8].");
            }

            SimulationManager simulation = SimulationManager.instance;
            if (simulation == null)
            {
                return Failure("simulation_unavailable", "SimulationManager.instance is unavailable.");
            }

            ManualResetEvent completion = new ManualResetEvent(false);
            CommandResult result = null;

            try
            {
                // AddAction is thread-safe and transfers execution to the
                // simulation thread. Do not call WaitOne from this action.
                simulation.AddAction(delegate
                {
                    try
                    {
                        if (!AgentBridge.Instance.LevelLoaded)
                        {
                            result = Failure("not_loaded", "City unloaded before the simulation action ran.");
                        }
                        else
                        {
                            result = RoadCommands.BuildRoad(body);
                        }
                    }
                    catch (Exception ex)
                    {
                        string details = ex.ToString();
                        Debug.LogError("[SkylinesAgentBridge] build-metro-network exception: " + details);
                        result = Failure("exception", details);
                    }
                    finally
                    {
                        completion.Set();
                    }
                });
            }
            catch (Exception ex)
            {
                completion.Close();
                string details = ex.ToString();
                Debug.LogError("[SkylinesAgentBridge] build-metro-network enqueue exception: " + details);
                return Failure("exception", details);
            }

            if (!completion.WaitOne(WaitTimeoutMs, false))
            {
                // The action is intentionally not retried. It may still finish
                // later; the caller must treat this result as uncertain.
                return FailureJson("uncertain", "Simulation action did not finish within 8 seconds.",
                    ",\"timeout\":true,\"retryAllowed\":false");
            }

            completion.Close();
            return result ?? Failure("exception", "Simulation action completed without a result.");
        }

        private static CommandResult Failure(string status, string error)
        {
            return FailureJson(status, error, "");
        }

        private static CommandResult FailureJson(string status, string error, string suffix)
        {
            CommandResult result = new CommandResult();
            result.Ok = false;
            result.Error = error;
            result.Json = "{\"ok\":false,\"status\":\"" + JsonUtil.Escape(status) +
                "\",\"error\":\"" + JsonUtil.Escape(error) + "\"" + suffix + "}";
            return result;
        }
    }
}
