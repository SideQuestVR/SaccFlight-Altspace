using System;
using System.IO;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The ground-vehicle logic (cars and the tank), written as code and emitted as two Visual Scripting graphs.
    ///
    /// Ported from SaccFlightAndVehicles (Copyright (c) 2021 Sacchan-VRC, MIT licence - see
    /// Assets/Flight/THIRD-PARTY-NOTICES.md), reference commit 3216e53f:
    ///   GroundCore, on the vehicle: SaccGroundVehicle (engine revs, throttle, steering, tank tracks),
    ///     SGV_GearBox (automatic mode), DFUNC_CarBrake (brake, handbrake), what SGV_EffectsController and
    ///     SGV_Gauges show, plus the same seat / ownership / crash / respawn handling as the aircraft graph.
    ///   WheelCore, on every wheel: SaccWheel (ray suspension, tyre grip, coupling to the engine).
    /// Variable names follow the originals so the two can be compared.
    ///
    /// The vehicle graph and its wheels' graphs talk through Object variables on the vehicle: it publishes
    /// Simulate, Revs, GearRatioL/R, Clutch, BrakeInput, HandBrakeInput and VehicleSpeed; the wheels add the
    /// engine load they caused to EngineForceUsed (and each publishes its own WheelRPS, for a tank's tracks).
    /// </summary>
    public static class GroundGraphs
    {
        public const string CorePath = "Assets/Flight/Graphs/GroundCore.asset";
        public const string WheelPath = "Assets/Flight/Graphs/WheelCore.asset";
        const string ReportPath = "Logs/ground_graphs_report.txt";

        [MenuItem("FlightWorld/Flight/Regenerate Ground Graphs")]
        public static void Regenerate()
        {
            var report = new StringBuilder();
            try
            {
                BuildCore().Save(CorePath, report);
                BuildWheel().Save(WheelPath, report);
                AssetDatabase.SaveAssets();
                report.AppendLine("OK");
            }
            catch (Exception e)
            {
                report.AppendLine("FAILED: " + e);
                Debug.LogException(e);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, report.ToString());
            Debug.Log("[GroundGraphs] " + report);
        }

        static readonly Type RB = typeof(Rigidbody), TF = typeof(Transform), V3T = typeof(Vector3), AN = typeof(Animator), GO = typeof(GameObject);
        static readonly Type[] Vec = { typeof(Vector3) };
        static readonly Type List = typeof(System.Collections.IEnumerable);
        static Type Sync => GraphTypes.Find("BS.BSSyncedObject");
        static Type Seat => GraphTypes.Find("BS.BSSeat");

        /// <summary>Same reason as in the aircraft graph: on the released app a passenger who sits first takes the vehicle.</summary>
        const bool LockOtherSeatsUntilPilot = true;

        /// <summary>
        /// A departure from the original, for keys and thumbsticks (there is no 450-degree wheel to hold here).
        /// The original's desktop steering allows 1 - speed / SteeringMaxSpeedDT of full lock, and full lock is
        /// 60 degrees of front wheel: at 36 km/h that is still 45 degrees, and with these cars' grip the car
        /// turns round in about a second on one key press (measured in the app: 150 to 190 degrees in a second).
        /// With this on, the lock from a key or a stick is also kept to what gives a set sideways acceleration
        /// (half the tyres' grip, about 1.3 g) at the present speed. Counter-steering into a slide and the
        /// handbrake keep the original's allowances. Off = the original's limits only.
        /// </summary>
        const bool SteeringAssist = true;

        // =====================================================================================================
        //  The vehicle
        // =====================================================================================================
        public static GraphWriter BuildCore()
        {
            var g = new GraphWriter();

            // ---- state ---------------------------------------------------------------------------
            g.Declare("Driving", false);        // the local player is driving this vehicle
            g.Declare("TestPilot", false);      // set by the automated drive test: no seat needed
            g.Declare("Dead", false);
            g.Declare("Reversing", false);      // the automatic gearbox is working towards / in reverse
            g.Declare("LimitingRev", false);    // the engine hit its rev limiter and is cut for a moment
            g.Declare("Awake", true);           // false while it RESTS: parked, empty and still (see the ten-hertz tick)
            g.Declare("IdleTimer", RestDelay);  // counts down to resting
            g.Declare("WheelsOn", false);       // the wheels' own graphs are switched on (only where the vehicle is simulated)
            g.Declare("DozeWait", 0f);          // how long the vehicle has been fit to doze (see FlightGraphs.BuildFleetWatch)
            g.Declare("Dozed", false);          // this graph was switched off, and has not run its slow tick since
            g.Declare("DrivenTimer", 0f);       // counts down from the last sign that another client is driving this vehicle
            g.Declare("LodBias", 0f);           // how many Mesh LOD levels coarser than Unity's own choice the vehicle is drawn
            foreach (var name in new[]
                     {
                         "KeyDrive", "KeySteer", "KeyHandbrake", "TriggerThrottle", "TriggerBrake", "GripHandbrake",
                         "StickSteerL", "StickSteerR", "StickLY", "StickRY",
                         "ThrottleInput", "BrakeInput", "HandBrake", "YawInput", "FinalThrottle", "TrackL", "TrackR",
                         "Revs", "LimiterTimer", "Gear", "GearRatio", "GearRatioL", "GearRatioR", "AutoClutch", "Clutch",
                         "SinceGearChange", "ReverseTimer", "Speed", "HudTimer",
                         "RespawnTimer", "EjectTimer", "ResetTimer", "Riders", "SeatTimer", "GuardTimer", "SoundTimer", "SoundElapsed",
                     })
                g.Declare(name, 0f);
            g.Declare("Health", 100f);
            g.Declare("TrackRotL", 0f);         // how far each caterpillar track has run, in degrees of its road wheels
            g.Declare("TrackRotR", 0f);
            g.Declare("TrackMatL", null);       // this vehicle's own copies of its track materials, to scroll them
            g.Declare("TrackMatR", null);
            g.Declare("LastPosition", Vector3.zero);
            g.Declare("SeatHome", Vector3.zero);
            g.Declare("SpawnPosition", Vector3.zero);
            g.Declare("SpawnRotation", Quaternion.identity);

            // ---- remember the parking spot ------------------------------------------------------------
            g.On(new Start(), _ =>
            {
                g.SetGraphVar("SpawnPosition", g.Get(TF, "position"));
                g.SetGraphVar("SpawnRotation", g.Get(TF, "rotation"));
                g.SetGraphVar("Health", g.P("FullHealth"));
                g.SetGraphVar("SeatHome", g.Get(TF, "localPosition", PilotSeatTransform(g)));
                g.SetGraphVar("LastPosition", g.Get(TF, "position"));
                Park(g);
                g.If(g.ObjVar("HasTracks", typeof(bool)), () =>
                {
                    var materials = g.Keep(g.Get(typeof(Renderer), "materials", g.ObjVar("TracksRenderer", typeof(Renderer))));
                    g.SetGraphVar("TrackMatL", g.ListItem(materials, g.RoundToInt(g.P("TrackSlotL")), typeof(Material)), typeof(Material));
                    g.SetGraphVar("TrackMatR", g.ListItem(materials, g.RoundToInt(g.P("TrackSlotR")), typeof(Material)), typeof(Material));
                });
                // It starts out resting, held where it was built, which is the height it rides at. (Left to
                // itself for its first seconds, before the app has said who simulates it, a vehicle fell on to
                // its belly and was then held there: seen in the app, up to 0.7 m into the ground.)
                Rest(g);
            });

            // ---- desktop input: the page forwards key state through SendToVisualScripting ----------------
            foreach (var (id, variable) in new[] { ("fw_drive", "KeyDrive"), ("fw_steer", "KeySteer"), ("fw_handbrake", "KeyHandbrake") })
            {
                g.On(g.Platform("OnJsReturnValue", ("Return ID", id)), e =>
                    g.SetGraphVar(variable, g.PlatformValue("ParseStringInvariant", "float", typeof(float),
                        ("string", g.EventOut(e, "Data", typeof(string))))));
            }

            // ---- VR input: right trigger = throttle, left trigger = brake, either stick sideways = steering,
            //      left grip = handbrake; the right stick pushed forward / pulled back also works as throttle /
            //      brake (a trigger pull is also a pointer click, which can land on a seat or a sign in view);
            //      the tank's tracks follow the two sticks' up / down ---------------------------------------
            g.On(g.Platform("OnControllerAxisUpdate"), e =>
            {
                var x = g.EventOut(e, "X Axis", typeof(float));
                var y = g.EventOut(e, "Y Axis", typeof(float));
                g.If(g.Same(g.EventOut(e, "Hand Side", typeof(int)), 1), () =>
                {
                    g.SetGraphVar("StickSteerR", x);
                    g.SetGraphVar("StickRY", y);
                }, () =>
                {
                    g.SetGraphVar("StickSteerL", x);
                    g.SetGraphVar("StickLY", y);
                });
            });
            g.On(g.Platform("OnTriggerAxisUpdate"), e =>
                g.If(g.Same(g.EventOut(e, "Hand Side", typeof(int)), 1),
                    () => g.SetGraphVar("TriggerThrottle", g.EventOut(e, "Trigger Value", typeof(float))),
                    () => g.SetGraphVar("TriggerBrake", g.EventOut(e, "Trigger Value", typeof(float)))));
            // (button 1 = grip, hand 0 = left)
            foreach (var (unit, held) in new[] { ("OnControllerButtonPressed", 1f), ("OnControllerButtonReleased", 0f) })
                g.On(g.Platform(unit), e =>
                    g.If(g.And(g.Same(g.EventOut(e, "Button Type", typeof(int)), 1), g.Same(g.EventOut(e, "Hand Side", typeof(int)), 0)), () =>
                        g.SetGraphVar("GripHandbrake", held)));

            // ---- taking the driver's seat: clicking it seats the player (the seat component does that) ----
            // (Not from the driver already in it: see SeatClicks in the aircraft graph.)
            g.On(g.Platform("OnClick", ("banterPlayerEvents", g.ObjVar("PilotSeat", GO))), _ =>
                g.If(g.And(g.And(g.Not(g.B("Dead")), g.Not(g.B("Driving"))),
                           g.Not(g.Get(Seat, "IsOccupiedByOther", g.ObjVar("PilotSeatComponent", Seat)))), () =>
                {
                    Wake(g);
                    g.SetGraphVar("Driving", true);
                    g.SetGraphVar("ResetTimer", 0f);
                    g.SetGraphVar("SeatTimer", 0.7f);
                    g.Do(Sync, "_TakeOwnership", Type.EmptyTypes, null);
                    BlockSticks(g, true);
                    Park(g);
                    Cockpit(g, true);
                }));

            // ---- crash damage (impact speed = collision impulse / mass), decided by whoever is responsible ----
            g.On(new OnCollisionEnter(), e =>
            {
                // A resting vehicle that is hit hard comes back to life (it is held still, but still feels the
                // knock). A player walking or running into it does not wake it: while it rests it cannot be
                // pushed about, and an awake, parked one could be (1.8 m in 8 s, seen in the app).
                g.If(g.Greater(g.Magnitude(g.EventOut(e, "relativeVelocity", V3T)), 6f), () => Wake(g));
                CrashDamage(e);
            });

            // (the body of the collision handler)
            void CrashDamage(OnCollisionEnter e) => g.If(g.And(g.Not(g.B("Dead")), Responsible(g)), () =>
            {
                var impact = g.Keep(g.Magnitude(g.EventOut(e, "impulse", V3T)) / g.Get(RB, "mass"));
                // a bump, a bang or a smash, by how hard it hit (SaccGroundVehicle; heard on this client only)
                void Crash(string sound) => g.Do(typeof(AudioSource), "Play", Type.EmptyTypes, g.ObjVar(sound, typeof(AudioSource)));
                g.If(g.Greater(impact, g.P("BigCrashSpeed")), () => Crash("BigCrashSound"),
                    () => g.If(g.Greater(impact, g.P("MediumCrashSpeed")), () => Crash("MediumCrashSound"),
                        () => g.If(g.Greater(impact, g.P("SmallCrashSpeed")), () => Crash("SmallCrashSound"))));
                g.If(g.Greater(impact, g.P("CrashDamageSpeed")), () =>
                {
                    var fraction = g.Clamp01((impact - g.P("CrashDamageSpeed")) / (g.P("CrashDeathSpeed") - g.P("CrashDamageSpeed")));
                    var health = g.SetGraphVar("Health", g.F("Health") - g.P("FullHealth") * fraction);
                    g.If(g.Less(health, 0.01f), () => Explode(g));
                });
            });

            // ---- everyone else learns of the explosion from a one-shot (which never returns to its sender) ----
            g.On(g.Platform("OnOneShot"), e =>
                g.If(g.And(g.Not(g.B("Dead")), g.Same(g.EventOut(e, "Data", typeof(string)), g.ObjVar("BoomMessage", typeof(string)))), () =>
                    Wreck(g, 0.5f)));

            // ---- reset sign beside the parking spot: brings back a vehicle nobody is in ---------------------
            g.On(g.Platform("OnClick", ("banterPlayerEvents", g.ObjVar("ResetSign", GO))), _ =>
                g.If(g.And(g.Not(g.B("Driving")), g.Not(g.B("Dead"))), () =>
                {
                    g.SetGraphVar("Riders", 0f);
                    g.ForEach(g.ObjVar("Seats", List), Seat, seat =>
                        g.If(g.Get(Seat, "IsOccupiedByOther", seat), () => g.SetGraphVar("Riders", g.F("Riders") + 1f)));
                    g.If(g.And(g.Less(g.F("Riders"), 0.5f), g.Not(LocalPlayerAboard(g))), () =>
                    {
                        g.Do(Sync, "_TakeOwnership", Type.EmptyTypes, null);
                        g.SetGraphVar("ResetTimer", 3f);
                    });
                }));

            // ---- every frame on every client: engine note, what bystanders see, and the timers -----------------
            g.On(new Update(), _ =>
            {
                var dt = g.Keep(g.Get(typeof(Time), "deltaTime"));

                // ---- ten times a second: is anybody using this vehicle? One that nobody is driving and that has
                //      stood still for two seconds RESTS: its wheels, engine, animator, effects and sounds all stop
                //      and its body is held where it stands, until a driver sits, something hits it, it is reset,
                //      or it starts to move. A row of parked vehicles then costs next to nothing. ----
                var soundLeft = g.SetGraphVar("SoundTimer", g.F("SoundTimer") - dt);
                var soundElapsed = g.SetGraphVar("SoundElapsed", g.F("SoundElapsed") + dt);
                g.If(g.Less(soundLeft, 0f), () =>
                {
                    g.SetGraphVar("SoundTimer", 0.1f);
                    // Back from a doze (see FlightGraphs.BuildFleetWatch): the time away is not movement, and the
                    // watch may have changed how coarsely the vehicle is drawn meanwhile.
                    g.If(g.B("Dozed"), () =>
                    {
                        g.SetGraphVar("Dozed", false);
                        g.SetGraphVar("DozeWait", 0f);
                        g.SetGraphVar("LastPosition", g.Get(TF, "position"));
                        g.SetGraphVar("LodBias", g.P("DozeLod"));
                    });
                    var position = g.Keep(g.Get(TF, "position"));
                    var moved = g.Keep(g.Distance(position, g.GraphVar("LastPosition", V3T)) / g.Max(soundElapsed, 0.001f));
                    var groundSpeed = g.Keep(g.Select(g.Greater(moved, 500f), 0f, moved));      // a jump home is not speed
                    var goingBackwards = g.Keep(g.Less(g.Dot(position - g.GraphVar("LastPosition", V3T), g.Get(TF, "forward")), 0f));
                    g.SetGraphVar("LastPosition", position);
                    g.SetGraphVar("SoundElapsed", 0f);
                    // Somebody else is driving it: the driver's seat says so, or (that word is not always there on a
                    // watching client) the vehicle is on the move while another client simulates it. The second
                    // reading is held for a few seconds.
                    // (a copy that another client simulates is kinematic here; the owner's own body never is, so a
                    // vehicle this client is responsible for rests and is reset exactly as before)
                    var drivenNow = g.And(g.Get(RB, "isKinematic"), g.Greater(groundSpeed, 1f));
                    var drivenLeft = g.SetGraphVar("DrivenTimer", g.Select(drivenNow, 4f, g.Max(g.F("DrivenTimer") - soundElapsed, 0f)));
                    var drivenByOther = g.Keep(g.And(g.Not(g.B("Driving")),
                        g.Or(g.Get(Seat, "IsOccupiedByOther", g.ObjVar("PilotSeatComponent", Seat)), g.Greater(drivenLeft, 0f))));
                    // (A RESTING vehicle cannot move under physics. One found somewhere else was put there from
                    // outside, as the app does when it sends an idle vehicle home: it rests on at the new place,
                    // so its movement is not a reason to wake it.)
                    var rolling = g.And(g.B("Awake"), g.Or(g.Greater(groundSpeed, 0.3f), g.Greater(g.Magnitude(g.Get(RB, "angularVelocity")), 0.3f)));
                    var inUse = g.Or(g.Or(g.B("Driving"), drivenByOther), g.Or(g.B("Dead"), rolling));
                    var idle = g.SetGraphVar("IdleTimer", g.F("IdleTimer") - 0.1f);
                    g.If(inUse, () => Wake(g), () => g.If(g.And(g.B("Awake"), g.Less(idle, 0f)), () => Rest(g)));

                    g.If(g.B("Awake"), () =>
                    {
                        // Only the driver's client knows the engine's revs, so everyone else shows an engine
                        // that follows how fast the vehicle is moving.
                        var running = g.Keep(g.And(g.Not(g.B("Dead")), g.Or(g.B("Driving"), drivenByOther)));
                        g.ForEach(g.ObjVar("EngineSounds", List), typeof(AudioSource), sound =>
                            g.Do(GO, "SetActive", new[] { typeof(bool) }, g.Get(typeof(AudioSource), "gameObject", sound), running));
                        g.Do(GO, "SetActive", new[] { typeof(bool) }, g.Get(typeof(AudioSource), "gameObject", g.ObjVar("TrackSound", typeof(AudioSource))), running);
                        g.If(g.Not(g.B("Driving")), () =>
                        {
                            AnimFloat(g, "revs", g.Select(drivenByOther, g.Clamp01(groundSpeed / g.P("SoundRefSpeed") * 0.75f + 0.12f), 0f));
                            AnimFloat(g, "speed", groundSpeed / 500f);
                        });
                        AnimBool(g, "occupied", g.Or(g.B("Driving"), drivenByOther));
                        AnimFloat(g, "health", g.Clamp01(g.F("Health") / g.P("FullHealth")));
                        // for a tank's tracks on a bystander's client: they run with the vehicle while somebody else drives
                        g.SetObjVar("RemoteDriven", drivenByOther);
                        g.SetObjVar("RemoteSpeed", g.Select(goingBackwards, -groundSpeed, groundSpeed));
                    });
                });

                // ---- every frame while it is awake ----
                g.If(g.B("Awake"), () =>
                {
                    // The engine sound's pitch is its object's scale, which the vehicle's animator drives from
                    // "revs" together with the volume (SGV_EffectsController).
                    g.ForEach(g.ObjVar("EngineSounds", List), typeof(AudioSource), sound =>
                        g.Set(typeof(AudioSource), "pitch", sound, g.X(g.Get(TF, "localScale", g.Get(typeof(AudioSource), "transform", sound)))));

                    // Caterpillar tracks (SGV_EffectsController): the tread texture runs and the cog wheels turn with
                    // the road wheel that drives each track; on a bystander's client, with the vehicle's speed.
                    g.If(g.ObjVar("HasTracks", typeof(bool)), () =>
                    {
                        var simulated = g.Keep(g.ObjVar("Simulate", typeof(bool)));
                        var remote = g.Keep(g.Select(g.ObjVar("RemoteDriven", typeof(bool)), g.ObjVar("RemoteSpeed", typeof(float)) / g.P("TrackCircumference"), 0f));
                        Val Track(string side)
                        {
                            var rps = g.Keep(g.Select(simulated, g.ObjVarOf(g.ObjVar("TrackWheel" + side, GO), "WheelRPS", typeof(float)), remote));
                            var run = g.SetGraphVar("TrackRot" + side, g.Call(typeof(Mathf), "Repeat", new[] { typeof(float), typeof(float) }, null,
                                g.F("TrackRot" + side) + rps * 360f * dt, 36000f));
                            g.Do(typeof(Material), "SetTextureOffset", new[] { typeof(string), typeof(Vector2) }, g.GraphVar("TrackMat" + side, typeof(Material)),
                                "_MainTex", g.V2(run * g.P("TrackUvX"), run * g.P("TrackUvY")));
                            g.ForEach(g.ObjVar("Cogs" + side, List), TF, cog =>
                                g.Set(TF, "localRotation", cog, g.Call(typeof(Quaternion), "AngleAxis", new[] { typeof(float), V3T }, null, run * g.P("CogSpeed"), Vector3.right)));
                            return rps;
                        }
                        var clatter = g.Keep(g.Abs(Track("L")) + g.Abs(Track("R")));
                        var trackSound = g.ObjVar("TrackSound", typeof(AudioSource));
                        g.Set(typeof(AudioSource), "volume", trackSound, g.Min(clatter * g.P("TrackSoundVolMulti"), g.P("TrackSoundMaxVol")));
                        g.Set(typeof(AudioSource), "pitch", trackSound, g.Min(clatter * g.P("TrackSoundPitchMulti"), g.P("TrackSoundMaxPitch")));
                    });
                });

                // ---- twice a second, resting or not ----------------------------------------------------------
                var guardLeft = g.SetGraphVar("GuardTimer", g.F("GuardTimer") - dt);
                g.If(g.Less(guardLeft, 0f), () =>
                {
                    g.SetGraphVar("GuardTimer", 0.5f);
                    // the other seats open only once there is a driver
                    if (LockOtherSeatsUntilPilot)
                    {
                        // (and never for the driver: a seat clicked on the move re-joints the player to the car)
                        var open = g.Keep(g.And(g.Not(g.B("Driving")),
                            g.Get(Seat, "IsOccupiedByOther", g.ObjVar("PilotSeatComponent", Seat))));
                        g.ForEach(g.ObjVar("PassengerSeatColliders", List), typeof(Collider), seatCollider =>
                            g.Set(typeof(Collider), "enabled", seatCollider, open));
                    }

                    // Detail by distance (as in the aircraft graph). The meshes carry Unity's automatic Mesh LODs,
                    // but the app only moves to a coarser level very late, so each vehicle adds its own bias:
                    // none within its near distance, then one level coarser for every doubling of the distance.
                    var viewer = g.Keep(g.PlatformValue("GetLocalUserState", "Position", V3T));
                    var coarser = g.Keep(g.Clamp(g.Call(typeof(Mathf), "Log", new[] { typeof(float), typeof(float) }, null,
                        g.Max(g.Distance(viewer, g.Get(TF, "position")) / g.P("LodNear"), 1f), 2f), 0f, 6f));
                    g.If(g.Greater(g.Abs(coarser - g.F("LodBias")), 0.3f), () =>
                    {
                        g.SetGraphVar("LodBias", coarser);
                        g.ForEach(g.ObjVar("LodRenderers", List), typeof(Renderer), drawn =>
                            g.Set(typeof(Renderer), "meshLodSelectionBias", drawn, coarser));
                    });

                    // Doze (see FlightGraphs.BuildFleetWatch): a vehicle that is resting, with nothing pending and
                    // no player near it or its reset sign, switches this whole graph off until the fleet watch
                    // switches it back on. Resting already holds its body and stops its wheels' graphs.
                    var settled = g.And(g.And(g.Not(g.B("Awake")), g.Not(g.B("Dead"))), g.And(g.Not(g.B("Driving")), g.Not(g.B("TestPilot"))));
                    var nothingPending = g.Less(g.Max(g.F("EjectTimer"), g.F("ResetTimer")), 0.0001f);
                    var alone = g.And(g.Greater(g.Distance(viewer, g.Get(TF, "position")), g.P("DozeBeyond")),
                        g.Greater(g.Distance(viewer, g.Get(TF, "position", g.Get(typeof(GameObject), "transform", g.ObjVar("ResetSign", typeof(GameObject))))),
                            FleetParts.SignWakeDistance + 3f));
                    g.If(g.And(g.And(settled, nothingPending), g.And(alone, g.ObjVarOf(g.ObjVar("Watch", typeof(GameObject)), "DozeOn", typeof(bool)))),
                        () => g.If(g.Greater(g.SetGraphVar("DozeWait", g.F("DozeWait") + 0.5f), 2.9f), () => FlightGraphs.Doze(g)),
                        () => g.SetGraphVar("DozeWait", 0f));
                });

                // The trip back to the arrival point waits a moment for the seat to let go of the player.
                g.If(g.Greater(g.F("EjectTimer"), 0f), () =>
                {
                    var left = g.SetGraphVar("EjectTimer", g.F("EjectTimer") - dt);
                    g.If(g.Less(left, 0.0001f), () =>
                    {
                        g.SetGraphVar("EjectTimer", 0f);
                        var home = g.ObjVar("RespawnPoint", TF);
                        g.PlatformDo("TeleportTo", ("Position", g.Get(TF, "position", home)),
                            ("Rotation", g.Y(g.Get(TF, "eulerAngles", home))), ("Stop Velocity", true), ("Is Spawn", true));
                    });
                });

                g.If(g.B("Dead"), () =>
                {
                    var left = g.SetGraphVar("RespawnTimer", g.F("RespawnTimer") - dt);
                    g.If(g.Less(left, 0f), () => Revive(g));
                });

                // Ownership arrives a moment after it is asked for; only the owner can move the vehicle.
                g.If(g.Greater(g.F("ResetTimer"), 0f), () =>
                {
                    g.SetGraphVar("ResetTimer", g.F("ResetTimer") - dt);
                    g.If(g.Call(Sync, "_DoIOwn", Type.EmptyTypes, null), () =>
                    {
                        g.SetGraphVar("ResetTimer", 0f);
                        Wake(g);
                        MoveHome(g);
                        g.SetGraphVar("Health", g.P("FullHealth"));
                    });
                });
            });

            // ---- the driver gets out (getting off a seat always teleports the player; see the aircraft graph) ----
            g.On(g.Platform("OnPlayerTeleported"), _ => g.If(g.And(g.B("Driving"), g.Not(g.B("TestPilot"))), () => Leave(g)));

            // ---- per frame while driving: controls, gearbox, cockpit ------------------------------------------
            g.On(new Update(), _ => g.If(g.B("Driving"), () =>
            {
                var dt = g.Keep(g.Get(typeof(Time), "deltaTime"));

                // A moment after sitting, the seat is slid so the driver's eyes are on the seat's design eye point.
                g.If(g.Greater(g.F("SeatTimer"), 0f), () =>
                {
                    var left = g.SetGraphVar("SeatTimer", g.F("SeatTimer") - dt);
                    g.If(g.And(g.Less(left, 0.0001f), g.Not(g.B("TestPilot"))), () =>
                        g.If(g.Less(g.F("Speed"), 3f), () =>
                        {
                            g.SetGraphVar("SeatTimer", 0f);
                            var seatTransform = g.Keep(PilotSeatTransform(g));
                            var eyes = g.PlatformValue("GetLocalUserState", "Position", V3T);
                            var shift = g.Keep(g.Call(TF, "InverseTransformDirection", Vec, null,
                                g.Get(TF, "position", g.ObjVar("EyePoint", TF)) - eyes));
                            var local = g.Keep(g.Get(TF, "localPosition", seatTransform));
                            var home = g.Keep(g.GraphVar("SeatHome", V3T));
                            Val Fit(Func<Val, Val> axis) => g.Clamp(axis(local) + axis(shift), axis(home) - 0.6f, axis(home) + 0.6f);
                            g.Set(TF, "localPosition", seatTransform, g.V3(Fit(g.X), Fit(g.Y), Fit(g.Z)));
                        }, () => g.SetGraphVar("SeatTimer", 0.5f)));
                });

                var velocity = g.Keep(g.Get(RB, "linearVelocity"));
                var speed = g.Keep(g.Magnitude(velocity));
                var handBrake = g.SetGraphVar("HandBrake", g.Clamp01(g.F("KeyHandbrake") + g.F("GripHandbrake")));

                g.If(g.ObjVar("TankMode", typeof(bool)), () =>
                {
                    // -- tank: each track has its own throttle (SaccGroundVehicle, TankMode with W A S D) --
                    var backwards = g.Less(g.Dot(velocity, g.Get(TF, "forward")), -0.5f);
                    var steer = g.Keep(g.F("KeySteer") * g.Select(backwards, -1f, 1f));        // steering reverses with the tank
                    var left = g.SetGraphVar("TrackL", g.Clamp((g.F("KeyDrive") + steer) * 2f + g.F("StickLY"), -1f, 1f));
                    var right = g.SetGraphVar("TrackR", g.Clamp((g.F("KeyDrive") - steer) * 2f + g.F("StickRY"), -1f, 1f));
                    g.SetGraphVar("ThrottleInput", left * 0.5f + 0.5f);        // (what the animator shows)
                    g.SetGraphVar("YawInput", right);
                    g.SetGraphVar("BrakeInput", 0f);
                    g.SetGraphVar("FinalThrottle", g.Abs(left) + g.Abs(right));
                    g.SetGraphVar("GearRatioL", g.F("GearRatio") * left * g.Select(g.Less(left, 0f), g.P("TANK_ReverseSpeed"), 1f));
                    g.SetGraphVar("GearRatioR", g.F("GearRatio") * right * g.Select(g.Less(right, 0f), g.P("TANK_ReverseSpeed"), 1f));
                }, () =>
                {
                    // -- car: throttle and brake; holding the brake at a standstill selects reverse, where the
                    //    two pedals swap (the original has a reverse toggle on a key / stick flick instead) --
                    var throttleRaw = g.Keep(g.Clamp01(g.Max(g.F("KeyDrive"), 0f) + g.F("TriggerThrottle") + g.Max(g.F("StickRY"), 0f)));
                    var brakeRaw = g.Keep(g.Clamp01(g.Max(-g.F("KeyDrive"), 0f) + g.F("TriggerBrake") + g.Max(-g.F("StickRY"), 0f)));
                    var slow = g.Keep(g.Less(speed, 1.5f));
                    g.If(g.B("Reversing"), () =>
                        g.If(g.And(g.And(g.Greater(throttleRaw, 0.5f), g.Less(brakeRaw, 0.1f)), slow), () => SetReversing(g, false)),
                    () =>
                        g.If(g.And(g.And(g.Greater(brakeRaw, 0.5f), g.Less(throttleRaw, 0.1f)), slow), () =>
                        {
                            var held = g.SetGraphVar("ReverseTimer", g.F("ReverseTimer") + dt);
                            g.If(g.Greater(held, 0.35f), () => SetReversing(g, true));
                        }, () => g.SetGraphVar("ReverseTimer", 0f)));
                    g.SetGraphVar("ThrottleInput", g.Select(g.B("Reversing"), brakeRaw, throttleRaw));
                    g.SetGraphVar("BrakeInput", g.Select(g.B("Reversing"), throttleRaw, brakeRaw));
                    g.SetGraphVar("FinalThrottle", g.F("ThrottleInput"));
                    g.SetGraphVar("GearRatioL", g.F("GearRatio"));
                    g.SetGraphVar("GearRatioR", g.F("GearRatio"));

                    // -- steering (SaccGroundVehicle): less lock the faster you go, but always enough to
                    //    counter-steer into a slide; keys ramp the wheel, a thumbstick sets it directly --
                    var slide = g.Keep(g.Call(V3T, "SignedAngle", new[] { V3T, V3T, V3T }, null, g.Get(TF, "forward"),
                        g.ProjectOnPlane(velocity, g.Get(TF, "up")), g.Get(TF, "up")));
                    var autoSteer = g.Keep(g.Select(g.Greater(g.Abs(slide), 110f), 0f, g.Clamp(slide / g.P("SteeringDegrees"), -1f, 1f)));
                    var unlimited = g.Keep(g.Or(g.ObjVar("SteeringMaxSpeedDTDisabled", typeof(bool)), g.Greater(handBrake, 0f)));
                    var original = g.Clamp(1f - speed / g.P("SteeringMaxSpeedDT"), g.P("DesktopMinSteering"), 1f);
                    // (the lock at which a car of this wheelbase turns with the set sideways acceleration: atan(wheelbase x a / v^2))
                    var gentle = g.Call(typeof(Mathf), "Atan", new[] { typeof(float) }, null,
                        g.P("Wheelbase") * g.P("SteerAssistAccel") / g.Max(speed * speed, 0.01f)) * 57.29578f / g.P("SteeringDegrees");
                    var bySpeed = g.Keep(SteeringAssist ? g.Min(original, g.Max(gentle, 0.02f)) : original);
                    var limitUpper = g.Keep(g.Select(unlimited, 1f,
                        g.Select(g.Less(autoSteer, 0f), bySpeed, g.Max(bySpeed, autoSteer + g.P("DesktopMinSteering")))));
                    var limitLower = g.Keep(g.Select(unlimited, -1f,
                        g.Select(g.Less(autoSteer, 0f), g.Min(-bySpeed, autoSteer - g.P("DesktopMinSteering")), -bySpeed)));
                    var stick = g.Keep(g.Clamp(g.F("StickSteerL") + g.F("StickSteerR"), -1f, 1f));
                    var keys = g.Keep(g.F("KeySteer"));
                    g.If(g.Greater(g.Abs(stick), 0.05f), () =>
                        g.SetGraphVar("YawInput", g.Select(g.Greater(stick, 0f), stick * limitUpper, -stick * limitLower)),
                    () => g.If(g.Greater(g.Abs(keys), 0.01f), () =>
                    {
                        var turned = g.SetGraphVar("YawInput", g.Clamp(g.F("YawInput") + keys * dt / g.P("SteeringKeyboardSecsToMax"), limitLower, limitUpper));
                        g.If(g.Less(keys * turned, 0f), () =>
                            g.SetGraphVar("YawInput", g.MoveTowards(turned, 0f, dt / g.P("SteeringReturnSpeedDT"))));
                    }, () =>
                        g.If(g.ObjVar("Drift_AutoSteer", typeof(bool)),
                            () => g.SetGraphVar("YawInput", g.Lerp(g.F("YawInput"), autoSteer, g.Blend(speed * g.P("AutoSteerStrength"), dt))),
                            () => g.SetGraphVar("YawInput", g.MoveTowards(g.F("YawInput"), 0f, dt / g.P("SteeringReturnSpeedDT"))))));
                    g.SetGraphVar("YawInput", g.Clamp(g.F("YawInput"), -1f, 1f));
                });

                // -- automatic gearbox (SGV_GearBox): up near the top of the rev range, down near the bottom,
                //    with the clutch pulled for a moment at each change --
                var since = g.SetGraphVar("SinceGearChange", g.F("SinceGearChange") + dt);
                var gear = g.Keep(g.F("Gear"));
                var inNeutral = g.Keep(g.Less(g.Abs(gear - g.P("NeutralGear")), 0.5f));
                var inMax = g.Keep(g.Greater(gear, g.P("TopGear") - 0.5f));
                var inMin = g.Keep(g.Less(gear, 0.5f));
                var normRevs = g.Keep(g.F("Revs") / g.P("RevLimiter"));
                var clutchOverride = g.Keep(g.Select(g.Or(
                        g.And(g.ObjVar("HandBrakeAutoClutch", typeof(bool)), g.Greater(handBrake, 0f)),
                        g.And(g.ObjVar("BrakeAutoClutch", typeof(bool)), g.Greater(g.F("BrakeInput"), 0f))), 1f, 0f));
                g.If(g.Or(g.Greater(since, g.P("AutomaticGearChangeDelay")), inNeutral), () =>
                {
                    var reversing = g.B("Reversing");
                    var revving = g.Keep(g.Greater(normRevs, g.P("GearChangeRevsUpper")));
                    var lugging = g.Keep(g.Less(normRevs, g.P("GearChangeRevsLower")));
                    var pullingAway = g.Keep(g.And(inNeutral, g.Greater(normRevs, g.P("GearChangeRevsUpperNeutral"))));
                    // One departure from the original: no change up while the driven wheels are spinning (the
                    // vehicle is doing less than 70% of the speed this gear gives at these revs). The original's
                    // automatic mode changes up on revs alone, so with the throttle floored - all a key or a
                    // trigger pulled home can do - it runs up the gears on wheelspin and the powerful rear-drive
                    // cars spin round. Its own cars are set up for changing gear by hand.
                    var hookedUp = g.Greater(speed, g.F("Revs") * g.Abs(g.F("GearRatio")) / 60f * g.P("DriveCircumference") * 0.7f);
                    var up = g.Or(
                        g.And(g.Not(reversing), g.Or(g.And(g.And(g.Not(inMax), revving), hookedUp), pullingAway)),
                        g.And(g.And(reversing, g.Not(inNeutral)), lugging));
                    var down = g.Or(
                        g.And(g.And(g.Not(reversing), g.And(g.Not(inNeutral), g.Not(inMin))), lugging),
                        g.And(reversing, g.Or(g.And(g.Not(inMin), revving), pullingAway)));
                    g.If(g.And(g.And(up, g.Not(inMax)), g.Less(clutchOverride, 0.5f)),
                        () => SetGear(g, gear + 1f, "gearup"),
                        () => g.If(g.And(down, g.Not(inMin)), () => SetGear(g, gear - 1f, "geardown")));
                });
                var autoClutch = g.SetGraphVar("AutoClutch", g.Max(g.F("AutoClutch") - g.P("ClutchDecaySpeed") * dt, 0f));
                g.SetGraphVar("Clutch", g.Max(clutchOverride, g.Min(1f, autoClutch)));

                // -- what the driver sees: steering wheel, pedals, rev counter (the vehicle's animator) --
                AnimFloat(g, "steering", g.F("YawInput") * 0.5f + 0.5f);
                AnimFloat(g, "throttle", g.F("ThrottleInput"));
                AnimFloat(g, "revs", normRevs);
                AnimFloat(g, "brake", g.F("BrakeInput"));
                AnimFloat(g, "handbrake", handBrake);
                AnimFloat(g, "speed", speed / 500f);

                var timer = g.SetGraphVar("HudTimer", g.F("HudTimer") + dt);
                g.If(g.Greater(timer, 0.25f), () =>
                {
                    g.SetGraphVar("HudTimer", 0f);

                    // Safety net for a driver who left without a teleport: only trusted when nearly stopped.
                    var head = g.PlatformValue("GetLocalUserState", "Position", V3T);
                    var seat = g.Get(TF, "position", PilotSeatTransform(g));
                    g.If(g.And(g.And(g.Not(g.B("TestPilot")), g.Less(speed, 5f)), g.Greater(g.Distance(head, seat), 2.5f)),
                        () => Leave(g));

                    // Gauges (SGV_Gauges): speed and gear.
                    var format = new[] { typeof(string), typeof(object), typeof(object), typeof(object) };
                    Type uiText = typeof(UnityEngine.UI.Text);
                    g.Set(uiText, "text", g.ObjVar("SpeedText", uiText),
                        g.Call(typeof(string), "Format", format, null, "{0:F0}", speed * g.P("SpeedMultiplier"), 0f, 0f));
                    var shown = g.Keep(g.F("Gear") - g.P("NeutralGear"));
                    g.Set(uiText, "text", g.ObjVar("GearText", uiText), g.Select(g.Less(shown, -0.5f), "R",
                        g.Select(g.Less(shown, 0.5f), "N", g.Call(typeof(string), "Format", format, null, "{0:F0}", shown, 0f, 0f), typeof(string)), typeof(string)));
                });
            }));

            // ---- physics step: the engine, and what the wheels need to know -------------------------------
            g.On(new FixedUpdate(), _ => g.If(g.B("Awake"), () =>
            {
                // Whoever is responsible for the vehicle runs its wheels, driver or not: an empty car that is
                // still rolling stands on its suspension on its owner's client. Nobody else does any wheel
                // work: the wheels' graphs are switched on only here, and only while this is true.
                // (Never on a kinematic body: that is a copy another client simulates, whatever this client
                // believes about owning it for the moment, and writing a velocity to it is an error.)
                var simulate = g.Keep(g.And(g.And(g.Not(g.B("Dead")), Responsible(g)), g.Not(g.Get(RB, "isKinematic"))));
                g.SetObjVar("Simulate", simulate);
                g.If(g.And(simulate, g.Not(g.B("WheelsOn"))), () => WheelsRun(g, true));
                g.If(g.And(g.Not(simulate), g.B("WheelsOn")), () => WheelsRun(g, false));
                g.If(simulate, () =>
                {
                    var dt = g.Keep(g.Get(typeof(Time), "fixedDeltaTime"));
                    var velocity = g.Keep(g.Get(RB, "linearVelocity"));
                    var speed = g.SetGraphVar("Speed", g.Magnitude(velocity));
                    g.SetObjVar("VehicleSpeed", speed);
                    var used = g.Keep(g.ObjVar("EngineForceUsed", typeof(float)));          // the load the wheels put on the engine last step
                    g.SetObjVar("EngineForceUsed", 0f);

                    g.If(g.B("Driving"), () =>
                    {
                        // -- engine (SaccGroundVehicle.RevUp): the original runs 1000 small steps a second with the
                        //    wheels' load applied half-way through a physics step; here two half steps --
                        // idle: a little throttle of its own keeps the engine turning over
                        var asked = g.F("FinalThrottle");
                        var idling = g.And(g.Less(asked, g.P("MinThrottle")), g.Less(g.F("Revs") / g.P("RevLimiter"), g.P("MinThrottle")));
                        var throttle = g.Keep(g.Select(idling, (g.P("MinThrottle") - asked) * g.P("MinThrottle_PStrength"), asked));
                        var half = g.Keep(dt * 0.5f);
                        void RevUp()
                        {
                            g.SetGraphVar("Revs", g.Max(g.Lerp(g.F("Revs"), 0f, g.Blend(g.P("EngineSlowDown"), half)), 0f));
                            g.If(g.Not(g.B("LimitingRev")), () =>
                            {
                                var response = g.Call(typeof(AnimationCurve), "Evaluate", new[] { typeof(float) },
                                    g.ObjVar("EngineResponseCurve", typeof(AnimationCurve)), g.F("Revs") / g.P("RevLimiter"));
                                var revs = g.SetGraphVar("Revs", g.F("Revs") + throttle * g.P("DriveSpeed") * half * response);
                                g.If(g.Greater(revs, g.P("RevLimiter")), () =>
                                {
                                    g.SetGraphVar("Revs", g.P("RevLimiter"));
                                    g.SetGraphVar("LimitingRev", true);
                                    g.SetGraphVar("LimiterTimer", g.P("RevLimiterDelay"));
                                });
                            });
                        }
                        RevUp();
                        g.SetGraphVar("Revs", g.Max(g.F("Revs") - used, 0f));
                        RevUp();
                    }, () =>
                        g.SetGraphVar("Revs", g.Max(g.Lerp(g.F("Revs"), 0f, g.Blend(g.P("EngineSlowDown"), dt)), 0f)));

                    // the rev limiter cuts the engine for a moment, then lets it back in once the revs have dropped
                    g.If(g.B("LimitingRev"), () =>
                    {
                        var left = g.SetGraphVar("LimiterTimer", g.F("LimiterTimer") - dt);
                        g.If(g.Less(left, 0f), () =>
                            g.If(g.Less(g.F("Revs"), g.P("RevLimiter")),
                                () => g.SetGraphVar("LimitingRev", false),
                                () => g.SetGraphVar("LimiterTimer", g.P("RevLimiterDelay"))));
                    });

                    // -- what the wheels read. Nobody driving: out of gear, parking brake on. --
                    var driving = g.Keep(g.B("Driving"));
                    g.SetObjVar("Revs", g.F("Revs"));
                    g.SetObjVar("GearRatioL", g.Select(driving, g.F("GearRatioL"), 0f));
                    g.SetObjVar("GearRatioR", g.Select(driving, g.F("GearRatioR"), 0f));
                    g.SetObjVar("Clutch", g.Select(driving, g.F("Clutch"), 1f));
                    g.SetObjVar("BrakeInput", g.Select(driving, g.F("BrakeInput"), 0f));
                    // Standing still with the driver off the pedals it is held on the handbrake, so it cannot creep.
                    var held = g.And(g.Less(g.F("FinalThrottle"), 0.01f), g.Less(speed, 0.3f));
                    g.SetObjVar("HandBrakeInput", g.Select(driving, g.Max(g.F("HandBrake"), g.Select(held, 1f, 0f)), g.P("ParkBrake")));

                    // -- air drag --
                    g.Set(RB, "linearVelocity", null, g.Lerp3(velocity, Vector3.zero, g.Blend(g.P("Drag"), dt)));
                });
            }));
            return g;
        }

        /// <summary>True on the client that should decide this vehicle's fate.</summary>
        static Val Responsible(GraphWriter g) =>
            g.Or(g.Or(g.B("Driving"), g.B("TestPilot")), g.Call(Sync, "_DoIOwn", Type.EmptyTypes, null));

        static Val PilotSeatTransform(GraphWriter g) => g.Get(GO, "transform", g.ObjVar("PilotSeat", GO));

        static Val Anim(GraphWriter g) => g.ObjVar("VehicleAnimator", AN);
        static void AnimFloat(GraphWriter g, string name, object value) =>
            g.Do(AN, "SetFloat", new[] { typeof(string), typeof(float) }, Anim(g), name, value);
        static void AnimBool(GraphWriter g, string name, object value) =>
            g.Do(AN, "SetBool", new[] { typeof(string), typeof(bool) }, Anim(g), name, value);
        static void AnimTrigger(GraphWriter g, string name) =>
            g.Do(AN, "SetTrigger", new[] { typeof(string) }, Anim(g), name);

        /// <summary>Seconds a vehicle must stand empty and still before it rests.</summary>
        const float RestDelay = 2f;

        /// <summary>Everything running again: body free, animator, effects and sounds on. Harmless when they already are.</summary>
        static void Wake(GraphWriter g)
        {
            g.SetGraphVar("IdleTimer", RestDelay);
            g.If(g.Not(g.B("Awake")), () =>
            {
                g.SetGraphVar("Awake", true);
                g.If(g.Not(g.B("Dead")), () => g.Set(RB, "constraints", null, RigidbodyConstraints.None));
                g.Set(RB, "interpolation", null, RigidbodyInterpolation.Interpolate);
                g.Do(RB, "WakeUp", Type.EmptyTypes, null);
                g.ForEach(g.ObjVar("RestingObjects", List), GO, item => g.Do(GO, "SetActive", new[] { typeof(bool) }, item, true));
                g.Set(AN, "enabled", Anim(g), true);
            });
        }

        /// <summary>
        /// Parked, empty and still: stop everything. The body is held with frozen constraints (the synced-object
        /// component re-applies isKinematic every frame, so that cannot be used), which also stands in for the
        /// suspension that no longer runs; the wheels' graphs, the animator and the effects and sounds are
        /// switched off; and this graph's physics step and per-frame work stop at their first node.
        /// </summary>
        static void Rest(GraphWriter g)
        {
            g.SetGraphVar("Awake", false);
            g.SetObjVar("Simulate", false);
            WheelsRun(g, false);
            g.Set(RB, "constraints", null, RigidbodyConstraints.FreezeAll);
            // No interpolation while it rests: a frozen, interpolated body puts its transform back where the body
            // is, which undid the app's own reset of an idle vehicle (it moves the transform). Without it, a
            // move from outside is simply the vehicle's new place. No pose is remembered or re-applied here.
            g.Set(RB, "interpolation", null, RigidbodyInterpolation.None);
            g.Set(AN, "enabled", Anim(g), false);
            g.ForEach(g.ObjVar("RestingObjects", List), GO, item => g.Do(GO, "SetActive", new[] { typeof(bool) }, item, false));
        }

        /// <summary>Switch every wheel's own graph on or off.</summary>
        static void WheelsRun(GraphWriter g, bool on)
        {
            g.SetGraphVar("WheelsOn", on);
            g.ForEach(g.ObjVar("WheelMachines", List), typeof(ScriptMachine), wheel => g.Set(typeof(ScriptMachine), "enabled", wheel, on));
        }

        /// <summary>A gear change: new ratio, clutch in for a moment, and the cockpit told.</summary>
        static void SetGear(GraphWriter g, object newGear, string trigger)
        {
            var gear = g.SetGraphVar("Gear", newGear, typeof(float));
            var index = g.Keep(g.RoundToInt(gear));
            g.SetGraphVar("GearRatio", g.ListItem(g.ObjVar("GearRatios", typeof(System.Collections.IList)), index, typeof(float)) * g.P("FinalDrive"));
            g.SetGraphVar("SinceGearChange", 0f);
            g.SetGraphVar("AutoClutch", g.P("AutoClutchStart"));
            g.Do(AN, "SetInteger", new[] { typeof(string), typeof(int) }, Anim(g), "currentgear", index);
            if (trigger != null) AnimTrigger(g, trigger);
        }

        /// <summary>Switch the automatic gearbox between going forwards and going backwards, through neutral.</summary>
        static void SetReversing(GraphWriter g, bool reversing)
        {
            g.SetGraphVar("Reversing", reversing);
            g.SetGraphVar("ReverseTimer", 0f);
            SetGear(g, g.P("NeutralGear"), null);
            g.SetGraphVar("SinceGearChange", 1000f);           // free to pick a gear straight away
        }

        /// <summary>Controls released, out of gear. (With nobody driving the wheels get the parking brake.)</summary>
        static void Park(GraphWriter g)
        {
            foreach (var name in new[] { "ThrottleInput", "BrakeInput", "HandBrake", "YawInput", "FinalThrottle", "TrackL", "TrackR",
                                         "GearRatioL", "GearRatioR", "AutoClutch", "Clutch", "ReverseTimer", "Revs" })
                g.SetGraphVar(name, 0f);
            g.SetGraphVar("GearRatio", g.P("NeutralRatio"));       // (nought for a car; a tank's only gear)
            g.SetGraphVar("Reversing", false);
            g.SetGraphVar("LimitingRev", false);
            g.SetGraphVar("Gear", g.P("NeutralGear"), typeof(float));
            g.SetGraphVar("SinceGearChange", 1000f);
        }

        /// <summary>The local player stops being the driver: controls back to walking, vehicle parked.</summary>
        static void Leave(GraphWriter g)
        {
            g.SetGraphVar("Driving", false);
            BlockSticks(g, false);
            Park(g);
            Unpiloted(g);
            Cockpit(g, false);
        }

        /// <summary>Nobody is driving: the driver's seat goes back to its authored place.</summary>
        static void Unpiloted(GraphWriter g)
        {
            g.SetGraphVar("SeatTimer", 0f);
            g.Set(TF, "localPosition", PilotSeatTransform(g), g.GraphVar("SeatHome", V3T));
        }

        /// <summary>What only the person driving sees and hears (gauges, the "enable in vehicle" objects), and the animator's driver state.</summary>
        static void Cockpit(GraphWriter g, bool aboard)
        {
            g.ForEach(g.ObjVar("InVehicleObjects", List), GO, item =>
                g.Do(GO, "SetActive", new[] { typeof(bool) }, item, aboard));
            AnimBool(g, "piloting", aboard);
            AnimBool(g, "insidevehicle", aboard);
            AnimBool(g, "occupied", aboard);
            // the driver cannot click this car's seats (passenger seats: the twice-a-second guard)
            g.Set(typeof(Collider), "enabled", g.ObjVar("PilotSeatCollider", typeof(Collider)), !aboard);
            if (aboard)
                g.ForEach(g.ObjVar("PassengerSeatColliders", List), typeof(Collider), seatCollider =>
                    g.Set(typeof(Collider), "enabled", seatCollider, false));
            if (!aboard)
            {
                AnimFloat(g, "steering", 0.5f);
                AnimFloat(g, "throttle", 0f);
                AnimFloat(g, "revs", 0f);
                AnimFloat(g, "brake", 0f);
                AnimFloat(g, "handbrake", 0f);
            }
        }

        /// <summary>True when the local player is inside (or right beside) this vehicle. Not for the editor test.</summary>
        static Val LocalPlayerAboard(GraphWriter g) =>
            g.And(g.Not(g.B("TestPilot")),
                g.Less(g.Distance(g.PlatformValue("GetLocalUserState", "Position", V3T), g.Get(TF, "position")), g.P("AboardRadius")));

        /// <summary>The vehicle is destroyed. Runs once, on the client responsible for it (see the aircraft graph).</summary>
        static void Explode(GraphWriter g)
        {
            Wake(g);
            var wasDriver = g.Keep(g.And(g.B("Driving"), g.Not(g.B("TestPilot"))));
            g.If(g.B("Driving"), () =>
            {
                BlockSticks(g, false);
                Cockpit(g, false);
            });
            g.SetGraphVar("Driving", false);
            g.SetGraphVar("ResetTimer", 0f);
            Park(g);
            Unpiloted(g);
            StopBody(g);
            g.Set(RB, "constraints", null, RigidbodyConstraints.FreezeAll);
            Wreck(g, 0f, wasDriver);
            g.PlatformDo("SendOneShot", ("Data", g.ObjVar("BoomMessage", typeof(string))), ("All Instances", false));
        }

        /// <summary>
        /// What every client does when the vehicle blows up: the vehicle's animator plays its destruction
        /// (explosion on; body, seats, attached effects and sounds off), others can pass through where it
        /// was, and the local player gets out if they were aboard.
        /// </summary>
        static void Wreck(GraphWriter g, float extraDelay, Val knownAboard = null)
        {
            Wake(g);
            g.SetGraphVar("Dead", true);
            g.SetGraphVar("RespawnTimer", g.P("RespawnDelay") + extraDelay);
            AnimFloat(g, "revs", 0f);
            AnimTrigger(g, "explode");
            AnimBool(g, "dead", true);
            g.Do(typeof(AudioSource), "Play", Type.EmptyTypes, g.ObjVar("ExplosionSound", typeof(AudioSource)));
            g.Set(RB, "detectCollisions", null, false);
            g.If(knownAboard != null ? g.Or(knownAboard, LocalPlayerAboard(g)) : LocalPlayerAboard(g), () =>
            {
                g.ForEach(g.ObjVar("Seats", List), Seat, seat =>
                    g.If(g.Not(g.Get(Seat, "IsOccupiedByOther", seat)), () => g.Do(Seat, "_Stand", Type.EmptyTypes, seat)));
                g.SetGraphVar("EjectTimer", 0.3f);
            });
        }

        /// <summary>The wreck's time is up: the owner moves it home, and everyone shows it again.</summary>
        static void Revive(GraphWriter g)
        {
            Wake(g);
            g.If(g.Or(g.B("TestPilot"), g.Call(Sync, "_DoIOwn", Type.EmptyTypes, null)), () => MoveHome(g));
            g.Set(RB, "detectCollisions", null, true);
            g.SetGraphVar("Health", g.P("FullHealth"));
            g.SetGraphVar("Dead", false);
            AnimBool(g, "dead", false);
        }

        /// <summary>Owner only: put the vehicle back on its parking spot, at rest, engine off.</summary>
        static void MoveHome(GraphWriter g)
        {
            g.Set(RB, "constraints", null, RigidbodyConstraints.None);
            g.Do(TF, "SetPositionAndRotation", new[] { V3T, typeof(Quaternion) }, null,
                g.GraphVar("SpawnPosition", V3T), g.GraphVar("SpawnRotation", typeof(Quaternion)));
            g.Set(RB, "position", null, g.GraphVar("SpawnPosition", V3T));
            g.Set(RB, "rotation", null, g.GraphVar("SpawnRotation", typeof(Quaternion)));
            StopBody(g);
            g.Do(RB, "WakeUp", Type.EmptyTypes, null);
            Park(g);
        }

        /// <summary>Zero the body's velocities, where this client simulates it (a kinematic copy has none to set).</summary>
        static void StopBody(GraphWriter g)
        {
            g.If(g.Not(g.Get(RB, "isKinematic")), () =>
            {
                g.Set(RB, "linearVelocity", null, Vector3.zero);
                g.Set(RB, "angularVelocity", null, Vector3.zero);
            });
        }

        /// <summary>While driving, the thumbsticks steer the vehicle instead of moving or turning the player.</summary>
        static void BlockSticks(GraphWriter g, bool block)
        {
            g.PlatformDo("SetBlockLeftThumbstick", ("Block Left Thumbstick", block));
            g.PlatformDo("SetBlockRightThumbstick", ("Block Right Thumbstick", block));
        }

        // =====================================================================================================
        //  One wheel (SaccWheel)
        // =====================================================================================================
        public static GraphWriter BuildWheel()
        {
            var g = new GraphWriter();
            g.Declare("Grounded", false);
            g.Declare("Skidding", false);       // the tyre's squeal and smoke are on
            foreach (var name in new[] { "CompressionLast", "Surf", "RPM", "RPS", "Rotation", "ForwardSpeed", "ForwardSlip", "ForceUsed", "SkidLength" })
                g.Declare(name, 0f);
            g.Declare("Grip3", Vector3.zero);

            Type hitType = typeof(RaycastHit);
            var force = new[] { V3T, V3T, typeof(ForceMode) };

            g.On(new FixedUpdate(), _ =>
            {
                var vehicle = g.ObjVar("Vehicle", GO);
                g.If(g.ObjVarOf(vehicle, "Simulate", typeof(bool)), () =>
                {
                    var dt = g.Keep(g.Get(typeof(Time), "fixedDeltaTime"));
                    var body = g.ObjVar("Body", RB);
                    var wheelPoint = g.ObjVar("WheelPoint", TF);
                    var up = g.Keep(g.Get(TF, "up", wheelPoint));
                    var origin = g.Keep(g.Get(TF, "position", wheelPoint));
                    var extra = g.P("ExtraRayCastDistance");
                    var travel = g.P("SuspensionDistance");
                    var radius = g.P("WheelRadius");

                    // ---- the engine's pull on the wheel (WheelPhysics, first part) ----
                    var ratio = g.Keep(g.Select(g.ObjVar("RightTrack", typeof(bool)),
                        g.ObjVarOf(vehicle, "GearRatioR", typeof(float)), g.ObjVarOf(vehicle, "GearRatioL", typeof(float))));
                    var clutch = g.Keep(g.ObjVarOf(vehicle, "Clutch", typeof(float)));
                    var tank = g.ObjVar("TankMode", typeof(bool));
                    // driven by the engine: a drive wheel that is in gear (a tank's tracks always are)
                    var driven = g.Keep(g.And(g.ObjVar("IsDriveWheel", typeof(bool)), g.Or(tank, g.Greater(g.Abs(ratio), 0.0000001f))));
                    var circumference = g.Keep(g.P("WheelCircumference"));
                    var treadBefore = g.Keep(g.F("Surf"));
                    g.If(driven, () =>
                    {
                        // the wheel is pulled towards the engine's speed through the clutch
                        var rpm = g.SetGraphVar("RPM", g.Lerp(g.F("RPM"), g.ObjVarOf(vehicle, "Revs", typeof(float)) * ratio,
                            1f - g.Pow(0.5f, (1f - clutch) * g.P("ClutchStrength"))));
                        g.SetGraphVar("Surf", circumference * rpm / 60f);
                    });
                    var surface = g.Keep(g.F("Surf"));

                    // ---- suspension: a ray down the wheel's travel; the more it is compressed the harder it pushes ----
                    var cast = g.DoOut(typeof(Physics), "Raycast",
                        new[] { V3T, V3T, hitType.MakeByRefType(), typeof(float), typeof(int), typeof(QueryTriggerInteraction) }, null,
                        origin + g.Scale(up, extra), -up, null, travel + extra, UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    g.If(g.Result(cast), () =>
                    {
                        var hit = g.OutParam(cast, 2, hitType);
                        var point = g.Keep(g.Get(hitType, "point", hit));
                        var normal = g.Keep(g.Get(hitType, "normal", hit));
                        var distance = g.Keep(g.Get(hitType, "distance", hit));
                        // The wheel's speed over the ground is the body's velocity at the contact point. (The original
                        // differences the contact point's position from step to step, so that it also works on moving
                        // platforms. This world's airfield is 15 km from the origin, where positions are only good to
                        // a millimetre: at 144 steps a second that is 14 cm/s of noise in every wheel, which the tyres
                        // then fight - a parked car's heading crept by 0.3 degrees a second.)
                        var pointVelocity = g.Keep(g.Call(RB, "GetPointVelocity", Vec, body, point));
                        g.SetGraphVar("Grounded", true);

                        // straight up when nearly stopped, so the vehicle can stand on a slope
                        var push = g.Keep(g.Select(g.Less(g.Angle(normal, Vector3.up), g.P("MaxParkingIncline")),
                            g.LerpVector(Vector3.up, normal, g.ObjVarOf(vehicle, "VehicleSpeed", typeof(float))), normal, V3T));
                        var compression = g.Keep(1f - (distance - extra) / travel);
                        var change = g.Keep(compression - g.F("CompressionLast"));
                        var damping = change * g.Select(g.Greater(change, 0f), g.P("Damping_Bump"), g.P("Damping_Rebound"));
                        g.SetGraphVar("CompressionLast", compression);
                        var raw = g.Keep(g.Scale(push, compression * g.P("SpringForceMulti") * dt + damping));
                        var limit = g.Keep(g.P("MaxSusForce") * dt);
                        var susForce = g.Keep(g.Select(g.Greater(g.Magnitude(raw), limit), g.Scale(g.Normalized(raw), limit), raw, V3T));
                        g.If(g.Greater(g.Dot(up, susForce), 0f), () =>       // never pull the vehicle down
                            g.Do(RB, "AddForceAtPosition", force, body, susForce, origin, ForceMode.VelocityChange));

                        // the wheel you see sits on the ground
                        var rides = g.Keep(g.Greater(distance, extra));
                        g.Set(TF, "position", g.ObjVar("WheelVisual", TF), g.Select(rides, point, origin, V3T) + g.Scale(up, radius));
                        g.If(g.ObjVar("HasVisualGround", typeof(bool)), () =>
                            g.Set(TF, "position", g.ObjVar("WheelVisualGround", TF), g.Select(rides, point, origin, V3T)));

                        // ---- the tyre on the ground (WheelPhysics) ----
                        var wheelForward = g.Keep(g.Get(TF, "forward", wheelPoint));
                        var wheelRight = g.Keep(g.Get(TF, "right", wheelPoint));
                        // the wheel's velocity along its own rolling direction, on the ground's surface
                        var rolling0 = g.Keep(g.ProjectOnPlane(pointVelocity, wheelRight));
                        var rolling = g.Keep(rolling0 - g.Project(rolling0, g.Normalized(g.ProjectOnPlane(normal, wheelRight))));
                        var rollingSpeed = g.Keep(g.Magnitude(rolling));
                        var forwardSpeed = g.SetGraphVar("ForwardSpeed", g.Select(g.Less(g.Dot(rolling, wheelForward), 0f), -rollingSpeed, rollingSpeed));
                        // slip: the difference between the tread's speed and the ground's
                        var slip = g.SetGraphVar("ForwardSlip", forwardSpeed - surface);
                        var forwardSkid = g.Keep(g.Scale(g.Normalized(g.ProjectOnPlane(wheelForward, normal)), slip));
                        var sideSkid = g.Keep(g.ProjectOnPlane(g.ProjectOnPlane(pointVelocity, rolling), normal));
                        g.SetGraphVar("SkidLength", g.Magnitude(g.Scale(wheelForward, treadBefore - forwardSpeed) + sideSkid));
                        var fullSkid = g.Keep(sideSkid + forwardSkid);
                        var fullMag = g.Keep(g.Magnitude(fullSkid));
                        // how much of the skid is along the wheel
                        var forwardShare = g.Keep(g.Select(g.Greater(fullMag, 0f), g.Dot(forwardSkid, fullSkid) / (fullMag * fullMag), 0f));
                        // grip is limited by the weight on the wheel
                        var maxGrip = g.Keep(g.Max(g.Magnitude(susForce) / dt * g.P("CurrentGrip"), 0.000001f));
                        var maxGripSide = g.Keep(g.Max(maxGrip * g.P("LateralGrip"), 0.000001f));
                        var lean = g.Keep(g.Max(g.Pow(g.Dot(g.Get(TF, "up"), normal), g.P("WheelRollGrip_Power")), 0.3f));
                        Type curve = typeof(AnimationCurve);
                        Val Grip(string curveName, Val skid, Val most) =>
                            g.Call(curve, "Evaluate", new[] { typeof(float) }, g.ObjVar(curveName, curve), skid / most) * most;

                        g.If(g.ObjVar("SeparateLongLatGrip", typeof(bool)), () =>
                        {
                            // along and across the wheel worked out separately (the tank's tracks)
                            var along = g.Keep(g.Scale(g.Normalized(forwardSkid), -Grip("GripCurve", g.Magnitude(forwardSkid), maxGrip)));
                            var across = g.Keep(g.Scale(g.Normalized(sideSkid), -Grip("GripCurveLateral", g.Magnitude(sideSkid), maxGripSide)));
                            var blended = g.Call(V3T, "Slerp", new[] { V3T, V3T, typeof(float) }, null, across, along, forwardShare);
                            g.SetGraphVar("Grip3", g.Scale(g.LerpVector(g.Scale(blended, dt), g.Scale(along + across, dt), g.P("LongLatSeparation")), lean), V3T);
                        }, () =>
                        {
                            var direction = g.Keep(g.Normalized(fullSkid));
                            var along = g.Scale(direction, -Grip("GripCurve", fullMag, maxGrip));
                            var across = g.Scale(direction, -Grip("GripCurveLateral", fullMag, maxGripSide));
                            g.SetGraphVar("Grip3", g.Scale(g.LerpVector(across, along, forwardShare), dt * lean), V3T);
                        });
                        // never more than would stop the wheel's share of the vehicle dead
                        var grip = g.Keep(g.ClampMagnitude(g.Scale(g.GraphVar("Grip3", V3T), g.P("GripGain")), g.Magnitude(pointVelocity) / g.P("NumWheels")));
                        g.Do(RB, "AddForceAtPosition", force, body, grip, point, ForceMode.VelocityChange);
                        g.SetGraphVar("ForceUsed", g.Dot(g.Normalized(rolling), grip));

                        // an engaged drive wheel keeps the engine's speed; any other wheel rolls with the ground
                        var onGround = g.Select(g.And(driven, g.Not(tank)), g.Lerp(surface, forwardSpeed, clutch), forwardSpeed);
                        g.SetGraphVar("Surf", g.Lerp(onGround, 0f, 1f - g.Pow(0.5f, g.P("WheelSlowDown"))));      // rolling resistance
                    }, () =>
                    {
                        // in the air: the wheel hangs at full droop and nothing pushes on the tyre
                        g.Set(TF, "position", g.ObjVar("WheelVisual", TF), origin - g.Scale(up, travel - radius));
                        g.If(g.ObjVar("HasVisualGround", typeof(bool)), () =>
                            g.Set(TF, "position", g.ObjVar("WheelVisualGround", TF), origin - g.Scale(up, travel)));
                        g.SetGraphVar("Grounded", false);
                        foreach (var name in new[] { "CompressionLast", "ForwardSpeed", "ForwardSlip", "ForceUsed", "SkidLength" })
                            g.SetGraphVar(name, 0f);
                    });

                    // ---- brakes: slow the wheel's rotation; on a drive wheel the engine is slowed with it ----
                    var brake = g.Keep(g.ObjVarOf(vehicle, "BrakeInput", typeof(float)) * g.P("BrakeMulti"));
                    var handBrake = g.Keep(g.ObjVarOf(vehicle, "HandBrakeInput", typeof(float)) * g.P("HandBrakeMulti"));
                    g.If(g.Greater(brake + handBrake, 0f), () =>
                    {
                        var before = g.Keep(g.F("Surf"));
                        var engineWeight = g.Keep(g.Lerp(g.P("Brake_EngineWeight"), 1f, clutch));
                        var braked = g.MoveTowards(before, 0f, g.P("BrakeStrength") * brake / engineWeight);
                        var after = g.SetGraphVar("Surf", g.Lerp(braked, 0f, handBrake / engineWeight));
                        g.If(driven, () =>
                        {
                            var rpmLost = (before - after) / circumference * 60f;
                            g.SetObjVar("EngineForceUsed", g.ObjVarOf(vehicle, "EngineForceUsed", typeof(float)) +
                                dt * rpmLost * g.P("EngineBrakeStrength") * g.Select(g.Less(ratio, 0f), -1f, 1f) * (1f - clutch), vehicle);
                        });
                    });
                    var rps = g.SetGraphVar("RPS", g.F("Surf") / circumference);
                    g.SetGraphVar("RPM", rps * 60f);
                    g.SetObjVar("WheelRPS", rps);

                    // ---- the load on the engine: pushing the vehicle slows it, being pushed by the vehicle speeds it up ----
                    g.If(driven, () =>
                    {
                        var slip = g.Keep(g.F("ForwardSlip"));
                        var slowing = g.Or(g.And(g.Less(slip, 0f), g.Greater(ratio, 0f)), g.And(g.Greater(slip, 0f), g.Less(ratio, 0f)));
                        var gearBackwards = g.Not(g.Same(g.Sign(g.F("ForwardSpeed")), g.Sign(ratio)));
                        var share = g.Select(slowing, g.Select(gearBackwards, 1f - clutch, 1f), -(1f - clutch));
                        g.SetObjVar("EngineForceUsed", g.ObjVarOf(vehicle, "EngineForceUsed", typeof(float)) +
                            g.Abs(g.F("ForceUsed")) * g.Abs(ratio) * g.P("EngineInfluence") * share, vehicle);
                    });
                });
            });

            // ---- the wheel you see turns with the tyre (a tank's road wheels copy the one that drives its track).
            //      This graph is switched on by the vehicle only on the client that simulates it, and off again
            //      when the vehicle rests, so on every other client a wheel costs nothing. ----
            void Turn(object revolutionsPerSecond) =>
                g.If(g.ObjVar("HasRotationSource", typeof(bool)),
                    () => g.Set(TF, "localRotation", g.ObjVar("WheelVisual", TF), g.Get(TF, "localRotation", g.ObjVar("RotationSourceVisual", TF))),
                    () =>
                    {
                        var turned = g.SetGraphVar("Rotation", g.Call(typeof(Mathf), "Repeat", new[] { typeof(float), typeof(float) }, null,
                            g.F("Rotation") + (revolutionsPerSecond is Val rps ? rps : g.F("RPS")) * 360f * g.Get(typeof(Time), "deltaTime"), 360f));
                        g.Set(TF, "localRotation", g.ObjVar("WheelVisual", TF),
                            g.Call(typeof(Quaternion), "AngleAxis", new[] { typeof(float), V3T }, null, turned, Vector3.right));
                    });
            g.On(new Update(), _ =>
            {
                var vehicle = g.ObjVar("Vehicle", GO);
                g.If(g.ObjVarOf(vehicle, "Simulate", typeof(bool)), () =>
                {
                    Turn(null);
                    // A sliding tyre squeals and smokes, more the faster it slides (SaccWheel.LateUpdate).
                    var skid = g.Keep(g.F("SkidLength"));
                    var volume = g.Keep(g.Min((skid - g.P("SkidSound_Min")) * g.P("SkidSound_VolumeIncrease"), 1f));
                    var sound = g.ObjVar("SkidSound", typeof(AudioSource));
                    var smoke = g.ObjVar("SkidParticle", typeof(ParticleSystem));
                    Type emission = typeof(ParticleSystem.EmissionModule);
                    g.If(g.And(g.B("Grounded"), g.Greater(volume, 0f)), () =>
                    {
                        g.If(g.Not(g.B("Skidding")), () =>
                        {
                            g.SetGraphVar("Skidding", true);
                            g.Do(GO, "SetActive", new[] { typeof(bool) }, g.Get(typeof(AudioSource), "gameObject", sound), true);
                            g.Set(emission, "enabled", g.Get(typeof(ParticleSystem), "emission", smoke), true);
                        });
                        g.Set(typeof(AudioSource), "volume", sound, volume * g.P("SkidVolume"));
                        g.Set(typeof(AudioSource), "pitch", sound, skid * g.P("SkidSound_PitchIncrease") + g.P("SkidSound_Pitch"));
                        g.Set(emission, "rateOverTimeMultiplier", g.Get(typeof(ParticleSystem), "emission", smoke), skid * g.P("SkidParticleAmount"));
                    }, () => g.If(g.B("Skidding"), () =>
                    {
                        g.SetGraphVar("Skidding", false);
                        g.Do(GO, "SetActive", new[] { typeof(bool) }, g.Get(typeof(AudioSource), "gameObject", sound), false);
                        g.Set(emission, "enabled", g.Get(typeof(ParticleSystem), "emission", smoke), false);
                    }));
                });
            });
            return g;
        }
    }
}
