using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Gunner seats (GunnerGraphs.cs), in Play mode, one seat after another: made the local gunner (as a click on the seat
    /// does), the turret aimed (the hand object pointed 30 degrees right of and 10 degrees above where the gun rests, or
    /// the right thumbstick held over), both triggers held; then checks that the turret turned, the gun's animator bool
    /// came on (with its sound), and launchers sent rounds into the weapons world. Arm it and enter Play mode.
    /// Output: Logs/gunner_test.txt (PASS / FAIL lines, then DONE).
    /// </summary>
    [InitializeOnLoad]
    public static class GunnerTest
    {
        const string ArmedKey = "FlightWorld.GunnerTest.Armed";
        const string LogPath = "Logs/gunner_test.txt";
        static double started = -1, phaseStart;
        static int index, phase;
        static List<ScriptMachine> gunners;
        static StringBuilder log;
        static float bombsBefore, bomb2Before, liveBefore, restYawBefore;
        static Quaternion handRotationBefore;

        static GunnerTest()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Test Gunner Seats")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(LogPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static VariableDeclarations Graph(ScriptMachine m) => Variables.Graph(GraphReference.New(m, true));
        static VariableDeclarations Obj(Component c) => c.GetComponent<Variables>().declarations;

        static void Check(string what, bool ok, string detail = "") => log.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? ": " + detail : "")}");

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; return; }
            var now = EditorApplication.timeSinceStartup;
            if (started < 0)
            {
                started = now;
                index = 0;
                phase = 0;
                log = new StringBuilder();
                gunners = Object.FindObjectsByType<ScriptMachine>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                    .Where(m => m.name.StartsWith("Gunner ")).OrderBy(m => m.transform.parent.name + m.name).ToList();
                log.AppendLine($"{gunners.Count} gunner seats");
                phaseStart = now + 4;      // (graphs start)
                return;
            }
            if (now < phaseStart) return;
            var watch = GameObject.Find("FleetWatch");
            if (index >= gunners.Count)
            {
                log.AppendLine("DONE");
                File.WriteAllText(LogPath, log.ToString());
                SessionState.SetBool(ArmedKey, false);
                EditorApplication.update -= Tick;
                started = -1;
                EditorApplication.isPlaying = false;
                return;
            }
            var m = gunners[index];
            var g = Graph(m);
            var o = Obj(m);
            string label = m.transform.parent.name + " / " + m.name.Substring(7);
            float mode = (float)o.Get("AimMode");
            var yawPart = (Transform)o.Get("YawPart");
            var hand = (Transform)o.Get("AimHand");
            if (phase == 0)
            {
                // be its gunner, aim
                g.Set("Gunning", true);
                g.Set("LastFrame", Time.time);
                Obj(watch.transform).Set("Gunner", m.gameObject);
                Obj(watch.transform).Set("HasGunner", true);
                restYawBefore = (float)g.Get("Yaw");
                if (mode > 0.5f && mode < 2.5f)
                {
                    var parent = (Transform)o.Get("YawParent");
                    float rest = (float)o.Get("RestYaw");
                    var dir = parent.TransformDirection(Quaternion.Euler(-10f, rest + 30f, 0f) * Vector3.forward);
                    handRotationBefore = hand.rotation;
                    if (mode < 1.5f) hand.rotation = Quaternion.LookRotation(dir);
                    else hand.position = Camera.main != null ? Camera.main.transform.position + dir : yawPart.position + dir;
                }
                if (mode > 2.5f) g.Set("StickRX", 1f);
                phase = 1;
                phaseStart = now + 1.5;
                return;
            }
            if (phase == 1)
            {
                float yaw = (float)g.Get("Yaw"), pitch = (float)g.Get("Pitch");
                if (mode > 0.5f && mode < 1.5f)
                {
                    float limitRight = (float)o.Get("LimitRight"), limitUp = (float)o.Get("LimitUp");
                    Check($"{label} turret follows the hand", Mathf.Abs(yaw - Mathf.Min(30f, limitRight)) < 3f && Mathf.Abs(pitch - Mathf.Min(10f, limitUp)) < 3f,
                        $"yaw {yaw:F1} pitch {pitch:F1}, part at {yawPart.localEulerAngles}");
                }
                else if (mode > 1.5f && mode < 2.5f) Check($"{label} turret follows the hand (from the head)", Mathf.Abs(yaw - restYawBefore) > 1f || Mathf.Abs(pitch) > 1f, $"yaw {yaw:F1} pitch {pitch:F1}");
                else if (mode > 2.5f) Check($"{label} turret turns with the stick", Mathf.Abs(yaw - restYawBefore) > 20f, $"yaw {yaw:F1} after 1.5 s");
                else log.AppendLine($"info {label}: no turret");
                g.Set("StickRX", 0f);
                if (mode > 0.5f && mode < 2.5f) hand.rotation = handRotationBefore;
                // fire
                bombsBefore = (float)g.Get("BombCount");
                bomb2Before = (float)g.Get("Bomb2Count");
                var world = GameObject.Find("WeaponsWorld");
                liveBefore = (float)Obj(world.transform).Get("LiveCount");
                g.Set("TrigL", 1f);
                g.Set("TrigR", 1f);
                phase = 2;
                phaseStart = now + 1.2;
                return;
            }
            if (phase == 2)
            {
                var vehicle = (GameObject)o.Get("Vehicle");
                var anim = (Animator)o.Get("VehicleAnimator");
                if ((bool)o.Get("HasGun"))
                {
                    string boolName = (string)o.Get("GunBool");
                    bool animOn = anim.parameters.Any(p => p.name == boolName) && anim.GetBool(boolName);
                    bool sound = vehicle.GetComponentsInChildren<AudioSource>(false).Any(a => a.isPlaying && a.loop && a.clip != null &&
                                     (a.clip.name.ToLowerInvariant().Contains("gun") || a.name.ToLowerInvariant().Contains("gun")));
                    Check($"{label} gun fires", (bool)g.Get("GunFiring") && animOn, $"bool {boolName} {animOn}, ammo left {(float)g.Get("GunAmmo"):F1} s");
                    Check($"{label} gun is heard", sound);
                }
                var world = GameObject.Find("WeaponsWorld");
                float live = (float)Obj(world.transform).Get("LiveCount");
                if ((float)o.Get("BombPoolSize") > 0.5f) Check($"{label} launcher 1 fires", (float)g.Get("BombCount") < bombsBefore, $"{bombsBefore} -> {(float)g.Get("BombCount")}, rounds out {liveBefore} -> {live}");
                if ((float)o.Get("Bomb2PoolSize") > 0.5f) Check($"{label} launcher 2 fires", (float)g.Get("Bomb2Count") < bomb2Before, $"{bomb2Before} -> {(float)g.Get("Bomb2Count")}");
                g.Set("TrigL", 0f);
                g.Set("TrigR", 0f);
                phase = 3;
                phaseStart = now + 0.5;
                return;
            }
            // leave the seat
            g.Set("Gunning", false);
            Obj(watch.transform).Set("HasGunner", false);
            var a2 = (Animator)o.Get("VehicleAnimator");
            if ((bool)o.Get("HasGun")) a2.SetBool((string)o.Get("GunBool"), false);
            index++;
            phase = 0;
            phaseStart = now + 0.3;
        }
    }
}
