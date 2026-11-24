using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Read-only: every Sacc seat that has passenger functions (gunner seats), with its dial functions and extensions and
    /// their settings, so turrets and gunner weapons can be ported. Output: Logs/passenger_functions_census.txt.
    /// </summary>
    public static class PassengerFunctionsCensus
    {
        static readonly string[] Prefabs =
        {
            "SH-1Main", "SH-3Main", "SH-2Main", "SaccBalloonMain", "StuntPlane_Main", "Wasp-WX3_Main", "SF-2Main", "SX-TR3Main",
            "SS-1Main", "ST-Thunder_Main", "SaccTankMain",
        };

        static string Value(object v, Transform root)
        {
            switch (v)
            {
                case null: return "null";
                case UnityEngine.Object o when o == null: return "null";
                case Component c: return c.GetType().Name + ":" + Path(c.transform, root);
                case GameObject go: return "GO:" + Path(go.transform, root);
                case Array a: return "[" + string.Join(", ", a.Cast<object>().Select(x => Value(x, root))) + "]";
                case float f: return f.ToString("0.###");
                default: return v.ToString();
            }
        }

        static string Path(Transform t, Transform root)
        {
            var s = t.name;
            for (var p = t.parent; p != null && p != root; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        static void Dump(MonoBehaviour m, Transform root, StringBuilder text, string indent)
        {
            text.AppendLine($"{indent}{m.GetType().Name} on {Path(m.transform, root)}");
            foreach (var f in m.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType == typeof(KeyCode)) continue;
                text.AppendLine($"{indent}   {f.Name} = {Value(f.GetValue(m), root)}");
            }
        }

        /// <summary>The pilots' and drivers' own dials (SaccEntity), with every function's settings. Logs/entity_dials_census.txt.</summary>
        [MenuItem("FlightWorld/Tools/Entity Dials Census")]
        public static void Entities()
        {
            var text = new StringBuilder();
            var names = new[]
            {
                "GRVR-1Main", "SD-1Main", "SH-1Main", "SH-3Main", "SC-1Main", "SH-2Main", "SaccBalloonMain", "StuntPlane_Main", "Wasp-WX3_Main",
                "SB-1Main", "SF-1Main", "SF-2Main", "SX-TR3Main", "SS-1Main", "ST-Baron_Main", "ST-Iccarus_Main", "ST-Shoreline_Main",
                "ST-Thunder_Main", "SaccTankMain",
            };
            var ported = new[] { "DFUNC_AAM", "DFUNC_AGM", "DFUNC_AltHold", "DFUNC_Bomb", "DFUNC_Brake", "DFUNC_Canopy", "DFUNC_Cruise", "DFUNC_Flaps",
                "DFUNC_Flares", "DFUNC_Gear", "DFUNC_Gun", "DFUNC_Hook", "DFUNC_Limits", "DFUNC_Smoke", "DFUNC_ToggleEngine", "DFUNC_VTOLAngle", "DFUNC_CarBrake" };
            foreach (var name in names)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Sacc/Prefabs/{name}.prefab");
                if (prefab == null) { text.AppendLine("?? " + name); continue; }
                text.AppendLine("==== " + name);
                var entity = prefab.GetComponentInChildren<SaccFlightAndVehicles.SaccEntity>(true);
                if (entity == null) continue;
                foreach (var (side, dial) in new[] { ("L", entity.Dial_Functions_L), ("R", entity.Dial_Functions_R) })
                    foreach (var m in (dial ?? new MonoBehaviour[0]).Where(x => x))
                    {
                        if (ported.Contains(m.GetType().Name)) { text.AppendLine($"  {side} {m.GetType().Name} ({m.gameObject.name}) [ported]"); continue; }
                        Dump(m, prefab.transform, text, "  " + side + " ");
                    }
                foreach (var m in (entity.ExtensionUdonBehaviours ?? new MonoBehaviour[0]).Where(x => x))
                    if (m.GetType().Name.StartsWith("SGV_AirControl") || m.GetType().Name.StartsWith("DFUNC")) Dump(m, prefab.transform, text, "  ext ");
            }
            File.WriteAllText("Logs/entity_dials_census.txt", text.ToString());
        }

        [MenuItem("FlightWorld/Tools/Passenger Functions Census")]
        public static void Run()
        {
            var text = new StringBuilder();
            foreach (var name in Prefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Sacc/Prefabs/{name}.prefab");
                if (prefab == null) { text.AppendLine("?? " + name); continue; }
                text.AppendLine("==== " + name);
                var root = prefab.transform;
                foreach (var seat in prefab.GetComponentsInChildren<SaccFlightAndVehicles.SaccVehicleSeat>(true))
                {
                    var pf = seat.PassengerFunctions;
                    text.AppendLine($"  seat {seat.name} pilot {seat.IsPilotSeat} functions {(pf ? Path(pf.transform, root) : "-")} animBool {seat.AnimBoolOnEnter}");
                    if (pf == null) continue;
                    foreach (var m in (pf.PassengerExtensions ?? new MonoBehaviour[0]).Where(x => x)) Dump(m, root, text, "     ext ");
                    foreach (var m in (pf.Dial_Functions_L ?? new MonoBehaviour[0]).Where(x => x)) Dump(m, root, text, "     L ");
                    foreach (var m in (pf.Dial_Functions_R ?? new MonoBehaviour[0]).Where(x => x)) Dump(m, root, text, "     R ");
                }
                foreach (var t in prefab.GetComponentsInChildren<SaccFlightAndVehicles.EXT_HandTurret>(true))
                    text.AppendLine($"  (hand turret on {Path(t.transform, root)})");
                foreach (var t in prefab.GetComponentsInChildren<SaccFlightAndVehicles.EXT_Turret>(true))
                    text.AppendLine($"  (turret on {Path(t.transform, root)})");
            }
            File.WriteAllText("Logs/passenger_functions_census.txt", text.ToString());
        }
    }
}
