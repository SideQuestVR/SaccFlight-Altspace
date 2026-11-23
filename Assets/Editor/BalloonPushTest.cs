using System.IO;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// The balloon's right stick, in Play mode: a test pilot lifts off on full burner, holds the right stick forward
    /// for 6 s, then right for 6 s, and the drift along the balloon's own axes is logged each way. Arm it, press Play.
    /// Output: Logs/balloon_push_test.txt.
    /// </summary>
    [InitializeOnLoad]
    public static class BalloonPushTest
    {
        const string ArmedKey = "FlightWorld.BalloonPushTest.Armed";
        const string LogPath = "Logs/balloon_push_test.txt";
        static double started = -1;
        static int step;
        static Vector3 from;
        static float heading;
        static string text = "";

        static BalloonPushTest()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Test Balloon Right Stick")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(LogPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Stick(float x, float y) => EventBus.Trigger("OnControllerAxisUpdate", new CustomEventArgs("OnControllerAxisUpdate", new object[] { 1, x, y }));

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; step = 0; return; }
            var balloon = GameObject.Find("Aircraft")?.transform.Find("Balloon")?.gameObject;
            var watch = GameObject.Find("FleetWatch");
            if (balloon == null || watch == null) return;
            if (started < 0) { started = EditorApplication.timeSinceStartup; step = 0; text = ""; }
            double t = EditorApplication.timeSinceStartup - started;
            var vars = Variables.Graph(GraphReference.New(balloon.GetComponent<ScriptMachine>(), true));
            var clock = Variables.Object(watch);
            var root = balloon.transform;
            var body = balloon.GetComponent<Rigidbody>();
            string State() => $" [piloting {vars.Get("Piloting")} dead {vars.Get("Dead")} health {vars.Get("Health")} held {vars.Get("Held")} constraints {body.constraints} engineOff {vars.Get("EngineOff")} throttle {vars.Get("Throttle")} engine {vars.Get("EngineOutput")} althold {vars.Get("AltHoldOn")} kinematic {body.isKinematic} velocity {body.linearVelocity} advanced {vars.Get("Advanced")} watchAdvanced {clock.Get("AdvancedControls")} pilotVehicle {(clock.Get("PilotVehicle") as GameObject)?.name} has {clock.Get("HasPilotVehicle")}]";
            if (step == 0 && t > 4)
            {
                vars.Set("TestPilot", true);
                vars.Set("Piloting", true);
                clock.Set("PilotVehicle", balloon);
                clock.Set("HasPilotVehicle", true);
                vars.Set("Throttle", 1f);
                step = 1;
            }
            if (step >= 1) vars.Set("Throttle", 1f);
            if (step == 1 && t > 12)
            {
                text += $"RightStickThrusts {Variables.Object(balloon).Get("RightStickThrusts")}, height after lift-off {root.position.y:F1} m{State()}\n";
                from = root.position;
                Stick(0f, 1f);
                step = 2;
            }
            else if (step == 2 && t > 18)
            {
                var moved = root.InverseTransformDirection(root.position - from);
                text += $"stick forward 6 s: ahead {moved.z:F2} m, right {moved.x:F2} m (RightThrustZ {vars.Get("RightThrustZ")}){State()}\n";
                from = root.position;
                Stick(1f, 0f);
                step = 3;
            }
            else if (step == 3 && t > 24)
            {
                var moved = root.InverseTransformDirection(root.position - from);
                text += $"stick right 6 s: ahead {moved.z:F2} m, right {moved.x:F2} m (RightThrustX {vars.Get("RightThrustX")}){State()}\n";
                Stick(0f, 0f);
                heading = root.eulerAngles.y;
                // the left stick to the right: a slow turn
                EventBus.Trigger("OnControllerAxisUpdate", new CustomEventArgs("OnControllerAxisUpdate", new object[] { 0, 1f, 0f }));
                step = 4;
            }
            else if (step == 4 && t > 32)
            {
                float turned = Mathf.DeltaAngle(heading, root.eulerAngles.y);
                text += $"left stick right 8 s: turned {turned:F1} degrees, spinning {balloon.GetComponent<Rigidbody>().angularVelocity.y * Mathf.Rad2Deg:F1} deg/s (InYaw {vars.Get("InYaw")})\nDONE\n";
                EventBus.Trigger("OnControllerAxisUpdate", new CustomEventArgs("OnControllerAxisUpdate", new object[] { 0, 0f, 0f }));
                File.WriteAllText(LogPath, text);
                SessionState.SetBool(ArmedKey, false);
                EditorApplication.update -= Tick;
                started = -1;
                step = 0;
                EditorApplication.isPlaying = false;
            }
        }
    }
}
