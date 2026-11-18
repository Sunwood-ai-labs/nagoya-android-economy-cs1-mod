using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace NagoyaRuntimeVisuals {
 public static class RuntimeMaterialProfiles {
  private static readonly string[] Modes={"diffuse","specular","transparent","emissive"};
  private static readonly string[] Shaders={"Legacy Shaders/Diffuse","Legacy Shaders/Specular","Legacy Shaders/Transparent/Diffuse","Legacy Shaders/Self-Illumin/Diffuse"};
  private static readonly Dictionary<int,string> reports=new Dictionary<int,string>();
  private struct AlphaRange { public int Min,Max; }
  private static readonly Dictionary<int,AlphaRange> alpha=new Dictionary<int,AlphaRange>();
  private static readonly Dictionary<int,Texture2D> specMaps=new Dictionary<int,Texture2D>();
  private static string catalog="";
  private static string ColorKey(Material m){return m.HasProperty("_Color")?"_Color":m.HasProperty("_TintColor")?"_TintColor":null;}
  public static float Gain(Material m){string key=ColorKey(m);return key==null?1:m.GetColor(key).r;}
  private static string Esc(string s){return (s??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n"," ").Replace("\r"," ");}
  private static string Number(float n){return n.ToString("R",CultureInfo.InvariantCulture);}
  private static float Value(Dictionary<string,string> cfg,string key,float fallback,float low,float high) {
   string text;if(!cfg.TryGetValue(key,out text))return fallback;
   float value;if(!float.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)||float.IsNaN(value)||float.IsInfinity(value)||value<low||value>high)throw new FormatException("Invalid material value "+key);
   return value;
  }
  public static string Apply(Material material,string asset,Dictionary<string,string> cfg,float gain) {
   Shader previous=material.shader;Texture texture=material.mainTexture;string mode="diffuse",error="";
   try {
    string value;if(cfg.TryGetValue("materialMode."+asset,out value))mode=value;
    int index=Array.IndexOf(Modes,mode);if(index<0)throw new FormatException("Unknown material mode "+mode);
    float gloss=Value(cfg,"materialGloss."+asset,.15f,.01f,1f),opacity=Value(cfg,"materialOpacity."+asset,1,0,1),specular=Value(cfg,"materialSpecular."+asset,.22f,0,1);
    if(gain<.2f||gain>1.5f||float.IsNaN(gain))throw new FormatException("Material gain bounds");
    string selectedName=Shaders[index];if(cfg.TryGetValue("materialShader."+asset,out value))selectedName=value;
    if(selectedName.Length>160||selectedName.IndexOf("InternalErrorShader",StringComparison.OrdinalIgnoreCase)>=0)throw new FormatException("Invalid shader override");
    Shader selected=Shader.Find(selectedName);if(selected==null||!selected.isSupported)throw new InvalidOperationException("Requested shader unavailable/unsupported: "+selectedName);
    material.shader=selected;if(material.HasProperty("_MainTex"))material.mainTexture=texture;
    string colorKey=ColorKey(material);Color tint=new Color(gain,gain,gain,opacity);
    if(!material.HasProperty("_MainTex")){Texture2D solid=texture as Texture2D;if(solid==null||solid.width>128||solid.height>128)throw new InvalidOperationException("Shader cannot carry a detailed albedo");Color pixel=solid.GetPixel(0,0);tint=new Color(pixel.r*gain,pixel.g*gain,pixel.b*gain,pixel.a*opacity);}
    if(colorKey!=null)material.SetColor(colorKey,tint);
    if(mode=="specular") {
     if(material.HasProperty("_Shininess"))material.SetFloat("_Shininess",gloss);
     else if(material.HasProperty("_Glossiness"))material.SetFloat("_Glossiness",gloss);
     else if(!material.HasProperty("_XYSMap"))throw new InvalidOperationException("Shader has no known specular controls");
     if(material.HasProperty("_SpecColor"))material.SetColor("_SpecColor",new Color(specular,specular,specular,1));
     if(material.HasProperty("_XYSMap")){Texture2D input=texture as Texture2D;Texture2D map;int key=input.GetInstanceID();if(!specMaps.TryGetValue(key,out map)){Color32[] pixels=input.GetPixels32();for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32(128,128,pixels[i].a,255);map=new Texture2D(input.width,input.height,TextureFormat.RGBA32,true,true);map.SetPixels32(pixels);map.Apply(true,false);map.wrapMode=TextureWrapMode.Clamp;specMaps[key]=map;}material.SetTexture("_XYSMap",map);}
    }
    if(mode=="emissive") {
     if(material.HasProperty("_Illum"))material.SetTexture("_Illum",Texture2D.whiteTexture);
     else if(material.HasProperty("_EmissionColor")){material.SetColor("_EmissionColor",Color.white);if(material.HasProperty("_EmissionMap"))material.SetTexture("_EmissionMap",texture);material.EnableKeyword("_EMISSION");}
     else if(material.HasProperty("_ACIMap"))material.SetTexture("_ACIMap",Texture2D.whiteTexture);
     else if(selected.name.IndexOf("Unlit",StringComparison.OrdinalIgnoreCase)<0&&selected.name.IndexOf("Sprite",StringComparison.OrdinalIgnoreCase)<0&&selected.name.IndexOf("UI/",StringComparison.OrdinalIgnoreCase)<0)throw new InvalidOperationException("Shader has no known unlit/emission mode");
    }
    if(mode=="transparent"){if(material.HasProperty("_SrcBlend")&&material.HasProperty("_DstBlend")){material.SetInt("_SrcBlend",5);material.SetInt("_DstBlend",10);if(material.HasProperty("_ZWrite"))material.SetInt("_ZWrite",0);if(material.HasProperty("_Mode"))material.SetFloat("_Mode",2);material.EnableKeyword("_ALPHABLEND_ON");material.DisableKeyword("_ALPHATEST_ON");material.renderQueue=3000;}if(material.renderQueue<2500)throw new InvalidOperationException("Shader has no transparent queue/blend controls");}
   }catch(Exception ex){
    error=ex.Message;material.shader=previous;material.mainTexture=texture;
    if(material.HasProperty("_Color"))material.color=new Color(gain,gain,gain,1);
   }
   if(mode!="diffuse"||asset=="android"||error.Length>0) {
    int min=255,max=255;Texture2D t=texture as Texture2D;
    if(t!=null&&(mode=="transparent"||mode=="specular")) {
     AlphaRange range;int key=t.GetInstanceID();
     if(!alpha.TryGetValue(key,out range)){
      Color32[] pixels=t.GetPixels32();range=new AlphaRange{Min=255,Max=0};
      for(int i=0;i<pixels.Length;i++){range.Min=Math.Min(range.Min,pixels[i].a);range.Max=Math.Max(range.Max,pixels[i].a);}
      if(alpha.Count>64)alpha.Clear();alpha[key]=range;
     }
     min=range.Min;max=range.Max;
    }
    reports[material.GetInstanceID()]="{\"asset\":\""+Esc(asset)+"\",\"requestedMode\":\""+Esc(mode)+"\",\"actualShader\":\""+Esc(material.shader.name)+"\",\"renderQueue\":"+material.renderQueue+",\"colorAlpha\":"+Number(ColorKey(material)!=null?material.GetColor(ColorKey(material)).a:1)+",\"textureAlphaMin\":"+min+",\"textureAlphaMax\":"+max+",\"error\":\""+Esc(error)+"\"}";
   }
   return error;
  }
  public static void Forget(Material material,Texture texture){if(material!=null)reports.Remove(material.GetInstanceID());if(texture!=null){int key=texture.GetInstanceID();alpha.Remove(key);Texture2D map;if(specMaps.TryGetValue(key,out map)){UnityEngine.Object.Destroy(map);specMaps.Remove(key);}}}
  public static void Clear(){reports.Clear();alpha.Clear();foreach(Texture2D map in specMaps.Values)UnityEngine.Object.Destroy(map);specMaps.Clear();}

  private static string Catalog() {
   if(catalog.Length>0)return catalog;
   List<Shader> shaders=new List<Shader>(Resources.FindObjectsOfTypeAll<Shader>());
   foreach(string name in new string[]{"Standard","Standard (Specular setup)","Sprites/Default","UI/Default","Unlit/Texture","Unlit/Transparent","Hidden/Internal-Colored","Particles/Alpha Blended","Custom/Particles/Alpha Blended","Specular","Transparent/Diffuse","Self-Illumin/Diffuse"}){Shader shader=Shader.Find(name);if(shader!=null&&!shaders.Contains(shader))shaders.Add(shader);}
   shaders.Sort(delegate(Shader a,Shader b){return string.CompareOrdinal(a.name,b.name);});
   StringBuilder json=new StringBuilder("[");bool first=true;
   foreach(Shader shader in shaders){
    if(!first)json.Append(',');first=false;Material probe=new Material(shader);
    json.Append("{\"name\":\"").Append(Esc(shader.name)).Append("\",\"supported\":").Append(shader.isSupported?"true":"false").Append(",\"queue\":").Append(probe.renderQueue).Append(",\"renderType\":\"").Append(Esc(probe.GetTag("RenderType",false,""))).Append("\",\"properties\":[");bool f=true;
    foreach(string property in new string[]{"_MainTex","_Color","_TintColor","_Shininess","_SpecColor","_Glossiness","_Metallic","_XYSMap","_ACIMap","_Illum","_EmissionColor","_EmissionMap","_SrcBlend","_DstBlend","_ZWrite","_Mode"})if(probe.HasProperty(property)){if(!f)json.Append(',');f=false;json.Append('"').Append(property).Append('"');}
    json.Append("]}");UnityEngine.Object.Destroy(probe);
   }
   json.Append(']');catalog=json.ToString();return catalog;
  }

  public static string StateJson() {
   StringBuilder s=new StringBuilder("{\"available\":[");
   for(int i=0;i<Modes.Length;i++){if(i>0)s.Append(',');s.Append("{\"mode\":\"").Append(Modes[i]).Append("\",\"shader\":\"").Append(Shaders[i]).Append("\",\"found\":").Append(Shader.Find(Shaders[i])!=null?"true":"false").Append('}');}
   s.Append("],\"catalog\":").Append(Catalog()).Append(",\"materials\":[");bool first=true;foreach(string row in reports.Values){if(!first)s.Append(',');first=false;s.Append(row);}s.Append("]}");return s.ToString();
  }
 }
}
