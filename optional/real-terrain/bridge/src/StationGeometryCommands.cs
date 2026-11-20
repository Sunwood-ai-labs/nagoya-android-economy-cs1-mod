using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
namespace SkylinesAgentBridge { public static class StationGeometryCommands { private const int MaxGeometryNodes=64;
public static CommandResult StationGeometry(int id)
        {
            BuildingManager buildings = BuildingManager.instance;
            NetManager net = NetManager.instance;
            if (id <= 0 || id >= buildings.m_buildings.m_buffer.Length)
            {
                return CommandResult.Fail("Building id is outside the loaded building buffer.");
            }

            Building building = buildings.m_buildings.m_buffer[id];
            if ((building.m_flags & Building.Flags.Created) == Building.Flags.None)
            {
                return CommandResult.Fail("Building was not found: " + id);
            }

            BuildingInfo info = building.Info;
            if (info == null)
            {
                return CommandResult.Fail("Building info was not found: " + id);
            }

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"buildingId\":").Append(id);
            json.Append(",\"flags\":\"").Append(JsonUtil.Escape(building.m_flags.ToString())).Append("\"");
            json.Append(",\"aiType\":\"").Append(JsonUtil.Escape(info.m_buildingAI == null ? "" : info.m_buildingAI.GetType().FullName)).Append("\"");
            json.Append(",\"prefab\":\"").Append(JsonUtil.Escape(info.name)).Append("\"");
            json.Append(",\"width\":").Append(info.GetWidth());
            json.Append(",\"length\":").Append(info.GetLength());
            json.Append(",\"netNode\":").Append(building.m_netNode);
            json.Append(",\"nodes\":[");

            HashSet<ushort> visited = new HashSet<ushort>();
            ushort nodeId = building.m_netNode;
            int nodeCount = 0;
            bool cycleDetected = false;
            bool truncated = false;
            bool firstNode = true;
            while (nodeId != 0 && nodeCount < MaxGeometryNodes)
            {
                if (nodeId >= net.m_nodes.m_buffer.Length)
                {
                    break;
                }
                if (!visited.Add(nodeId))
                {
                    cycleDetected = true;
                    break;
                }

                NetNode node = net.m_nodes.m_buffer[nodeId];
                if (!firstNode)
                {
                    json.Append(",");
                }
                json.Append("{\"id\":").Append(nodeId);
                AppendPoint(json, "position", node.m_position);
                json.Append(",\"nextBuildingNode\":").Append(node.m_nextBuildingNode);
                json.Append(",\"segment0\":").Append(node.m_segment0);
                json.Append(",\"segment1\":").Append(node.m_segment1);
                json.Append(",\"segment2\":").Append(node.m_segment2);
                json.Append(",\"segment3\":").Append(node.m_segment3);
                json.Append(",\"segment4\":").Append(node.m_segment4);
                json.Append(",\"segment5\":").Append(node.m_segment5);
                json.Append(",\"segment6\":").Append(node.m_segment6);
                json.Append(",\"segment7\":").Append(node.m_segment7);
                json.Append("}");
                firstNode = false;
                nodeCount++;
                nodeId = node.m_nextBuildingNode;
            }
            if (nodeId != 0 && nodeCount >= MaxGeometryNodes)
            {
                truncated = true;
            }
            json.Append("]");
            json.Append(",\"nodeCount\":").Append(nodeCount);
            json.Append(",\"cycleDetected\":").Append(JsonUtil.Bool(cycleDetected));
            json.Append(",\"nodeChainTruncated\":").Append(JsonUtil.Bool(truncated));
            json.Append(",\"pathReflection\":");
            AppendPathReflection(json, info);
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }
public static CommandResult PrefabGeometry(string name) {
            if(name!="Metro Entrance"&&name!="Train Station")return CommandResult.Fail("Only prototype station prefabs supported");
            BuildingInfo info=PrefabCollection<BuildingInfo>.FindLoaded(name);if(info==null)return CommandResult.Fail("Station prefab unavailable");
            StringBuilder json=new StringBuilder("{\"ok\":true,\"prefab\":\""+JsonUtil.Escape(name)+"\",\"width\":"+info.GetWidth()+",\"length\":"+info.GetLength()+",\"aiType\":\""+JsonUtil.Escape(info.m_buildingAI.GetType().FullName)+"\",\"paths\":");
            AppendPathReflection(json,info);json.Append("}");return CommandResult.FromJson(json.ToString());
        }
private static void AppendPoint(StringBuilder json, string name, Vector3 point)
        {
            json.Append(",\"").Append(name).Append("\":{\"x\":").Append(JsonUtil.Number(point.x));
            json.Append(",\"y\":").Append(JsonUtil.Number(point.y));
            json.Append(",\"z\":").Append(JsonUtil.Number(point.z)).Append("}");
        }
private static void AppendPathReflection(StringBuilder json, BuildingInfo info)
        {
            FieldInfo pathsField = FindField(info.GetType(), "m_paths");
            if (pathsField == null)
            {
                json.Append("{\"status\":\"unconfirmed\",\"reason\":\"m_paths field unavailable\"}");
                return;
            }

            Array paths = null;
            try { paths = pathsField.GetValue(info) as Array; } catch { }
            if (paths == null)
            {
                json.Append("{\"status\":\"unconfirmed\",\"reason\":\"m_paths value unavailable\"}");
                return;
            }

            StringBuilder entries = new StringBuilder();
            bool first = true;
            int emitted = 0;
            for (int i = 0; i < Math.Min(paths.Length,64); i++)
            {
                object path = paths.GetValue(i);
                if (path == null)
                {
                    continue;
                }

                Type pathType = path.GetType();
                FieldInfo netInfoField = FindField(pathType, "m_netInfo");
                FieldInfo nodesField = FindField(pathType, "m_nodes");
                if (netInfoField == null && nodesField == null)
                {
                    continue;
                }

                if (!first)
                {
                    entries.Append(",");
                }
                entries.Append("{\"index\":").Append(i);
                if (netInfoField != null)
                {
                    NetInfo netInfo = null;
                    try { netInfo = netInfoField.GetValue(path) as NetInfo; } catch { }
                    entries.Append(",\"m_netInfo\":\"").Append(JsonUtil.Escape(netInfo == null ? "" : netInfo.name)).Append("\"");
                }
                if (nodesField != null)
                {
                    Array nodes = null;
                    try { nodes = nodesField.GetValue(path) as Array; } catch { }
                    if (nodes != null)
                    {
                        entries.Append(",\"m_nodesLocal\":[");
                        bool firstNode = true;
                        for (int n = 0; n < nodes.Length && n < 256; n++)
                        {
                            Vector3 point;
                            if (!TryReadVector3(nodes.GetValue(n), out point))
                            {
                                continue;
                            }
                            if (!firstNode)
                            {
                                entries.Append(",");
                            }
                            entries.Append("{\"x\":").Append(JsonUtil.Number(point.x));
                            entries.Append(",\"y\":").Append(JsonUtil.Number(point.y));
                            entries.Append(",\"z\":").Append(JsonUtil.Number(point.z)).Append("}");
                            firstNode = false;
                        }
                        entries.Append("]");
                    }
                }
                entries.Append("}");
                first = false;
                emitted++;
            }

            json.Append("{\"status\":\"").Append(emitted == 0 ? "unconfirmed" : "confirmed");
            json.Append("\",\"localUntransformed\":").Append(JsonUtil.Bool(emitted > 0));
            json.Append(",\"paths\":[").Append(entries.ToString()).Append("]}");
        }
private static FieldInfo FindField(Type type, string name)
        {
            if (type == null)
            {
                return null;
            }
            return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
private static bool TryReadVector3(object value, out Vector3 point)
        {
            if (value is Vector3)
            {
                point = (Vector3)value;
                return true;
            }
            point = Vector3.zero;
            return false;
        }
}}
