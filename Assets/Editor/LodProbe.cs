using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Which vehicle meshes carry Unity's automatic Mesh LODs, and how many triangles each level has:
    /// Logs/lod_probe.txt, one block per vehicle, renderers sorted by size.
    /// </summary>
    public static class LodProbe
    {
        [MenuItem("FlightWorld/Tools/Probe Vehicle LODs")]
        public static void Probe()
        {
            var text = new StringBuilder();
            foreach (var rootName in new[] { "Aircraft", "GroundVehicles" })
            {
                var root = GameObject.Find(rootName);
                if (root == null) continue;
                foreach (Transform vehicle in root.transform)
                {
                    var renderers = vehicle.GetComponentsInChildren<Renderer>(true)
                        .Select(r => (r, mesh: r is SkinnedMeshRenderer s ? s.sharedMesh : r.TryGetComponent<MeshFilter>(out var f) ? f.sharedMesh : null))
                        .Where(x => x.mesh != null).ToList();
                    if (renderers.Count == 0) continue;
                    long all = renderers.Sum(x => Triangles(x.mesh, 0));
                    long withLods = renderers.Where(x => x.mesh.lodCount > 1).Sum(x => Triangles(x.mesh, 0));
                    text.AppendLine($"== {vehicle.name}: {renderers.Count} mesh renderers, {all} triangles at full detail, {withLods} of them in meshes that have LODs");
                    foreach (var (r, mesh) in renderers.OrderByDescending(x => Triangles(x.mesh, 0)).Take(6))
                    {
                        string levels = string.Join(" / ", Enumerable.Range(0, Mathf.Max(1, mesh.lodCount)).Select(level => Triangles(mesh, level).ToString()));
                        text.AppendLine($"   {r.GetType().Name,-20} {r.name,-28} mesh '{mesh.name}' lods {mesh.lodCount}: {levels}" +
                                        $"  bias {r.meshLodSelectionBias} force {r.forceMeshLod} active {r.gameObject.activeInHierarchy}");
                    }
                }
            }
            text.AppendLine($"QualitySettings.meshLodThreshold (editor) = {QualitySettings.meshLodThreshold}");
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/lod_probe.txt", text.ToString());
        }

        static long Triangles(Mesh mesh, int level)
        {
            long total = 0;
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
                total += (mesh.lodCount > 1 ? mesh.GetLod(sub, level).indexCount : (int)mesh.GetIndexCount(sub)) / 3;
            return total;
        }
    }
}
