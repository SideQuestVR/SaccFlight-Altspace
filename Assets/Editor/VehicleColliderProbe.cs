using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Read-only: one vehicle's colliders (path, kind, trigger, enabled, active, world bounds), seats and renderers, in
    /// the vehicle's own axes. Vehicle name in Library/FlightWorld/test_target.txt. Output: Logs/vehicle_colliders.txt.
    /// </summary>
    public static class VehicleColliderProbe
    {
        [MenuItem("FlightWorld/Tools/Test: Probe Vehicle Colliders")]
        public static void Probe()
        {
            string name = File.Exists("Library/FlightWorld/test_target.txt") ? File.ReadAllText("Library/FlightWorld/test_target.txt").Trim() : "Balloon";
            var vehicle = GameObject.Find("Aircraft")?.transform.Find(name) ?? GameObject.Find("GroundVehicles")?.transform.Find(name);
            var text = new StringBuilder();
            if (vehicle == null) { File.WriteAllText("Logs/vehicle_colliders.txt", "no vehicle " + name); return; }
            string Path(Transform t) { string p = t.name; for (var u = t.parent; u != null && u != vehicle; u = u.parent) p = u.name + "/" + p; return p; }
            string Local(Vector3 w) { var l = vehicle.InverseTransformPoint(w); return $"({l.x:F2}, {l.y:F2}, {l.z:F2})"; }
            text.AppendLine($"{name} at {vehicle.position}, layer {vehicle.gameObject.layer}");
            foreach (var c in vehicle.GetComponentsInChildren<Collider>(true))
                text.AppendLine($"collider {Path(c.transform)}: {c.GetType().Name} trigger {c.isTrigger} enabled {c.enabled} active {c.gameObject.activeInHierarchy} layer {c.gameObject.layer} " +
                                $"min {Local(c.bounds.min)} max {Local(c.bounds.max)}" +
                                (c is BoxCollider box ? $" | box centre {box.center} size {box.size}, at {Local(c.transform.position)} rot {(Quaternion.Inverse(vehicle.rotation) * c.transform.rotation).eulerAngles} scale {c.transform.lossyScale}" : ""));
            foreach (var r in vehicle.GetComponentsInChildren<Renderer>(true))
                text.AppendLine($"renderer {Path(r.transform)}: min {Local(r.bounds.min)} max {Local(r.bounds.max)} active {r.gameObject.activeInHierarchy}");
            foreach (var s in vehicle.GetComponentsInChildren<Transform>(true).Where(t => t.name.Contains("Seat") || t.name.StartsWith("EyePoint")))
                text.AppendLine($"seat-ish {Path(s)} at {Local(s.position)}");
            File.WriteAllText("Logs/vehicle_colliders.txt", text.ToString());
        }
    }
}
