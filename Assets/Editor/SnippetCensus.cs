using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Read-only: for every vehicle in the scene, what it points at outside its own hierarchy (variables on any of its
    /// objects, and component fields), and what in the scene points at it. Logs/snippet_census.txt.
    /// </summary>
    public static class SnippetCensus
    {
        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static Transform TransformOf(Object o) => o is GameObject go ? go.transform : o is Component c ? c.transform : null;

        [MenuItem("FlightWorld/Tools/Snippet Census")]
        public static void Run()
        {
            var text = new StringBuilder();
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                text.AppendLine($"root {root.name} ({root.transform.childCount} children){(root.activeSelf ? "" : " inactive")}");
            foreach (var rootName in new[] { "Aircraft", "GroundVehicles" })
            {
                var fleet = GameObject.Find(rootName);
                if (fleet == null) continue;
                foreach (Transform v in fleet.transform)
                {
                    text.AppendLine();
                    var body = v.GetComponent<Rigidbody>();
                    text.AppendLine($"== {rootName}/{v.name} at {v.position:F2} euler {v.eulerAngles:F1} active {v.gameObject.activeSelf} body {(body != null)} objects {v.GetComponentsInChildren<Transform>(true).Length}");
                    var outside = new SortedDictionary<string, int>();
                    void Note(string where, string field, Object target)
                    {
                        if (target == null) return;
                        var t = TransformOf(target);
                        if (t == null) return;                                 // an asset
                        if (EditorUtility.IsPersistent(target)) return;
                        if (t == v || t.IsChildOf(v)) return;
                        string key = $"{field} -> {PathOf(t)} [{target.GetType().Name}]";
                        outside.TryGetValue(key, out int n);
                        outside[key] = n + 1;
                    }
                    foreach (var vars in v.GetComponentsInChildren<Variables>(true))
                        foreach (var d in vars.declarations)
                        {
                            if (d.value is Object o) Note(PathOf(vars.transform), "var " + d.name, o);
                            else if (d.value is IEnumerable list && !(d.value is string))
                                foreach (var item in list) if (item is Object io) Note(PathOf(vars.transform), "var " + d.name + "[]", io);
                        }
                    foreach (var c in v.GetComponentsInChildren<Component>(true))
                    {
                        if (c == null || c is Variables || c is Transform) continue;
                        var so = new SerializedObject(c);
                        var p = so.GetIterator();
                        while (p.NextVisible(true))
                            if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue != null)
                                Note(PathOf(c.transform), $"{c.GetType().Name}.{p.propertyPath}", p.objectReferenceValue);
                    }
                    foreach (var kv in outside) text.AppendLine($"   out x{kv.Value}: {kv.Key}");
                    var machines = v.GetComponentsInChildren<ScriptMachine>(true);
                    text.AppendLine("   graphs: " + string.Join(", ", machines.GroupBy(m => m.nest.macro != null ? m.nest.macro.name : "(embedded)").Select(g => $"{g.Key} x{g.Count()}")));
                    var ids = v.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == "BSObjectId").ToArray();
                    text.AppendLine($"   object ids: {ids.Length}; e.g. {string.Join(", ", ids.Take(4).Select(i => new SerializedObject(i).FindProperty("Id").stringValue))}");
                    var vd = v.GetComponent<Variables>()?.declarations;
                    if (vd != null)
                        foreach (var name in new[] { "RespawnPoint", "ResetSign", "Watch", "WeaponsWorld", "SeaCollider", "Moor", "WaterLevel", "HasCatapult", "Catapult", "CatapultAnimator", "CatapultPoint", "TargetIndex", "IsBoat", "BoomMessage" })
                            if (vd.IsDefined(name))
                            {
                                var value = vd.Get(name);
                                var t = value is Object uo ? TransformOf(uo) : null;
                                text.AppendLine($"   {name} = {(t != null ? PathOf(t) + " @ " + t.position.ToString("F2") + " euler " + t.eulerAngles.ToString("F1") : value ?? "null")}");
                            }
                }
            }
            foreach (var name in new[] { "FleetWatch", "WeaponsWorld" })
            {
                var go = GameObject.Find(name);
                if (go == null) continue;
                text.AppendLine();
                text.AppendLine($"== {name}: children {string.Join(", ", go.transform.Cast<Transform>().Select(c => c.name))}");
                foreach (var d in go.GetComponent<Variables>().declarations)
                {
                    string shown = d.value is Object o ? (TransformOf(o) != null ? PathOf(TransformOf(o)) : o.name) + $" [{o.GetType().Name}]"
                        : d.value is IList list ? $"list of {list.Count}" : d.value?.ToString() ?? "null";
                    text.AppendLine($"   {d.name} = {shown}");
                }
            }
            File.WriteAllText("Logs/snippet_census.txt", text.ToString());
        }
    }
}
