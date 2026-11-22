using System;
using System.IO;
using System.Linq;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Automated flight check for the generated graphs. Arm for one aircraft, then enter Play mode: a
    /// scripted pilot takes off, climbs, levels, rolls and glides while telemetry is logged to
    /// Logs/flight_test_&lt;aircraft&gt;.txt. It drives the same graph variables the keyboard does, so it
    /// exercises the real physics graph end to end.
    /// </summary>
    [InitializeOnLoad]
    public static class FlightTest
    {
        const string ArmedKey = "FlightWorld.FlightTest.Target";
        const string CrashKey = "FlightWorld.FlightTest.Crash";
        static float deathTime = -1f, reviveTime = -1f, healthBeforeDive = -1f, impactSpeed;
        static Vector3 deathPosition;
        static string wreckState = "";
        static string cruiseSound = "";
        static double nextLog, startTime = -1;
        static Vector3 startPosition, startForward, startRight;
        static float lastPitch, pitchRate;
        static double lastPitchTime = -1;

        /// <summary>
        /// The scripted pilot's elevator: towards the wanted attitude, eased off by how fast the nose is already
        /// moving, so that agile aircraft are not thrown into an oscillation. (Negative = nose up.)
        /// </summary>
        static float PitchHold(float pitchDeg, float targetPitch)
        {
            double now = Time.timeAsDouble;
            if (now > lastPitchTime)        // (this can be called more than once in a frame)
            {
                pitchRate = lastPitchTime > 0 ? Mathf.Clamp((pitchDeg - lastPitch) / (float)(now - lastPitchTime), -90f, 90f) : 0f;
                lastPitch = pitchDeg;
                lastPitchTime = now;
            }
            return Mathf.Clamp((pitchDeg - targetPitch) * 0.05f + pitchRate * 0.03f, -1f, 1f);
        }

        static bool finished, diagnosed;
        static int shots;

        /// <summary>A picture from the pilot's eye point, or from behind and above the vehicle, into Library/FlightWorld/shots.</summary>
        static void Shot(GameObject aircraft, string name, bool fromCockpit)
        {
            const int width = 1600, height = 900;
            var eye = Variables.Object(aircraft).Get("EyePoint") as Transform;
            var go = new GameObject("__TestShotCamera") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                var cam = go.AddComponent<Camera>();
                var t = aircraft.transform;
                if (fromCockpit && eye != null) cam.transform.SetPositionAndRotation(eye.position, t.rotation);
                else
                {
                    cam.transform.position = t.position - t.forward * 24f + Vector3.up * 7f + t.right * 9f;
                    cam.transform.LookAt(t.position + t.forward * 3f);
                }
                cam.fieldOfView = fromCockpit ? 95f : 50f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 30000f;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                Directory.CreateDirectory("Library/FlightWorld/shots");
                File.WriteAllBytes($"Library/FlightWorld/shots/{name}.jpg", tex.EncodeToJPG(88));
            }
            finally
            {
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
        static float liftoffTime = -1f, liftoffSpeed, maxAltitude, cruiseSpeed, peakRollRate;

        // rotation speed (m/s), climb pitch (deg), cruise throttle
        // (vehicles whose thrust points up lift off straight away and fly nose-down)
        static (float rotate, float climbPitch, float cruise) Profile(string target, GameObject vehicle) =>
            Variables.Object(vehicle).Get("VTOLOnly") is bool thrustUp && thrustUp ? (0f, -8f, 0.8f)
            : target.StartsWith("SF-") || target.StartsWith("SC-") || target.StartsWith("SX-") ? (80f, 12f, 0.55f) : (40f, 9f, 0.7f);

        /// <summary>The vehicle to test is named in this file (Tools/remote.sh writes it), so any vehicle can be tested.</summary>
        const string TargetFile = "Library/FlightWorld/test_target.txt";
        static string Target => File.Exists(TargetFile) ? File.ReadAllText(TargetFile).Trim() : "SF-1 1";

        static FlightTest()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(ArmedKey, ""))) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Arm Scripted Flight")]
        public static void ArmFlight() => Arm(Target);

        [MenuItem("FlightWorld/Flight/Test/Arm Crash Test")]
        public static void ArmCrash() => Arm(Target, true);

        /// <summary>Where every seat and reset sign is, for driving a headset test from outside the app.</summary>
        [MenuItem("FlightWorld/Flight/Test/Report Seat Positions")]
        public static void ReportSeats()
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var text = new System.Text.StringBuilder();
            foreach (var fleet in new[] { GameObject.Find("Aircraft"), GameObject.Find("GroundVehicles") }.Where(found => found != null))
            foreach (Transform aircraft in fleet.transform)
            {
                foreach (var t in aircraft.GetComponentsInChildren<Transform>(true))
                {
                    bool seat = t.GetComponent(GraphTypes.Find("BS.BSSeat")) != null;
                    if (!seat && t.name != "Board" && t != aircraft) continue;
                    text.AppendLine(string.Format(culture, "{0} | {1} | {2:F2},{3:F2},{4:F2} | yaw {5:F1}",
                        aircraft.name, t == aircraft ? "(root)" : t.name, t.position.x, t.position.y, t.position.z, t.eulerAngles.y));
                }
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/seat_positions.txt", text.ToString());

            // where each seat lets its rider out (world space), for checking exits against what the headset reports
            var exits = new System.Text.StringBuilder();
            foreach (var fleet in new[] { GameObject.Find("Aircraft"), GameObject.Find("GroundVehicles") }.Where(found => found != null))
            foreach (Transform vehicle in fleet.transform)
                foreach (var seat in vehicle.GetComponentsInChildren(GraphTypes.Find("BS.BSSeat"), true))
                {
                    var so = new SerializedObject(seat);
                    var local = so.FindProperty("exitPosition").vector3Value;
                    var world = seat.transform.TransformPoint(local);
                    exits.AppendLine(string.Format(culture, "{0} | {1} | exit {2:F2},{3:F2},{4:F2} | local {5:F2},{6:F2},{7:F2} | scale {8:F3},{9:F3},{10:F3} | used {11}",
                        vehicle.name, seat.name, world.x, world.y, world.z, local.x, local.y, local.z,
                        seat.transform.lossyScale.x, seat.transform.lossyScale.y, seat.transform.lossyScale.z, so.FindProperty("useExitPosition").boolValue));
                }
            File.WriteAllText("Logs/seat_exits.txt", exits.ToString());
        }

        static bool Crash => SessionState.GetBool(CrashKey, false);
        const string HandsOffKey = "FlightWorld.FlightTest.HandsOff";
        const string RiderFile = "Library/FlightWorld/test_rider.txt";
        static double lastHandsOffLog = -1;

        /// <summary>
        /// Hands-off check: full throttle and no stick at all, logging attitude ten times a second for 9 s.
        /// A number in Library/FlightWorld/test_rider.txt adds that many kilograms hanging at the pilot's seat,
        /// which is what the app does with a seated player's body (the scripted pilot has none).
        /// </summary>
        [MenuItem("FlightWorld/Flight/Test/Arm Hands-Off Test")]
        public static void ArmHandsOff()
        {
            Arm(Target);
            SessionState.SetBool(HandsOffKey, true);
        }

        static void HandsOffTick(string target, GameObject aircraft, Rigidbody body, VariableDeclarations vars,
            float t, float altitude, float speed, float pitchDeg, float rollDeg)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            string log = LogPath(target);
            float rider = File.Exists(RiderFile) && float.TryParse(File.ReadAllText(RiderFile).Trim(), System.Globalization.NumberStyles.Float, culture, out var kg) ? kg : 0f;
            vars.Set("KeyPitch", 0f);
            vars.Set("KeyRoll", 0f);
            vars.Set("Throttle", t < 0.5f ? 0f : 1f);
            vars.Set("TestRider", rider > 0f);      // the graph then gives the rider's weight back, as it does for a real pilot
            if (rider > 0f && Variables.Object(aircraft).Get("PilotSeat") is GameObject seat && Time.deltaTime > 0f)
                body.AddForceAtPosition(Vector3.down * rider * 9.81f * Time.deltaTime, seat.transform.position + aircraft.transform.up * 0.3f, ForceMode.Impulse);
            if (Time.timeAsDouble - lastHandsOffLog >= 0.1)
            {
                lastHandsOffLog = Time.timeAsDouble;
                var flat = Vector3.ProjectOnPlane(body.position - startPosition, Vector3.up);
                File.AppendAllText(log, string.Format(culture, "H {0:F1} s: up {1:F1} m, pitch {2:F1}, roll {3:F1}, along {4:F1} m, sideways {5:F1} m, speed {6:F1}\n",
                    t, altitude, pitchDeg, rollDeg, Vector3.Dot(flat, startForward), Vector3.Dot(flat, startRight), speed));
            }
            if (t > 9.5f || (bool)vars.Get("Dead"))
            {
                finished = true;
                File.AppendAllText(log, string.Format(culture, "SUMMARY hands-off with a {0:F0} kg rider: after {1:F1} s it is {2:F1} m up, pitch {3:F1}, roll {4:F1}{5}\nDONE\n",
                    rider, t, altitude, pitchDeg, rollDeg, (bool)vars.Get("Dead") ? " (destroyed)" : ""));
                SessionState.SetString(ArmedKey, "");
                SessionState.SetBool(HandsOffKey, false);
                EditorApplication.isPlaying = false;
            }
        }
        static string LogPath(string target) => $"Logs/{(Crash ? "crash" : "flight")}_test_{target.Replace(' ', '_')}.txt";

        static void Arm(string target, bool crash = false)
        {
            SessionState.SetBool(CrashKey, crash);
            SessionState.SetBool(HandsOffKey, false);
            // Play mode must keep running while the editor is not the focused window.
            if (!PlayerSettings.runInBackground) PlayerSettings.runInBackground = true;
            SessionState.SetString(ArmedKey, target);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath(target), "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!Application.isPlaying || finished) return;
            string target = SessionState.GetString(ArmedKey, "");
            var aircraft = GameObject.Find("Aircraft")?.transform.Find(target)?.gameObject;
            if (aircraft == null) return;
            var machine = aircraft.GetComponent<ScriptMachine>();
            var body = aircraft.GetComponent<Rigidbody>();
            if (machine == null || body == null) return;
            var vars = Variables.Graph(GraphReference.New(machine, true));
            var (rotateSpeed, climbPitch, cruiseThrottle) = Profile(target, aircraft);
            string log = LogPath(target);

            if (startTime < 0)
            {
                startTime = Time.timeAsDouble;
                startPosition = body.position;
                startForward = Vector3.ProjectOnPlane(aircraft.transform.forward, Vector3.up).normalized;
                startRight = Vector3.Cross(Vector3.up, startForward);
                vars.Set("TestPilot", true);
                vars.Set("Piloting", true);
                // What sitting in the pilot's seat switches on: the cockpit-only objects (display, stick, lever).
                if (Variables.Object(aircraft).Get("InVehicleObjects") is System.Collections.IEnumerable cockpit)
                    foreach (var item in cockpit) (item as GameObject)?.SetActive(true);
                shots = 0;
                File.AppendAllText(log, "t,alt,speed,climb,pitchDeg,rollDeg,aoa,engine,inPitch,phase\n");
            }
            float t = (float)(Time.timeAsDouble - startTime);
            float altitude = body.position.y - startPosition.y;
            float speed = body.linearVelocity.magnitude;
            var forward = aircraft.transform.forward;
            float pitchDeg = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float rollDeg = Mathf.DeltaAngle(0f, aircraft.transform.eulerAngles.z);
            if (Crash) { CrashTick(target, aircraft, body, vars, t, altitude, speed, pitchDeg, rollDeg, rotateSpeed, climbPitch); return; }
            if (SessionState.GetBool(HandsOffKey, false)) { HandsOffTick(target, aircraft, body, vars, t, altitude, speed, pitchDeg, rollDeg); return; }

            // ---- the scripted pilot: holds a pitch attitude and wings level, like a simple autopilot ----
            // (pitch input: negative = nose up; roll input: positive = roll left)
            string phase;
            float targetPitch, throttle = 1f, rollCommand = float.NaN;
            if (altitude < 2f && speed < rotateSpeed) { phase = "takeoff roll"; targetPitch = 0f; }
            else if (altitude < 400f && t < 40f) { phase = "climb"; targetPitch = climbPitch; }
            else if (t < 46f) { phase = "level, cruise power"; targetPitch = 2f; throttle = cruiseThrottle; }
            else if (t < 47.2f) { phase = "roll left"; targetPitch = 2f; throttle = cruiseThrottle; rollCommand = 1f; }
            else if (t < 56f) { phase = "recover level"; targetPitch = 2f; throttle = cruiseThrottle; }
            else { phase = "idle glide"; targetPitch = -3f; throttle = 0f; }
            float keyPitch = phase == "takeoff roll" ? 0f : PitchHold(pitchDeg, targetPitch);
            float keyRoll = float.IsNaN(rollCommand) ? Mathf.Clamp(-rollDeg * 0.03f, -1f, 1f) : rollCommand;
            vars.Set("KeyPitch", keyPitch);
            vars.Set("KeyRoll", keyRoll);
            vars.Set("Throttle", throttle);

            // ---- stuck on the ground? say what is holding it ----
            if (!diagnosed && t > 6f && speed < 0.5f)
            {
                diagnosed = true;
                var text = new System.Text.StringBuilder();
                text.AppendLine($"STUCK: kinematic {body.isKinematic}, constraints {body.constraints}, sleeping {body.IsSleeping()}, " +
                                $"collisions {body.detectCollisions}, mass {body.mass}, centre of mass {body.centerOfMass}, inertia {body.inertiaTensor}, " +
                                $"position {body.position}, velocity {body.linearVelocity}, max speed {body.maxLinearVelocity}");
                foreach (var wheel in aircraft.GetComponentsInChildren<WheelCollider>(true))
                {
                    bool grounded = wheel.GetGroundHit(out var hit);
                    text.AppendLine($"  wheel {wheel.name}: active {wheel.gameObject.activeInHierarchy} enabled {wheel.enabled} grounded {grounded}" +
                                    (grounded ? $" on '{hit.collider.name}' force {hit.force:F0} slip {hit.forwardSlip:F2}/{hit.sidewaysSlip:F2}" : "") +
                                    $" brake {wheel.brakeTorque:F0} motor {wheel.motorTorque:F3} rpm {wheel.rpm:F1} mass {wheel.mass} spring {wheel.suspensionSpring.spring:F0}/{wheel.suspensionSpring.damper:F0} y {wheel.transform.position.y:F2}");
                }
                foreach (var collider in aircraft.GetComponentsInChildren<Collider>(true).Where(c => !(c is WheelCollider)))
                    text.AppendLine($"  collider {collider.name} ({collider.GetType().Name}): enabled {collider.enabled && collider.gameObject.activeInHierarchy} trigger {collider.isTrigger} layer {collider.gameObject.layer} lowest {collider.bounds.min.y:F2}");
                foreach (var name in new[] { "Piloting", "TestPilot", "Dead", "Throttle", "EngineOutput", "Speed", "Health" })
                    text.Append($"  {name}={vars.Get(name)}");
                text.AppendLine();
                var obj = Variables.Object(aircraft);
                foreach (var name in new[] { "ThrottleStrength", "ThrottleAfterburnerPoint", "AfterburnerThrustMulti", "AirFriction", "ExtraDrag", "AtmosphereThinningStart", "AtmosphereThinningEnd", "SeaLevel", "BrakeTorque", "RotMultiMaxSpeed" })
                    text.Append($"  {name}={(obj.IsDefined(name) ? obj.Get(name) : "MISSING")}");
                text.AppendLine();
                File.AppendAllText(log, text.ToString());
            }

            // ---- pictures: the pilot's view on the runway and in the climb, and the aircraft from outside ----
            string shotName = target.Replace(' ', '_');
            if (shots == 0 && t > 1.2f) { shots = 1; Shot(aircraft, $"cockpit_ground_{shotName}", true); }
            if (shots == 1 && t > 9f) { shots = 2; Shot(aircraft, $"cockpit_climb_{shotName}", true); Shot(aircraft, $"chase_climb_{shotName}", false); }
            if (shots == 2 && t > 46.8f) { shots = 3; Shot(aircraft, $"cockpit_roll_{shotName}", true); Shot(aircraft, $"chase_roll_{shotName}", false); }

            // ---- measurements ----
            if (liftoffTime < 0f && altitude > 1.5f) { liftoffTime = t; liftoffSpeed = speed; }
            maxAltitude = Mathf.Max(maxAltitude, altitude);
            if (phase == "level, cruise power" && t > 44f)
            {
                cruiseSpeed = speed;
                var engineSound = Variables.Object(aircraft).Get("EngineSound") as AudioSource;
                var windSound = Variables.Object(aircraft).Get("WindSound") as AudioSource;
                if (engineSound != null && windSound != null)
                    cruiseSound = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "; engine sound at cruise: volume {0:F2}, pitch {1:F2}, playing {2}; wind volume {3:F2}",
                        engineSound.volume, engineSound.pitch, engineSound.isPlaying, windSound.volume);
            }
            if (phase == "roll left") peakRollRate = Mathf.Max(peakRollRate, Mathf.Abs(aircraft.transform.InverseTransformDirection(body.angularVelocity).z) * Mathf.Rad2Deg);

            if (Time.timeAsDouble >= nextLog)
            {
                nextLog = Time.timeAsDouble + 2.0;
                File.AppendAllText(log, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F1},{1:F1},{2:F1},{3:F1},{4:F1},{5:F1},{6:F1},{7:F2},{8:F2},{9}\n",
                    t, altitude, speed, body.linearVelocity.y, pitchDeg, rollDeg,
                    Convert.ToSingle(vars.Get("AoA")), Convert.ToSingle(vars.Get("EngineOutput")),
                    Convert.ToSingle(vars.Get("InPitch")), phase));
            }

            if (t > 66f || float.IsNaN(speed))
            {
                finished = true;
                File.AppendAllText(log, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "SUMMARY liftoff at {0:F1} s and {1:F0} m/s; max altitude {2:F0} m; cruise {3:F0} m/s; peak roll rate {4:F0} deg/s; final speed {5:F0} m/s{6}\nDONE\n",
                    liftoffTime, liftoffSpeed, maxAltitude, cruiseSpeed, peakRollRate, speed, (float.IsNaN(speed) ? " (NaN!)" : "") + cruiseSound));
                SessionState.SetString(ArmedKey, "");
                EditorApplication.isPlaying = false;
            }
        }

        /// <summary>
        /// Crash check: take off, climb, then fly into the ground. The aircraft must explode on impact, stay
        /// put while it is a wreck, and come back whole on its parking spot after the respawn delay.
        /// </summary>
        static void CrashTick(string target, GameObject aircraft, Rigidbody body, VariableDeclarations vars,
            float t, float altitude, float speed, float pitchDeg, float rollDeg, float rotateSpeed, float climbPitch)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            string log = LogPath(target);
            bool dead = (bool)vars.Get("Dead");
            float health = Convert.ToSingle(vars.Get("Health"));
            var model = (GameObject)Variables.Object(aircraft).Get("Model");
            bool hasGear = Variables.Object(aircraft).IsDefined("HasGear") && (bool)Variables.Object(aircraft).Get("HasGear");
            bool gearDown() => !(bool)vars.Get("GearUp") && Convert.ToSingle(vars.Get("GearPos")) > 0.999f;
            string State() => string.Format(culture,
                "model {0}, constraints {1}, collisions {2}, piloting {3}, health {4:F0}, {5:F1} m from parking spot, speed {6:F1}, gear {7}",
                model.activeSelf ? "shown" : "hidden", body.constraints, body.detectCollisions ? "on" : "off",
                vars.Get("Piloting"), health, Vector3.Distance(body.position, startPosition), body.linearVelocity.magnitude,
                gearDown() ? "down" : "UP");

            string phase;
            if (deathTime < 0f)
            {
                float targetPitch;
                if (altitude < 2f && speed < rotateSpeed && healthBeforeDive < 0f) { phase = "takeoff roll"; targetPitch = 0f; }
                else if (altitude < 150f && t < 35f && healthBeforeDive < 0f)
                {
                    phase = "climb";
                    targetPitch = climbPitch;
                    // gear up, as a pilot would: the aircraft must come back with it down
                    if (hasGear && altitude > 20f && !(bool)vars.Get("GearUp")) vars.Set("GearUp", true);
                }
                else { phase = "dive"; targetPitch = -40f; if (healthBeforeDive < 0f) healthBeforeDive = health; }
                vars.Set("KeyPitch", phase == "takeoff roll" ? 0f : PitchHold(pitchDeg, targetPitch));
                vars.Set("KeyRoll", Mathf.Clamp(-rollDeg * 0.03f, -1f, 1f));
                vars.Set("Throttle", 1f);
                if (!dead) impactSpeed = speed;
                else
                {
                    deathTime = t;
                    deathPosition = body.position;
                    wreckState = "";
                    File.AppendAllText(log, string.Format(culture,
                        "{0:F1} exploded after hitting the ground at about {1:F0} m/s\nAT {2:F1},{3:F1},{4:F1}\n",
                        t, impactSpeed, deathPosition.x, deathPosition.y, deathPosition.z));
                }
            }
            else if (reviveTime < 0f)
            {
                phase = "wreck";
                if (wreckState.Length == 0 && t > deathTime + 1f)
                {
                    wreckState = State();
                    File.AppendAllText(log, string.Format(culture, "{0:F1} wreck: {1}\n", t, wreckState));
                }
                float drift = Vector3.Distance(body.position, deathPosition);
                if (dead && drift > 0.5f) File.AppendAllText(log, string.Format(culture, "{0:F1} WRECK MOVED {1:F1} m\n", t, drift));
                if (!dead)
                {
                    reviveTime = t;
                    File.AppendAllText(log, string.Format(culture,
                        "{0:F1} came back {1:F1} s after the explosion: {2}\n", t, t - deathTime, State()));
                }
            }
            else phase = "parked again";

            if (Time.timeAsDouble >= nextLog)
            {
                nextLog = Time.timeAsDouble + 2.0;
                File.AppendAllText(log, string.Format(culture,
                    "{0:F1},{1:F1},{2:F1},{3:F1},{4:F1},{5:F1},{6:F1},{7:F2},{8:F0},{9}\n",
                    t, altitude, speed, body.linearVelocity.y, pitchDeg, rollDeg,
                    Convert.ToSingle(vars.Get("AoA")), Convert.ToSingle(vars.Get("EngineOutput")), health, phase));
            }

            bool settled = reviveTime > 0f && t > reviveTime + 4f;
            if (settled || t > 110f)
            {
                finished = true;
                float full = Convert.ToSingle(Variables.Object(aircraft).Get("FullHealth"));
                bool ok = settled && model.activeSelf && body.constraints == RigidbodyConstraints.None && body.detectCollisions
                          && health > full - 1f && Vector3.Distance(body.position, startPosition) < 1.5f && body.linearVelocity.magnitude < 0.5f
                          && wreckState.StartsWith("model hidden, constraints FreezeAll, collisions off, piloting False")
                          && healthBeforeDive > full - 1f && gearDown();
                File.AppendAllText(log, string.Format(culture,
                    "SUMMARY {0}: health before the dive {1:F0}; exploded at {2:F1} s; back after {3:F1} s; now {4}\nDONE\n",
                    ok ? "PASS" : "FAIL", healthBeforeDive, deathTime, reviveTime - deathTime, State()));
                SessionState.SetString(ArmedKey, "");
                EditorApplication.isPlaying = false;
            }
        }
    }
}
