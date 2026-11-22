using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.CityTools
{
    /// <summary>
    /// The ground's grain: a small tiling grey texture (Tools/citygen/ground_detail.py) that the ground shader
    /// multiplies over the map-rendered ground near the viewer, so sand and tarmac are not flat colour.
    /// "Apply Ground Detail" puts it on every existing ground material without rebuilding the city.
    /// </summary>
    public static class GroundDetail
    {
        const string TexturePath = "Assets/City/Textures/ground_detail.png";

        // metres one repeat of the grain covers (fine, broad), how strongly it shows, and how far the fine layer reaches
        static readonly Vector4 Params = new Vector4(7f, 53f, 0.5f, 700f);

        public static void Apply(Material material)
        {
            if (!material.HasProperty("_DetailMap")) return;
            material.SetTexture("_DetailMap", Texture());
            material.SetVector("_DetailParams", Params);
        }

        static Texture2D Texture()
        {
            if (AssetImporter.GetAtPath(TexturePath) is TextureImporter importer &&
                (importer.sRGBTexture || importer.wrapMode != TextureWrapMode.Repeat || !importer.mipmapEnabled || importer.anisoLevel < 4))
            {
                importer.sRGBTexture = false;                 // it is a multiplier, not a colour: 0.5 must stay 0.5
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.mipmapEnabled = true;
                importer.anisoLevel = 4;                      // it is always seen at a shallow angle
                importer.filterMode = FilterMode.Trilinear;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        }

        [MenuItem("FlightWorld/City/Apply Ground Detail")]
        public static void ApplyToAll()
        {
            var report = new StringBuilder();
            if (Texture() == null) report.AppendLine("FAILED: " + TexturePath + " missing (run Tools/citygen/ground_detail.py)");
            else
            {
                int done = 0;
                foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/City" }))
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                    if (material == null || material.shader == null || material.shader.name != "FlightWorld/CityUnlit") continue;
                    Apply(material);
                    EditorUtility.SetDirty(material);
                    done++;
                }
                AssetDatabase.SaveAssets();
                report.AppendLine($"{done} ground materials given the grain texture");
                report.AppendLine("OK");
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/ground_detail_report.txt", report.ToString());
        }
    }
}
