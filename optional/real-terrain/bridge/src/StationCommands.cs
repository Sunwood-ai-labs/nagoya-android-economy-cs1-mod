using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;
namespace SkylinesAgentBridge {
 public static class StationCommands {
  private static bool Finite(float f){return !float.IsNaN(f)&&!float.IsInfinity(f);}
  public static CommandResult PlaceStation(string body){
   string name=JsonUtil.GetString(body,"buildingPrefab","");if(name!="Metro Entrance"&&name!="Train Station")return CommandResult.Fail("Only Metro Entrance and Train Station supported");
   BuildingInfo info=PrefabCollection<BuildingInfo>.FindLoaded(name);if(info==null)return CommandResult.Fail("Station prefab unavailable");if(AssetPolicy.IsBlockedBuildingPrefab(info))return CommandResult.Fail("Station prefab is blocked by current asset policy");
   float x=JsonUtil.GetPointNumber(body,"position","x",float.NaN),z=JsonUtil.GetPointNumber(body,"position","z",float.NaN),angle=JsonUtil.GetNumber(body,"angleDegrees",0);
   if(!Finite(x)||!Finite(z)||!Finite(angle)||Math.Abs(x)>7900||Math.Abs(z)>7900)return CommandResult.Fail("Station pose outside finite map bounds");
   Vector3 position=new Vector3(x,0,z);position.y=TerrainManager.instance.SampleRawHeightSmoothWithWater(position,false,0);
   BuildingTool tool=ToolsModifierControl.GetTool<BuildingTool>();if(tool==null)return CommandResult.Fail("Loaded native building tool unavailable");
   MethodInfo create=typeof(BuildingTool).GetMethod("CreateBuilding",BindingFlags.Instance|BindingFlags.NonPublic,null,new Type[]{typeof(BuildingInfo).MakeByRefType(),typeof(Vector3),typeof(float),typeof(int),typeof(bool),typeof(bool)},null);
   if(create==null)return CommandResult.Fail("Verified native builder signature unavailable");
   if(JsonUtil.GetBool(body,"dryRun",false))return CommandResult.FromJson("{\"ok\":true,\"dryRun\":true,\"nativeBuilderResolved\":true,\"widthCells\":"+info.GetWidth()+",\"lengthCells\":"+info.GetLength()+"}");
   // Use the game's complete builder so station path networks are created too.
   object[] args={info,position,angle*Mathf.Deg2Rad,0,false,false};ushort id=(ushort)create.Invoke(tool,args);
   if(id==0)return CommandResult.Fail("Native station builder returned0; inspect before another action");
   CommandResult geometry=StationGeometryCommands.StationGeometry(id);if(!geometry.Ok)return geometry;
   return CommandResult.FromJson("{\"ok\":true,\"buildingId\":"+id+",\"nativeToolBuilder\":true,\"stationGeometry\":"+geometry.Json+"}");
  }
  public static CommandResult ConnectTrack(string body){
   string prefab=JsonUtil.GetString(body,"roadPrefab","Metro Track");if(prefab!="Metro Track"&&prefab!="Train Track")return CommandResult.Fail("Only Metro Track or Train Track supported");
   NetInfo info=PrefabCollection<NetInfo>.FindLoaded(prefab);if(info==null)return CommandResult.Fail("Track prefab absent");
   NetManager net=NetManager.instance;float av=JsonUtil.GetNumber(body,"startNodeId",0),bv=JsonUtil.GetNumber(body,"endNodeId",0);
   if(!Finite(av)||!Finite(bv)||av!=(int)av||bv!=(int)bv||av<=0||bv<=0||av>=net.m_nodes.m_buffer.Length||bv>=net.m_nodes.m_buffer.Length||av==bv)return CommandResult.Fail("Two exact existing node IDs required");
   ushort a=(ushort)av,b=(ushort)bv;NetNode na=net.m_nodes.m_buffer[a],nb=net.m_nodes.m_buffer[b];
   if((na.m_flags&NetNode.Flags.Created)==0||(nb.m_flags&NetNode.Flags.Created)==0||na.Info.m_class.m_subService!=info.m_class.m_subService||nb.Info.m_class.m_subService!=info.m_class.m_subService)return CommandResult.Fail("Station track node identity/type mismatch");
   Vector3 delta=nb.m_position-na.m_position;if(delta.magnitude<32||delta.magnitude>600||Math.Abs(delta.y)>1)return CommandResult.Fail("Bounded test track must be32..600m with matching station elevations");
   Vector3 da=OutgoingDirection(a,net,delta.normalized),db=OutgoingDirection(b,net,-delta.normalized);
   for(int i=0;i<8;i++){ushort existing=GetSegment(na,i);if(existing!=0){NetSegment s=net.m_segments.m_buffer[existing];if(s.m_startNode==b||s.m_endNode==b)return CommandResult.Fail("Nodes are already directly connected");}}
   if(JsonUtil.GetBool(body,"dryRun",false))return CommandResult.FromJson("{\"ok\":true,\"dryRun\":true,\"length\":"+JsonUtil.Number(delta.magnitude)+"}");
   SimulationManager sim=SimulationManager.instance;Randomizer random=sim.m_randomizer;ushort id;
   bool created=net.CreateSegment(out id,ref random,info,a,b,da,db,sim.m_currentBuildIndex,sim.m_currentBuildIndex,false);sim.m_randomizer=random;if(!created)return CommandResult.Fail("Native track creation failed; no retry");sim.m_currentBuildIndex+=2;
   NetSegment actual=net.m_segments.m_buffer[id];return CommandResult.FromJson("{\"ok\":true,\"segmentId\":"+id+",\"startNodeId\":"+actual.m_startNode+",\"endNodeId\":"+actual.m_endNode+",\"prefab\":\""+JsonUtil.Escape(actual.Info.name)+"\",\"flags\":\""+JsonUtil.Escape(actual.m_flags.ToString())+"\"}");
  }
  private static ushort GetSegment(NetNode n,int i){switch(i){case 0:return n.m_segment0;case 1:return n.m_segment1;case 2:return n.m_segment2;case 3:return n.m_segment3;case 4:return n.m_segment4;case 5:return n.m_segment5;case 6:return n.m_segment6;default:return n.m_segment7;}}
  private static Vector3 OutgoingDirection(ushort id,NetManager net,Vector3 fallback){NetNode n=net.m_nodes.m_buffer[id];ushort only=0;int count=0;for(int i=0;i<8;i++){ushort s=GetSegment(n,i);if(s!=0){only=s;count++;}}if(count!=1)throw new InvalidOperationException("Station endpoint must have exactly one existing track");NetSegment segment=net.m_segments.m_buffer[only];Vector3 d=segment.m_startNode==id?segment.m_startDirection:segment.m_endDirection;d=-d;d.y=0;return d.sqrMagnitude>.1f?d.normalized:fallback;}
 }
}
