using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Read-only: which upstream (Sacc / VAU) components each fleet prefab carries, and where, so the port's gaps can
    /// be listed. Output: Logs/sacc_component_census.txt.
    /// </summary>
    public static class SaccComponentCensus
    {
        static readonly string[] Prefabs =
        {
            "GRVR-1Main", "SD-1Main", "SH-1Main", "SH-3Main", "SC-1Main", "SH-2Main", "SaccBalloonMain", "StuntPlane_Main",
            "Wasp-WX3_Main", "SB-1Main", "SF-1Main", "SF-2Main", "SX-TR3Main", "SS-1Main",
            "ST-Baron_Main", "ST-Iccarus_Main", "ST-Shoreline_Main", "ST-Thunder_Main", "SaccTankMain",
        };

        [MenuItem("FlightWorld/Tools/Sacc Component Census")]
        public static void Run()
        {
            var text = new StringBuilder();
            var all = Directory.GetFiles("Assets/Sacc/Prefabs", "*.prefab").Select(p => p.Replace(Path.DirectorySeparatorChar, '/')).ToList();
            var paths = Prefabs.Select(n => all.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == n) ?? ("missing " + n)).ToList();
            paths.Add("Assets/VAU320/Prefab/v320neo_main 1217.prefab");
            var totals = new Dictionary<string, List<string>>();
            foreach (var path in paths)
            {
                var prefab = path.StartsWith("missing") ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { text.AppendLine("?? " + path); continue; }
                var name = Path.GetFileNameWithoutExtension(path);
                text.AppendLine("== " + name);
                foreach (var group in prefab.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null)
                             .GroupBy(m => m.GetType().Name).OrderBy(g => g.Key))
                {
                    var ns = group.First().GetType().Namespace ?? "";
                    if (ns.StartsWith("TMPro") || ns.StartsWith("UnityEngine")) continue;
                    var where = string.Join(", ", group.Select(m => m.gameObject.name).Distinct().Take(6));
                    text.AppendLine($"   {group.Key} x{group.Count()}  [{where}]");
                    if (!totals.TryGetValue(group.Key, out var list)) totals[group.Key] = list = new List<string>();
                    list.Add(name);
                }
            }
            text.AppendLine();
            text.AppendLine("== totals (component: vehicles)");
            foreach (var kv in totals.OrderBy(k => k.Key)) text.AppendLine($"   {kv.Key}: {string.Join(", ", kv.Value)}");
            File.WriteAllText("Logs/sacc_component_census.txt", text.ToString());
        }
    }
}
