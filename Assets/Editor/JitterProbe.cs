using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Do vehicles shake? Records, at every rendered frame of Play mode, where each vehicle is drawn (its transform,
    /// which is what the eye sees) and where its body is, then measures how unevenly it moves from frame to frame.
    ///
    /// Smooth motion: each frame's step is close to the average of its neighbours' steps. Shake: steps that go
    /// long-short-long, or a body that sits still but whose drawn position wobbles. Reported per vehicle as the
    /// size of the frame-to-frame unevenness in millimetres and degrees, with the worst frames listed.
    ///
    /// Arm it (it records 12 s, starting 4 s after Play), then enter Play mode. To include a flying vehicle, arm
    /// the scripted flight test as well; it records whatever is in the scene. Output: Logs/jitter_probe.txt.
    /// </summary>
    [InitializeOnLoad]
    public static class JitterProbe
    {
        const string ArmedKey = "FlightWorld.JitterProbe.Armed";
        const string ReportPath = "Logs/jitter_probe.txt";
        static float Start => File.Exists("Library/FlightWorld/jitter_start.txt") && float.TryParse(File.ReadAllText("Library/FlightWorld/jitter_start.txt").Trim(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 4f;
        const float Length = 12f;

        struct Sample
        {
            public float time, dt;
            public int frame;
            public bool stepped;            // a physics step ran since the last rendered frame
            public Vector3 drawn, body;
            public Quaternion drawnRot, bodyRot;
            public Vector3 eye;             // the vehicle's eye point (for the pilot's view)
            public Vector3 camera;
        }

        static readonly Dictionary<Transform, List<Sample>> samples = new Dictionary<Transform, List<Sample>>();
        static double started = -1;
        static int lastRecorded = -1, stepsSeen, stepsAtLast;
        static bool hooked;

        static JitterProbe()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Tools/Probe Vehicle Jitter")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(ReportPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying)
            {
                started = -1;
                Unhook();
                return;
            }
            if (started < 0)
            {
                started = EditorApplication.timeSinceStartup;
                samples.Clear();
                view.Clear();
                sat = false;
                seated = null;
                lastRecorded = -1;
                stepsSeen = stepsAtLast = 0;
                Hook();
                return;
            }
            double t = EditorApplication.timeSinceStartup - started;
            // With Library/FlightWorld/jitter_sit.txt naming a vehicle, the editor's own player is sat in its pilot
            // seat (as a click would) shortly before recording, so the camera rides with it.
            if (!sat && t > Start - 1.5f && File.Exists(SitFile))
            {
                sat = true;
                string name = File.ReadAllText(SitFile).Trim();
                var vehicle = GameObject.Find("Aircraft")?.transform.Find(name);
                var seat = vehicle != null ? vehicle.GetComponent<Unity.VisualScripting.Variables>().declarations.Get("PilotSeatComponent") as Component : null;
                if (seat != null) { seat.GetType().GetMethod("_Sit").Invoke(seat, null); seated = vehicle; }
            }
            if (t > Start + Length + 0.5) Finish();
        }

        const string SitFile = "Library/FlightWorld/jitter_sit.txt";
        static bool sat;
        static Transform seated;
        static readonly List<(float t, float dt, Vector3 local, Quaternion localRot)> view = new List<(float, float, Vector3, Quaternion)>();

        static void Hook()
        {
            if (hooked) return;
            RenderPipelineManager.beginContextRendering += OnRender;
            Physics.simulationMode = Physics.simulationMode;     // (no-op: keeps the default simulation)
            hooked = true;
        }

        static void Unhook()
        {
            if (!hooked) return;
            RenderPipelineManager.beginContextRendering -= OnRender;
            hooked = false;
        }

        static void OnRender(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!EditorApplication.isPlaying || started < 0) return;
            double t = EditorApplication.timeSinceStartup - started;
            if (t < Start || t > Start + Length) return;
            if (Time.frameCount == lastRecorded) return;            // one sample per frame, however many cameras
            lastRecorded = Time.frameCount;
            var camera = Camera.main;
            if (seated != null && camera != null)
                view.Add((Time.time, Time.deltaTime, seated.InverseTransformPoint(camera.transform.position),
                          Quaternion.Inverse(seated.rotation) * camera.transform.rotation));
            foreach (var rootName in new[] { "Aircraft", "GroundVehicles" })
            {
                var fleet = GameObject.Find(rootName);
                if (fleet == null) continue;
                foreach (Transform vehicle in fleet.transform)
                {
                    var body = vehicle.GetComponent<Rigidbody>();
                    if (body == null) continue;
                    var variables = vehicle.GetComponent<Unity.VisualScripting.Variables>();
                    Transform eye = variables != null && variables.declarations.IsDefined("EyePoint") ? variables.declarations.Get("EyePoint") as Transform : null;
                    if (!samples.TryGetValue(vehicle, out var list)) samples[vehicle] = list = new List<Sample>();
                    list.Add(new Sample
                    {
                        time = Time.time, dt = Time.deltaTime, frame = Time.frameCount,
                        stepped = Time.fixedTime != (list.Count > 0 ? lastFixed[vehicle] : -1f),
                        drawn = vehicle.position, drawnRot = vehicle.rotation,
                        body = body.position, bodyRot = body.rotation,
                        eye = eye != null ? eye.position : vehicle.position,
                        camera = camera != null ? camera.transform.position : Vector3.zero,
                    });
                    lastFixed[vehicle] = Time.fixedTime;
                }
            }
        }

        static readonly Dictionary<Transform, float> lastFixed = new Dictionary<Transform, float>();

        static void Finish()
        {
            Unhook();
            var text = new StringBuilder();
            text.AppendLine($"recorded {Length:F0} s of rendered frames; unevenness = how far each frame's step differs from the average of the steps either side of it");
            text.AppendLine($"from {Start:F0} s after Play");
            text.AppendLine("vehicle        frames  speed m/s   drawn: mean / p95 / max mm    angle: p95 / max deg   body vs drawn mm   no-step frames   height range mm, up/down swings a second");
            foreach (var pair in samples.OrderBy(p => p.Key.name))
            {
                var list = pair.Value;
                if (list.Count < 10) continue;
                var bumps = new List<(float mm, int i)>();
                var turns = new List<float>();
                float speed = 0f;
                for (int i = 1; i < list.Count - 1; i++)
                {
                    // the step into this frame and the step out of it, each scaled to the frame's own length, compared
                    var a = (list[i].drawn - list[i - 1].drawn) / Mathf.Max(list[i].dt, 1e-4f);
                    var b = (list[i + 1].drawn - list[i].drawn) / Mathf.Max(list[i + 1].dt, 1e-4f);
                    var expected = (a + b) * 0.5f * list[i].dt;
                    var actual = list[i].drawn - list[i - 1].drawn;
                    bumps.Add(((actual - expected).magnitude * 1000f, i));
                    float ra = Quaternion.Angle(list[i - 1].drawnRot, list[i].drawnRot) / Mathf.Max(list[i].dt, 1e-4f);
                    float rb = Quaternion.Angle(list[i].drawnRot, list[i + 1].drawnRot) / Mathf.Max(list[i + 1].dt, 1e-4f);
                    turns.Add(Mathf.Abs(ra - rb) * list[i].dt);
                    speed += b.magnitude;
                }
                speed /= Mathf.Max(list.Count - 2, 1);
                var sorted = bumps.Select(x => x.mm).OrderBy(x => x).ToList();
                var turnSorted = turns.OrderBy(x => x).ToList();
                float P(List<float> v, float q) => v.Count == 0 ? 0f : v[Mathf.Clamp((int)(q * (v.Count - 1)), 0, v.Count - 1)];
                float bodyGap = list.Average(s => (s.body - s.drawn).magnitude) * 1000f;
                int noStep = list.Count(s => !s.stepped);
                float lowY = list.Min(x => x.drawn.y), highY = list.Max(x => x.drawn.y);
                int swings = 0;
                for (int i = 2; i < list.Count; i++)
                {
                    float d1 = list[i - 1].drawn.y - list[i - 2].drawn.y, d2 = list[i].drawn.y - list[i - 1].drawn.y;
                    if (d1 * d2 < 0f && Mathf.Abs(d2) > 0.0002f) swings++;
                }
                float span = list.Last().time - list.First().time;
                text.AppendLine($"{pair.Key.name,-14} {list.Count,5}  {speed,8:F1}   {sorted.Average(),7:F2} / {P(sorted, 0.95f),7:F2} / {sorted.Last(),8:F2}    {P(turnSorted, 0.95f),6:F3} / {turnSorted.Last(),6:F3}      {bodyGap,8:F2}      {noStep,4}        {(highY - lowY) * 1000f,8:F1}  {swings / Mathf.Max(span, 0.1f) / 2f,5:F1}");
                if (sorted.Last() > 5f)
                    foreach (var (mm, i) in bumps.OrderByDescending(x => x.mm).Take(4))
                        text.AppendLine($"      worst: frame {list[i].frame} t {list[i].time:F3} dt {list[i].dt * 1000f:F1} ms  {mm:F1} mm  physics step this frame {list[i].stepped}");
            }
            if (view.Count > 10)
            {
                // The seated view: where the camera is in the vehicle's own space, frame by frame. A rider sitting
                // still sees the cockpit still; any change here is the cockpit shaking in front of their eyes.
                var steps = new List<float>();
                var turns = new List<float>();
                for (int i = 1; i < view.Count; i++)
                {
                    steps.Add((view[i].local - view[i - 1].local).magnitude * 1000f);
                    turns.Add(Quaternion.Angle(view[i].localRot, view[i - 1].localRot));
                }
                steps.Sort();
                turns.Sort();
                float Q(List<float> v, float q) => v[Mathf.Clamp((int)(q * (v.Count - 1)), 0, v.Count - 1)];
                var span = view.Select(v => v.local).ToList();
                var mid = new Vector3(span.Average(v => v.x), span.Average(v => v.y), span.Average(v => v.z));
                text.AppendLine($"SEATED VIEW in {seated?.name ?? "?"} ({view.Count} frames): camera moves against the cockpit by mean {steps.Average():F2} / p95 {Q(steps, 0.95f):F2} / max {steps.Last():F2} mm a frame, " +
                                $"turns mean {turns.Average():F3} / p95 {Q(turns, 0.95f):F3} / max {turns.Last():F3} deg a frame; wanders up to {span.Max(v => (v - mid).magnitude) * 1000f:F0} mm from its average place");
            }
            text.AppendLine($"Time.fixedDeltaTime {Time.fixedDeltaTime * 1000f:F2} ms; frames recorded {samples.Values.FirstOrDefault()?.Count ?? 0}");
            Directory.CreateDirectory("Logs");
            File.WriteAllText(ReportPath, text.ToString());
            SessionState.SetBool(ArmedKey, false);
            EditorApplication.update -= Tick;
            started = -1;
            EditorApplication.isPlaying = false;
        }
    }
}
