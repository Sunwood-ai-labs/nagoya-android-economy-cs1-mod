using System;
using System.Threading;
using UnityEngine;
namespace SkylinesAgentBridge {
 public static class MetroReleaseCommands {
  public static CommandResult Release(string body) {
   float number=JsonUtil.GetNumber(body,"id",0);if(float.IsNaN(number)||float.IsInfinity(number)||number<1||number>65535||number!=(int)number)return CommandResult.Fail("Exact native segment id required");
   ushort id=(ushort)number;bool keepNodes=JsonUtil.GetBool(body,"keepNodes",true),dryRun=JsonUtil.GetBool(body,"dryRun",false);
   if(!AgentBridge.Instance.LevelLoaded||SimulationManager.instance==null)return CommandResult.Fail("No loaded city");
   ManualResetEvent done=new ManualResetEvent(false);CommandResult result=null;
   SimulationManager.instance.AddAction(delegate {
    try {
     if(!AgentBridge.Instance.LevelLoaded){result=CommandResult.Fail("City unloaded");return;}
     NetManager net=NetManager.instance;if(id>=net.m_segments.m_buffer.Length){result=CommandResult.Fail("Segment id outside loaded native buffer");return;}NetSegment old=net.m_segments.m_buffer[id];
     if((old.m_flags&NetSegment.Flags.Created)==0||old.Info==null||old.Info.name!="Metro Track"){result=CommandResult.Fail("Only existing Metro Track segments may be released");return;}
     if(dryRun){result=CommandResult.FromJson("{\"ok\":true,\"dryRun\":true,\"segmentId\":"+id+",\"simulationThread\":true}");return;}
     ushort a=old.m_startNode,b=old.m_endNode;net.ReleaseSegment(id,keepNodes);
     result=CommandResult.FromJson("{\"ok\":true,\"releasedSegmentId\":"+id+",\"oldStartNodeId\":"+a+",\"oldEndNodeId\":"+b+",\"keepNodes\":"+JsonUtil.Bool(keepNodes)+",\"simulationThread\":true}");
    } catch(Exception ex){Debug.LogException(ex);result=CommandResult.Fail(ex.ToString());}
    finally{done.Set();}
   });
   if(!done.WaitOne(8000,false)){CommandResult timeout=CommandResult.Fail("Native release outcome uncertain; do not retry");timeout.Json="{\"ok\":false,\"status\":\"uncertain\",\"timeout\":true,\"retryAllowed\":false}";return timeout;}
   done.Close();return result??CommandResult.Fail("No release result");
  }
 }
}
