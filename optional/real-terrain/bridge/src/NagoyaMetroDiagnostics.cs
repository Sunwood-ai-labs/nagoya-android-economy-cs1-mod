using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using ColossalFramework;
using UnityEngine;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// Fixed, read-only diagnostics for the Nagoya Metro path experiment.
    /// No caller-controlled type/member names are accepted.
    /// </summary>
    public static class NagoyaMetroDiagnostics
    {
        private const BindingFlags InstanceFields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly ushort[] SegmentIds = new ushort[]
        {
            13713, 10872, 10861, 8409, 33997, 30563, 15724, 36860
        };

        private static readonly ushort[] NodeIds = new ushort[]
        {
            8385, 30358, 3752, 27882, 22404, 15799, 20512, 14413, 14412
        };

        private static readonly string[] PrefabNames = new string[]
        {
            "Metro Track", "Metro Station Track", "Metro Line"
        };

        public static CommandResult Build()
        {
            NetManager net = NetManager.instance;
            if (net == null)
            {
                return CommandResult.Fail("NetManager.instance is unavailable.");
            }

            StringBuilder json = new StringBuilder(32768);
            json.Append("{\"ok\":true,\"readOnly\":true,\"prefabs\":[");
            for (int i = 0; i < PrefabNames.Length; i++)
            {
                if (i > 0) json.Append(",");
                AppendPrefab(json, PrefabNames[i]);
            }

            json.Append("],\"segments\":[");
            for (int i = 0; i < SegmentIds.Length; i++)
            {
                if (i > 0) json.Append(",");
                AppendSegment(json, net, SegmentIds[i]);
            }

            json.Append("],\"nodes\":[");
            for (int i = 0; i < NodeIds.Length; i++)
            {
                if (i > 0) json.Append(",");
                AppendNode(json, net, NodeIds[i]);
            }

            json.Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        private static void AppendPrefab(StringBuilder json, string name)
        {
            NetInfo info = PrefabCollection<NetInfo>.FindLoaded(name);
            json.Append("{\"name\":\"").Append(JsonUtil.Escape(name)).Append("\"");
            if (info == null)
            {
                json.Append(",\"available\":false}");
                return;
            }

            json.Append(",\"available\":true");
            AppendField(json, info, "m_maxTurnAngle");
            AppendField(json, info, "m_maxTurnAngleCos");
            AppendField(json, info, "m_maxBuildAngle");
            AppendField(json, info, "m_maxBuildAngleCos");
            FieldRead netAi = ReadField(info, "m_netAI");
            json.Append(",\"m_netAIType\":");
            if (!netAi.Available || netAi.Value == null)
            {
                json.Append("null");
            }
            else
            {
                json.Append("\"").Append(JsonUtil.Escape(netAi.Value.GetType().FullName)).Append("\"");
            }

            json.Append(",\"lanes\":[");
            FieldRead lanesRead = ReadField(info, "m_lanes");
            Array lanes = lanesRead.Value as Array;
            if (lanes != null)
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    if (i > 0) json.Append(",");
                    AppendLaneInfo(json, lanes.GetValue(i), i);
                }
            }
            json.Append("]}");
        }

        private static void AppendLaneInfo(StringBuilder json, object lane, int index)
        {
            json.Append("{\"index\":").Append(index);
            AppendField(json, lane, "m_direction");
            AppendField(json, lane, "m_finalDirection");
            AppendField(json, lane, "m_position");
            AppendField(json, lane, "m_laneType");
            AppendField(json, lane, "m_vehicleType");
            AppendField(json, lane, "m_stopType");
            AppendField(json, lane, "m_allowConnect");
            json.Append("}");
        }

        private static void AppendSegment(StringBuilder json, NetManager net, ushort id)
        {
            json.Append("{\"id\":").Append(id);
            if (id == 0 || id >= net.m_segments.m_buffer.Length)
            {
                json.Append(",\"available\":false}");
                return;
            }

            NetSegment segment = net.m_segments.m_buffer[id];
            json.Append(",\"available\":true");
            AppendField(json, segment, "m_flags");
            AppendField(json, segment, "m_startNode");
            AppendField(json, segment, "m_endNode");
            AppendField(json, segment, "m_cornerAngleStart");
            AppendField(json, segment, "m_cornerAngleEnd");
            AppendField(json, segment, "m_overridePathFindDirectionLimit");
            AppendField(json, segment, "m_path");
            NetInfo info = segment.Info;
            json.Append(",\"prefab\":");
            if (info == null) json.Append("null");
            else json.Append("\"").Append(JsonUtil.Escape(info.name)).Append("\"");

            uint firstLane = ReadUInt32(segment, "m_lanes");
            json.Append(",\"laneChain\":[");
            uint laneId = firstLane;
            int guard = 0;
            bool first = true;
            while (laneId != 0 && guard++ < 64 && laneId < net.m_lanes.m_buffer.Length)
            {
                if (!first) json.Append(",");
                AppendNetLane(json, net.m_lanes.m_buffer[laneId], laneId);
                first = false;
                laneId = ReadUInt32(net.m_lanes.m_buffer[laneId], "m_nextLane");
            }
            json.Append("],\"laneChainTerminated\":").Append(JsonUtil.Bool(laneId == 0));
            json.Append("}");
        }

        private static void AppendNetLane(StringBuilder json, object lane, uint id)
        {
            json.Append("{\"id\":").Append(id);
            AppendField(json, lane, "m_nodes");
            AppendField(json, lane, "m_flags");
            AppendField(json, lane, "m_length");
            AppendField(json, lane, "m_segment");
            AppendField(json, lane, "m_nextLane");
            json.Append("}");
        }

        private static void AppendNode(StringBuilder json, NetManager net, ushort id)
        {
            json.Append("{\"id\":").Append(id);
            if (id == 0 || id >= net.m_nodes.m_buffer.Length)
            {
                json.Append(",\"available\":false}");
                return;
            }

            NetNode node = net.m_nodes.m_buffer[id];
            json.Append(",\"available\":true");
            AppendField(json, node, "m_flags");
            AppendField(json, node, "m_lane");
            AppendField(json, node, "m_nextLaneNode");
            AppendField(json, node, "m_connectCount");
            AppendField(json, node, "m_transportLine");
            AppendField(json, node, "m_position");
            json.Append(",\"segments\":[");
            bool first = true;
            for (int i = 0; i < 8; i++)
            {
                ushort segment = node.GetSegment(i);
                if (segment == 0) continue;
                if (!first) json.Append(",");
                json.Append(segment);
                first = false;
            }
            json.Append("]}");
        }

        private static uint ReadUInt32(object target, string name)
        {
            FieldRead read = ReadField(target, name);
            if (!read.Available || read.Value == null) return 0u;
            try { return Convert.ToUInt32(read.Value, CultureInfo.InvariantCulture); }
            catch { return 0u; }
        }

        private static void AppendField(StringBuilder json, object target, string name)
        {
            json.Append(",\"").Append(name).Append("\":");
            FieldRead read = ReadField(target, name);
            if (!read.Available)
            {
                json.Append("null");
                return;
            }
            AppendValue(json, read.Value);
        }

        private static void AppendValue(StringBuilder json, object value)
        {
            if (value == null)
            {
                json.Append("null");
                return;
            }
            if (value is string)
            {
                json.Append("\"").Append(JsonUtil.Escape((string)value)).Append("\"");
                return;
            }
            if (value is Enum)
            {
                json.Append("\"").Append(JsonUtil.Escape(value.ToString())).Append("\"");
                return;
            }
            if (value is float)
            {
                json.Append(JsonUtil.Number((float)value));
                return;
            }
            if (value is double)
            {
                json.Append(((double)value).ToString("0.###", CultureInfo.InvariantCulture));
                return;
            }
            if (value is Vector3)
            {
                Vector3 v = (Vector3)value;
                json.Append("{\"x\":").Append(JsonUtil.Number(v.x));
                json.Append(",\"y\":").Append(JsonUtil.Number(v.y));
                json.Append(",\"z\":").Append(JsonUtil.Number(v.z)).Append("}");
                return;
            }
            if (value is bool)
            {
                json.Append(JsonUtil.Bool((bool)value));
                return;
            }
            try
            {
                json.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            catch
            {
                json.Append("null");
            }
        }

        private static FieldRead ReadField(object target, string name)
        {
            if (target == null) return new FieldRead(false, null);
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(name, InstanceFields | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    try { return new FieldRead(true, field.GetValue(target)); }
                    catch { return new FieldRead(true, null); }
                }
                type = type.BaseType;
            }
            return new FieldRead(false, null);
        }

        private sealed class FieldRead
        {
            public readonly bool Available;
            public readonly object Value;
            public FieldRead(bool available, object value)
            {
                Available = available;
                Value = value;
            }
        }
    }
}
