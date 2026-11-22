using System;
using System.IO;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The aircraft logic, written as code and emitted as Visual Scripting graphs.
    ///
    /// The flight model is a port of SaccAirVehicle from SaccFlightAndVehicles
    /// (Copyright (c) 2021 Sacchan-VRC, MIT licence - see Assets/Flight/THIRD-PARTY-NOTICES.md),
    /// reference commit 3216e53f. Variable names follow the original so the two can be compared.
    /// Tuning values live in Object variables on each aircraft; state lives in Graph variables.
    ///
    /// Input conventions (Sacc's RotationInputs): pitch -1 = nose up, +1 = nose down;
    /// yaw -1 = nose left, +1 = nose right; roll -1 = roll right, +1 = roll left.
    /// </summary>
    public static partial class FlightGraphs
    {
        public const string CorePath = "Assets/Flight/Graphs/FlightCore.asset";
        public const string WatchPath = "Assets/Flight/Graphs/FleetWatch.asset";
        const string ReportPath = "Logs/flight_graphs_report.txt";

        [MenuItem("FlightWorld/Flight/Regenerate Flight Graphs")]
        public static void Regenerate()
        {
            var report = new StringBuilder();
            try
            {
                BuildCore().Save(CorePath, report);
                BuildTeleportSign().Save(TeleportPath, report);
                BuildFleetWatch().Save(WatchPath, report);
                BuildWeaponsWorld().Save(WeaponsPath, report);
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
            Debug.Log("[FlightGraphs] " + report);
        }

        static readonly Type RB = typeof(Rigidbody), TF = typeof(Transform), V3T = typeof(Vector3), AN = typeof(Animator);
        static readonly Type[] Vec = { typeof(Vector3) };

        public static GraphWriter BuildCore()
        {
            var g = new GraphWriter();

            // ---- state ---------------------------------------------------------------------------
            g.Declare("Piloting", false);       // the local player is flying this aircraft
            g.Declare("TestPilot", false);      // set by the automated flight test: no seat needed
            g.Declare("TestRider", false);      // set by that test when it hangs a rider's weight on the seat, as the app does
            g.Declare("EngineOutput", 0f);
            g.Declare("Throttle", 0f);
            g.Declare("InPitch", 0f);
            g.Declare("InYaw", 0f);
            g.Declare("InRoll", 0f);
            g.Declare("LerpedPitch", 0f);
            g.Declare("LerpedYaw", 0f);
            g.Declare("LerpedRoll", 0f);
            foreach (var name in new[] { "KeyPitch", "KeyYaw", "KeyRoll", "KeyThrottle", "KeyBrake",
                                         "StickPitch", "StickYaw", "StickRoll", "StickThrottle", "TriggerBrake" })
                g.Declare(name, 0f);
            g.Declare("Speed", 0f);             // telemetry for the HUD, effects and tests
            g.Declare("AoA", 0f);
            g.Declare("AoAPitch", 0f);          // signed, for the head-up display
            g.Declare("VertGs", 1f);            // load factor along the vehicle's up axis, smoothed over about 0.1 s
            g.Declare("MaxGs", 0f);             // the largest this pilot has pulled
            g.Declare("LastVel", Vector3.zero);
            g.Declare("GsAll", Vector3.zero);      // load factor on all three axes, smoothed
            g.Declare("Brake", 0f);
            g.Declare("Taxiing", false);        // on the ground (the ray under the hull found it)
            g.Declare("TaxiLerper", 0f);        // turn rate while steering on the ground without a steerable wheel
            g.Declare("VTOLAngle", 0f);         // thrust direction, 0 = ahead .. 1 = fully tilted; follows the input at a limited rate
            g.Declare("VTOLAngleInput", 0f);
            g.Declare("KeyVtol", 0f);
            g.Declare("Riding", false);         // the local player is aboard as a passenger
            g.Declare("LodBias", 0f);           // how many Mesh LOD levels coarser than Unity's own choice the vehicle is drawn
            g.Declare("DozeWait", 0f);          // how long the vehicle has been fit to doze (see BuildFleetWatch)
            g.Declare("Dozed", false);          // this graph was switched off, and has not run its slow tick since
            g.Declare("StartHold", 2.5f);       // seconds the vehicle is still held where it was built, after the world loads
            g.Declare("Floating", false);       // a boat's floats are in the water
            g.Declare("DepthSum", 0f);          // scratch: how deep its floats are, added up
            g.Declare("Awake", true);           // animator, effects and sounds are running (they rest on a vehicle nobody is using)
            g.Declare("DisplayTimer", 0f);      // time since an airliner's flight displays were last redrawn
            g.Declare("GearPos", 1f);           // landing gear that travels by a position: 1 = down and locked, 0 = up
            g.Declare("FlapPos", 0f);           // flaps that travel by a position: 0 = in, 1 = at the ported detent
            g.Declare("NearShown", true);       // the parts drawn only for players close by are being drawn
            g.Declare("SeatsOpen", true);       // the passenger seats can be clicked (they open once there is a pilot)
            g.Declare("LastTick", 0f);          // when the fleet clock last gave this vehicle its slow tick
            g.Declare("Held", false);           // resting on the ground with its body frozen in place
            g.Declare("RawSpeed", 0f);          // the last speed worked out from movement, before a jump is thrown out
            g.Declare("FlownTimer", 0f);        // counts down from the last sign that another client is flying this vehicle
            g.Declare("IdleTimer", 6f);         // counts down to resting them
            g.Declare("NearSeat", false);       // scratch: the local player's head is at one of the passenger seats

            g.Declare("MovedSpeed", 0f);        // how fast the vehicle is moving, measured on every client
            g.Declare("GearUp", false);         // landing gear retracted (DFUNC_Gear)
            g.Declare("FlapsOn", false);        // flaps out (DFUNC_Flaps): more lift, more drag
            g.Declare("StickVtol", 0f);
            foreach (var name in new[] { "KeyThrustX", "KeyThrustY", "KeyThrustZ", "StickThrustX", "StickThrustZ" })
                g.Declare(name, 0f);            // manoeuvring thrusters (SAV_ThreeDThrust): sideways, up, ahead
            g.Declare("LeftGrip", false);
            g.Declare("TrailsOn", false);       // wing-tip vapour trails are streaming
            g.Declare("HudTimer", 0f);
            g.Declare("Health", 100f);
            g.Declare("Dead", false);           // exploded, waiting to reappear at its parking spot
            g.Declare("RespawnTimer", 0f);
            g.Declare("EjectTimer", 0f);        // counts down to sending the local player back to the arrival point
            g.Declare("ResetTimer", 0f);        // a reset was asked for; waiting to become the aircraft's owner
            g.Declare("BrakeTimer", 0f);        // counts down to braking the wheels again once they are back after a wreck
            g.Declare("Riders", 0f);            // scratch: other players found sitting in the aircraft
            g.Declare("SeatTimer", 0f);         // counts down to fitting the seat height to the pilot who just sat
            g.Declare("LimitTimer", 0f);        // counts down to the next time the speed limit is lifted again
            g.Declare("GuardTimer", 0f);        // counts down to the next check of who may sit in and touch the aircraft
            g.Declare("SoundTimer", 0f);        // counts down to the next update of the engine and wind sound
            g.Declare("SoundElapsed", 0f);      // time since that update, for measuring speed from movement
            g.Declare("LastPosition", Vector3.zero);
            g.Declare("SeatHome", Vector3.zero);   // the pilot seat's authored place in the aircraft
            g.Declare("SeatTarget", Vector3.zero); // where the seat is sliding to, to fit this pilot (in its parent's space)
            g.Declare("SeatSliding", false);
            g.Declare("SpawnPosition", Vector3.zero);
            g.Declare("SpawnRotation", Quaternion.identity);
            DeclareAdvanced(g);
            DeclareFunctions(g);

            // ---- remember the parking spot ------------------------------------------------------------
            g.On(new Start(), _ =>
            {
                g.SetGraphVar("SpawnPosition", g.Get(TF, "position"));
                g.SetGraphVar("SpawnRotation", g.Get(TF, "rotation"));
                g.SetGraphVar("Health", g.P("FullHealth"));
                // (fuel is not modelled; the vehicles' animators want some before rotors that ran down start again)
                AnimFloat(g, "fuel", 1f);
                g.SetGraphVar("SeatHome", g.Get(TF, "localPosition", PilotSeatTransform(g)));
                g.SetGraphVar("LastPosition", g.Get(TF, "position"));
                g.SetGraphVar("SoundElapsed", -g.P("TickOffset"));      // so the vehicles' ticks fall on different frames
                IgnoreSea(g);
                // Held where it was built for the first moments: the app notes each vehicle's place as it loads
                // (its idle reset puts a left vehicle back there), and the ground under it may load a moment
                // later. Without this, vehicles were noted up to 1.2 m under where they stand.
                g.Set(RB, "constraints", null, RigidbodyConstraints.FreezeAll);
                g.SetGraphVar("VTOLAngle", g.P("VTOLDefaultValue"));
                g.SetGraphVar("VTOLAngleInput", g.P("VTOLDefaultValue"));
                g.SetGraphVar("FlapsOn", g.ObjVar("FlapsDefaultOn", typeof(bool)));
                g.SetGraphVar("FlapPos", g.Select(g.ObjVar("FlapsDefaultOn", typeof(bool)), 1f, 0f));
                NoSpeedLimit(g);
                ParkingBrake(g);
                FunctionsHome(g);
            });
            // the dial functions' messages and the damage this vehicle takes (FlightGraphsFunctions.cs)
            FunctionMessages(g);

            // ---- gear and flaps: a key or a thumbstick click toggles them, for the pilot only -------------------
            // Everyone else is told with a one-shot, so they see the gear travel and the flaps move too.
            void Toggle(string state, string animatorBool, string feature, string message)
            {
                g.If(g.And(g.B("Piloting"), g.ObjVar(feature, typeof(bool))), () =>
                {
                    var now = g.SetGraphVar(state, g.Not(g.B(state)), typeof(bool));
                    AnimBool(g, animatorBool, now);
                    g.PlatformDo("SendOneShot", ("Data", g.Call(typeof(string), "Concat", new[] { typeof(string), typeof(string), typeof(string) }, null,
                        message, g.ObjVar("BoomMessage", typeof(string)), g.Select(now, "1", "0", typeof(string)))), ("All Instances", false));
                });
            }
            foreach (var (id, state, animatorBool, feature, message) in new[]
                     { ("fw_gear", "GearUp", "gearup", "HasGear", "gear:"), ("fw_flaps", "FlapsOn", "flaps", "HasFlaps", "flaps:") })
            {
                g.On(g.Platform("OnJsReturnValue", ("Return ID", id)), e =>
                    g.If(g.Greater(g.PlatformValue("ParseStringInvariant", "float", typeof(float), ("string", g.EventOut(e, "Data", typeof(string)))), 0.5f),
                        () => Toggle(state, animatorBool, feature, message)));
                foreach (var value in new[] { "1", "0" })
                    g.On(g.Platform("OnOneShot"), e =>
                        g.If(g.Same(g.EventOut(e, "Data", typeof(string)), g.Call(typeof(string), "Concat", new[] { typeof(string), typeof(string), typeof(string) }, null,
                            message, g.ObjVar("BoomMessage", typeof(string)), value)), () =>
                        {
                            Wake(g);
                            g.SetGraphVar(state, value == "1");
                            AnimBool(g, animatorBool, value == "1");
                        }));
            }
            // The owner put the aircraft back on its spot (reset sign or respawn): gear down here too.
            g.On(g.Platform("OnOneShot"), e =>
                g.If(g.Same(g.EventOut(e, "Data", typeof(string)), g.Call(typeof(string), "Concat", new[] { typeof(string), typeof(string) }, null,
                    "home:", g.ObjVar("BoomMessage", typeof(string)))), () =>
                {
                    Wake(g);
                    GearHome(g);
                }));
            // (button 4 = thumbstick click: right hand = gear, left hand = flaps)
            g.On(g.Platform("OnControllerButtonPressed"), e =>
                g.If(g.Same(g.EventOut(e, "Button Type", typeof(int)), 4), () =>
                    g.If(g.Same(g.EventOut(e, "Hand Side", typeof(int)), 1),
                        () => Toggle("GearUp", "gearup", "HasGear", "gear:"),
                        () => Toggle("FlapsOn", "flaps", "HasFlaps", "flaps:"))));

            // ---- desktop input: the page forwards key state through SendToVisualScripting ----------------
            foreach (var (id, variable) in new[] { ("fw_pitch", "KeyPitch"), ("fw_yaw", "KeyYaw"), ("fw_roll", "KeyRoll"),
                                                   ("fw_throttle", "KeyThrottle"), ("fw_brake", "KeyBrake"), ("fw_vtol", "KeyVtol"),
                                                   ("fw_thrust_x", "KeyThrustX"), ("fw_thrust_y", "KeyThrustY"), ("fw_thrust_z", "KeyThrustZ") })
            {
                g.On(g.Platform("OnJsReturnValue", ("Return ID", id)), e =>
                    g.SetGraphVar(variable, g.PlatformValue("ParseStringInvariant", "float", typeof(float),
                        ("string", g.EventOut(e, "Data", typeof(string))))));
            }

            // ---- VR input: right stick = pitch / roll, left stick = throttle / yaw, left trigger = brakes ----
            // (With the advanced controls on, the hands work the cockpit's stick and throttle instead and the
            // thumbsticks pick functions on the dials: FlightGraphsHands.cs.)
            g.On(g.Platform("OnControllerAxisUpdate"), e =>
            {
                var x = g.Keep(g.EventOut(e, "X Axis", typeof(float)));
                var y = g.Keep(g.EventOut(e, "Y Axis", typeof(float)));
                var hand = g.Keep(g.EventOut(e, "Hand Side", typeof(int)));
                // (the balloon: the right stick pushes it ahead, astern and sideways with its thrusters, in either mode)
                g.If(g.And(g.Same(hand, 1), g.ObjVar("RightStickThrusts", typeof(bool))), () =>
                {
                    g.SetGraphVar("RightThrustX", x);
                    g.SetGraphVar("RightThrustZ", y);
                }, () =>
                g.If(g.B("Advanced"), () =>
                    g.If(g.Same(hand, 1), () => DialPick(g, "R", x, y), () => DialPick(g, "L", x, y)), () =>
                g.If(g.Same(hand, 1), () =>
                {
                    g.SetGraphVar("StickPitch", y);            // stick back = nose up
                    g.SetGraphVar("StickRoll", -x);
                }, () =>
                {
                    // With the left grip held the stick tilts the thrust instead: forward for forward flight,
                    // back to point it down for hovering (aircraft that can do it).
                    // (On a vehicle with manoeuvring thrusters the same grip makes the stick fire them: ahead,
                    // astern and sideways.)
                    g.If(g.B("LeftGrip"), () =>
                    {
                        g.SetGraphVar("StickVtol", -y);
                        g.SetGraphVar("StickThrottle", 0f);
                        g.SetGraphVar("StickThrustZ", y);
                        g.SetGraphVar("StickThrustX", x);
                        g.SetGraphVar("StickYaw", g.Select(g.ObjVar("HasThrusters", typeof(bool)), 0f, x));
                    }, () =>
                    {
                        g.SetGraphVar("StickThrottle", y);
                        g.SetGraphVar("StickVtol", 0f);
                        g.SetGraphVar("StickThrustZ", 0f);
                        g.SetGraphVar("StickThrustX", 0f);
                        g.SetGraphVar("StickYaw", x);
                    });
                })));
            });
            // (button 1 = grip, hand 0 = left)
            foreach (var (unit, held) in new[] { ("OnControllerButtonPressed", true), ("OnControllerButtonReleased", false) })
                g.On(g.Platform(unit), e =>
                    g.If(g.Same(g.EventOut(e, "Button Type", typeof(int)), 1), () =>
                    {
                        var hand = g.Keep(g.EventOut(e, "Hand Side", typeof(int)));
                        Grip(g, hand, held);
                        g.If(g.And(g.Same(hand, 0), g.Not(g.B("Advanced"))), () =>
                        {
                            g.SetGraphVar("LeftGrip", held);
                            if (!held)
                                foreach (var name in new[] { "StickVtol", "StickThrustX", "StickThrustZ" }) g.SetGraphVar(name, 0f);
                        });
                    }));
            g.On(g.Platform("OnTriggerAxisUpdate"), e =>
            {
                var value = g.Keep(g.EventOut(e, "Trigger Value", typeof(float)));
                var hand = g.Keep(g.EventOut(e, "Hand Side", typeof(int)));
                g.If(g.B("Advanced"), () => g.If(g.Same(hand, 0),
                    () => DialTrigger(g, "L", value, () => Toggle("GearUp", "gearup", "HasGear", "gear:"), () => Toggle("FlapsOn", "flaps", "HasFlaps", "flaps:")),
                    () => DialTrigger(g, "R", value, () => Toggle("GearUp", "gearup", "HasGear", "gear:"), () => Toggle("FlapsOn", "flaps", "HasFlaps", "flaps:"))),
                    () => g.If(g.Same(hand, 0), () => g.SetGraphVar("TriggerBrake", value)));
            });
            // the hands, once the app has put the hand objects on them this frame (the fleet watch's LateUpdate)
            g.On(Custom(g, "fw_hands"), _ => g.If(g.And(g.B("Piloting"), g.B("Advanced")), () => Hands(g)));

            // ---- taking the pilot's seat: clicking it seats the player (the seat component does that) -------
            // (A click on a seat somebody else is in does not seat you, so it must not make you the pilot either.
            // Nor does one from the pilot already in it: the seat's click area is off while they are aboard, see
            // SeatClicks, but should a click arrive anyway it must not restart anything in flight.)
            g.On(g.Platform("OnClick", ("banterPlayerEvents", g.ObjVar("PilotSeat", typeof(GameObject)))), _ =>
                g.If(g.And(g.And(g.Not(g.B("Dead")), g.Not(g.B("Piloting"))),
                           g.Not(g.Get(Seat, "IsOccupiedByOther", g.ObjVar("PilotSeatComponent", Seat)))), () =>
                {
                    g.SetGraphVar("Piloting", true);
                    g.If(g.ObjVar("IsBoat", typeof(bool)), () => g.SetObjVar("Busy", true), () => Fly(g, true));
                    g.SetGraphVar("Throttle", 0f);
                    g.SetGraphVar("ResetTimer", 0f);
                    NoSpeedLimit(g);
                    g.Do(Sync, "_TakeOwnership", Type.EmptyTypes, null);
                    BlockSticks(g, true);
                    Wake(g);
                    Cockpit(g, true);
                    g.SetGraphVar("EngineOff", false);
                    g.SetGraphVar("EngineStarting", 0f);
                    Say(g, "engine", "1");
                    g.If(g.Or(g.B("CanopyOpen"), g.B("CanopyBroken")), () => { }, () => Say(g, "canopy", "0"));
                    g.SetGraphVar("LastVel", g.Get(RB, "linearVelocity"));
                    g.SetGraphVar("VertGs", 1f);
                    g.SetGraphVar("GsAll", Vector3.up);
                    g.SetGraphVar("MaxGs", 0f);
                }));

            // ---- crash damage (SaccAirVehicle: impact speed = collision impulse / mass) ------------------
            // Runs for whoever is responsible for the aircraft: its pilot, or its network owner once the
            // pilot has left, so an abandoned aircraft that falls out of the sky still cleans itself up.
            g.On(new OnCollisionEnter(), e => g.If(g.And(g.Not(g.B("Dead")), Responsible(g)), () =>
            {
                var impact = g.Keep(g.Magnitude(g.EventOut(e, "impulse", V3T)) / g.Get(RB, "mass"));
                g.If(g.Greater(impact, g.P("CrashDamageSpeed")), () =>
                {
                    var fraction = g.Clamp01((impact - g.P("CrashDamageSpeed")) / (g.P("CrashDeathSpeed") - g.P("CrashDamageSpeed")));
                    var health = g.SetGraphVar("Health", g.F("Health") - g.P("FullHealth") * fraction);
                    g.If(g.Less(health, 0.01f), () => Explode(g));
                });
            }));

            // ---- everyone else learns of the explosion from a one-shot (which never returns to its sender) ----
            g.On(g.Platform("OnOneShot"), e =>
                g.If(g.And(g.Not(g.B("Dead")), g.Same(g.EventOut(e, "Data", typeof(string)), g.ObjVar("BoomMessage", typeof(string)))), () =>
                    Wreck(g, 0.5f)));       // a little later than the owner, so the aircraft has already moved home

            // ---- reset sign behind the parking spot: brings back an aircraft nobody is in ---------------------
            g.On(g.Platform("OnClick", ("banterPlayerEvents", g.ObjVar("ResetSign", typeof(GameObject)))), _ =>
                g.If(g.And(g.Not(g.B("Piloting")), g.Not(g.B("Dead"))), () =>
                {
                    g.SetGraphVar("Riders", 0f);
                    g.ForEach(g.ObjVar("Seats", typeof(System.Collections.IEnumerable)), Seat, seat =>
                        g.If(g.Get(Seat, "IsOccupiedByOther", seat), () => g.SetGraphVar("Riders", g.F("Riders") + 1f)));
                    g.If(g.And(g.Less(g.F("Riders"), 0.5f), g.Not(LocalPlayerAboard(g))), () =>
                    {
                        g.Do(Sync, "_TakeOwnership", Type.EmptyTypes, null);
                        g.SetGraphVar("ResetTimer", 3f);
                    });
                }));

            // ---- the fleet clock's events ----------------------------------------------------------------------
            // A vehicle's graph has no per-frame and no per-step event of its own. One graph for the whole fleet
            // (BuildFleetWatch) sends "fw_frame" every frame and "fw_step" every physics step to the one vehicle
            // the local player is piloting (and to a boat, which must float), and hands the slow tick "fw_tick"
            // round the fleet so that each vehicle gets it about ten times a second. Measured on a Quest, 21
            // parked vehicles each answering every frame and every step to find nothing to do cost 1.9 ms of a
            // 13.6 ms frame.
            g.On(Custom(g, "fw_frame"), _ => g.If(g.B("Piloting"), PilotFrame));

            g.On(Custom(g, "fw_tick"), _ =>
            {
                var clock = g.Keep(g.Get(typeof(Time), "time"));
                // ---- ten times a second on every client: engine and wind sound ---------------------------------
                // Only the pilot's client knows the engine's output, so everyone else hears an engine that
                // follows how fast the aircraft is moving (and idles while somebody sits in the pilot's seat).
                // Speed is measured from the aircraft's own movement, which works for an owner and a copy alike.
                // A vehicle in use takes every turn the clock gives it (ten a second). One that is resting lets four of
                // five go by: all it has to notice is somebody arriving, and half a second is soon enough for that.
                var soundElapsed = g.Keep(g.Min(clock - g.F("LastTick"), 0.6f));     // time since the last tick taken
                g.If(g.Or(g.Or(g.B("Awake"), g.B("Piloting")), g.Greater(soundElapsed, 0.45f)), () =>
                {
                    g.SetGraphVar("LastTick", clock);
                    // Which vehicle the clock sends frames and steps to: the one this player pilots. (Said here as well as
                    // when the seat is taken, so a pilot who got there some other way - the scripted test pilot - is found.)
                    g.If(g.Not(g.ObjVar("IsBoat", typeof(bool))), () =>
                    {
                        var watch = g.ObjVar("Watch", typeof(GameObject));
                        var mine = g.Keep(g.Same(g.ObjVarOf(watch, "PilotVehicle", typeof(GameObject)), g.Get(TF, "gameObject")));
                        g.If(g.B("Piloting"),
                            () => g.If(g.Not(g.And(mine, g.ObjVarOf(watch, "HasPilotVehicle", typeof(bool)))), () => Fly(g, true)),
                            () => g.If(g.And(mine, g.ObjVarOf(watch, "HasPilotVehicle", typeof(bool))), () => Fly(g, false)));
                    });

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
                    // (A jump is not speed: home after a crash, or the app putting an idle vehicle back. One reading
                    // far from the one before it is thrown out; a vehicle that really is that fast reads so twice.)
                    var jumped = g.Or(g.Greater(moved, 1500f), g.Greater(moved - g.F("RawSpeed"), 150f));
                    var airspeed = g.Keep(g.Select(jumped, 0f, moved));
                    g.SetGraphVar("RawSpeed", moved);
                    g.SetGraphVar("MovedSpeed", airspeed);
                    g.SetGraphVar("LastPosition", position);
                    g.SetGraphVar("SoundElapsed", 0f);
                    // Somebody else is flying it. The pilot's seat normally says so; where it does not (a watcher saw
                    // a helicopter's rotors stop while another player flew it away), the vehicle being on the move,
                    // or well above where it parks, while another client simulates it says the same. That second
                    // reading is held for a few seconds, so a steady hover does not stop the rotors.
                    // (a copy that another client simulates is kinematic here; the owner's own body never is)
                    var flownNow = g.And(g.Get(RB, "isKinematic"),
                        g.Or(g.Greater(airspeed, 1f), g.Greater(g.Y(position) - g.Y(g.GraphVar("SpawnPosition", V3T)), 3f)));
                    var flownLeft = g.SetGraphVar("FlownTimer", g.Select(flownNow, 8f, g.Max(g.F("FlownTimer") - soundElapsed, 0f)));
                    var heardRunning = g.Keep(g.And(g.Not(g.B("Piloting")),
                        g.Or(g.Get(Seat, "IsOccupiedByOther", g.ObjVar("PilotSeatComponent", Seat)), g.Greater(flownLeft, 0f))));
                    var power = g.Keep(g.Select(g.B("Dead"), 0f,
                        g.Select(g.B("Piloting"), g.F("EngineOutput"),
                            g.Select(heardRunning, g.Clamp01(airspeed / g.P("SoundRefSpeed") + 0.3f), 0f))));
                    // A vehicle nobody is using rests: its animator, effects and sounds are switched off a few
                    // seconds after it was last crewed, moving or wrecked, and come back the moment it is used.
                    // (Fourteen idle animators with twenty layers each, and their particle systems, cost a
                    // Quest several milliseconds a frame for nothing.)
                    var crewed = g.Keep(g.Or(g.B("Piloting"), heardRunning));
                    g.If(g.Not(g.Same(crewed, g.ObjVar("Crewed", typeof(bool)))), () => g.SetObjVar("Crewed", crewed));   // (what missiles can lock on to)
                    g.If(g.Or(g.Or(crewed, g.B("Dead")), g.Or(g.B("Riding"), g.Greater(airspeed, 1f))),
                        () => Wake(g),
                        () => g.If(g.And(g.B("Awake"), g.Less(g.SetGraphVar("IdleTimer", g.F("IdleTimer") - 0.1f), 0f)), () => Rest(g)));

                    // The vehicle's own animator shows the rest (SAV_EffectsController): exhaust flame and
                    // afterburner, slats and vapour by speed, smoke by damage. A pilot's client knows the real
                    // values and also sets the fast-moving ones every frame; everyone else shows the estimate.
                    g.If(g.B("Awake"), () =>
                    {
                    // The vehicle's own sounds, mixed as SAV_SoundController mixes them. Aboard, the pilot hears the
                    // cockpit's engine note, the rush of air, and only a little of the exhaust; outside there is
                    // an idle whine, the exhaust's roar by engine power, and a rumble that carries a long way.
                    // Each source's authored volume is its full volume.
                    Type audio = typeof(AudioSource);
                    var on = g.Keep(g.Select(g.And(g.Not(g.B("Dead")), g.Or(g.B("Piloting"), heardRunning)), 1f, 0f));
                    var aboard = g.Keep(g.Or(g.B("Piloting"), g.B("Riding")));
                    var inside = g.Keep(g.Select(aboard, 1f, 0f));
                    void Volume(string source, object value) => g.Set(audio, "volume", g.ObjVar(source, audio), value);
                    Volume("InsideSound", on * inside * g.P("InsideVolume"));
                    g.Set(audio, "pitch", g.ObjVar("InsideSound", audio), power * 0.4f + 0.8f);
                    Volume("IdleSound", on * (1f - inside) * g.P("IdleVolume"));
                    g.Set(audio, "pitch", g.ObjVar("IdleSound", audio), power + 1f);
                    Volume("EngineSound", power * g.P("ThrustVolume") * g.Select(aboard, g.P("InVehicleThrustVolumeFactor"), 1f));
                    Volume("DistantSound", power * (1f - inside) * g.P("DistantVolume"));
                    Volume("WindSound", g.Select(g.And(aboard, g.Not(g.B("Dead"))),
                        g.Min(g.Max(g.F("AoA"), g.P("PlaneWindMinVol")) * airspeed / g.P("WindRefSpeed") * g.P("PlaneWindMultiplier"), g.P("WindVolume")), 0f));

                        g.If(g.Not(g.B("Piloting")), () =>
                        {
                            AnimFloat(g, "engineoutput", power);
                            AnimFloat(g, "throttle", power);
                        });
                        AnimBool(g, "afterburneron", g.And(g.ObjVar("HasAfterburner", typeof(bool)), g.Greater(power, g.P("ThrottleAfterburnerPoint"))));
                        AnimFloat(g, "mach10", airspeed * 0.000291545f);
                        AnimFloat(g, "health", g.Clamp01(g.F("Health") / g.P("FullHealth")));
                        AnimBool(g, "occupied", crewed);
                        AnimBool(g, "EngineOn", g.And(crewed, g.Not(g.B("EngineOff"))));   // rotors and propellers turn while there is a pilot with the engine on
                        // The canopy (on one aircraft, a cabin that lowers to the ground) stands open while the
                        // vehicle is parked and empty, so people can see where to get in, and shuts when a pilot
                        // sits down. Every client works this out the same way from the pilot's seat.
                        AnimBool(g, "canopyopen", g.And(g.ObjVar("HasCanopy", typeof(bool)), g.Select(crewed, g.Or(g.B("CanopyOpen"), g.B("CanopyBroken")),
                            g.And(g.Not(g.B("Dead")), g.Less(airspeed, 1f)), typeof(bool))));
                        // Gear legs, flaps, slats and thrust levers that travel by a position rather than a switch
                        // (EsnyaSFAddons' advanced gear and flaps, as an airliner built on them has). Every client
                        // runs them from the same gear and flap switches, so everyone sees the legs swing.
                        g.If(g.ObjVar("HasTravellingLevers", typeof(bool)), () =>
                        {
                            var gearPos = g.SetGraphVar("GearPos", g.MoveTowards(g.F("GearPos"), g.Select(g.B("GearUp"), 0f, 1f), soundElapsed / g.P("GearTime")));
                            foreach (var leg in new[] { "gearposC", "gearposL", "gearposR" }) AnimFloat(g, leg, gearPos);
                            var flapPos = g.SetGraphVar("FlapPos", g.MoveTowards(g.F("FlapPos"), g.Select(g.B("FlapsOn"), 1f, 0f), soundElapsed / g.P("FlapTime")));
                            AnimFloat(g, "flapsangle", flapPos * g.P("FlapAngleOut"));
                            AnimFloat(g, "slatsangle", flapPos * g.P("SlatAngleOut"));
                            AnimFloat(g, "flapslever", g.Select(g.B("FlapsOn"), g.P("FlapLeverOut"), 0f));
                            // (the thrust levers idle part-way up their travel; below that is reverse, which is not ported)
                            AnimFloat(g, "throttleleveler", g.P("ThrottleLeverIdle") + power * (1f - g.P("ThrottleLeverIdle")));
                        });
                    });

                g.If(g.Greater(g.F("StartHold"), 0f), () =>
                    g.If(g.Less(g.SetGraphVar("StartHold", g.F("StartHold") - soundElapsed), 0.0001f), () =>
                    {
                        g.SetGraphVar("StartHold", 0f);
                        // A vehicle nobody is using stays held, where it was parked (at its measured resting pose):
                        // released onto its wheels it sank and bounced, which players saw as shaking. One in use, and a
                        // boat, is let go to fly or float.
                        g.If(g.Not(g.B("Dead")), () => g.If(g.Or(g.Or(g.B("Piloting"), g.ObjVar("IsBoat", typeof(bool))), g.B("Riding")),
                            () => g.Set(RB, "constraints", null, RigidbodyConstraints.None),
                            () =>
                            {
                                g.SetGraphVar("Held", true);
                                g.Set(RB, "interpolation", null, RigidbodyInterpolation.None);
                            }));
                    }));

                // ---- twice a second on every client ----------------------------------------------------------
                var guardLeft = g.SetGraphVar("GuardTimer", g.F("GuardTimer") - soundElapsed);
                g.If(g.Less(guardLeft, 0f), () =>
                {
                    g.SetGraphVar("GuardTimer", 0.5f);

                    // the other seats open only once there is a pilot
                    if (LockOtherSeatsUntilPilot)
                    {
                        var hasPilot = g.Keep(g.Or(g.Or(g.B("Piloting"), g.Greater(g.F("FlownTimer"), 0f)),
                            g.Get(Seat, "IsOccupiedByOther", g.ObjVar("PilotSeatComponent", Seat))));
                        g.If(g.Or(g.And(hasPilot, g.Not(g.B("SeatsOpen"))), g.And(g.Not(hasPilot), g.B("SeatsOpen"))), () =>
                        {
                            g.SetGraphVar("SeatsOpen", hasPilot);
                            SeatClicks(g);
                        });
                    }

                    // Detail by distance. The vehicles' meshes carry Unity's automatic Mesh LODs (each level about
                    // half the triangles of the one before), but the app only moves to a coarser level very
                    // late: from the arrival point every vehicle was drawn in full, 390,000 triangles. So each
                    // vehicle adds its own bias: none within its near distance (around it, and aboard), then one
                    // level coarser for every doubling of the distance.
                    var viewer = g.Keep(g.PlatformValue("GetLocalUserState", "Position", V3T));
                    // (the scripted test pilot is aboard in spirit: its pictures are taken from the cockpit)
                    var coarser = g.Keep(g.Select(g.B("TestPilot"), 0f, g.Clamp(g.Call(typeof(Mathf), "Log", new[] { typeof(float), typeof(float) }, null,
                        g.Max(g.Distance(viewer, g.Get(TF, "position")) / g.P("LodNear"), 1f), 2f), 0f, 6f)));
                    g.If(g.Greater(g.Abs(coarser - g.F("LodBias")), 0.3f), () =>
                    {
                        g.SetGraphVar("LodBias", coarser);
                        g.ForEach(g.ObjVar("LodRenderers", typeof(System.Collections.IEnumerable)), typeof(Renderer), drawn =>
                            g.Set(typeof(Renderer), "meshLodSelectionBias", drawn, coarser));
                    });

                    // Passengers. A seat does not say who sits in it, so a player whose head is at one of the
                    // passenger seats' eye points counts as aboard: they get the cabin's own objects and
                    // sounds. Only judged while the vehicle is slow (the head position a graph reads trails a
                    // fast vehicle); once aboard they stay aboard until they are let out of a seat.
                    g.If(g.And(g.And(g.And(g.Not(g.B("Piloting")), g.Not(g.B("TestPilot"))), g.Less(g.F("MovedSpeed"), 5f)),
                             g.Or(g.B("Riding"), g.Less(g.Distance(viewer, g.Get(TF, "position")), g.P("AboardRadius")))), () =>
                    {
                        g.SetGraphVar("NearSeat", false);
                        var head = g.Keep(g.PlatformValue("GetLocalUserState", "Position", V3T));
                        g.ForEach(g.ObjVar("PassengerEyePoints", typeof(System.Collections.IEnumerable)), TF, eye =>
                            g.If(g.Less(g.Distance(head, g.Get(TF, "position", eye)), 1f), () => g.SetGraphVar("NearSeat", true)));
                        g.If(g.And(g.B("NearSeat"), g.And(g.Not(g.B("Riding")), g.Not(g.B("Dead")))), () => Ride(g, true));
                        g.If(g.And(g.Not(g.B("NearSeat")), g.B("Riding")), () => Ride(g, false));
                    });

                    // A boat rides the water by this graph, every physics step. With nobody within 250 m of it and
                    // nobody aboard it is held where it floats instead (as parked aircraft are held), and the clock
                    // stops sending it frames and steps: its dock is 4.8 km from the airfield, and it was floating
                    // away the largest share of the fleet's script time there.
                    g.If(g.ObjVar("IsBoat", typeof(bool)), () =>
                    {
                        var wanted = g.Keep(g.Or(g.Or(g.B("Awake"), g.B("Piloting")), g.Less(g.Distance(viewer, g.Get(TF, "position")), 250f)));
                        var busy = g.Keep(g.ObjVar("Busy", typeof(bool)));
                        g.If(g.And(wanted, g.Not(busy)), () =>
                        {
                            g.SetObjVar("Busy", true);
                            g.If(g.B("Held"), () =>
                            {
                                g.SetGraphVar("Held", false);
                                g.If(g.Not(g.B("Dead")), () => g.Set(RB, "constraints", null, RigidbodyConstraints.None));
                                g.Set(RB, "interpolation", null, RigidbodyInterpolation.Interpolate);
                                g.Do(RB, "WakeUp", Type.EmptyTypes, null);
                            });
                        });
                        g.If(g.And(g.And(g.Not(wanted), busy), g.And(g.Not(g.B("Dead")), g.Less(g.F("StartHold"), 0.0001f))), () =>
                        {
                            g.SetObjVar("Busy", false);
                            g.SetGraphVar("Held", true);
                            g.Set(RB, "constraints", null, RigidbodyConstraints.FreezeAll);
                            g.Set(RB, "interpolation", null, RigidbodyInterpolation.None);
                        });
                    });

                    // Parts drawn only for players close by or aboard: an airliner's cabin is more triangles than
                    // the rest of the fleet together, and nobody across the apron can see into it.
                    g.If(g.ObjVar("HasNearObjects", typeof(bool)), () =>
                    {
                        var close = g.Keep(g.Or(g.Or(g.B("Piloting"), g.B("Riding")), g.Or(g.B("TestPilot"),
                            g.Less(g.Distance(viewer, g.Get(TF, "position")), g.P("NearWithin") + g.Select(g.B("NearShown"), 6f, 0f)))));
                        g.If(g.Or(g.And(close, g.Not(g.B("NearShown"))), g.And(g.Not(close), g.B("NearShown"))), () =>
                        {
                            g.SetGraphVar("NearShown", close);
                            g.ForEach(g.ObjVar("NearObjects", typeof(System.Collections.IEnumerable)), typeof(GameObject), part =>
                                g.Do(typeof(GameObject), "SetActive", new[] { typeof(bool) }, part, close));
                        });
                    });

                    // Doze. A vehicle that is resting, with nothing pending and no player near it or its reset
                    // sign, switches this whole graph off: twenty parked vehicles otherwise each answer every
                    // frame, every physics step and every controller event only to find they have nothing to do.
                    // The fleet watch (BuildFleetWatch) switches it back on when a player comes near, when the
                    // vehicle is moved, or when dozing is turned off. A boat never dozes, nor anything else while its
                    // floats are in the water: they float by this graph.
                    var settled = g.And(g.And(g.Not(g.B("Awake")), g.Not(g.B("Dead"))),
                        g.And(g.And(g.Not(g.B("Piloting")), g.Not(g.B("Riding"))),
                            g.And(g.Not(g.B("TestPilot")), g.And(g.Not(g.B("Floating")), g.Not(g.ObjVar("IsBoat", typeof(bool)))))));
                    var nothingPending = g.Less(g.Max(g.Max(g.F("StartHold"), g.F("EjectTimer")), g.Max(g.F("BrakeTimer"), g.F("ResetTimer"))), 0.0001f);
                    var alone = g.And(g.Greater(g.Distance(viewer, g.Get(TF, "position")), g.P("DozeBeyond")),
                        g.Greater(g.Distance(viewer, g.Get(TF, "position", g.Get(typeof(GameObject), "transform", g.ObjVar("ResetSign", typeof(GameObject))))),
                            FleetParts.SignWakeDistance + 3f));
                    g.If(g.And(g.And(settled, nothingPending), g.And(alone, g.ObjVarOf(g.ObjVar("Watch", typeof(GameObject)), "DozeOn", typeof(bool)))),
                        () => g.If(g.Greater(g.SetGraphVar("DozeWait", g.F("DozeWait") + 0.5f), 2.9f), () => FlightGraphs.Doze(g)),
                        () => g.SetGraphVar("DozeWait", 0f));
                });

                // Only with LiftSpeedCapForOlderApps: lift the cap again every second, in case the app puts it
                // back (it applies it when it starts simulating an object, which can be after this graph starts).
                if (LiftSpeedCapForOlderApps)
                {
                    var limitLeft = g.SetGraphVar("LimitTimer", g.F("LimitTimer") - soundElapsed);
                    g.If(g.Less(limitLeft, 0f), () =>
                    {
                        g.SetGraphVar("LimitTimer", 1f);
                        NoSpeedLimit(g);
                    });
                }

                // The seat lifts a player it lets go of a frame later; the trip back to the arrival point
                // waits for that, so it is always the last word on where the player ends up.
                g.If(g.Greater(g.F("EjectTimer"), 0f), () =>
                {
                    var left = g.SetGraphVar("EjectTimer", g.F("EjectTimer") - soundElapsed);
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
                    var left = g.SetGraphVar("RespawnTimer", g.F("RespawnTimer") - soundElapsed);
                    g.If(g.Less(left, 0f), () => Revive(g));
                });

                // The wheels are part of the airframe the animator switches off while it is a wreck, and a
                // wheel forgets its brake when it is switched off: brake again once they are back.
                g.If(g.Greater(g.F("BrakeTimer"), 0f), () =>
                {
                    var left = g.SetGraphVar("BrakeTimer", g.F("BrakeTimer") - soundElapsed);
                    g.If(g.Less(left, 0.0001f), () =>
                    {
                        g.SetGraphVar("BrakeTimer", 0f);
                        g.If(g.Not(g.B("Piloting")), () => ParkingBrake(g));
                        IgnoreSea(g);       // (colliders forget what they were told to ignore when they are switched off and on)
                    });
                });

                // Ownership arrives a moment after it is asked for; only the owner can move the aircraft.
                g.If(g.Greater(g.F("ResetTimer"), 0f), () =>
                {
                    g.SetGraphVar("ResetTimer", g.F("ResetTimer") - soundElapsed);
                    g.If(g.Call(Sync, "_DoIOwn", Type.EmptyTypes, null), () =>
                    {
                        g.SetGraphVar("ResetTimer", 0f);
                        MoveHome(g);
                        g.SetGraphVar("Health", g.P("FullHealth"));
                    });
                });
                });      // (end of the ten-times-a-second block)
            });

            // ---- the pilot gets out -------------------------------------------------------------------------
            // Getting off a seat always teleports the player (the seat lifts them clear); sitting down does
            // not. Measured in the app on a Quest, 2026-11-21. Comparing head and seat positions is no good
            // at speed: the head position a graph can read trails the seat by about 50 ms, which is 2.5 m
            // at 50 m/s, so a distance test "loses" the pilot as soon as the aircraft is fast.
            // (The scripted test pilot has no body: the editor's own arrival teleport must not unseat it.)
            g.On(g.Platform("OnPlayerTeleported"), _ =>
            {
                g.If(g.And(g.B("Piloting"), g.Not(g.B("TestPilot"))), () => Leave(g));
                g.If(g.B("Riding"), () => Ride(g, false));
            });

            // ---- per frame while piloting: seat fit, instruments, controls --------------------------------------
            void PilotFrame()
            {
                AdvancedFrame(g);
                g.If(g.Greater(g.F("EngineStarting"), 0f), () =>
                    g.If(g.Less(g.SetGraphVar("EngineStarting", g.F("EngineStarting") - g.Get(typeof(Time), "deltaTime")), 0.0001f), () => EngineSwitch(g, true)));
                // (Seats are not slid to fit the player any more, here or for passengers: every other client draws a
                // rider against its own copy of the seat, so a seat moved on the sitter's client alone showed them off
                // their seat by the slide on everyone else's screen. The builder puts each seat where the app's seat
                // code puts an average avatar's eyes on the cockpit's eye point: FleetParts.SeatFor.)

                var timer = g.SetGraphVar("HudTimer", g.F("HudTimer") + g.Get(typeof(Time), "deltaTime"));
                g.If(g.Greater(timer, 0.25f), () =>
                {
                    g.SetGraphVar("HudTimer", 0f);
                    g.If(g.B("Advanced"), () => AdvancedLit(g));
                    FunctionsTick(g);

                    // Safety net for a pilot who left without a teleport: only trusted when nearly stopped.
                    var head = g.PlatformValue("GetLocalUserState", "Position", V3T);
                    var seat = g.Get(TF, "position", g.Get(typeof(GameObject), "transform", g.ObjVar("PilotSeat", typeof(GameObject))));
                    g.If(g.And(g.And(g.Not(g.B("TestPilot")), g.Less(g.F("Speed"), 5f)), g.Greater(g.Distance(head, seat), 2.5f)),
                        () => Leave(g));
                    // Figures, as SAV_HUDController writes them: G now and the most pulled, Mach, climb rate
                    // (ft/min) over altitude (ft), speed (kt), angle of attack.
                    var format = new[] { typeof(string), typeof(object), typeof(object), typeof(object) };
                    Type uiText = typeof(UnityEngine.UI.Text);
                    var speed = g.Keep(g.F("Speed"));
                    var velocity = g.Keep(g.Get(RB, "linearVelocity"));
                    var vertGs = g.Keep(g.F("VertGs"));
                    g.If(g.Greater(g.Abs(vertGs), g.Abs(g.F("MaxGs"))), () => g.SetGraphVar("MaxGs", vertGs));
                    g.Set(uiText, "text", g.ObjVar("HudG", uiText),
                        g.Call(typeof(string), "Format", format, null, "{0:F1}\n{1:F1}", vertGs, g.F("MaxGs"), 0f));
                    g.Set(uiText, "text", g.ObjVar("HudMach", uiText),
                        g.Call(typeof(string), "Format", format, null, "{0:F2}", speed / 343f, 0f, 0f));
                    var altitude = g.Keep((g.Y(g.Get(RB, "worldCenterOfMass")) - g.P("SeaLevel")) * g.P("HudAltitudeConversion"));
                    g.Set(uiText, "text", g.ObjVar("HudAltitude", uiText),
                        g.Call(typeof(string), "Format", format, null, "{0:F0}\n{1:F0}", g.Y(velocity) * 60f * g.P("HudAltitudeConversion"), altitude, 0f));
                    var knots = g.Keep(g.Call(typeof(string), "Format", format, null, "{0:F0}", speed * g.P("HudSpeedConversion"), 0f, 0f));
                    g.Set(uiText, "text", g.ObjVar("HudKnots", uiText), knots);
                    g.Set(uiText, "text", g.ObjVar("HudAirspeed", uiText), knots);
                    g.Set(uiText, "text", g.ObjVar("HudAoA", uiText), g.Select(g.Less(speed, 2f), "",
                        g.Call(typeof(string), "Format", format, null, "{0:F0}", g.F("AoAPitch"), 0f, 0f), typeof(string)));

                    // Vehicles without a head-up display of their own get a plain readout instead.
                    g.Set(typeof(TMPro.TMP_Text), "text", g.ObjVar("HudText", typeof(TMPro.TMP_Text)),
                        g.Call(typeof(string), "Format", format, null, "{0:0} kt\n{1:0} ft\nTHR {2:0}%", speed * 1.944f, altitude, g.F("Throttle") * 100f));
                });

                // Joystick, throttle lever, control surfaces and nozzle follow the pilot's inputs (0.5 = centred).
                AnimFloat(g, "pitchinput", g.F("InPitch") * 0.5f + 0.5f);
                AnimFloat(g, "yawinput", g.F("InYaw") * 0.5f + 0.5f);
                AnimFloat(g, "rollinput", g.F("InRoll") * 0.5f + 0.5f);
                AnimFloat(g, "throttle", g.F("Throttle"));
                AnimFloat(g, "engineoutput", g.F("EngineOutput"));
                AnimFloat(g, "AoA", g.Select(g.Greater(g.F("Speed"), 20f), g.F("AoA") * 0.00555555556f, 0f));
                AnimFloat(g, "Gs", g.Select(g.Greater(g.F("Speed"), 20f), g.F("VertGs") * 0.005f + 0.5f, 0.5f));
                AnimFloat(g, "brake", g.F("Brake"));
                AnimFloat(g, "vtolangle", g.F("VTOLAngle"));
                AnimBool(g, "onground", g.B("Taxiing"));

                // Wing-tip trails stream while pulling G (SAV_EffectsController; the on and off points differ
                // so they do not flicker).
                var pulling = g.Keep(g.Abs(g.F("VertGs")));
                g.If(g.And(g.Not(g.B("TrailsOn")), g.Greater(pulling, g.P("TrailGs"))), () =>
                {
                    g.SetGraphVar("TrailsOn", true);
                    g.ForEach(g.ObjVar("WingTrails", typeof(System.Collections.IEnumerable)), typeof(ParticleSystem), trail =>
                        g.Do(typeof(ParticleSystem), "Play", Type.EmptyTypes, trail));
                });
                g.If(g.And(g.B("TrailsOn"), g.Less(pulling, g.P("TrailGs") * 0.8f)), () => TrailsOff(g));

                // Head-up display symbology (SAV_HUDController): the flight-path marker sits where the
                // vehicle is actually going, the pitch ladder and heading tape stay fixed to the world.
                var hud = g.ObjVar("HudRoot", TF);
                g.If(g.ObjVar("HudFollowsEye", typeof(bool)), () => g.Set(TF, "position", hud, g.Get(TF, "position", g.ObjVar("EyePoint", TF))));
                var hudPosition = g.Keep(g.Get(TF, "position", hud));
                var flightPath = g.Keep(g.Get(RB, "linearVelocity"));
                var marker = g.ObjVar("HudVelocity", TF);
                g.Set(TF, "position", marker, hudPosition + g.Select(g.Less(g.Magnitude(flightPath), 2f), new Vector3(0f, -2f, 0f), flightPath, V3T));
                g.Set(TF, "localPosition", marker, g.Scale(g.Get(V3T, "normalized", g.Get(TF, "localPosition", marker)), g.P("HudDistance")));
                g.Set(TF, "rotation", marker, g.Call(typeof(Quaternion), "LookRotation", new[] { V3T, V3T }, null,
                    g.Get(TF, "position", marker) - hudPosition, g.Get(TF, "up", hud)));
                var attitude = g.Keep(g.Get(TF, "eulerAngles"));
                var euler = new[] { typeof(float), typeof(float), typeof(float) };
                g.Set(TF, "localRotation", g.ObjVar("HudHeading", TF),
                    g.Call(typeof(Quaternion), "Euler", euler, null, 0f, -g.Y(attitude) + 180f, 0f));
                g.Set(TF, "rotation", g.ObjVar("HudElevation", TF),
                    g.Call(typeof(Quaternion), "Euler", euler, null, 0f, g.Y(attitude), 0f));
                g.Set(TF, "localRotation", g.ObjVar("HudDown", TF),
                    g.Call(typeof(Quaternion), "Euler", euler, null, 0f, 0f, -g.Z(attitude)));

                // Analogue instruments (SaccGaugeController): the dials are a little animator of their own whose
                // needles follow these numbers; the artificial horizon is a ball turned against the attitude.
                g.If(g.ObjVar("HasGauges", typeof(bool)), () =>
                {
                    var feet = g.Keep((g.Y(g.Get(RB, "worldCenterOfMass")) - g.P("SeaLevel")) * 3.28084f);
                    var motion = g.Keep(g.Get(RB, "linearVelocity"));
                    var attitudeNow = g.Keep(g.Get(TF, "eulerAngles"));
                    var vehicleUp = g.Keep(g.Get(TF, "up"));
                    var slip = g.Keep(g.Select(g.Less(g.F("Speed"), 5f), 0f,
                        g.Clamp01(g.Call(V3T, "SignedAngle", new[] { V3T, V3T, V3T }, null, g.Get(TF, "forward"),
                            g.Call(V3T, "ProjectOnPlane", new[] { V3T, V3T }, null, motion, vehicleUp), vehicleUp) / g.P("GaugeMaxSlip") * 0.5f + 0.5f)));
                    var ascent = g.Keep(g.Clamp01(g.Y(motion) * 196.85f / g.P("GaugeMaxAscent") * 0.5f + 0.5f));     // feet per minute
                    var compass = g.Keep(g.Call(typeof(Mathf), "Repeat", new[] { typeof(float), typeof(float) }, null, g.Y(attitudeNow) / 360f, 1f));
                    var airspeed = g.Keep(g.F("Speed") / g.P("GaugeMaxSpeed"));
                    g.ForEach(g.ObjVar("DialsAnimators", typeof(System.Collections.IEnumerable)), AN, dials =>
                    {
                        void Dial(string name, object value) =>
                            g.Do(AN, "SetFloat", new[] { typeof(string), typeof(float) }, dials, name, value);
                        Dial("altitude100", feet / 100f);
                        Dial("altitude1000", feet / 1000f);
                        Dial("altitude10000", feet / 10000f);
                        Dial("fuel", 1f);
                        Dial("speed", airspeed);
                        Dial("engine", g.F("EngineOutput"));
                        Dial("slip", slip);
                        Dial("ascent", ascent);
                        Dial("compass", compass);
                    });
                    g.ForEach(g.ObjVar("HorizonBalls", typeof(System.Collections.IEnumerable)), TF, ball =>
                        g.Set(TF, "localRotation", ball, g.Call(typeof(Quaternion), "Euler", new[] { typeof(float), typeof(float), typeof(float) }, null,
                            g.X(attitudeNow), g.Z(attitudeNow), 0f)));
                });

                // An airliner's flight displays (the VAU A320's primary flight display, navigation display, standby
                // instrument and engine page): each is an animator of its own fed with numbers between 0 and 1,
                // scaled as its original script scaled them (PFDBasicDisplay, NDDisplay, ECAMDisplay).
                // They are redrawn about eighteen times a second, not every frame: each display is a canvas of moving
                // parts, and a headset spent 9 ms a frame rebuilding thirty of them. Their animators are switched off
                // and stepped from here, so between steps nothing on the canvases moves and nothing is rebuilt.
                var sinceDisplays = g.SetGraphVar("DisplayTimer", g.F("DisplayTimer") + g.Get(typeof(Time), "deltaTime"));
                g.If(g.And(g.ObjVar("HasFlightDisplays", typeof(bool)), g.Greater(sinceDisplays, g.P("DisplayInterval"))), () =>
                {
                    g.SetGraphVar("DisplayTimer", 0f);
                    Type mathf = typeof(Mathf), two = typeof(float);
                    Val Repeat(object value, float length) => g.Call(mathf, "Repeat", new[] { two, two }, null, value, length);
                    Val Whole(object value) => g.Call(mathf, "Floor", new[] { two }, null, value);
                    var feet = g.Keep(g.Max((g.Y(g.Get(RB, "worldCenterOfMass")) - g.P("SeaLevel")) * 3.28084f, 0f));
                    var motion = g.Keep(g.Get(RB, "linearVelocity"));
                    var attitudeNow = g.Keep(g.Get(TF, "eulerAngles"));
                    var vehicleUp = g.Keep(g.Get(TF, "up"));
                    var knots = g.Keep(g.F("Speed") * 1.94384f);
                    // nose up and right wing down are positive
                    var pitchUp = g.Keep(-g.Call(mathf, "DeltaAngle", new[] { two, two }, null, 0f, g.X(attitudeNow)));
                    var bankRight = g.Keep(-g.Call(mathf, "DeltaAngle", new[] { two, two }, null, 0f, g.Z(attitudeNow)));
                    var heading = g.Keep(g.Y(attitudeNow));
                    var slipAngle = g.Keep(g.Select(g.Less(g.F("Speed"), 5f), 0f,
                        g.Call(V3T, "SignedAngle", new[] { V3T, V3T, V3T }, null, g.Get(TF, "forward"),
                            g.Call(V3T, "ProjectOnPlane", new[] { V3T, V3T }, null, motion, vehicleUp), vehicleUp)));
                    var climbRate = g.Keep(g.Y(motion) * 196.85f);                  // feet per minute
                    var height = g.Keep((g.Y(g.Get(TF, "position")) - g.Y(g.GraphVar("SpawnPosition", V3T))) * 3.28084f);   // above where it parks
                    var list = typeof(System.Collections.IEnumerable);
                    g.ForEach(g.ObjVar("AttitudeDisplays", list), AN, display =>
                    {
                        void Feed(string name, object value) => g.Do(AN, "SetFloat", new[] { typeof(string), two }, display, name, value);
                        Feed("AirSpeedNormalize", knots / 600f);
                        Feed("PitchAngleNormalize", g.Clamp01((pitchUp + 90f) / 180f));
                        Feed("BankAngleNormalize", g.Clamp01((bankRight + 180f) / 360f));
                        Feed("HeadingNormalize", Repeat(heading + 180f, 360f) / 360f);
                        Feed("AltitudeNormalize", feet / 99990f);
                        Feed("Altitude10Normalize", Repeat(feet, 100f) / 100f);
                        Feed("Altitude100Normalize", Repeat(Whole(feet / 100f), 10f) / 10f);
                        Feed("Altitude1000Normalize", Repeat(Whole(feet / 1000f), 10f) / 10f);
                        Feed("Altitude10000Normalize", Repeat(Whole(feet / 10000f), 10f) / 10f);
                        Feed("VerticalSpeedNormalize", g.Clamp01((climbRate + 6000f) / 12000f));
                        Feed("SlipAngleNormalize", g.Clamp01((slipAngle + 40f) / 80f));
                    });
                    void Step(string displays) => g.ForEach(g.ObjVar(displays, list), AN, display =>
                        g.Do(AN, "Update", new[] { two }, display, sinceDisplays));
                    g.ForEach(g.ObjVar("PrimaryDisplays", list), AN, display =>
                    {
                        void Feed(string name, object value) => g.Do(AN, "SetFloat", new[] { typeof(string), two }, display, name, value);
                        Feed("RHNormalize", g.Clamp01((height + 600f) / 1200f));
                        Feed("TRKPCHNormalize", 0.5f);
                        Feed("SpeedTrend", 0.5f);
                        Feed("PilotInputX", g.F("InRoll") * 0.5f + 0.5f);
                        Feed("PilotInputY", g.F("InPitch") * 0.5f + 0.5f);
                        // speed tape markers (never-exceed, stall warning, lowest selectable, next flap limit) and the
                        // selected speed: fixed clean-wing figures, as nothing here selects a speed
                        Feed("VMAXNormalize", 350f / 360f);
                        Feed("VSWNormalize", 140f / 300f);
                        Feed("VLSNormalize", 150f / 200f);
                        Feed("VFENEXTNormalize", 230f / 240f);
                        Feed("AirSpeedSelectNormalize", 250f / 500f);
                    });
                    g.ForEach(g.ObjVar("NavigationDisplays", list), AN, display =>
                    {
                        void Feed(string name, object value) => g.Do(AN, "SetFloat", new[] { typeof(string), two }, display, name, value);
                        Feed("HeadingNormalize", heading / 360f);
                        Feed("SlipAngleNormalize", g.Clamp01((slipAngle + 50f) / 100f));
                    });
                    g.ForEach(g.ObjVar("EngineDisplays", list), AN, display =>
                    {
                        void Feed(string name, object value) => g.Do(AN, "SetFloat", new[] { typeof(string), two }, display, name, value);
                        // fan speed as a fraction of its reference (idle about 0.19, take-off 1.04) over the gauge's 1.2
                        var fan = g.Keep((g.F("EngineOutput") * 0.85f + 0.19f) / 1.2f);
                        var exhaust = g.Keep((g.F("EngineOutput") * 600f + 420f) / 1000f);
                        foreach (var engine in new[] { "ENG1", "ENG2" })
                        {
                            Feed(engine + "N1", fan);
                            Feed(engine + "N1Cmd", g.F("Throttle") * 0.85f + 0.19f);
                            Feed(engine + "EGT", exhaust);
                        }
                        Feed("flapPos", g.F("FlapPos") * g.P("FlapAngleOut"));
                        Feed("slatPos", g.F("FlapPos") * g.P("SlatAngleOut"));
                    });
                    Step("AttitudeDisplays");
                    Step("NavigationDisplays");
                    Step("EngineDisplays");
                });

                // Propeller follows engine output (aircraft without one point this at an empty object).
                g.Do(TF, "Rotate", new[] { typeof(float), typeof(float), typeof(float) }, g.ObjVar("Propeller", TF),
                    0f, 0f, g.F("EngineOutput") * g.P("PropellerSpeed") * g.Get(typeof(Time), "deltaTime"));
            }

            // ---- physics (pilot only) ------------------------------------------------------------------------
            g.On(Custom(g, "fw_step"), _ =>
            {
                // A boat must float whether or not anyone is aboard, on whichever client is responsible for it.
                // (Also before anybody owns it: in the first moments after a world loads every client simulates its own
                // copy, and without its floats the boat sank 250 m before the app put it back.)
                g.If(g.ObjVar("HasFloats", typeof(bool)), () =>
                    g.If(g.And(g.Not(g.B("Dead")), g.Or(Responsible(g), g.Not(g.Get(RB, "isKinematic")))), () => Floats(g)));
                g.If(g.B("Piloting"), () => Physics(g));
            });
            return g;
        }

        static Type Sync => GraphTypes.Find("BS.BSSyncedObject");
        static Type Seat => GraphTypes.Find("BS.BSSeat");

        /// <summary>True on the client that should decide this aircraft's fate.</summary>
        static Val Responsible(GraphWriter g) =>
            g.Or(g.Or(g.B("Piloting"), g.B("TestPilot")), g.Call(Sync, "_DoIOwn", Type.EmptyTypes, null));

        /// <summary>The local player stops being the pilot: controls back to walking, aircraft braked.</summary>
        static void Leave(GraphWriter g)
        {
            g.SetGraphVar("Piloting", false);
            g.SetGraphVar("Throttle", 0f);
            BlockSticks(g, false);
            ParkingBrake(g);
            Unpiloted(g);
            Cockpit(g, false);
        }

        static void TrailsOff(GraphWriter g)
        {
            g.SetGraphVar("TrailsOn", false);
            g.ForEach(g.ObjVar("WingTrails", typeof(System.Collections.IEnumerable)), typeof(ParticleSystem), trail =>
                g.Do(typeof(ParticleSystem), "Stop", Type.EmptyTypes, trail));
        }

        static Val Anim(GraphWriter g) => g.ObjVar("VehicleAnimator", AN);
        static void AnimFloat(GraphWriter g, string name, object value) =>
            g.Do(AN, "SetFloat", new[] { typeof(string), typeof(float) }, Anim(g), name, value);
        static void AnimBool(GraphWriter g, string name, object value) =>
            g.Do(AN, "SetBool", new[] { typeof(string), typeof(bool) }, Anim(g), name, value);
        static void AnimTrigger(GraphWriter g, string name) =>
            g.Do(AN, "SetTrigger", new[] { typeof(string) }, Anim(g), name);

        /// <summary>
        /// What only the person flying sees and hears: the head-up display, instruments, stick and throttle
        /// lever, warning tones (the vehicle's "enable in vehicle" objects), and the animator's local-pilot
        /// state. Controls are left centred when the pilot goes.
        /// </summary>
        /// <summary>Animator, effects and sounds on (see the ten-hertz tick); harmless when they already are.</summary>
        static void Wake(GraphWriter g)
        {
            g.SetGraphVar("IdleTimer", 6f);
            g.If(g.B("Held"), () =>
            {
                g.SetGraphVar("Held", false);
                g.If(g.Not(g.B("Dead")), () => g.Set(RB, "constraints", null, RigidbodyConstraints.None));
                g.Set(RB, "interpolation", null, RigidbodyInterpolation.Interpolate);
                g.Do(RB, "WakeUp", Type.EmptyTypes, null);
            });
            g.If(g.Not(g.B("Awake")), () =>
            {
                g.SetGraphVar("Awake", true);
                g.ForEach(g.ObjVar("RestingObjects", typeof(System.Collections.IEnumerable)), typeof(GameObject), item =>
                    g.Do(typeof(GameObject), "SetActive", new[] { typeof(bool) }, item, true));
                g.Set(AN, "enabled", Anim(g), true);
            });
        }

        /// <summary>A stock Custom Event on this vehicle: the fleet clock triggers these.</summary>
        static CustomEvent Custom(GraphWriter g, string name)
        {
            var unit = g.Add(new CustomEvent());
            g.Feed(unit, "name", name);
            return unit;
        }

        /// <summary>Trigger a custom event on another object's graph.</summary>
        static void Trigger(GraphWriter g, Val target, string name)
        {
            var unit = g.Add(new TriggerCustomEvent());
            g.Feed(unit, "name", name);
            g.Feed(unit, "target", target);
            g.Chain(unit, "enter", "exit");
        }

        /// <summary>Tell the fleet clock that this is, or no longer is, the vehicle the local player pilots.</summary>
        static void Fly(GraphWriter g, bool flying)
        {
            var watch = g.ObjVar("Watch", typeof(GameObject));
            if (flying) g.SetObjVar("PilotVehicle", g.Get(TF, "gameObject"), watch);
            g.SetObjVar("HasPilotVehicle", flying, watch);
        }

        /// <summary>Switch this vehicle's own graph off, until the fleet watch switches it back on.</summary>
        internal static void Doze(GraphWriter g)
        {
            g.SetGraphVar("DozeWait", 0f);
            g.SetGraphVar("Dozed", true);
            g.SetObjVar("DozeLod", g.F("LodBias"));
            g.SetObjVar("DozePosition", g.Get(TF, "position"));
            g.SetObjVar("Dozing", true);
            g.Set(typeof(ScriptMachine), "enabled", g.ObjVar("Machine", typeof(ScriptMachine)), false);
        }

        static void Rest(GraphWriter g)
        {
            // A vehicle nobody is using is also held where it stands, as the cars are: a parked aircraft is a body on
            // its wheels, and a player walking or teleporting into one pushed 60 tonnes of airliner 8 m aside. Not
            // interpolated while held, so that the app moving it from outside (its idle reset) is simply its new
            // place. A boat is left free, to ride the waves; a craft that hovers on its floats is held at its hover.
            g.If(g.And(g.And(g.Not(g.B("Dead")), g.Less(g.F("StartHold"), 0.0001f)),
                    g.Not(g.ObjVar("IsBoat", typeof(bool)))), () =>
            {
                g.SetGraphVar("Held", true);
                g.Set(RB, "constraints", null, RigidbodyConstraints.FreezeAll);
                g.Set(RB, "interpolation", null, RigidbodyInterpolation.None);
            });
            g.SetGraphVar("Awake", false);
            g.Set(AN, "enabled", Anim(g), false);
            g.ForEach(g.ObjVar("RestingObjects", typeof(System.Collections.IEnumerable)), typeof(GameObject), item =>
                g.Do(typeof(GameObject), "SetActive", new[] { typeof(bool) }, item, false));
        }

        /// <summary>A passenger gets the cabin objects every rider shares (not the pilot's own instruments).</summary>
        static void Ride(GraphWriter g, bool aboard)
        {
            if (aboard) Wake(g);
            g.SetGraphVar("Riding", aboard);
            g.ForEach(g.ObjVar("SharedInVehicleObjects", typeof(System.Collections.IEnumerable)), typeof(GameObject), item =>
                g.Do(typeof(GameObject), "SetActive", new[] { typeof(bool) }, item, aboard));
            AnimBool(g, "localpassenger", aboard);
            SeatClicks(g);
        }

        /// <summary>
        /// Which of this vehicle's seats the local player can click. None while they are aboard it (piloting or
        /// riding): the right trigger is also the pointer's click, and the pilot seat's click area is around the
        /// pilot and the passenger seats beside them, so a pull of the trigger in flight re-sat the player. The app
        /// then joints their 76 kg body to the moving aircraft afresh, the aircraft takes the jolt, and the G it
        /// reads from it wrecked the airframe in mid-air. From outside: the pilot seat, and the others once there is
        /// a pilot (SeatsOpen). To change seats, get out first.
        /// </summary>
        static void SeatClicks(GraphWriter g)
        {
            var outside = g.Keep(g.Not(g.Or(g.B("Piloting"), g.B("Riding"))));
            g.Set(typeof(Collider), "enabled", g.ObjVar("PilotSeatCollider", typeof(Collider)), outside);
            var open = g.Keep(g.And(outside, g.B("SeatsOpen")));
            g.ForEach(g.ObjVar("PassengerSeatColliders", typeof(System.Collections.IEnumerable)), typeof(Collider), seatCollider =>
                g.Set(typeof(Collider), "enabled", seatCollider, open));
        }

        static void Cockpit(GraphWriter g, bool aboard)
        {
            g.ForEach(g.ObjVar("InVehicleObjects", typeof(System.Collections.IEnumerable)), typeof(GameObject), item =>
                g.Do(typeof(GameObject), "SetActive", new[] { typeof(bool) }, item, aboard));
            AnimBool(g, "localpilot", aboard);
            AnimBool(g, "occupied", aboard);
            AnimBool(g, "EngineOn", aboard);
            SeatClicks(g);
            if (!aboard)
            {
                AdvancedOff(g);
                FunctionsRelease(g);
                g.SetGraphVar("CruiseOn", false);
                g.SetGraphVar("AltHoldOn", false);
                g.SetGraphVar("RightThrustX", 0f);
                g.SetGraphVar("RightThrustZ", 0f);
                TrailsOff(g);
                AnimFloat(g, "Gs", 0.5f);
                AnimFloat(g, "brake", 0f);
                foreach (var axis in new[] { "pitchinput", "yawinput", "rollinput" }) AnimFloat(g, axis, 0.5f);
                AnimFloat(g, "throttle", 0f);
                AnimFloat(g, "engineoutput", 0f);
                AnimFloat(g, "AoA", 0f);
            }
        }

        /// <summary>
        /// Newtons of lift given back at the pilot's seat for the rider the app hangs there (76 kg x 9.81).
        /// Set to 0 if the app stops loading vehicles with their riders.
        /// </summary>
        const float RiderWeight = 745.6f;

        /// <summary>Unity's own default for a body's top speed: no limit in practice.</summary>
        const float UnlimitedSpeed = 1e16f;

        /// <summary>
        /// The app released before 2026-11-21 makes the first player to sit in ANY seat of a vehicle its
        /// "driver", who takes its ownership back every 3 s. A passenger who sat before the pilot would keep
        /// taking the aircraft away from the pilot, whose client is the only one flying it. With this on, the
        /// other seats cannot be clicked until the pilot's seat is taken. The app has since been changed so
        /// that the player whose world takes ownership becomes the driver; once that version is the one in
        /// use, this can go off (it is also turned off to test that change).
        ///
        /// Do not try to keep bystanders off an occupied aircraft by switching the body's collision detection
        /// off on their client: its seats then cannot be hit by a pointer there, so nobody can board
        /// (two-client test, 2026-11-21). The fixed app does not let touch take these aircraft anyway.
        ///
        /// Off since 2026-11-22: players found every passenger seat unclickable (nobody can ride in an empty
        /// aircraft or wait in one for a pilot), and the app on the headset that day was built after the fix.
        /// </summary>
        public const bool LockOtherSeatsUntilPilot = false;

        /// <summary>
        /// Until 2026-11-21 the app held every synced physics object to 50 m/s while a player simulated it,
        /// which pinned every aircraft to 50 m/s. The cap was then removed from the app itself, so the world
        /// leaves the body's speed limit alone and this is off. Turn it on only to make the world flyable on
        /// an app version that still has the cap: the graph then resets the limit to Unity's default at the
        /// start, when a pilot sits, and once a second.
        /// </summary>
        const bool LiftSpeedCapForOlderApps = false;

        static void NoSpeedLimit(GraphWriter g)
        {
            if (LiftSpeedCapForOlderApps) g.Set(RB, "maxLinearVelocity", null, UnlimitedSpeed);
        }

        /// <summary>Nobody is flying: the pilot seat goes back to its authored place.</summary>
        static void Unpiloted(GraphWriter g)
        {
            g.SetGraphVar("SeatTimer", 0f);
            g.SetGraphVar("SeatSliding", false);
            g.Set(TF, "localPosition", PilotSeatTransform(g), g.GraphVar("SeatHome", V3T));
        }

        static Val PilotSeatTransform(GraphWriter g) =>
            g.Get(typeof(GameObject), "transform", g.ObjVar("PilotSeat", typeof(GameObject)));

        /// <summary>
        /// Wheels braked and undriven. Without it an aircraft somebody steps out of keeps rolling, and one
        /// sent home from a crash creeps forward on wheels that are still spinning.
        /// </summary>
        static void ParkingBrake(GraphWriter g)
        {
            g.ForEach(g.ObjVar("AllWheels", typeof(System.Collections.IEnumerable)), typeof(WheelCollider), wheel =>
                g.Set(typeof(WheelCollider), "motorTorque", wheel, 0f));
            g.ForEach(g.ObjVar("BrakeWheels", typeof(System.Collections.IEnumerable)), typeof(WheelCollider), wheel =>
                g.Set(typeof(WheelCollider), "brakeTorque", wheel, g.P("BrakeTorque")));
        }

        /// <summary>True when the local player is inside (or right beside) this aircraft. Not for the editor test.</summary>
        static Val LocalPlayerAboard(GraphWriter g) =>
            g.And(g.Not(g.B("TestPilot")),
                g.Less(g.Distance(g.PlatformValue("GetLocalUserState", "Position", V3T), g.Get(TF, "position")), g.P("AboardRadius")));

        /// <summary>
        /// The aircraft is destroyed. Runs once, on the client responsible for it: stops it dead where it is
        /// and tells everyone else. The synced-object component re-applies isKinematic every frame, so the
        /// wreck is held with frozen constraints instead.
        /// </summary>
        static void Explode(GraphWriter g)
        {
            var wasPilot = g.Keep(g.And(g.B("Piloting"), g.Not(g.B("TestPilot"))));
            g.If(g.B("Piloting"), () =>
            {
                BlockSticks(g, false);
                Cockpit(g, false);
            });
            g.SetGraphVar("Piloting", false);
            g.SetGraphVar("ResetTimer", 0f);
            SeatClicks(g);
            Unpiloted(g);
            g.Set(RB, "linearVelocity", null, Vector3.zero);
            g.Set(RB, "angularVelocity", null, Vector3.zero);
            g.Set(RB, "constraints", null, RigidbodyConstraints.FreezeAll);
            Wreck(g, 0f, wasPilot);
            g.PlatformDo("SendOneShot", ("Data", g.ObjVar("BoomMessage", typeof(string))), ("All Instances", false));
        }

        /// <summary>
        /// What every client does when the aircraft blows up: show the explosion, hide the aircraft and let
        /// others fly through where it was, and get the local player out if they were aboard. A vehicle
        /// must never be moved with riders still in it, so they leave before it goes home.
        /// </summary>
        static void Wreck(GraphWriter g, float extraDelay, Val knownAboard = null)
        {
            g.SetGraphVar("Dead", true);
            g.SetObjVar("Wrecked", true);
            g.SetGraphVar("GunFiring", false);
            AnimBoolNamed(g, g.ObjVar("GunBool", typeof(string)), false);
            Smoke(g, false);
            g.SetGraphVar("RespawnTimer", g.P("RespawnDelay") + extraDelay);
            g.SetGraphVar("Throttle", 0f);
            g.SetGraphVar("EngineOutput", 0f);
            // The vehicle's animator plays its own destruction: explosion effect on; airframe, seats and
            // attached effects and sounds off, until "reappear".
            Wake(g);
            AnimTrigger(g, "explode");
            AnimBool(g, "dead", true);
            g.If(g.B("Riding"), () => Ride(g, false));
            g.Do(typeof(AudioSource), "Play", Type.EmptyTypes, g.ObjVar("ExplosionSound", typeof(AudioSource)));
            g.Set(typeof(TMPro.TMP_Text), "text", g.ObjVar("HudText", typeof(TMPro.TMP_Text)), "");
            g.Set(RB, "detectCollisions", null, false);
            // The pilot is aboard by definition: at speed their head position trails the aircraft by more
            // than its length, so the distance test alone would miss them.
            g.If(knownAboard != null ? g.Or(knownAboard, LocalPlayerAboard(g)) : LocalPlayerAboard(g), () =>
            {
                // Standing up from a seat you are not in does nothing; seats taken by others are skipped.
                g.ForEach(g.ObjVar("Seats", typeof(System.Collections.IEnumerable)), Seat, seat =>
                    g.If(g.Not(g.Get(Seat, "IsOccupiedByOther", seat)), () => g.Do(Seat, "_Stand", Type.EmptyTypes, seat)));
                g.SetGraphVar("EjectTimer", 0.3f);
            });
        }

        /// <summary>The wreck's time is up: the owner moves it home, and everyone shows it again.</summary>
        static void Revive(GraphWriter g)
        {
            g.If(g.Or(g.B("TestPilot"), g.Call(Sync, "_DoIOwn", Type.EmptyTypes, null)), () => MoveHome(g), () => GearHome(g));
            g.Set(RB, "detectCollisions", null, true);
            g.SetGraphVar("Health", g.P("FullHealth"));
            g.SetGraphVar("Dead", false);
            AnimTrigger(g, "reappear");
            AnimBool(g, "dead", false);
            g.SetGraphVar("BrakeTimer", 0.4f);
        }

        /// <summary>Owner only: put the aircraft back on its parking spot, at rest, engine off.</summary>
        static void MoveHome(GraphWriter g)
        {
            Wake(g);
            g.Set(RB, "constraints", null, RigidbodyConstraints.None);
            // Both the transform and the body are set: a transform change alone is overwritten by the body's
            // interpolation whenever a frame passes without a physics step.
            g.Do(TF, "SetPositionAndRotation", new[] { V3T, typeof(Quaternion) }, null,
                g.GraphVar("SpawnPosition", V3T), g.GraphVar("SpawnRotation", typeof(Quaternion)));
            g.Set(RB, "position", null, g.GraphVar("SpawnPosition", V3T));
            g.Set(RB, "rotation", null, g.GraphVar("SpawnRotation", typeof(Quaternion)));
            g.Set(RB, "linearVelocity", null, Vector3.zero);
            g.Set(RB, "angularVelocity", null, Vector3.zero);
            g.Do(RB, "WakeUp", Type.EmptyTypes, null);
            ParkingBrake(g);
            foreach (var name in new[] { "Throttle", "EngineOutput", "LerpedPitch", "LerpedYaw", "LerpedRoll", "InPitch", "InYaw", "InRoll" })
                g.SetGraphVar(name, 0f);
            g.SetGraphVar("LastVel", Vector3.zero);
            g.SetGraphVar("VertGs", 1f);
            g.SetGraphVar("GsAll", Vector3.up);
            g.SetGraphVar("TaxiLerper", 0f);
            GearHome(g);
            // Everyone else saw the gear go up (a one-shot from the pilot): tell them it is down again.
            g.PlatformDo("SendOneShot", ("Data", g.Call(typeof(string), "Concat", new[] { typeof(string), typeof(string) }, null,
                "home:", g.ObjVar("BoomMessage", typeof(string)))), ("All Instances", false));
            g.SetGraphVar("VTOLAngle", g.P("VTOLDefaultValue"));
            g.SetGraphVar("VTOLAngleInput", g.P("VTOLDefaultValue"));
        }

        /// <summary>
        /// Gear down and locked and flaps as parked, at once (no travel): an aircraft that is reset or respawns
        /// stands on its wheels. Runs on every client.
        /// </summary>
        static void GearHome(GraphWriter g)
        {
            g.SetGraphVar("GearUp", false);
            g.SetGraphVar("GearPos", 1f);
            AnimBool(g, "gearup", false);
            AnimTrigger(g, "instantgeardown");
            g.SetGraphVar("FlapsOn", g.ObjVar("FlapsDefaultOn", typeof(bool)));
            g.SetGraphVar("FlapPos", g.Select(g.ObjVar("FlapsDefaultOn", typeof(bool)), 1f, 0f));
            AnimBool(g, "flaps", g.ObjVar("FlapsDefaultOn", typeof(bool)));
            FunctionsHome(g);           // and rearmed, everything switched off (Sacc's respawn)
        }

        /// <summary>While flying, the thumbsticks steer the aircraft instead of moving or turning the player.</summary>
        static void BlockSticks(GraphWriter g, bool block)
        {
            g.PlatformDo("SetBlockLeftThumbstick", ("Block Left Thumbstick", block));
            g.PlatformDo("SetBlockRightThumbstick", ("Block Right Thumbstick", block));
        }

        /// <summary>
        /// Buoyancy (SAV_FloatScript): each float point is pushed up by how deep it sits under the waves, with
        /// extra resistance while it is still going down; the water also resists sliding sideways much more
        /// than moving ahead, and damps turning. The original looks for the water with rays against a water
        /// collider; this world's sea is one flat level, so the level is a number.
        /// </summary>
        static void Floats(GraphWriter g)
        {
            var dt = g.Keep(g.Get(typeof(Time), "fixedDeltaTime"));
            var radius = g.Keep(g.P("FloatRadius"));
            var waveTime = g.Keep(g.Get(typeof(Time), "time") * g.P("WaveSpeed"));
            g.SetGraphVar("DepthSum", 0f);
            g.ForEach(g.ObjVar("FloatPoints", typeof(System.Collections.IEnumerable)), TF, point =>
            {
                var at = g.Keep(g.Get(TF, "position", point));
                var wave = g.Call(typeof(Mathf), "PerlinNoise", new[] { typeof(float), typeof(float) }, null,
                    (g.X(at) + waveTime) * g.P("WaveScale"), (g.Z(at) + waveTime) * g.P("WaveScale")) * g.P("WaveHeight") - g.P("WaveHeight") * 0.5f;
                var depth = g.Keep((g.P("WaterLevel") + wave + radius - g.Y(at)) / (radius * 2f));
                g.If(g.Greater(depth, 0f), () =>
                {
                    g.SetGraphVar("DepthSum", g.F("DepthSum") + depth);
                    var sinking = g.Max(-g.Y(g.Call(RB, "GetPointVelocity", Vec, null, at)), 0f);
                    var compression = g.Min(sinking * dt / (radius * 2f) * g.P("Compressing"), g.P("MaxCompressingForce")) / dt / 90f;
                    g.Do(RB, "AddForceAtPosition", new[] { V3T, V3T, typeof(ForceMode) }, null,
                        g.V3(0f, (g.Min(depth, g.P("MaxDepthForce")) * g.P("FloatForce") + compression) * dt, 0f), at, ForceMode.VelocityChange);
                });
            });
            var afloat = g.Keep(g.Min(g.F("DepthSum"), g.P("MaxDepthForce")));
            g.SetGraphVar("Floating", g.Greater(afloat, 0f), typeof(bool));
            g.If(g.Greater(afloat, 0f), () =>
            {
                var velocity = g.Keep(g.Get(RB, "linearVelocity"));
                var right = g.Keep(g.Get(TF, "right"));
                var forward = g.Keep(g.Get(TF, "forward"));
                var resist = g.Scale(right, -g.Dot(velocity, right) * g.P("WaterSidewaysDrag")) + g.Scale(forward, -g.Dot(velocity, forward) * g.P("WaterForwardDrag"));
                g.Do(RB, "AddForce", new[] { V3T, typeof(ForceMode) }, null, g.Scale(resist, afloat * dt), ForceMode.VelocityChange);
                g.Do(RB, "AddTorque", new[] { V3T, typeof(ForceMode) }, null,
                    g.Scale(g.Get(RB, "angularVelocity"), -(afloat * g.P("WaterRotDrag"))), ForceMode.Acceleration);
            });
        }

        /// <summary>
        /// The world's sea is solid at the surface (so aircraft crash into it and people can stand on it). A
        /// boat's hull is told to pass through that one collider, and then sits in the water on its floats.
        /// </summary>
        static void IgnoreSea(GraphWriter g) =>
            g.If(g.ObjVar("IsBoat", typeof(bool)), () =>
                g.ForEach(g.ObjVar("HullColliders", typeof(System.Collections.IEnumerable)), typeof(Collider), hull =>
                    g.Do(typeof(Physics), "IgnoreCollision", new[] { typeof(Collider), typeof(Collider), typeof(bool) }, null,
                        hull, g.ObjVar("SeaCollider", typeof(Collider)), true)));

        public const string TeleportPath = "Assets/Flight/Graphs/TeleportSign.asset";

        /// <summary>A sign that takes whoever clicks it somewhere else (the boat dock, and back).</summary>
        /// <summary>
        /// The fleet watch: the one graph that keeps running for all the parked vehicles together. A vehicle with
        /// nothing to do switches its own graph off (Doze); twice a second this one looks at every dozing
        /// vehicle and switches it back on if the local player has come near it or its reset sign, if it is no
        /// longer where it dozed off (somebody else is driving it, or the app sent it home), or if dozing has
        /// been turned off from the page (fw_doze 0, for measuring what it saves). Meanwhile it keeps a dozing
        /// vehicle's Mesh LOD bias right for the distance, which the vehicle otherwise does itself.
        /// Not covered: a dozing vehicle rammed by somebody else's vehicle takes no damage from the hit.
        /// </summary>
        public static GraphWriter BuildFleetWatch()
        {
            var g = new GraphWriter();
            Type GO = typeof(GameObject), list = typeof(System.Collections.IEnumerable);
            g.Declare("Elapsed", 0f);
            g.Declare("LastViewer", Vector3.zero);
            g.Declare("TickBudget", 0f);        // slow ticks owed to the fleet, in vehicles
            g.Declare("TickIndex", 0f);         // whose turn it is

            // ---- the clock: every physics step, the vehicle the local player pilots and whatever else says it is busy ----
            g.On(new FixedUpdate(), _ =>
            {
                g.If(g.ObjVar("HasPilotVehicle", typeof(bool)), () => Trigger(g, g.ObjVar("PilotVehicle", GO), "fw_step"));
                g.ForEach(g.ObjVar("BusyVehicles", list), GO, vehicle =>
                    g.If(g.ObjVarOf(vehicle, "Busy", typeof(bool)), () => Trigger(g, vehicle, "fw_step")));
            });
            // ---- the How To Fly board's switch for the advanced controls (each player's own; FlightGraphsHands.cs) ----
            g.On(g.Platform("OnClick", ("banterPlayerEvents", g.ObjVar("AdvancedButton", GO))), _ =>
            {
                var on = g.SetObjVar("AdvancedControls", g.Not(g.ObjVar("AdvancedControls", typeof(bool))), null, typeof(bool));
                g.Set(typeof(TMPro.TMP_Text), "text", g.ObjVar("AdvancedLabel", typeof(TMPro.TMP_Text)),
                    g.Select(on, FleetParts.AdvancedOn, FleetParts.AdvancedOff, typeof(string)));
                g.If(on, () =>
                {
                    foreach (var hand in new[] { "LeftHand", "RightHand" })
                        g.PlatformDo("AttachToPlayer", ("Object", g.Get(TF, "gameObject", g.ObjVar(hand, TF))));
                    g.PlatformDo("AddToastMessage", ("Message", "Advanced controls on: grip the stick and throttle, thumbsticks pick from the dials, triggers work them"));
                }, () => g.PlatformDo("AddToastMessage", ("Message", "Advanced controls off: the thumbsticks fly again")));
            });
            // ---- after the app has moved the hand objects to the hands this frame: the vehicle the player flies reads them ----
            g.On(new LateUpdate(), _ =>
                g.If(g.And(g.ObjVar("HasPilotVehicle", typeof(bool)), g.ObjVar("AdvancedControls", typeof(bool))), () =>
                    Trigger(g, g.ObjVar("PilotVehicle", GO), "fw_hands")));
            g.On(g.Platform("OnJsReturnValue", ("Return ID", "fw_doze")), e =>
                g.SetObjVar("DozeOn", g.Greater(g.PlatformValue("ParseStringInvariant", "float", typeof(float),
                    ("string", g.EventOut(e, "Data", typeof(string)))), 0.5f)));
            g.On(new Update(), _ =>
            {
                // ---- the clock: every frame, the same vehicles as every step ----
                var frame = g.Keep(g.Get(typeof(Time), "deltaTime"));
                g.If(g.ObjVar("HasPilotVehicle", typeof(bool)), () => Trigger(g, g.ObjVar("PilotVehicle", GO), "fw_frame"));
                g.ForEach(g.ObjVar("BusyVehicles", list), GO, vehicle =>
                    g.If(g.ObjVarOf(vehicle, "Busy", typeof(bool)), () => Trigger(g, vehicle, "fw_frame")));
                // ---- the slow tick goes round the fleet: each vehicle about ten times a second, at most four a frame ----
                var count = g.Keep(g.P("TickCount"));
                g.SetGraphVar("TickBudget", g.Min(g.F("TickBudget") + frame * count * 10f, 4f));
                for (int turn = 0; turn < 4; turn++)
                    g.If(g.Greater(g.F("TickBudget"), 0.999f), () =>
                    {
                        g.SetGraphVar("TickBudget", g.F("TickBudget") - 1f);
                        var index = g.SetGraphVar("TickIndex",
                            g.Call(typeof(Mathf), "Repeat", new[] { typeof(float), typeof(float) }, null, g.F("TickIndex") + 1f, count));
                        Trigger(g, g.ListItem(g.ObjVar("TickVehicles", list),
                            g.Call(typeof(Mathf), "FloorToInt", new[] { typeof(float) }, null, index), GO), "fw_tick");
                    });

                var elapsed = g.SetGraphVar("Elapsed", g.F("Elapsed") + frame);
                g.If(g.Greater(elapsed, 0.5f), () =>
                {
                    g.SetGraphVar("Elapsed", 0f);
                    var viewer = g.Keep(g.PlatformValue("GetLocalUserState", "Position", V3T));
                    // A player arriving fast (flying, driving) is looked for further ahead, so that what they
                    // are about to reach is awake by the time they get there.
                    var lead = g.Keep(g.Min(g.Distance(viewer, g.GraphVar("LastViewer", V3T)) / g.Max(elapsed, 0.001f), 150f) * 1.5f);
                    g.SetGraphVar("LastViewer", viewer);
                    var dozeOn = g.Keep(g.ObjVar("DozeOn", typeof(bool)));
                    g.ForEach(g.ObjVar("Vehicles", list), GO, vehicle =>
                        g.If(g.ObjVarOf(vehicle, "Dozing", typeof(bool)), () =>
                        {
                            var place = g.Keep(g.Get(TF, "position", g.Get(GO, "transform", vehicle)));
                            var away = g.Keep(g.Distance(viewer, place));
                            var sign = g.Get(TF, "position", g.Get(GO, "transform", g.ObjVarOf(vehicle, "ResetSign", GO)));
                            var wake = g.Or(g.Or(g.Not(dozeOn), g.Less(away, g.ObjVarOf(vehicle, "DozeBeyond", typeof(float)) - 6f + lead)),
                                g.Or(g.Less(g.Distance(viewer, sign), FleetParts.SignWakeDistance),
                                    g.Greater(g.Distance(place, g.ObjVarOf(vehicle, "DozePosition", V3T)), 0.05f)));
                            g.If(wake, () =>
                            {
                                g.SetObjVar("Dozing", false, vehicle);
                                g.Set(typeof(ScriptMachine), "enabled", g.ObjVarOf(vehicle, "Machine", typeof(ScriptMachine)), true);
                            }, () =>
                            {
                                var coarser = g.Keep(g.Clamp(g.Call(typeof(Mathf), "Log", new[] { typeof(float), typeof(float) }, null,
                                    g.Max(away / g.ObjVarOf(vehicle, "LodNear", typeof(float)), 1f), 2f), 0f, 6f));
                                g.If(g.Greater(g.Abs(coarser - g.ObjVarOf(vehicle, "DozeLod", typeof(float))), 0.3f), () =>
                                {
                                    g.SetObjVar("DozeLod", coarser, vehicle);
                                    g.ForEach(g.ObjVarOf(vehicle, "LodRenderers", list), typeof(Renderer), drawn =>
                                        g.Set(typeof(Renderer), "meshLodSelectionBias", drawn, coarser));
                                });
                            });
                        }));
                });
            });
            return g;
        }

        public static GraphWriter BuildTeleportSign()
        {
            var g = new GraphWriter();
            g.On(g.Platform("OnClick", ("banterPlayerEvents", g.ObjVar("ClickTarget", typeof(GameObject)))), _ =>
            {
                var to = g.ObjVar("Destination", TF);
                g.PlatformDo("TeleportTo", ("Position", g.Get(TF, "position", to)),
                    ("Rotation", g.Y(g.Get(TF, "eulerAngles", to))), ("Stop Velocity", true), ("Is Spawn", false));
            });
            return g;
        }

        /// <summary>
        /// SaccAirVehicle's FixedUpdate, for whoever is flying. One model serves jets, propeller aircraft,
        /// helicopters, tilt-thrust aircraft, a drone and a balloon: they differ only in their settings.
        /// Not ported: wind and gusts, fuel, engine on/off (the engine runs while there is a pilot).
        /// </summary>
        static void Physics(GraphWriter g)
        {
            var dt = g.Keep(g.Get(typeof(Time), "fixedDeltaTime"));
            var vel = g.Keep(g.Get(RB, "linearVelocity"));
            var forward = g.Keep(g.Get(TF, "forward"));
            var up = g.Keep(g.Get(TF, "up"));
            var right = g.Keep(g.Get(TF, "right"));
            var speed = g.SetGraphVar("Speed", g.Magnitude(vel));
            var mass = g.Keep(g.Get(RB, "mass"));

            // -- pilot inputs: keys and sticks move the virtual stick at a fixed rate; throttle is a lever --
            // (a stick held in the hand follows the hand, as Sacc's does: the slew only stands in for a thumbstick's travel)
            var slew = g.Keep(dt * g.Select(g.B("Advanced"), 60f, 5f));
            // (altitude hold flies the stick while it is on and the pilot is not holding it: FlightGraphsFunctions.cs)
            AltHoldStep(g, dt, vel, forward, up, right);
            var held = g.Keep(g.And(g.B("AltHoldOn"), g.Not(g.B("StickHeld"))));
            Val Slew(string state, string key, string stick, string hold) =>
                g.SetGraphVar(state, g.Select(held, g.F(hold), g.MoveTowards(g.F(state), g.Clamp(g.F(key) + g.F(stick), -1f, 1f), slew)));
            var inPitch = Slew("InPitch", "KeyPitch", "StickPitch", "AHOutPitch");
            var inYaw = Slew("InYaw", "KeyYaw", "StickYaw", "AHOutYaw");
            var inRoll = Slew("InRoll", "KeyRoll", "StickRoll", "AHOutRoll");
            var throttleRate = g.Clamp(g.F("KeyThrottle") + g.F("StickThrottle"), -1f, 1f);
            var manualThrottle = g.SetGraphVar("Throttle", g.Clamp01(g.F("Throttle") + throttleRate * g.P("KeyboardThrottleSens") * dt));
            var throttle = g.Keep(CruiseThrottle(g, manualThrottle, speed, dt));
            // with the engine off (or starting) it gives nothing, whatever the lever says (DFUNC_ToggleEngine)
            var engineWanted = g.Keep(g.Select(g.B("EngineOff"), 0f, throttle));
            var brake = g.SetGraphVar("Brake", g.Max(g.F("KeyBrake"), g.F("TriggerBrake")));

            // -- the ground under the vehicle: one ray straight down its own belly, from just below its hull --
            // (the original casts one ray for "am I on the ground" and another for ground effect)
            var cast = g.DoOut(typeof(Physics), "Raycast",
                new[] { V3T, V3T, typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int), typeof(QueryTriggerInteraction) }, null,
                g.Get(TF, "position", g.ObjVar("GroundProbe", TF)), -up, null, g.P("ProbeLength"), UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var below = g.Keep(g.Select(g.Result(cast), g.Get(typeof(RaycastHit), "distance", g.OutParam(cast, 2, typeof(RaycastHit))), 100000f));
            var taxiing = g.SetGraphVar("Taxiing", g.And(g.Less(below, g.P("GroundContactDistance")), g.Not(g.B("GearUp"))), typeof(bool));
            // gear and flaps change drag and lift while they are out (DFUNC_Gear, DFUNC_Flaps)
            var flaps = g.Keep(g.Select(g.B("FlapsOn"), 1f, 0f));
            var extraDrag = g.Keep(g.P("ExtraDrag") + g.Select(g.B("GearUp"), 0f, g.P("GearDrag")) + flaps * g.P("FlapsDrag") +
                g.Select(g.Or(g.B("CanopyOpen"), g.B("CanopyBroken")), g.P("CanopyDrag"), 0f));
            // the hook: catching a cable, and being stopped by it (DFUNC_Hook)
            HookStep(g, dt, vel, speed);
            // the gun pushes back while it fires (DFUNC_Gun's RecoilForce)
            g.If(g.B("GunFiring"), () => g.Do(RB, "AddRelativeForce", new[] { V3T, typeof(ForceMode) }, null, g.V3(0f, 0f, -g.P("GunRecoil")), ForceMode.Acceleration));
            var extraLift = g.Keep(g.P("ExtraLift") + flaps * g.P("FlapsLift"));
            var maxLift = g.Keep(g.P("MaxLift") + flaps * g.P("FlapsExtraMaxLift"));
            var airborne = g.Keep(g.Select(taxiing, 0f, 1f));                              // "ThrustVecGrounded"
            var groundEffectRange = g.P("GroundEffectMaxDistance");
            var closeness = g.Keep(g.Clamp01((groundEffectRange - (below + g.P("GroundEffectProbeOffset"))) / groundEffectRange));

            // -- thrust direction: 0 = straight ahead, 90 degrees = straight up (helicopters stay near 90) --
            var vtolOn = g.ObjVar("VTOLenabled", typeof(bool));
            var vtolOnly = g.ObjVar("VTOLOnly", typeof(bool));
            g.SetGraphVar("VTOLAngleInput", g.Clamp01(g.F("VTOLAngleInput") + g.Clamp(g.F("KeyVtol") + g.F("StickVtol"), -1f, 1f) * dt * 0.5f));
            var vtolAngle = g.SetGraphVar("VTOLAngle", g.MoveTowards(g.F("VTOLAngle"), g.F("VTOLAngleInput"), g.P("VTOLAngleDivider") * dt));
            var vtolDegrees = g.Keep(g.Select(vtolOn, g.P("VTOLMinAngle") + (g.P("VTOLMaxAngle") - g.P("VTOLMinAngle")) * vtolAngle, 0f));
            var hovering = g.Keep(g.Min(g.Abs(vtolDegrees) / 90f, 1f));                      // "VTOLAngleForward90"
            var vtolActive = g.Keep(g.And(vtolOn, g.Or(vtolOnly, g.Greater(vtolAngle, 0f))));
            var vtolBlend = g.Keep(g.Select(vtolActive, (1f - g.Min(speed / g.P("VTOLLoseControlSpeed"), 1f)) * hovering, 0f));
            // Control that comes from the engine's thrust and so works with no airflow: all of it for a
            // helicopter, a share of it for tilt-thrust and vectored-thrust aircraft, none on the ground.
            Val ThrustControl(string axis) => g.Select(vtolOnly, 1f,
                g.Lerp(g.P(axis + "ThrustVecMulti"), g.P("VTOL" + axis + "ThrustVecMulti"), vtolBlend)) * airborne;

            // -- atmosphere and angle of attack (WindAndAoA; no wind here) --
            var altitude = g.Y(g.Get(RB, "worldCenterOfMass")) - g.P("SeaLevel");
            var atmosphere = g.Keep(g.Clamp01(1f - (altitude - g.P("AtmosphereThinningStart")) /
                                              (g.P("AtmosphereThinningEnd") - g.P("AtmosphereThinningStart"))));
            var aoaPitch = g.Keep(g.Call(V3T, "SignedAngle", new[] { V3T, V3T, V3T }, null, forward,
                g.Call(V3T, "ProjectOnPlane", new[] { V3T, V3T }, null, vel, right), right) - g.P("ZeroLiftAoA"));
            var aoaYaw = g.Keep(g.Call(V3T, "SignedAngle", new[] { V3T, V3T, V3T }, null, forward,
                g.Call(V3T, "ProjectOnPlane", new[] { V3T, V3T }, null, vel, up), up));
            g.SetGraphVar("AoA", g.Max(g.Abs(aoaPitch), g.Abs(aoaYaw)));
            g.SetGraphVar("AoAPitch", aoaPitch);

            Val AoaLift(Val angle, string maxAngle, string minControl)
            {
                var a = g.Keep(g.Abs(angle));
                a = g.Keep(g.Select(g.Greater(a, 90f), 180f - a, a));            // flying backwards
                var curve = 1f - g.Pow(a / g.P(maxAngle), g.P("AoaCurveStrength"));
                var floor = g.Clamp01((1f - a / 90f) * g.P(minControl));            // some authority left at high AoA
                return g.Keep(g.Clamp(curve, floor, 1f));
            }

            var aoaLiftPitch = AoaLift(aoaPitch, "MaxAngleOfAttackPitch", "HighPitchAoaMinControl");
            var aoaLiftYaw = AoaLift(aoaYaw, "MaxAngleOfAttackYaw", "HighYawAoaMinControl");

            // -- engine response: spools up faster than it spools down --
            var spool = g.Select(g.Less(g.F("EngineOutput"), engineWanted), g.P("AccelerationResponse"),
                g.P("AccelerationResponse") * g.P("EngineSpoolDownSpeedMulti"));
            var engineBefore = g.Keep(g.F("EngineOutput"));
            var engine = g.SetGraphVar("EngineOutput", g.Lerp(engineBefore, engineWanted, g.Blend(spool, dt)));

            // -- airflow over the airframe --
            var sidespeed = g.Keep(g.Dot(vel, right));
            var downspeed = g.Keep(-g.Dot(vel, up));
            var pitchDown = g.Keep(g.Less(downspeed, 0f));                           // air hitting the vehicle from above
            var speedLiftFactor = g.Keep(speed * speed);
            var rotlift = g.Keep(g.Min(speed / g.P("RotMultiMaxSpeed"), 1f));        // control authority grows with speed
            var minLift = g.Keep(rotlift * g.Min(aoaLiftPitch, aoaLiftYaw));

            // -- stick to rotation strengths (SetRotInputs): going backwards reverses surfaces that have no thrust control --
            var reversing = g.Keep(g.And(g.Not(g.Greater(g.Dot(vel, forward), 0f)), g.Not(vtolActive)));
            Val Reversed(string axis) => g.Select(reversing, g.P("Reversing" + axis + "Zero"), 1f);
            var pitchRaw = g.Keep(g.Clamp(inPitch, -1f, 1f) * g.P("PitchStrength") * Reversed("Pitch"));
            var pitch = g.Select(g.Greater(pitchRaw, 0f), pitchRaw * g.P("PitchDownStrMulti"), pitchRaw);
            var inWater = g.Keep(g.Select(g.ObjVar("IsBoat", typeof(bool)), g.Select(g.B("Floating"), 1f, 0f), 1f));
            var yaw = g.Clamp(-inYaw, -1f, 1f) * g.P("YawStrength") * Reversed("Yaw") * inWater;
            var roll = g.Clamp(inRoll, -1f, 1f) * g.P("RollStrength") * Reversed("Roll");
            var lerpedPitch = g.SetGraphVar("LerpedPitch", g.Lerp(g.F("LerpedPitch"), pitch * g.Max(ThrustControl("Pitch"), minLift), g.Blend(g.P("PitchResponse"), dt)));
            var lerpedYaw = g.SetGraphVar("LerpedYaw", g.Lerp(g.F("LerpedYaw"), yaw * g.Max(ThrustControl("Yaw"), minLift), g.Blend(g.P("YawResponse"), dt)));
            var lerpedRoll = g.SetGraphVar("LerpedRoll", g.Lerp(g.F("LerpedRoll"), roll * g.Max(ThrustControl("Roll"), minLift), g.Blend(g.P("RollResponse"), dt)));

            // lift never drops to zero at extreme AoA, or there would be no drag when flying sideways
            var liftPitch = g.Keep(g.Max(aoaLiftPitch, g.P("HighPitchAoaMinLift")));
            var liftYaw = g.Keep(g.Max(aoaLiftYaw, g.P("HighYawAoaMinLift")));
            var liftMin = g.Min(liftPitch, liftYaw);

            // -- thrust, with the afterburner as the last part of the throttle travel --
            var point = g.P("ThrottleAfterburnerPoint");
            var thrust = g.Keep((g.Min(engine, point) / point * g.P("ThrottleStrength")
                                 + g.Max(engine - point, 0f) / (1f - point) * g.P("ThrottleStrength") * (g.P("AfterburnerThrustMulti") - 1f)) * inWater);
            var tilted = g.Select(vtolOnly, thrust, g.Lerp(thrust, thrust * g.P("VTOLThrottleStrengthMulti"), hovering));
            var thrustAlongAxis = g.Keep(g.Select(vtolOn, tilted * (closeness * g.P("VTOLGroundEffectStrength") + 1f), thrust));
            var radians = g.Keep(vtolDegrees * 0.0174532925f);
            var thrustAcc = g.Keep(g.Select(g.ObjVar("Thrust_AlwaysWorldUp", typeof(bool)),
                g.Scale(g.Call(TF, "InverseTransformDirection", Vec, null, Vector3.up), thrust),
                g.Scale(g.V3(0f, g.Call(typeof(Mathf), "Sin", new[] { typeof(float) }, null, radians),
                    g.Call(typeof(Mathf), "Cos", new[] { typeof(float) }, null, radians)), thrustAlongAxis), V3T));

            // -- lift: body-axis force that damps the airflow component hitting the belly / side --
            var liftSide = g.Min(speedLiftFactor * g.P("Lift"), maxLift);
            var liftBelly = g.Min(speedLiftFactor * g.P("Lift"), g.Select(pitchDown, maxLift * g.P("PitchDownLiftMulti"), maxLift));
            // lift from forward speed, stronger close to the ground (ground effect)
            var velLiftCoefficient = g.P("VelLift") + closeness * g.P("GroundEffectStrength") / mass;
            var velLiftLimit = g.Select(g.Greater(closeness, 0f), g.Max(g.P("VelLiftMax"), g.P("GroundEffectLiftMax")), g.P("VelLiftMax"));
            var velLift = liftMin * g.Min(speedLiftFactor * liftPitch * velLiftCoefficient, velLiftLimit);
            var liftX = -sidespeed * g.P("SidewaysLift") * liftSide * liftYaw;
            var liftY = (g.Select(pitchDown, downspeed * g.P("PitchDownLiftMulti"), downspeed) * extraLift * liftBelly * liftPitch + velLift)
                        * g.Select(g.ObjVar("DroneMode", typeof(bool)), throttle, 1f);

            // -- drag: velocity eased towards still air, plus a bump around the speed of sound; the air brake adds to it --
            var soundBarrier = (1f - g.Clamp01(g.Abs(speed - 343f) / g.P("SoundBarrierWidth"))) * g.P("SoundBarrierStrength");
            var dragRate = g.Keep((g.P("AirFriction") + soundBarrier) * (extraDrag + brake * g.P("AirbrakeStrength")));
            g.If(g.ObjVar("HasCenterOfDrag", typeof(bool)),
                () => g.Do(RB, "AddForceAtPosition", new[] { V3T, V3T }, null, g.Scale(vel, -(dragRate * mass)),
                    g.Get(TF, "position", g.ObjVar("CenterOfDrag", TF))),
                () => g.Set(RB, "linearVelocity", null, g.Lerp3(vel, Vector3.zero, g.Blend(dragRate * 90f, dt))));

            // -- on the ground the brake also slows the vehicle directly (DFUNC_Brake) --
            g.If(g.And(g.And(taxiing, g.Greater(brake, 0f)), g.And(g.Greater(speed, 0.05f), g.Less(speed, g.P("GroundBrakeSpeed")))), () =>
                g.Do(RB, "AddForce", new[] { V3T, typeof(ForceMode) }, null,
                    g.Scale(g.Get(V3T, "normalized", vel), -g.Min(brake * g.P("GroundBrakeStrength") * dt, speed)), ForceMode.VelocityChange));

            // -- apply (accelerations x mass, as the original does) --
            g.If(g.ObjVar("HasCenterOfThrust", typeof(bool)),
                () => g.Do(RB, "AddForceAtPosition", new[] { V3T, V3T }, null,
                    g.Scale(g.Call(TF, "TransformDirection", Vec, null, thrustAcc), atmosphere * mass), g.Get(TF, "position", g.ObjVar("CenterOfThrust", TF))),
                () => g.Do(RB, "AddRelativeForce", Vec, null, g.Scale(thrustAcc, atmosphere * mass)));
            g.Do(RB, "AddRelativeForce", Vec, null, g.Scale(g.V3(liftX, liftY, 0f), atmosphere * mass));

            // -- manoeuvring thrusters (SAV_ThreeDThrust): straight pushes along the vehicle's own axes, each
            //    with its own strength either way. Where they are the only engine, the throttle lever is the
            //    upward thruster, so the vehicle is flown like a helicopter. --
            g.If(g.ObjVar("HasThrusters", typeof(bool)), () =>
            {
                var sideways = g.Keep(g.Clamp(g.F("KeyThrustX") + g.F("StickThrustX") + g.F("RightThrustX"), -1f, 1f));
                var upward = g.Keep(g.Clamp(g.F("KeyThrustY") + g.F("StickThrustY") + g.Select(g.ObjVar("ThrottleIsUpThrust", typeof(bool)), throttle, 0f), -1f, 1f));
                var ahead = g.Keep(g.Clamp(g.F("KeyThrustZ") + g.F("StickThrustZ") + g.F("RightThrustZ"), -1f, 1f));
                Val Push(Val input, string axis) => input * g.Select(g.Greater(input, 0f), g.P("Thrust" + axis + "Positive"), g.P("Thrust" + axis + "Negative"));
                g.Do(RB, "AddRelativeForce", new[] { V3T, typeof(ForceMode) }, null,
                    g.V3(Push(sideways, "X"), Push(upward, "Y"), Push(ahead, "Z")), ForceMode.Acceleration);
            });

            // -- the pilot's own weight. The app hangs a seated player's body (76 kg) on the vehicle with a
            //    rigid joint, well ahead of the centre of mass in most cockpits: a light helicopter noses over
            //    under it (seen on a Quest, 2026-11-21; the scripted test pilot has no body). Only the client
            //    flying the vehicle carries that load, so it alone takes it off again. --
            if (RiderWeight > 0f)
                g.If(g.Or(g.Not(g.B("TestPilot")), g.B("TestRider")), () =>
                    g.Do(RB, "AddForceAtPosition", new[] { V3T, V3T }, null, new Vector3(0f, RiderWeight, 0f),
                        g.Get(TF, "position", PilotSeatTransform(g)) + g.Scale(up, 0.3f)));

            // -- rotation: roll, the air's resistance to turning, engine torque, and the nose dropping in a stall --
            var w = g.Keep(g.Call(TF, "InverseTransformDirection", Vec, null, g.Get(RB, "angularVelocity")));
            var friction = g.Keep(rotlift * liftPitch * liftYaw);
            var adverseYaw = (engine - engineBefore) / dt * g.P("AdverseYaw");          // a rotor or propeller spinning up twists the airframe
            var adverseRoll = (1f - rotlift) * g.P("AdverseRoll") * engine;
            Type curve = typeof(AnimationCurve);
            var stallPitch = g.Call(curve, "Evaluate", new[] { typeof(float) }, g.ObjVar("PitchAoaPitchForce", curve), aoaPitch) * g.P("PitchAoaPitchForceMulti") * rotlift;
            var stallRoll = g.Select(g.Greater(aoaYaw, 0f), -1f, 1f) *
                            g.Call(curve, "Evaluate", new[] { typeof(float) }, g.ObjVar("YawAoaRollForce", curve), g.Abs(aoaYaw)) * g.P("YawAoaRollForceMulti") * rotlift;
            var torque = g.V3(-g.X(w) * g.P("PitchFriction") * friction - g.X(w) * g.P("PitchConstantFriction") + stallPitch,
                              -g.Y(w) * g.P("YawFriction") * friction + adverseYaw - g.Y(w) * g.P("YawConstantFriction"),
                              lerpedRoll + stallRoll - g.Z(w) * g.P("RollFriction") * friction + adverseRoll - g.Z(w) * g.P("RollConstantFriction"));
            g.Do(RB, "AddRelativeTorque", Vec, null, g.Scale(torque, atmosphere * mass));

            // pitch and yaw act as forces at the tail where the vehicle has moment points, including the
            // weathervane ("vel straighten") terms; vehicles without them are simply twisted
            var pitching = g.Keep((lerpedPitch + downspeed * g.P("VelStraightenStrPitch") * liftPitch * rotlift) * atmosphere * mass);
            g.If(g.ObjVar("HasPitchMoment", typeof(bool)),
                () => g.Do(RB, "AddForceAtPosition", new[] { V3T, V3T }, null, g.Scale(up, pitching), g.Get(TF, "position", g.ObjVar("PitchMoment", TF))),
                () => g.Do(RB, "AddRelativeTorque", Vec, null, g.V3(pitching, 0f, 0f)));
            var yawing = g.Keep((lerpedYaw - sidespeed * g.P("VelStraightenStrYaw") * liftYaw * rotlift) * atmosphere * mass);
            g.If(g.ObjVar("HasYawMoment", typeof(bool)),
                () => g.Do(RB, "AddForceAtPosition", new[] { V3T, V3T }, null, g.Scale(right, yawing), g.Get(TF, "position", g.ObjVar("YawMoment", TF))),
                () => g.Do(RB, "AddRelativeTorque", Vec, null, g.V3(0f, -yawing, 0f)));

            // -- on the ground: vehicles with a steerable wheel steer with it; the rest are turned on the
            //    spot by the rudder (the original turns every vehicle this way) --
            g.If(g.And(taxiing, g.ObjVar("UseTaxiRotation", typeof(bool))), () =>
            {
                var still = g.Select(g.Greater(g.P("TaxiStillSpeed"), 0f), g.Min(speed / g.P("TaxiStillSpeed"), 1f), 1f);
                var turnRate = g.SetGraphVar("TaxiLerper", g.Lerp(g.F("TaxiLerper"), inYaw * g.P("TaxiRotationSpeed") * still, g.Blend(g.P("TaxiRotationResponse"), dt)));
                g.Set(RB, "angularVelocity", null, g.Call(TF, "TransformDirection", Vec, null, g.V3(g.X(w), turnRate * 0.0174532925f, g.Z(w))));
            }, () => g.SetGraphVar("TaxiLerper", 0f));

            // The physics engine holds wheels "sticky" at rest unless some drive torque is applied, which
            // pins light aircraft to the spot. A token torque while the engine runs lets them roll freely
            // (the original does the same).
            var wake = g.Keep(g.Select(g.Greater(engine, 0.02f), 0.01f, 0f));
            g.ForEach(g.ObjVar("AllWheels", typeof(System.Collections.IEnumerable)), typeof(WheelCollider), wheel =>
                g.Set(typeof(WheelCollider), "motorTorque", wheel, wake));
            g.Set(typeof(WheelCollider), "steerAngle", g.ObjVar("NoseWheel", typeof(WheelCollider)), inYaw * g.P("NoseWheelSteerAngle"));
            var brakeTorque = g.Keep(brake * g.P("BrakeTorque"));
            g.ForEach(g.ObjVar("BrakeWheels", typeof(System.Collections.IEnumerable)), typeof(WheelCollider), wheel =>
                g.Set(typeof(WheelCollider), "brakeTorque", wheel, brakeTorque));

            // -- G: the change in velocity since the last step, less what gravity alone would have done,
            //    averaged over about a tenth of a second. Too much of it damages the airframe. --
            var fall = g.Keep(dt * 9.81f);
            var felt = g.Call(TF, "InverseTransformDirection", Vec, null, vel - g.GraphVar("LastVel", V3T) + g.V3(0f, fall, 0f));
            var gs = g.SetGraphVar("GsAll", g.Lerp3(g.GraphVar("GsAll", V3T), g.Scale(felt, 1f / fall), g.Clamp01(dt * 10f)), V3T);
            g.SetGraphVar("VertGs", g.Y(gs));
            g.SetGraphVar("LastVel", vel);
            var overG = g.Keep(g.Max(g.Magnitude(gs) - g.P("MaxGs"), 0f));
            g.If(g.And(g.Greater(overG, 0f), g.Not(g.B("Dead"))), () =>
            {
                var health = g.SetGraphVar("Health", g.F("Health") - overG * 0.01f * g.P("GDamage"));
                g.If(g.Less(health, 0.01f), () => Explode(g));
            });
        }
    }

    public static class GraphTypes
    {
        public static Type Find(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            throw new Exception($"type '{fullName}' not found (is the Creator SDK installed?)");
        }
    }
}
