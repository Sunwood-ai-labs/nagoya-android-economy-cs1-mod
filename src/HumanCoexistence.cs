using ICities;
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework.Math;
using UnityEngine;

namespace NagoyaStationBlock {
 public sealed class HumanCoexistence : MonoBehaviour {
  public int DesiredHumans=12;
  public int MaxTransitions=NagoyaStationBlock.NativeHumanRouteLoop.DefaultMaxTransitions;
  public string RuntimeStatePath=NagoyaRoadDelivery.RuntimePaths.Data(@"runtime\human-coexistence-runtime.json");
  public bool NativeCitizenMode=true;
  public bool LoopRoutes=true;
  private readonly object stateLock=new object();
  private readonly List<CreatedHuman> created=new List<CreatedHuman>();
  private Vector3[] starts=new Vector3[0],ends=new Vector3[0];
  private float nextState;
  private bool loaded,spawnPending,releasePending,released;
  private CitizenManager ownerManager;
  private string lastError="";
  private static MethodInfo nativePathMethod;

  public bool Load(Vector3[] sidewalkRoute) {
   if(sidewalkRoute==null||sidewalkRoute.Length<2){SetError("blocked_route_missing");return false;}
   return LoadPairs(new Vector3[]{sidewalkRoute[0]},new Vector3[]{sidewalkRoute[sidewalkRoute.Length-1]});
  }
  public bool LoadPairs(Vector3[] pairStarts,Vector3[] pairEnds) {
   return LoadPairs(pairStarts,pairEnds,NagoyaStationBlock.NativeHumanRouteLoop.DefaultMaxTransitions);
  }
  public bool LoadPairs(Vector3[] pairStarts,Vector3[] pairEnds,int maxTransitions) {
   if(pairStarts==null||pairEnds==null||pairStarts.Length==0||pairStarts.Length!=pairEnds.Length||pairStarts.Length>32){SetError("blocked_pair_contract");return false;}
   if(!NagoyaStationBlock.NativeHumanRouteLoop.IsValidMaxTransitions(maxTransitions)){SetError("blocked_maxTransitions_range_16_4096");return false;}
   for(int i=0;i<pairStarts.Length;i++) if(Vector3.Distance(pairStarts[i],pairEnds[i])<30f){SetError("blocked_pair_too_short_"+i);return false;}
   lock(stateLock){starts=(Vector3[])pairStarts.Clone();ends=(Vector3[])pairEnds.Clone();DesiredHumans=pairStarts.Length;MaxTransitions=maxTransitions;loaded=true;released=false;releasePending=false;lastError="";}
   WriteState("loaded_pairs_native_api_pending");return true;
  }
  public bool SpawnNativeHumans() {
   lock(stateLock){if(!loaded||!NativeCitizenMode||spawnPending||created.Count>0||released)return false;spawnPending=true;}
   if(!NativeHumanTrace.Install(RuntimeStatePath)) { lock(stateLock){spawnPending=false;lastError=NativeHumanTrace.Error;} WriteState("native_spawn_blocked_trace_install"); return false; }
   SimulationManager sim=SimulationManager.instance;if(sim==null){lock(stateLock){spawnPending=false;lastError="SimulationManager_missing";}WriteState("native_spawn_blocked");return false;}
   sim.AddAction(delegate { SpawnBatch(); });return true;
  }
  private void SpawnBatch() {
   bool any=false;Vector3[] ss,ee;lock(stateLock){if(releasePending||released){spawnPending=false;return;}ownerManager=CitizenManager.instance;ss=starts;ee=ends;}
   int target=Math.Min(Math.Min(DesiredHumans,ss.Length),32);
   for(int i=0;i<target;i++) if(SpawnOne(i,ss[i],ee[i])) any=true;
   lock(stateLock){spawnPending=false;if(!any)lastError="no_native_human_created";}
   WriteState(any?"native_spawn_attempted":"native_spawn_blocked");
  }
  private bool SpawnOne(int sourceId,Vector3 start,Vector3 end) {
   CitizenManager cm=CitizenManager.instance;if(cm==null){SetError("CitizenManager_missing");return false;}
   Randomizer random=new Randomizer((uint)(0x4E474800+sourceId));uint citizen=0;ushort instance=0;CitizenInfo info=null;CitizenAI ai=null;
   Citizen.Gender gender=(sourceId&1)==0?Citizen.Gender.Male:Citizen.Gender.Female;
   try {
    info=cm.GetGroupCitizenInfo(ref random,ItemClass.Service.Residential,gender,Citizen.SubCulture.Generic,Citizen.AgePhase.Adult0);if(info==null)throw new Exception("CitizenInfo_missing");
    ai=info.GetAI() as CitizenAI;if(ai==null)throw new Exception("CitizenAI_missing");
    if(!cm.CreateCitizen(out citizen,120,0,ref random,gender))throw new Exception("CreateCitizen_failed");
    if(!cm.CreateCitizenInstance(out instance,ref random,info,citizen)){cm.ReleaseCitizen(citizen);return false;}
    CitizenInstance data=cm.m_instances.m_buffer[instance];
    data.m_citizen=citizen;data.m_sourceBuilding=0;data.m_targetBuilding=0;data.m_frame0.m_position=start;data.m_frame0.m_rotation=Quaternion.identity;data.m_frame1=data.m_frame0;data.m_frame2=data.m_frame0;data.m_frame3=data.m_frame0;data.m_targetPos=new Vector4(start.x,start.y,start.z,1f);data.m_flags|=CitizenInstance.Flags.Created;
    cm.m_instances.m_buffer[instance]=data;
    Citizen parent=cm.m_citizens.m_buffer[citizen];parent.m_instance=instance;parent.CurrentLocation=Citizen.Location.Moving;cm.m_citizens.m_buffer[citizen]=parent;
    ai.CreateInstance(instance,ref data);data.m_frame0.m_position=start;data.m_frame0.m_rotation=Quaternion.identity;data.m_frame1=data.m_frame0;data.m_frame2=data.m_frame0;data.m_frame3=data.m_frame0;data.m_flags|=CitizenInstance.Flags.Character;cm.m_instances.m_buffer[instance]=data;
    data.Spawn(instance);cm.m_instances.m_buffer[instance]=data;
    MethodInfo pathMethod=ResolveNativePathMethod();object[] args=new object[]{instance,data,start,end,null,false,false};bool path=(bool)pathMethod.Invoke(ai,args);data=(CitizenInstance)args[1];cm.m_instances.m_buffer[instance]=data;
    if(!path){data.Unspawn(instance);cm.m_instances.m_buffer[instance]=data;TryReleaseOwned(cm,citizen,instance,data.m_infoIndex);return false;}
    CreatedHuman h=new CreatedHuman(citizen,instance,sourceId,info.name,ai.GetType().Name,data.m_infoIndex,start,end,MaxTransitions);lock(stateLock){created.Add(h);}NativeHumanTrace.RegisterOwned(instance,citizen,sourceId,info.name);NativeHumanRouteLoop.RegisterOwned(instance,citizen,sourceId,data.m_infoIndex,start,end,ownerManager,MaxTransitions);return true;
   } catch(Exception error) {
    SetError(error.Message);
    if(instance!=0)TryReleaseOwned(cm,citizen,instance,0);else if(citizen!=0)try{cm.ReleaseCitizen(citizen);}catch{}
    Debug.LogWarning("HumanCoexistence native spawn blocked: "+error.Message);return false;
   }
  }
  private static MethodInfo ResolveNativePathMethod() {
   if(nativePathMethod!=null)return nativePathMethod;
   Type[] expected=new Type[]{typeof(ushort),typeof(CitizenInstance).MakeByRefType(),typeof(Vector3),typeof(Vector3),typeof(VehicleInfo),typeof(bool),typeof(bool)};
   foreach(MethodInfo m in typeof(CitizenAI).GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)) {if(m.Name!="StartPathFind")continue;ParameterInfo[] p=m.GetParameters();if(p.Length!=expected.Length)continue;bool same=true;for(int i=0;i<p.Length;i++)if(p[i].ParameterType!=expected[i])same=false;if(same){nativePathMethod=m;break;}}
   if(nativePathMethod==null)throw new Exception("CitizenAI.StartPathFind_exact7_not_found");return nativePathMethod;
  }
  private static bool TryReleaseOwned(CitizenManager cm,uint citizen,ushort instance,ushort infoIndex) {
   bool ownInstance=false,ownCitizen=false;
   try {if(instance<cm.m_instances.m_buffer.Length){CitizenInstance d=cm.m_instances.m_buffer[instance];ownInstance=d.m_citizen==citizen&&(d.m_flags&CitizenInstance.Flags.Created)!=0&&(infoIndex==0||d.m_infoIndex==infoIndex);}}catch{}
   try {if(citizen<cm.m_citizens.m_buffer.Length){Citizen c=cm.m_citizens.m_buffer[citizen];ownCitizen=c.m_instance==instance;}}catch{}
   if(!ownInstance||!ownCitizen)return false;
   try{cm.ReleaseCitizenInstance(instance);cm.ReleaseCitizen(citizen);return true;}catch{return false;}
  }
  private void Update() {
   if(!loaded||Time.realtimeSinceStartup<nextState)return;nextState=Time.realtimeSinceStartup+1f;
   lock(stateLock) {
    CitizenManager cm=CitizenManager.instance;
    for(int i=0;i<created.Count;i++) {
     CreatedHuman h=created[i];
     try {
      if(cm==null||h.InstanceId>=cm.m_instances.m_buffer.Length)throw new Exception("instance_readback_unavailable");
      CitizenInstance d=cm.m_instances.m_buffer[h.InstanceId];
      h.SourceBuilding=d.m_sourceBuilding;h.TargetBuilding=d.m_targetBuilding;h.InstanceFlags=((uint)d.m_flags).ToString();
      if(h.CitizenId<cm.m_citizens.m_buffer.Length){Citizen parent=cm.m_citizens.m_buffer[h.CitizenId];h.ParentInstanceId=parent.m_instance;h.CurrentLocation=((int)parent.CurrentLocation).ToString();}
      h.PreviousPathId=h.PathId;
      h.PathId=d.m_path;
      h.PathUnitFlags="";h.PathFindFlags="";
      if(d.m_path!=0&&PathManager.instance!=null&&d.m_path<PathManager.instance.m_pathUnits.m_buffer.Length){PathUnit pu=PathManager.instance.m_pathUnits.m_buffer[d.m_path];h.PathUnitFlags=((uint)pu.m_simulationFlags).ToString();h.PathFindFlags=pu.m_pathFindFlags.ToString();}
      int observedLoopCount,observedMaxTransitions;string observedLoopStatus,observedLoopReason;NativeHumanRouteLoop.GetSnapshot(h.InstanceId,out observedLoopCount,out observedLoopStatus,out observedLoopReason,out observedMaxTransitions);h.ReturnCount=observedLoopCount;h.MaxTransitions=observedMaxTransitions;h.NativeLoopStatus=observedLoopStatus;h.NativeLoopReason=observedLoopReason;
      bool own=!h.Lost&&d.m_citizen==h.CitizenId&&(d.m_flags&CitizenInstance.Flags.Created)!=0&&d.m_infoIndex==h.InfoIndex;
      if(!own){h.Active=false;h.State=d.m_citizen==0?"despawned":"not_owned_or_reused";h.Lost=true;}
      else {
       float moved=Vector3.Distance(h.Position,d.m_frame0.m_position);h.ActualMovedMeters+=moved;h.PositionChanged=moved>0.01f;h.Position=d.m_frame0.m_position;h.Flags=((uint)d.m_flags).ToString();h.Active=true;h.State=(d.m_flags&CitizenInstance.Flags.OnPath)!=0?"on_path":"active";
      }
      created[i]=h;
     } catch(Exception error){h.Active=false;h.Lost=true;h.State="readback_failed";h.LastError=error.Message;created[i]=h;}
    }
   }
   WriteState("running_native_citizen_ai");
  }
  public void ReleaseCreated() {
   lock(stateLock){if(releasePending||released)return;releasePending=true;loaded=false;}
   SimulationManager sim=SimulationManager.instance;if(sim==null){lock(stateLock){releasePending=false;lastError="SimulationManager_missing_release";}WriteState("release_blocked");return;}
   sim.AddAction(delegate { ReleaseBatch(); });
  }
  private void ReleaseBatch() {
   CitizenManager cm=CitizenManager.instance;lock(stateLock){for(int i=0;i<created.Count;i++){CreatedHuman h=created[i];bool ok=!h.Lost&&cm!=null&&cm==ownerManager&&TryReleaseOwned(cm,h.CitizenId,h.InstanceId,h.InfoIndex);h.Active=false;h.State=ok?"released_created_ids":"release_skipped_not_owned";created[i]=h;}releasePending=false;released=true;}
   NativeHumanRouteLoop.ClearOwned();NativeHumanRouteLoop.Uninstall();NativeHumanResidentIsolation.Uninstall();NativeHumanTrace.ClearOwned();NativeHumanTrace.Uninstall();
   WriteState("released_created_ids");
  }
  private void OnDisable(){ReleaseCreated();} private void OnDestroy(){ReleaseCreated();}
  private void SetError(string e){lock(stateLock){lastError=e;}WriteState("blocked");}
  private static string Esc(string s){return (s??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r"," ").Replace("\n"," ");}
  private static string F(float n){return n.ToString(System.Globalization.CultureInfo.InvariantCulture);}
  private void WriteState(string status) {
   lock(stateLock) {
    try {
     int active=0;for(int i=0;i<created.Count;i++){if(created[i].Active)active++;}
     StringBuilder sb=new StringBuilder();
     sb.Append("{\"utc\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",\"status\":\"").Append(Esc(status)).Append("\",\"mode\":\"NativeCitizenAI\",\"residentEconomicIsolation\":\"prototype_owned_cohort_transfer_exclusion_unverified\",\"residentIsolationInstalled\":").Append(NativeHumanResidentIsolation.Installed?"true":"false").Append(",\"maxTransitions\":").Append(MaxTransitions).Append(",\"spawnPending\":").Append(spawnPending?"true":"false").Append(",\"nativeCreatedCount\":").Append(created.Count).Append(",\"activeCount\":").Append(active).Append(",\"lastError\":\"").Append(Esc(lastError)).Append("\",\"humans\":[");
     for(int i=0;i<created.Count;i++) {
      if(i>0)sb.Append(',');
      CreatedHuman h=created[i];
      sb.Append("{\"sourceId\":").Append(h.SourceId).Append(",\"citizenId\":").Append(h.CitizenId).Append(",\"instanceId\":").Append(h.InstanceId).Append(",\"nativeInfoName\":\"").Append(Esc(h.InfoName)).Append("\",\"nativeAIType\":\"").Append(Esc(h.AIType)).Append("\",\"position\":{\"x\":").Append(F(h.Position.x)).Append(",\"y\":").Append(F(h.Position.y)).Append(",\"z\":").Append(F(h.Position.z)).Append("},\"active\":").Append(h.Active?"true":"false").Append(",\"routeState\":\"").Append(Esc(h.State)).Append("\",\"pathId\":").Append(h.PathId).Append(",\"previousPathId\":").Append(h.PreviousPathId).Append(",\"pathUnitFlags\":\"").Append(Esc(h.PathUnitFlags)).Append("\",\"pathFindFlags\":\"").Append(Esc(h.PathFindFlags)).Append("\",\"flags\":\"").Append(Esc(h.Flags)).Append("\",\"instanceFlags\":\"").Append(Esc(h.InstanceFlags)).Append("\",\"sourceBuilding\":").Append(h.SourceBuilding).Append(",\"targetBuilding\":").Append(h.TargetBuilding).Append(",\"parentInstanceId\":").Append(h.ParentInstanceId).Append(",\"currentLocation\":\"").Append(Esc(h.CurrentLocation)).Append("\",\"returnCount\":").Append(h.ReturnCount).Append(",\"maxTransitions\":").Append(h.MaxTransitions).Append(",\"nativeLoopStatus\":\"").Append(Esc(h.NativeLoopStatus)).Append("\",\"nativeLoopReason\":\"").Append(Esc(h.NativeLoopReason)).Append("\",\"positionChanged\":").Append(h.PositionChanged?"true":"false").Append(",\"actualMovedMeters\":").Append(F(h.ActualMovedMeters)).Append(",\"lastError\":\"").Append(Esc(h.LastError)).Append("\",\"lost\":").Append(h.Lost?"true":"false").Append("}");
     }
     sb.Append("]}");
     string tmp=RuntimeStatePath+".tmp";File.WriteAllText(tmp,sb.ToString());if(File.Exists(RuntimeStatePath))File.Replace(tmp,RuntimeStatePath,null);else File.Move(tmp,RuntimeStatePath);
    } catch(Exception error) { Debug.LogWarning("HumanCoexistence state write blocked: "+error.Message); }
   }
  }
  private struct CreatedHuman {public uint CitizenId,PathId,PreviousPathId;public ushort InstanceId,InfoIndex,SourceBuilding,TargetBuilding,ParentInstanceId;public int SourceId,ReturnCount,MaxTransitions;public string InfoName,AIType,State,Flags,InstanceFlags,PathUnitFlags,PathFindFlags,CurrentLocation,NativeLoopStatus,NativeLoopReason,LastError;public Vector3 Start,End,Position;public bool Active,Lost,PositionChanged;public float ActualMovedMeters;public CreatedHuman(uint c,ushort i,int s,string n,string a,ushort ix,Vector3 p,Vector3 e,int maxTransitions){CitizenId=c;InstanceId=i;SourceId=s;InfoName=n;AIType=a;InfoIndex=ix;State="path_started";PathId=0;PreviousPathId=0;Flags="";InstanceFlags="";PathUnitFlags="";PathFindFlags="";CurrentLocation="";NativeLoopStatus="registered";NativeLoopReason="";SourceBuilding=0;TargetBuilding=0;ParentInstanceId=0;ReturnCount=0;MaxTransitions=maxTransitions;LastError="";Start=p;End=e;Position=p;Active=true;Lost=false;PositionChanged=false;ActualMovedMeters=0f;}}
 }
}
