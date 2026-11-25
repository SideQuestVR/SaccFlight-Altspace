using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// A vehicle kit in a world that is not this one, worked the way a player works it: an empty world (a floor and a
    /// light) whose page holds nothing but the snippet; the pilot's (driver's) seat clicked the way the app clicks it
    /// (BSScene.Click); keys pressed into the page (BSLink.OnKeyPress, as the desktop player does), so they go through
    /// the snippet's own script; and the VR sticks and triggers raised as the app raises them (the event bus).
    /// Nothing is set on the vehicle's graph by hand: every check reads what the controls did.
    ///
    ///   Library/FlightWorld/kit_only.txt = one slug; FlightWorld/Snippets/Test/Arm Controls Test; then Play.
    ///   Library/FlightWorld/kit_live.txt present: the published script and the CDN's bundles (else local ones).
    /// Output: Logs/kit_controls_&lt;slug&gt;.txt (PASS / FAIL lines, then DONE). Page and scene are put back afterwards.
    /// </summary>
    [InitializeOnLoad]
    public static class KitControlsTest
    {
        const string ArmedKey = "FlightWorld.KitControlsTest.Slug";
        const string ReturnSceneKey = "FlightWorld.KitControlsTest.ReturnScene";
        const string IndexPath = "Assets/WebRoot/index.html";
        const string BackupPath = "Library/FlightWorld/index.html.controls_test_backup";
        const string ScenePath = "Assets/Flight/Kits/KitTestWorld.unity";
        const string BundleHost = "http://localhost:42071";
        static readonly Vector3 Place = new Vector3(0f, 0f, 30f);
        const float BoatWater = 3f;

        static double started = -1, stageAt;
        static int stage;
        static StringBuilder log;
        static readonly List<string> errors = new List<string>();
        static GameObject vehicle;
        static Vector3 before;
        static float farthest;
        static bool hooked, car, boat;
        static string seatVar;

        static KitControlsTest()
        {
            if (SessionState.GetString(ArmedKey, "") != "") Hook();
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredEditMode) return;
                string back = SessionState.GetString(ReturnSceneKey, "");
                if (back == "" || SessionState.GetString(ArmedKey, "") != "") return;
                SessionState.SetString(ReturnSceneKey, "");
                EditorSceneManager.OpenScene(back);
            };
        }

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            EditorApplication.update += Tick;
            Application.logMessageReceived += OnLog;
        }

        static void Unhook()
        {
            hooked = false;
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (!EditorApplication.isPlaying || message.Contains("This cannot be used during play mode")) return;
            bool kitLine = message.Contains("vehicle-kits") || message.Contains("BSKitItem") || message.Contains("Kit item");
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert || kitLine)
                if (errors.Count < 60) errors.Add($"[{type}] {message.Split('\n')[0]}" + (type == LogType.Exception ? " | " + stack.Split('\n').FirstOrDefault() : ""));
        }

        /// <summary>Library/FlightWorld/kit_attrs.txt: more attributes for the element, e.g. advanced-controls="true" advanced-button="false".</summary>
        static string Extra() => File.Exists("Library/FlightWorld/kit_attrs.txt") ? " " + File.ReadAllText("Library/FlightWorld/kit_attrs.txt").Trim() : "";

        /// <summary>Library/FlightWorld/kit_lift.txt: metres to put the snippet above the floor (snap-to-ground should set it down).</summary>
        static float Lift() => File.Exists("Library/FlightWorld/kit_lift.txt") && float.TryParse(File.ReadAllText("Library/FlightWorld/kit_lift.txt").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;

        static string Slug => File.Exists("Library/FlightWorld/kit_only.txt") ? File.ReadAllText("Library/FlightWorld/kit_only.txt").Trim() : "";
        static string LogPath(string slug) => $"Logs/kit_controls_{slug}.txt";

        /// <summary>The empty world: a 4 km floor at height 0 and a sun. Made once, under the git-ignored kits folder.</summary>
        static void EnsureScene()
        {
            if (File.Exists(ScenePath)) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.position = new Vector3(0f, -0.5f, 0f);
            floor.transform.localScale = new Vector3(4000f, 1f, 4000f);
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, 30f, 0f);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        [MenuItem("FlightWorld/Snippets/Test/Arm Controls Test")]
        public static void Arm()
        {
            string slug = Slug;
            if (string.IsNullOrEmpty(slug) || slug.Contains(",")) { File.WriteAllText("Logs/kit_controls.txt", "no single slug in kit_only.txt\n"); return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Flight/Kits/{slug}.prefab");
            bool isBoat = prefab != null && prefab.GetComponent<ScriptMachine>() != null;     // (only the boat's kit has the water graph on its root)
            string current = EditorSceneManager.GetActiveScene().path;
            if (current != ScenePath) SessionState.SetString(ReturnSceneKey, current);
            EditorSceneManager.SaveOpenScenes();
            EnsureScene();
            EditorSceneManager.OpenScene(ScenePath);

            bool live = File.Exists("Library/FlightWorld/kit_live.txt");
            string script = live ? "https://pilots.worldspace.host/vehicle-kits.js" : "vehicle-kits.js";
            string bundles = live ? "" : $" windows=\"{BundleHost}/windows/{slug}\" android=\"{BundleHost}/android/{slug}\"";
            var at = Place + (isBoat ? Vector3.up * BoatWater : Vector3.zero) + Vector3.up * Lift();
            string element = $"  <bs-snippet name=\"{slug}\" title=\"{slug}\" script=\"{script}\" single=\"true\" " +
                             FormattableString.Invariant($"position=\"{at.x:F1} {at.y:F1} {at.z:F1}\" rotation=\"0 0 0\"") + bundles + Extra() + "></bs-snippet>\n";
            if (!File.Exists(BackupPath)) File.Copy(IndexPath, BackupPath);
            // a stranger's page: nothing of this world's (its script.js also forwards the keys), only the snippet
            File.WriteAllText(IndexPath, "<html world-asset>\n<head>\n  <meta charset=\"utf-8\">\n  <title>Kit test</title>\n</head>\n<body>\n" + element + "</body>\n</html>\n");
            SessionState.SetString(ArmedKey, slug);
            File.WriteAllText(LogPath(slug), $"armed: {(live ? "LIVE (published script, CDN bundles)" : "local script and bundles")}, empty world, snippet at {at}\n");
            Hook();
        }

        [MenuItem("FlightWorld/Snippets/Test/Restore After Controls Test")]
        public static void Restore()
        {
            if (File.Exists(BackupPath))
            {
                File.Copy(BackupPath, IndexPath, true);
                File.Delete(BackupPath);
            }
            SessionState.SetString(ArmedKey, "");
            Unhook();
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                string back = SessionState.GetString(ReturnSceneKey, "");
                if (back != "") { SessionState.SetString(ReturnSceneKey, ""); EditorSceneManager.OpenScene(back); }
            }
        }

        static void Shot(string name, Vector3 from, Vector3 at)
        {
            const int width = 1280, height = 720;
            var go = new GameObject("__KitControlsCamera") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.transform.position = from;
                cam.transform.LookAt(at);
                cam.fieldOfView = 50f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 5000f;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                Directory.CreateDirectory("Library/FlightWorld/shots");
                File.WriteAllBytes($"Library/FlightWorld/shots/{name}.jpg", tex.EncodeToJPG(85));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        static void Check(string what, bool ok, string detail) => log.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}: {detail}");

        static void Finish(string slug)
        {
            log.AppendLine($"console: {errors.Count} lines");
            foreach (var line in errors.Distinct().Take(40)) log.AppendLine("   " + (line.Length > 300 ? line.Substring(0, 300) : line));
            log.AppendLine("DONE");
            File.AppendAllText(LogPath(slug), log.ToString());
            started = -1;
            Restore();
            EditorApplication.isPlaying = false;
        }

        static VariableDeclarations Graph() => Variables.Graph(GraphReference.New(vehicle.GetComponent<ScriptMachine>(), true));
        static float F(string name) { var g = Graph(); return g.IsDefined(name) ? Convert.ToSingle(g.Get(name)) : float.NaN; }
        static bool B(string name) { var g = Graph(); return g.IsDefined(name) && g.Get(name) is bool b && b; }

        static BS.BSLink Link() => UnityEngine.Object.FindFirstObjectByType<BS.BSLink>();
        static void Key(KeyCode key, bool down) { if (down) Link().OnKeyPress(key); else Link().OnKeyRelease(key); }
        static void Axis(int hand, float x, float y) =>
            EventBus.Trigger("OnControllerAxisUpdate", new CustomEventArgs("", new object[] { hand, x, y }));
        static void TriggerAxis(int hand, float value) =>
            EventBus.Trigger("OnTriggerAxisUpdate", new CustomEventArgs("", new object[] { hand, value }));

        static void Track()
        {
            if (vehicle != null) farthest = Mathf.Max(farthest, Vector3.Distance(vehicle.transform.position, before));
        }

        static string State()
        {
            var g = Graph();
            var names = car ? new[] { "Driving", "KeyDrive", "KeySteer", "KeyHandbrake", "TriggerThrottle", "StickRY", "Dead", "Awake", "WheelsOn",
                                      "ThrottleInput", "BrakeInput", "HandBrake", "FinalThrottle", "Revs", "Gear", "GearRatio", "Clutch", "AutoClutch", "Reversing", "Health" }
                            : new[] { "Piloting", "Throttle", "KeyThrottle", "StickThrottle", "EngineOutput", "KeyPitch", "StickPitch", "Dead", "Held", "Awake", "FuelLeft" };
            var body = vehicle.GetComponent<Rigidbody>();
            return string.Join(", ", names.Where(g.IsDefined).Select(n => $"{n}={g.Get(n)}")) +
                   $", speed {body.linearVelocity.magnitude:F1} m/s, constraints {body.constraints}, kinematic {body.isKinematic}, sleeping {body.IsSleeping()}" +
                   (car ? ", wheels: " + string.Join(" ", vehicle.GetComponentsInChildren<WheelCollider>(true).Select(w =>
                       $"{w.name}[on {w.enabled && w.gameObject.activeInHierarchy}, motor {w.motorTorque:F0}, brake {w.brakeTorque:F0}, rpm {w.rpm:F0}, grounded {w.isGrounded}]")) +
                       ", wheel graphs: " + string.Join(" ", vehicle.GetComponentsInChildren<ScriptMachine>(true).Where(m => m.gameObject != vehicle && m.nest.macro != null && m.nest.macro.name == "WheelCore").Select(m =>
                       {
                           var w = Variables.Graph(GraphReference.New(m, true));
                           string V(string n) => w.IsDefined(n) ? $"{n}={(w.Get(n) is float f ? f.ToString("0.##") : w.Get(n))}" : "";
                           return $"{m.name}[on {m.enabled && m.gameObject.activeInHierarchy}, {V("Grounded")} {V("RPM")} {V("CompressionLast")} {V("ForwardSpeed")} {V("ForceUsed")}]";
                       })) + $", height above the snippet {vehicle.transform.position.y - Place.y:F2}, pitch {Vector3.SignedAngle(Vector3.ProjectOnPlane(vehicle.transform.forward, Vector3.up), vehicle.transform.forward, vehicle.transform.right):F1} deg (+ = nose down), mass {vehicle.GetComponent<Rigidbody>().mass:F0}, joints on it " +
                       string.Join(",", UnityEngine.Object.FindObjectsByType<Joint>(FindObjectsSortMode.None).Where(j => j.connectedBody == vehicle.GetComponent<Rigidbody>() || j.transform.IsChildOf(vehicle.transform)).Select(j => $"{j.GetType().Name} on {j.name} (body {(j.GetComponent<Rigidbody>() != null ? j.GetComponent<Rigidbody>().mass.ToString("F0") : "-")} kg)")) + ", colliders reaching the floor: " +
                       string.Join(",", vehicle.GetComponentsInChildren<Collider>(false).Where(c => c.enabled && !c.isTrigger && c.bounds.min.y < Place.y + 0.06f)
                           .Select(c => $"{c.GetType().Name} '{c.name}' (parent '{(c.transform.parent != null ? c.transform.parent.name : "-")}', bottom {c.bounds.min.y - Place.y:F2}, layer {c.gameObject.layer})")) + ", touching: " +
                       string.Join(",", Physics.OverlapBox(vehicle.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).Select(c => c.bounds).Aggregate((x, y2) => { x.Encapsulate(y2); return x; }).center,
                           vehicle.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).Select(c => c.bounds).Aggregate((x, y2) => { x.Encapsulate(y2); return x; }).extents)
                           .Where(c => !c.transform.IsChildOf(vehicle.transform)).Select(c => $"{c.name}({(c.isTrigger ? "trigger" : "solid")},layer {c.gameObject.layer})").Distinct()) : "");
        }

        static void Tick()
        {
            string slug = SessionState.GetString(ArmedKey, "");
            if (slug == "") { Unhook(); return; }
            if (!EditorApplication.isPlaying) { started = -1; return; }
            double now = EditorApplication.timeSinceStartup;
            if (started < 0) { started = now; stage = 0; log = new StringBuilder(); errors.Clear(); vehicle = null; return; }
            double t = now - started, since = now - stageAt;
            void Next() { stage++; stageAt = now; }

            if (stage == 0)
            {
                var holder = GameObject.Find("snippet:" + slug);
                var body = holder != null ? holder.GetComponentsInChildren<Rigidbody>(true).FirstOrDefault(b =>
                    b.GetComponent<Variables>() != null && b.GetComponent<Variables>().declarations.IsDefined("BoomMessage")) : null;
                if (body != null)
                {
                    vehicle = body.gameObject;
                    car = Graph().IsDefined("Driving");
                    boat = vehicle.GetComponent<Variables>().declarations.IsDefined("IsBoat") && vehicle.GetComponent<Variables>().declarations.Get("IsBoat") is bool ib && ib;
                    Check("the kit arrives", true, $"{t:F1} s after Play, in an empty world with only the snippet on the page; asked for height {(boat ? BoatWater : 0f) + Lift():F1}, placed at height {holder.transform.position.y:F2}");
                    arrivedPose = "";
                    Next();
                }
                else if (t > 120) { Check("the kit arrives", false, $"nothing after {t:F0} s"); Finish(slug); }
                return;
            }
            if (vehicle == null) { Check("the vehicle stays", false, "it was destroyed"); Finish(slug); return; }
            Track();
            var decl = vehicle.GetComponent<Variables>().declarations;

            switch (stage)
            {
                case 1 when since > 5:
                {
                    log.AppendLine($"   before sitting: {State()}");
                    // the snippet's advanced-controls settings, as the kit's watch took them from the page
                    var watchVars = (decl.Get("Watch") as GameObject)?.GetComponent<Variables>()?.declarations;
                    if (watchVars != null && watchVars.IsDefined("KitName") && (string)watchVars.Get("KitName") != "")
                    {
                        string attrs = Extra();
                        bool wantOn = attrs.Contains("advanced-controls=\"true\""), wantHidden = attrs.Contains("advanced-button=\"false\"");
                        bool on = watchVars.Get("AdvancedControls") is bool a && a;
                        var button = watchVars.Get("AdvancedButton") as GameObject;
                        bool shown = button != null && button.activeInHierarchy;
                        var label = watchVars.Get("AdvancedLabel") as TMPro.TMP_Text;
                        Check("the snippet's advanced-controls settings are applied", on == wantOn && shown == !wantHidden,
                            $"asked: {(attrs == "" ? "(defaults)" : attrs)}; advanced controls {(on ? "on" : "off")}, button {(shown ? "shown" : "hidden")}, label '{(label != null ? label.text.Replace("\n", " ") : "none")}'");
                    }
                    // what a visitor sees as they arrive: the whole kit from behind its sign, and from the side
                    var all = vehicle.GetComponentsInChildren<Renderer>().Where(r => r.enabled).Select(r => r.bounds).Aggregate((x, y2) => { x.Encapsulate(y2); return x; });
                    float reach = all.extents.magnitude * 2.2f + 4f;
                    var signs = (decl.Get("ResetSign") as GameObject)?.transform.parent;
                    Shot($"kitctl_{slug}_arrive", all.center + new Vector3(-0.6f, 0.45f, -1f).normalized * reach, all.center);
                    Shot($"kitctl_{slug}_side", all.center + new Vector3(1f, 0.15f, 0.1f).normalized * reach, all.center);
                    // walk up to the pilot's seat and click it
                    var seat = decl.Get("PilotSeat") as GameObject;
                    var cam = Camera.main;
                    if (seat == null || cam == null) { Check("the pilot's seat can be clicked", false, $"seat {(seat == null ? "missing" : "ok")}, camera {(cam == null ? "missing" : "ok")}"); Finish(slug); return; }
                    var player = cam.transform.root;
                    var wanted = seat.transform.position + vehicle.transform.right * 1.0f + Vector3.up * 0.6f;
                    player.position += wanted - cam.transform.position;
                    Physics.SyncTransforms();
                    var scene = BS.BSScene.Instance();
                    scene.Click(seat, seat.transform.position, Vector3.up);
                    log.AppendLine($"   clicked '{seat.name}' from {Vector3.Distance(cam.transform.position, seat.transform.position):F1} m");
                    Next();
                    break;
                }
                case 2 when since > 2:
                {
                    var seat = (GameObject)decl.Get("PilotSeat");
                    float eyeToSeat = Vector3.Distance(Camera.main.transform.position, seat.transform.position);
                    bool inCharge = car ? B("Driving") : B("Piloting");
                    Check("clicking the seat seats you and puts you in charge", inCharge && eyeToSeat < 1.5f,
                        $"{(car ? "Driving" : "Piloting")}={inCharge}, eyes {eyeToSeat:F2} m from the seat; {State()}");
                    if (!inCharge) { Finish(slug); return; }
                    before = vehicle.transform.position;
                    farthest = 0f;
                    // keyboard, through the page and the snippet's script: each key reaches the graph
                    var keys = car
                        ? new[] { (KeyCode.W, "KeyDrive", 1f), (KeyCode.S, "KeyDrive", -1f), (KeyCode.D, "KeySteer", 1f), (KeyCode.Space, "KeyHandbrake", 1f) }
                        : new[] { (KeyCode.LeftShift, "KeyThrottle", 1f), (KeyCode.W, "KeyPitch", 1f), (KeyCode.A, "KeyRoll", 1f), (KeyCode.E, "KeyYaw", 1f) };
                    keyChecks = keys;
                    keyIndex = 0;
                    Key(keys[0].Item1, true);
                    Next();
                    break;
                }
                case 3 when since > 0.6:
                {
                    var (key, variable, value) = keyChecks[keyIndex];
                    float got = F(variable);
                    keyResults.Add($"{key}->{variable}={got:0.##}" + (Mathf.Abs(got - value) < 0.01f ? "" : $" (wanted {value})"));
                    keyOk &= Mathf.Abs(got - value) < 0.01f;
                    Key(key, false);
                    keyIndex++;
                    if (keyIndex < keyChecks.Length) { Key(keyChecks[keyIndex].Item1, true); stageAt = now; break; }
                    Check("keys reach the vehicle through the snippet's script", keyOk, string.Join(", ", keyResults));
                    keyResults.Clear(); keyOk = true;
                    // then drive or fly on the keyboard alone
                    Key(car ? KeyCode.W : KeyCode.LeftShift, true);
                    Next();
                    break;
                }
                case 4 when since > (car ? 8 : 14) || (since > 2 && farthest > 25f):
                    Key(car ? KeyCode.W : KeyCode.LeftShift, false);
                    Check("it moves on the keyboard", farthest > 3f, $"{farthest:F1} m at the farthest; {State()}");
                    before = vehicle.transform.position;
                    farthest = 0f;
                    // VR: sticks and triggers as the app raises them
                    if (car) { TriggerAxis(1, 1f); Axis(1, 0.5f, 0f); }
                    else { Axis(0, 0f, 1f); Axis(1, -0.4f, 0.3f); TriggerAxis(0, 0f); }
                    Next();
                    break;
                case 5 when since > 0.5:
                    if (car) Check("the VR trigger and stick reach the vehicle", Mathf.Abs(F("TriggerThrottle") - 1f) < 0.01f && Mathf.Abs(F("StickSteerR") - 0.5f) < 0.01f,
                        $"TriggerThrottle={F("TriggerThrottle")}, StickSteerR={F("StickSteerR")}");
                    else if ((decl.Get("Watch") as GameObject)?.GetComponent<Variables>()?.declarations is VariableDeclarations wv && wv.IsDefined("AdvancedControls") && wv.Get("AdvancedControls") is bool adv && adv)
                        log.AppendLine("   (VR sticks not checked: with advanced controls on they pick the dials' functions, by design)");
                    else if (Graph().IsDefined("RightThrustX") && decl.IsDefined("RightStickThrusts") && decl.Get("RightStickThrusts") is bool thrusts && thrusts)
                        Check("the VR sticks reach the vehicle", Mathf.Abs(F("StickThrottle") - 1f) < 0.01f && Mathf.Abs(F("RightThrustZ") - 0.3f) < 0.01f,
                            $"StickThrottle={F("StickThrottle")}, RightThrustX={F("RightThrustX")}, RightThrustZ={F("RightThrustZ")}");
                    else Check("the VR sticks reach the vehicle", Mathf.Abs(F("StickThrottle") - 1f) < 0.01f && Mathf.Abs(F("StickPitch") - 0.3f) < 0.01f && Mathf.Abs(F("StickRoll") - 0.4f) < 0.01f,
                        $"StickThrottle={F("StickThrottle")}, StickPitch={F("StickPitch")}, StickRoll={F("StickRoll")}");
                    if (!car) Axis(1, 0f, 0f);         // (stick centred again: the throttle alone moves it)
                    Next();
                    break;
                case 6 when since > (car ? 8 : 14) || (since > 2 && farthest > 25f):
                    Check("it moves on the VR controls", farthest > 3f, $"{farthest:F1} m at the farthest; {State()}");
                    if (car) { TriggerAxis(1, 0f); Axis(1, 0f, 0f); } else Axis(0, 0f, 0f);
                    Finish(slug);
                    break;
            }
        }

        static string arrivedPose;
        static (KeyCode, string, float)[] keyChecks;
        static int keyIndex;
        static bool keyOk = true;
        static readonly List<string> keyResults = new List<string>();
    }
}

