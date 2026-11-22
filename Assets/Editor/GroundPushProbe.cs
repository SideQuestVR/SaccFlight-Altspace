using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Does the ground push things that are simply resting on it? Rows of 80 kg balls (radius 0.2 m) are set
    /// down across the airport apron, where a ball was first seen to be pushed (14767.33, 7217.53), and each
    /// ball's travel over 4 s is written to Logs/ground_push.txt. A ball on sound ground does not move.
    /// (What pushed them, 2026-11-21: not the ground but the airliner's jet-blast particle systems, which carry a
    /// collider force and were running with nobody aboard. "Probe Things That Push" lists such sources.)
    /// Arm it and enter Play mode.
    /// </summary>
    [InitializeOnLoad]
    public static class GroundPushProbe
    {
        const string ArmedKey = "FlightWorld.GroundPushProbe.Armed";
        const string ReportPath = "Logs/ground_push.txt";
        static double started = -1;
        static int step;
        static readonly List<(Rigidbody body, Vector3 from, string label)> balls = new List<(Rigidbody, Vector3, string)>();

        static GroundPushProbe()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Tools/Probe Ground Pushes")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(ReportPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Drop(Vector3 at, string label)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "__ball";
            go.transform.localScale = Vector3.one * 0.4f;
            go.transform.position = at;
            var body = go.AddComponent<Rigidbody>();
            body.mass = 80f;
            balls.Add((body, at, label));
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; step = 0; return; }
            if (started < 0) { started = EditorApplication.timeSinceStartup; balls.Clear(); return; }
            double t = EditorApplication.timeSinceStartup - started;
            try
            {
                if (step == 0 && t > 5)
                {
                    // across the apron 40 m ahead of the A320's exit point, 0.5 m apart, as first measured ...
                    var origin = new Vector3(-3.47f, 1.6f, 154.24f);
                    float yaw = 301.3f * Mathf.Deg2Rad;
                    Vector3 ahead = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw)), right = new Vector3(ahead.z, 0f, -ahead.x);
                    for (int i = -40; i <= 40; i++) Drop(origin + ahead * 40f + right * (i * 0.5f), $"apron row, {i * 0.5f:+0.0;-0.0} m");
                    // ... along the line itself ...
                    for (int i = -3; i <= 3; i++) Drop(origin + ahead * (i * 40f) + right * 0.5f, $"along the line, {i * 40:+0;-0} m");
                    // ... and a coarse grid over the arrival area and the rows of vehicles
                    var arrival = new Vector3(0.00f, 1.6f, 0.00f);
                    for (int a = -2; a <= 6; a++) for (int r = -3; r <= 6; r++) Drop(arrival + ahead * (a * 25f) + right * (r * 25f + 3.7f), $"grid {a * 25:+0;-0} ahead {r * 25:+0;-0} right");
                    step = 1;
                }
                else if (step == 1 && t > 9.5)
                {
                    var text = new StringBuilder();
                    var moved = balls.Select(b => (b.label, travel: Vector3.Distance(new Vector3(b.body.position.x, 0, b.body.position.z), new Vector3(b.from.x, 0, b.from.z)),
                        height: b.body.position.y, b.from)).ToList();
                    int pushed = moved.Count(m => m.travel > 0.3f);
                    text.AppendLine($"{balls.Count} balls set down; {pushed} pushed more than 0.3 m in 4.5 s; resting height {moved.Min(m => m.height):F3}..{moved.Max(m => m.height):F3}");
                    foreach (var m in moved.Where(m => m.travel > 0.3f).OrderByDescending(m => m.travel).Take(30))
                        text.AppendLine($"   {m.travel,6:F2} m  {m.label}  from ({m.from.x:F2}, {m.from.z:F2})");
                    text.AppendLine(pushed == 0 ? "PASS" : "FAIL");
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
