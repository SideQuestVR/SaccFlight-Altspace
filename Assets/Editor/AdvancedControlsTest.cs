using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// The advanced controls in Play mode, without a headset. Arm it (FlightWorld/Flight/Test/Test Advanced Controls,
    /// vehicle name in Library/FlightWorld/test_target.txt, default "SF-1 1") and press Play. It makes the local
    /// player the vehicle's (test) pilot with the board's switch on, then raises the same controller events the
    /// app raises (grips, thumbsticks, triggers) and holds the two hand objects where a player's hands would be,
    /// moved and turned by known amounts. Each step's result is checked against what Sacc's maths give.
    /// Output: Logs/advanced_test.txt (PASS / FAIL lines, then DONE).
    /// </summary>
    [InitializeOnLoad]
    public static class AdvancedControlsTest
    {
        const string ArmedKey = "FlightWorld.AdvancedControlsTest.Armed";
        const string LogPath = "Logs/advanced_test.txt";
        static double started = -1;
        static int step;
        static readonly StringBuilder log = new StringBuilder();
        static int failures;

        static AdvancedControlsTest()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Test Advanced Controls")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(LogPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static string Target()
        {
            var path = "Library/FlightWorld/test_target.txt";
            var name = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            return name.Length > 0 ? name : "SF-1 1";
        }

        static void Raise(string hook, params object[] arguments) => EventBus.Trigger(hook, new CustomEventArgs(hook, arguments));

        static void Check(string what, bool ok, string detail)
        {
            if (!ok) failures++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}: {detail}");
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; step = 0; return; }
            var vehicle = GameObject.Find("Aircraft")?.transform.Find(Target())?.gameObject;
            var watch = GameObject.Find("FleetWatch");
            if (vehicle == null || watch == null) return;
            if (started < 0) { started = EditorApplication.timeSinceStartup; log.Clear(); failures = 0; step = 0; }
            double t = EditorApplication.timeSinceStartup - started;
            var machine = vehicle.GetComponent<ScriptMachine>();
            var vars = Variables.Graph(GraphReference.New(machine, true));
            var own = Variables.Object(vehicle);
            var clock = Variables.Object(watch);
            var root = vehicle.transform;
            var left = (Transform)clock.Get("LeftHand");
            var right = (Transform)clock.Get("RightHand");
            bool switched = own.Get("SwitchHandsJoyThrottle") is bool s && s;
            var stickHand = switched ? left : right;
            var throttleHand = switched ? right : left;
            int stickSide = switched ? 0 : 1, throttleSide = switched ? 1 : 0;
            float F(string name) => System.Convert.ToSingle(vars.Get(name));
            float P(string name) => System.Convert.ToSingle(own.Get(name));

            // The hands, where the editor's attachment code would put them: held by this test instead.
            foreach (var hand in new[] { left, right })
                foreach (var constraint in hand.GetComponents<UnityEngine.Animations.ParentConstraint>()) constraint.constraintActive = false;
            var eye = ((Transform)own.Get("EyePoint")).position;
            Quaternion stickTurn = Quaternion.identity;
            Vector3 throttleShift = Vector3.zero;

            if (step == 0 && t > 4)
            {
                vars.Set("TestPilot", true);
                vars.Set("Piloting", true);
                vars.Set("Throttle", 0f);
                // No thrust for this test (Play mode only): a parked vehicle that rolls away under its own throttle moves
                // between this script placing the hands and the graph reading them, which reads as the hand drawing back.
                own.Set("ThrottleStrength", 0f);
                own.Set("ThrottleIsUpThrust", false);
                clock.Set("PilotVehicle", vehicle);
                clock.Set("HasPilotVehicle", true);
                clock.Set("AdvancedControls", true);
                log.AppendLine($"vehicle {vehicle.name}, stick in the {(switched ? "left" : "right")} hand, max angles {P("MaxJoyX")}/{P("MaxJoyY")}/{P("MaxJoyZ")}");
                step = 1;
            }
            // the pose this step holds the hands in (re-applied every editor tick)
            float turnPitch = step == 3 ? 20f : 0f, turnYaw = step == 4 ? 15f : 0f, turnRoll = step == 5 ? 20f : 0f;
            if (turnPitch > 0f) stickTurn = Quaternion.FromToRotation(root.up, (root.up + Mathf.Tan(turnPitch * Mathf.Deg2Rad) * root.forward).normalized);
            if (turnYaw > 0f) stickTurn = Quaternion.FromToRotation(root.forward, (root.forward + Mathf.Tan(turnYaw * Mathf.Deg2Rad) * root.right).normalized);
            if (turnRoll > 0f) stickTurn = Quaternion.FromToRotation(root.up, (root.up + Mathf.Tan(turnRoll * Mathf.Deg2Rad) * root.right).normalized);
            if (step == 8) throttleShift = root.forward * 0.05f;
            if (step == 9) throttleShift = root.forward * 0.02f;
            stickHand.SetPositionAndRotation(eye + root.right * (switched ? -0.25f : 0.25f) - root.up * 0.45f + root.forward * 0.35f,
                stickTurn * root.rotation * Quaternion.Euler(10f, 0f, 30f));      // (any starting attitude: only the change counts)
            throttleHand.SetPositionAndRotation(eye + root.right * (switched ? 0.3f : -0.3f) - root.up * 0.45f + root.forward * 0.3f + throttleShift,
                root.rotation * Quaternion.Euler(-20f, 40f, 0f));

            double at = 4 + step * 1.0;
            if (t < at) return;
            switch (step)
            {
                case 1:
                    Check("switch noticed", vars.Get("Advanced") is bool a && a && vars.Get("AdvancedShown") is bool b && b,
                        $"Advanced {vars.Get("Advanced")} shown {vars.Get("AdvancedShown")}");
                    var shown = ((System.Collections.IEnumerable)own.Get("DialDisplays")).Cast<GameObject>().ToList();
                    Check("dial displays on", shown.Count == 0 || shown.All(d => d.activeSelf), $"{shown.Count(d => d.activeSelf)} of {shown.Count} on");
                    Raise("OnControllerButtonPressed", 1, stickSide);
                    break;
                case 2:
                    Check("stick taken, centred", vars.Get("StickHeld") is bool h && h && Mathf.Abs(F("StickPitch")) < 0.02f && Mathf.Abs(F("StickRoll")) < 0.02f && Mathf.Abs(F("StickYaw")) < 0.02f,
                        $"held {vars.Get("StickHeld")} pitch {F("StickPitch"):F3} yaw {F("StickYaw"):F3} roll {F("StickRoll"):F3}");
                    break;
                case 3:
                    Check("hand tilted forward 20 deg = nose down", Mathf.Abs(F("StickPitch") - 20f / P("MaxJoyX")) < 0.03f && Mathf.Abs(F("StickRoll")) < 0.03f,
                        $"pitch {F("StickPitch"):F3} (want {20f / P("MaxJoyX"):F3}) roll {F("StickRoll"):F3} yaw {F("StickYaw"):F3}; animator pitchinput {Anim(vehicle, "pitchinput"):F3}");
                    break;
                case 4:
                    Check("hand twisted right 15 deg = yaw right", Mathf.Abs(F("StickYaw") - 15f / P("MaxJoyY")) < 0.03f && Mathf.Abs(F("StickPitch")) < 0.03f,
                        $"yaw {F("StickYaw"):F3} (want {15f / P("MaxJoyY"):F3}) pitch {F("StickPitch"):F3} roll {F("StickRoll"):F3}");
                    break;
                case 5:
                    Check("hand tilted right 20 deg = roll right", Mathf.Abs(F("StickRoll") + 20f / P("MaxJoyZ")) < 0.03f && Mathf.Abs(F("StickPitch")) < 0.03f,
                        $"roll {F("StickRoll"):F3} (want {-20f / P("MaxJoyZ"):F3}) pitch {F("StickPitch"):F3} InRoll {F("InRoll"):F3}");
                    Raise("OnControllerButtonReleased", 1, stickSide);
                    break;
                case 6:
                    Check("stick let go, centred", !(vars.Get("StickHeld") is bool h2 && h2) && F("StickRoll") == 0f,
                        $"held {vars.Get("StickHeld")} roll {F("StickRoll"):F3}");
                    Raise("OnControllerButtonPressed", 1, throttleSide);
                    break;
                case 7:
                    Check("throttle taken", vars.Get("ThrottleHeld") is bool th && th && F("Throttle") < 0.01f, $"held {vars.Get("ThrottleHeld")} throttle {F("Throttle"):F3}");
                    break;
                case 8:
                {
                    bool vertical = own.Get("VerticalThrottle") is bool v && v;
                    float want = vertical ? 0f : Mathf.Min(0.05f * P("ThrottleSensitivity"), 1f);
                    Check("hand 5 cm forward", Mathf.Abs(F("Throttle") - want) < 0.03f, $"throttle {F("Throttle"):F3} (want {want:F3}{(vertical ? ", vertical throttle" : "")}); animator throttle {Anim(vehicle, "throttle"):F3}");
                    break;
                }
                case 9:
                {
                    bool vertical = own.Get("VerticalThrottle") is bool v && v;
                    float want = vertical ? 0f : Mathf.Min(0.02f * P("ThrottleSensitivity"), 1f);
                    Check("hand back to 2 cm forward", Mathf.Abs(F("Throttle") - want) < 0.03f, $"throttle {F("Throttle"):F3} (want {want:F3})");
                    Raise("OnControllerButtonReleased", 1, throttleSide);
                    Pick("R", own, 1);
                    break;
                }
                case 10:
                {
                    float gearSlot = P("RGearSlot");
                    Check("right dial: gear picked", gearSlot < 0f || F("RSel") == gearSlot, $"RSel {F("RSel")} gear slot {gearSlot}; highlighter z {((Transform)own.Get("RHighlighter")).localEulerAngles.z:F1}");
                    if (gearSlot >= 0f) { Raise("OnTriggerAxisUpdate", 1, 1f); Raise("OnTriggerAxisUpdate", 1, 0f); }
                    break;
                }
                case 11:
                {
                    float gearSlot = P("RGearSlot");
                    var lit = ((System.Collections.IEnumerable)own.Get("GearLit")).Cast<GameObject>().ToList();
                    Check("right trigger: gear up, its mark dark", gearSlot < 0f || (vars.Get("GearUp") is bool up && up && lit.All(l => !l.activeSelf)),
                        $"GearUp {vars.Get("GearUp")}, {lit.Count(l => l.activeSelf)} of {lit.Count} gear marks lit");
                    if (gearSlot >= 0f) { Raise("OnTriggerAxisUpdate", 1, 1f); Raise("OnTriggerAxisUpdate", 1, 0f); }    // and down again
                    Pick("L", own, 0);
                    break;
                }
                case 12:
                {
                    float brakeSlot = P("LBrakeSlot");
                    Check("left dial: brake picked, gear down again", (brakeSlot < 0f || F("LSel") == brakeSlot) && !(vars.Get("GearUp") is bool up && up),
                        $"LSel {F("LSel")} brake slot {brakeSlot} GearUp {vars.Get("GearUp")}");
                    Raise("OnTriggerAxisUpdate", 0, 0.6f);
                    break;
                }
                case 13:
                {
                    float brakeSlot = P("LBrakeSlot");
                    Check("left trigger 0.6 = brakes 0.6", brakeSlot < 0f || Mathf.Abs(F("TriggerBrake") - 0.6f) < 0.01f, $"TriggerBrake {F("TriggerBrake"):F2}, Brake {F("Brake"):F2}");
                    Raise("OnTriggerAxisUpdate", 0, 0f);
                    Raise("OnControllerAxisUpdate", 1, 0.3f, 0.9f);         // a thumbstick must not fly the aircraft now
                    break;
                }
                case 14:
                    Check("thumbstick does not move the stick", F("StickPitch") == 0f && F("StickRoll") == 0f, $"pitch {F("StickPitch"):F2} roll {F("StickRoll"):F2}");
                    Raise("OnControllerAxisUpdate", 1, 0f, 0f);
                    clock.Set("AdvancedControls", false);
                    break;
                case 15:
                {
                    var remaining = ((System.Collections.IEnumerable)own.Get("DialDisplays")).Cast<GameObject>().ToList();
                    Check("switched off: displays gone", !(vars.Get("Advanced") is bool a2 && a2) && remaining.All(d => !d.activeSelf), $"Advanced {vars.Get("Advanced")}, {remaining.Count(d => d.activeSelf)} displays on");
                    Raise("OnControllerAxisUpdate", 1, 0f, 1f);
                    break;
                }
                case 16:
                    Check("switched off: thumbstick flies again", F("StickPitch") > 0.9f, $"StickPitch {F("StickPitch"):F2}");
                    Raise("OnControllerAxisUpdate", 1, 0f, 0f);
                    log.AppendLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
                    log.AppendLine("DONE");
                    File.WriteAllText(LogPath, log.ToString());
                    SessionState.SetBool(ArmedKey, false);
                    EditorApplication.update -= Tick;
                    started = -1;
                    step = 0;
                    EditorApplication.isPlaying = false;
                    return;
            }
            File.WriteAllText(LogPath, log.ToString());
            step++;
        }

        static float Anim(GameObject vehicle, string name) => vehicle.GetComponent<Animator>().GetFloat(name);

        /// <summary>Push a thumbstick toward the dial slot of the function wanted, as SaccEntity reads the stick.</summary>
        static void Pick(string side, VariableDeclarations own, int hand)
        {
            float slot = System.Convert.ToSingle(own.Get(side + (side == "R" ? "GearSlot" : "BrakeSlot")));
            if (slot < 0f) return;
            float degrees = System.Convert.ToSingle(own.Get(side + "DialDegrees"));
            var check = (Vector2)own.Get(side + "DialCheck");
            float signed = 180f - degrees * (slot + 0.5f);                   // SignedAngle(check, stick) for the middle of that slice
            float r = signed * Mathf.Deg2Rad;
            var stick = new Vector2(check.x * Mathf.Cos(r) - check.y * Mathf.Sin(r), check.x * Mathf.Sin(r) + check.y * Mathf.Cos(r));
            Raise("OnControllerAxisUpdate", hand, stick.x, stick.y);
            Raise("OnControllerAxisUpdate", hand, 0f, 0f);
        }
    }
}
