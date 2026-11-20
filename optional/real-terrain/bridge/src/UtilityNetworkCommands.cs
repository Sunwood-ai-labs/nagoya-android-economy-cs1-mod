using System;
using System.Threading;
using ColossalFramework;
using UnityEngine;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// Utility-only build wrapper. The HTTP thread waits; the simulation
    /// action never waits on AgentBridge.Queue and is never retried.
    /// </summary>
    public static class UtilityNetworkCommands
    {
        private const int WaitTimeoutMs = 8000;

        public static CommandResult BuildUtilityNetwork(string body)
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
            float heightOffset = JsonUtil.GetNumber(body, "heightOffset", float.NaN);
            bool validWater = string.Equals(prefab, "Water Pipe", StringComparison.Ordinal) &&
                !float.IsNaN(heightOffset) && heightOffset >= -16f && heightOffset <= -4f;
            bool validPower = string.Equals(prefab, "Power Line", StringComparison.Ordinal) &&
                !float.IsNaN(heightOffset) && Math.Abs(heightOffset) < 0.0001f;
            if (!validWater && !validPower)
            {
                return Failure("invalid_utility", "Only Water Pipe[-16,-4] or Power Line[0] is allowed.");
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
                        Debug.LogError("[SkylinesAgentBridge] build-utility-network exception: " + details);
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
                Debug.LogError("[SkylinesAgentBridge] build-utility-network enqueue exception: " + details);
                return Failure("exception", details);
            }

            if (!completion.WaitOne(WaitTimeoutMs, false))
            {
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
