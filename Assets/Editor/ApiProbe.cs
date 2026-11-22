using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld
{
    /// <summary>One-off inspection tools: what a Sacc vehicle prefab contains, and what Unity's Mesh LOD does.</summary>
    public static class ApiProbe
    {
        /// <summary>
        /// Writes Logs/sacc_prefab_<name>.txt for each vehicle prefab: the hierarchy with every component, and
        /// the serialized settings of the Sacc stand-in components (object references shown as scene paths).
        /// </summary>
        [MenuItem("FlightWorld/Tools/Dump Sacc Prefabs")]
        public static void DumpSaccPrefabs()
        {
            Directory.CreateDirectory("Logs/sacc");
            var index = new StringBuilder();
            foreach (var path in Directory.GetFiles("Assets/Sacc/Prefabs", "*.prefab").Select(p => p.Replace("\\", "/")))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { index.AppendLine(path + ": could not load"); continue; }
                var text = new StringBuilder();
                var all = prefab.GetComponentsInChildren<Transform>(true);
                int renderers = prefab.GetComponentsInChildren<Renderer>(true).Length;
                long tris = prefab.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).Sum(f => (long)f.sharedMesh.triangles.Length / 3)
                            + prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(f => f.sharedMesh != null).Sum(f => (long)f.sharedMesh.triangles.Length / 3);
                var kinds = prefab.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null).GroupBy(m => m.GetType().Name)
                    .OrderByDescending(g => g.Count()).Select(g => g.Key + (g.Count() > 1 ? " x" + g.Count() : ""));
                int missing = all.Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
                index.AppendLine($"{Path.GetFileNameWithoutExtension(path)}: {all.Length} objects, {renderers} renderers, {tris} triangles, " +
                                 $"{prefab.GetComponentsInChildren<WheelCollider>(true).Length} wheels, {prefab.GetComponentsInChildren<Rigidbody>(true).Length} bodies, " +
                                 $"{missing} missing scripts | " + string.Join(", ", kinds));

                foreach (var t in all)
                {
                    int depth = 0;
                    for (var p = t; p != prefab.transform; p = p.parent) depth++;
                    var components = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name);
                    int gone = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                    text.AppendLine(new string(' ', depth * 2) + t.name + (t.gameObject.activeSelf ? "" : " (off)") +
                                    "  [" + string.Join(", ", components) + (gone > 0 ? $", missing x{gone}" : "") + "]" +
                                    (depth <= 2 ? "  @" + t.localPosition.ToString("F2") : ""));
                }
                text.AppendLine();
                foreach (var behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().Namespace == "SaccFlightAndVehicles"))
                {
                    text.AppendLine($"=== {behaviour.GetType().Name} on {PathOf(behaviour.transform, prefab.transform)}");
                    foreach (var field in behaviour.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                        text.AppendLine("  " + field.Name + " = " + Describe(field.GetValue(behaviour), prefab.transform));
                }
                File.WriteAllText($"Logs/sacc/{Path.GetFileNameWithoutExtension(path)}.txt", text.ToString());
            }
            File.WriteAllText("Logs/sacc/_index.txt", index.ToString());
        }

        /// <summary>Physics, rendering, animation and audio facts per vehicle prefab, plus the shaders used overall.</summary>
        [MenuItem("FlightWorld/Tools/Dump Sacc Details")]
        public static void DumpSaccDetails()
        {
            Directory.CreateDirectory("Logs/sacc");
            var shaderUse = new SortedDictionary<string, SortedSet<string>>();
            var summary = new StringBuilder();
            foreach (var path in Directory.GetFiles("Assets/Sacc/Prefabs", "*.prefab").Select(p => p.Replace("\\", "/")))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                var text = new StringBuilder();
                var root = prefab.transform;
                foreach (var body in prefab.GetComponentsInChildren<Rigidbody>(true))
                    text.AppendLine($"BODY {PathOf(body.transform, root)}: mass {body.mass}, drag {body.linearDamping}, angular drag {body.angularDamping}, kinematic {body.isKinematic}, gravity {body.useGravity}, interpolation {body.interpolation}, detection {body.collisionDetectionMode}");
                foreach (var c in prefab.GetComponentsInChildren<Collider>(true))
                {
                    string shape = c is MeshCollider mc ? $"mesh '{(mc.sharedMesh ? mc.sharedMesh.name : "none")}' convex {mc.convex} tris {(mc.sharedMesh ? mc.sharedMesh.triangles.Length / 3 : 0)}"
                        : c is BoxCollider bc ? $"box {bc.size} at {bc.center}" : c is SphereCollider sc ? $"sphere r {sc.radius}" : c is CapsuleCollider cc ? $"capsule r {cc.radius} h {cc.height}"
                        : c is WheelCollider wc ? $"WHEEL r {wc.radius} travel {wc.suspensionDistance} spring {wc.suspensionSpring.spring} damper {wc.suspensionSpring.damper} mass {wc.mass} local {wc.transform.localPosition}" : c.GetType().Name;
                    text.AppendLine($"COLLIDER {PathOf(c.transform, root)} layer {c.gameObject.layer} trigger {c.isTrigger} on {c.enabled && c.gameObject.activeInHierarchy}: {shape}");
                }
                foreach (var group in prefab.GetComponentsInChildren<LODGroup>(true))
                    text.AppendLine($"LODGROUP {PathOf(group.transform, root)}: " + string.Join(" | ", group.GetLODs().Select(l => $"until {l.screenRelativeTransitionHeight:F3}: " + string.Join(",", l.renderers.Where(r => r).Select(r => r.name)))));
                foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    int tris = r is SkinnedMeshRenderer s && s.sharedMesh ? s.sharedMesh.triangles.Length / 3 : r.GetComponent<MeshFilter>() && r.GetComponent<MeshFilter>().sharedMesh ? r.GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3 : 0;
                    var mats = r.sharedMaterials.Where(m => m).Select(m =>
                    {
                        string shader = m.shader ? m.shader.name : "none";
                        if (!shaderUse.TryGetValue(shader, out var users)) shaderUse[shader] = users = new SortedSet<string>();
                        users.Add(name);
                        var tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                        string mode = m.HasProperty("_Mode") ? " mode " + m.GetFloat("_Mode") : "";
                        string color = m.HasProperty("_Color") ? " color " + m.GetColor("_Color") : "";
                        string emission = m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor") ? " emission " + m.GetColor("_EmissionColor") : "";
                        return $"{m.name}<{shader}>{mode}{color}{emission} tex {(tex ? tex.name + " " + tex.width + "px" : "-")}";
                    });
                    text.AppendLine($"RENDER {PathOf(r.transform, root)} ({r.GetType().Name}, {tris} tris, active {r.gameObject.activeInHierarchy}): " + string.Join(" + ", mats));
                }
                foreach (var animator in prefab.GetComponentsInChildren<Animator>(true))
                {
                    var controller = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
                    text.AppendLine($"ANIMATOR {PathOf(animator.transform, root)}: {(controller ? controller.name : "none")}: " +
                                    (controller ? string.Join(", ", controller.parameters.Select(p => $"{p.name}:{p.type}{(p.type == AnimatorControllerParameterType.Float ? "=" + p.defaultFloat : p.type == AnimatorControllerParameterType.Bool ? "=" + p.defaultBool : "")}")) : ""));
                    if (controller) text.AppendLine("  layers: " + string.Join(", ", controller.layers.Select(l => l.name)));
                }
                foreach (var a in prefab.GetComponentsInChildren<AudioSource>(true))
                    text.AppendLine($"AUDIO {PathOf(a.transform, root)}: clip {(a.clip ? a.clip.name + " " + a.clip.length.ToString("F1") + "s" : "none")}, loop {a.loop}, awake {a.playOnAwake}, vol {a.volume}, pitch {a.pitch}, spatial {a.spatialBlend}, min {a.minDistance}, max {a.maxDistance}, doppler {a.dopplerLevel}, active {a.gameObject.activeInHierarchy}");
                foreach (var seat in prefab.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().Name == "SaccVehicleSeat"))
                    text.AppendLine($"SEAT {PathOf(seat.transform, root)} local {seat.transform.localPosition}: " + string.Join("; ", seat.transform.Cast<Transform>().Select(c => $"{c.name} {c.localPosition} rot {c.localEulerAngles}")));
                File.WriteAllText($"Logs/sacc/{name}_details.txt", text.ToString());
                summary.AppendLine($"{name}: " + string.Join("; ", prefab.GetComponentsInChildren<Rigidbody>(true).Select(b => $"{b.name} {b.mass} kg")));
            }
            summary.AppendLine();
            foreach (var pair in shaderUse) summary.AppendLine($"SHADER {pair.Key}: {pair.Value.Count} prefabs ({string.Join(", ", pair.Value.Take(6))}{(pair.Value.Count > 6 ? ", ..." : "")})");
            File.WriteAllText("Logs/sacc/_details_summary.txt", summary.ToString());
        }

        static string PathOf(Transform t, Transform root)
        {
            if (t == null) return "null";
            var parts = new List<string>();
            for (var p = t; p != null && p != root; p = p.parent) parts.Insert(0, p.name);
            return parts.Count == 0 ? "(root)" : string.Join("/", parts);
        }

        static string Describe(object value, Transform root)
        {
            switch (value)
            {
                case null: return "null";
                case UnityEngine.Object o when o == null: return "none";
                case Component c: return c.GetType().Name + " @" + PathOf(c.transform, root);
                case GameObject g: return "GameObject @" + PathOf(g.transform, root);
                case UnityEngine.Object o: return o.GetType().Name + " '" + o.name + "'";
                case AnimationCurve curve: return "curve(" + string.Join(" ", curve.keys.Select(k => $"{k.time:G4}:{k.value:G4}")) + ")";
                case string s: return "\"" + s + "\"";
                case Array a:
                    return "[" + string.Join("; ", a.Cast<object>().Take(40).Select(x => Describe(x, root))) + (a.Length > 40 ? "; ..." : "") + "]";
                case float f: return f.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
                case LayerMask mask: return "layers " + mask.value;
                default: return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }
}
