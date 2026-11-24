using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SaccFlightAndVehicles;
using Unity.VisualScripting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Puts the ground vehicles from SaccFlightAndVehicles (MIT, (c) 2021 Sacchan-VRC) into the scene as
    /// drivable cars and a tank, the same way SaccFleetBuilder does for aircraft: each comes from its original
    /// prefab (model, armature, colliders, animator, sounds, effects kept as authored); the settings of its
    /// VRChat scripts are read through the data-only stand-ins, which are then removed; materials are rebuilt
    /// on this world's shaders; seats become platform seats; and the generated graphs are attached - GroundCore
    /// on the vehicle and WheelCore on every wheel - with the prefab's own values as their variables.
    ///
    /// Three kinds of setting are read straight from the prefab file instead, because the stand-in scripts do
    /// not carry them: the gear ratios, the engine's response curve and the tyres' grip curves.
    ///
    /// Re-running replaces the "GroundVehicles" root it built before and nothing else.
    /// </summary>
    public static class SaccGroundBuilder
    {
        const string ReportPath = "Logs/ground_build_report.txt";
        const string PrefabDir = "Assets/Sacc/Prefabs";
        public const string RootName = "GroundVehicles";

        /// <summary>The row behind the arrival point, listed from the runway side outwards: (prefab, name in the world).</summary>
        static readonly (string prefab, string name)[] Fleet =
        {
            ("ST-Baron_Main", "Baron"), ("ST-Iccarus_Main", "Iccarus"), ("ST-Shoreline_Main", "Shoreline"),
            ("ST-Thunder_Main", "Thunder"), ("SaccTankMain", "Tank"),
        };

        // Metres from the arrival point: the row stands this far behind it, facing away, starting here on the
        // runway side and growing away from the runway.
        const float RowBehind = 22f, RowStartsAt = -20f, GapBetweenVehicles = 4f;

        // Where each vehicle comes to rest on its suspension, in metres from the height worked out from its
        // springs (measured by the drive test, which reports "settled ... from its built height"). A vehicle
        // rests held at the height it was built at, so that height has to be the one it rides at.
        static readonly Dictionary<string, float> Settles = new Dictionary<string, float>
        {
            ["Baron"] = -0.05f, ["Iccarus"] = -0.02f, ["Shoreline"] = -0.03f, ["Thunder"] = -0.02f, ["Tank"] = 0.02f,
        };

        // Parts of a prefab that are not carried over: weapons and their dial menus, turret and commander
        // functions, VRChat sync and camera helpers. Removed by name wherever they sit in the vehicle.
        static readonly HashSet<string> Dropped = new HashSet<string>
        {
            "DialFunctions", "GunnerDialFunctions", "TurretFunctions", "CommanderFunctions", "SyncScript", "CameraPositions",
            "WaterTrigger", "AGMLockTarget", "AAMTarget", "StickDisplayCanvas", "ResupplyTrigger",
        };

        [MenuItem("FlightWorld/Flight/Build Ground Vehicles In Scene")]
        public static void BuildVehicles()
        {
            var report = new StringBuilder();
            int failed = 0;
            try
            {
                GroundGraphs.Regenerate();
                var graph = AssetDatabase.LoadAssetAtPath<ScriptGraphAsset>(GroundGraphs.CorePath);
                var wheelGraph = AssetDatabase.LoadAssetAtPath<ScriptGraphAsset>(GroundGraphs.WheelPath);
                if (graph == null || wheelGraph == null)
                    throw new Exception("ground graphs missing (see Logs/ground_graphs_report.txt)");
                var required = GroundGraphs.BuildCore().objectVariables;
                var wheelRequired = GroundGraphs.BuildWheel().objectVariables;

                var scene = EditorSceneManager.GetActiveScene();
                foreach (var old in scene.GetRootGameObjects().Where(o => o.name == RootName).ToArray())
                    UnityEngine.Object.DestroyImmediate(old);
                var fleet = new GameObject(RootName);
                var signs = FleetParts.Child(fleet, "Signs");

                var spawn = GameObject.Find("RunwaySpawn");
                var origin = spawn != null ? spawn.transform.position : new Vector3(0f, 1.1f, 0f);
                var facing = spawn != null ? spawn.transform.rotation : Quaternion.identity;
                var away = facing * Quaternion.Euler(0f, 180f, 0f);            // the row faces away from the arrival point
                float ground = origin.y - 0.1f;
                SaccMaterials.Unconverted.Clear();

                float cursor = RowStartsAt;
                foreach (var (prefab, name) in Fleet)
                {
                    try
                    {
                        var vehicle = Build(prefab, name, fleet, graph, wheelGraph, wheelRequired, report);
                        // (turned to face away, the vehicle's own +x points towards the runway side)
                        float side = cursor + vehicle.bounds.size.x * 0.5f + vehicle.bounds.center.x;
                        cursor += vehicle.bounds.size.x + GapBetweenVehicles;
                        Park(vehicle, signs, spawn != null ? spawn.transform : fleet.transform,
                            origin + facing * new Vector3(side, 0f, -RowBehind), away, ground, required, report);
                    }
                    catch (Exception e)
                    {
                        failed++;
                        report.AppendLine($"NOT BUILT {name} ({prefab}): {e.Message}");
                        Debug.LogException(e);
                        foreach (Transform stray in fleet.transform)
                            if (stray.name == name || stray.name == prefab) { UnityEngine.Object.DestroyImmediate(stray.gameObject); break; }
                    }
                }
                report.AppendLine($"{FleetParts.RegisterWithWatch()} vehicles on the fleet watch");
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                if (SaccMaterials.Unconverted.Count > 0)
                    report.AppendLine("shaders with no stand-in yet (their renderers are off): " + string.Join(", ", SaccMaterials.Unconverted));
                report.AppendLine($"{Fleet.Length - failed} of {Fleet.Length} ground vehicles built");
                report.AppendLine("OK");
            }
            catch (Exception e)
            {
                report.AppendLine("FAILED: " + e);
                Debug.LogException(e);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, report.ToString());
            Debug.Log("[SaccGroundBuilder] " + report);
        }

        /// <summary>A vehicle assembled at the origin, waiting to be parked.</summary>
        sealed class Built
        {
            public string name, summary;
            public GameObject root;
            public Variables variables;
            public Bounds bounds;           // its solid parts, in its own axes
            public float lowest;            // where the ground is under its wheels at rest, relative to its origin
        }

        /// <summary>Everything a wheel's script knows, read before the script is removed.</summary>
        sealed class WheelData
        {
            public GameObject go;
            public Transform point, visual, visualGround;
            public SaccWheel rotationSource;
            public Dictionary<string, float> values = new Dictionary<string, float>();
            public bool separateGrip, drive, steer;
            public AnimationCurve gripCurve, gripCurveLateral;
            public float surfaceGrip = 1f, surfaceSlowdown = 0.1f;
            public float brakeMulti, handBrakeMulti;
            public SaccWheel source;
            public ParticleSystem[] skidParticles;
            public AudioSource[] skidSounds;
            public float skidAmount = 0.3f;
        }

        static Built Build(string prefabName, string name, GameObject fleet, ScriptGraphAsset graph, ScriptGraphAsset wheelGraph,
            IEnumerable<string> wheelRequired, StringBuilder report)
        {
            string prefabPath = $"{PrefabDir}/{prefabName}.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath)
                         ?? throw new Exception($"prefab missing: {prefabPath} (run Tools/sacc_import/import_sacc.py)");
            EnsureMeshLods(prefab);

            // ---- a loose copy of the prefab; before it is unpacked, find each script's entry in the prefab file --
            var holder = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var yaml = new PrefabYaml(prefabPath);
            var entries = new Dictionary<Component, string>();
            foreach (var script in holder.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m is SaccWheel || m is SaccGroundVehicle || m is SGV_GearBox))
                entries[script] = yaml.Entry(PrefabUtility.GetCorrespondingObjectFromSource(script));
            PrefabUtility.UnpackPrefabInstance(holder, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            var entity = holder.GetComponentInChildren<SaccEntity>(true) ?? throw new Exception($"{prefabName}: no SaccEntity");
            var ground = holder.GetComponentInChildren<SaccGroundVehicle>(true) ?? throw new Exception($"{prefabName}: not a ground vehicle");
            var root = entity.gameObject;
            root.transform.SetParent(fleet.transform, true);
            if (holder != root) UnityEngine.Object.DestroyImmediate(holder);
            root.name = name;
            root.SetActive(true);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);      // built at the origin, parked at the end
            root.transform.localScale = Vector3.one;

            // ---- everything the prefab's scripts know, read before they are removed --------------------------
            var body = root.GetComponent<Rigidbody>() ?? throw new Exception($"{prefabName}: no Rigidbody on the vehicle");
            var animator = root.GetComponent<Animator>() ?? throw new Exception($"{prefabName}: no Animator on the vehicle");
            var mesh = ground.VehicleMesh != null ? ground.VehicleMesh.gameObject : throw new Exception($"{prefabName}: no VehicleMesh");
            var centreOfMass = entity.CenterOfMass != null ? entity.CenterOfMass : root.transform;
            var seats = root.GetComponentsInChildren<SaccVehicleSeat>(true)
                .Select(s => (go: s.gameObject, pilot: s.IsPilotSeat, eye: s.TargetEyePosition, enable: s.EnableInSeat ?? new GameObject[0])).ToList();
            if (!seats.Any(s => s.pilot)) throw new Exception($"{prefabName}: no driver's seat");
            // gunner seats (SaccGunnerSeats.cs): read now, before the gunner dial objects are stripped
            var gunners = SaccFleetBuilder.ReadGunners(root, name, report);
            // the driver's horn, lights, boost, jump and air control (GroundGraphsExtras.cs), before the dial is stripped
            var extras = ReadDriverExtras(root, entity, report, name);
            var inside = (entity.EnableInVehicle ?? new GameObject[0]).Concat(seats.Where(s => s.pilot).SelectMany(s => s.enable)).Where(o => o).Distinct().ToList();
            bool tankMode = ground.TankMode;

            var effects = root.GetComponentInChildren<SGV_EffectsController>(true);
            var effectsObject = effects != null ? effects.gameObject : null;
            var engineSounds = effects != null && effects.EngineSounds != null ? effects.EngineSounds.Where(a => a).ToList() : new List<AudioSource>();
            var bang = effects != null && effects.Explosion != null ? effects.Explosion.FirstOrDefault(a => a) : null;
            AudioSource First(AudioSource[] sources) => sources != null ? sources.FirstOrDefault(a => a) : null;
            var smallCrash = effects != null ? First(effects.SmallCrash) : null;
            var mediumCrash = effects != null ? First(effects.MediumCrash) : null;
            var bigCrash = effects != null ? First(effects.BigCrash) : null;
            bool hasTracks = effects != null && effects.DoCaterpillarTracks && effects.TracksRenderer != null &&
                             effects.TrackMaterialSlots != null && effects.TrackMaterialSlots.Length >= 2 &&
                             effects.TrackSourceWheels != null && effects.TrackSourceWheels.Length >= 2 &&
                             effects.TrackSourceWheels[0] != null && effects.TrackSourceWheels[1] != null;
            var tracksRenderer = hasTracks ? effects.TracksRenderer : null;
            int trackSlotL = hasTracks ? effects.TrackMaterialSlots[0] : 0, trackSlotR = hasTracks ? effects.TrackMaterialSlots[1] : 0;
            var trackWheelL = hasTracks ? effects.TrackSourceWheels[0].gameObject : null;
            var trackWheelR = hasTracks ? effects.TrackSourceWheels[1].gameObject : null;
            var trackUv = hasTracks && effects.TrackSpeedMulti != null && effects.TrackSpeedMulti.Length > 0 ? effects.TrackSpeedMulti[0] : new Vector2(0f, 0.01f);
            var cogsL = hasTracks && effects.CogWheelsTrack0 != null ? effects.CogWheelsTrack0.Where(t => t).ToList() : new List<Transform>();
            var cogsR = hasTracks && effects.CogWheelsTrack1 != null ? effects.CogWheelsTrack1.Where(t => t).ToList() : new List<Transform>();
            float cogSpeed = hasTracks && effects.CogWheelsTrack0_rotSpeeds != null && effects.CogWheelsTrack0_rotSpeeds.Length > 0 ? effects.CogWheelsTrack0_rotSpeeds[0] : 1f;
            var trackSound = effects != null ? effects.TrackSound : null;
            float trackVol = effects != null ? effects.TrackSoundVolMulti : 0f, trackPitch = effects != null ? effects.TrackSoundPitchMulti : 0f;
            float trackMaxVol = effects != null ? effects.TrackSoundMaxVol : 1f, trackMaxPitch = effects != null ? effects.TrackSoundMaxPitch : 1f;
            var gauges = root.GetComponentInChildren<SGV_Gauges>(true);
            var speedText = gauges != null ? gauges.Speed_txt : null;
            var gearText = gauges != null ? gauges.Gear_txt : null;
            float speedMultiplier = gauges != null && gauges.SpeedMultiplier > 0f ? gauges.SpeedMultiplier : 3.6f;

            var gearBox = root.GetComponentInChildren<SGV_GearBox>(true);
            float[] gearRatios = (gearBox != null ? PrefabYaml.Floats(entries[gearBox], "GearRatios") : null)
                                 ?? new[] { -.04f, 0f, .04f, .08f, .12f, .16f, .2f };           // the script's own default
            if (gearRatios.Length == 0) throw new Exception($"{prefabName}: no gear ratios");
            // (with no neutral among the ratios the original starts in its first gear: the tank has one gear only)
            int neutral = Mathf.Max(Array.FindIndex(gearRatios, r => r == 0f), 0);
            var responseCurve = PrefabYaml.Curve(entries[ground], "EngineResponseCurve") ?? AnimationCurve.Linear(0f, 1f, 1f, 1f);

            var brakes = root.GetComponentsInChildren<DFUNC_CarBrake>(true);
            var footBrake = brakes.FirstOrDefault(b => !b.IsHandBrake);
            var handBrake = brakes.FirstOrDefault(b => b.IsHandBrake);

            var drive = new HashSet<MonoBehaviour>(ground.DriveWheels ?? new MonoBehaviour[0]);
            var steer = new HashSet<MonoBehaviour>(ground.SteerWheels ?? new MonoBehaviour[0]);
            int numWheels = (ground.DriveWheels?.Length ?? 0) + (ground.SteerWheels?.Length ?? 0) + (ground.OtherWheels?.Length ?? 0);
            var wheels = new List<WheelData>();
            foreach (var wheel in root.GetComponentsInChildren<SaccWheel>(true))
            {
                var data = new WheelData
                {
                    source = wheel, go = wheel.gameObject,
                    point = wheel.WheelPoint != null ? wheel.WheelPoint : wheel.transform,
                    visual = wheel.WheelVisual, visualGround = wheel.WheelVisual_Ground, rotationSource = wheel.WheelVisual_RotationSource,
                    separateGrip = wheel.SeparateLongLatGrip, drive = drive.Contains(wheel), steer = steer.Contains(wheel),
                    gripCurve = PrefabYaml.Curve(entries[wheel], "GripCurve") ?? AnimationCurve.Linear(0f, 1f, 1f, 1f),
                    gripCurveLateral = PrefabYaml.Curve(entries[wheel], "GripCurveLateral") ?? AnimationCurve.Linear(0f, 1f, 1f, 1f),
                    skidParticles = wheel.SurfaceType_SkidParticles ?? new ParticleSystem[0],
                    skidSounds = wheel.SurfaceType_SkidSounds ?? new AudioSource[0],
                };
                if (data.visual == null) throw new Exception($"{prefabName}: wheel {wheel.name} has no WheelVisual");
                // (surface type 0, the default: this world's ground is not tagged with the original's surface numbers)
                var grips = PrefabYaml.Floats(entries[wheel], "SurfaceType_Grips");
                var slows = PrefabYaml.Floats(entries[wheel], "SurfaceType_Slowdown");
                if (grips != null && grips.Length > 0) data.surfaceGrip = grips[0];
                if (slows != null && slows.Length > 0) data.surfaceSlowdown = slows[0];
                var amounts = PrefabYaml.Floats(entries[wheel], "SurfaceType_SkidParticles_Amount");
                if (amounts != null && amounts.Length > 0) data.skidAmount = amounts[0];
                foreach (var field in typeof(SaccWheel).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (field.GetValue(wheel) is float value) data.values[field.Name] = value;
                if (footBrake != null)
                {
                    bool front = footBrake.BrakeWheels_Front != null && footBrake.BrakeWheels_Front.Contains(wheel);
                    bool back = footBrake.BrakeWheels_Back != null && footBrake.BrakeWheels_Back.Contains(wheel);
                    data.brakeMulti = front ? footBrake.Brake_FrontStrengthMulti : back ? footBrake.Brake_BackStrengthMulti : 0f;
                    if (front || back) data.values["BrakeStrength"] = footBrake.BrakeStrength;     // the brake sets its wheels' strength when it starts
                }
                if (handBrake != null)
                {
                    bool front = handBrake.BrakeWheels_Front != null && handBrake.BrakeWheels_Front.Contains(wheel);
                    bool back = handBrake.BrakeWheels_Back != null && handBrake.BrakeWheels_Back.Contains(wheel);
                    data.handBrakeMulti = front ? handBrake.Brake_FrontStrengthMulti : back ? handBrake.Brake_BackStrengthMulti : 0f;
                }
                wheels.Add(data);
            }
            if (wheels.Count == 0) throw new Exception($"{prefabName}: no wheels");

            var scripted = new HashSet<AudioSource>();
            foreach (var stub in root.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().Namespace == "SaccFlightAndVehicles"))
                foreach (var field in stub.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.Name == "DopplerSounds") continue;     // only re-pitched; the animator starts them (guns)
                    if (field.FieldType == typeof(AudioSource) && field.GetValue(stub) is AudioSource one && one) scripted.Add(one);
                    else if (field.FieldType == typeof(AudioSource[]) && field.GetValue(stub) is AudioSource[] many)
                        foreach (var item in many) if (item) scripted.Add(item);
                }

            var tuning = new Dictionary<string, object>();
            foreach (var (owner, type) in new (object, Type)[] { (ground, typeof(SaccGroundVehicle)), (gearBox, typeof(SGV_GearBox)) })
            {
                if (owner == null) continue;
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    var value = field.GetValue(owner);
                    if (value is float f) tuning[field.Name] = f;
                    else if (value is int i) tuning[field.Name] = (float)i;
                    else if (value is bool b) tuning[field.Name] = b;
                }
            }
            float fullHealth = ground.Health, crashDamage = ground.Crash_Damage_Speed, crashDeath = ground.Crash_Death_Speed;
            float clutchLength = gearBox != null ? gearBox.AutoClutch_Length : 0.5f;
            float clutchStay = gearBox != null ? gearBox.AutoClutch_StayPressed : 0f;
            float finalDrive = gearBox != null ? gearBox.FinalDrive : 1f;
            bool handBrakeAutoClutch = handBrake != null && handBrake.AutoClutch, footBrakeAutoClutch = footBrake != null && footBrake.AutoClutch;
            bool parkBrake = handBrake != null && handBrake.EnableBrakeOnExit;
            string brakeNote = (footBrake != null ? $"foot brake strength {footBrake.BrakeStrength}" : "no foot brake") + ", " +
                               (handBrake != null ? $"handbrake{(parkBrake ? " (on when empty)" : "")}{(handBrakeAutoClutch ? " with clutch" : "")}" : "no handbrake");
            float explodeLength = ExplodeLength(animator);

            // ---- strip: stand-ins, VRChat components, parts not carried over --------------------------------
            foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(t => t != null && (Dropped.Contains(t.name) || t.name.StartsWith("RacingTrigger"))).ToArray())
                if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
            int notInBuilds = FleetParts.RemoveStrippedFromBuilds(root);
            foreach (var extra in root.GetComponentsInChildren<Rigidbody>(true).Where(r => r != body).ToArray())
                UnityEngine.Object.DestroyImmediate(extra.gameObject);
            foreach (var camera in root.GetComponentsInChildren<Camera>(true).ToArray())
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            foreach (var stub in root.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().Namespace == "SaccFlightAndVehicles").ToArray())
                UnityEngine.Object.DestroyImmediate(stub);
            int gone = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                gone += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                t.gameObject.layer = 0;
            }
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true).ToArray()) UnityEngine.Object.DestroyImmediate(group);
            foreach (var raycaster in root.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(true).ToArray()) UnityEngine.Object.DestroyImmediate(raycaster);
            foreach (var trigger in root.GetComponentsInChildren<Collider>(true).Where(c => c.isTrigger && !(c is WheelCollider)).ToArray())
                UnityEngine.Object.DestroyImmediate(trigger);                 // interaction volumes of the original scripts
            wheels.RemoveAll(w => w.go == null);
            if (wheels.Count == 0) throw new Exception($"{prefabName}: its wheels were inside a part that is not carried over");

            // ---- screens that showed a camera's picture have nothing to show: the cameras are not carried over ----
            int screensOff = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                if (renderer.sharedMaterials.Any(m => m != null && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") is RenderTexture))
                {
                    renderer.gameObject.SetActive(false);
                    screensOff++;
                }

            // ---- materials -----------------------------------------------------------------------------------
            int hidden = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                bool particles = renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer;
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => SaccMaterials.Convert(m, particles)).ToArray();
                if (renderer is ParticleSystemRenderer psr && psr.trailMaterial != null) psr.trailMaterial = SaccMaterials.Convert(psr.trailMaterial, true);
                if (renderer.sharedMaterials.Any(m => m != null && !SaccMaterials.Usable(m)) && !renderer.TryGetComponent<TMPro.TMP_Text>(out _))
                {
                    renderer.enabled = false;
                    hidden++;
                }
            }
            foreach (var graphic in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                if (graphic.material != null && graphic.material != graphic.defaultMaterial)
                    graphic.material = SaccMaterials.Convert(graphic.material, false);

            // ---- body: pivot on the centre of mass (SaccGroundVehicle.SetCoMMeshOffset) ------------------------
            var offset = centreOfMass.position - root.transform.position;
            foreach (Transform child in root.transform) child.position -= offset;
            body.automaticCenterOfMass = false;
            body.centerOfMass = Vector3.zero;
            body.automaticInertiaTensor = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.isKinematic = false;
            body.constraints = RigidbodyConstraints.None;        // the prefabs are authored frozen; their scripts release them

            // ---- effects and sound -----------------------------------------------------------------------------
            // Left as authored, except: sounds the original scripts start and stop themselves stay silent, tyre
            // smoke does not start by itself, and the engine note plays whenever the graph switches its object on
            // (its volume and pitch come from the vehicle's animator).
            foreach (var source in scripted) if (source) source.playOnAwake = false;
            foreach (var smoke in wheels.SelectMany(w => w.skidParticles).Where(p => p))
            {
                var emission = smoke.emission;
                emission.enabled = false;
            }
            foreach (var squeal in wheels.SelectMany(w => w.skidSounds).Where(a => a))
            {
                squeal.loop = true;
                squeal.playOnAwake = true;
                squeal.volume = 0f;
                squeal.gameObject.SetActive(false);
            }
            var engineList = new AotList();
            foreach (var engine in engineSounds)
            {
                engine.loop = true;
                engine.playOnAwake = true;
                engine.enabled = true;
                engine.gameObject.SetActive(false);
                engineList.Add(engine);
            }
            if (trackSound != null)
            {
                trackSound.loop = true;
                trackSound.playOnAwake = true;
                trackSound.volume = 0f;
                trackSound.gameObject.SetActive(false);
            }
            if (bang == null)
            {
                bang = FleetParts.Child(root, "ExplosionSound").AddComponent<AudioSource>();
                bang.clip = FleetParts.Clip("explosion");
                bang.spatialBlend = 1f;
                bang.minDistance = 30f;
                bang.maxDistance = 3000f;
            }
            bang.playOnAwake = false;
            bang.loop = false;

            // ---- seats ---------------------------------------------------------------------------------------
            GameObject pilotSeat = null;
            Transform pilotEye = null;
            int headsOut = 0;
            var seatList = new AotList();
            var passengerSeatColliders = new AotList();
            foreach (var (go, pilot, eye, _) in seats)
            {
                if (go == null) continue;
                // The eye target stays fixed in the vehicle; the seat object is what gets slid to fit a driver.
                var eyePoint = eye != null ? eye : FleetParts.Child(go, "TargetEyePosition").transform;
                if (eye == null) eyePoint.localPosition = new Vector3(0f, 0.75f, 0f);
                eyePoint.SetParent(root.transform, true);
                eyePoint.name = "EyePoint " + go.name;
                if (tankMode)
                {
                    // A tank's crew sit inside the hull and see out through camera screens, which are not carried
                    // over: seat them with their heads out of the top instead, just above whatever is over them.
                    UnityEngine.Physics.SyncTransforms();
                    var above = eyePoint.position + Vector3.up * 6f;
                    var roofs = UnityEngine.Physics.RaycastAll(above, Vector3.down, 9f, UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                        .Where(h => h.collider.attachedRigidbody == body).ToArray();
                    if (roofs.Length > 0)
                    {
                        eyePoint.position = new Vector3(eyePoint.position.x, roofs.Max(h => h.point.y) + 0.32f, eyePoint.position.z);
                        headsOut++;
                    }
                }
                var exit = go.transform.Find("Exit");                    // where the original lets its riders out
                Vector3 exitPoint = exit != null ? exit.position : go.transform.position + root.transform.right * 2f;
                // The seat keeps the way the original faces (the truck bed's benches face inwards), stood upright,
                // and goes where the app will put the sitter's eyes on the eye point (FleetParts.SeatFor).
                var facing = FleetParts.UprightFacing(go.transform, root.transform);
                go.transform.position = FleetParts.SeatFor(eyePoint.position, root.transform.up, facing * Vector3.forward);
                go.transform.rotation = facing;
                go.layer = 5;                                            // UI layer: the only layers that can be clicked
                foreach (var old in go.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(old);
                var click = go.AddComponent<BoxCollider>();
                click.isTrigger = true;
                click.size = new Vector3(0.6f, 1.2f, 0.9f);              // (seats sit side by side in a car: no wider than a seat)
                click.center = FleetParts.SeatClickCentre;
                var seatComponent = go.AddComponent(GraphTypes.Find("BS.BSSeat"));
                FleetParts.SetSerialized(seatComponent, report, ("unseatOnMove", false), ("unseatOnJump", true), ("viewMode", 0),
                    ("useExitPosition", true), ("exitPosition", go.transform.InverseTransformPoint(exitPoint)));
                FleetParts.SetSerialized(go.GetComponent(GraphTypes.Find("BS.BSPointerRange")), report, ("distance", 5f));
                seatList.Add(seatComponent);
                if (pilot && pilotSeat == null)
                {
                    pilotSeat = go;
                    pilotEye = eyePoint;
                }
                else passengerSeatColliders.Add(click);
            }
            if (pilotSeat == null) throw new Exception($"{prefabName}: the driver's seat was inside a part that is not carried over");
            // Each seat onto its own cushion (with its eye point), and its rider's feet on the floor (SeatLegs).
            SeatLegs.Fit(root, true);

            // ---- platform components ------------------------------------------------------------------------
            var sync = root.AddComponent(GraphTypes.Find("BS.BSSyncedObject"));
            FleetParts.SetSerialized(sync, report, ("takeOwnershipOnCollision", false), ("takeOwnershipOnProximity", false), ("takeOwnershipOnGrab", false));
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;    // the animator turns the steered wheels: physics depends on it
            animator.keepAnimatorStateOnDisable = true;                  // the graph switches it off while the vehicle rests

            // ---- its size, and where the ground is under its wheels at rest ---------------------------------
            UnityEngine.Physics.SyncTransforms();        // collider bounds are only as fresh as the physics scene
            var solids = root.GetComponentsInChildren<Collider>(false).Where(c => c.enabled && !c.isTrigger && !(c is WheelCollider)).ToList();
            if (solids.Count == 0) throw new Exception($"{prefabName}: no solid colliders");
            var bounds = solids[0].bounds;
            foreach (var collider in solids) bounds.Encapsulate(collider.bounds);
            // Standing still, the springs together push up as hard as gravity pulls down.
            float restCompression = Mathf.Clamp01(9.81f / wheels.Sum(w => w.values["SpringForceMulti"]));
            float lowest = wheels.Min(w => w.point.position.y - (1f - restCompression) * w.values["SuspensionDistance"]);
            float clearance = bounds.min.y - lowest;

            // ---- wheels: each gets the wheel graph and its own settings --------------------------------------
            var spares = FleetParts.Child(root, "Unused");
            spares.SetActive(false);
            var spareTransform = FleetParts.Child(spares, "NoTransform").transform;
            var byScript = wheels.ToDictionary(w => w.source, w => w);
            var wheelMachines = new AotList();
            foreach (var wheel in wheels)
            {
                var v = wheel.go.AddComponent<Variables>().declarations;
                foreach (var key in new[] { "ExtraRayCastDistance", "SuspensionDistance", "WheelRadius", "MaxParkingIncline", "Damping_Bump", "Damping_Rebound",
                                            "SpringForceMulti", "MaxSusForce", "ClutchStrength", "LateralGrip", "WheelRollGrip_Power", "LongLatSeparation",
                                            "GripGain", "Brake_EngineWeight", "BrakeStrength", "EngineBrakeStrength", "EngineInfluence" })
                    v.Set(key, wheel.values.TryGetValue(key, out var value) ? value : throw new Exception($"{prefabName}: wheel setting '{key}' not found"));
                v.Set("WheelCircumference", wheel.values["WheelRadius"] * 2f * Mathf.PI);
                v.Set("WheelRPS", 0f);
                foreach (var key in new[] { "SkidSound_Min", "SkidSound_VolumeIncrease", "SkidSound_PitchIncrease", "SkidSound_Pitch" })
                    v.Set(key, wheel.values.TryGetValue(key, out var skidValue) ? skidValue : 0f);
                // (whoever runs the wheels is normally sitting in the vehicle: the original turns the squeal down for them)
                v.Set("SkidVolume", wheel.values.TryGetValue("SkidVolInVehicleMulti", out var inVehicle) ? inVehicle : 1f);
                v.Set("SkidParticleAmount", wheel.skidAmount);
                var squealSource = wheel.skidSounds.FirstOrDefault(a => a);
                if (squealSource == null)
                {
                    squealSource = FleetParts.Child(spares, "NoSkidSound " + wheel.go.name).AddComponent<AudioSource>();
                    squealSource.playOnAwake = false;
                }
                var smokeSystem = wheel.skidParticles.FirstOrDefault(p => p);
                if (smokeSystem == null)
                {
                    smokeSystem = FleetParts.Child(spares, "NoSkidSmoke " + wheel.go.name).AddComponent<ParticleSystem>();
                    var none = smokeSystem.emission;
                    none.enabled = false;
                }
                v.Set("SkidSound", squealSource);
                v.Set("SkidParticle", smokeSystem);
                v.Set("CurrentGrip", wheel.values["Grip"] * wheel.surfaceGrip);
                v.Set("WheelSlowDown", wheel.surfaceSlowdown * 0.01f);
                v.Set("NumWheels", (float)numWheels);
                v.Set("BrakeMulti", wheel.brakeMulti);
                v.Set("HandBrakeMulti", wheel.handBrakeMulti);
                v.Set("IsDriveWheel", wheel.drive || (tankMode && wheel.steer));      // a tank's "steer wheels" are its right track
                v.Set("RightTrack", tankMode && wheel.steer);
                v.Set("TankMode", tankMode);
                v.Set("SeparateLongLatGrip", wheel.separateGrip);
                v.Set("GripCurve", wheel.gripCurve);
                v.Set("GripCurveLateral", wheel.gripCurveLateral);
                v.Set("Vehicle", root);
                v.Set("Body", body);
                v.Set("WheelPoint", wheel.point);
                v.Set("WheelVisual", wheel.visual);
                v.Set("HasVisualGround", wheel.visualGround != null);
                v.Set("WheelVisualGround", wheel.visualGround != null ? wheel.visualGround : spareTransform);
                var rotationSource = wheel.rotationSource != null && byScript.TryGetValue(wheel.rotationSource, out var other) ? other.visual : null;
                v.Set("HasRotationSource", rotationSource != null);
                v.Set("RotationSourceVisual", rotationSource != null ? rotationSource : spareTransform);
                var missingOnWheel = wheelRequired.Where(needed => !v.IsDefined(needed)).ToList();
                if (missingOnWheel.Count > 0) throw new Exception($"{name}: wheel {wheel.go.name} is missing variables the wheel graph reads: " + string.Join(", ", missingOnWheel));
                var wheelMachine = wheel.go.AddComponent<ScriptMachine>();
                wheelMachine.nest.SwitchToMacro(wheelGraph);
                wheelMachine.enabled = false;            // the vehicle's graph switches the wheels on where and while it is simulated
                wheelMachines.Add(wheelMachine);
            }

            // ---- tuning and references read by the vehicle graph ---------------------------------------------
            float Value(string key, float fallback = 0f) => tuning.TryGetValue(key, out var number) && number is float f ? f : fallback;
            bool Flag(string key) => tuning.TryGetValue(key, out var flag) && flag is bool on && on;
            float decay = clutchLength > 0f ? 1f / clutchLength : 1000f;
            var variables = root.AddComponent<Variables>();
            foreach (var pair in tuning) variables.declarations.Set(pair.Key, pair.Value);
            foreach (var key in new[] { "TankMode", "SteeringMaxSpeedDTDisabled", "Drift_AutoSteer" }) variables.declarations.Set(key, Flag(key));
            foreach (var (key, value) in new (string, float)[]
                     {
                         ("FullHealth", fullHealth), ("CrashDamageSpeed", crashDamage), ("CrashDeathSpeed", crashDeath),
                         // the vehicle's own "explode" animation hides it and brings it back by itself: come home when it ends
                         ("RespawnDelay", explodeLength > 1f ? explodeLength : 5f),
                         ("AboardRadius", Mathf.Max(bounds.extents.x, bounds.extents.z) + 3f), ("SoundRefSpeed", 40f),
                         ("NeutralGear", neutral), ("TopGear", gearRatios.Length - 1), ("FinalDrive", finalDrive),
                         ("NeutralRatio", gearRatios[neutral] * finalDrive),
                         ("Wheelbase", Mathf.Max(wheels.Max(w => w.point.position.z) - wheels.Min(w => w.point.position.z), 0.5f)),
                         ("SteerAssistAccel", 0.5f * 9.81f * wheels.Average(w => w.values["Grip"] * w.surfaceGrip)),
                         ("DriveCircumference", wheels.Where(w => w.drive || (tankMode && w.steer)).Select(w => w.values["WheelRadius"] * 2f * Mathf.PI).DefaultIfEmpty(2f).Average()),
                         ("ClutchDecaySpeed", decay), ("AutoClutchStart", 1f + decay * clutchStay),
                         ("SpeedMultiplier", speedMultiplier), ("ParkBrake", parkBrake ? 1f : 0f),
                         ("TANK_ReverseSpeed", Value("TANK_ReverseSpeed", 0.75f)), ("SteeringDegrees", Value("SteeringDegrees", 60f)),
                         ("SteeringMaxSpeedDT", Mathf.Max(Value("SteeringMaxSpeedDT", 40f), 0.01f)), ("DesktopMinSteering", Value("DesktopMinSteering", 0.2f)),
                         ("SteeringKeyboardSecsToMax", Mathf.Max(Value("SteeringKeyboardSecsToMax", 0.5f), 0.01f)),
                         ("SteeringReturnSpeedDT", Mathf.Max(Value("SteeringReturnSpeedDT", 0.5f), 0.01f)), ("AutoSteerStrength", Value("AutoSteerStrength", 5f)),
                         ("RevLimiter", Mathf.Max(Value("RevLimiter", 8000f), 1f)), ("AutomaticGearChangeDelay", Value("AutomaticGearChangeDelay", 0.25f)),
                         ("GearChangeRevsUpper", Value("GearChangeRevsUpper", 0.9f)), ("GearChangeRevsLower", Value("GearChangeRevsLower", 0.4f)),
                         ("GearChangeRevsUpperNeutral", Value("GearChangeRevsUpperNeutral", 0.42f)),
                         ("MinThrottle", Value("MinThrottle", 0.08f)), ("MinThrottle_PStrength", Value("MinThrottle_PStrength", 2f)),
                         ("EngineSlowDown", Value("EngineSlowDown", 1f)), ("DriveSpeed", Value("DriveSpeed")), ("RevLimiterDelay", Value("RevLimiterDelay", 0.04f)),
                         ("Drag", Value("Drag", 0.02f)),
                         // shared with the wheels while running
                         ("VehicleSpeed", 0f), ("EngineForceUsed", 0f), ("Revs", 0f), ("GearRatioL", 0f), ("GearRatioR", 0f), ("Clutch", 0f),
                         ("BrakeInput", 0f), ("HandBrakeInput", parkBrake ? 1f : 0f),
                     })
                variables.declarations.Set(key, value);
            AudioSource SpareSound(AudioSource source, string label)
            {
                if (source != null) return source;
                var spare = FleetParts.Child(spares, label).AddComponent<AudioSource>();
                spare.playOnAwake = false;
                return spare;
            }
            variables.declarations.Set("SmallCrashSound", SpareSound(smallCrash, "NoSmallCrash"));
            variables.declarations.Set("MediumCrashSound", SpareSound(mediumCrash, "NoMediumCrash"));
            variables.declarations.Set("BigCrashSound", SpareSound(bigCrash, "NoBigCrash"));
            foreach (var (key, value) in new (string, float)[]
                     {
                         ("SmallCrashSpeed", Value("SmallCrashSpeed", 1f)), ("MediumCrashSpeed", Value("MediumCrashSpeed", 8f)), ("BigCrashSpeed", Value("BigCrashSpeed", 25f)),
                         ("TrackSlotL", trackSlotL), ("TrackSlotR", trackSlotR), ("TrackUvX", trackUv.x), ("TrackUvY", trackUv.y), ("CogSpeed", cogSpeed),
                         ("TrackCircumference", wheels.Select(w => w.values["WheelRadius"] * 2f * Mathf.PI).Average()),
                         ("TrackSoundVolMulti", trackVol), ("TrackSoundPitchMulti", trackPitch), ("TrackSoundMaxVol", trackMaxVol), ("TrackSoundMaxPitch", trackMaxPitch),
                     })
                variables.declarations.Set(key, value);
            hasTracks = hasTracks && tracksRenderer != null && trackWheelL != null && trackWheelR != null;
            variables.declarations.Set("HasTracks", hasTracks);
            variables.declarations.Set("TracksRenderer", hasTracks ? tracksRenderer : (Renderer)root.GetComponentInChildren<Renderer>(true));
            variables.declarations.Set("TrackWheelL", hasTracks ? trackWheelL : root);
            variables.declarations.Set("TrackWheelR", hasTracks ? trackWheelR : root);
            var cogListL = new AotList();
            foreach (var cog in cogsL) if (cog) cogListL.Add(cog);
            var cogListR = new AotList();
            foreach (var cog in cogsR) if (cog) cogListR.Add(cog);
            variables.declarations.Set("CogsL", cogListL);
            variables.declarations.Set("CogsR", cogListR);
            variables.declarations.Set("TrackSound", SpareSound(trackSound, "NoTrackSound"));
            variables.declarations.Set("WheelMachines", wheelMachines);
            // Detail by distance: the renderers whose meshes have automatic LODs worth switching.
            var lodRenderers = new AotList();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var drawn = renderer is SkinnedMeshRenderer skinnedMesh ? skinnedMesh.sharedMesh
                    : renderer.TryGetComponent<MeshFilter>(out var meshFilter) ? meshFilter.sharedMesh : null;
                if (drawn == null || drawn.lodCount <= 1) continue;
                long indices = 0;
                for (int sub = 0; sub < drawn.subMeshCount; sub++) indices += drawn.GetIndexCount(sub);
                if (indices / 3 >= 300) lodRenderers.Add(renderer);
            }
            variables.declarations.Set("LodRenderers", lodRenderers);
            variables.declarations.Set("LodNear", Mathf.Clamp(bounds.size.magnitude * 0.6f, 10f, 25f));
            // What rests with the animator on a parked vehicle: its effects (particle systems) and its sounds.
            var resting = new AotList();
            if (effectsObject != null && effectsObject != root && !mesh.transform.IsChildOf(effectsObject.transform) &&
                !pilotSeat.transform.IsChildOf(effectsObject.transform))
                resting.Add(effectsObject);
            variables.declarations.Set("RestingObjects", resting);
            variables.declarations.Set("Simulate", false);
            variables.declarations.Set("RemoteDriven", false);
            variables.declarations.Set("RemoteSpeed", 0f);
            variables.declarations.Set("HandBrakeAutoClutch", handBrakeAutoClutch);
            variables.declarations.Set("BrakeAutoClutch", footBrakeAutoClutch);
            var ratioList = new AotList();
            foreach (float ratio in gearRatios) ratioList.Add(ratio);
            variables.declarations.Set("GearRatios", ratioList);
            variables.declarations.Set("EngineResponseCurve", responseCurve);
            UnityEngine.UI.Text SpareText(UnityEngine.UI.Text t, string label) => t != null ? t : FleetParts.Child(spares, label).AddComponent<UnityEngine.UI.Text>();
            variables.declarations.Set("SpeedText", SpareText(speedText, "NoSpeedText"));
            variables.declarations.Set("GearText", SpareText(gearText, "NoGearText"));
            variables.declarations.Set("PilotSeat", pilotSeat);
            variables.declarations.Set("PilotSeatComponent", pilotSeat.GetComponent(GraphTypes.Find("BS.BSSeat")));
            variables.declarations.Set("PilotSeatCollider", pilotSeat.GetComponent<Collider>());
            variables.declarations.Set("EyePoint", pilotEye);
            variables.declarations.Set("Seats", seatList);
            variables.declarations.Set("PassengerSeatColliders", passengerSeatColliders);
            variables.declarations.Set("Model", mesh);
            variables.declarations.Set("BoomMessage", "boom:" + name);
            variables.declarations.Set("EngineSounds", engineList);
            variables.declarations.Set("ExplosionSound", bang);
            variables.declarations.Set("VehicleAnimator", animator);
            foreach (var pair in extras) variables.declarations.Set(pair.Key, pair.Value ?? (object)FleetParts.Child(root, "No" + pair.Key).transform);
            var insideList = new AotList();
            foreach (var go in inside) if (go) insideList.Add(go);
            variables.declarations.Set("InVehicleObjects", insideList);
            root.AddComponent<ScriptMachine>().nest.SwitchToMacro(graph);
            SaccFleetBuilder.BuildGunners(root, gunners, body, animator, report);
            FleetParts.WatchVariables(variables, root.GetComponent<ScriptMachine>(), bounds);

            int triangles = 0, lodMeshes = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled))
            {
                var drawn = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                    : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
                if (drawn == null) continue;
                for (int sub = 0; sub < drawn.subMeshCount; sub++) triangles += (int)(drawn.GetIndexCount(sub) / 3);
                if (drawn.lodCount > 1) lodMeshes++;
            }
            var culture = CultureInfo.InvariantCulture;
            report.AppendLine(string.Format(culture,
                "   [{0}] gears {1} (neutral is number {2}); engine curve {3} keys; wheels: {4}; rest compression {5:F2}, body clears the ground by {6:F2} m; " +
                "hull x {7:F1}..{8:F1}, y {9:F2}..{10:F2}, z {11:F1}..{12:F1}; explode animation {13:F1} s; engine sounds {14}; {15}",
                name, string.Join(" ", gearRatios.Select(r => r.ToString(culture))), neutral, responseCurve.length,
                string.Join(" ", wheels.Select(w => $"{w.go.name}({(w.drive ? "drive" : "")}{(w.steer ? (tankMode ? " right-track" : " steer") : "")} grip {w.values["Grip"] * w.surfaceGrip:F2} curve {w.gripCurve.length} keys r{w.values["WheelRadius"]:F2} travel {w.values["SuspensionDistance"]:F2} at {w.point.position.x:F2},{w.point.position.y:F2},{w.point.position.z:F2})")),
                restCompression, clearance, bounds.min.x, bounds.max.x, bounds.min.y, bounds.max.y, bounds.min.z, bounds.max.z, explodeLength, engineSounds.Count,
                brakeNote));
            return new Built
            {
                name = name, root = root, variables = variables, bounds = bounds, lowest = lowest,
                summary = $"{name}: {prefabName}, {(tankMode ? "tank (two tracks)" : "car")}, {body.mass} kg, {seatList.Count} seat(s), {wheels.Count} wheel(s), " +
                          $"{gearRatios.Length} gears, {root.GetComponentsInChildren<Renderer>(false).Count(r => r.enabled)} renderers drawing {triangles} triangles " +
                          $"({lodMeshes} meshes with automatic LODs), {hidden} renderers off (no shader yet), {gone} VRChat components removed, {notInBuilds} editor-only part(s) deleted" +
                          $"{(speedText != null ? ", own gauges" : ", no gauges")}" +
                          $"{(screensOff > 0 ? $", {screensOff} camera screen(s) switched off" : "")}" +
                          $"{(headsOut > 0 ? $", {headsOut} seat(s) raised to look out of the top" : "")}",
            };
        }

        /// <summary>Stand a built vehicle on its parking spot, with its reset sign behind it and stable network ids.</summary>
        static void Park(Built vehicle, GameObject signs, Transform respawnPoint, Vector3 parking, Quaternion facing, float ground,
            IEnumerable<string> required, StringBuilder report)
        {
            var root = vehicle.root;
            string name = vehicle.name;
            var position = new Vector3(parking.x, ground - vehicle.lowest + 0.02f + (Settles.TryGetValue(name, out float settle) ? settle : 0f), parking.z);
            // behind its tail and a little to its left: between the vehicle and the arrival point, facing the arrival point
            var sign = FleetParts.ResetSign(signs, name,
                position + facing * new Vector3(vehicle.bounds.min.x - 0.6f, 0f, vehicle.bounds.min.z - 1.6f), ground, facing);
            vehicle.variables.declarations.Set("RespawnPoint", respawnPoint);
            vehicle.variables.declarations.Set("ResetSign", sign);
            var missing = required.Where(v => !vehicle.variables.declarations.IsDefined(v)).ToList();
            if (missing.Count > 0) throw new Exception($"{name} is missing variables the graph reads: " + string.Join(", ", missing));
            root.transform.SetPositionAndRotation(position, facing);

            // ---- network ids that are the same in every build ------------------------------------------------
            var used = new HashSet<string>();
            foreach (var component in root.GetComponentsInChildren(GraphTypes.Find("BS.BSObjectId"), true))
            {
                var path = new List<string>();
                for (var t = component.transform; t != root.transform; t = t.parent) path.Insert(0, t.name);
                path.Insert(0, name);
                string id = "fw-" + string.Join("-", path).Replace(' ', '_');
                for (int n = 2; !used.Add(id); n++) id = id.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '~') + "~" + n;
                var so = new SerializedObject(component);
                so.FindProperty("Id").stringValue = id;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            if (used.Count == 0) throw new Exception($"{name}: no network id components found to stabilise");
            report.AppendLine($"{vehicle.summary}, {used.Count} network ids, at {position}");
        }

        /// <summary>How long the vehicle's own "Explode" animation runs before it puts everything back.</summary>
        /// <summary>
        /// The driver's own dial functions that GroundGraphsExtras.cs works, read from the vehicle's SaccEntity dials and
        /// extensions: horn, headlights, boost, jump and air control. Values for the vehicle's object variables.
        /// </summary>
        static Dictionary<string, object> ReadDriverExtras(GameObject root, SaccEntity entity, StringBuilder report, string name)
        {
            var all = (entity.Dial_Functions_L ?? new MonoBehaviour[0]).Concat(entity.Dial_Functions_R ?? new MonoBehaviour[0])
                .Concat(entity.ExtensionUdonBehaviours ?? new MonoBehaviour[0]).Where(m => m != null).ToList();
            var v = new Dictionary<string, object>();
            var found = new List<string>();
            var horn = all.OfType<DFUNC_Horn>().FirstOrDefault(h => h.Horn != null);
            v["HasHorn"] = horn != null;
            v["HornSound"] = horn != null ? horn.Horn : FleetParts.Child(root, "NoHorn").AddComponent<AudioSource>();
            if (horn != null) { horn.Horn.playOnAwake = false; found.Add("horn"); }
            // headlights: the first switch that switches objects (DFUNC_ToggleBool), as authored to start with
            var lights = all.OfType<DFUNC_ToggleBool>().FirstOrDefault(t => (t.ToggleObjects != null && t.ToggleObjects.Any(o => o)) || !string.IsNullOrEmpty(t.AnimBoolName));
            var lightObjects = new AotList();
            if (lights != null) foreach (var o in lights.ToggleObjects ?? new GameObject[0]) if (o) { lightObjects.Add(o); o.SetActive(lights.OnDefault); }
            v["HasLights"] = lights != null;
            v["LightObjects"] = lightObjects;
            v["LightBool"] = lights != null ? lights.AnimBoolName ?? "" : "";
            if (lights != null) found.Add(lights.gameObject.name.Replace("DFUNC_", "").ToLowerInvariant());
            // boost (DFUNC_CarBoost): a push of BoostForce (force type) or of the boost's extra engine power, as an
            // acceleration along its point (or the vehicle), for BoostInSeconds, filling again over ResupplyTime
            var boost = all.OfType<DFUNC_CarBoost>().FirstOrDefault();
            v["HasBoost"] = boost != null;
            v["BoostPoint"] = boost != null && boost.BoostPoint != null ? boost.BoostPoint : root.transform;
            v["BoostAccel"] = boost == null ? 0f : boost.BoostType_Force ? boost.BoostForce : 8f;
            v["BoostSeconds"] = boost != null ? Mathf.Clamp(boost.BoostInSeconds, 1f, 60f) : 10f;
            v["BoostRefill"] = boost != null ? Mathf.Max(boost.ResupplyTime, 1f) : 10f;
            v["BoostFloat"] = boost != null ? boost._BoostingAnimFloatName ?? "" : "";
            if (boost != null) found.Add("boost");
            // jump (DFUNC_CarJump)
            var jump = all.OfType<DFUNC_CarJump>().FirstOrDefault();
            v["HasJump"] = jump != null;
            v["JumpImpulse"] = jump != null ? jump.JumpForce : 0f;
            v["JumpHold"] = jump != null ? jump.HoldJumpForce : 0f;
            v["JumpSeconds"] = jump != null ? Mathf.Max(jump.MaxJumpLengthSecs, 0.05f) : 0.2f;
            v["JumpTrigger"] = jump != null ? jump.AnimatorTriggerName ?? "" : "";
            v["JumpSound"] = jump != null && jump.JumpSound != null ? jump.JumpSound : FleetParts.Child(root, "NoJumpSound").AddComponent<AudioSource>();
            if (jump != null) found.Add("jump");
            // air control (SGV_AirControl)
            var air = all.OfType<SGV_AirControl>().FirstOrDefault();
            v["HasAirControl"] = air != null;
            v["AirPitch"] = air != null ? air.RotationStrengthPitch : 0f;
            v["AirRoll"] = air != null ? air.RotationStrengthRoll : 0f;
            v["AirYaw"] = air != null ? air.RotationStrengthYaw : 0f;
            if (air != null) found.Add("air control");
            // reset (DFUNC_ResetCar)
            var reset = all.OfType<DFUNC_ResetCar>().FirstOrDefault();
            v["HasReset"] = reset != null;
            v["ResetHeight"] = reset != null ? reset.AddedHeight : 0f;
            v["ResetMaxSpeed"] = reset != null ? reset.AllowRespawnSpeed : 0f;
            v["ResetStop"] = reset != null && reset.StopCarOnReset;
            v["ResetStopSpin"] = reset != null && reset.StopRotatingOnReset;
            if (reset != null) found.Add("reset");
            foreach (var source in new[] { v["HornSound"], v["JumpSound"] }.OfType<AudioSource>()) source.playOnAwake = false;
            // (anything kept that sits under a part stripped later - the dial objects - is moved out to the vehicle first)
            foreach (var t in new[] { v["BoostPoint"], v["HornSound"], v["JumpSound"] }.Select(o => o is Component c ? c.transform : null).Where(t => t != null))
                for (var up = t.parent; up != null && up != root.transform; up = up.parent)
                    if (Dropped.Contains(up.name)) { t.SetParent(root.transform, true); break; }
            report.AppendLine($"   [{name}] driver functions: {(found.Count > 0 ? string.Join(", ", found) : "none")}");
            return v;
        }

        static float ExplodeLength(Animator animator)
        {
            if (!(animator.runtimeAnimatorController is UnityEditor.Animations.AnimatorController controller)) return 0f;
            foreach (var layer in controller.layers)
                foreach (var child in layer.stateMachine.states)
                    if (child.state.name == "Explode" && child.state.motion is AnimationClip clip)
                        return clip.length / Mathf.Max(Mathf.Abs(child.state.speed), 0.01f);
            return 0f;
        }

        /// <summary>Turns on Unity's automatic Mesh LODs for the model files a prefab draws (as the aircraft builder does).</summary>
        static void EnsureMeshLods(GameObject prefab)
        {
            var paths = prefab.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh)
                .Concat(prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(s => s.sharedMesh))
                .Where(m => m != null).Select(AssetDatabase.GetAssetPath).Where(p => p.StartsWith("Assets/Sacc/")).Distinct();
            foreach (var path in paths)
                if (AssetImporter.GetAtPath(path) is ModelImporter importer && !importer.generateMeshLods)
                {
                    importer.generateMeshLods = true;
                    importer.SaveAndReimport();
                }
        }

        /// <summary>
        /// Reads script settings straight from a prefab's text file, for the kinds of field the data-only
        /// stand-in scripts do not declare (arrays and curves that have an initial value in the original source).
        /// </summary>
        sealed class PrefabYaml
        {
            readonly Dictionary<long, string> entries = new Dictionary<long, string>();

            public PrefabYaml(string path)
            {
                string text = File.ReadAllText(path).Replace("\r\n", "\n");
                foreach (Match match in Regex.Matches(text, @"--- !u!\d+ &(-?\d+)[^\n]*\n(.*?)(?=\n--- !u!|\z)", RegexOptions.Singleline))
                    entries[long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] = "\n" + match.Groups[2].Value + "\n";
            }

            /// <summary>The text of one object of the prefab asset (empty if it cannot be found).</summary>
            public string Entry(UnityEngine.Object assetObject)
            {
                if (assetObject != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(assetObject, out _, out long id) &&
                    entries.TryGetValue(id, out var text))
                    return text;
                return "";
            }

            static float Number(string text)
            {
                text = text.Trim();
                if (text.IndexOf("inf", StringComparison.OrdinalIgnoreCase) >= 0) return text.StartsWith("-") ? float.NegativeInfinity : float.PositiveInfinity;
                return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            /// <summary>A list of numbers, or null when the prefab does not store the field (the script's default applies).</summary>
            public static float[] Floats(string entry, string field)
            {
                if (string.IsNullOrEmpty(entry)) return null;
                if (Regex.IsMatch(entry, @"\n  " + Regex.Escape(field) + @": \[\]")) return new float[0];
                var match = Regex.Match(entry, @"\n  " + Regex.Escape(field) + @":\n((?:  - [^\n]*\n)+)");
                if (!match.Success) return null;
                return match.Groups[1].Value.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => Number(line.Substring(4))).ToArray();
            }

            /// <summary>An animation curve, or null when the prefab does not store the field.</summary>
            public static AnimationCurve Curve(string entry, string field)
            {
                if (string.IsNullOrEmpty(entry)) return null;
                var match = Regex.Match(entry, @"\n  " + Regex.Escape(field) + @":\n((?:    [^\n]*\n)+)");
                if (!match.Success) return null;
                var keys = new List<Keyframe>();
                foreach (var block in Regex.Split(match.Groups[1].Value, @"    - serializedVersion: \d+\n").Skip(1))
                {
                    float Read(string name, float fallback)
                    {
                        var found = Regex.Match(block, @"      " + name + @": ([^\n]*)\n");
                        return found.Success ? Number(found.Groups[1].Value) : fallback;
                    }
                    keys.Add(new Keyframe(Read("time", 0f), Read("value", 0f), Read("inSlope", 0f), Read("outSlope", 0f), Read("inWeight", 0f), Read("outWeight", 0f))
                    {
                        weightedMode = (WeightedMode)(int)Read("weightedMode", 0f),
                    });
                }
                return keys.Count > 0 ? new AnimationCurve(keys.ToArray()) : null;
            }
        }
    }
}
