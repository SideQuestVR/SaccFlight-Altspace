using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>Everything on a vehicle in the scene that can push other bodies without touching them: particle
    /// systems with collision or force settings, force fields, constant forces, effectors, wind zones.</summary>
    public static class PushersProbe
    {
        /// <summary>Takes out the collision slabs that were tried over the ground on 2026-11-21 (the pushing they were
        /// meant to stop came from an aircraft's jet-blast particles, not from the ground).</summary>
        [MenuItem("FlightWorld/Tools/Remove Ground Slabs")]
        public static void RemoveSlabs()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            int removed = 0;
            foreach (var old in scene.GetRootGameObjects().Where(o => o.name == "GroundSlabs").ToArray()) { Object.DestroyImmediate(old); removed++; }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            File.WriteAllText("Logs/ground_slabs_report.txt", $"removed {removed} slab root(s)");
        }

        [MenuItem("FlightWorld/Tools/Probe Things That Push")]
        public static void Probe()
        {
            var text = new StringBuilder();
            foreach (var rootName in new[] { "Aircraft", "GroundVehicles" })
            {
                var root = GameObject.Find(rootName);
                if (root == null) continue;
                foreach (Transform vehicle in root.transform)
                {
                    foreach (var ps in vehicle.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var collision = ps.collision;
                        var forces = ps.externalForces;
                        if (!(collision.enabled && collision.colliderForce != 0f) && !forces.enabled) continue;
                        var main = ps.main;
                        text.AppendLine($"{vehicle.name}: particle system '{ps.name}' active {ps.gameObject.activeInHierarchy} playOnAwake {main.playOnAwake} " +
                                        $"collision {(collision.enabled ? $"{collision.type} mode {collision.mode} colliderForce {collision.colliderForce} collidesWith {collision.collidesWith.value} sendMessages {collision.sendCollisionMessages}" : "off")} " +
                                        $"speed {main.startSpeed.constantMax} lifetime {main.startLifetime.constantMax} rate {ps.emission.rateOverTime.constantMax} emitting {ps.emission.enabled}");
                    }
                    foreach (var c in vehicle.GetComponentsInChildren<Component>(true).Where(c => c != null && (c is ParticleSystemForceField || c is ConstantForce || c is WindZone)))
                        text.AppendLine($"{vehicle.name}: {c.GetType().Name} on '{c.name}' active {c.gameObject.activeInHierarchy}" +
                                        (c is ConstantForce cf ? $" enabled {cf.enabled} force {cf.force} relative {cf.relativeForce} torque {cf.torque} relative torque {cf.relativeTorque}" : ""));
                }
            }
            File.WriteAllText("Logs/pushers.txt", text.Length > 0 ? text.ToString() : "nothing found\n");
        }
    }
}
