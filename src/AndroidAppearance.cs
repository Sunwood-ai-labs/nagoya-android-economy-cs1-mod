using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NagoyaRoadDelivery {
 public sealed partial class CitywideScene {
  private string androidManifest=RuntimePaths.Data(@"android-finish-v1\manifest.json");
  private string androidAppearanceError="",androidAppearanceSignature="";
  private DateTime androidAppearanceStamp=DateTime.MinValue;
  private float nextAndroidAppearance;
  private struct AndroidBinding {public MeshFilter Filter;public MeshRenderer Renderer;public Transform Transform;public Mesh Mesh;public Vector3 Pivot;}
  private static Bounds ActorBounds(AndroidAssetData asset) {
   bool first=true;Bounds result=new Bounds();
   foreach(AndroidPartData p in asset.Parts)if(p.Name!="parcel")for(int i=0;i<p.Vertices.Length;i+=3){
    Vector3 v=new Vector3(p.Vertices[i]+p.Pivot[0],p.Vertices[i+1]+p.Pivot[1],p.Vertices[i+2]+p.Pivot[2]);
    if(first){result=new Bounds(v,Vector3.zero);first=false;}else result.Encapsulate(v);
   }
   if(first)throw new FormatException("Empty actor bounds");return result;
  }
  private void ApplyAndroidAppearance() {
   if(android==null||actors.Count==0||Time.realtimeSinceStartup<nextAndroidAppearance)return;
   nextAndroidAppearance=Time.realtimeSinceStartup+.5f;
   string file=Path.Combine(Root,"android-visual.json");if(!File.Exists(file))return;
   DateTime stamp=File.GetLastWriteTimeUtc(file);if(stamp==androidAppearanceStamp)return;androidAppearanceStamp=stamp;
   List<Mesh> candidateMeshes=new List<Mesh>();Material candidateMaterial=null;Texture2D candidateTexture=null;
   try {
    if(new FileInfo(file).Length>4096)throw new FormatException("Android appearance file size");
    byte[] configBytes=File.ReadAllBytes(file);Dictionary<string,object> cfg=Map(JsonData.Parse(Encoding.UTF8.GetString(configBytes)));
    string manifest=Text(cfg,"manifest");
    AndroidAssetData candidate=AndroidObjAsset.Load(manifest,RuntimePaths.DataRoot);
    string signature=Hash(configBytes)+"|"+candidate.Sha256;if(signature==androidAppearanceSignature)return;
    Bounds before=ActorBounds(android),after=ActorBounds(candidate);
    if(after.min.z<before.min.z-.001f||after.max.z>before.max.z+.001f||after.min.y<-.001f||after.max.y>before.max.y+.03f)
     throw new FormatException("Candidate changes dock depth, feet or height beyond current envelope");
    foreach(AndroidPartData p in candidate.Parts){
     AndroidPartData old=null;foreach(AndroidPartData q in android.Parts)if(q.Name==p.Name)old=q;
     if(old==null)throw new FormatException("Candidate actor part mismatch");
     for(int i=0;i<3;i++)if(Math.Abs(old.Pivot[i]-p.Pivot[i])>.00001f)throw new FormatException("Candidate changes animation pivot");
    }
    Dictionary<string,string> profile=new Dictionary<string,string>();object value;
    profile["materialMode.android"]=cfg.TryGetValue("materialMode",out value)?(string)value:"diffuse";
    if(cfg.TryGetValue("shader",out value))profile["materialShader.android"]=(string)value;
    foreach(string key in new string[]{"gloss","opacity","specular"})if(cfg.TryGetValue(key,out value)){
     if(!(value is double))throw new FormatException("Numeric Android material profile required");
     string target=key=="gloss"?"materialGloss.android":key=="opacity"?"materialOpacity.android":"materialSpecular.android";
     profile[target]=((double)value).ToString("R",CultureInfo.InvariantCulture);
    }
    candidateTexture=new Texture2D(2,2,TextureFormat.RGBA32,true);
    if(!candidateTexture.LoadImage(candidate.Texture))throw new FormatException("Candidate texture decode");
    candidateTexture.wrapMode=TextureWrapMode.Clamp;candidateTexture.filterMode=FilterMode.Trilinear;candidateTexture.anisoLevel=4;
    candidateMaterial=new Material(Shader.Find("Legacy Shaders/Diffuse"));candidateMaterial.mainTexture=candidateTexture;
    string materialError=NagoyaRuntimeVisuals.RuntimeMaterialProfiles.Apply(candidateMaterial,"android",profile,1);
    if(materialError.Length>0)throw new InvalidOperationException(materialError);
    Dictionary<string,Mesh> built=new Dictionary<string,Mesh>();
    foreach(AndroidPartData p in candidate.Parts){Mesh mesh=PartMesh(p);candidateMeshes.Add(mesh);built.Add(p.Name,mesh);}
    List<AndroidBinding> bindings=new List<AndroidBinding>();
    foreach(Actor actor in actors)if(actor.Kind=="android")foreach(AndroidPartData p in candidate.Parts){
     Transform child=actor.Root.transform.Find(p.Name);if(child==null)throw new InvalidOperationException("Live actor part missing: "+p.Name);
     MeshFilter filter=child.GetComponent<MeshFilter>();MeshRenderer renderer=child.GetComponent<MeshRenderer>();
     if(filter==null||renderer==null)throw new InvalidOperationException("Live actor mesh component missing");
     bindings.Add(new AndroidBinding{Filter=filter,Renderer=renderer,Transform=child,Mesh=built[p.Name],Pivot=new Vector3(p.Pivot[0],p.Pivot[1],p.Pivot[2])});
    }
    if(bindings.Count!=56*4)throw new InvalidOperationException("Expected 56 complete Android bindings");
    // All input and bindings are validated before any live actor is changed.
    foreach(AndroidBinding binding in bindings){binding.Filter.sharedMesh=binding.Mesh;binding.Renderer.sharedMaterial=candidateMaterial;binding.Transform.localPosition=binding.Pivot;}
    Material previousMaterial=androidMaterial;Texture previousTexture=androidMaterial.mainTexture;
    foreach(Mesh mesh in androidMeshes.Values){ownedMeshes.Remove(mesh);Destroy(mesh);}androidMeshes.Clear();
    foreach(KeyValuePair<string,Mesh> pair in built)androidMeshes.Add(pair.Key,pair.Value);
    android=candidate;androidMaterial=candidateMaterial;ownedMaterials.Add(candidateMaterial);ownedTextures.Add(candidateTexture);
    NagoyaRuntimeVisuals.RuntimeMaterialProfiles.Forget(previousMaterial,previousTexture);ownedMaterials.Remove(previousMaterial);Destroy(previousMaterial);
    Texture2D oldTexture=previousTexture as Texture2D;if(oldTexture!=null){ownedTextures.Remove(oldTexture);Destroy(oldTexture);}
    androidManifest=manifest;androidAppearanceSignature=signature;androidAppearanceError="";
    candidateMeshes.Clear();candidateMaterial=null;candidateTexture=null;
   }catch(Exception ex){
    androidAppearanceError=ex.Message;
    foreach(Mesh mesh in candidateMeshes){ownedMeshes.Remove(mesh);Destroy(mesh);}
    if(candidateMaterial!=null){NagoyaRuntimeVisuals.RuntimeMaterialProfiles.Forget(candidateMaterial,candidateTexture);Destroy(candidateMaterial);}
    if(candidateTexture!=null)Destroy(candidateTexture);
   }
  }
 }
}
