using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Automated drive check for the generated ground-vehicle graphs. Name the vehicle in
    /// Library/FlightWorld/test_target.txt, arm, then enter Play mode: a scripted driver accelerates, steers
    /// both ways, brakes to a stop, leaves the vehicle parked and empty, then reverses, while telemetry is
    /// logged to Logs/drive_test_&lt;vehicle&gt;.txt. It feeds the same graph variables the keyboard does, so
    /// it exercises the real engine, gearbox and wheel graphs end to end.
    /// </summary>
    [InitializeOnLoad]
    public static class DriveTest
    {
        const string ArmedKey = "FlightWorld.DriveTest.Target";
        const string TargetFile = "Library/FlightWorld/test_target.txt";
        static string Target => File.Exists(TargetFile) ? File.ReadAllText(TargetFile).Trim() : "Baron";
        static string LogPath(string target) => $"Logs/drive_test_{target.Replace(' ', '_')}.txt";

        static double startTime = -1, nextLog;
        static bool finished, probed, skidSeen, skidSoundOn, skidSmokeOn;
        static string restState = "not checked", trackState = "";
        static float settleHeading = float.NaN, headingCreep, positionCreep, awakePush = -1f, restedPush = -1f, settledBy;
        static Vector3 settleAt, pushFrom;
        static bool pushingAwake, pushingRested;
        // an outside reset while it rests (the app sends an idle vehicle home by moving its body)
        static Vector3 resetTarget;
        static bool resetDone;
        static string restInterpolation = "?", drivingInterpolation = "?";
        static float resetMissSoon = -1f, resetMissLater = -1f;
        static string resetState = "not tried";
        static double nextSkidCheck;
        static int shots;
        static Vector3 startPosition, brakeStart, parkedAt, parkingSpot;
        static float restHeight, maxSpeed, zeroTo100 = -1f, topGear, yawRight, yawLeft, brakeFrom = -1f, brakeBegan = -1f, stopTime = -1f, stopDistance = -1f;
        static float parkedSince = -1f, parkedDrift, parkedSink, reverseSince = -1f, reverseSpeed, minUp = 1f, maxRevs, lowestHeight = float.MaxValue, highestHeight = float.MinValue;
        static string phase = "settle", stage = "settle";
        static float stageStart;
        static float crashSince = -1f, deathTime = -1f, reviveTime = -1f;
        static bool crashDone;
        static string wreckState = "", finalState = "";

        static DriveTest()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(ArmedKey, ""))) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Arm Scripted Drive")]
        public static void Arm()
        {
            string target = Target;
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
            if (target.Length == 0) return;
            var vehicle = GameObject.Find(SaccGroundBuilder.RootName)?.transform.Find(target)?.gameObject;
            if (vehicle == null) return;
            var machine = vehicle.GetComponent<ScriptMachine>();
            var body = vehicle.GetComponent<Rigidbody>();
            if (machine == null || body == null) return;
            var vars = Variables.Graph(GraphReference.New(machine, true));
            var shared = Variables.Object(vehicle);
            var culture = CultureInfo.InvariantCulture;
            string log = LogPath(target);

            if (startTime < 0)
            {
                startTime = Time.timeAsDouble;
                startPosition = body.position;
                parkingSpot = body.position;
                vars.Set("TestPilot", true);
                vars.Set("Driving", true);
                // What sitting in the driver's seat switches on: the cockpit-only objects (gauges).
                if (shared.Get("InVehicleObjects") is System.Collections.IEnumerable cockpit)
                    foreach (var item in cockpit) (item as GameObject)?.SetActive(true);
                string owns = "?";
                try
                {
                    var sync = vehicle.GetComponent(GraphTypes.Find("BS.BSSyncedObject"));
                    owns = Convert.ToString(sync.GetType().GetMethod("_DoIOwn")?.Invoke(sync, null));
                }
                catch (Exception e) { owns = "error " + e.GetType().Name; }
                File.AppendAllText(log, $"vehicle at {body.position}, mass {body.mass}, synced object says this client owns it: {owns}\n" +
                                        "t,kmh,forward m/s,revs,gear,clutch,yawRate deg/s,height,up.y,wheels on the ground,phase\n");
            }
            float t = (float)(Time.timeAsDouble - startTime);
            var velocity = body.linearVelocity;
            float speed = velocity.magnitude;
            float forward = Vector3.Dot(velocity, vehicle.transform.forward);
            float yawRate = vehicle.transform.InverseTransformDirection(body.angularVelocity).y * Mathf.Rad2Deg;
            float height = body.position.y;
            float revs = Convert.ToSingle(vars.Get("Revs"));
            float gear = Convert.ToSingle(vars.Get("Gear")) - Convert.ToSingle(shared.Get("NeutralGear"));
            if (crashSince < 0f) minUp = Mathf.Min(minUp, vehicle.transform.up.y);
            maxRevs = Mathf.Max(maxRevs, revs);

            // ---- the scripted driver: one stage after another ----
            float drive = 0f, steer = 0f;
            float inStage = t - stageStart;
            void Next(string next) { stage = next; stageStart = t; }
            switch (stage)
            {
                case "settle":          // sitting still with a driver in: it must not creep or turn
                    if (inStage > 1.5f && float.IsNaN(settleHeading)) { settleHeading = vehicle.transform.eulerAngles.y; settleAt = body.position; }
                    if (inStage > 5f)
                    {
                        headingCreep = Mathf.Abs(Mathf.DeltaAngle(settleHeading, vehicle.transform.eulerAngles.y)) / 3.5f;
                        positionCreep = Vector3.Distance(settleAt, body.position) / 3.5f;
                        restHeight = height;
                        settledBy = height - parkingSpot.y;
                        startPosition = body.position;
                        Next("accelerate");
                    }
                    break;
                case "accelerate":          // flat out from rest for twelve seconds
                    drive = 1f;
                    maxSpeed = Mathf.Max(maxSpeed, speed);
                    topGear = Mathf.Max(topGear, gear);
                    if (zeroTo100 < 0f && speed > 27.78f) zeroTo100 = inStage;
                    if (inStage > 12f) Next("slow down");
                    break;
                case "slow down":           // steering is tried at town speed, not flat out
                    drive = speed > 16f ? -1f : 0f;
                    if (speed < 16f || inStage > 15f) Next("steer right");
                    break;
                case "steer right":
                    drive = 0.35f;
                    steer = 1f;
                    if (inStage > 0.5f && Mathf.Abs(yawRate) > Mathf.Abs(yawRight)) yawRight = yawRate;
                    if (inStage > 2.5f) Next("steer left");
                    break;
                case "steer left":
                    drive = 0.35f;
                    steer = -1f;
                    if (inStage > 1f && Mathf.Abs(yawRate) > Mathf.Abs(yawLeft)) yawLeft = yawRate;
                    if (inStage > 3f) Next("straighten");
                    break;
                case "straighten":
                    drive = 1f;
                    if (inStage > 3f) Next("brake");
                    break;
                case "brake":
                    if (brakeFrom < 0f) { brakeFrom = speed; brakeStart = body.position; }
                    drive = -1f;
                    if (speed < 1f) { stopTime = inStage; stopDistance = Vector3.Distance(body.position, brakeStart); Next("parked, empty"); }
                    else if (inStage > 20f) Next("parked, empty");
                    break;
                case "parked, empty":
                    if (parkedSince < 0f)
                    {
                        parkedSince = t;
                        vars.Set("Driving", false);                      // nobody at the wheel: the parking brake must hold it
                    }
                    // A sideways shove of 3 m/s^2 for a second while it is parked but still awake (on its
                    // handbrake), and again for a second and a half once it rests: it must stay where it is.
                    bool shoveAwake = inStage > 0.3f && inStage < 1.3f, shoveRested = inStage > 3.8f && inStage < 5.3f;
                    if (shoveAwake || shoveRested) body.AddForce(vehicle.transform.right * body.mass * 3f, ForceMode.Force);
                    if (shoveAwake && !pushingAwake) { pushingAwake = true; pushFrom = body.position; }
                    if (!shoveAwake && pushingAwake && awakePush < 0f) awakePush = Vector3.Distance(body.position, pushFrom);
                    if (shoveRested && !pushingRested) { pushingRested = true; pushFrom = body.position; }
                    if (!shoveRested && pushingRested && restedPush < 0f) restedPush = Vector3.Distance(body.position, pushFrom);
                    if (inStage > 1.5f && !resetDone)
                    {
                        if (parkedAt == Vector3.zero) parkedAt = body.position;
                        parkedDrift = Mathf.Max(parkedDrift, Vector3.Distance(body.position, parkedAt));
                        parkedSink = Mathf.Max(parkedSink, parkedAt.y - height);
                    }
                    if (inStage > 5.5f && restState == "not checked")
                    {
                        // by now it should be resting: nothing running, held where it stands
                        var wheelMachines = vehicle.GetComponentsInChildren<ScriptMachine>(true).Where(m => m.gameObject != vehicle).ToArray();
                        var animator = vehicle.GetComponent<Animator>();
                        restState = string.Format(culture, "awake {0}, body {1}, {2} of {3} wheel graphs running, animator {4}, Simulate {5}",
                            vars.Get("Awake"), body.constraints, wheelMachines.Count(m => m.enabled && m.gameObject.activeInHierarchy), wheelMachines.Length,
                            animator != null && animator.enabled ? "on" : "off", shared.Get("Simulate"));
                    }
                    if (inStage > 6f && !resetDone)
                    {
                        // What the app's idle reset does to a vehicle left standing: move the body somewhere
                        // else from outside (transform and rigidbody pose, velocities zeroed). It must stay there.
                        resetDone = true;
                        resetTarget = body.position + vehicle.transform.right * 12f + vehicle.transform.forward * 6f;
                        var turned = body.rotation * Quaternion.Euler(0f, 25f, 0f);
                        // (Only the transform is set: the hardest case. A body held with frozen constraints and
                        // interpolation ignores that, which is why a resting vehicle's interpolation is switched off.)
                        vehicle.transform.SetPositionAndRotation(resetTarget, turned);
                    }
                    if (resetDone && resetMissSoon < 0f && inStage > 6.6f) resetMissSoon = Vector3.Distance(vehicle.transform.position, resetTarget);
                    if (resetDone && resetMissLater < 0f && inStage > 10.5f)
                    {
                        resetMissLater = Vector3.Distance(vehicle.transform.position, resetTarget);
                        restInterpolation = body.interpolation.ToString();
                        resetState = string.Format(culture, "awake {0}, body {1}", vars.Get("Awake"), body.constraints);
                    }
                    if (inStage > 11f)
                    {
                        vars.Set("Driving", true);
                        Next("reverse");
                    }
                    break;
                case "reverse":
                    drive = -1f;
                    reverseSpeed = Mathf.Min(reverseSpeed, forward);
                    if (inStage > 1f) drivingInterpolation = body.interpolation.ToString();
                    if (inStage > 5.5f && trackState.Length == 0 && shared.Get("HasTracks") is bool tracked && tracked)
                    {
                        // a tank: have its treads and cog wheels been running?
                        var treads = shared.Get("TracksRenderer") as Renderer;
                        var own = treads != null ? treads.materials : new Material[0];
                        int slotL = Mathf.RoundToInt(Convert.ToSingle(shared.Get("TrackSlotL"))), slotR = Mathf.RoundToInt(Convert.ToSingle(shared.Get("TrackSlotR")));
                        var cog = (shared.Get("CogsL") as System.Collections.IEnumerable)?.Cast<Transform>().FirstOrDefault();
                        trackState = string.Format(culture, "; tracks have run {0:F0} / {1:F0} degrees of road wheel, tread texture offsets {2} / {3}, a cog wheel is turned {4:F0} degrees, track noise volume {5:F2}",
                            Convert.ToSingle(vars.Get("TrackRotL")), Convert.ToSingle(vars.Get("TrackRotR")),
                            slotL < own.Length ? own[slotL].GetTextureOffset("_MainTex").ToString("F2") : "?", slotR < own.Length ? own[slotR].GetTextureOffset("_MainTex").ToString("F2") : "?",
                            cog != null ? cog.localEulerAngles.x : float.NaN, (shared.Get("TrackSound") as AudioSource)?.volume ?? -1f);
                    }
                    if (inStage > 6f) Next("crash");
                    break;
                case "crash":
                {
                    // nearly out of health and dropped on its roof from a height, it must blow up, hold still
                    // as a wreck, then come back whole on its parking spot
                    var model = shared.Get("Model") as GameObject;
                    string State() => string.Format(culture, "model {0}, constraints {1}, collisions {2}, driving {3}, health {4:F0}, {5:F1} m from its parking spot, speed {6:F1}",
                        model != null && model.activeInHierarchy ? "shown" : "hidden", body.constraints, body.detectCollisions ? "on" : "off",
                        vars.Get("Driving"), Convert.ToSingle(vars.Get("Health")), Vector3.Distance(body.position, parkingSpot), body.linearVelocity.magnitude);
                    if (crashSince < 0f)
                    {
                        crashSince = t;
                        vars.Set("Driving", false);
                        vars.Set("Health", 1f);
                        var raised = body.position + Vector3.up * 25f;
                        var onItsRoof = Quaternion.AngleAxis(180f, vehicle.transform.forward) * body.rotation;
                        vehicle.transform.SetPositionAndRotation(raised, onItsRoof);
                        body.position = raised;
                        body.rotation = onItsRoof;
                        body.linearVelocity = Vector3.zero;
                        body.angularVelocity = Vector3.zero;
                    }
                    bool dead = (bool)vars.Get("Dead");
                    if (deathTime < 0f && dead) deathTime = t;
                    if (deathTime > 0f && wreckState.Length == 0 && t > deathTime + 1f) wreckState = State();
                    if (deathTime > 0f && !dead && reviveTime < 0f) reviveTime = t;
                    if (reviveTime > 0f && t > reviveTime + 3f) { finalState = State(); crashDone = true; Next("done"); }
                    else if (inStage > 50f) { finalState = "TIMED OUT: " + State(); crashDone = true; Next("done"); }
                    break;
                }
            }
            phase = stage == "crash" ? "dropped on its roof from 25 m" : stage;
            vars.Set("KeyDrive", drive);
            vars.Set("KeySteer", steer);
            if (phase != "settle" && crashSince < 0f) { lowestHeight = Mathf.Min(lowestHeight, height); highestHeight = Mathf.Max(highestHeight, height); }

            if (shots == 0 && t > 1.5f) { shots = 1; Shot(vehicle, $"car_parked_{target.Replace(' ', '_')}", false); Shot(vehicle, $"car_cockpit_{target.Replace(' ', '_')}", true); }
            if (shots == 1 && t > 12f) { shots = 2; Shot(vehicle, $"car_driving_{target.Replace(' ', '_')}", false); }

            // ---- do the tyres squeal and smoke when they slide? ----
            if (Time.timeAsDouble >= nextSkidCheck && crashSince < 0f)
            {
                nextSkidCheck = Time.timeAsDouble + 0.15;
                foreach (var wheelVars in vehicle.GetComponentsInChildren<Variables>(false).Where(v => v.gameObject != vehicle && v.declarations.IsDefined("WheelPoint")))
                {
                    var wheelGraph = Variables.Graph(GraphReference.New(wheelVars.GetComponent<ScriptMachine>(), true));
                    if (!(wheelGraph.Get("Skidding") is bool skidding) || !skidding) continue;
                    skidSeen = true;
                    if (wheelVars.declarations.Get("SkidSound") is AudioSource squeal && squeal.gameObject.activeInHierarchy && squeal.isPlaying) skidSoundOn = true;
                    if (wheelVars.declarations.Get("SkidParticle") is ParticleSystem smoke && smoke.gameObject.activeInHierarchy && smoke.emission.enabled) skidSmokeOn = true;
                }
            }

            // ---- once, at rest: what each wheel's suspension ray is standing on (it must never be the vehicle itself) ----
            if (!probed && t > 1.8f)
            {
                probed = true;
                var text = new System.Text.StringBuilder();
                foreach (var wheelVars in vehicle.GetComponentsInChildren<Variables>(true).Where(v => v.gameObject != vehicle && v.declarations.IsDefined("WheelPoint")))
                {
                    var w = wheelVars.declarations;
                    var point = (Transform)w.Get("WheelPoint");
                    float extra = Convert.ToSingle(w.Get("ExtraRayCastDistance")), travel = Convert.ToSingle(w.Get("SuspensionDistance"));
                    bool hit = Physics.Raycast(point.position + point.up * extra, -point.up, out var found, travel + extra,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    var wheelGraph = Variables.Graph(GraphReference.New(wheelVars.GetComponent<ScriptMachine>(), true));
                    text.AppendLine(string.Format(culture, "  wheel {0}: {1}; graph says grounded {2}, compression {3:F2}, tread speed {4:F2} m/s; visual at height {5:F2} above the ray's ground",
                        wheelVars.name,
                        hit ? $"ray hits '{found.collider.name}'{(found.collider.attachedRigidbody == body ? " WHICH IS THE VEHICLE ITSELF" : "")} {found.distance - extra:F2} m below the wheel point"
                            : "ray hits nothing",
                        wheelGraph.Get("Grounded"), Convert.ToSingle(wheelGraph.Get("CompressionLast")), Convert.ToSingle(wheelGraph.Get("Surf")),
                        hit ? ((Transform)w.Get("WheelVisual")).position.y - found.point.y : float.NaN));
                }
                text.AppendLine($"  shared: Simulate {shared.Get("Simulate")}, HandBrakeInput {shared.Get("HandBrakeInput")}, Revs {shared.Get("Revs")}, speed {speed:F2} m/s, height change since start {height - startPosition.y:F2} m");
                File.AppendAllText(log, text.ToString());
            }

            if (Time.timeAsDouble >= nextLog)
            {
                nextLog = Time.timeAsDouble + 1.0;
                File.AppendAllText(log, string.Format(culture, "{0:F1},{1:F1},{2:F1},{3:F0},{4:F0},{5:F2},{6:F1},{7:F2},{8:F2},{9},{10}\n",
                    t, speed * 3.6f, forward, revs, gear, Convert.ToSingle(vars.Get("Clutch")), yawRate, height - startPosition.y,
                    vehicle.transform.up.y,
                    vehicle.GetComponentsInChildren<ScriptMachine>(false).Count(m => m.gameObject != vehicle && m.enabled &&
                        Variables.Graph(GraphReference.New(m, true)).Get("Grounded") is bool onGround && onGround),
                    phase));
            }

            if (phase == "done" || t > 130f || float.IsNaN(speed))
            {
                finished = true;
                float backHome = Vector3.Distance(body.position, parkingSpot);
                bool ok = !float.IsNaN(speed) && maxSpeed > 8f && minUp > 0.5f && stopTime > 0f && parkedDrift < 0.5f && parkedSink < 0.15f
                          && reverseSpeed < -1f && Mathf.Abs(yawRight) > 3f && Mathf.Abs(yawLeft) > 3f && yawRight * yawLeft < 0f
                          && wreckState.StartsWith("model hidden, constraints FreezeAll, collisions off, driving False")
                          // (three seconds after coming back it may already be resting again, held with frozen constraints)
                          && (finalState.StartsWith("model shown, constraints None, collisions on, driving False")
                              || finalState.StartsWith("model shown, constraints FreezeAll, collisions on, driving False"))
                          && backHome < 1.5f
                          && restState.StartsWith("awake False, body FreezeAll, 0 of ") && restState.Contains("animator off")
                          && headingCreep < 0.05f && positionCreep < 0.01f && restedPush >= 0f && restedPush < 0.01f
                          && resetMissSoon >= 0f && resetMissSoon < 0.3f && resetMissLater >= 0f && resetMissLater < 0.3f
                          && resetState.StartsWith("awake False, body FreezeAll")
                          && restInterpolation == "None" && drivingInterpolation == "Interpolate";
                File.AppendAllText(log, string.Format(culture,
                    "SUMMARY {0}: top speed {1:F0} km/h after 12 s flat out (highest gear {2:F0}, peak revs {3:F0}); 0-100 km/h {4}; steering at about 55 km/h: yaw rate right {5:F0} / left {6:F0} deg/s; " +
                    "braked from {7:F0} km/h in {8} over {9:F0} m; parked and empty for 4.5 s it moved {10:F2} m and sank {11:F2} m; reverse reached {12:F1} m/s; " +
                    "ride height varied {13:F2}..{14:F2} m from rest; least upright up.y {15:F2}{16}; crash: exploded {17}; wreck: {18}; back after {19}: {20}; tyres slid enough to squeal: {21}; after 5.5 s parked: {22}\nDONE\n",
                    ok ? "PASS" : "FAIL", maxSpeed * 3.6f, topGear, maxRevs, zeroTo100 > 0f ? zeroTo100.ToString("F1", culture) + " s" : "not reached",
                    yawRight, yawLeft, brakeFrom * 3.6f, stopTime > 0f ? stopTime.ToString("F1", culture) + " s" : "never stopped", stopDistance,
                    parkedDrift, parkedSink, reverseSpeed, lowestHeight - restHeight, highestHeight - restHeight, minUp, float.IsNaN(speed) ? " (NaN!)" : "",
                    deathTime > 0f ? (deathTime - crashSince).ToString("F1", culture) + " s after the drop" : "NEVER",
                    wreckState.Length > 0 ? wreckState : "-", reviveTime > 0f ? (reviveTime - deathTime).ToString("F1", culture) + " s" : "NEVER",
                    finalState.Length > 0 ? finalState : "-",
                    skidSeen ? $"yes (squeal heard playing: {(skidSoundOn ? "yes" : "no")}, smoke emitting: {(skidSmokeOn ? "yes" : "no")})" : "no",
                    restState + trackState + string.Format(culture, "; standing still with a driver in, it crept {0:F3} m/s and turned {1:F3} deg/s; it settled {2:F3} m from its built height; shoved sideways at 3 m/s2 it moved {3:F3} m parked awake and {4:F3} m resting",
                        positionCreep, headingCreep, settledBy, awakePush, restedPush) +
                    string.Format(culture, "; moved from outside while resting (as the app's idle reset does) it was {0:F2} m from where it was put after 0.6 s and {1:F2} m after 4.5 s ({2})",
                        resetMissSoon, resetMissLater, resetState) +
                    string.Format(culture, "; body interpolation resting {0}, driving {1}", restInterpolation, drivingInterpolation)));
                SessionState.SetString(ArmedKey, "");
                EditorApplication.isPlaying = false;
            }
        }

        /// <summary>A picture from the driver's eye point, or from behind and above the vehicle, into Library/FlightWorld/shots.</summary>
        static void Shot(GameObject vehicle, string name, bool fromCockpit)
        {
            const int width = 1600, height = 900;
            var eye = Variables.Object(vehicle).Get("EyePoint") as Transform;
            var go = new GameObject("__TestShotCamera") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                var cam = go.AddComponent<Camera>();
                var t = vehicle.transform;
                if (fromCockpit && eye != null) cam.transform.SetPositionAndRotation(eye.position, t.rotation);
                else
                {
                    cam.transform.position = t.position - t.forward * 7f + Vector3.up * 2.6f + t.right * 3.5f;
                    cam.transform.LookAt(t.position + t.forward * 1f);
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
    }
}
