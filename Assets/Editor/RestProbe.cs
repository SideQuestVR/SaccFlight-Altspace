using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Parked and left alone, does every aircraft stay where it is when something shoves it, and does the boat
    /// stay afloat with nobody aboard? Arm it and enter Play mode: after the vehicles have come to rest each one
    /// is given a sideways shove (a change of velocity of 3 m/s, far more than a walking player gives) and its
    /// movement over the next two seconds is written to Logs/rest_probe.txt, with PASS or FAIL as the last line.
    /// </summary>
    [InitializeOnLoad]
    public static class RestProbe
    {
        const string ArmedKey = "FlightWorld.RestProbe.Armed";
        const string ReportPath = "Logs/rest_probe.txt";
        static double started = -1;
        static int step;
        static Vector3[] before;
        static Rigidbody[] bodies;

        static RestProbe()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Tools/Probe Resting Vehicles")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(ReportPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; step = 0; return; }
            if (started < 0) { started = EditorApplication.timeSinceStartup; return; }
            double t = EditorApplication.timeSinceStartup - started;
            try
            {
                if (step == 0 && t > 14)
                {
                    var fleet = GameObject.Find("Aircraft");
                    bodies = fleet.transform.Cast<Transform>().Select(v => v.GetComponent<Rigidbody>()).Where(b => b != null).ToArray();
                    before = bodies.Select(b => b.position).ToArray();
                    foreach (var body in bodies) body.AddForce(body.transform.right * 3f, ForceMode.VelocityChange);
                    step = 1;
                }
                else if (step == 1 && t > 16.2)
                {
                    var text = new StringBuilder();
                    bool failed = false;
                    for (int i = 0; i < bodies.Length; i++)
                    {
                        var body = bodies[i];
                        float moved = Vector3.Distance(body.position, before[i]);
                        bool floats = body.name == "SS-1";
                        bool ok = floats ? body.position.y > -2f && body.position.y < 3f : moved < 0.05f;
                        if (!ok) failed = true;
                        text.AppendLine($"{(ok ? "ok    " : "WRONG ")}{body.name,-12} moved {moved:F3} m after a 3 m/s shove; constraints {body.constraints}, " +
                                        $"interpolation {body.interpolation}, height {body.position.y:F2}{(floats ? " (a boat: free to move, must be afloat)" : "")}");
                    }
                    text.AppendLine(failed ? "FAIL" : "PASS");
                    Directory.CreateDirectory("Logs");
                    File.WriteAllText(ReportPath, text.ToString());
                    SessionState.SetBool(ArmedKey, false);
                    EditorApplication.update -= Tick;
                    started = -1;
                    step = 0;
                    EditorApplication.isPlaying = false;
                }
            }
            catch (Exception e)
            {
                File.WriteAllText(ReportPath, "FAIL: " + e);
                SessionState.SetBool(ArmedKey, false);
                EditorApplication.update -= Tick;
                EditorApplication.isPlaying = false;
            }
        }
    }
}
