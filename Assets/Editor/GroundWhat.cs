using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>What is under a point on the apron: every collider a ray straight down passes through, with its height there.</summary>
    public static class GroundWhat
    {
        [MenuItem("FlightWorld/Tools/Probe What Is Under The Apron")]
        public static void Probe()
        {
            var text = new StringBuilder();
            Physics.SyncTransforms();
            foreach (var at in new[] { new Vector3(14767.33f, 60f, 7217.53f), new Vector3(14772.53f, 60f, 7226.07f), new Vector3(14804.72f, 60f, 7042.08f) })
            {
                text.AppendLine($"under ({at.x:F2}, {at.z:F2}):");
                foreach (var hit in Physics.RaycastAll(at, Vector3.down, 200f, ~0, QueryTriggerInteraction.Collide).OrderBy(h => h.distance))
                    text.AppendLine($"   y {hit.point.y:F4}  {hit.collider.GetType().Name} '{hit.collider.name}' layer {hit.collider.gameObject.layer} trigger {hit.collider.isTrigger} " +
                                    $"bounds size {hit.collider.bounds.size} normal {hit.normal}");
            }
            foreach (var c in Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Where(c => c.bounds.size.x > 3000f))
                text.AppendLine($"large mesh collider '{c.name}': bounds centre {c.bounds.center} size {c.bounds.size}, triangles {c.sharedMesh.triangles.Length / 3}, convex {c.convex}");
            File.WriteAllText("Logs/ground_what.txt", text.ToString());
        }
    }
}
