using System.IO;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.CityTools
{
    /// <summary>
    /// Import settings for everything the city pipeline writes into Assets/City/Generated.
    /// Applied automatically on import, and re-applied by the scene builder for assets that were
    /// imported before this script existed.
    /// </summary>
    public class CityImportSettings : AssetPostprocessor
    {
        public const string Root = "Assets/City/Generated/";
        public const string MeshDir = "Assets/City/Generated/Meshes";
        public const string GroundDir = "Assets/City/Generated/Ground";
        public const string FacadePath = "Assets/City/Generated/Facades/facades_array.png";
        public const string WaterTintPath = "Assets/City/Generated/Ground/watertint.png";

        void OnPreprocessModel()
        {
            if (assetPath.StartsWith(Root)) ApplyModel((ModelImporter)assetImporter);
        }

        void OnPreprocessTexture()
        {
            if (assetPath.StartsWith(Root)) ApplyTexture((TextureImporter)assetImporter, assetPath);
        }

        /// <summary>Returns true when the importer already had every setting.</summary>
        public static bool ApplyModel(ModelImporter mi)
        {
            bool ok = mi.materialImportMode == ModelImporterMaterialImportMode.None
                      && !mi.isReadable && !mi.importAnimation && !mi.importCameras && !mi.importLights
                      && mi.animationType == ModelImporterAnimationType.None
                      && mi.importTangents == ModelImporterTangents.None
                      && mi.meshCompression == ModelImporterMeshCompression.Off
                      && !mi.generateSecondaryUV && !mi.importBlendShapes;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.isReadable = false;
            mi.importAnimation = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importVisibility = false;
            mi.importBlendShapes = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.None;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.generateSecondaryUV = false;
            mi.useFileScale = true;
            mi.globalScale = 1f;
            mi.weldVertices = true;
            return ok;
        }

        /// <summary>Returns true when the importer already had every setting.</summary>
        public static bool ApplyTexture(TextureImporter ti, string path)
        {
            bool facade = path == FacadePath;
            var wantShape = facade ? TextureImporterShape.Texture2DArray : TextureImporterShape.Texture2D;
            var wantWrap = facade ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            var android = ti.GetPlatformTextureSettings("Android");
            var wantFormat = facade ? TextureImporterFormat.ASTC_4x4 : TextureImporterFormat.ASTC_6x6;
            var settings = new TextureImporterSettings();
            ti.ReadTextureSettings(settings);
            bool ok = ti.textureShape == wantShape && ti.wrapMode == wantWrap && ti.mipmapEnabled
                      && ti.anisoLevel == 4 && ti.maxTextureSize == 2048 && !ti.alphaIsTransparency
                      && android.overridden && android.format == wantFormat
                      && (!facade || (settings.flipbookColumns == 4 && settings.flipbookRows == 2));

            ti.textureType = TextureImporterType.Default;
            if (facade)
            {
                // The facade sheet is a 4 x 2 grid of styles that becomes an 8-layer texture array.
                settings.textureShape = wantShape;
                settings.flipbookColumns = 4;
                settings.flipbookRows = 2;
                ti.SetTextureSettings(settings);
            }
            ti.textureShape = wantShape;
            ti.sRGBTexture = true;
            ti.alphaIsTransparency = false;
            ti.alphaSource = facade ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            ti.mipmapEnabled = true;
            ti.wrapMode = wantWrap;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4;
            ti.maxTextureSize = 2048;
            ti.isReadable = false;
            ti.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Android", overridden = true, maxTextureSize = 2048, format = wantFormat,
            });
            return ok;
        }

        /// <summary>Re-import any generated asset whose settings differ (e.g. imported before this script).</summary>
        public static int EnsureAll()
        {
            int changed = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { MeshDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is ModelImporter mi && !ApplyModel(mi))
                {
                    mi.SaveAndReimport();
                    changed++;
                }
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Texture", new[] { Root.TrimEnd('/') }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is TextureImporter ti && !ApplyTexture(ti, path))
                {
                    ti.SaveAndReimport();
                    changed++;
                }
            }
            return changed;
        }
    }
}
