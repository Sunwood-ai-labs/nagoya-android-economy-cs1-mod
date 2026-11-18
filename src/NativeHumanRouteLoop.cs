using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NagoyaStationBlock
{
    // Owned-only native route transition.  It never suppresses a failed or non-owned arrival.
    internal static class NativeHumanRouteLoop
    {
        private const string Owner = "sunwood.nagoya.native-human-loop";
        public const int DefaultMaxTransitions = 512;
        public const int MinMaxTransitions = 16;
        public const int MaxMaxTransitions = 4096;
        private const float EndpointGateM = 20f;
        private static readonly object Sync = new object();
        private static readonly Dictionary<ushort, RouteRecord> Owned = new Dictionary<ushort, RouteRecord>();
        private static Harmony harmony;
        private static MethodInfo pathMethod;
        private static bool installed;
        private static string error = "";
        public static string Error { get { lock (Sync) return error; } }

        public static bool Install()
        {
            lock (Sync)
            {
                if (installed) return true;
                error = "";
                Harmony candidate = null;
                try
                {
                    MethodInfo arrival = FindExact(typeof(HumanAI), "ArriveAtTarget", new Type[] { typeof(ushort), typeof(CitizenInstance).MakeByRefType() });
                    candidate = new Harmony(Owner);
                    candidate.Patch(arrival, new HarmonyMethod(typeof(NativeHumanRouteLoop).GetMethod("ArriveAtTargetPrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
                    harmony = candidate;
                    installed = true;
                    return true;
                }
                catch (Exception ex)
                {
                    try { if (candidate != null) candidate.UnpatchAll(Owner); } catch { }
                    harmony = null;
                    installed = false;
                    error = "route_loop_install_failed:" + ex.GetBaseException().Message;
                    return false;
                }
            }
        }

        public static void RegisterOwned(ushort instanceId, uint citizenId, int sourceId, ushort infoIndex, Vector3 start, Vector3 end, CitizenManager ownerManager)
        {
            RegisterOwned(instanceId, citizenId, sourceId, infoIndex, start, end, ownerManager, DefaultMaxTransitions);
        }

        public static void RegisterOwned(ushort instanceId, uint citizenId, int sourceId, ushort infoIndex, Vector3 start, Vector3 end, CitizenManager ownerManager, int maxTransitions)
        {
            if (!IsValidMaxTransitions(maxTransitions)) throw new ArgumentOutOfRangeException("maxTransitions", maxTransitions, "Expected 16..4096");
            lock (Sync)
            {
                RouteRecord record = new RouteRecord();
                record.InstanceId = instanceId;
                record.CitizenId = citizenId;
                record.SourceId = sourceId;
                record.InfoIndex = infoIndex;
                record.Start = start;
                record.End = end;
                record.GoingToEnd = true;
                record.OwnerManager = ownerManager;
                record.MaxTransitions = maxTransitions;
                record.Status = "registered";
                Owned[instanceId] = record;
            }
        }

        public static bool IsValidMaxTransitions(int maxTransitions)
        {
            return maxTransitions >= MinMaxTransitions && maxTransitions <= MaxMaxTransitions;
        }

        public static bool GetSnapshot(ushort instanceId, out int returnCount, out string status, out string reason)
        {
            int maxTransitions;
            return GetSnapshot(instanceId, out returnCount, out status, out reason, out maxTransitions);
        }

        public static bool GetSnapshot(ushort instanceId, out int returnCount, out string status, out string reason, out int maxTransitions)
        {
            lock (Sync)
            {
                RouteRecord record;
                if (!Owned.TryGetValue(instanceId, out record))
                {
                    returnCount = 0; status = "unregistered"; reason = ""; maxTransitions = DefaultMaxTransitions; return false;
                }
                returnCount = record.ReturnCount;
                status = record.Status;
                reason = record.Reason;
                maxTransitions = record.MaxTransitions;
                return true;
            }
        }

        public static bool TryGetOwnedResident(uint citizenId, ref Citizen passedCitizen, out ushort instanceId, out string reason)
        {
            lock (Sync)
            {
                instanceId = 0;
                reason = "unregistered";
                RouteRecord match = null;
                foreach (RouteRecord item in Owned.Values) if (item.CitizenId == citizenId) { match = item; break; }
                if (match == null) return false;
                instanceId = match.InstanceId;
                CitizenManager manager = CitizenManager.instance;
                if (manager == null || manager != match.OwnerManager) { reason = "owner_manager_mismatch"; return false; }
                if (passedCitizen.m_instance != match.InstanceId) { reason = "passed_parent_instance_mismatch"; return false; }
                if (citizenId >= manager.m_citizens.m_buffer.Length || match.CitizenId >= manager.m_citizens.m_buffer.Length) { reason = "citizen_buffer_range"; return false; }
                Citizen liveCitizen = manager.m_citizens.m_buffer[citizenId];
                if (liveCitizen.m_instance != match.InstanceId) { reason = "live_parent_instance_mismatch"; return false; }
                if (match.InstanceId >= manager.m_instances.m_buffer.Length) { reason = "instance_buffer_range"; return false; }
                CitizenInstance liveInstance = manager.m_instances.m_buffer[match.InstanceId];
                if (liveInstance.m_citizen != citizenId || (liveInstance.m_flags & CitizenInstance.Flags.Created) == 0 || liveInstance.m_infoIndex != match.InfoIndex) { reason = "live_instance_ownership_mismatch"; return false; }
                reason = "owned_cohort_transfer_exclusion";
                return true;
            }
        }

        public static void ClearOwned()
        {
            lock (Sync) Owned.Clear();
        }

        public static void Uninstall()
        {
            lock (Sync)
            {
                try { if (harmony != null) harmony.UnpatchAll(Owner); }
                catch (Exception ex) { error = "route_loop_uninstall_failed:" + ex.GetBaseException().Message; }
                harmony = null;
                installed = false;
            }
        }

        private static bool ArriveAtTargetPrefix(ushort __0, ref CitizenInstance __1, ref bool __result)
        {
            ushort instanceID = __0;
            CitizenInstance data = __1;
            RouteRecord record;
            CitizenManager manager;
            Vector3 nextTarget;
            Vector3 current;
            lock (Sync)
            {
                if (!Owned.TryGetValue(instanceID, out record)) return true;
                if (record.Status == "stopped" || record.Status == "budget_exhausted") return true;
                manager = CitizenManager.instance;
                if (manager == null || manager != record.OwnerManager)
                {
                    record.Status = "stopped"; record.Reason = "owner_manager_mismatch"; return true;
                }
                if (!Owns(manager, record, instanceID, data))
                {
                    record.Status = "stopped"; record.Reason = "ownership_mismatch"; return true;
                }
                if (record.ReturnCount >= record.MaxTransitions)
                {
                    record.Status = "budget_exhausted"; record.Reason = "max_transitions_" + record.MaxTransitions.ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
                }
                current = data.m_frame0.m_position;
                Vector3 expected = record.GoingToEnd ? record.End : record.Start;
                float measuredDistance = Vector3.Distance(current, expected);
                record.LastEndpointDistanceM = measuredDistance;
                if (measuredDistance > EndpointGateM)
                {
                    record.Status = "stopped"; record.Reason = "endpoint_gate_miss_" + measuredDistance.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "m"; return true;
                }
                if (record.TransitionInFlight)
                {
                    record.Status = "stopped"; record.Reason = "transition_already_in_flight"; return true;
                }
                record.TransitionInFlight = true;
                nextTarget = record.GoingToEnd ? record.Start : record.End;
            }

            bool accepted = false;
            string failure = "";
            Vector4 previousTargetPos = data.m_targetPos;
            try
            {
                CitizenAI ai = data.Info == null ? null : data.Info.GetAI() as CitizenAI;
                if (ai == null) throw new InvalidOperationException("citizen_ai_missing");
                // Native GetPathTargetPosition advances only after the current waypoint is reached.
                data.m_targetPos = new Vector4(current.x, current.y, current.z, 1f);
                object[] args = new object[] { instanceID, data, current, nextTarget, null, false, false };
                accepted = (bool)ResolvePathMethod().Invoke(ai, args);
                data = (CitizenInstance)args[1];
                manager.m_instances.m_buffer[instanceID] = data;
                if (!accepted) { data.m_targetPos = previousTargetPos; manager.m_instances.m_buffer[instanceID] = data; failure = "native_path_rejected"; }
            }
            catch (Exception ex) { data.m_targetPos = previousTargetPos; manager.m_instances.m_buffer[instanceID] = data; failure = ex.GetBaseException().Message; }

            int transitionCount;
            float endpointDistance;
            string transitionStatus;
            string transitionReason;
            lock (Sync)
            {
                if (!Owned.TryGetValue(instanceID, out record)) return true;
                record.TransitionInFlight = false;
                if (accepted)
                {
                    record.ReturnCount++;
                    record.GoingToEnd = !record.GoingToEnd;
                    record.Status = "native_next_path_started";
                    record.Reason = "accepted_simulation_thread";
                }
                else
                {
                    record.Status = "stopped";
                    record.Reason = failure.Length == 0 ? "native_path_rejected" : failure;
                }
                transitionCount = record.ReturnCount;
                endpointDistance = record.LastEndpointDistanceM;
                transitionStatus = record.Status;
                transitionReason = record.Reason;
            }
            NativeHumanTrace.RecordRouteTransition(instanceID, transitionCount, endpointDistance, transitionStatus, transitionReason);
            __1 = data;
            if (accepted) { __result = true; return false; }
            return true;
        }

        private static bool Owns(CitizenManager manager, RouteRecord record, ushort instanceID, CitizenInstance data)
        {
            if (data.m_citizen != record.CitizenId || (data.m_flags & CitizenInstance.Flags.Created) == 0 || data.m_infoIndex != record.InfoIndex) return false;
            if (record.CitizenId >= manager.m_citizens.m_buffer.Length) return false;
            return manager.m_citizens.m_buffer[record.CitizenId].m_instance == instanceID;
        }

        private static MethodInfo ResolvePathMethod()
        {
            if (pathMethod != null) return pathMethod;
            Type[] expected = new Type[] { typeof(ushort), typeof(CitizenInstance).MakeByRefType(), typeof(Vector3), typeof(Vector3), typeof(VehicleInfo), typeof(bool), typeof(bool) };
            foreach (MethodInfo method in typeof(CitizenAI).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "StartPathFind") continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != expected.Length) continue;
                bool same = true;
                for (int i = 0; i < parameters.Length; i++) if (parameters[i].ParameterType != expected[i]) same = false;
                if (same) { pathMethod = method; break; }
            }
            if (pathMethod == null) throw new MissingMethodException("CitizenAI.StartPathFind_exact7_not_found");
            return pathMethod;
        }

        private static MethodInfo FindExact(Type owner, string name, Type[] parameterTypes)
        {
            foreach (MethodInfo method in owner.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != name) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length) continue;
                bool same = true;
                for (int i = 0; i < parameters.Length; i++) if (parameters[i].ParameterType != parameterTypes[i]) same = false;
                if (same) return method;
            }
            throw new MissingMethodException(owner.FullName, name);
        }

        private sealed class RouteRecord
        {
            public ushort InstanceId, InfoIndex;
            public uint CitizenId;
            public int SourceId, ReturnCount;
            public Vector3 Start, End;
            public bool GoingToEnd, TransitionInFlight;
            public CitizenManager OwnerManager;
            public float LastEndpointDistanceM;
            public int MaxTransitions = DefaultMaxTransitions;
            public string Status = "", Reason = "";
        }
    }
}
