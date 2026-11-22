using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using SaccFlightAndVehicles;
using Unity.VisualScripting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Puts vehicles from SaccFlightAndVehicles (MIT, (c) 2021 Sacchan-VRC) into the scene as working aircraft.
    ///
    /// Each vehicle comes from its original prefab: model, armature, colliders, wheels, animator, sounds and
    /// effects are kept as authored. What the prefab's VRChat scripts would do at run time is done here or by
    /// the flight graph instead:
    ///  - the flight tuning and the references between parts are read from the prefab through data-only
    ///    stand-ins for those scripts (Tools/sacc_import), then every stand-in is removed, because a world
    ///    cannot ship scripts;
    ///  - the vehicle is re-pivoted on its centre of mass and its wheel springs are scaled by its mass, as
    ///    SaccAirVehicle does when it starts;
    ///  - materials are rebuilt on this world's shaders (the originals are for another render pipeline);
    ///  - seats become platform seats, the body a synced object, and the flight graph is attached with the
    ///    prefab's own values as its variables.
    /// </summary>
    public static class SaccFleetBuilder
    {
        const string ReportPath = "Logs/aircraft_build_report.txt";
        const string PrefabDir = "Assets/Sacc/Prefabs";

        /// <summary>
        /// The fleet on the apron: (prefab, name in the world, row), each row listed from the runway side
        /// outwards. Row 0 is nearest the arrival point and holds everything that can rise straight up;
        /// row 1 stands beyond it with nothing ahead, for the aircraft that need a take-off run.
        /// </summary>
        static readonly (string prefab, string name, int row)[] Fleet =
        {
            ("GRVR-1Main", "GRVR-1", 0), ("SD-1Main", "SD-1", 0), ("SH-1Main", "SH-1", 0), ("SH-3Main", "SH-3", 0),
            ("SC-1Main", "SC-1", 0), ("SH-2Main", "SH-2", 0), ("SaccBalloonMain", "Balloon", 0),
            ("StuntPlane_Main", "Stunt Plane", 1), ("Wasp-WX3_Main", "Wasp WX3", 1), ("SB-1Main", "SB-1", 1),
            ("SF-1Main", "SF-1 1", 1), ("SF-1Main", "SF-1 2", 1), ("SF-2Main", "SF-2", 1), ("SX-TR3Main", "SX-TR3", 1),
            // VAU v320neo (MIT, (c) 2023 VRChat Aerospace University; Tools/sacc_import/import_vau320.py): an airliner
            // built on SaccFlightAndVehicles 1.64 with EsnyaSFAddons' engines, gear and flaps
            ("Assets/VAU320/Prefab/v320neo_main 1217.prefab", "A320", 1),
            ("SS-1Main", "SS-1", Afloat),
        };

        /// <summary>What one vehicle needs differently from the rest.</summary>
        sealed class Special
        {
            /// <summary>Tuned for Sacc before 1.7 with AutoAdjustValuesToMass off: its strengths are forces for its own mass.</summary>
            public bool absoluteForces;
            /// <summary>How many of its passenger seats become real seats, in the prefab's order. (A seat costs nothing per
            /// frame; the graph looks at every passenger seat's eye point twice a second, only while the local player is
            /// at the vehicle and it is nearly still.)</summary>
            public int passengerSeats = int.MaxValue;
            public string[] noSeatsUnder = new string[0];
            /// <summary>Passenger seats the original does not have, for a vehicle that would otherwise carry nobody but its
            /// pilot: where the rider sits, in metres to the right of, above and ahead of the pilot seat.</summary>
            public Vector3[] extraSeats = new Vector3[0];
            /// <summary>Always drawn at full detail: no coarser mesh levels at a distance, and the graph's distance bias
            /// leaves it alone (the balloon: a big shape seen from far away, and only about 13,000 triangles).</summary>
            public bool fullDetail;
            /// <summary>Where everybody gets out: metres to the right of and ahead of the vehicle's centre of mass, on the ground
            /// (its seats are metres above it).</summary>
            public Vector2? exitAt;
            public float seatReach = 5f;
            public Vector3 passengerClick = new Vector3(1.0f, 1.4f, 1.1f);
            /// <summary>Parts drawn only for players close by or aboard (a cabin of 115,000 triangles).</summary>
            public string[] nearOnly = new string[0];
            public float nearWithin;
            /// <summary>No real-time lights, and no sound plays by itself: most of its sounds belonged to scripts that are not ported.</summary>
            public bool quiet;
            /// <summary>Extra room before it in its row (a 36 m wingspan beside a balloon 42 m across).</summary>
            public float gapBefore;
            /// <summary>Parts switched off for good, by the end of their path in the vehicle. On the airliner: screens with
            /// nothing behind them in this port (flight bag, MCDU, datalink, system page) and the first officer's copies of
            /// the captain's displays. Every element of a canvas on a moving vehicle is re-synced each frame; its flight
            /// deck has 753 of them, and with all of them on a headset lost 9 ms a frame in the captain's seat.</summary>
            public string[] switchedOff = new string[0];
            /// <summary>Objects whose own collider is removed (their children's stay). The airliner's fuselage is one solid
            /// capsule with the flight deck and cabin inside it: a player let go of a seat in there is thrown clear by the
            /// physics (35 m, measured). Its floor, belly, wings, tail and engines have colliders of their own.</summary>
            public string[] hollow = new string[0];
            /// <summary>A sign at the arrival point takes players to it, and one beside it brings them back: (label, where the
            /// player arrives, in metres to the right of and ahead of where the vehicle is parked).</summary>
            public (string label, float right, float ahead)? signTo;
        }

        static readonly Dictionary<string, Special> Specials = new Dictionary<string, Special>
        {
            ["A320"] = new Special
            {
                // every cabin seat; not the joke seats on the engines and in the hold
                absoluteForces = true, noSeatsUnder = new[] { "VerySecuritySetas" },
                exitAt = new Vector2(-5.5f, 11f), seatReach = 9f, passengerClick = new Vector3(0.42f, 1.0f, 0.7f),
                nearOnly = new[] { "INTERIOR", "interior_empty", "InVehicleStaticStuff" }, nearWithin = 45f, quiet = true,
                gapBefore = 30f, signTo = ("AIRLINER", -5.5f, 10f),       // beside the nose, under the captain's window
                switchedOff = new[] { "Avionics/EFB", "DU_static/MCDU", "DU_static/DCDU", "ECAM/ECAM-SD", "DisplayUnits/EFIS_R" },
                hollow = new[] { "机身碰撞体" },      // ("fuselage collision body")
            },
            // The drone has one chair; a passenger rides on the cargo pack strapped on behind it.
            ["SD-1"] = new Special { extraSeats = new[] { new Vector3(0f, 0.25f, -0.9f) } },
            ["Balloon"] = new Special { fullDetail = true },
        };

        /// <summary>Row number for a vehicle that is moored on the nearest open water instead of parked on the apron.</summary>
        const int Afloat = -1;

        // Metres from the arrival point: the first row ahead of it, the next row beyond that, and where every
        // row starts on the runway side (the runway's edge is 60 m that way, so rows grow away from it).
        const float FirstRowAhead = 26f, RowSpacing = 46f, RowStartsAt = -45f, GapBetweenVehicles = 5f;

        // Parts of a prefab that are not carried over yet: weapons and their dial menus, VRChat sync and camera
        // helpers. Removed by name wherever they sit in the vehicle.
        static readonly HashSet<string> Dropped = new HashSet<string>
        {
            "DialFunctions", "AAMs", "AGMs", "Bombs", "SyncScript", "CameraPositions", "WaterTrigger", "AAMTarget",
            "StickDisplayCanvas", "AtGScreen", "AtGCam", "ResupplyTrigger",
            // The airliner's jet blast: invisible particles that push whatever they hit (collider force 5000) for 100 m
            // behind each engine, and ahead of it for reverse thrust. Its engine script ran them by thrust; left
            // running they threw players and anything round along two lines across the apron.
            "Brust", "ReveseBrust",
        };

        [MenuItem("FlightWorld/Flight/Build Fleet In Scene")]
        public static void BuildFleet()
        {
            var report = new StringBuilder();
            int failed = 0;
            try
            {
                FlightGraphs.Regenerate();
                var graph = AssetDatabase.LoadAssetAtPath<ScriptGraphAsset>(FlightGraphs.CorePath);
                if (graph == null) throw new Exception("flight graph missing: " + FlightGraphs.CorePath);
                var required = FlightGraphs.BuildCore().objectVariables;

                var scene = EditorSceneManager.GetActiveScene();
                foreach (var old in scene.GetRootGameObjects().Where(o => o.name == "Aircraft").ToArray())
                    UnityEngine.Object.DestroyImmediate(old);
                var fleet = new GameObject("Aircraft");
                var signs = FleetParts.Child(fleet, "Signs");

                var spawn = GameObject.Find("RunwaySpawn");
                var origin = spawn != null ? spawn.transform.position : new Vector3(0f, 1.1f, 0f);
                var facing = spawn != null ? spawn.transform.rotation : Quaternion.identity;
                float ground = origin.y - 0.1f;
                SaccMaterials.Unconverted.Clear();
                // Build each one where it stands in its prefab, then park them in rows by their real sizes.
                var built = new List<(int row, Built vehicle)>();
                foreach (var (prefab, name, row) in Fleet)
                {
                    try
                    {
                        built.Add((row, Build(prefab, name, fleet, graph, report)));
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
                Moor(built.Where(b => b.row == Afloat).Select(b => b.vehicle).ToList(), fleet, signs, spawn != null ? spawn.transform : fleet.transform,
                    origin, facing, ground, required, report);
                foreach (var row in built.Where(b => b.row != Afloat).GroupBy(b => b.row).OrderBy(r => r.Key))
                {
                    float cursor = RowStartsAt;
                    foreach (var (_, vehicle) in row)
                    {
                        Specials.TryGetValue(vehicle.name, out var special);
                        cursor += special != null ? special.gapBefore : 0f;
                        float side = cursor - vehicle.bounds.min.x;
                        cursor += vehicle.bounds.size.x + GapBetweenVehicles;
                        var stand = origin + facing * new Vector3(side, 0f, FirstRowAhead + RowSpacing * row.Key);
                        Park(vehicle, signs, spawn != null ? spawn.transform : fleet.transform, stand, facing, ground, required, report);
                        if (special != null && special.signTo.HasValue)
                        {
                            // a vehicle that stands a long walk from the arrival point gets a sign there and one back
                            var (label, right, ahead) = special.signTo.Value;
                            var arrive = FleetParts.Child(signs, "Arrive " + vehicle.name).transform;
                            arrive.SetPositionAndRotation(new Vector3(stand.x, ground + 0.15f, stand.z) + facing * new Vector3(right, 0f, ahead),
                                facing * Quaternion.Euler(0f, 90f, 0f));                                  // facing the vehicle
                            int count = signs.transform.Cast<Transform>().Count(t => t.name.StartsWith("To The ") && t.name != "To The Airfield");
                            Teleporter(signs, "To The " + vehicle.name, label, origin + facing * new Vector3(8.5f + 2.6f * count, 0f, 5f), ground, facing, arrive);
                            Teleporter(signs, "To Arrivals From " + vehicle.name, "BACK TO\nARRIVALS",
                                arrive.position + facing * new Vector3(-2.2f, 0f, -1.5f), ground, facing * Quaternion.Euler(0f, 180f, 0f),
                                spawn != null ? spawn.transform : fleet.transform);
                        }
                    }
                }
                FleetParts.BuildBoards(signs, origin, facing, ground);
                foreach (var tag in new[] { "DFUNC", "DialFuncOn", "EnableInVehicle", "MFD", "RStickDisplayHighlighter" })
                    if (UnityEditorInternal.InternalEditorUtility.tags.Contains(tag) && GameObject.FindGameObjectsWithTag(tag).Length == 0)
                        UnityEditorInternal.InternalEditorUtility.RemoveTag(tag);
                report.AppendLine($"{FleetParts.RegisterWithWatch()} vehicles on the fleet watch");
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                if (SaccMaterials.Unconverted.Count > 0)
                    report.AppendLine("shaders with no stand-in yet (their renderers are off): " + string.Join(", ", SaccMaterials.Unconverted));
                report.AppendLine($"{Fleet.Length - failed} of {Fleet.Length} vehicles built");
                report.AppendLine("OK");
            }
            catch (Exception e)
            {
                report.AppendLine("FAILED: " + e);
                Debug.LogException(e);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, report.ToString());
            Debug.Log("[SaccFleetBuilder] " + report);
        }

        /// <summary>A vehicle assembled at the origin, waiting to be parked.</summary>
        sealed class Built
        {
            public string name, summary;
            public GameObject root;
            public Variables variables;
            public Bounds bounds;           // its solid parts, in its own axes
            public float lowest;            // the lowest point it rests on, relative to its origin
        }

        static Built Build(string prefabName, string name, GameObject fleet, ScriptGraphAsset graph, StringBuilder report)
        {
            string prefabPath = prefabName.EndsWith(".prefab") ? prefabName : $"{PrefabDir}/{prefabName}.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath)
                         ?? throw new Exception($"prefab missing: {prefabPath} (run Tools/sacc_import/import_sacc.py or import_vau320.py)");
            if (!Specials.TryGetValue(name, out var special)) special = new Special();
            EnsureMeshLods(prefab);
            EnsureTextureSizes(prefab);

            // ---- a loose copy of the prefab, with the vehicle itself lifted out of its holder ---------------
            var holder = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(holder, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            var entity = holder.GetComponentInChildren<SaccEntity>(true) ?? throw new Exception($"{prefabName}: no SaccEntity");
            // (a boat's SaccSeaVehicle is a cut-down SaccAirVehicle with the same field names for what it keeps)
            MonoBehaviour flight = holder.GetComponentInChildren<SaccAirVehicle>(true);
            bool boat = flight == null;
            if (boat) flight = holder.GetComponentsInChildren<MonoBehaviour>(true).FirstOrDefault(m => m != null && m.GetType().Name == "SaccSeaVehicle");
            if (flight == null) throw new Exception($"{prefabName}: neither an air vehicle nor a sea vehicle");
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
            var meshTransform = Field<Transform>(flight, "VehicleMesh");
            var mesh = meshTransform != null ? meshTransform.gameObject : throw new Exception($"{prefabName}: no VehicleMesh");
            var centreOfMass = entity.CenterOfMass != null ? entity.CenterOfMass : root.transform;
            bool hasPitchMoment = Field<Transform>(flight, "PitchMoment") != null, hasYawMoment = Field<Transform>(flight, "YawMoment") != null;
            var pitchMoment = hasPitchMoment ? Field<Transform>(flight, "PitchMoment") : centreOfMass;
            var yawMoment = hasYawMoment ? Field<Transform>(flight, "YawMoment") : centreOfMass;
            var seats = root.GetComponentsInChildren<SaccVehicleSeat>(true)
                .Select(s => (go: s.gameObject, pilot: s.IsPilotSeat, eye: s.TargetEyePosition, enable: s.EnableInSeat ?? new GameObject[0])).ToList();
            if (!seats.Any(s => s.pilot)) throw new Exception($"{prefabName}: no pilot seat");
            var extraSeatsFor = special.extraSeats;
            for (int i = 0; i < extraSeatsFor.Length; i++)
            {
                // (made where the seat code below expects an authored seat: it puts the rider's eyes 0.75 m above
                // this point and then the seat 0.45 m under and 0.3 m behind the eyes)
                var pilotGo = seats.First(s => s.pilot).go.transform;
                var extra = new GameObject($"PassengerSeat (extra {i + 1})");
                extra.transform.SetParent(pilotGo.parent, false);
                var at = extraSeatsFor[i];
                extra.transform.position = pilotGo.position + root.transform.right * at.x + root.transform.up * (at.y - 0.3f) + root.transform.forward * (at.z + 0.3f);
                extra.transform.rotation = root.transform.rotation;
                seats.Add((extra, false, null, new GameObject[0]));
            }
            var sound = root.GetComponentInChildren<SAV_SoundController>(true);
            var thrust = sound != null && sound.Thrust != null ? sound.Thrust.FirstOrDefault(a => a) : null;
            var wind = sound != null ? sound.PlaneWind : null;
            var bang = sound != null && sound.Explosion != null ? sound.Explosion.FirstOrDefault(a => a) : null;
            float windFullSpeed = sound != null && sound.PlaneWindMaxVolSpeed > 1f ? sound.PlaneWindMaxVolSpeed : 300f;
            var idle = sound != null && sound.PlaneIdle != null ? sound.PlaneIdle.FirstOrDefault(a => a) : null;
            var insideNote = sound != null ? sound.PlaneInside : null;
            var distant = sound != null ? sound.PlaneDistant : null;
            float insideThrust = sound != null ? sound.InVehicleThrustVolumeFactor : 0.09f;
            float windMinimum = sound != null ? sound.PlaneWindMinVol : 3f, windGain = sound != null ? sound.PlaneWindMultiplier : 0.25f;
            var inside = (entity.EnableInVehicle ?? new GameObject[0]).Concat(seats.Where(s => s.pilot).SelectMany(s => s.enable)).Where(o => o).Distinct().ToList();
            var shared = (entity.EnableInVehicle ?? new GameObject[0]).Where(o => o).Distinct().ToList();
            var hudScreen = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "HudScreen_Pilot");
            var hudControl = root.GetComponentInChildren<SAV_HUDController>(true);
            var hudRoot = hudControl != null ? hudControl.transform : null;
            var hudParts = hudControl == null ? default : (velocity: hudControl.VelocityIndicator, heading: hudControl.HeadingIndicator,
                elevation: hudControl.ElevationIndicator, down: hudControl.DownIndicator, g: hudControl.HUDText_G, mach: hudControl.HUDText_mach,
                altitude: hudControl.HUDText_altitude, knots: hudControl.HUDText_knots, airspeed: hudControl.HUDText_knotsairspeed,
                aoa: hudControl.HUDText_angleofattack, distance: hudControl.distance_from_head, feet: hudControl.AltitudeConversion,
                knotsPer: hudControl.SpeedConversion, eye: hudControl.PilotSeatAdjusterTarget);
            var effects = root.GetComponentInChildren<SAV_EffectsController>(true);
            var effectsObject = effects != null ? effects.gameObject : null;
            var soundObject = sound != null ? sound.gameObject : null;
            var wingTrails = effects != null && effects.WingTrails != null ? effects.WingTrails.Where(t => t).ToList() : new List<ParticleSystem>();
            float trailGs = effects != null && effects.TrailGs > 0f ? effects.TrailGs : 4f;

            T Field<T>(object owner, string field) where T : class => owner?.GetType().GetField(field)?.GetValue(owner) as T;
            MonoBehaviour StandIn(string type) => root.GetComponentsInChildren<MonoBehaviour>(true).FirstOrDefault(m => m != null && m.GetType().Name == type);
            float Setting(MonoBehaviour owner, string field, float fallback) =>
                owner != null && owner.GetType().GetField(field)?.GetValue(owner) is float value ? value : fallback;
            var groundDetector = Field<Transform>(flight, "GroundDetector");
            var groundEffectPoint = Field<Transform>(flight, "GroundEffectEmpty");
            var centreOfDrag = Field<Transform>(flight, "CenterOfDrag");
            var centreOfThrust = Field<Transform>(flight, boat ? "ThrustPoint" : "CenterOfThrust");
            float health = Setting(flight, "Health", 30f), groundRay = Setting(flight, "GroundDetectorRayDistance", 0f);
            float crashDamage = Setting(flight, boat ? "CrashDmg_MinSpeed" : "Crash_Damage_Speed", 10f);
            float crashDeath = Setting(flight, boat ? "CrashDmg_MaxSpeed" : "Crash_Death_Speed", 50f);
            var floatScript = StandIn("SAV_FloatScript");
            var floatPoints = new AotList();
            foreach (var point in Field<Transform[]>(floatScript, "FloatPoints") ?? new Transform[0]) if (point != null) floatPoints.Add(point);
            var floatSettings = new[] { "FloatForce", "MaxDepthForce", "Compressing", "MaxCompressingForce", "FloatRadius", "WaterSidewaysDrag",
                                        "WaterForwardDrag", "WaterRotDrag", "WaveHeight", "WaveScale", "WaveSpeed" }
                .ToDictionary(key => key, key => Setting(floatScript, key, key == "FloatRadius" ? 0.6f : 0f));
            var stallPitchCurve = Field<AnimationCurve>(flight, "PitchAoaPitchForce");
            var stallRollCurve = Field<AnimationCurve>(flight, "YawAoaRollForce");
            bool tiltThrust = StandIn("DFUNC_VTOLAngle") != null;
            var brakeFunction = StandIn("DFUNC_Brake") ?? StandIn("DFUNC_a320_Brake");
            float airbrake = Setting(brakeFunction, "AirbrakeStrength", 0f);
            float groundBrake = Setting(brakeFunction, "GroundBrakeStrength", 6f);
            float groundBrakeSpeed = Setting(brakeFunction, "GroundBrakeSpeed", 40f);
            var thrusters = StandIn("SAV_ThreeDThrust");
            bool hasThrusters = thrusters != null;
            Vector2 Pair(string field) => thrusters != null && thrusters.GetType().GetField(field)?.GetValue(thrusters) is Vector2 pair ? pair : Vector2.zero;
            Vector2 thrustX = Pair("ThreeDThrottleStrengthX"), thrustY = Pair("ThreeDThrottleStrengthY"), thrustZ = Pair("ThreeDThrottleStrengthZ");
            bool hasCanopy = StandIn("DFUNC_Canopy") != null;
            var gauges = StandIn("SaccGaugeController");
            var dialAnimators = new AotList();
            var horizonBalls = new AotList();
            var gaugeNames = new[] { "altitude100", "altitude1000", "altitude10000", "fuel", "speed", "engine", "slip", "ascent", "compass" };
            var gaugesMissing = new SortedSet<string>();
            foreach (var dial in Field<Animator[]>(gauges, "DialsAnimator") ?? new Animator[0])
            {
                if (dial == null) continue;
                dialAnimators.Add(dial);
                var have = dial.runtimeAnimatorController is UnityEditor.Animations.AnimatorController controller
                    ? new HashSet<string>(controller.parameters.Select(parameter => parameter.name)) : new HashSet<string>();
                foreach (var gaugeName in gaugeNames) if (!have.Contains(gaugeName)) gaugesMissing.Add(gaugeName);
            }
            foreach (var ball in Field<Transform[]>(gauges, "Horizon") ?? new Transform[0]) if (ball != null) horizonBalls.Add(ball);
            float gaugeSpeed = Setting(gauges, "MaxSpeed", 257.2f), gaugeSlip = Setting(gauges, "MaxSlip", 15f), gaugeAscent = Setting(gauges, "MaxAscent", 5000f);
            if (gaugesMissing.Count > 0) report.AppendLine($"WARNING {name}: its dials have no parameter(s) {string.Join(", ", gaugesMissing)}; the gauges are left alone");
            bool hasGauges = dialAnimators.Count > 0 && gaugesMissing.Count == 0;
            var gearFunction = StandIn("DFUNC_Gear");
            var flapsFunction = StandIn("DFUNC_Flaps");
            float gearDrag = gearFunction != null ? Setting(gearFunction, "LandingGearDragMulti", 1.3f) - 1f : 0f;
            float flapsDrag = flapsFunction != null ? Setting(flapsFunction, "FlapsDragMulti", 1.4f) - 1f : 0f;
            float flapsLift = flapsFunction != null ? Setting(flapsFunction, "FlapsLiftMulti", 1.35f) - 1f : 0f;
            float flapsMaxLift = Setting(flapsFunction, "FlapsExtraMaxLift", 0f);
            bool hasGear = gearFunction != null, hasFlaps = flapsFunction != null;      // (the stand-ins are destroyed further down)
            bool flapsDefaultOn = flapsFunction != null && !(flapsFunction.GetType().GetField("DefaultFlapsOff")?.GetValue(flapsFunction) is bool off && off);
            // A flap lever with several detents (the A320's): one position is ported, the one that serves for take-off
            // and landing alike ("2": flaps 15, slats 22), with that detent's own lift and drag.
            float flapAngleOut = 0f, slatAngleOut = 0f, flapLeverOut = 0f, flapTime = 8f;
            var flapLever = StandIn("DFUNC_a320_FlapController");
            if (flapsFunction == null && flapLever != null)
            {
                float[] Table(string field, float[] fallback) =>
                    flapLever.GetType().GetField(field)?.GetValue(flapLever) is float[] stored && stored.Length == fallback.Length ? stored : fallback;
                var flapDetents = Table("flapDetents", new[] { 0f, 0f, 10f, 15f, 20f, 35f });
                var slatDetents = Table("slatDetents", new[] { 0f, 18f, 18f, 22f, 22f, 27f });
                var detentDrag = Table("dragMultiplier", new[] { 1f, 1.4f, 1.6f, 1.8f, 1.9f, 2f });
                var detentLift = Table("liftMultiplier", new[] { 1f, 1.456f, 1.6f, 1.84f, 1.9f, 2.06f });
                const int detent = 3, leverStep = 2, leverSteps = 5;
                float fullFlap = Mathf.Max(flapDetents[flapDetents.Length - 1], 1f);
                hasFlaps = true;
                flapsDrag = detentDrag[detent] - 1f;
                flapsLift = detentLift[detent] - 1f;
                flapAngleOut = flapDetents[detent] / fullFlap;
                slatAngleOut = slatDetents[detent] / fullFlap;
                flapLeverOut = leverStep / (leverSteps - 1f);
                flapTime = Mathf.Max(flapDetents[detent] / Mathf.Max(Setting(flapLever, "response", 1.6f), 0.1f), 1f);
            }

            var scripted = new HashSet<AudioSource>();
            foreach (var stub in root.GetComponentsInChildren<MonoBehaviour>(true).Where(IsStandIn))
                foreach (var field in stub.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.FieldType == typeof(AudioSource) && field.GetValue(stub) is AudioSource one && one) scripted.Add(one);
                    else if (field.FieldType == typeof(AudioSource[]) && field.GetValue(stub) is AudioSource[] many)
                        foreach (var item in many) if (item) scripted.Add(item);
                }

            var tuning = new Dictionary<string, object>();
            void Take(MonoBehaviour source, bool onlyMissing)
            {
                foreach (var field in source.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (onlyMissing && tuning.ContainsKey(field.Name)) continue;
                    var value = field.GetValue(source);
                    if (value is float f) tuning[field.Name] = f;
                    else if (value is int i) tuning[field.Name] = (float)i;
                    else if (value is bool b) tuning[field.Name] = b;
                }
            }
            Take(flight, false);
            if (special.absoluteForces)
            {
                // Sacc 1.64 multiplied these by the vehicle's mass when it started (AutoAdjustValuesToMass) or took
                // them as they stood; this vehicle was tuned as they stood. The model ported here works per unit of
                // mass throughout, as the current Sacc does.
                foreach (var key in new[] { "ThrottleStrength", "PitchStrength", "PitchFriction", "PitchConstantFriction", "YawStrength", "YawFriction",
                                            "YawConstantFriction", "RollStrength", "RollFriction", "RollConstantFriction", "VelStraightenStrPitch",
                                            "VelStraightenStrYaw", "YawAoaRollForceMulti", "PitchAoaPitchForceMulti", "Lift", "MaxLift", "VelLiftMax",
                                            "AdverseRoll", "AdverseYaw", "GroundEffectLiftMax" })
                    if (tuning.TryGetValue(key, out var asForce) && asForce is float force) tuning[key] = force / body.mass;
            }
            // Engines that are scripts of their own (EsnyaSFAddons' advanced engine, as an airliner has): the
            // vehicle's thrust is theirs, in newtons.
            var engines = root.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().Name.EndsWith("AdvancedEngine")).ToList();
            float engineThrust = engines.Sum(m => m.GetType().GetField("maxThrust")?.GetValue(m) is float newtons ? newtons : 0f);
            if (engineThrust > 0f && !(tuning.TryGetValue("ThrottleStrength", out var ownThrust) && ownThrust is float alreadyHas && alreadyHas > 0f))
                tuning["ThrottleStrength"] = engineThrust / body.mass;
            // (the same engines idle their thrust lever part-way up its travel, below which is reverse)
            float reverserRatio = engines.Count > 0 && engines[0].GetType().GetField("reverserRatio")?.GetValue(engines[0]) is float ratio ? ratio : 0f;
            float leverIdle = reverserRatio / (1f + reverserRatio);
            var animatorParameters = animator.runtimeAnimatorController is UnityEditor.Animations.AnimatorController animatorController
                ? new HashSet<string>(animatorController.parameters.Select(parameter => parameter.name)) : new HashSet<string>();
            bool travellingLevers = new[] { "gearposC", "gearposL", "gearposR", "flapsangle", "slatsangle", "flapslever", "throttleleveler" }.All(animatorParameters.Contains);
            float gearTime = Mathf.Max(Setting(effects, "GearTransitionTime", 5f), 0.5f);
            if (boat)
            {
                // what a boat's script does not have is taken from an untouched air vehicle, then everything
                // that would make it fly or roll is taken out: it is pushed, steered and held up by water
                var template = new GameObject("__defaults") { hideFlags = HideFlags.HideAndDontSave };
                Take(template.AddComponent<SaccAirVehicle>(), true);
                UnityEngine.Object.DestroyImmediate(template);
                foreach (var key in new[] { "PitchStrength", "RollStrength", "Lift", "VelLift", "MaxLift", "SidewaysLift", "VelStraightenStrPitch",
                                            "VelStraightenStrYaw", "PitchFriction", "RollFriction", "GroundEffectStrength", "TaxiRotationSpeed",
                                            "AdverseYaw", "AdverseRoll" })
                    tuning[key] = 0f;
                foreach (var key in new[] { "VTOLOnly", "DroneMode", "Thrust_AlwaysWorldUp", "DoStallForces", "HasAfterburner" }) tuning[key] = false;
            }

            // ---- strip: stand-ins, VRChat components, parts not carried over yet ----------------------------
            foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(t => t != null && (Dropped.Contains(t.name) || t.name.StartsWith("RacingTrigger"))).ToArray())
                if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
            int notInBuilds = FleetParts.RemoveStrippedFromBuilds(root);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (special.switchedOff.Length == 0) break;
                string path = t.name;
                for (var up = t.parent; up != null && up != root.transform; up = up.parent) path = up.name + "/" + path;
                if (special.switchedOff.Any(end => path.EndsWith(end))) t.gameObject.SetActive(false);
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(t => special.hollow.Contains(t.name)))
                foreach (var own in t.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(own);
            foreach (var extra in root.GetComponentsInChildren<Rigidbody>(true).Where(r => r != body).ToArray())
                UnityEngine.Object.DestroyImmediate(extra.gameObject);
            foreach (var camera in root.GetComponentsInChildren<Camera>(true).ToArray())
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            foreach (var stub in root.GetComponentsInChildren<MonoBehaviour>(true).Where(IsStandIn).ToArray())
                UnityEngine.Object.DestroyImmediate(stub);
            if (special.quiet)
            {
                foreach (var light in root.GetComponentsInChildren<Light>(true)) light.enabled = false;
                foreach (var source in root.GetComponentsInChildren<AudioSource>(true)) source.playOnAwake = false;
            }
            int gone = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                gone += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                t.gameObject.layer = 0;
                if (!t.CompareTag("Untagged")) t.gameObject.tag = "Untagged";     // (the client has none of a prefab's own tags)
            }
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true).ToArray()) UnityEngine.Object.DestroyImmediate(group);
            foreach (var raycaster in root.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(true).ToArray()) UnityEngine.Object.DestroyImmediate(raycaster);
            foreach (var trigger in root.GetComponentsInChildren<Collider>(true).Where(c => c.isTrigger && !(c is WheelCollider)).ToArray())
                UnityEngine.Object.DestroyImmediate(trigger);                 // interaction volumes of the original scripts

            // ---- materials -----------------------------------------------------------------------------------
            int hidden = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                bool particles = renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer;
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => SaccMaterials.Convert(m, particles)).ToArray();
                if (renderer is ParticleSystemRenderer psr && psr.trailMaterial != null) psr.trailMaterial = SaccMaterials.Convert(psr.trailMaterial, true);
                // (A slot with no material at all is left alone: it draws nothing in the app, and on a particle renderer
                // the second slot is only the trail's. Switching such renderers off took working effects with it.)
                if (renderer.sharedMaterials.Any(m => m != null && !SaccMaterials.Usable(m)) && !(renderer.GetComponent<TMPro.TMP_Text>() != null))
                {
                    renderer.enabled = false;
                    hidden++;
                }
            }

            foreach (var graphic in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                if (graphic.material != null && graphic.material != graphic.defaultMaterial)
                    graphic.material = SaccMaterials.Convert(graphic.material, false);

            // ---- body: pivot on the centre of mass, wheels scaled by mass (SaccAirVehicle's start-up) --------
            var offset = centreOfMass.position - root.transform.position;
            foreach (Transform child in root.transform) child.position -= offset;
            body.automaticCenterOfMass = false;
            body.centerOfMass = Vector3.zero;
            body.automaticInertiaTensor = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.isKinematic = false;
            body.constraints = RigidbodyConstraints.None;        // the prefabs are authored frozen; their scripts release them
            var wheels = root.GetComponentsInChildren<WheelCollider>(true);
            foreach (var wheel in special.absoluteForces ? new WheelCollider[0] : wheels)     // (already in real units there)
            {
                wheel.mass *= body.mass;
                var spring = wheel.suspensionSpring;
                spring.spring *= body.mass;
                spring.damper *= body.mass;
                wheel.suspensionSpring = spring;
            }
            // A single wheel on the centreline steers (a nose wheel, or a tail wheel turned the other way);
            // the rest are braked. Vehicles with no such wheel are turned on the spot by the rudder instead.
            float track = wheels.Length > 0 ? wheels.Max(w => Mathf.Abs(w.transform.position.x)) : 0f;
            var centred = wheels.Where(w => Mathf.Abs(w.transform.position.x) < Mathf.Max(0.05f, track * 0.25f)).ToList();
            var steering = centred.Count == 1 && wheels.Length >= 3 ? centred[0] : null;
            var allWheels = new AotList();
            var brakeWheels = new AotList();
            foreach (var wheel in wheels)
            {
                allWheels.Add(wheel);
                if (wheel != steering) brakeWheels.Add(wheel);
            }
            float steerAngle = steering == null ? 0f : steering.transform.position.z >= 0f ? 30f : -30f;
            var nose = steering != null ? steering : SpareWheel(root, "NoSteeringWheel");

            // ---- effects and sound -----------------------------------------------------------------------------
            // Effects and warning tones are left as authored: the vehicle's animator switches them on and off
            // (explosion, vapour, smoke, stall tone). Sounds the original scripts start and stop themselves
            // stay silent until the graph plays them.
            foreach (var source in scripted) if (source) source.playOnAwake = false;
            var trailList = new AotList();
            foreach (var trail in wingTrails)
            {
                var main = trail.main;
                main.playOnAwake = false;
                var emission = trail.emission;
                emission.enabled = true;
                trailList.Add(trail);
            }
            var audio = root.transform.Find("Audio") != null ? root.transform.Find("Audio").gameObject : FleetParts.Child(root, "Audio");
            if (thrust == null)
            {
                thrust = FleetParts.Loop(FleetParts.Child(audio, "Engine"), FleetParts.Clip("jet_loop"), 14f, 1500f, 0.6f);
                thrust.volume = 0.8f;
            }
            if (wind == null)
            {
                wind = FleetParts.Loop(FleetParts.Child(audio, "Wind"), FleetParts.Clip("wind_loop"), 3f, 30f, 0f);
                wind.volume = 0.7f;
            }
            // The loops the graph mixes play all the time and start silent; what they were authored at is
            // their full volume.
            var silence = FleetParts.Child(audio, "Unused");
            silence.SetActive(false);
            AudioSource Loop(AudioSource source, string label, out float fullVolume)
            {
                if (source == null) source = FleetParts.Child(silence, label).AddComponent<AudioSource>();
                fullVolume = source.volume;
                source.loop = true;
                source.playOnAwake = true;
                source.volume = 0f;
                source.enabled = true;
                return source;
            }
            thrust = Loop(thrust, "Thrust", out float thrustVolume);
            wind = Loop(wind, "Wind", out float windVolume);
            idle = Loop(idle, "Idle", out float idleVolume);
            insideNote = Loop(insideNote, "Inside", out float insideVolume);
            distant = Loop(distant, "Distant", out float distantVolume);
            if (bang == null)
            {
                bang = FleetParts.Child(audio, "Explosion").AddComponent<AudioSource>();
                bang.clip = FleetParts.Clip("explosion");
                bang.spatialBlend = 1f;
                bang.minDistance = 40f;
                bang.maxDistance = 4000f;
            }
            bang.loop = false;
            if (special.quiet)
            {
                var played = new HashSet<AudioSource> { thrust, wind, idle, insideNote, distant, bang };
                foreach (var source in root.GetComponentsInChildren<AudioSource>(true).Where(a => !played.Contains(a)).ToArray())
                {
                    foreach (var filter in source.GetComponents<Behaviour>().Where(b => b is AudioLowPassFilter || b is AudioHighPassFilter ||
                                 b is AudioReverbFilter || b is AudioEchoFilter || b is AudioDistortionFilter || b is AudioChorusFilter).ToArray())
                        UnityEngine.Object.DestroyImmediate(filter);
                    UnityEngine.Object.DestroyImmediate(source);
                }
            }

            // ---- seats ---------------------------------------------------------------------------------------
            GameObject pilotSeat = null;
            Transform pilotEye = null;
            var seatList = new AotList();
            var passengerSeatColliders = new AotList();
            var passengerEyes = new AotList();
            int passengerSeatsMade = 0;
            bool Under(Transform t, string ancestor)
            {
                for (var up = t; up != null && up != root.transform; up = up.parent) if (up.name == ancestor) return true;
                return false;
            }
            foreach (var (go, pilot, eye, _) in seats)
            {
                if (!pilot && (passengerSeatsMade >= special.passengerSeats || special.noSeatsUnder.Any(ancestor => Under(go.transform, ancestor)))) continue;
                if (!pilot) passengerSeatsMade++;
                // The eye target stays fixed in the airframe; the seat object is what gets slid to fit a pilot.
                var eyePoint = eye != null ? eye : FleetParts.Child(go, "TargetEyePosition").transform;
                if (eye == null) eyePoint.localPosition = new Vector3(0f, 0.75f, 0f);
                eyePoint.SetParent(go.transform.parent, true);         // beside the seat: some seats ride a cabin that moves
                eyePoint.name = "EyePoint " + go.name;
                var exit = go.transform.Find("Exit");                    // where the original lets its riders out
                // (the vehicle stands at the origin here, already pivoted on its centre of mass; the ground is under its wheels)
                float groundUnder = wheels.Length > 0 ? wheels.Min(w => w.transform.position.y - w.radius - w.suspensionDistance * 0.5f) : root.transform.position.y;
                Vector3 exitPoint = special.exitAt.HasValue ? new Vector3(special.exitAt.Value.x, groundUnder + 0.25f, special.exitAt.Value.y)
                    : exit != null ? exit.position : go.transform.position + root.transform.right * 3f;
                // A seated player's eyes come out roughly 0.45 m above and 0.3 m ahead of a platform seat's origin.
                go.transform.position = eyePoint.position - root.transform.up * 0.45f - root.transform.forward * 0.30f;
                go.transform.rotation = root.transform.rotation;
                go.layer = 5;                                            // UI layer: the only layers that can be clicked
                foreach (var old in go.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(old);
                var click = go.AddComponent<BoxCollider>();
                click.isTrigger = true;
                click.size = pilot ? new Vector3(1.0f, 1.4f, 1.1f) : special.passengerClick;
                click.center = new Vector3(0f, 0.4f, 0.1f);
                var seatComponent = go.AddComponent(GraphTypes.Find("BS.BSSeat"));
                FleetParts.SetSerialized(seatComponent, report, ("unseatOnMove", false), ("unseatOnJump", true), ("viewMode", 0),
                    ("useExitPosition", true), ("exitPosition", go.transform.InverseTransformPoint(exitPoint)));
                FleetParts.SetSerialized(go.GetComponent(GraphTypes.Find("BS.BSPointerRange")), report, ("distance", special.seatReach));
                seatList.Add(seatComponent);
                if (pilot && pilotSeat == null)
                {
                    pilotSeat = go;
                    pilotEye = eyePoint;
                }
                else
                {
                    passengerSeatColliders.Add(click);
                    passengerEyes.Add(eyePoint);
                }
            }

            // ---- head-up display ----------------------------------------------------------------------------
            // The vehicle's own display is centred on the pilot's eye point (SAV_HUDController does this when
            // it starts). Anything a vehicle's display lacks is pointed at an unused stand-in, so the graph
            // can treat every vehicle alike; a vehicle with no display at all gets a plain readout.
            var unused = FleetParts.Child(root, "Unused");
            unused.SetActive(false);
            Transform Spare(Transform t, string label) => t != null ? t : FleetParts.Child(unused, label).transform;
            UnityEngine.UI.Text SpareText(UnityEngine.UI.Text t, string label) => t != null ? t : FleetParts.Child(unused, label).AddComponent<UnityEngine.UI.Text>();
            if (hudRoot != null) hudRoot.position = hudParts.eye != null ? hudParts.eye.position : pilotEye.position;
            var readoutHolder = hudRoot != null ? unused : (inside.FirstOrDefault() ?? root);
            var hud = FleetParts.Child(readoutHolder, "Readout");
            hud.transform.position = hudScreen != null ? hudScreen.position : pilotEye.position + root.transform.forward * 0.9f - root.transform.up * 0.08f;
            hud.transform.rotation = root.transform.rotation;
            var hudText = hud.AddComponent<TMPro.TextMeshPro>();
            hudText.rectTransform.sizeDelta = new Vector2(0.4f, 0.2f);
            hudText.fontSize = 0.34f;
            hudText.alignment = TMPro.TextAlignmentOptions.Center;
            hudText.color = new Color(0.35f, 1f, 0.45f);
            hudText.text = "";

            // ---- platform components ------------------------------------------------------------------------
            var sync = root.AddComponent(GraphTypes.Find("BS.BSSyncedObject"));
            FleetParts.SetSerialized(sync, report, ("takeOwnershipOnCollision", false), ("takeOwnershipOnProximity", false), ("takeOwnershipOnGrab", false));
            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                animator.keepAnimatorStateOnDisable = true;      // the graph rests it on an idle vehicle; gear and flaps must stay as they were
            }

            // ---- its size, and the point just under its hull that the graph looks down from ---------------------
            UnityEngine.Physics.SyncTransforms();        // collider bounds are only as fresh as the physics scene
            var solids = root.GetComponentsInChildren<Collider>(false).Where(c => c.enabled && !c.isTrigger && !(c is WheelCollider)).ToList();
            if (solids.Count == 0) throw new Exception($"{prefabName}: no solid colliders");
            var bounds = solids[0].bounds;
            foreach (var collider in solids) bounds.Encapsulate(collider.bounds);
            float lowest = wheels.Length > 0
                ? Mathf.Min(bounds.min.y, wheels.Min(w => w.transform.position.y - w.radius - w.suspensionDistance * 0.5f))
                : bounds.min.y;
            var probe = FleetParts.Child(root, "GroundProbe").transform;
            probe.localPosition = new Vector3(0f, bounds.min.y - 0.03f, 0f);
            float detectorAbove = groundDetector != null ? groundDetector.position.y - probe.position.y : 0f;
            float contactDistance = groundDetector != null && groundRay > 0f ? groundRay - detectorAbove : -1f;
            float effectAbove = (groundEffectPoint != null ? groundEffectPoint : centreOfMass).position.y - probe.position.y;

            // ---- tuning and references read by the graph -----------------------------------------------------
            bool Flag(string key) => tuning.TryGetValue(key, out var flag) && flag is bool on && on;
            float Value(string key, float fallback = 0f) => tuning.TryGetValue(key, out var number) && number is float f ? f : fallback;
            if (!Flag("HasAfterburner"))
            {
                tuning["HasAfterburner"] = false;
                tuning["ThrottleAfterburnerPoint"] = 0.9999f;
                tuning["AfterburnerThrustMulti"] = 1f;
            }
            bool vtolOnly = Flag("VTOLOnly");
            float tiltRange = Value("VTOLMaxAngle") - Value("VTOLMinAngle");
            float groundEffectRange = Mathf.Max(Value("GroundEffectMaxDistance"), 0.001f);
            foreach (var key in new[] { "VTOLOnly", "DroneMode", "Thrust_AlwaysWorldUp" }) tuning[key] = Flag(key);
            foreach (var axis in new[] { "Pitch", "Yaw", "Roll" })
            {
                // Going backwards reverses a control surface, unless the vehicle steers that axis with thrust.
                tuning[$"Reversing{axis}Zero"] = vtolOnly || Value($"{axis}ThrustVecMulti") != 0f ? 1f : -Value($"Reversing{axis}StrengthMulti", 2f);
                foreach (var key in new[] { $"{axis}ThrustVecMulti", $"VTOL{axis}ThrustVecMulti", $"{axis}ConstantFriction" }) tuning[key] = Value(key);
            }
            if (!Flag("DoStallForces"))
            {
                tuning["PitchAoaPitchForceMulti"] = 0f;
                tuning["YawAoaRollForceMulti"] = 0f;
            }
            foreach (var (key, value) in new (string, float)[]
                     {
                         ("SeaLevel", 0f), ("ExtraDrag", 1f), ("ExtraLift", 1f),
                         ("FullHealth", health), ("CrashDamageSpeed", crashDamage), ("CrashDeathSpeed", crashDeath),
                         ("RespawnDelay", 5f), ("NoseWheelSteerAngle", steerAngle), ("BrakeTorque", body.mass * 2.5f), ("PropellerSpeed", 0f),
                         ("SoundRefSpeed", Value("RotMultiMaxSpeed", 220f)), ("WindRefSpeed", windFullSpeed),
                         ("AboardRadius", Mathf.Max(bounds.extents.x, bounds.extents.z) + 4f),
                         ("VTOLAngleDivider", tiltRange > 0f ? Value("VTOLAngleTurnRate", 90f) / tiltRange : 0f),
                         ("VTOLLoseControlSpeed", Mathf.Max(Value("VTOLLoseControlSpeed", 120f), 0.001f)),
                         ("VTOLMinAngle", Value("VTOLMinAngle")), ("VTOLMaxAngle", Value("VTOLMaxAngle")), 
                         // A tilt-thrust aircraft with no wheels cannot make a take-off run: it starts with its thrust pointing up.
                         ("VTOLDefaultValue", tiltThrust && wheels.Length == 0 ? 1f : Value("VTOLDefaultValue")),
                         ("VTOLThrottleStrengthMulti", Value("VTOLThrottleStrengthMulti", 1f)), ("VTOLGroundEffectStrength", Value("VTOLGroundEffectStrength")),
                         ("GroundEffectMaxDistance", groundEffectRange), ("GroundEffectStrength", Value("GroundEffectStrength")),
                         ("GroundEffectLiftMax", Value("GroundEffectLiftMax")), ("GroundEffectProbeOffset", effectAbove),
                         ("GroundContactDistance", contactDistance),
                         ("ProbeLength", Mathf.Max(Mathf.Max(groundEffectRange - effectAbove, contactDistance), 0.5f) + 0.1f),
                         ("TaxiRotationSpeed", Value("TaxiRotationSpeed")), ("TaxiRotationResponse", Value("TaxiRotationResponse", 2.5f)),
                         ("TaxiStillSpeed", Flag("DisallowTaxiRotationWhileStill") ? Value("TaxiFullTurningSpeed", 20f) : 0f),
                         ("AdverseYaw", Value("AdverseYaw")), ("AdverseRoll", Value("AdverseRoll")), ("ZeroLiftAoA", Value("ZeroLiftAoA")),
                         ("PitchAoaPitchForceMulti", Value("PitchAoaPitchForceMulti")), ("YawAoaRollForceMulti", Value("YawAoaRollForceMulti")),
                         ("MaxGs", Value("MaxGs", 40f)), ("GDamage", Value("GDamage", 10f)),
                         ("AirbrakeStrength", airbrake), ("GroundBrakeStrength", groundBrake), ("GroundBrakeSpeed", groundBrakeSpeed),
                         ("GearDrag", gearDrag), ("FlapsDrag", flapsDrag), ("FlapsLift", flapsLift), ("FlapsExtraMaxLift", flapsMaxLift),
                         ("ThrustXPositive", thrustX.x), ("ThrustXNegative", thrustX.y), ("ThrustYPositive", thrustY.x), ("ThrustYNegative", thrustY.y),
                         ("ThrustZPositive", thrustZ.x), ("ThrustZNegative", thrustZ.y),
                         ("ThrustVolume", thrustVolume), ("WindVolume", windVolume), ("IdleVolume", idleVolume), ("InsideVolume", insideVolume),
                         ("DistantVolume", distantVolume), ("InVehicleThrustVolumeFactor", insideThrust),
                         ("PlaneWindMinVol", windMinimum), ("PlaneWindMultiplier", windGain),
                     })
                tuning[key] = value;
            var variables = root.AddComponent<Variables>();
            foreach (var pair in tuning) variables.declarations.Set(pair.Key, pair.Value);
            variables.declarations.Set("VTOLenabled", vtolOnly || tiltThrust);
            variables.declarations.Set("HasPitchMoment", hasPitchMoment);
            variables.declarations.Set("HasYawMoment", hasYawMoment);
            variables.declarations.Set("HasCenterOfDrag", centreOfDrag != null);
            variables.declarations.Set("HasCenterOfThrust", centreOfThrust != null);
            variables.declarations.Set("UseTaxiRotation", steering == null);
            variables.declarations.Set("HasThrusters", hasThrusters);
            // with no main engine the throttle lever works the upward thruster
            variables.declarations.Set("ThrottleIsUpThrust", hasThrusters && Value("ThrottleStrength") <= 0f);
            variables.declarations.Set("TickOffset", (fleet.transform.childCount % 12) * 0.008f);
            variables.declarations.Set("IsBoat", boat);
            variables.declarations.Set("HasFloats", floatPoints.Count > 0);
            variables.declarations.Set("FloatPoints", floatPoints);
            foreach (var pair in floatSettings) variables.declarations.Set(pair.Key, pair.Value);
            variables.declarations.Set("WaterLevel", 0f);         // set where it is moored
            var hullList = new AotList();
            if (boat) foreach (var solid in solids) hullList.Add(solid);
            variables.declarations.Set("HullColliders", hullList);
            var seaSurface = boat ? GameObject.Find("CityEnvironment/SeaCollider") : null;
            if (boat && seaSurface == null) throw new Exception($"{prefabName}: the sea's collider (CityEnvironment/SeaCollider) was not found");
            variables.declarations.Set("SeaCollider", seaSurface != null ? seaSurface.GetComponent<Collider>() : null);
            variables.declarations.Set("HasGauges", hasGauges);
            variables.declarations.Set("DialsAnimators", dialAnimators);
            variables.declarations.Set("HorizonBalls", horizonBalls);
            variables.declarations.Set("GaugeMaxSpeed", Mathf.Max(gaugeSpeed, 1f));
            variables.declarations.Set("GaugeMaxSlip", Mathf.Max(gaugeSlip, 0.1f));
            variables.declarations.Set("GaugeMaxAscent", Mathf.Max(gaugeAscent, 1f));
            variables.declarations.Set("HasCanopy", hasCanopy);
            // the display is kept on the pilot's eye point when the seat is not fixed to the airframe
            variables.declarations.Set("HudFollowsEye", hudRoot != null && pilotSeat.transform.parent != root.transform);
            variables.declarations.Set("HasGear", hasGear);
            variables.declarations.Set("HasFlaps", hasFlaps);
            variables.declarations.Set("FlapsDefaultOn", flapsDefaultOn);
            variables.declarations.Set("HasTravellingLevers", travellingLevers);
            variables.declarations.Set("GearTime", gearTime);
            variables.declarations.Set("FlapTime", flapTime);
            variables.declarations.Set("FlapAngleOut", flapAngleOut);
            variables.declarations.Set("SlatAngleOut", slatAngleOut);
            variables.declarations.Set("FlapLeverOut", flapLeverOut);
            variables.declarations.Set("ThrottleLeverIdle", leverIdle);
            // parts that are drawn only for players close by or aboard
            var nearList = new AotList();
            foreach (var part in root.GetComponentsInChildren<Transform>(true)) if (special.nearOnly.Contains(part.name)) nearList.Add(part.gameObject);
            variables.declarations.Set("NearObjects", nearList);
            variables.declarations.Set("HasNearObjects", nearList.Count > 0);
            variables.declarations.Set("NearWithin", special.nearWithin);
            // an airliner's flight displays: animators fed with normalised numbers (the A320's PFD, ND, standby and engine pages)
            var attitudeDisplays = new AotList();
            var primaryDisplays = new AotList();
            var navigationDisplays = new AotList();
            var engineDisplays = new AotList();
            foreach (var display in root.GetComponentsInChildren<Animator>(true))
            {
                if (display == animator || !(display.runtimeAnimatorController is UnityEditor.Animations.AnimatorController displayController)) continue;
                if (!display.gameObject.activeInHierarchy) continue;        // (on a part that is switched off for good)
                var has = new HashSet<string>(displayController.parameters.Select(parameter => parameter.name));
                if (new[] { "AirSpeedNormalize", "PitchAngleNormalize", "BankAngleNormalize", "HeadingNormalize", "AltitudeNormalize", "Altitude10Normalize",
                            "Altitude100Normalize", "Altitude1000Normalize", "Altitude10000Normalize", "VerticalSpeedNormalize", "SlipAngleNormalize" }.All(has.Contains))
                {
                    attitudeDisplays.Add(display);
                    if (new[] { "RHNormalize", "TRKPCHNormalize", "VMAXNormalize", "VSWNormalize", "VLSNormalize", "VFENEXTNormalize", "AirSpeedSelectNormalize",
                                "SpeedTrend", "PilotInputX", "PilotInputY" }.All(has.Contains)) primaryDisplays.Add(display);
                }
                else if (new[] { "HeadingNormalize", "SlipAngleNormalize", "LOCHDGNormalize" }.All(has.Contains)) navigationDisplays.Add(display);
                else if (new[] { "ENG1N1", "ENG2N1", "ENG1EGT", "ENG2EGT", "ENG1N1Cmd", "ENG2N1Cmd", "flapPos", "slatPos" }.All(has.Contains)) engineDisplays.Add(display);
            }
            // (the graph steps these animators itself, a few times a second; left running they redraw their canvases every frame)
            foreach (Animator display in attitudeDisplays.Cast<Animator>().Concat(navigationDisplays.Cast<Animator>()).Concat(engineDisplays.Cast<Animator>()))
            {
                display.keepAnimatorStateOnDisable = true;
                display.enabled = false;
            }
            variables.declarations.Set("DisplayInterval", 0.055f);
            variables.declarations.Set("HasFlightDisplays", attitudeDisplays.Count + navigationDisplays.Count + engineDisplays.Count > 0);
            variables.declarations.Set("AttitudeDisplays", attitudeDisplays);
            variables.declarations.Set("PrimaryDisplays", primaryDisplays);
            variables.declarations.Set("NavigationDisplays", navigationDisplays);
            variables.declarations.Set("EngineDisplays", engineDisplays);
            variables.declarations.Set("CenterOfDrag", centreOfDrag != null ? centreOfDrag : centreOfMass);
            variables.declarations.Set("CenterOfThrust", centreOfThrust != null ? centreOfThrust : centreOfMass);
            variables.declarations.Set("PitchAoaPitchForce", stallPitchCurve ?? AnimationCurve.Constant(0f, 1f, 0f));
            variables.declarations.Set("YawAoaRollForce", stallRollCurve ?? AnimationCurve.Constant(0f, 1f, 0f));
            variables.declarations.Set("GroundProbe", probe);
            variables.declarations.Set("AllWheels", allWheels);
            variables.declarations.Set("BrakeWheels", brakeWheels);
            variables.declarations.Set("PitchMoment", pitchMoment);
            variables.declarations.Set("YawMoment", yawMoment);
            variables.declarations.Set("NoseWheel", nose);
            variables.declarations.Set("PilotSeat", pilotSeat);
            variables.declarations.Set("PilotSeatComponent", pilotSeat.GetComponent(GraphTypes.Find("BS.BSSeat")));
            variables.declarations.Set("PilotSeatCollider", pilotSeat.GetComponent<BoxCollider>());
            variables.declarations.Set("EyePoint", pilotEye);
            variables.declarations.Set("Seats", seatList);
            variables.declarations.Set("PassengerSeatColliders", passengerSeatColliders);
            variables.declarations.Set("HudText", hudText);
            variables.declarations.Set("HudRoot", Spare(hudRoot, "HudRoot"));
            variables.declarations.Set("HudVelocity", Spare(hudParts.velocity, "HudVelocity"));
            variables.declarations.Set("HudHeading", Spare(hudParts.heading, "HudHeading"));
            variables.declarations.Set("HudElevation", Spare(hudParts.elevation, "HudElevation"));
            variables.declarations.Set("HudDown", Spare(hudParts.down, "HudDown"));
            variables.declarations.Set("HudG", SpareText(hudParts.g, "HudG"));
            variables.declarations.Set("HudMach", SpareText(hudParts.mach, "HudMach"));
            variables.declarations.Set("HudAltitude", SpareText(hudParts.altitude, "HudAltitude"));
            variables.declarations.Set("HudKnots", SpareText(hudParts.knots, "HudKnots"));
            variables.declarations.Set("HudAirspeed", SpareText(hudParts.airspeed, "HudAirspeed"));
            variables.declarations.Set("HudAoA", SpareText(hudParts.aoa, "HudAoA"));
            variables.declarations.Set("HudDistance", hudParts.distance > 0f ? hudParts.distance : 1.333f);
            variables.declarations.Set("HudAltitudeConversion", hudParts.feet > 0f ? hudParts.feet : 3.28084f);
            variables.declarations.Set("HudSpeedConversion", hudParts.knotsPer > 0f ? hudParts.knotsPer : 1.94385f);
            variables.declarations.Set("WingTrails", trailList);
            variables.declarations.Set("TrailGs", trailGs);
            variables.declarations.Set("Propeller", FleetParts.Child(root, "NoPropeller").transform);
            variables.declarations.Set("Model", mesh);
            variables.declarations.Set("BoomMessage", "boom:" + name);
            variables.declarations.Set("EngineSound", thrust);
            variables.declarations.Set("WindSound", wind);
            variables.declarations.Set("IdleSound", idle);
            variables.declarations.Set("InsideSound", insideNote);
            variables.declarations.Set("DistantSound", distant);
            variables.declarations.Set("ExplosionSound", bang);
            variables.declarations.Set("VehicleAnimator", animator);
            // As the vehicle's own scripts did when they started: what is only for those aboard starts switched off.
            // (An airliner's flight deck is 45 canvases and a dozen animators; left on, it cost a headset 7 ms a
            // frame with nobody in it.)
            foreach (var aboardOnly in inside.Concat(shared).Concat(seats.SelectMany(s => s.enable)).Where(o => o).Distinct()) aboardOnly.SetActive(false);
            var insideList = new AotList();
            foreach (var go in inside) insideList.Add(go);
            variables.declarations.Set("InVehicleObjects", insideList);
            var sharedList = new AotList();
            foreach (var go in shared) sharedList.Add(go);
            variables.declarations.Set("SharedInVehicleObjects", sharedList);
            variables.declarations.Set("PassengerEyePoints", passengerEyes);
            // What rests with the animator on an idle vehicle: its effects (particle systems) and its sounds.
            var resting = new AotList();
            foreach (var controller in new[] { effectsObject, soundObject })
                if (controller != null && controller != root && !mesh.transform.IsChildOf(controller.transform)) resting.Add(controller);
            variables.declarations.Set("RestingObjects", resting);
            root.AddComponent<ScriptMachine>().nest.SwitchToMacro(graph);

            // the renderers whose level of detail the graph biases by distance: the meshes worth it
            var lodRenderers = new AotList();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (special.fullDetail)
                {
                    renderer.forceMeshLod = 0;          // always the full mesh, at any distance
                    continue;
                }
                var drawn = renderer is SkinnedMeshRenderer skinnedMesh ? skinnedMesh.sharedMesh
                    : renderer.TryGetComponent<MeshFilter>(out var meshFilter) ? meshFilter.sharedMesh : null;
                if (drawn != null && drawn.lodCount > 1 && drawn.GetIndexCount(0) >= 900) lodRenderers.Add(renderer);
            }
            variables.declarations.Set("LodRenderers", lodRenderers);
            variables.declarations.Set("LodNear", Mathf.Clamp(bounds.size.magnitude * 0.6f, 10f, 25f));
            FleetParts.WatchVariables(variables, root.GetComponent<ScriptMachine>(), bounds, boat);
            int triangles = 0, lodMeshes = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled))
            {
                var drawn = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                    : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
                if (drawn == null) continue;
                triangles += (int)(drawn.GetIndexCount(0) / 3);
                for (int sub = 1; sub < drawn.subMeshCount; sub++) triangles += (int)(drawn.GetIndexCount(sub) / 3);
                if (drawn.lodCount > 1) lodMeshes++;
            }
            var lowestSolid = solids.OrderBy(c => c.bounds.min.y).First();
            string detail = $"   [{name}] lowest solid '{lowestSolid.name}' ({lowestSolid.GetType().Name}, active {lowestSolid.gameObject.activeInHierarchy}, enabled {lowestSolid.enabled}) at y {lowestSolid.bounds.min.y:F2}; " +
                            $"hull {bounds.min.y:F2}..{bounds.max.y:F2}, x {bounds.min.x:F1}..{bounds.max.x:F1}, z {bounds.min.z:F1}..{bounds.max.z:F1}; " +
                            $"ground detector y {(groundDetector != null ? groundDetector.position.y.ToString("F2") : "none")} ray {groundRay:F2}; " +
                            $"ground effect point y {(groundEffectPoint != null ? groundEffectPoint.position.y.ToString("F2") : "none")}; wheels " +
                            string.Join(" ", wheels.Select(w => $"{w.name}({w.transform.position.x:F2},{w.transform.position.y:F2},{w.transform.position.z:F2} r{w.radius:F2} t{w.suspensionDistance:F2} active {w.gameObject.activeInHierarchy})"));
            report.AppendLine(detail);
            string kind = boat ? $"boat ({floatPoints.Count} float points)" : vtolOnly ? "thrust up (helicopter-like)" : tiltThrust ? "tilting thrust" : "aeroplane";
            return new Built
            {
                name = name, root = root, variables = variables, bounds = bounds, lowest = lowest,
                summary = $"{name}: {System.IO.Path.GetFileNameWithoutExtension(prefabName)}, {kind}, {body.mass} kg, {seatList.Count} seat(s)" +
                          $"{(seatList.Count < seats.Count ? $" of the model's {seats.Count}" : "")}, {wheels.Length} wheel(s)" +
                          $"{(steering != null ? " (steers with " + steering.name + ")" : " (turned by rudder on the ground)")}, " +
                          $"{root.GetComponentsInChildren<Renderer>(false).Count(r => r.enabled)} renderers drawing {triangles} triangles ({lodMeshes} meshes with automatic LODs), " +
                          $"{hidden} renderers off (no shader yet), {gone} VRChat components removed, {notInBuilds} editor-only part(s) deleted, ground contact within {contactDistance:F2} m of the probe" +
                          $"{(hudRoot != null ? ", own head-up display" : ", plain readout")}" +
                          $"{(hasThrusters ? $", thrusters (up {thrustY.x}, ahead {thrustZ.x}, sideways {thrustX.x} m/s2)" : "")}" +
                          $"{(hasGauges ? $", {dialAnimators.Count} set(s) of dials" : "")}{(hasCanopy ? ", canopy" : "")}{(pilotSeat.transform.parent != root.transform ? " (seat under " + pilotSeat.transform.parent.name + ")" : "")}" +
                          $"{(hasGear ? ", retractable gear" : "")}{(hasFlaps ? (flapsDefaultOn ? ", flaps (out)" : ", flaps (in)") : "")}" +
                          $"{(engineThrust > 0f ? $", {engines.Count} engine(s) of {engineThrust / engines.Count / 1000f:F0} kN" : "")}" +
                          $"{(special.absoluteForces ? ", tuning converted from forces" : "")}{(travellingLevers ? ", gear and flaps travel by position" : "")}" +
                          $"{(attitudeDisplays.Count + navigationDisplays.Count + engineDisplays.Count > 0 ? $", flight displays ({attitudeDisplays.Count} attitude, {navigationDisplays.Count} navigation, {engineDisplays.Count} engine)" : "")}" +
                          $"{(nearList.Count > 0 ? $", {nearList.Count} part(s) drawn only within {special.nearWithin:F0} m" : "")}",
            };
        }

        /// <summary>A blue sign that takes whoever clicks it to `destination` (the TeleportSign graph).</summary>
        static void Teleporter(GameObject signs, string name, string text, Vector3 position, float height, Quaternion towards, Transform destination)
        {
            var teleport = AssetDatabase.LoadAssetAtPath<ScriptGraphAsset>(FlightGraphs.TeleportPath) ?? throw new Exception("teleport sign graph missing");
            var board = FleetParts.Sign(signs, name, text, new Color(0.10f, 0.42f, 0.72f), position, height, towards);
            var holder = board.transform.parent.gameObject;
            var variables = holder.AddComponent<Variables>();
            variables.declarations.Set("ClickTarget", board);
            variables.declarations.Set("Destination", destination);
            holder.AddComponent<ScriptMachine>().nest.SwitchToMacro(teleport);
        }

        /// <summary>Stand a built vehicle on its parking spot, with its reset sign behind it and stable network ids.</summary>
        /// <summary>
        /// Boats: found the nearest open water to the arrival point, moored there beside a pontoon, with a sign at
        /// the arrival point that takes players to the pontoon and one there that brings them back.
        /// </summary>
        static void Moor(List<Built> boats, GameObject fleet, GameObject signs, Transform respawnPoint, Vector3 origin, Quaternion facing, float ground,
            IEnumerable<string> required, StringBuilder report)
        {
            if (boats.Count == 0) return;
            UnityEngine.Physics.SyncTransforms();
            float sea = 0f;
            var seaRoot = GameObject.Find("City/Sea");
            if (seaRoot != null && seaRoot.GetComponentInChildren<Renderer>() is Renderer water) sea = water.bounds.max.y;
            bool Land(Vector3 p) => UnityEngine.Physics.Raycast(new Vector3(p.x, sea + 400f, p.z), Vector3.down, out var hit, 600f) && hit.point.y > sea + 0.3f;
            Vector3? found = null;
            var outward = Vector3.forward;
            for (float range = 300f; range <= 15000f && found == null; range += 100f)
                for (int bearing = 0; bearing < 360 && found == null; bearing += 6)
                {
                    var direction = Quaternion.Euler(0f, bearing, 0f) * Vector3.forward;
                    var p = origin + direction * range;
                    if (Land(p)) continue;
                    bool open = true;
                    for (int around = 0; around < 360 && open; around += 45) open = !Land(p + Quaternion.Euler(0f, around, 0f) * Vector3.forward * 110f);
                    // real water has a shore: land again within a few hundred metres behind us (holes in the map do not)
                    if (open && Land(p - direction * 260f)) { found = p; outward = direction; }
                }
            if (found == null) throw new Exception("no open water found within 15 km of the arrival point for the boats");
            var dock = FleetParts.Child(fleet, "Dock");
            var heading = Quaternion.LookRotation(outward);
            float deck = sea + 0.45f;
            var pontoon = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pontoon.name = "Pontoon";
            pontoon.transform.SetParent(dock.transform, false);
            pontoon.transform.SetPositionAndRotation(new Vector3(found.Value.x, deck - 0.4f, found.Value.z), heading);
            pontoon.transform.localScale = new Vector3(12f, 0.8f, 8f);
            pontoon.GetComponent<MeshRenderer>().sharedMaterial = FleetParts.FlatMaterial("Pontoon", new Color(0.45f, 0.40f, 0.33f));
            var arrive = FleetParts.Child(dock, "Arrive").transform;
            arrive.SetPositionAndRotation(new Vector3(found.Value.x, deck + 0.1f, found.Value.z) - heading * new Vector3(0f, 0f, 2f), heading);

            var teleport = AssetDatabase.LoadAssetAtPath<ScriptGraphAsset>(FlightGraphs.TeleportPath) ?? throw new Exception("teleport sign graph missing");
            void Teleporter(string name, string text, Vector3 position, float height, Quaternion towards, Transform destination)
            {
                var board = FleetParts.Sign(signs, name, text, new Color(0.10f, 0.42f, 0.72f), position, height, towards);
                var holder = board.transform.parent.gameObject;
                var variables = holder.AddComponent<Variables>();
                variables.declarations.Set("ClickTarget", board);
                variables.declarations.Set("Destination", destination);
                holder.AddComponent<ScriptMachine>().nest.SwitchToMacro(teleport);
            }
            Teleporter("To The Boat", "BOAT\nDOCK", origin + facing * new Vector3(8.5f, 0f, 5f), ground, facing, arrive);
            Teleporter("To The Airfield", "BACK TO\nAIRFIELD", arrive.position + heading * new Vector3(-4.5f, 0f, -1f), deck, heading, respawnPoint);

            float along = 4f + 8f * 0.5f;        // clear of the pontoon's outer edge
            foreach (var boat in boats)
            {
                along += boat.bounds.extents.z + 2f;
                var berth = found.Value + outward * along + heading * new Vector3(0f, 0f, -boat.bounds.center.z);
                boat.variables.declarations.Set("WaterLevel", sea);
                // afloat: the hull's lowest point 0.35 m under the surface; its reset sign stands on the pontoon
                Park(boat, signs, respawnPoint, berth, heading, sea - 0.35f, required, report, deck,
                    new Vector3(found.Value.x, deck, found.Value.z) + heading * new Vector3(4.5f, 0f, -1f));
                along += boat.bounds.extents.z;
            }
            report.AppendLine($"dock at {found.Value} ({Vector3.Distance(found.Value, origin):F0} m from the arrival point), sea level {sea:F2}, boats face {heading.eulerAngles.y:F0}");
        }

        static void Park(Built vehicle, GameObject signs, Transform respawnPoint, Vector3 parking, Quaternion facing, float ground,
            IEnumerable<string> required, StringBuilder report, float? signGround = null, Vector3? signAt = null)
        {
            var root = vehicle.root;
            string name = vehicle.name;
            var position = new Vector3(parking.x, ground - vehicle.lowest + 0.05f, parking.z);
            var sign = FleetParts.ResetSign(signs, name,
                signAt ?? position + facing * new Vector3(vehicle.bounds.center.x - 2.5f, 0f, vehicle.bounds.min.z - 3f), signGround ?? ground, facing);
            vehicle.variables.declarations.Set("RespawnPoint", respawnPoint);
            vehicle.variables.declarations.Set("ResetSign", sign);
            var missing = required.Where(v => !vehicle.variables.declarations.IsDefined(v)).ToList();
            if (missing.Count > 0) throw new Exception($"{name} is missing variables the graph reads: " + string.Join(", ", missing));
            // where it really comes to rest on its suspension (measured: FlightWorld/Flight/Test/Measure Settled
            // Parking Poses), so it does not drop and bounce a few seconds after every load
            if (EditorTools.SettleProbe.TryGet(name, out var settle, out var settleTurn))
                root.transform.SetPositionAndRotation(position + facing * settle, facing * settleTurn);
            else
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

        /// <summary>An inert wheel for vehicles that have fewer than three, so the graph's wheel references always exist.</summary>
        static WheelCollider SpareWheel(GameObject root, string name)
        {
            var go = FleetParts.Child(root, name);
            go.SetActive(false);
            return go.AddComponent<WheelCollider>();
        }

        /// <summary>
        /// Turns on Unity's automatic Mesh LODs for the model files a prefab draws: the importer adds simplified
        /// index buffers to each mesh and the renderer picks one by size on screen, with no extra objects.
        /// </summary>
        /// <summary>A data-only stand-in for one of the imported packages' scripts (Tools/sacc_import): never left in the scene.</summary>
        static bool IsStandIn(MonoBehaviour behaviour)
        {
            if (behaviour == null) return false;
            string path = AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(behaviour));
            return path.StartsWith("Assets/Sacc/") || path.StartsWith("Assets/VAU320/");
        }

        /// <summary>No texture of an imported vehicle is brought in larger than the platform's limit for a headset.</summary>
        static void EnsureTextureSizes(GameObject prefab)
        {
            const int limit = 2048;
            foreach (var path in AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab), true).Where(p => p.StartsWith("Assets/VAU320/")))
                if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.maxTextureSize > limit)
                {
                    importer.maxTextureSize = limit;
                    importer.SaveAndReimport();
                }
        }

        static void EnsureMeshLods(GameObject prefab)
        {
            var paths = prefab.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh)
                .Concat(prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(s => s.sharedMesh))
                .Where(m => m != null).Select(AssetDatabase.GetAssetPath).Where(p => p.StartsWith("Assets/Sacc/") || p.StartsWith("Assets/VAU320/")).Distinct();
            foreach (var path in paths)
                if (AssetImporter.GetAtPath(path) is ModelImporter importer && !importer.generateMeshLods)
                {
                    importer.generateMeshLods = true;
                    importer.SaveAndReimport();
                }
        }
    }
}
