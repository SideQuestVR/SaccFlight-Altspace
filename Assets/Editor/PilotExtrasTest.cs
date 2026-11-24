using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// The pilots' switches, boosts and thrust reverser on the advanced controls' dials (FlightGraphsFunctions.cs), in
    /// Play mode: the Stunt Plane's first switch picked and pulled, the Wasp's boosts held, the SD-1's reverser pulled.
    /// Output: Logs/pilot_extras_test.txt (PASS / FAIL, then DONE).
    /// </summary>
    [InitializeOnLoad]
    public static class PilotExtrasTest
    {
        const string ArmedKey = "FlightWorld.PilotExtrasTest.Armed";
        const string LogPath = "Logs/pilot_extras_test.txt";
        static double started = -1, stepAt;
        static int step;
        static StringBuilder log;
        static Vector3 velocityBefore;

        static PilotExtrasTest()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Test Pilot Extras")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(LogPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Raise(string hook, params object[] arguments) => EventBus.Trigger(hook, new CustomEventArgs(hook, arguments));
        static void Check(string what, bool ok, string detail) => log.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}: {detail}");

        static void Pick(VariableDeclarations own, string side, string kind)
        {
            float slot = System.Convert.ToSingle(own.Get(side + kind + "Slot"));
            float degrees = System.Convert.ToSingle(own.Get(side + "DialDegrees"));
            var check = (Vector2)own.Get(side + "DialCheck");
            float r = (180f - degrees * (slot + 0.5f)) * Mathf.Deg2Rad;
            var stick = new Vector2(check.x * Mathf.Cos(r) - check.y * Mathf.Sin(r), check.x * Mathf.Sin(r) + check.y * Mathf.Cos(r));
            int hand = side == "L" ? 0 : 1;
            Raise("OnControllerAxisUpdate", hand, stick.x, stick.y);
            Raise("OnControllerAxisUpdate", hand, 0f, 0f);
        }

        static string SideOf(VariableDeclarations own, string kind) =>
            System.Convert.ToSingle(own.Get("L" + kind + "Slot")) >= 0f ? "L" : System.Convert.ToSingle(own.Get("R" + kind + "Slot")) >= 0f ? "R" : null;

        static (GameObject go, VariableDeclarations own, VariableDeclarations g) Fly(string name)
        {
            var go = GameObject.Find("Aircraft").transform.Find(name).gameObject;
            var g = Variables.Graph(GraphReference.New(go.GetComponent<ScriptMachine>(), true));
            var watch = Variables.Object(GameObject.Find("FleetWatch"));
            g.Set("TestPilot", true);
            g.Set("Piloting", true);
            watch.Set("PilotVehicle", go);
            watch.Set("HasPilotVehicle", true);
            watch.Set("AdvancedControls", true);
            return (go, Variables.Object(go), g);
        }

        static void Land(string name)
        {
            var go = GameObject.Find("Aircraft").transform.Find(name).gameObject;
            var g = Variables.Graph(GraphReference.New(go.GetComponent<ScriptMachine>(), true));
            g.Set("Piloting", false);
            g.Set("TestPilot", false);
            Variables.Object(GameObject.Find("FleetWatch")).Set("HasPilotVehicle", false);
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; return; }
            var now = EditorApplication.timeSinceStartup;
            if (started < 0) { started = now; step = 0; stepAt = now + 4; log = new StringBuilder(); return; }
            if (now < stepAt) return;
            double wait = 1.5;
            switch (step)
            {
                case 0: Fly("Stunt Plane"); break;
                case 1:
                {
                    var (go, own, g) = Fly("Stunt Plane");
                    string side = SideOf(own, "Toggle1");
                    Pick(own, side, "Toggle1");
                    Raise("OnTriggerAxisUpdate", side == "L" ? 0 : 1, 1f);
                    Raise("OnTriggerAxisUpdate", side == "L" ? 0 : 1, 0f);
                    wait = 0.5;
                    break;
                }
                case 2:
                {
                    var (go, own, g) = Fly("Stunt Plane");
                    var objects = ((IEnumerable)own.Get("Toggle1Objects")).Cast<GameObject>().ToList();
                    var emitters = ((IEnumerable)own.Get("Toggle1Emission")).Cast<ParticleSystem>().ToList();
                    Check("Stunt Plane switch 1 turns on", (bool)g.Get("Toggle1On") && objects.All(o => o.activeSelf) && emitters.All(p => p.emission.enabled) && objects.Count + emitters.Count > 0,
                        $"on {g.Get("Toggle1On")}, {objects.Count(o => o.activeSelf)} of {objects.Count} objects shown, {emitters.Count(p => p.emission.enabled)} of {emitters.Count} emitting");
                    Land("Stunt Plane");
                    Fly("Wasp WX3");
                    break;
                }
                case 3:
                {
                    var (go, own, g) = Fly("Wasp WX3");
                    string side = SideOf(own, "Boost2");
                    Pick(own, side, "Boost2");
                    velocityBefore = go.GetComponent<Rigidbody>().linearVelocity;
                    Raise("OnTriggerAxisUpdate", side == "L" ? 0 : 1, 1f);
                    wait = 1.0;
                    break;
                }
                case 4:
                {
                    var (go, own, g) = Fly("Wasp WX3");
                    string side = SideOf(own, "Boost2");
                    var gained = go.GetComponent<Rigidbody>().linearVelocity - velocityBefore;
                    var anim = go.GetComponent<Animator>();
                    string floatName = (string)own.Get("Boost2Float");
                    Check("Wasp boost 2 pushes", (bool)g.Get("Boost2On") && gained.magnitude > 3f && (floatName.Length == 0 || anim.GetFloat(floatName) > 0.5f),
                        $"on {g.Get("Boost2On")}, +{gained.magnitude:F1} m/s in 1 s, {floatName} {(floatName.Length > 0 ? anim.GetFloat(floatName) : 0f):F0}, left {(float)g.Get("Boost2Left"):F2}");
                    Raise("OnTriggerAxisUpdate", side == "L" ? 0 : 1, 0f);
                    Land("Wasp WX3");
                    Fly("SD-1");
                    break;
                }
                case 5:
                {
                    var (go, own, g) = Fly("SD-1");
                    string side = SideOf(own, "Handling");
                    Pick(own, side, "Handling");
                    Raise("OnTriggerAxisUpdate", side == "L" ? 0 : 1, 1f);
                    Raise("OnTriggerAxisUpdate", side == "L" ? 0 : 1, 0f);
                    wait = 0.5;
                    break;
                }
                case 6:
                {
                    var (go, own, g) = Fly("SD-1");
                    Check("SD-1 thrust reverser", (bool)g.Get("HandlingOn") && (float)own.Get("HandlingRatio") < 0f, $"on {g.Get("HandlingOn")}, thrust x {own.Get("HandlingRatio")}");
                    Land("SD-1");
                    log.AppendLine("DONE");
                    File.WriteAllText(LogPath, log.ToString());
                    SessionState.SetBool(ArmedKey, false);
                    EditorApplication.update -= Tick;
                    started = -1;
                    EditorApplication.isPlaying = false;
                    return;
                }
            }
            step++;
            stepAt = now + wait;
        }
    }
}
