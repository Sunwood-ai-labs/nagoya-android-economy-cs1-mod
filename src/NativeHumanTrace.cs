using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace NagoyaStationBlock
{
    // Observation-only Harmony callbacks for humans owned by HumanCoexistence.
    // This class never changes the original arguments or return values.
    internal static class NativeHumanTrace
    {
        private const string Owner = "sunwood.nagoya.native-human-trace";
        private static readonly object Sync = new object();
        private static readonly Dictionary<ushort, TraceRecord> Owned = new Dictionary<ushort, TraceRecord>();
        private static Harmony harmony;
        private static string tracePath = "";
        private static bool installed;
        private static string error = "";
        public static string Error { get { return error; } private set { error = value ?? ""; } }

        public static bool Install(string statePath)
        {
            lock (Sync)
            {
                if (installed) return true;
                tracePath = (statePath ?? "") + ".callback-trace.json";
                Error = "";
                Harmony candidate = null;
                try
                {
                    Type byRefInstance = typeof(CitizenInstance).MakeByRefType();
                    MethodInfo failure = FindExact(typeof(HumanAI), "PathfindFailure", new Type[] { typeof(ushort), byRefInstance });
                    MethodInfo arrive = FindExact(typeof(HumanAI), "ArriveAtDestination", new Type[] { typeof(ushort), byRefInstance, typeof(bool) });
                    MethodInfo release = FindExact(typeof(CitizenManager), "ReleaseCitizenInstance", new Type[] { typeof(ushort) });
                    candidate = new Harmony(Owner);
                    candidate.Patch(failure, new HarmonyMethod(typeof(NativeHumanTrace).GetMethod("PathfindFailurePrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
                    candidate.Patch(arrive, new HarmonyMethod(typeof(NativeHumanTrace).GetMethod("ArriveAtDestinationPrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
                    candidate.Patch(release, new HarmonyMethod(typeof(NativeHumanTrace).GetMethod("ReleaseCitizenInstancePrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
                    if (!NativeHumanRouteLoop.Install()) throw new InvalidOperationException(NativeHumanRouteLoop.Error);
                    if (!NativeHumanResidentIsolation.Install()) throw new InvalidOperationException(NativeHumanResidentIsolation.Error);
                    harmony = candidate;
                    installed = true;
                    WriteStateLocked();
                    return true;
                }
                catch (Exception error)
                {
                    try { if (candidate != null) candidate.UnpatchAll(Owner); } catch { }
                    NativeHumanRouteLoop.Uninstall();
                    NativeHumanResidentIsolation.Uninstall();
                    harmony = null;
                    installed = false;
                    Error = "trace_install_failed:" + error.GetBaseException().Message;
                    WriteStateLocked();
                    return false;
                }
            }
        }

        public static void RegisterOwned(ushort instanceId, uint citizenId, int sourceId, string infoName)
        {
            lock (Sync)
            {
                TraceRecord record = new TraceRecord();
                record.InstanceId = instanceId;
                record.CitizenId = citizenId;
                record.SourceId = sourceId;
                record.InfoName = infoName ?? "";
                Owned[instanceId] = record;
                WriteStateLocked();
            }
        }

        public static void ClearOwned()
        {
            lock (Sync)
            {
                Owned.Clear();
                WriteStateLocked();
            }
        }

        public static void Uninstall()
        {
            lock (Sync)
            {
                try { if (harmony != null) harmony.UnpatchAll(Owner); }
                catch (Exception error) { Error = "trace_uninstall_failed:" + error.GetBaseException().Message; }
                harmony = null;
                installed = false;
                WriteStateLocked();
            }
        }

        private static void PathfindFailurePrefix(object[] __args)
        {
            Observe("HumanAI.PathfindFailure", ReadInstanceId(__args), false, false);
        }

        private static void ArriveAtDestinationPrefix(object[] __args)
        {
            Observe("HumanAI.ArriveAtDestination", ReadInstanceId(__args), ReadBool(__args, 2), true);
        }

        private static void ReleaseCitizenInstancePrefix(object[] __args)
        {
            Observe("CitizenManager.ReleaseCitizenInstance", ReadUInt16(__args, 0), false, false);
        }

        private static ushort ReadInstanceId(object[] args)
        {
            return ReadUInt16(args, 0);
        }

        private static ushort ReadUInt16(object[] args, int index)
        {
            if (args == null || index < 0 || index >= args.Length || args[index] == null) return 0;
            try { return Convert.ToUInt16(args[index]); } catch { return 0; }
        }

        private static bool ReadBool(object[] args, int index)
        {
            if (args == null || index < 0 || index >= args.Length || args[index] == null) return false;
            try { return Convert.ToBoolean(args[index]); } catch { return false; }
        }

        private static void Observe(string callback, ushort instanceId, bool success, bool hasSuccess)
        {
            if (instanceId == 0) return;
            lock (Sync)
            {
                TraceRecord record;
                if (!Owned.TryGetValue(instanceId, out record)) return;
                CitizenManager manager = CitizenManager.instance;
                if (manager == null || instanceId >= manager.m_instances.m_buffer.Length || record.CitizenId >= manager.m_citizens.m_buffer.Length) return;
                CitizenInstance live = manager.m_instances.m_buffer[instanceId];
                if (live.m_citizen != record.CitizenId || (live.m_flags & CitizenInstance.Flags.Created) == 0 || manager.m_citizens.m_buffer[record.CitizenId].m_instance != instanceId) return;
                record.LastCallback = callback;
                if (callback == "HumanAI.PathfindFailure") record.PathFailureCount++;
                if (callback == "HumanAI.ArriveAtDestination") { if (!record.HasFirstArrival) { record.FirstArrivalSuccess = success; record.HasFirstArrival = true; } record.ArriveCount++; record.LastArrivalSuccess = success; }
                if (callback == "CitizenManager.ReleaseCitizenInstance") record.ReleaseCount++;
                if (record.FirstCallbackStack.Length == 0) record.FirstCallbackStack = Environment.StackTrace;
                ReadLiveFields(record, instanceId);
                AppendEvent(record, callback, hasSuccess, success);
                WriteStateLocked();
                WriteOrderedStateLocked();
            }
        }

        public static void RecordRouteTransition(ushort instanceId, int returnCount, float endpointDistanceM, string status, string reason)
        {
            lock (Sync)
            {
                TraceRecord record;
                if (!Owned.TryGetValue(instanceId, out record)) return;
                CitizenManager manager = CitizenManager.instance;
                if (manager == null || instanceId >= manager.m_instances.m_buffer.Length || record.CitizenId >= manager.m_citizens.m_buffer.Length) return;
                CitizenInstance live = manager.m_instances.m_buffer[instanceId];
                if (live.m_citizen != record.CitizenId || (live.m_flags & CitizenInstance.Flags.Created) == 0 || manager.m_citizens.m_buffer[record.CitizenId].m_instance != instanceId) return;
                ReadLiveFields(record, instanceId);
                string eventName = "NativeHumanRouteLoop." + (status ?? "unknown");
                record.LastEvent = eventName;
                record.LastLoopReturnCount = returnCount;
                record.LastLoopEndpointDistanceM = endpointDistanceM;
                record.LastLoopStatus = status;
                record.LastLoopReason = reason;
                AppendEvent(record, eventName, false, false);
                WriteStateLocked();
                WriteOrderedStateLocked();
            }
        }

        public static void RecordResidentTransferSkipped(ushort instanceId, uint citizenId, TransferManager.TransferReason reason, TransferManager.TransferOffer offer, string ownershipReason)
        {
            lock (Sync)
            {
                TraceRecord record;
                if (!Owned.TryGetValue(instanceId, out record) || record.CitizenId != citizenId) return;
                CitizenManager manager = CitizenManager.instance;
                if (manager == null || instanceId >= manager.m_instances.m_buffer.Length || citizenId >= manager.m_citizens.m_buffer.Length) return;
                CitizenInstance live = manager.m_instances.m_buffer[instanceId];
                if (live.m_citizen != citizenId || (live.m_flags & CitizenInstance.Flags.Created) == 0 || manager.m_citizens.m_buffer[citizenId].m_instance != instanceId) return;
                ReadLiveFields(record, instanceId);
                record.TransferSkipCount++;
                record.LastTransferReason = reason.ToString();
                record.LastTransferReasonCode = (int)reason;
                record.LastOfferFlags = offer.m_flags;
                record.LastOfferObjectHash = offer.m_object.GetHashCode();
                record.LastOfferLocalPark = offer.m_isLocalPark;
                record.LastTransferReasonText = ownershipReason ?? "";
                AppendEvent(record, "ResidentAI.StartTransfer.skipped", false, false);
                WriteStateLocked();
                WriteOrderedStateLocked();
                WriteResidentTransferStateLocked();
            }
        }

        private static void AppendEvent(TraceRecord record, string callback, bool hasSuccess, bool success)
        {
            record.EventSequence++;
            CallbackEvent item = new CallbackEvent();
            item.Sequence = record.EventSequence;
            item.Callback = callback;
            item.HasSuccess = hasSuccess;
            item.Success = success;
            item.PathId = record.PathId;
            item.PathUnitFlags = record.PathUnitFlags;
            item.PathFindFlags = record.PathFindFlags;
            item.InstanceFlags = record.InstanceFlags;
            item.SourceBuilding = record.SourceBuilding;
            item.TargetBuilding = record.TargetBuilding;
            item.ParentInstanceId = record.ParentInstanceId;
            item.Position = record.Position;
            item.TransferReason = record.LastTransferReason;
            item.TransferReasonCode = record.LastTransferReasonCode;
            item.OfferFlags = record.LastOfferFlags;
            item.OfferObjectHash = record.LastOfferObjectHash;
            item.OfferLocalPark = record.LastOfferLocalPark;
            if (record.Events.Count >= 64) record.Events.RemoveAt(0);
            record.Events.Add(item);
        }

        private static void ReadLiveFields(TraceRecord record, ushort instanceId)
        {
            try
            {
                CitizenManager cm = CitizenManager.instance;
                if (cm == null || instanceId >= cm.m_instances.m_buffer.Length) return;
                CitizenInstance data = cm.m_instances.m_buffer[instanceId];
                record.PathId = data.m_path;
                record.InstanceFlags = ((uint)data.m_flags).ToString();
                record.SourceBuilding = data.m_sourceBuilding;
                record.TargetBuilding = data.m_targetBuilding;
                record.Position = data.m_frame0.m_position;
                if (data.m_path != 0 && PathManager.instance != null && data.m_path < PathManager.instance.m_pathUnits.m_buffer.Length)
                {
                    PathUnit path = PathManager.instance.m_pathUnits.m_buffer[data.m_path];
                    record.PathUnitFlags = ((uint)path.m_simulationFlags).ToString();
                    record.PathFindFlags = path.m_pathFindFlags.ToString();
                }
                if (record.CitizenId < cm.m_citizens.m_buffer.Length)
                {
                    Citizen citizen = cm.m_citizens.m_buffer[record.CitizenId];
                    record.ParentInstanceId = citizen.m_instance;
                    record.CurrentLocation = ((int)citizen.CurrentLocation).ToString();
                }
            }
            catch (Exception error) { record.LastReadError = error.GetBaseException().Message; }
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

        private static void WriteStateLocked()
        {
            if (String.IsNullOrEmpty(tracePath)) return;
            try
            {
                string directory = Path.GetDirectoryName(tracePath);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"utc\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",\"owner\":\"").Append(Owner).Append("\",\"harmonyInstalled\":").Append(installed ? "true" : "false").Append(",\"lastError\":\"").Append(Esc(Error)).Append("\",\"records\":[");
                bool first = true;
                foreach (TraceRecord record in Owned.Values)
                {
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"instanceId\":").Append(record.InstanceId).Append(",\"citizenId\":").Append(record.CitizenId).Append(",\"sourceId\":").Append(record.SourceId).Append(",\"nativeInfoName\":\"").Append(Esc(record.InfoName)).Append("\",\"lastCallback\":\"").Append(Esc(record.LastCallback)).Append("\",\"pathFailureCount\":").Append(record.PathFailureCount).Append(",\"arriveCount\":").Append(record.ArriveCount).Append(",\"releaseCount\":").Append(record.ReleaseCount).Append(",\"lastArrivalSuccess\":").Append(record.LastArrivalSuccess ? "true" : "false").Append(",\"pathId\":").Append(record.PathId).Append(",\"pathUnitFlags\":\"").Append(Esc(record.PathUnitFlags)).Append("\",\"pathFindFlags\":\"").Append(Esc(record.PathFindFlags)).Append("\",\"instanceFlags\":\"").Append(Esc(record.InstanceFlags)).Append("\",\"sourceBuilding\":").Append(record.SourceBuilding).Append(",\"targetBuilding\":").Append(record.TargetBuilding).Append(",\"parentInstanceId\":").Append(record.ParentInstanceId).Append(",\"currentLocation\":\"").Append(Esc(record.CurrentLocation)).Append("\",\"position\":{\"x\":").Append(F(record.Position.x)).Append(",\"y\":").Append(F(record.Position.y)).Append(",\"z\":").Append(F(record.Position.z)).Append("},\"lastReadError\":\"").Append(Esc(record.LastReadError)).Append("\",\"firstCallbackStack\":\"").Append(Esc(record.FirstCallbackStack)).Append("\"}");
                }
                sb.Append("]}");
                string tmp = tracePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(tracePath)) File.Replace(tmp, tracePath, null); else File.Move(tmp, tracePath);
            }
            catch (Exception error) { Error = "trace_state_write_failed:" + error.GetBaseException().Message; }
        }

        private static void WriteResidentTransferStateLocked()
        {
            if (String.IsNullOrEmpty(tracePath)) return;
            try
            {
                string residentPath = tracePath + ".resident-isolation.json";
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"utc\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",\"owner\":\"sunwood.nagoya.native-human-resident-isolation\",\"records\":[");
                bool first = true;
                foreach (TraceRecord record in Owned.Values)
                {
                    if (record.TransferSkipCount == 0) continue;
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"instanceId\":").Append(record.InstanceId).Append(",\"citizenId\":").Append(record.CitizenId).Append(",\"sourceId\":").Append(record.SourceId).Append(",\"skipCount\":").Append(record.TransferSkipCount).Append(",\"lastTransferReason\":\"").Append(Esc(record.LastTransferReason)).Append("\",\"lastTransferReasonCode\":").Append(record.LastTransferReasonCode).Append(",\"lastOfferFlags\":").Append(record.LastOfferFlags).Append(",\"lastOfferObjectHash\":").Append(record.LastOfferObjectHash).Append(",\"lastOfferLocalPark\":").Append(record.LastOfferLocalPark).Append(",\"ownershipReason\":\"").Append(Esc(record.LastTransferReasonText)).Append("\",\"events\":[");
                    bool eventFirst = true;
                    foreach (CallbackEvent item in record.Events)
                    {
                        if (item.Callback != "ResidentAI.StartTransfer.skipped") continue;
                        if (!eventFirst) sb.Append(','); eventFirst = false;
                        sb.Append("{\"sequence\":").Append(item.Sequence).Append(",\"callback\":\"ResidentAI.StartTransfer.skipped\",\"transferReason\":\"").Append(Esc(item.TransferReason)).Append("\",\"transferReasonCode\":").Append(item.TransferReasonCode).Append(",\"offerFlags\":").Append(item.OfferFlags).Append(",\"offerObjectHash\":").Append(item.OfferObjectHash).Append(",\"offerLocalPark\":").Append(item.OfferLocalPark).Append("}");
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
                string tmp = residentPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(residentPath)) File.Replace(tmp, residentPath, null); else File.Move(tmp, residentPath);
            }
            catch (Exception error) { Error = "resident_state_write_failed:" + error.GetBaseException().Message; }
        }

        private static void WriteOrderedStateLocked()
        {
            if (String.IsNullOrEmpty(tracePath)) return;
            try
            {
                string orderedPath = tracePath + ".ordered.json";
                string directory = Path.GetDirectoryName(orderedPath);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"utc\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",\"owner\":\"").Append(Owner).Append("\",\"records\":[");
                bool first = true;
                foreach (TraceRecord record in Owned.Values)
                {
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"instanceId\":").Append(record.InstanceId).Append(",\"citizenId\":").Append(record.CitizenId).Append(",\"sourceId\":").Append(record.SourceId).Append(",\"firstArrivalObserved\":").Append(record.HasFirstArrival ? "true" : "false").Append(",\"firstArrivalSuccess\":").Append(record.HasFirstArrival ? (record.FirstArrivalSuccess ? "true" : "false") : "null").Append(",\"lastArrivalSuccess\":").Append(record.LastArrivalSuccess ? "true" : "false").Append(",\"lastCallback\":\"").Append(Esc(record.LastCallback)).Append("\",\"lastEvent\":\"").Append(Esc(record.LastEvent)).Append("\",\"loopReturnCount\":").Append(record.LastLoopReturnCount).Append(",\"loopEndpointDistanceM\":").Append(F(record.LastLoopEndpointDistanceM)).Append(",\"loopStatus\":\"").Append(Esc(record.LastLoopStatus)).Append("\",\"loopReason\":\"").Append(Esc(record.LastLoopReason)).Append("\",\"events\":[");
                    for (int eventIndex = 0; eventIndex < record.Events.Count; eventIndex++)
                    {
                        if (eventIndex > 0) sb.Append(',');
                        CallbackEvent item = record.Events[eventIndex];
                        sb.Append("{\"sequence\":").Append(item.Sequence).Append(",\"callback\":\"").Append(Esc(item.Callback)).Append("\",\"hasSuccess\":").Append(item.HasSuccess ? "true" : "false").Append(",\"success\":").Append(item.Success ? "true" : "false").Append(",\"pathId\":").Append(item.PathId).Append(",\"pathUnitFlags\":\"").Append(Esc(item.PathUnitFlags)).Append("\",\"pathFindFlags\":\"").Append(Esc(item.PathFindFlags)).Append("\",\"instanceFlags\":\"").Append(Esc(item.InstanceFlags)).Append("\",\"sourceBuilding\":").Append(item.SourceBuilding).Append(",\"targetBuilding\":").Append(item.TargetBuilding).Append(",\"parentInstanceId\":").Append(item.ParentInstanceId).Append(",\"position\":{\"x\":").Append(F(item.Position.x)).Append(",\"y\":").Append(F(item.Position.y)).Append(",\"z\":").Append(F(item.Position.z)).Append("}}");
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
                string tmp = orderedPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(orderedPath)) File.Replace(tmp, orderedPath, null); else File.Move(tmp, orderedPath);
            }
            catch (Exception error) { Error = "ordered_state_write_failed:" + error.GetBaseException().Message; }
        }

        private static string Esc(string text)
        {
            return (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        private static string F(float value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class TraceRecord
        {
            public ushort InstanceId, SourceBuilding, TargetBuilding, ParentInstanceId;
            public uint CitizenId, PathId;
            public int SourceId, PathFailureCount, ArriveCount, ReleaseCount;
            public int EventSequence, LastLoopReturnCount, TransferSkipCount, LastTransferReasonCode, LastOfferObjectHash;
            public uint LastOfferFlags;
            public byte LastOfferLocalPark;
            public float LastLoopEndpointDistanceM;
            public string InfoName = "", LastCallback = "", LastEvent = "", LastLoopStatus = "", LastLoopReason = "", LastTransferReason = "", LastTransferReasonText = "", PathUnitFlags = "", PathFindFlags = "", InstanceFlags = "", CurrentLocation = "", LastReadError = "", FirstCallbackStack = "";
            public bool HasFirstArrival, FirstArrivalSuccess, LastArrivalSuccess;
            public readonly List<CallbackEvent> Events = new List<CallbackEvent>();
            public Vector3 Position;
        }

        private sealed class CallbackEvent
        {
            public int Sequence;
            public string Callback = "", TransferReason = "", PathUnitFlags = "", PathFindFlags = "", InstanceFlags = "";
            public bool HasSuccess, Success;
            public uint PathId;
            public int TransferReasonCode, OfferObjectHash;
            public uint OfferFlags;
            public byte OfferLocalPark;
            public ushort SourceBuilding, TargetBuilding, ParentInstanceId;
            public Vector3 Position;
        }
    }
}
