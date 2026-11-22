using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// What does somebody WATCHING see when another player flies a vehicle away? The editor has no second
    /// player, so this moves a vehicle the way a remote pilot's client would (its body follows positions from
    /// outside, nobody is in its seat here) and reads the vehicle's animator: is it running, is the engine
    /// shown as on, which state are the rotors in. Vehicle name in Library/FlightWorld/test_target.txt
    /// (default SH-2); arm it and enter Play mode; the answer goes to Logs/watcher_probe.txt.
    /// </summary>
    [InitializeOnLoad]
    public static class WatcherProbe
    {
        const string ArmedKey = "FlightWorld.WatcherProbe.Armed";
        const string ReportPath = "Logs/watcher_probe.txt";
        static double started = -1, lastLog = -1;
        static readonly StringBuilder text = new StringBuilder();
        static Vector3 home;
        static bool failed;

        static WatcherProbe()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Tools/Probe Watching A Remote Flight")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(ReportPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static string Rotors(Animator animator)
        {
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                if (animator.GetLayerName(layer) != "EngineOutput") continue;
                var info = animator.GetCurrentAnimatorStateInfo(layer);
                foreach (var name in new[] { "BladeSpin", "BladeNoSpin", "BladeSpinUp", "BladeSpinDown" })
                    if (info.IsName(name)) return name;
                return "state " + info.shortNameHash;
            }
            return "(no rotor layer)";
        }

        static void Check(string what, bool ok)
        {
            text.AppendLine((ok ? "ok    " : "WRONG ") + what);
            if (!ok) failed = true;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; return; }
            string target = File.Exists("Library/FlightWorld/test_target.txt") ? File.ReadAllText("Library/FlightWorld/test_target.txt").Trim() : "";
            if (string.IsNullOrEmpty(target)) target = "SH-2";
            var fleet = GameObject.Find("Aircraft");
            var vehicle = fleet != null ? fleet.transform.Find(target) : null;
            if (vehicle == null) return;
            var body = vehicle.GetComponent<Rigidbody>();
            var animator = vehicle.GetComponent<Animator>();
            if (started < 0)
            {
                started = EditorApplication.timeSinceStartup;
                text.Clear();
                failed = false;
                lastLog = -1;
                home = vehicle.position;
                return;
            }
            double t = EditorApplication.timeSinceStartup - started;
            try
            {
                // 0-12 s parked; 12-20 s climb at 3 m/s; 20-34 s hover 24 m up, dead still; 34-37 s back down; then parked
                Vector3 want = home;
                if (t > 12 && t <= 20) want = home + Vector3.up * (float)(t - 12) * 3f;
                else if (t > 20 && t <= 34) want = home + Vector3.up * 24f;
                else if (t > 34 && t <= 37) want = home + Vector3.up * 24f * (float)(1 - (t - 34) / 3);
                if (t > 12 && t <= 37.5)
                {
                    body.isKinematic = true;
                    body.position = want;
                    vehicle.position = want;
                }
                if (t - lastLog >= 2)
                {
                    lastLog = t;
                    text.AppendLine($"{t,5:F1} s  height {vehicle.position.y - home.y,5:F1} m  animator {(animator.enabled ? "running" : "stopped")}  " +
                                    $"EngineOn {animator.GetBool("EngineOn")}  occupied {animator.GetBool("occupied")}  rotors {Rotors(animator)}");
                }
                if (t > 11 && t < 11.2 && text.ToString().IndexOf("parked:", StringComparison.Ordinal) < 0)
                    text.AppendLine($"parked: animator {(animator.enabled ? "running" : "stopped")}, EngineOn {animator.GetBool("EngineOn")}");
                if (t > 19 && t < 19.2 && text.ToString().IndexOf("climbing", StringComparison.Ordinal) < 0)
                    Check("climbing: the watcher sees the engine on and the animator running", animator.enabled && animator.GetBool("EngineOn"));
                if (t > 33 && t < 33.2 && text.ToString().IndexOf("hovering", StringComparison.Ordinal) < 0)
                    Check("hovering dead still for 13 s: engine still on, rotors turning", animator.enabled && animator.GetBool("EngineOn") && Rotors(animator).StartsWith("BladeSpin") && Rotors(animator) != "BladeSpinDown");
                if (t > 52)
                {
                    Check("15 s after it was set down and left: engine shown off", !animator.GetBool("EngineOn") || !animator.enabled);
                    text.AppendLine(failed ? "FAIL" : "PASS");
                    Directory.CreateDirectory("Logs");
                    File.WriteAllText(ReportPath, text.ToString());
                    SessionState.SetBool(ArmedKey, false);
                    EditorApplication.update -= Tick;
                    started = -1;
                    EditorApplication.isPlaying = false;
                }
            }
            catch (Exception e)
            {
                text.AppendLine("FAIL: " + e);
                File.WriteAllText(ReportPath, text.ToString());
                SessionState.SetBool(ArmedKey, false);
                EditorApplication.update -= Tick;
                EditorApplication.isPlaying = false;
            }
        }
    }
}
