using System;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    // A reversible native terrain material theme with an authored concrete texture,
    // not a flat image, mesh overlay or a terrain-height modification.
    internal static class UrbanTerrainAppearance
    {
        private static TerrainProperties properties;
        private static Texture2D originalGrass;
        private static Texture2D originalPavement,concrete;
        private static DateTime textureStamp=DateTime.MinValue;
        private static float nextTextureCheck;
        private const string TexturePath=@"H:\CS1-PLATEAU\nagoya-station\simulation\urban-concrete-diffuse.png";
        public static string TextureSHA256="";
        public static string TextureError="";
        private static bool originalDecorations;
        private static Vector3 pollution,field,fertility,forest;
        public static bool Active { get { return properties!=null && properties.m_grassDiffuse==properties.m_pavementDiffuse && !properties.m_useGrassDecorations; } }
        public static string TextureName { get { return properties==null||properties.m_grassDiffuse==null?"":properties.m_grassDiffuse.name; } }

        private static void ApplyShaderProperties(TerrainProperties p)
        {
            MethodInfo method=typeof(TerrainProperties).GetMethod("InitializeShaderProperties",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            if(method==null)throw new MissingMethodException("Verified TerrainProperties.InitializeShaderProperties missing");
            method.Invoke(p,null);
        }
        public static void Install()
        {
            if(Active)return;
            TerrainProperties p=TerrainManager.instance.m_properties;
            if(p==null||p.m_pavementDiffuse==null||p.m_grassDiffuse==null)throw new InvalidOperationException("Native terrain pavement material unavailable");
            properties=p;originalGrass=p.m_grassDiffuse;originalPavement=p.m_pavementDiffuse;originalDecorations=p.m_useGrassDecorations;
            pollution=p.m_grassPollutionColorOffset;field=p.m_grassFieldColorOffset;fertility=p.m_grassFertilityColorOffset;forest=p.m_grassForestColorOffset;
            p.m_useGrassDecorations=false;
            p.m_grassPollutionColorOffset=Vector3.zero;p.m_grassFieldColorOffset=Vector3.zero;p.m_grassFertilityColorOffset=Vector3.zero;p.m_grassForestColorOffset=Vector3.zero;
            LoadConcrete(p);
        }
        private static void LoadConcrete(TerrainProperties p)
        {
            byte[] data=File.ReadAllBytes(TexturePath);if(data.Length<64||data.Length>1024*1024)throw new FormatException("Concrete texture byte budget");
            Texture2D candidate=new Texture2D(2,2,TextureFormat.RGB24,true);
            try
            {
                if(!candidate.LoadImage(data)||candidate.width!=512||candidate.height!=512)throw new FormatException("Expected512px concrete texture");
                Color32[] pixels=candidate.GetPixels32();foreach(Color32 pixel in pixels)if(pixel.a!=255)throw new FormatException("Opaque concrete texture required");
                candidate.name="NagoyaNeutralGrayConcrete";candidate.wrapMode=TextureWrapMode.Repeat;candidate.filterMode=FilterMode.Trilinear;candidate.anisoLevel=4;
                Texture2D old=concrete;concrete=candidate;p.m_grassDiffuse=concrete;p.m_pavementDiffuse=concrete;
                ApplyShaderProperties(p);using(SHA256 sha=SHA256.Create())TextureSHA256=BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();
                textureStamp=File.GetLastWriteTimeUtc(TexturePath);TextureError="";if(old!=null)UnityEngine.Object.Destroy(old);
            }
            catch{if(candidate!=concrete)UnityEngine.Object.Destroy(candidate);throw;}
        }
        public static void Refresh()
        {
            if(properties==null||Time.realtimeSinceStartup<nextTextureCheck)return;nextTextureCheck=Time.realtimeSinceStartup+1;
            try{if(File.GetLastWriteTimeUtc(TexturePath)!=textureStamp)LoadConcrete(properties);}catch(Exception error){TextureError=error.Message;}
        }
        public static void Clear()
        {
            if(properties==null)return;
            TerrainProperties p=properties;properties=null;
            p.m_grassDiffuse=originalGrass;p.m_pavementDiffuse=originalPavement;p.m_useGrassDecorations=originalDecorations;
            p.m_grassPollutionColorOffset=pollution;p.m_grassFieldColorOffset=field;p.m_grassFertilityColorOffset=fertility;p.m_grassForestColorOffset=forest;
            ApplyShaderProperties(p);originalGrass=null;originalPavement=null;if(concrete!=null)UnityEngine.Object.Destroy(concrete);concrete=null;TextureSHA256="";TextureError="";textureStamp=DateTime.MinValue;
        }
    }
}
