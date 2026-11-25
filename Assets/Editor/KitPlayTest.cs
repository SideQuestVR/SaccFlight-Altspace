using System;
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
    /// Loads one vehicle kit the way a stranger's world would: a hand-written bs-snippet element in the page, the loader
    /// script (Assets/WebRoot/vehicle-kits.js) and the kit's bundle from a local web server, in Play mode. The kit is put
    /// on free flat ground away from the world's own fleet. Checks that it arrives, stands where it was put, is wired to
    /// its own clock and sign, and moves under its own power with the scripted pilot.
    ///
    ///   python -m http.server 42071 --directory Library/FlightWorld/kits      (serves the bundles)
    ///   Library/FlightWorld/kit_only.txt = the kit's slug; FlightWorld/Snippets/Test/Arm Kit Test; then Play.
    /// Output: Logs/kit_test_&lt;slug&gt;.txt (PASS / FAIL lines, then DONE). The page is put back when the test ends.
    /// </summary>
    [InitializeOnLoad]
    public static class KitPlayTest
    {
        const string ArmedKey = "FlightWorld.KitPlayTest.Slug";
        const string PlaceKey = "FlightWorld.KitPlayTest.Place";
        const string IndexPath = "Assets/WebRoot/index.html";
        const string BackupPath = "Library/FlightWorld/index.html.kit_test_backup";
        const string BundleHost = "http://localhost:42071";

        static double started = -1, loadedAt = -1, pilotedAt = -1;
        static int stage;
        static StringBuilder log;
        static readonly List<string> errors = new List<string>();
        static Vector3 parkedAt, before;
        static GameObject vehicle;
        static bool hooked, crashed;
        static float farthest;

        static KitPlayTest()
        {
            if (SessionState.GetString(ArmedKey, "") != "") Hook();
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
            if (!EditorApplication.isPlaying) return;
            bool kitLine = message.Contains("vehicle-kits") || message.Contains("BSKitItem") || message.Contains("Kit item") || message.Contains("BundleCheck");
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert || kitLine)
                if (errors.Count < 60) errors.Add($"[{type}] {message.Split('\n')[0]}" + (type == LogType.Exception ? " | " + stack.Split('\n').FirstOrDefault() : ""));
        }

        static string Slug => File.Exists("Library/FlightWorld/kit_only.txt") ? File.ReadAllText("Library/FlightWorld/kit_only.txt").Trim() : "";

        [MenuItem("FlightWorld/Snippets/Test/Arm Kit Test")]
        public static void Arm()
        {
            string slug = Slug;
            string logPath = $"Logs/kit_test_{slug}.txt";
            var spawn = GameObject.Find("RunwaySpawn");
            if (string.IsNullOrEmpty(slug) || slug.Contains(",") || spawn == null) { File.WriteAllText("Logs/kit_test.txt", "no single slug in kit_only.txt, or no RunwaySpawn\n"); return; }
            // free flat ground near the arrival point, clear of the fleet
            Physics.SyncTransforms();
            Vector3? place = null;
            float heading = spawn.transform.eulerAngles.y;
            var tried = new StringBuilder();
            foreach (var offset in new[] { new Vector3(60, 0, -60), new Vector3(90, 0, -20), new Vector3(0, 0, -90), new Vector3(120, 0, -90), new Vector3(-60, 0, -120), new Vector3(150, 0, 0), new Vector3(60, 0, -160) })
            {
                var at = spawn.transform.position + spawn.transform.rotation * offset;
                if (!Physics.Raycast(at + Vector3.up * 200f, Vector3.down, out var hit, 400f, ~0, QueryTriggerInteraction.Ignore)) { tried.AppendLine($"   {at:F0}: no ground"); continue; }
                bool flat = hit.normal.y > 0.995f;
                bool clear = !Physics.CheckBox(hit.point + Vector3.up * 6f, new Vector3(22f, 5f, 26f), Quaternion.Euler(0f, heading, 0f), ~0, QueryTriggerInteraction.Ignore);
                tried.AppendLine($"   {at:F0}: ground {hit.point.y:F2} ({hit.collider.name}), flat {flat}, clear {clear}");
                if (flat && clear) { place = hit.point; break; }
            }
            // a boat goes on the water: ahead of the world's own boat, at the level that one floats on
            var worldBoat = GameObject.Find("Aircraft")?.transform.Cast<Transform>().FirstOrDefault(v =>
                v.GetComponent<Variables>() is Variables bv && bv.declarations.IsDefined("IsBoat") && bv.declarations.Get("IsBoat") is bool isBoat && isBoat);
            var kitPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Flight/Kits/{slug}.prefab");
            if (kitPrefab != null && kitPrefab.GetComponent<ScriptMachine>() != null && worldBoat != null)
            {
                float level = System.Convert.ToSingle(worldBoat.GetComponent<Variables>().declarations.Get("WaterLevel"));
                var ahead = worldBoat.position + Quaternion.Euler(0f, worldBoat.eulerAngles.y, 0f) * new Vector3(0f, 0f, 45f);
                place = new Vector3(ahead.x, level, ahead.z);
                heading = worldBoat.eulerAngles.y;
                tried.AppendLine($"   boat: on the water at {place.Value:F1}");
            }
            if (place == null) { File.WriteAllText(logPath, "no free flat ground found:\n" + tried); return; }
            var p = place.Value;
            string where = FormattableString.Invariant($"{p.x:F2} {p.y:F2} {p.z:F2}");
            if (!File.Exists(BackupPath)) File.Copy(IndexPath, BackupPath);
            string page = File.ReadAllText(BackupPath);
            // Library/FlightWorld/kit_live.txt present: the published script and the bundles it names on the CDN, as a
            // world that added the snippet gets them; otherwise this project's copy of the script and the local bundles.
            bool live = File.Exists("Library/FlightWorld/kit_live.txt");
            string script = live ? "https://pilots.worldspace.host/vehicle-kits.js" : "vehicle-kits.js";
            string bundles = live ? "" : $" windows=\"{BundleHost}/windows/{slug}\" android=\"{BundleHost}/android/{slug}\"";
            string element = $"  <bs-snippet name=\"{slug}\" title=\"{slug}\" script=\"{script}\" single=\"true\" position=\"{where}\" " +
                             FormattableString.Invariant($"rotation=\"0 {heading:F1} 0\"") + bundles + "></bs-snippet>\n";
            tried.AppendLine(live ? "   LIVE: the published script and the CDN's bundles" : "   local script and bundles");
            File.WriteAllText(IndexPath, page.Replace("</body>", element + "</body>"));
            SessionState.SetString(ArmedKey, slug);
            SessionState.SetString(PlaceKey, FormattableString.Invariant($"{p.x},{p.y},{p.z},{heading}"));
            File.WriteAllText(logPath, $"armed at {where}, heading {heading:F1}\n{tried}");
            Hook();
        }

        /// <summary>Writes "editing" or "playing" to Library/FlightWorld/kit_state.txt (Tools/kit_tests.sh waits for Play to end).</summary>
        [MenuItem("FlightWorld/Snippets/Test/Is Editing")]
        public static void IsEditing() =>
            File.WriteAllText("Library/FlightWorld/kit_state.txt", EditorApplication.isPlayingOrWillChangePlaymode ? "playing" : "editing");

        [MenuItem("FlightWorld/Snippets/Test/Restore Page")]
        public static void Restore()
        {
            if (File.Exists(BackupPath))
            {
                File.Copy(BackupPath, IndexPath, true);
                File.Delete(BackupPath);
            }
            SessionState.SetString(ArmedKey, "");
            Unhook();
        }

        static void Shot(string name, Vector3 from, Vector3 at, float fov)
        {
            const int width = 1600, height = 900;
            var go = new GameObject("__KitTestCamera") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.transform.position = from;
                cam.transform.LookAt(at);
                cam.fieldOfView = fov;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 22000f;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                Directory.CreateDirectory("Library/FlightWorld/shots");
                File.WriteAllBytes($"Library/FlightWorld/shots/{name}.jpg", tex.EncodeToJPG(88));
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
            File.AppendAllText($"Logs/kit_test_{slug}.txt", log.ToString());
            started = -1;
            Restore();
            EditorApplication.isPlaying = false;
        }

        static void Tick()
        {
            string slug = SessionState.GetString(ArmedKey, "");
            if (slug == "") { Unhook(); return; }
            if (!EditorApplication.isPlaying) { started = -1; return; }
            double now = EditorApplication.timeSinceStartup;
            if (started < 0) { started = now; loadedAt = -1; pilotedAt = -1; stage = 0; log = new StringBuilder(); errors.Clear(); vehicle = null; return; }
            double t = now - started;
            var numbers = SessionState.GetString(PlaceKey, "0,0,0,0").Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var place = new Vector3(numbers[0], numbers[1], numbers[2]);

            if (stage == 0)
            {
                var holder = GameObject.Find("snippet:" + slug);
                var body = holder != null ? holder.GetComponentsInChildren<Rigidbody>(true).FirstOrDefault(b => b.GetComponent<Variables>() != null && b.GetComponent<Variables>().declarations.IsDefined("BoomMessage")) : null;
                if (body != null)
                {
                    vehicle = body.gameObject;
                    loadedAt = now;
                    var kit = holder.transform.GetChild(0);
                    // (this world's sea has a solid collider that its own boat is told to ignore; a kit's boat is for water without one)
                    if (kit.GetComponent<ScriptMachine>() != null && GameObject.Find("CityEnvironment/SeaCollider") is GameObject sea && sea.GetComponent<Collider>() is Collider seaBox)
                        seaBox.enabled = false;
                    Check("the kit arrives", true, $"{t:F1} s after Play; holder at {holder.transform.position:F2} (asked {place:F2}), heading {holder.transform.eulerAngles.y:F1}; kit root '{kit.name}' with {kit.GetComponentsInChildren<Transform>(true).Length} objects");
                    var d = vehicle.GetComponent<Variables>().declarations;
                    var watch = d.Get("Watch") as GameObject;
                    var sign = d.Get("ResetSign") as GameObject;
                    var respawn = d.Get("RespawnPoint") as Transform;
                    bool own = watch != null && watch.transform.IsChildOf(kit) && sign != null && sign.transform.IsChildOf(kit) && respawn != null && respawn.IsChildOf(kit);
                    var clock = watch != null ? watch.GetComponent<Variables>().declarations : null;
                    Check("wired to its own clock, sign and respawn point", own,
                        $"watch {(watch != null ? watch.name : "null")}, sign {(sign != null ? sign.transform.parent.name : "null")}, vehicles {(clock?.Get("Vehicles") as System.Collections.IList)?.Count}, " +
                        $"tick count {clock?.Get("TickCount")}, respawn at {(respawn != null ? respawn.position.ToString("F1") : "null")}");
                    var ids = kit.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == "BSObjectId")
                        .Select(c => (string)c.GetType().GetField("Id").GetValue(c)).ToList();
                    Check("network ids are the kit's", ids.All(i => i.StartsWith("kfw-")), string.Join(", ", ids.Take(8)));
                    stage = 1;
                }
                else if (t > 120)
                {
                    Check("the kit arrives", false, $"nothing after {t:F0} s (holder {(holder != null ? "made, " + holder.transform.childCount + " children" : "missing")})");
                    Finish(slug);
                }
                return;
            }

            var rb = vehicle != null ? vehicle.GetComponent<Rigidbody>() : null;
            if (rb == null) { Check("the vehicle stays", false, "it was destroyed"); Finish(slug); return; }
            var machine = vehicle.GetComponent<ScriptMachine>();
            double sinceLoad = now - loadedAt;
            switch (stage)
            {
                case 1 when sinceLoad > 6:
                    parkedAt = vehicle.transform.position;
                    var holderT = GameObject.Find("snippet:" + slug).transform;
                    var local = holderT.InverseTransformPoint(parkedAt);
                    Check("it stands where it was put", Mathf.Abs(local.x) < 1.5f && Mathf.Abs(local.z) < 1.5f && local.y > -0.3f && local.y < 8f && rb.linearVelocity.magnitude < 0.6f,
                        $"vehicle {local:F2} from the snippet's position, speed {rb.linearVelocity.magnitude:F2} m/s, tilt {Vector3.Angle(vehicle.transform.up, Vector3.up):F1} deg, graph {(machine != null && machine.enabled ? "running" : "off")}");
                    // pictures: the sign with its panel as a player in front of it sees them, and the whole kit
                    var signBoard = vehicle.GetComponent<Variables>().declarations.Get("ResetSign") as GameObject;
                    var stand = vehicle.GetComponent<Variables>().declarations.Get("RespawnPoint") as Transform;
                    if (signBoard != null && stand != null)
                    {
                        var signs = signBoard.transform.parent;
                        Shot($"kit_{slug}_sign", signs.TransformPoint(new Vector3(-1.0f, 1.5f, -3.4f)), signs.TransformPoint(new Vector3(-1.0f, 1.35f, 0f)), 60f);
                        float far = Mathf.Max(12f, Vector3.Distance(signs.position, vehicle.transform.position) * 1.6f);
                        Shot($"kit_{slug}_all", signs.TransformPoint(new Vector3(-3f, 0f, -far * 0.6f)) + Vector3.up * far * 0.35f, Vector3.Lerp(signs.position, vehicle.transform.position, 0.6f) + Vector3.up, 60f);
                    }
                    var g = Variables.Graph(GraphReference.New(machine, true));
                    g.Set("TestPilot", true);
                    bool car = g.IsDefined("Driving");
                    g.Set(car ? "Driving" : "Piloting", true);
                    before = parkedAt;
                    farthest = 0f;
                    crashed = false;
                    pilotedAt = now;
                    stage = 2;
                    break;
                case 2:
                    var vars = Variables.Graph(GraphReference.New(machine, true));
                    if (vars.IsDefined("Driving")) vars.Set("KeyDrive", 1f);
                    else { vars.Set("Throttle", 1f); vars.Set("KeyPitch", 0f); vars.Set("KeyRoll", 0f); }
                    // (the farthest it got: one that flies into a building is back at its place by the end)
                    farthest = Mathf.Max(farthest, Vector3.Distance(vehicle.transform.position, before));
                    if (vars.IsDefined("Dead") && vars.Get("Dead") is bool dead && dead) crashed = true;
                    if (now - pilotedAt > 16)
                    {
                        float moved = farthest;
                        string State(string name) => vars.IsDefined(name) ? $"{name}={vars.Get(name)}" : null;
                        var clockNow = (vehicle.GetComponent<Variables>().declarations.Get("Watch") as GameObject)?.GetComponent<Variables>().declarations;
                        string state = string.Join(", ", new[] { "Piloting", "Driving", "Held", "Dead", "Awake", "Throttle", "EngineOutput", "EngineOn", "FuelLeft", "VTOLAngle", "Taxiing", "Floating", "StartHold" }.Select(State).Where(x => x != null)) +
                                       $", constraints {rb.constraints}, clock has pilot {clockNow?.Get("HasPilotVehicle")}";
                        Check("it moves under its own power", moved > 2f, $"{moved:F1} m at the farthest in 16 s with the scripted pilot{(crashed ? " (it crashed into something and came back)" : "")}, speed {rb.linearVelocity.magnitude:F1} m/s, height {vehicle.transform.position.y - before.y:+0.0;-0.0} m ({state})");
                        if (vars.IsDefined("Driving")) { vars.Set("KeyDrive", 0f); vars.Set("Driving", false); }
                        else { vars.Set("Throttle", 0f); vars.Set("Piloting", false); }
                        vars.Set("TestPilot", false);
                        stage = 3;
                    }
                    break;
                case 3 when now - pilotedAt > 18:
                    Finish(slug);
                    break;
            }
        }
    }
}

