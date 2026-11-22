using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Proof that flight logic can be authored as code and shipped as Visual Scripting graphs.
    /// Builds a tiny graph (On Fixed Update -> Rigidbody.AddRelativeTorque) entirely from C#, checks
    /// every member it uses against the Creator SDK's allow list, saves it as a graph asset and puts a
    /// spinning marker cube beside the spawn point. If the cube spins in the published world, generated
    /// graphs run in the app.
    /// </summary>
    public static class GraphSpike
    {
        const string GraphPath = "Assets/Flight/Graphs/Spike_Spin.asset";
        const string ReportPath = "Logs/graph_spike_report.txt";

        [MenuItem("FlightWorld/Flight/Build Graph Spike")]
        public static void Build()
        {
            var report = new StringBuilder();
            try
            {
                BuildInternal(report);
                report.AppendLine("OK");
            }
            catch (Exception e)
            {
                report.AppendLine("FAILED: " + e);
                Debug.LogException(e);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, report.ToString());
            Debug.Log("[GraphSpike] " + report);
        }

        static void BuildInternal(StringBuilder report)
        {
            // ---- the graph, built in code --------------------------------------------------------
            var graph = new FlowGraph();
            var onFixedUpdate = new FixedUpdate { position = new Vector2(-300, 0) };
            graph.units.Add(onFixedUpdate);

            var member = new Member(typeof(Rigidbody), nameof(Rigidbody.AddRelativeTorque),
                new[] { typeof(Vector3), typeof(ForceMode) });
            var addTorque = new InvokeMember(member) { position = new Vector2(60, 0) };
            graph.units.Add(addTorque);

            var torque = new Literal(typeof(Vector3), new Vector3(0f, 2.5f, 0f)) { position = new Vector2(-300, 160) };
            graph.units.Add(torque);

            foreach (var unit in graph.units) unit.EnsureDefined();
            graph.controlConnections.Add(new ControlConnection(onFixedUpdate.trigger, addTorque.enter));
            graph.valueConnections.Add(new ValueConnection(torque.output, addTorque.inputParameters[0]));
            addTorque.inputParameters[1].SetDefaultValue(ForceMode.Acceleration);
            report.AppendLine($"graph: {graph.units.Count} units, {graph.controlConnections.Count} control + " +
                              $"{graph.valueConnections.Count} value connections");

            // ---- allow-list check -------------------------------------------------------------------
            var allowed = AllowedMembers();
            var used = graph.units.OfType<MemberUnit>().Select(u => u.member.targetType.FullName + "." + u.member.name).ToList();
            foreach (var id in used)
            {
                bool ok = allowed == null || allowed.Contains(id);
                report.AppendLine($"member {id}: {(allowed == null ? "allow list not found" : ok ? "allowed" : "NOT ALLOWED")}");
                if (!ok) throw new Exception($"{id} is not on the Creator SDK allow list");
            }

            // ---- asset (kept at a stable path so references survive regeneration) -------------------
            Directory.CreateDirectory(Path.GetDirectoryName(GraphPath));
            var asset = AssetDatabase.LoadAssetAtPath<ScriptGraphAsset>(GraphPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<ScriptGraphAsset>();
                asset.graph = graph;
                AssetDatabase.CreateAsset(asset, GraphPath);
            }
            else
            {
                asset.graph = graph;
                EditorUtility.SetDirty(asset);
            }
            AssetDatabase.SaveAssets();

            // ---- marker cube beside the spawn point ---------------------------------------------------
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == "GraphSpike").ToArray())
                UnityEngine.Object.DestroyImmediate(old);
            var spawn = GameObject.Find("RunwaySpawn");
            var basePos = spawn != null ? spawn.transform.position + spawn.transform.forward * 6f : Vector3.zero;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "GraphSpike";
            cube.transform.position = basePos + Vector3.up * 1.6f;
            var body = cube.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.angularDamping = 1f;
            body.constraints = RigidbodyConstraints.FreezePosition;
            var machine = cube.AddComponent<ScriptMachine>();
            machine.nest.SwitchToMacro(asset);
            EditorUtility.SetDirty(machine);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            report.AppendLine($"cube with generated graph placed at {cube.transform.position}");
        }

        /// <summary>Appends the marker cube's current spin to Logs/graph_spike_probe.txt (run during Play mode).</summary>
        [MenuItem("FlightWorld/Flight/Probe Graph Spike")]
        public static void Probe()
        {
            var cube = GameObject.Find("GraphSpike");
            var body = cube != null ? cube.GetComponent<Rigidbody>() : null;
            string line = body == null
                ? "no GraphSpike cube"
                : $"playing={Application.isPlaying} t={Time.time:F2} yaw={cube.transform.eulerAngles.y:F1} angularVelocityY={body.angularVelocity.y:F3}";
            File.AppendAllText("Logs/graph_spike_probe.txt", line + "\n");
        }

        /// <summary>The Creator SDK's list of members a world graph may use (null if the SDK type is missing).</summary>
        public static HashSet<string> AllowedMembers()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("BS.SDKEditor.VsStubsAllowed", false);
                var field = type?.GetField("members");
                if (field?.GetValue(null) is string[] members) return new HashSet<string>(members, StringComparer.Ordinal);
            }
            return null;
        }
    }
}
