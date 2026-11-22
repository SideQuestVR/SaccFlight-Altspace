using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// What is in a vehicle prefab before it is ported: size, triangles, materials and their shaders, scripts
    /// (and how many are missing), seats, wheels, animators and their parameters, sounds. The prefab's path is
    /// read from Library/FlightWorld/probe_prefab.txt; the answer goes to Logs/prefab_probe.txt.
    /// </summary>
    public static class PrefabProbe
    {
        static string Path(Transform t, Transform root)
        {
            var parts = new List<string>();
            while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>A component's public settings as text: numbers, switches, and the names of what it points at.</summary>
        static string Values(MonoBehaviour b, int limit)
        {
            var parts = new List<string>();
            foreach (var field in b.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                var value = field.GetValue(b);
                if (value is float || value is int || value is bool || value is string || value is Vector3) parts.Add($"{field.Name}={value}");
                else if (value is UnityEngine.Object o && o != null) parts.Add($"{field.Name}->{o.name}");
                else if (value is Array a && a.Length > 0 && a.Length <= 12)
                    parts.Add($"{field.Name}=[{string.Join(",", a.Cast<object>().Select(x => x is UnityEngine.Object uo ? (uo != null ? uo.name : "null") : Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)))}]");
                else if (value is Array big && big.Length > 12) parts.Add($"{field.Name}=[{big.Length} items]");
                if (parts.Count >= limit) break;
            }
            return string.Join(", ", parts);
        }

        /// <summary>Every canvas of a vehicle in the scene (name in Library/FlightWorld/test_target.txt): where it is and how
        /// many drawn elements it carries. A canvas on a moving vehicle is re-synced every frame, element by element.</summary>
        [MenuItem("FlightWorld/Tools/Probe Canvases")]
        public static void ProbeCanvases()
        {
            var text = new StringBuilder();
            string target = File.ReadAllText("Library/FlightWorld/test_target.txt").Trim();
            var fleet = GameObject.Find("Aircraft");
            var vehicle = fleet != null ? fleet.transform.Find(target) : null;
            if (vehicle == null) { File.WriteAllText("Logs/canvas_probe.txt", "no vehicle " + target); return; }
            int total = 0;
            foreach (var canvas in vehicle.GetComponentsInChildren<Canvas>(true).OrderByDescending(c => c.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length))
            {
                var graphics = canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
                int own = graphics.Count(g => g.GetComponentInParent<Canvas>(true) == canvas);
                total += own;
                text.AppendLine($"{own,4} own ({graphics.Length,4} incl. nested) {(canvas.isRootCanvas ? "root  " : "nested")} " +
                                $"{graphics.Count(g => g is UnityEngine.UI.Text),4} texts  animators {canvas.GetComponentsInChildren<Animator>(true).Length}  {Path(canvas.transform, vehicle)}");
            }
            text.AppendLine($"{total} drawn elements on {vehicle.GetComponentsInChildren<Canvas>(true).Length} canvases");
            File.WriteAllText("Logs/canvas_probe.txt", text.ToString());
        }

        [MenuItem("FlightWorld/Tools/Probe Prefab")]
        public static void Probe()
        {
            var text = new StringBuilder();
            try
            {
                string path = File.ReadAllText("Library/FlightWorld/probe_prefab.txt").Trim();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) throw new Exception("no prefab at " + path);
                var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                try
                {
                    Physics.SyncTransforms();
                    var all = root.GetComponentsInChildren<Transform>(true);
                    text.AppendLine($"{path}: {all.Length} objects ({all.Count(t => t.gameObject.activeInHierarchy)} active)");

                    // size
                    var bounds = new Bounds(root.transform.position, Vector3.zero);
                    bool any = false;
                    foreach (var r in root.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && !(r is ParticleSystemRenderer) && !(r is TrailRenderer)))
                    {
                        if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
                    }
                    text.AppendLine($"visible size {bounds.size.x:F1} x {bounds.size.y:F1} x {bounds.size.z:F1} m, centre {bounds.center}");

                    // triangles
                    long active = 0, total = 0;
                    var meshes = new List<(string name, string holder, int tris, bool on, bool skinned)>();
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh mesh = r is SkinnedMeshRenderer s ? s.sharedMesh : r.TryGetComponent<MeshFilter>(out var f) ? f.sharedMesh : null;
                        if (mesh == null) continue;
                        int tris = 0;
                        for (int i = 0; i < mesh.subMeshCount; i++) tris += (int)(mesh.GetIndexCount(i) / 3);
                        bool on = r.enabled && r.gameObject.activeInHierarchy;
                        total += tris;
                        if (on) active += tris;
                        meshes.Add((mesh.name, r.name, tris, on, r is SkinnedMeshRenderer));
                    }
                    text.AppendLine($"renderers {root.GetComponentsInChildren<Renderer>(true).Length}; triangles {active} drawn as it stands, {total} with everything switched on");
                    foreach (var m in meshes.OrderByDescending(m => m.tris).Take(25))
                        text.AppendLine($"   {m.tris,8} {(m.on ? "on " : "off")} {(m.skinned ? "skinned" : "       ")} {m.holder} (mesh {m.name})");

                    // materials
                    text.AppendLine("materials by shader:");
                    foreach (var group in root.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct()
                                 .GroupBy(m => m.shader != null ? m.shader.name : "(none)").OrderByDescending(g => g.Count()))
                        text.AppendLine($"   {group.Count(),3} {group.Key}: {string.Join(", ", group.Select(m => m.name).Take(8))}");
                    int nullMaterials = root.GetComponentsInChildren<Renderer>(true).Sum(r => r.sharedMaterials.Count(m => m == null));
                    text.AppendLine($"   {nullMaterials} material slots are empty (their material is in a package that was not brought in)");

                    // scripts
                    int missing = all.Sum(t => t.GetComponents<Component>().Count(c => c == null));
                    text.AppendLine($"scripts: {missing} components have no script here; the rest:");
                    foreach (var group in root.GetComponentsInChildren<MonoBehaviour>(true).Where(b => b != null).GroupBy(b => b.GetType().FullName).OrderByDescending(g => g.Count()))
                        text.AppendLine($"   {group.Count(),4} {group.Key}");

                    // physics
                    foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
                        text.AppendLine($"rigidbody on {body.name}: mass {body.mass}, drag {body.linearDamping}, angular {body.angularDamping}, constraints {body.constraints}, kinematic {body.isKinematic}");
                    foreach (var wheel in root.GetComponentsInChildren<WheelCollider>(true))
                        text.AppendLine($"wheel {wheel.name}: at {root.transform.InverseTransformPoint(wheel.transform.position)} radius {wheel.radius} travel {wheel.suspensionDistance} " +
                                        $"spring {wheel.suspensionSpring.spring}/{wheel.suspensionSpring.damper} mass {wheel.mass} active {wheel.gameObject.activeInHierarchy}");
                    var colliders = root.GetComponentsInChildren<Collider>(true);
                    text.AppendLine($"colliders: {colliders.Length} ({colliders.Count(c => c.isTrigger)} triggers); " +
                                    string.Join(", ", colliders.GroupBy(c => c.GetType().Name).Select(g => $"{g.Count()} {g.Key}")));

                    // animators
                    foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                    {
                        var controller = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
                        text.AppendLine($"animator on {animator.name}: {(controller != null ? controller.name : "no controller")}, " +
                                        $"{(controller != null ? controller.layers.Length : 0)} layers; parameters: " +
                                        (controller != null ? string.Join(", ", controller.parameters.Select(p => $"{p.name}({p.type.ToString()[0]})")) : ""));
                    }
                    var sounds = root.GetComponentsInChildren<AudioSource>(true);
                    text.AppendLine($"audio sources: {sounds.Length} ({sounds.Count(a => a.clip == null)} without a clip)");
                    text.AppendLine($"particle systems: {root.GetComponentsInChildren<ParticleSystem>(true).Length}; lights: {root.GetComponentsInChildren<Light>(true).Length}; " +
                                    $"canvases: {root.GetComponentsInChildren<Canvas>(true).Length}; cameras: {root.GetComponentsInChildren<Camera>(true).Length}; " +
                                    $"text (TMP): {root.GetComponentsInChildren<TMPro.TMP_Text>(true).Length}; text (UI): {root.GetComponentsInChildren<UnityEngine.UI.Text>(true).Length}");

                    // details a port needs
                    text.AppendLine("solid colliders:");
                    foreach (var c in colliders.Where(c => !c.isTrigger && !(c is WheelCollider)))
                        text.AppendLine($"   {c.GetType().Name} on {Path(c.transform, root.transform)}: size {c.bounds.size}, local centre {root.transform.InverseTransformPoint(c.bounds.center)}, active {c.gameObject.activeInHierarchy}, enabled {c.enabled}, layer {c.gameObject.layer}");
                    text.AppendLine("empty material slots on: " + string.Join("; ", root.GetComponentsInChildren<Renderer>(true).Where(r => r.sharedMaterials.Any(m => m == null))
                        .Select(r => Path(r.transform, root.transform) + (r.gameObject.activeInHierarchy ? "" : " (off)"))));
                    text.AppendLine("seats:");
                    var seatList = root.GetComponentsInChildren<MonoBehaviour>(true).Where(b => b != null && b.GetType().Name == "SaccVehicleSeat").ToList();
                    foreach (var seat in seatList.Take(14).Concat(seatList.Skip(Math.Max(14, seatList.Count - 4))))
                        text.AppendLine($"   {Path(seat.transform, root.transform)} at {root.transform.InverseTransformPoint(seat.transform.position)} " + Values(seat, 6));
                    text.AppendLine($"   ({seatList.Count} in all; pilot seats: {seatList.Count(s => s.GetType().GetField("IsPilotSeat")?.GetValue(s) is bool b && b)})");
                    foreach (var typeName in new[] { "SFEXT_a320_AdvancedEngine", "DFUNC_a320_FlapController", "DFUNC_a320_Brake", "SFEXT_a320_AdvancedGear", "DFUNC_Gear", "DFUNC_a320_AdvancedThrustReverser",
                                                     "DFUNC_a320_ElevatorTrim", "SFEXT_a320_EngineFanDriver", "SaccEntity", "SAV_EffectsController", "DFUNC_Canopy", "AutoBrake" })
                        foreach (var b in root.GetComponentsInChildren<MonoBehaviour>(true).Where(b => b != null && b.GetType().Name == typeName).Take(2))
                            text.AppendLine($"{typeName} on {Path(b.transform, root.transform)}: " + Values(b, 60));
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh mesh = r is SkinnedMeshRenderer s2 ? s2.sharedMesh : r.TryGetComponent<MeshFilter>(out var f2) ? f2.sharedMesh : null;
                        if (mesh != null && mesh.triangles.Length / 3 > 2500)
                            text.AppendLine($"big mesh {Path(r.transform, root.transform)}: materials {string.Join(", ", r.sharedMaterials.Select(m => m != null ? m.name : "(none)"))}, bounds size {r.bounds.size}");
                    }
                    text.AppendLine("root local axes: forward " + root.transform.forward + ", prefab root rotation " + root.transform.eulerAngles);

                    // the first two levels of the hierarchy
                    text.AppendLine("hierarchy:");
                    foreach (Transform child in root.transform)
                    {
                        text.AppendLine($"   {child.name}{(child.gameObject.activeSelf ? "" : " (off)")}: {child.GetComponentsInChildren<Transform>(true).Length} objects");
                        foreach (Transform grand in child)
                            text.AppendLine($"      {grand.name}{(grand.gameObject.activeSelf ? "" : " (off)")}: {grand.GetComponentsInChildren<Transform>(true).Length}");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
                text.AppendLine("OK");
            }
            catch (Exception e)
            {
                text.AppendLine("FAILED: " + e);
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/prefab_probe.txt", text.ToString());
        }
    }
}
