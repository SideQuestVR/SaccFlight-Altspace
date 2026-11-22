using System;
using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Development aid: writes the port names of the Visual Scripting units the graph builder uses, the
    /// platform's own (BS) units, and which planned members are on the Creator SDK allow list, to
    /// Logs/unit_dump.txt. Lets the builder be written against ground truth instead of guesses.
    /// </summary>
    public static class UnitDump
    {
        static readonly string[] UnitNames =
        {
            "ScalarSum", "ScalarSubtract", "ScalarMultiply", "ScalarDivide", "ScalarMinimum", "ScalarMaximum",
            "ScalarAbsolute", "ScalarExponentiate", "ScalarLerp", "ScalarMoveTowards", "ScalarRoot", "ScalarModulo",
            "Vector3Sum", "Vector3Subtract", "Vector3Multiply", "Vector3Lerp", "Vector3Normalize",
            "Vector3DotProduct", "Vector3CrossProduct", "Vector3Distance", "Vector3Project", "Vector3Angle",
            "SelectUnit", "If", "Sequence", "SetVariable", "GetVariable", "IsVariableDefined", "CustomEvent",
            "TriggerCustomEvent", "Less", "LessOrEqual", "Greater", "GreaterOrEqual", "Equal", "NotEqual", "And", "Or",
            "Negate", "ExclusiveOr", "Update", "FixedUpdate", "LateUpdate", "Start", "OnEnable", "OnCollisionEnter",
            "OnTriggerEnter", "This", "NullCheck", "Cooldown", "Timer", "ToggleFlow", "Once", "GetObjectVariable",
            "SetObjectVariable", "GetGraphVariable", "SetGraphVariable",
        };

        static readonly string[] Members =
        {
            "UnityEngine.Rigidbody.linearVelocity", "UnityEngine.Rigidbody.angularVelocity", "UnityEngine.Rigidbody.mass",
            "UnityEngine.Rigidbody.AddRelativeForce", "UnityEngine.Rigidbody.AddRelativeTorque",
            "UnityEngine.Rigidbody.AddForceAtPosition", "UnityEngine.Rigidbody.AddForce", "UnityEngine.Rigidbody.MoveRotation",
            "UnityEngine.Rigidbody.rotation", "UnityEngine.Rigidbody.position", "UnityEngine.Rigidbody.isKinematic",
            "UnityEngine.Rigidbody.worldCenterOfMass", "UnityEngine.Rigidbody.centerOfMass",
            "UnityEngine.Transform.forward", "UnityEngine.Transform.up", "UnityEngine.Transform.right",
            "UnityEngine.Transform.position", "UnityEngine.Transform.rotation", "UnityEngine.Transform.localRotation",
            "UnityEngine.Transform.localEulerAngles", "UnityEngine.Transform.localPosition",
            "UnityEngine.Transform.InverseTransformDirection", "UnityEngine.Transform.TransformDirection",
            "UnityEngine.Transform.InverseTransformPoint", "UnityEngine.Transform.Rotate", "UnityEngine.Transform.SetPositionAndRotation",
            "UnityEngine.Vector3..ctor", "UnityEngine.Vector3.x", "UnityEngine.Vector3.y", "UnityEngine.Vector3.z",
            "UnityEngine.Vector3.magnitude", "UnityEngine.Vector3.sqrMagnitude", "UnityEngine.Vector3.normalized",
            "UnityEngine.Vector3.Dot", "UnityEngine.Vector3.Cross", "UnityEngine.Vector3.Lerp", "UnityEngine.Vector3.ProjectOnPlane",
            "UnityEngine.Vector3.SignedAngle", "UnityEngine.Vector3.Angle", "UnityEngine.Vector3.MoveTowards",
            "UnityEngine.Vector3.op_Multiply", "UnityEngine.Vector3.op_Addition", "UnityEngine.Vector3.op_Subtraction",
            "UnityEngine.Vector3.op_Division", "UnityEngine.Vector3.op_UnaryNegation", "UnityEngine.Vector3.zero",
            "UnityEngine.Vector3.up", "UnityEngine.Vector3.forward", "UnityEngine.Vector3.right",
            "UnityEngine.Vector2.x", "UnityEngine.Vector2.y",
            "UnityEngine.Mathf.Min", "UnityEngine.Mathf.Max", "UnityEngine.Mathf.Clamp", "UnityEngine.Mathf.Clamp01",
            "UnityEngine.Mathf.Lerp", "UnityEngine.Mathf.Pow", "UnityEngine.Mathf.Abs", "UnityEngine.Mathf.Sign",
            "UnityEngine.Mathf.Sin", "UnityEngine.Mathf.Cos", "UnityEngine.Mathf.Acos", "UnityEngine.Mathf.Atan2",
            "UnityEngine.Mathf.PerlinNoise", "UnityEngine.Mathf.MoveTowards", "UnityEngine.Mathf.Sqrt", "UnityEngine.Mathf.Exp",
            "UnityEngine.Mathf.InverseLerp", "UnityEngine.Mathf.Rad2Deg", "UnityEngine.Mathf.Deg2Rad", "UnityEngine.Mathf.Round",
            "UnityEngine.Time.fixedDeltaTime", "UnityEngine.Time.deltaTime", "UnityEngine.Time.time",
            "UnityEngine.Physics.Raycast", "UnityEngine.RaycastHit.distance",
            "UnityEngine.Quaternion.Euler", "UnityEngine.Quaternion.AngleAxis", "UnityEngine.Quaternion.Inverse",
            "UnityEngine.Quaternion.op_Multiply", "UnityEngine.Quaternion.identity", "UnityEngine.Quaternion.Slerp",
            "UnityEngine.AudioSource.pitch", "UnityEngine.AudioSource.volume",
            "UnityEngine.WheelCollider.isGrounded", "UnityEngine.WheelCollider.brakeTorque", "UnityEngine.WheelCollider.motorTorque",
            "UnityEngine.WheelCollider.steerAngle",
            "UnityEngine.GameObject.SetActive", "UnityEngine.GameObject.transform", "UnityEngine.GameObject.GetComponent",
            "UnityEngine.Component.transform", "UnityEngine.Component.gameObject",
            "TMPro.TMP_Text.text", "UnityEngine.Animator.SetFloat", "UnityEngine.Collision.relativeVelocity",
            "UnityEngine.Collision.impulse", "UnityEngine.ParticleSystem.Play", "UnityEngine.ParticleSystem.Stop",
            "System.Single.Parse", "System.String.Format", "System.Single.ToString", "System.Int32.ToString",
        };

        [MenuItem("FlightWorld/Flight/Dump Units And Members")]
        public static void Dump()
        {
            var sb = new StringBuilder();
            var graph = new FlowGraph();
            var flowAssembly = typeof(FlowGraph).Assembly;
            sb.AppendLine("== units ==");
            foreach (var name in UnitNames)
            {
                var type = flowAssembly.GetType("Unity.VisualScripting." + name, false);
                sb.AppendLine(type == null ? $"{name}: MISSING" : Describe(type, graph));
            }

            sb.AppendLine("== platform (BS) units ==");
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch { continue; }
                foreach (var type in types.Where(t => typeof(IUnit).IsAssignableFrom(t) && !t.IsAbstract && t.Namespace != null
                                                      && (t.Namespace.StartsWith("BS") || t.Namespace.StartsWith("Banter"))))
                {
                    sb.AppendLine($"[{assembly.GetName().Name}] " + Describe(type, graph));
                }
            }

            sb.AppendLine("== members ==");
            var allowed = GraphSpike.AllowedMembers();
            foreach (var id in Members) sb.AppendLine($"{(allowed != null && allowed.Contains(id) ? "ok  " : "NO  ")}{id}");
            if (allowed != null)
            {
                sb.AppendLine("== allowed members on synced object / seat / attached object / rigidbody wrappers ==");
                foreach (var id in allowed.Where(m => m.StartsWith("BS.BSSyncedObject.") || m.StartsWith("BS.BSSeat.")
                                                      || m.StartsWith("BS.BSAttachedObject.") || m.StartsWith("BS.BSHeldEvents.")).OrderBy(m => m))
                {
                    if (!id.Contains(".Get") && !id.Contains("Component") && !id.EndsWith(".name") && !id.EndsWith(".tag")) sb.AppendLine("    " + id);
                }
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/unit_dump.txt", sb.ToString());
            Debug.Log("[UnitDump] wrote Logs/unit_dump.txt");
        }

        static string Describe(Type type, FlowGraph graph)
        {
            try
            {
                var unit = (IUnit)Activator.CreateInstance(type);
                graph.units.Add(unit);
                unit.EnsureDefined();
                string Ports<T>(System.Collections.Generic.IEnumerable<T> ports) where T : IUnitPort =>
                    string.Join(", ", ports.Select(p => p is ValueInput vi ? $"{p.key}:{vi.type.Name}" : p is ValueOutput vo ? $"{p.key}:{vo.type.Name}" : p.key));
                return $"{type.FullName} | cin[{Ports(unit.controlInputs)}] cout[{Ports(unit.controlOutputs)}] " +
                       $"vin[{Ports(unit.valueInputs)}] vout[{Ports(unit.valueOutputs)}]";
            }
            catch (Exception e)
            {
                return $"{type.FullName}: could not instantiate ({e.GetType().Name})";
            }
        }
    }
}
