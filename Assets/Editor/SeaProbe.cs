using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>What is under the boat dock: which collider a ray straight down meets on the water and on the shore.</summary>
    public static class SeaProbe
    {
        [MenuItem("FlightWorld/Tools/Probe Sea Colliders")]
        public static void Probe()
        {
            var text = new StringBuilder();
            Physics.SyncTransforms();
            var dock = GameObject.Find("Aircraft/Dock/Pontoon");
            var at = dock != null ? dock.transform.position : Vector3.zero;
            foreach (var offset in new[] { new Vector3(30f, 0f, 30f), new Vector3(-60f, 0f, 10f), new Vector3(400f, 0f, 0f), new Vector3(-400f, 0f, 0f) })
            {
                var from = new Vector3(at.x + offset.x, 300f, at.z + offset.z);
                var hits = Physics.RaycastAll(from, Vector3.down, 400f).OrderBy(h => h.distance).ToArray();
                text.AppendLine($"at {from.x:F0},{from.z:F0}: " + (hits.Length == 0 ? "nothing" : string.Join(" | ", hits.Select(h =>
                    $"{Path(h.collider.transform)} ({h.collider.GetType().Name}, layer {h.collider.gameObject.layer}, trigger {h.collider.isTrigger}) y {h.point.y:F2}"))));
            }
            foreach (var rootName in new[] { "City/Sea", "City/Ground", "City/Outer" })
            {
                var root = GameObject.Find(rootName);
                if (root == null) { text.AppendLine(rootName + ": missing"); continue; }
                var colliders = root.GetComponentsInChildren<Collider>(true);
                text.AppendLine($"{rootName}: {root.transform.childCount} children, {colliders.Length} colliders" +
                                (colliders.Length > 0 ? $", e.g. {Path(colliders[0].transform)} bounds y {colliders[0].bounds.min.y:F2}..{colliders[0].bounds.max.y:F2}" : ""));
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/sea_probe.txt", text.ToString());
        }

        static string Path(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
