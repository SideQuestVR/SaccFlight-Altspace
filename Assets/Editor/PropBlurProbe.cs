using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// A look, not a change: shows every propeller blur disc in the open scene as it is at full engine speed (the
    /// engine animation fades them in), WITHOUT saving, so a picture can be taken from a cockpit. "Test: Reload Scene
    /// From Disk" puts the scene back. Lists what it switched on in Logs/prop_blur_probe.txt.
    /// </summary>
    public static class PropBlurProbe
    {
        [MenuItem("FlightWorld/Tools/Test: Show Propeller Blur (unsaved)")]
        public static void Show()
        {
            var report = new StringBuilder();
            foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var material = renderer.sharedMaterial;
                if (material == null) continue;
                string name = material.name.ToLowerInvariant();
                if (!name.StartsWith("propblur") && !name.StartsWith("spprop")) continue;
                for (var t = renderer.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
                renderer.enabled = true;
                var block = new MaterialPropertyBlock();
                var colour = material.GetColor("_Color");
                colour.a = 1f;
                block.SetColor("_Color", colour);
                renderer.SetPropertyBlock(block);
                report.AppendLine($"{renderer.transform.root.name}/{renderer.name}: {material.name} on {material.shader.name}");
            }
            File.WriteAllText("Logs/prop_blur_probe.txt", report.ToString() + "OK\n");
        }
    }
}
