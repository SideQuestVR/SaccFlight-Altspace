using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// The dial functions in Play mode (FlightWorld/Flight/Test/Test Dial Functions, then Play). "SF-1 1" is held
    /// still 300 m up with a test pilot and the advanced controls on; "SF-1 2" is held 300 m ahead of it at the same
    /// height and counts as crewed. Each step picks a function on a dial with the thumbstick, as SaccEntity reads it,
    /// and pulls or holds the trigger through the same events the app raises; the next step checks what happened.
    /// Output: Logs/functions_test.txt (PASS / FAIL lines, then DONE).
    /// </summary>
    [InitializeOnLoad]
    public static class FunctionsTest
    {
        const string ArmedKey = "FlightWorld.FunctionsTest.Armed";
        const string LogPath = "Logs/functions_test.txt";
        static double started = -1, stepAt;
        static int step;
        static readonly StringBuilder log = new StringBuilder();
        static int failures;
        static float targetHealth;
        static bool holdRight, holdLeft;

        static FunctionsTest()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Test Dial Functions")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(LogPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Raise(string hook, params object[] arguments) => EventBus.Trigger(hook, new CustomEventArgs(hook, arguments));
        static void Check(string what, bool ok, string detail)
        {
            if (!ok) failures++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}: {detail}");
        }

        /// <summary>Push the thumbstick toward a dial slot and back (hand 0 = left dial, 1 = right).</summary>
        static void Pick(VariableDeclarations own, string side, string kind)
        {
            float slot = System.Convert.ToSingle(own.Get(side + kind + "Slot"));
            if (slot < 0f) { log.AppendLine($"(no {kind} on the {side} dial)"); return; }
            float degrees = System.Convert.ToSingle(own.Get(side + "DialDegrees"));
            var check = (Vector2)own.Get(side + "DialCheck");
            float r = (180f - degrees * (slot + 0.5f)) * Mathf.Deg2Rad;
            var stick = new Vector2(check.x * Mathf.Cos(r) - check.y * Mathf.Sin(r), check.x * Mathf.Sin(r) + check.y * Mathf.Cos(r));
            int hand = side == "L" ? 0 : 1;
            Raise("OnControllerAxisUpdate", hand, stick.x, stick.y);
            Raise("OnControllerAxisUpdate", hand, 0f, 0f);
        }

        static void Pull(string side) { int hand = side == "L" ? 0 : 1; Raise("OnTriggerAxisUpdate", hand, 1f); Raise("OnTriggerAxisUpdate", hand, 0f); }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; step = 0; return; }
            var fleet = GameObject.Find("Aircraft");
            string shooter = File.Exists("Library/FlightWorld/test_target.txt") ? File.ReadAllText("Library/FlightWorld/test_target.txt").Trim() : "";
            if (shooter.Length == 0 || shooter == "SF-1 2") shooter = "SF-1 1";
            var me = fleet?.transform.Find(shooter)?.gameObject;
            var them = fleet?.transform.Find("SF-1 2")?.gameObject;
            var watch = GameObject.Find("FleetWatch");
            var weapons = GameObject.Find("WeaponsWorld");
            if (me == null || them == null || watch == null || weapons == null) return;
            if (started < 0) { started = EditorApplication.timeSinceStartup; stepAt = 5; log.Clear(); failures = 0; step = 0; }
            double t = EditorApplication.timeSinceStartup - started;
            var vars = Variables.Graph(GraphReference.New(me.GetComponent<ScriptMachine>(), true));
            var theirs = Variables.Graph(GraphReference.New(them.GetComponent<ScriptMachine>(), true));
            var own = Variables.Object(me);
            var clock = Variables.Object(watch);
            var world = Variables.Object(weapons);
            object V(string name) => vars.Get(name);
            float F(string name) => System.Convert.ToSingle(vars.Get(name));
            bool B(string name) => vars.Get(name) is bool b && b;
            var anim = me.GetComponent<Animator>();

            // held where the test wants them: this one 300 m up, the other 300 m ahead of it
            var spawn = (Vector3)vars.Get("SpawnPosition");
            var at = spawn + Vector3.up * 300f;
            foreach (var (vehicle, place) in new[] { (me, at), (them, at + me.transform.forward * 300f) })
            {
                var body = vehicle.GetComponent<Rigidbody>();
                body.constraints = RigidbodyConstraints.FreezeAll;
                body.position = place;
                vehicle.transform.position = place;
            }
            theirs.Set("FlownTimer", 8f);              // (counts as crewed: somebody else flies it)
            theirs.Set("TestPilot", true);             // (and this client is the one responsible for it: nobody owns it in the editor)
            if (holdRight) Raise("OnTriggerAxisUpdate", 1, 1f);
            if (holdLeft) Raise("OnTriggerAxisUpdate", 0, 1f);

            if (step == 0)
            {
                if (t < 4) return;
                vars.Set("TestPilot", true);
                vars.Set("Piloting", true);
                clock.Set("PilotVehicle", me);
                clock.Set("HasPilotVehicle", true);
                clock.Set("AdvancedControls", true);
                step = 1;
                stepAt = t + 1.5;
                return;
            }
            if (t < stepAt) return;
            double wait = 1.0;
            switch (step)
            {
                case 1:
                    Check("set up", B("Advanced") && !B("Taxiing") && Variables.Object(them).Get("Crewed") is bool c && c,
                        $"advanced {V("Advanced")} taxiing {V("Taxiing")} target crewed {Variables.Object(them).Get("Crewed")}");
                    Pick(own, "L", "Engine"); Pull("L"); Pull("L");     // a double pull stops a running engine
                    break;
                case 2:
                    Check("engine: double pull stops it", B("EngineOff") && !anim.GetBool("EngineOn"), $"EngineOff {V("EngineOff")} anim EngineOn {anim.GetBool("EngineOn")}");
                    break;
                case 3:
                    Pull("L");                                           // (the toggle delay has passed) a pull starts it
                    Check("engine: a pull starts it", F("EngineStarting") > 0f, $"starting {F("EngineStarting"):F1} s");
                    wait = 10.0;            // (game time runs slower than the clock in a busy editor)
                    break;
                case 4:
                    Check("engine: running after its start-up time", !B("EngineOff") && anim.GetBool("EngineOn"), $"EngineOff {V("EngineOff")} anim {anim.GetBool("EngineOn")}");
                    Pick(own, "L", "Canopy"); Pull("L");
                    break;
                case 5:
                    Check("canopy opens", B("CanopyOpen"), $"CanopyOpen {V("CanopyOpen")} anim canopyopen {anim.GetBool("canopyopen")}");
                    Pick(own, "R", "Hook"); Pull("R");
                    break;
                case 6:
                    Check("hook down", System.Convert.ToSingle(own.Get("RHookSlot")) < 0f || (B("HookDown") && anim.GetBool("hookdown")), $"HookDown {V("HookDown")} anim {anim.GetBool("hookdown")}");
                    Pick(own, "R", "Smoke"); Pull("R");
                    break;
                case 7:
                {
                    var smoke = ((System.Collections.IEnumerable)own.Get("SmokeSystems")).Cast<ParticleSystem>().ToList();
                    Check("smoke on", B("SmokeOn") && smoke.All(s => s.emission.enabled), $"SmokeOn {V("SmokeOn")}, {smoke.Count(s => s.emission.enabled)} of {smoke.Count} emitting");
                    Pick(own, "L", "Flares"); holdLeft = true;
                    wait = 0.6;
                    break;
                }
                case 8:
                    holdLeft = false; Raise("OnTriggerAxisUpdate", 0, 0f);
                    Check("flares", F("FlareCount") < System.Convert.ToSingle(own.Get("FlaresFull")) && System.Convert.ToSingle(own.Get("FlareTime")) > 0f,
                        $"{F("FlareCount")} of {own.Get("FlaresFull")} left; LSel {F("LSel")} flares slot {own.Get("LFlaresSlot")} TrigL {F("TrigL")} next {F("FlareNext"):F1} time {Time.time:F1}");
                    Pick(own, "L", "Cruise"); Pull("L");
                    break;
                case 9:
                    Check("cruise on", B("CruiseOn"), $"CruiseOn {V("CruiseOn")} speed {F("CruiseSpeed"):F1}");
                    Pull("L"); Pull("L");                                // a double pull stops it
                    Pick(own, "L", "Althold"); Pull("L");
                    break;
                case 10:
                    Check("cruise off, altitude hold on", !B("CruiseOn") && B("AltHoldOn"), $"CruiseOn {V("CruiseOn")} AltHoldOn {V("AltHoldOn")} out pitch {F("AHOutPitch"):F2} roll {F("AHOutRoll"):F2}");
                    Pull("L");
                    Pick(own, "R", "Aam");                              // missiles: search and lock (1.5 s)
                    wait = 2.5;
                    break;
                case 11:
                    Check("missile locked on the other SF-1", B("AAMLocked") && V("AAMTarget") as GameObject == them, $"locked {V("AAMLocked")} target {(V("AAMTarget") as GameObject)?.name} timer {F("AAMLockTimer"):F1}");
                    targetHealth = System.Convert.ToSingle(theirs.Get("Health"));
                    Pull("R");
                    wait = 0.5;
                    break;
                case 12:
                    Check("missile away", F("AAMCount") < System.Convert.ToSingle(own.Get("AAMFull")) && System.Convert.ToSingle(world.Get("LiveCount")) > 0.5f,
                        $"{F("AAMCount")} left, {world.Get("LiveCount")} round(s) in the air");
                    wait = 3.0;
                    break;
                case 13:
                {
                    float health = System.Convert.ToSingle(theirs.Get("Health"));
                    bool dead = theirs.Get("Dead") is bool d && d;
                    var aams = ((System.Collections.IEnumerable)own.Get("AamPool")).Cast<GameObject>().Select(r => $"{r.name} ended {Variables.Object(r).Get("Ended")} lost {Variables.Object(r).Get("Lost")} at {r.transform.position}");
                    Check("missile hit the other SF-1", health < targetHealth - 0.01f || dead, $"its health {targetHealth:F1} -> {health:F1}, dead {dead}; " + string.Join("; ", aams.Take(2)));
                    wait = 7.0;                                          // (let it come back if it blew up)
                    break;
                }
                case 14:
                    targetHealth = System.Convert.ToSingle(theirs.Get("Health"));
                    Pick(own, "R", "Gun"); holdRight = true;
                    wait = 0.6;
                    break;
                case 15:
                {
                    holdRight = false; Raise("OnTriggerAxisUpdate", 1, 0f);
                    float health = System.Convert.ToSingle(theirs.Get("Health"));
                    bool dead = theirs.Get("Dead") is bool d && d;
                    Check("gun: ammunition used, target hit", F("GunAmmo") < System.Convert.ToSingle(own.Get("GunAmmoFull")) && (health < targetHealth - 0.01f || dead),
                        $"ammo {F("GunAmmo"):F1} of {own.Get("GunAmmoFull")}; its health {targetHealth:F1} -> {health:F1}, dead {dead}");
                    Pick(own, "R", "Bomb"); holdRight = true;
                    wait = 0.1;
                    break;
                }
                case 16:
                    holdRight = false; Raise("OnTriggerAxisUpdate", 1, 0f);
                    Check("bomb away", F("BombCount") < System.Convert.ToSingle(own.Get("BombFull")), $"{F("BombCount")} left, {world.Get("LiveCount")} round(s) in the air");
                    step = 69;                                           // (on to the extra steps 70-72, then back to 17)
                    Pick(own, "L", "Limits"); Pull("L");                 // limits: on at the start, a pull turns them off
                    wait = 0.5;
                    break;
                case 70:
                    Check("limits off", !(own.Get("HasLimits") is bool hl && hl) || !B("LimitsOn"), $"has limits {own.Get("HasLimits")} on {V("LimitsOn")}");
                    if (System.Convert.ToSingle(own.Get("RBomb2Slot")) >= 0f || System.Convert.ToSingle(own.Get("LBomb2Slot")) >= 0f)
                    {
                        string side = System.Convert.ToSingle(own.Get("RBomb2Slot")) >= 0f ? "R" : "L";
                        Pick(own, side, "Bomb2");
                        if (side == "R") holdRight = true; else holdLeft = true;
                    }
                    wait = 0.15;
                    break;
                case 71:
                    holdRight = holdLeft = false; Raise("OnTriggerAxisUpdate", 1, 0f); Raise("OnTriggerAxisUpdate", 0, 0f);
                    wait = 0.6;
                    break;
                case 72:
                {
                    var second = ((System.Collections.IEnumerable)own.Get("Bomb2Pool")).Cast<GameObject>().Where(r => Variables.Object(r).Get("Alive") is bool a && a).ToList();
                    if (System.Convert.ToSingle(own.Get("Bomb2Full")) > 0f)
                        Check("second launcher fires ahead", second.Count > 0 && second.All(r => me.transform.InverseTransformPoint(r.transform.position).z > 20f),
                            string.Join(", ", second.Select(r => $"{r.name} {me.transform.InverseTransformPoint(r.transform.position)}")) + $" ({F("Bomb2Count")} left)");
                    Pick(own, "R", "Agm"); Pull("R");                    // guided bomb: a pull locks where the pilot looks
                    wait = 0.5;
                    break;
                }
                case 73:
                    step = 17;                                           // (counts on from here after this step's checks)
                    goto case 17;
                case 17:
                    Check("guided bomb locked", B("AGMLocked"), $"locked {V("AGMLocked")} at {V("AGMPoint")}");
                    Pull("R"); Pull("R");                                // a double pull fires
                    wait = 0.5;
                    break;
                case 18:
                    Check("guided bomb away", F("AGMCount") < System.Convert.ToSingle(own.Get("AGMFull")), $"{F("AGMCount")} left, {world.Get("LiveCount")} round(s) in the air");
                    wait = 24.0;            // (a guided bomb that cannot reach its mark lives 20 s)
                    break;
                case 19:
                {
                    var rounds = ((System.Collections.IEnumerable)own.Get("AllRounds")).Cast<GameObject>().ToList();
                    var flying = rounds.Where(r => Variables.Object(r).Get("Alive") is bool a && a).ToList();
                    Check("rounds went off", flying.All(r => Variables.Object(r).Get("Exploded") is bool e && e) || flying.Count == 0,
                        string.Join(", ", flying.Select(r => $"{r.name} exploded {Variables.Object(r).Get("Exploded")} at {r.transform.position} ({Vector3.Distance(r.transform.position, me.transform.position):F0} m away)")) +
                        " | ended: " + string.Join(", ", rounds.Where(r => System.Convert.ToSingle(Variables.Object(r).Get("Ended")) > 0f).Select(r => $"{r.name} {Variables.Object(r).Get("Ended")}")));
                    wait = 11.0;
                    break;
                }
                case 20:
                    Check("all back in their pools", System.Convert.ToSingle(world.Get("LiveCount")) < 0.5f, $"LiveCount {world.Get("LiveCount")}");
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
            stepAt = t + wait;
        }
    }
}
