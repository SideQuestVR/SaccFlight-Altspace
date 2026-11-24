using System.IO;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// The drivers' extra functions (GroundGraphsExtras.cs) on the Iccarus, in Play mode: boost held from a standstill,
    /// the jump's held push, and air control's pitch. The buttons themselves cannot be pressed in the editor, so the
    /// test holds them through the graph variables they set. Output: Logs/driver_extras_test.txt (PASS / FAIL, DONE).
    /// </summary>
    [InitializeOnLoad]
    public static class DriverExtrasTest
    {
        const string ArmedKey = "FlightWorld.DriverExtrasTest.Armed";
        const string LogPath = "Logs/driver_extras_test.txt";
        static double started = -1;
        static int stage;
        static StringBuilder log;
        static Vector3 before;

        static DriverExtrasTest()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Test Driver Extras")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(LogPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Check(string what, bool ok, string detail) => log.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}: {detail}");

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; return; }
            var car = GameObject.Find("GroundVehicles")?.transform.Find("Iccarus")?.gameObject;
            if (car == null) return;
            var g = Variables.Graph(GraphReference.New(car.GetComponent<ScriptMachine>(), true));
            var body = car.GetComponent<Rigidbody>();
            var anim = car.GetComponent<Animator>();
            var now = EditorApplication.timeSinceStartup;
            if (started < 0) { started = now; stage = 0; log = new StringBuilder(); return; }
            double t = now - started;
            switch (stage)
            {
                case 0 when t > 4:
                    g.Set("TestPilot", true);
                    g.Set("Driving", true);
                    stage = 1;
                    break;
                case 1 when t > 7:
                    before = body.linearVelocity;
                    g.Set("BoostHeld", true);
                    stage = 2;
                    break;
                case 2 when t > 8:
                    float gained = Vector3.Dot(body.linearVelocity - before, car.transform.forward);
                    Check("boost pushes the car forward", gained > 3f, $"+{gained:F1} m/s in 1 s, boost left {(float)g.Get("BoostLeft"):F2}, animator boosting {anim.GetFloat("boosting"):F0}");
                    g.Set("BoostHeld", false);
                    stage = 3;
                    break;
                case 3 when t > 11:
                    before = body.linearVelocity;
                    g.Set("JumpTime", 0.3f);
                    g.Set("JumpHeld", true);
                    stage = 4;
                    break;
                case 4 when t > 11.35:
                    float up = body.linearVelocity.y - before.y;
                    Check("a held jump pushes up", up > 2f, $"+{up:F1} m/s upwards in 0.35 s");
                    g.Set("JumpHeld", false);
                    // (well clear of the ground, so the check does not depend on how long the hop lasted)
                    body.position += Vector3.up * 6f;
                    car.transform.position = body.position;
                    body.angularVelocity = Vector3.zero;
                    g.Set("AirHeld", 1f);
                    g.Set("StickRY", 1f);
                    stage = 5;
                    break;
                case 5 when t > 11.85:
                    float pitchRate = car.transform.InverseTransformDirection(body.angularVelocity).x * Mathf.Rad2Deg;
                    Check("air control pitches the car", Mathf.Abs(pitchRate) > 20f, $"pitch rate {pitchRate:F0} deg/s after 0.5 s");
                    g.Set("AirHeld", 0f);
                    g.Set("StickRY", 0f);
                    g.Set("Driving", false);
                    g.Set("TestPilot", false);
                    log.AppendLine("DONE");
                    File.WriteAllText(LogPath, log.ToString());
                    SessionState.SetBool(ArmedKey, false);
                    EditorApplication.update -= Tick;
                    started = -1;
                    EditorApplication.isPlaying = false;
                    break;
            }
        }
    }
}
