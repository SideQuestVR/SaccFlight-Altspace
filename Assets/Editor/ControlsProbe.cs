using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Read-only: what each fleet prefab has for Sacc's VR controls. Its dial functions (left and right, in order),
    /// the stick display and its highlighters, the grab settings of its SaccAirVehicle, and which parts the
    /// animator's stick and throttle inputs move. Output: Logs/controls_probe.txt.
    /// </summary>
    public static class ControlsProbe
    {
        static readonly string[] Prefabs =
        {
            "GRVR-1Main", "SD-1Main", "SH-1Main", "SH-3Main", "SC-1Main", "SH-2Main", "SaccBalloonMain", "StuntPlane_Main",
            "Wasp-WX3_Main", "SB-1Main", "SF-1Main", "SF-2Main", "SX-TR3Main", "Assets/VAU320/Prefab/v320neo_main 1217.prefab", "SS-1Main",
        };

        [MenuItem("FlightWorld/Tools/Test: Probe Cockpit Controls")]
        public static void Probe()
        {
            var text = new StringBuilder();
            foreach (var name in Prefabs)
            {
                string path = name.EndsWith(".prefab") ? name : $"Assets/Sacc/Prefabs/{name}.prefab";
                var root = PrefabUtility.LoadPrefabContents(path);
                try { Describe(root, name, text); }
                catch (System.Exception e) { text.AppendLine($"== {name}: FAILED {e.Message}"); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            File.WriteAllText("Logs/controls_probe.txt", text.ToString() + "OK\n");
        }

        /// <summary>Every public setting of every dial function on every fleet prefab. Output: Logs/dfunc_probe.txt.</summary>
        [MenuItem("FlightWorld/Tools/Test: Probe Dial Function Settings")]
        public static void ProbeFunctions()
        {
            var text = new StringBuilder();
            foreach (var name in Prefabs)
            {
                string path = name.EndsWith(".prefab") ? name : $"Assets/Sacc/Prefabs/{name}.prefab";
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var entity = root.GetComponentsInChildren<MonoBehaviour>(true).FirstOrDefault(m => m != null && m.GetType().Name == "SaccEntity");
                    var top = entity != null ? entity.transform : root.transform;
                    text.AppendLine($"== {name}");
                    var seen = new System.Collections.Generic.HashSet<MonoBehaviour>();
                    foreach (var side in new[] { "L", "R" })
                    {
                        int slot = 0;
                        foreach (var f in Get(entity, "Dial_Functions_" + side) as MonoBehaviour[] ?? new MonoBehaviour[0])
                        {
                            if (f != null && seen.Add(f)) Dump(f, $"{side}{slot}", top, text);
                            slot++;
                        }
                    }
                    // weapon and effect helpers that are not on a dial themselves
                    foreach (var m in root.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && !seen.Contains(m) &&
                                 (m.GetType().Name.StartsWith("DFUNC_") || m.GetType().Name.Contains("AAM") || m.GetType().Name.Contains("AGM") || m.GetType().Name.Contains("Bomb"))))
                        Dump(m, "--", top, text);
                }
                catch (System.Exception e) { text.AppendLine($"== {name}: FAILED {e.Message}"); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            File.WriteAllText("Logs/dfunc_probe.txt", text.ToString() + "OK\n");
        }

        static void Dump(MonoBehaviour f, string slot, Transform top, StringBuilder text)
        {
            text.Append($"  [{slot}] {f.GetType().Name}@{PathOf(f.transform, top)}:");
            foreach (var field in f.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                var v = field.GetValue(f);
                string s;
                if (v == null) s = "null";
                else if (v is Object o) s = o == null ? "null" : o is Component c ? $"{c.GetType().Name}:{PathOf(c.transform, top)}" : o is GameObject go ? $"GO:{PathOf(go.transform, top)}" : $"{o.GetType().Name}:{o.name}";
                else if (v is System.Array a) s = $"[{a.Length}] " + string.Join(",", a.Cast<object>().Take(6).Select(e => e is Object eo ? (eo == null ? "null" : eo is Component ec ? PathOf(ec.transform, top) : eo is GameObject eg ? PathOf(eg.transform, top) : eo.name) : e?.ToString()));
                else s = v.ToString();
                if (s.Length > 140) s = s.Substring(0, 140) + "...";
                text.Append($" {field.Name}={s};");
            }
            text.AppendLine();
        }

        static string PathOf(Transform t, Transform top)
        {
            if (t == null) return "(none)";
            string p = t.name;
            for (var up = t.parent; up != null && up != top; up = up.parent) p = up.name + "/" + p;
            return p;
        }

        static object Get(object owner, string field) => owner?.GetType().GetField(field)?.GetValue(owner);

        static void Describe(GameObject holder, string name, StringBuilder text)
        {
            var entity = holder.GetComponentsInChildren<MonoBehaviour>(true).FirstOrDefault(m => m != null && m.GetType().Name == "SaccEntity");
            var flight = holder.GetComponentsInChildren<MonoBehaviour>(true).FirstOrDefault(m => m != null && (m.GetType().Name == "SaccAirVehicle" || m.GetType().Name == "SaccSeaVehicle"));
            var top = entity != null ? entity.transform : holder.transform;
            text.AppendLine($"== {name}");
            foreach (var side in new[] { "L", "R" })
            {
                var list = Get(entity, "Dial_Functions_" + side) as MonoBehaviour[] ?? new MonoBehaviour[0];
                text.AppendLine($"  dial {side} ({list.Length}): " + string.Join(", ", list.Select(f => f == null ? "null"
                    : $"{f.GetType().Name}@{PathOf(f.transform, top)} on={PathOf((Get(f, "Dial_Funcon") as GameObject)?.transform, top)}")));
                var hl = Get(entity, side + "StickDisplayHighlighter") as Transform;
                text.AppendLine($"  highlighter {side}: {PathOf(hl, top)} active {(hl != null && hl.gameObject.activeInHierarchy)} rot {(hl != null ? hl.localEulerAngles.ToString() : "")}");
            }
            text.AppendLine($"  dial sens {Get(entity, "DialSensitivity")} L-straight {Get(entity, "LeftDialDivideStraightUp")} R-straight {Get(entity, "RightDialDivideStraightUp")}");
            foreach (var display in top.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("StickDisplay")))
                text.AppendLine($"  display {PathOf(display, top)} active {display.gameObject.activeSelf} " +
                                $"comps {string.Join("/", display.GetComponents<Component>().Select(c => c == null ? "missing" : c.GetType().Name))}");
            foreach (var field in new[] { "MaxJoyAngles", "GripSensitivity", "ThrottleSensitivity", "VerticalThrottle", "SwitchHandsJoyThrottle",
                                          "JoystickPushPullPitch", "JoystickPushPullDistance", "ThrottleAfterburnerPoint", "HasAfterburner" })
                text.Append($"  {field}={Get(flight, field)}");
            text.AppendLine();
            text.AppendLine($"  ControlsRoot: {PathOf(Get(flight, "ControlsRoot") as Transform, top)}");
            var animator = top.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController is AnimatorController controller)
            {
                foreach (var parameter in new[] { "pitchinput", "rollinput", "yawinput", "throttle", "vtolangle", "gearup", "flaps" })
                {
                    var paths = new System.Collections.Generic.SortedSet<string>();
                    foreach (var layer in controller.layers)
                        foreach (var state in All(layer.stateMachine))
                        {
                            bool timed = state.timeParameterActive && state.timeParameter == parameter;
                            if (state.motion is BlendTree tree) Walk(tree, parameter, timed, paths);
                            else if (timed && state.motion is AnimationClip clip)
                                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                                    paths.Add(binding.path + ":" + binding.propertyName.Replace("m_Local", "").Replace("localEulerAnglesRaw", "rot"));
                        }
                    text.AppendLine($"  anim {parameter}: {string.Join(" | ", paths.Take(8))}{(paths.Count > 8 ? $" (+{paths.Count - 8})" : "")}");
                }
            }
        }

        static System.Collections.Generic.IEnumerable<AnimatorState> All(AnimatorStateMachine machine)
        {
            foreach (var child in machine.states) yield return child.state;
            foreach (var sub in machine.stateMachines) foreach (var s in All(sub.stateMachine)) yield return s;
        }

        static void Walk(BlendTree tree, string parameter, bool under, System.Collections.Generic.SortedSet<string> paths)
        {
            bool mine = under || tree.blendParameter == parameter || (tree.blendType != BlendTreeType.Simple1D && tree.blendParameterY == parameter);
            foreach (var child in tree.children)
            {
                if (child.motion is BlendTree sub) Walk(sub, parameter, mine, paths);
                else if (mine && child.motion is AnimationClip clip)
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        paths.Add(binding.path + ":" + binding.propertyName.Replace("m_Local", "").Replace("localEulerAnglesRaw", "rot"));
            }
        }
    }
}
