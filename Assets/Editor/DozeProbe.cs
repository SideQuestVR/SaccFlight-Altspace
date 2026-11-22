using System;
using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Checks the fleet watch (FlightGraphs.BuildFleetWatch) in Play mode: do parked vehicles away from the
    /// player switch their graphs off, and does each of the ways back work - the vehicle being moved, dozing
    /// being turned off, and the player coming near? Arm it and enter Play mode; it writes Logs/doze_probe.txt
    /// and leaves Play mode. The last line is PASS or FAIL.
    /// </summary>
    [InitializeOnLoad]
    public static class DozeProbe
    {
        const string ArmedKey = "FlightWorld.DozeProbe.Armed";
        const string ReportPath = "Logs/doze_probe.txt";
        static double started = -1;
        static int step;
        static readonly StringBuilder text = new StringBuilder();
        static GameObject moved, approached;
        static bool failed;

        static DozeProbe()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Tools/Probe Dozing")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(ReportPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static GameObject Watch => GameObject.Find("FleetWatch");

        static GameObject[] Vehicles() =>
            ((AotList)Variables.Object(Watch).Get("Vehicles")).Cast<GameObject>().Where(v => v != null).ToArray();

        static bool Dozing(GameObject vehicle) => !vehicle.GetComponent<ScriptMachine>().enabled;

        static Transform Player()
        {
            var controller = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)
                .FirstOrDefault(b => b.GetType().Name == "BSDesktopController" || b.GetType().Name == "FlexaMover");
            return controller != null ? controller.transform : null;
        }

        static void Check(string what, bool ok)
        {
            text.AppendLine((ok ? "ok    " : "WRONG ") + what);
            if (!ok) failed = true;
        }

        /// <summary>Why a vehicle that is not dozing is still awake: its graph's own state.</summary>
        static string Why(GameObject vehicle)
        {
            var graph = Variables.Graph(GraphReference.New(vehicle.GetComponent<ScriptMachine>(), true));
            string Value(string name) => graph.IsDefined(name) ? Convert.ToString(graph.Get(name), System.Globalization.CultureInfo.InvariantCulture) : "-";
            var body = vehicle.GetComponent<Rigidbody>();
            return $"      {vehicle.name}: Awake {Value("Awake")}, IdleTimer {Value("IdleTimer")}, MovedSpeed {Value("MovedSpeed")}, DozeWait {Value("DozeWait")}, " +
                   $"Dead {Value("Dead")}, StartHold {Value("StartHold")}, Eject {Value("EjectTimer")}, Brake {Value("BrakeTimer")}, Reset {Value("ResetTimer")}, " +
                   $"body speed {(body != null ? body.linearVelocity.magnitude : 0f):F2} spin {(body != null ? body.angularVelocity.magnitude : 0f):F2}, at {vehicle.transform.position}, " +
                   $"DozeBeyond {Variables.Object(vehicle).Get("DozeBeyond")}";
        }

        static string Census()
        {
            var vehicles = Vehicles();
            var player = Player();
            return $"{vehicles.Count(Dozing)} of {vehicles.Length} dozing; awake: " + string.Join(", ", vehicles.Where(v => !Dozing(v))
                .Select(v => v.name + (player != null ? $" ({Vector3.Distance(player.position, v.transform.position):F0} m)" : "")));
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; step = 0; return; }
            if (started < 0) { started = EditorApplication.timeSinceStartup; text.Clear(); failed = false; return; }
            double t = EditorApplication.timeSinceStartup - started;
            try
            {
                if (step == 0 && t > 16)
                {
                    var player = Player();
                    text.AppendLine($"player object: {(player != null ? player.name + " at " + player.position : "not found")}");
                    text.AppendLine("after 16 s: " + Census());
                    foreach (var awake in Vehicles().Where(v => !Dozing(v))) text.AppendLine(Why(awake));
                    var dozing = Vehicles().Where(Dozing).ToArray();
                    Check("some vehicles doze", dozing.Length > 0);
                    Check("every dozing vehicle says so in its variables", dozing.All(v => (bool)Variables.Object(v).Get("Dozing")));
                    // 1. a dozing vehicle is moved from outside (as the app does when it sends an idle vehicle home)
                    moved = dozing.FirstOrDefault(v => v.name == "Baron") ?? dozing.FirstOrDefault();
                    if (moved != null)
                    {
                        moved.transform.position += moved.transform.right * 1.5f;
                        var body = moved.GetComponent<Rigidbody>();
                        if (body != null) body.position = moved.transform.position;
                    }
                    step = 1;
                }
                else if (step == 1 && t > 17.3)
                {
                    if (moved != null) Check($"{moved.name}, moved 1.5 m, woke within 1.3 s", !Dozing(moved));
                    step = 2;
                }
                else if (step == 2 && t > 23)
                {
                    if (moved != null) Check($"{moved.name} dozed off again at its new place", Dozing(moved));
                    // 2. dozing turned off for the whole fleet
                    Variables.Object(Watch).Set("DozeOn", false);
                    step = 3;
                }
                else if (step == 3 && t > 24.3)
                {
                    text.AppendLine("dozing turned off: " + Census());
                    Check("with dozing turned off every graph runs", Vehicles().All(v => !Dozing(v)));
                    Variables.Object(Watch).Set("DozeOn", true);
                    step = 4;
                }
                else if (step == 4 && t > 30)
                {
                    text.AppendLine("dozing turned on again: " + Census());
                    Check("turned on again, vehicles doze again", Vehicles().Any(Dozing));
                    // 3. the player comes near a dozing vehicle
                    var player = Player();
                    approached = Vehicles().Where(Dozing).OrderByDescending(v => player != null ? Vector3.Distance(player.position, v.transform.position) : 0f)
                        .FirstOrDefault(v => v.name != "SS-1");
                    if (player != null && approached != null)
                    {
                        var controller = player.GetComponent<CharacterController>();
                        if (controller != null) controller.enabled = false;
                        var body = player.GetComponent<Rigidbody>();
                        var beside = approached.transform.position + approached.transform.right * 4f + Vector3.up * 1.5f;
                        player.position = beside;
                        if (body != null) { body.position = beside; body.linearVelocity = Vector3.zero; }
                        if (controller != null) controller.enabled = true;
                    }
                    step = 5;
                }
                else if (step == 5 && t > 31.5)
                {
                    var player = Player();
                    if (player != null && approached != null)
                    {
                        float distance = Vector3.Distance(player.position, approached.transform.position);
                        text.AppendLine($"player put beside {approached.name}; now {distance:F1} m from it");
                        Check($"{approached.name} woke when the player came near", !Dozing(approached));
                    }
                    else Check("a player object to move and a dozing vehicle to approach were found", false);
                    text.AppendLine("at the end: " + Census());
                    text.AppendLine(failed ? "FAIL" : "PASS");
                    Finish();
                }
            }
            catch (Exception e)
            {
                text.AppendLine("FAIL: " + e);
                Finish();
            }
        }

        static void Finish()
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(ReportPath, text.ToString());
            SessionState.SetBool(ArmedKey, false);
            EditorApplication.update -= Tick;
            started = -1;
            step = 0;
            EditorApplication.isPlaying = false;
        }
    }
}
