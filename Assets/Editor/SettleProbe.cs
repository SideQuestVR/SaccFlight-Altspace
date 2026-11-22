using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Where does each parked aircraft really come to rest? Built from its prefab, an aircraft stands where its
    /// model says; on its suspension it then sinks or tips a little (5 cm for most, over a metre of bouncing for the
    /// SF-2). Players saw that settling a few seconds after every load as the vehicles shaking.
    ///
    /// Arm it and enter Play mode: it notes every aircraft as loaded (still frozen by the start hold), lets them
    /// release and settle, and after 25 s writes each one's change of pose, in its own space, to
    /// Assets/Flight/Editor/SettledPoses.txt. The fleet builder parks each aircraft with that change applied, so
    /// it starts where it would come to rest. The boat is left out: it floats. Re-run after changing a vehicle.
    /// </summary>
    [InitializeOnLoad]
    public static class SettleProbe
    {
        const string ArmedKey = "FlightWorld.SettleProbe.Armed";
        public const string PosesPath = "Assets/Flight/Editor/SettledPoses.txt";
        static double started = -1;
        static readonly Dictionary<string, (Vector3 p, Quaternion r)> first = new Dictionary<string, (Vector3, Quaternion)>();

        static SettleProbe()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Measure Settled Parking Poses")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText("Logs/settle_probe.txt", "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static IEnumerable<Transform> Aircraft()
        {
            var fleet = GameObject.Find("Aircraft");
            return fleet == null ? Enumerable.Empty<Transform>() :
                fleet.transform.Cast<Transform>().Where(v => v.GetComponent<Rigidbody>() != null && v.name != "SS-1");
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; return; }
            if (started < 0)
            {
                started = EditorApplication.timeSinceStartup;
                first.Clear();
                foreach (var v in Aircraft()) first[v.name] = (v.position, v.rotation);
                return;
            }
            if (EditorApplication.timeSinceStartup - started < 25) return;
            var culture = CultureInfo.InvariantCulture;
            var file = new StringBuilder("# change from the built parking pose to where each aircraft comes to rest: name|dx,dy,dz (its own space)|qx,qy,qz,qw\n");
            var report = new StringBuilder();
            foreach (var v in Aircraft())
            {
                if (!first.TryGetValue(v.name, out var was)) continue;
                var body = v.GetComponent<Rigidbody>();
                var delta = Quaternion.Inverse(was.r) * (v.position - was.p);
                var turn = Quaternion.Inverse(was.r) * v.rotation;
                file.AppendLine(string.Format(culture, "{0}|{1:R},{2:R},{3:R}|{4:R},{5:R},{6:R},{7:R}", v.name, delta.x, delta.y, delta.z, turn.x, turn.y, turn.z, turn.w));
                report.AppendLine($"{v.name,-12} moved {delta.x:F3}, {delta.y:F3}, {delta.z:F3} m, turned {Quaternion.Angle(Quaternion.identity, turn):F2} deg; now {body.constraints}, speed {body.linearVelocity.magnitude:F3}");
            }
            File.WriteAllText(PosesPath, file.ToString());
            File.WriteAllText("Logs/settle_probe.txt", report.ToString() + "OK\n");
            SessionState.SetBool(ArmedKey, false);
            EditorApplication.update -= Tick;
            started = -1;
            EditorApplication.isPlaying = false;
        }

        /// <summary>The measured change for one aircraft, if there is one.</summary>
        public static bool TryGet(string name, out Vector3 delta, out Quaternion turn)
        {
            delta = Vector3.zero;
            turn = Quaternion.identity;
            if (!File.Exists(PosesPath)) return false;
            foreach (var line in File.ReadAllLines(PosesPath))
            {
                var parts = line.Split('|');
                if (parts.Length != 3 || parts[0] != name) continue;
                float[] p = parts[1].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                float[] q = parts[2].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                delta = new Vector3(p[0], p[1], p[2]);
                turn = new Quaternion(q[0], q[1], q[2], q[3]);
                return true;
            }
            return false;
        }
    }
}
