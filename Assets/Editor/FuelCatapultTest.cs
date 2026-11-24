using System.IO;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Fuel, resupply and the catapult (FlightGraphsFunctions.cs), in Play mode on the SF-1:
    ///  - at its stand with the gun empty and half its fuel gone, both come back by the second (resupply);
    ///  - with fuel under the low mark the engine is held down with it, and with none it gives nothing;
    ///  - put behind the catapult with the catapult picked on its dial it locks on, and a pull throws it down the runway.
    /// Output: Logs/fuel_catapult_test.txt (PASS / FAIL, then DONE).
    /// </summary>
    [InitializeOnLoad]
    public static class FuelCatapultTest
    {
        const string ArmedKey = "FlightWorld.FuelCatapultTest.Armed";
        const string LogPath = "Logs/fuel_catapult_test.txt";
        static double started = -1, stepAt;
        static int step;
        static StringBuilder log;
        static float fuelBefore, gunBefore;

        static FuelCatapultTest()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Test Fuel And Catapult")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(LogPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Raise(string hook, params object[] arguments) => EventBus.Trigger(hook, new CustomEventArgs(hook, arguments));
        static void Check(string what, bool ok, string detail) => log.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}: {detail}");

        static void Pick(VariableDeclarations own, string kind)
        {
            string side = System.Convert.ToSingle(own.Get("L" + kind + "Slot")) >= 0f ? "L" : "R";
            float slot = System.Convert.ToSingle(own.Get(side + kind + "Slot"));
            float degrees = System.Convert.ToSingle(own.Get(side + "DialDegrees"));
            var check = (Vector2)own.Get(side + "DialCheck");
            float r = (180f - degrees * (slot + 0.5f)) * Mathf.Deg2Rad;
            var stick = new Vector2(check.x * Mathf.Cos(r) - check.y * Mathf.Sin(r), check.x * Mathf.Sin(r) + check.y * Mathf.Cos(r));
            int hand = side == "L" ? 0 : 1;
            Raise("OnControllerAxisUpdate", hand, stick.x, stick.y);
            Raise("OnControllerAxisUpdate", hand, 0f, 0f);
        }

        static void Pull(VariableDeclarations own, string kind)
        {
            int hand = System.Convert.ToSingle(own.Get("L" + kind + "Slot")) >= 0f ? 0 : 1;
            Raise("OnTriggerAxisUpdate", hand, 1f);
            Raise("OnTriggerAxisUpdate", hand, 0f);
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; return; }
            var go = GameObject.Find("Aircraft")?.transform.Find("SF-1 1")?.gameObject;
            var watchObject = GameObject.Find("FleetWatch");
            if (go == null || watchObject == null) return;
            var g = Variables.Graph(GraphReference.New(go.GetComponent<ScriptMachine>(), true));
            var own = Variables.Object(go);
            var watch = Variables.Object(watchObject);
            var body = go.GetComponent<Rigidbody>();
            var now = EditorApplication.timeSinceStartup;
            if (started < 0) { started = now; step = 0; stepAt = now + 4; log = new StringBuilder(); return; }
            float F(string name) => System.Convert.ToSingle(g.Get(name));
            float P(string name) => System.Convert.ToSingle(own.Get(name));
            if (step >= 4 && step <= 7) g.Set("Throttle", 1f);          // (held open through the fuel checks)
            if (now < stepAt) return;
            double wait = 1.5;
            switch (step)
            {
                case 0:
                    g.Set("TestPilot", true);
                    g.Set("Piloting", true);
                    watch.Set("PilotVehicle", go);
                    watch.Set("HasPilotVehicle", true);
                    watch.Set("AdvancedControls", true);
                    wait = 2.0;
                    break;
                case 1:
                    g.Set("GunAmmo", 0f);
                    g.Set("FuelLeft", P("Fuel") * 0.5f);
                    g.Set("AAMCount", 0f);
                    fuelBefore = F("FuelLeft");
                    gunBefore = F("GunAmmo");
                    wait = 3.5;
                    break;
                case 2:
                    Check("resupply at the stand refills gun, missiles and fuel", F("GunAmmo") > gunBefore + 0.2f && F("FuelLeft") > fuelBefore + 10f && F("AAMCount") > 0.5f,
                        $"gun {gunBefore:F1} -> {F("GunAmmo"):F1} s of {P("GunAmmoFull")}, fuel {fuelBefore:F0} -> {F("FuelLeft"):F0} of {P("Fuel")}, missiles 0 -> {F("AAMCount")} of {P("AAMFull")}");
                    wait = 0.2;
                    break;
                case 3:
                    // away from the stand (no resupply there), held in the air so nothing rolls: fuel checks
                    body.constraints = RigidbodyConstraints.FreezeAll;
                    body.position = go.transform.position + Vector3.up * 400f;
                    go.transform.position = body.position;
                    g.Set("FuelLeft", P("Fuel"));
                    wait = 0.5;
                    break;
                case 4:
                    fuelBefore = F("FuelLeft");
                    wait = 4.0;
                    break;
                case 5:
                    Check("fuel burns with the throttle open", F("FuelLeft") < fuelBefore - 1f, $"{fuelBefore:F1} -> {F("FuelLeft"):F1} in 4 s (consumption {P("FuelConsumption")}/s, x{P("FuelConsumptionABMulti")} in afterburner); engine {F("EngineOutput"):F2}");
                    g.Set("FuelLeft", P("LowFuel") * 0.3f);
                    wait = 4.0;
                    break;
                case 6:
                    Check("low fuel holds the engine down", F("EngineOutput") < 0.4f && F("EngineOutput") > 0.02f, $"engine {F("EngineOutput"):F2} with {F("FuelLeft"):F0} left of a low mark of {P("LowFuel")}");
                    g.Set("FuelLeft", 0f);
                    wait = 5.0;
                    break;
                case 7:
                    Check("no fuel, no thrust", F("EngineOutput") < 0.05f, $"engine {F("EngineOutput"):F2}, health {F("Health"):F0}");
                    g.Set("Throttle", 0f);
                    g.Set("FuelLeft", P("Fuel"));
                    // behind the catapult: its catapult point 1 m short of the shuttle, 6 degrees off its heading
                    var shuttle = (Transform)own.Get("Catapult");
                    var point = (Transform)own.Get("CatapultPoint");
                    go.transform.rotation = Quaternion.Euler(0f, shuttle.eulerAngles.y + 6f, 0f);
                    var want = shuttle.position - shuttle.forward * 1f;
                    var place = go.transform.position + (want - point.position);
                    place.y = shuttle.position.y + 2.5f;
                    body.constraints = RigidbodyConstraints.None;
                    body.position = place;
                    body.rotation = go.transform.rotation;
                    go.transform.position = place;
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    wait = 5.0;         // (drops onto its wheels and settles)
                    break;
                case 8:
                    Check("standing on the runway behind the catapult", (bool)g.Get("Taxiing") && body.linearVelocity.magnitude < 3f,
                        $"health {F("Health"):F0} of {P("FullHealth")}, taxiing {g.Get("Taxiing")}, speed {body.linearVelocity.magnitude:F1}, {Vector3.Distance(((Transform)own.Get("CatapultPoint")).position, ((Transform)own.Get("Catapult")).position):F1} m from the shuttle");
                    Pick(own, "Catapult");
                    wait = 1.0;
                    break;
                case 9:
                {
                    var shuttle2 = (Transform)own.Get("Catapult");
                    float off = Vector3.Angle(go.transform.forward, shuttle2.forward);
                    Check("locks on to the catapult", (bool)g.Get("OnCatapult") && off < 1f && body.constraints == RigidbodyConstraints.FreezeAll, $"health {F("Health"):F0}, on {g.Get("OnCatapult")}, {off:F1} deg off its heading, held {body.constraints}");
                    Pull(own, "Catapult");
                    wait = 3.0;
                    break;
                }
                case 10:
                {
                    var shuttle3 = (Transform)own.Get("Catapult");
                    float along = Vector3.Dot(body.linearVelocity, shuttle3.forward);
                    Check("the catapult throws it down the runway", !(bool)g.Get("OnCatapult") && along > 50f && !(bool)g.Get("Dead"),
                        $"on {g.Get("OnCatapult")}, {along:F0} m/s along the runway {3.0:F0} s after the pull, dead {g.Get("Dead")}, health {F("Health"):F0}");
                    g.Set("Piloting", false);
                    g.Set("TestPilot", false);
                    watch.Set("HasPilotVehicle", false);
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
