using System;
using Unity.VisualScripting;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The advanced controls: SaccFlightAndVehicles' own VR controls, for players who switch them on at the How To
    /// Fly board. As SaccAirVehicle has them, the pilot holds a grip to take the cockpit's stick and moves the hand:
    /// tilting it pitches and rolls, twisting it yaws (double-tap the grip to keep hold). The other grip takes the
    /// throttle, pushed forward or pulled back (raised or lowered in a helicopter, VerticalThrottle). As SaccEntity
    /// has them, each thumbstick then picks a function on its dial, shown on the cockpit's dial display, and the
    /// trigger on that side works it. Ported: gear and flaps (a pull toggles), brakes (as hard as the trigger is
    /// pulled), thrust tilt (DFUNC_VTOLAngle: hand forward or back while the trigger is held) and manoeuvring
    /// thrusters (SAV_ThreeDThrust: hand moved any way while the trigger is held). Weapons, engine start, canopy,
    /// cruise, altitude hold, limits, hook, catapult and smoke can be picked but do nothing.
    ///
    /// Hands: two objects that the app keeps on the local player's physics hands (FleetParts.Watch), read after the
    /// app has placed them each frame (the fleet watch's LateUpdate sends "fw_hands").
    /// </summary>
    public static partial class FlightGraphs
    {
        static void DeclareAdvanced(GraphWriter g)
        {
            g.Declare("Advanced", false);       // the advanced controls are in use (on at the board, and not a boat)
            g.Declare("AdvancedShown", false);  // the dial displays and grabs have been set up for them
            g.Declare("GripL", false);
            g.Declare("GripR", false);
            g.Declare("TrigL", 0f);
            g.Declare("TrigR", 0f);
            g.Declare("LSel", -1f);             // the function picked on each dial (-1 = none yet)
            g.Declare("RSel", -1f);
            g.Declare("GrabToggle", false);     // the stick is kept held after a double tap of its grip
            g.Declare("GripTapTime", -10f);
            g.Declare("GripReleases", 0f);
            g.Declare("StickHeld", false);
            g.Declare("StickUp0", Vector3.up);       // the vehicle's up and forward in the hand's own axes, when it took the stick
            g.Declare("StickFwd0", Vector3.forward);
            g.Declare("ThrottleHeld", false);
            g.Declare("ThrottleZero", 0f);
            g.Declare("ThrottleTemp", 0f);
            g.Declare("ThrottleAxisLast", 0f);
            g.Declare("VtolHeld", false);
            g.Declare("VtolZero", 0f);
            g.Declare("VtolTemp", 0f);
            g.Declare("ThrustHeld", false);
            g.Declare("ThrustZero", Vector3.zero);
            g.Declare("StickThrustY", 0f);
            g.Declare("WheelValue", 0f);        // a boat's wheel: how far it has been turned, in degrees (SaccSeaVehicle)
            g.Declare("WheelLast", Vector3.up);
            g.Declare("RightThrustX", 0f);      // the balloon's right stick (RightStickThrusts)
            g.Declare("RightThrustZ", 0f);
        }

        /// <summary>Whether the local player has the advanced controls on (the board's button).</summary>
        static Val AdvancedWanted(GraphWriter g) =>
            g.ObjVarOf(g.ObjVar("Watch", typeof(GameObject)), "AdvancedControls", typeof(bool));

        /// <summary>
        /// Set up for the advanced controls, or back to the sticks: dial displays shown or hidden, nothing held or
        /// picked, the hand objects put on the player's hands.
        /// </summary>
        static void AdvancedSetUp(GraphWriter g, object on)
        {
            g.SetGraphVar("AdvancedShown", on, typeof(bool));
            g.ForEach(g.ObjVar("DialDisplays", typeof(System.Collections.IEnumerable)), typeof(GameObject), display =>
                g.Do(typeof(GameObject), "SetActive", new[] { typeof(bool) }, display, on));
            foreach (var name in new[] { "StickPitch", "StickYaw", "StickRoll", "StickThrottle", "StickVtol", "StickThrustX", "StickThrustY", "StickThrustZ", "TriggerBrake", "TrigL", "TrigR" })
                g.SetGraphVar(name, 0f);
            foreach (var name in new[] { "GrabToggle", "StickHeld", "ThrottleHeld", "VtolHeld", "ThrustHeld", "GripL", "GripR", "LeftGrip" })
                g.SetGraphVar(name, false);
            foreach (var side in new[] { "L", "R" })
            {
                // (a dial of one function has it picked from the start, as SaccEntity does)
                g.SetGraphVar(side + "Sel", g.Select(g.Same(g.P(side + "DialCount"), 1f), 0f, -1f));
                g.Set(typeof(Transform), "localEulerAngles", g.ObjVar(side + "Highlighter", typeof(Transform)), new Vector3(0f, 180f, 0f));
            }
            if (on is Val switchedOn)
                g.If(switchedOn, () =>
                {
                    foreach (var hand in new[] { "LeftHand", "RightHand" })
                        g.PlatformDo("AttachToPlayer", ("Object", g.Get(typeof(Transform), "gameObject", g.ObjVar(hand, typeof(Transform)))));
                    AdvancedLit(g);
                }, () => FunctionsRelease(g));
            else FunctionsRelease(g);
        }

        /// <summary>The pilot is in the cockpit: notice the board's switch being turned either way, and keep the display's marks right.</summary>
        static void AdvancedFrame(GraphWriter g)
        {
            var advanced = g.Keep(AdvancedWanted(g));
            g.SetGraphVar("Advanced", advanced);
            g.If(g.Not(g.Same(advanced, g.B("AdvancedShown"))), () => AdvancedSetUp(g, advanced));
        }

        /// <summary>Out of the cockpit: back to the sticks for next time.</summary>
        static void AdvancedOff(GraphWriter g)
        {
            g.SetGraphVar("Advanced", false);
            g.If(g.B("AdvancedShown"), () => AdvancedSetUp(g, false));
        }

        /// <summary>The dial display's lit marks: gear down, flaps out.</summary>
        static void AdvancedLit(GraphWriter g)
        {
            var gearDown = g.Keep(g.Not(g.B("GearUp")));
            g.ForEach(g.ObjVar("GearLit", typeof(System.Collections.IEnumerable)), typeof(GameObject), lit =>
                g.Do(typeof(GameObject), "SetActive", new[] { typeof(bool) }, lit, gearDown));
            var flapsOut = g.Keep(g.B("FlapsOn"));
            g.ForEach(g.ObjVar("FlapsLit", typeof(System.Collections.IEnumerable)), typeof(GameObject), lit =>
                g.Do(typeof(GameObject), "SetActive", new[] { typeof(bool) }, lit, flapsOut));
        }

        /// <summary>
        /// A thumbstick pushed past the dial's threshold picks the function in that direction (SaccEntity's maths:
        /// angle from its check direction, round the dial, one slice per function), and the display's highlighter
        /// turns to it. Letting go of the stick keeps the pick.
        /// </summary>
        static void DialPick(GraphWriter g, string side, Val x, Val y)
        {
            var count = g.Keep(g.P(side + "DialCount"));
            var reach = g.P("DialSensitivity");
            g.If(g.And(g.Greater(count, 1.5f), g.Greater(x * x + y * y, reach * reach)), () =>
            {
                var direction = g.Call(typeof(Vector2), "SignedAngle", new[] { typeof(Vector2), typeof(Vector2) }, null,
                    g.ObjVar(side + "DialCheck", typeof(Vector2)), g.V2(x, y));
                var degrees = g.Keep(g.P(side + "DialDegrees"));
                var pick = g.Keep(g.Call(typeof(Mathf), "Floor", new[] { typeof(float) }, null, g.Min((180f - direction) / degrees, count - 1f)));
                var usable = g.ListItem(g.ObjVar(side + "DialValid", typeof(System.Collections.IList)),
                    g.Call(typeof(Mathf), "FloorToInt", new[] { typeof(float) }, null, pick), typeof(bool));
                g.If(g.And(usable, g.Not(g.Same(pick, g.F(side + "Sel")))), () =>
                {
                    g.SetGraphVar(side + "Sel", pick);
                    g.Set(typeof(Transform), "localEulerAngles", g.ObjVar(side + "Highlighter", typeof(Transform)), g.V3(0f, 0f, -degrees * pick));
                });
            });
        }

        /// <summary>The function in `slot` is the one picked on that side's dial.</summary>
        static Val Picked(GraphWriter g, string side, string slot)
        {
            var at = g.Keep(g.P(side + slot));
            return g.And(g.Greater(at, -0.5f), g.Same(g.F(side + "Sel"), at));
        }

        /// <summary>
        /// A trigger on a dial: gear and flaps toggle on a pull past three quarters (DFUNC_Gear, DFUNC_Flaps); held
        /// functions (brake, tilt, thrusters) read the trigger every frame in Hands.
        /// </summary>
        static void DialTrigger(GraphWriter g, string side, Val value, Action gear, Action flaps)
        {
            var before = g.Keep(g.F("Trig" + side));
            g.SetGraphVar("Trig" + side, value);
            g.If(g.And(g.Greater(value, 0.75f), g.Not(g.Greater(before, 0.75f))), () =>
            {
                g.If(Picked(g, side, "GearSlot"), gear);
                g.If(Picked(g, side, "FlapsSlot"), flaps);
                FunctionsPress(g, side);
            });
        }

        /// <summary>
        /// Grip pressed or released. The stick's hand also counts double taps: a second press within a quarter of a
        /// second keeps the stick held until the grip has been let go twice more (SaccAirVehicle's GrabToggle).
        /// </summary>
        static void Grip(GraphWriter g, Val hand, bool held)
        {
            g.If(g.Same(hand, 0), () => g.SetGraphVar("GripL", held), () => g.SetGraphVar("GripR", held));
            var stickHand = g.Same(hand, g.Select(g.ObjVar("SwitchHandsJoyThrottle", typeof(bool)), 0, 1, typeof(int)));
            g.If(stickHand, () =>
            {
                if (held)
                {
                    var now = g.Keep(g.Get(typeof(Time), "time"));
                    g.If(g.Less(now - g.F("GripTapTime"), 0.25f), () =>
                    {
                        g.SetGraphVar("GrabToggle", true);
                        g.SetGraphVar("GripReleases", 0f);
                    });
                    g.SetGraphVar("GripTapTime", now);
                }
                else
                {
                    var releases = g.SetGraphVar("GripReleases", g.F("GripReleases") + 1f);
                    g.If(g.Greater(releases, 1.5f), () => g.SetGraphVar("GrabToggle", false));
                }
            });
        }

        /// <summary>
        /// Every frame while flying with the advanced controls, after the app has put the hand objects where the
        /// hands are: SaccAirVehicle's VR joystick and throttle, and the held dial functions.
        /// </summary>
        static void Hands(GraphWriter g)
        {
            Type TFT = typeof(Transform), V3 = typeof(Vector3);
            var switched = g.Keep(g.ObjVar("SwitchHandsJoyThrottle", typeof(bool)));
            var left = g.Keep(g.ObjVar("LeftHand", TFT));
            var right = g.Keep(g.ObjVar("RightHand", TFT));
            var stickHand = g.Keep(g.Select(switched, left, right, TFT));
            var throttleHand = g.Keep(g.Select(switched, right, left, TFT));
            var stickGrip = g.Keep(g.Or(g.Select(switched, g.B("GripL"), g.B("GripR"), typeof(bool)), g.B("GrabToggle")));
            var throttleGrip = g.Keep(g.Select(switched, g.B("GripR"), g.B("GripL"), typeof(bool)));
            Val InVehicle(Val worldDirection) => g.Call(TFT, "InverseTransformDirection", new[] { V3 }, null, worldDirection);
            Val HandOffset(Val hand) => InVehicle(g.Get(TFT, "position") - g.Get(TFT, "position", hand));   // vehicle minus hand, in the vehicle's axes

            // ---- the stick. Sacc keeps the hand's rotation when it grabs, turned with the vehicle since, and reads
            // how far the hand has turned from it as three angles. The same with directions: the vehicle's up and
            // forward as the hand saw them at the grab, taken through the hand now and back into the vehicle's axes.
            g.If(stickGrip, () =>
            {
                g.If(g.Not(g.B("StickHeld")), () =>
                {
                    g.SetGraphVar("StickHeld", true);
                    g.SetGraphVar("StickUp0", g.Call(TFT, "InverseTransformDirection", new[] { V3 }, stickHand, g.Get(TFT, "up")));
                    g.SetGraphVar("StickFwd0", g.Call(TFT, "InverseTransformDirection", new[] { V3 }, stickHand, g.Get(TFT, "forward")));
                    g.SetGraphVar("WheelValue", -g.F("InYaw") * g.P("SteeringWheelDegrees"));
                    g.SetGraphVar("WheelLast", Vector3.up);
                });
                var up = g.Keep(InVehicle(g.Call(TFT, "TransformDirection", new[] { V3 }, stickHand, g.GraphVar("StickUp0", V3))));
                var ahead = g.Keep(InVehicle(g.Call(TFT, "TransformDirection", new[] { V3 }, stickHand, g.GraphVar("StickFwd0", V3))));
                // -(acos(c) - 90 degrees) / the angle for full deflection
                Val Deflection(Val component, string maximum) =>
                    (90f - g.Call(typeof(Mathf), "Acos", new[] { typeof(float) }, null, g.Clamp(component, -1f, 1f)) * 57.29578f) / g.P(maximum);
                g.If(g.ObjVar("IsBoat", typeof(bool)), () =>
                {
                    // a boat's helm is a wheel: the hand's turn about the boat's length adds up, a full turn of
                    // SteeringWheelDegrees being full rudder (clockwise = to starboard)
                    var across = g.Keep(g.Call(V3, "ProjectOnPlane", new[] { V3, V3 }, null, up, Vector3.forward));
                    var turned = g.SetGraphVar("WheelValue", g.F("WheelValue") +
                        g.Call(V3, "SignedAngle", new[] { V3, V3, V3 }, null, g.GraphVar("WheelLast", V3), across, Vector3.forward));
                    g.SetGraphVar("WheelLast", across);
                    g.SetGraphVar("StickYaw", g.Clamp(-turned / g.P("SteeringWheelDegrees"), -1f, 1f));
                    g.SetGraphVar("StickPitch", 0f);
                    g.SetGraphVar("StickRoll", 0f);
                }, () =>
                {
                    g.SetGraphVar("StickPitch", Deflection(g.Z(up), "MaxJoyX"));      // forward = nose down
                    g.SetGraphVar("StickYaw", Deflection(g.X(ahead), "MaxJoyY"));     // twisted right = yaw right
                    g.SetGraphVar("StickRoll", -Deflection(g.X(up), "MaxJoyZ"));      // tilted right = roll right
                });
            }, () => g.If(g.B("StickHeld"), () =>
            {
                g.SetGraphVar("StickHeld", false);
                foreach (var name in new[] { "StickPitch", "StickYaw", "StickRoll" }) g.SetGraphVar(name, 0f);
            }));

            // ---- the throttle: moved by as far as the hand has gone since it took hold, six times over
            // (ThrottleSensitivity); the afterburner only with a quick push past its detent, or once already in it.
            g.If(throttleGrip, () =>
            {
                var offset = g.Keep(HandOffset(throttleHand));
                var axis = g.Keep(g.Select(g.ObjVar("VerticalThrottle", typeof(bool)), g.Y(offset), g.Z(offset)));
                g.If(g.Not(g.B("ThrottleHeld")), () =>
                {
                    g.SetGraphVar("ThrottleHeld", true);
                    g.SetGraphVar("ThrottleZero", axis);
                    g.SetGraphVar("ThrottleTemp", g.F("Throttle"));
                    g.SetGraphVar("ThrottleAxisLast", axis);
                });
                var sensitivity = g.Keep(g.P("ThrottleSensitivity"));
                var wanted = g.F("ThrottleTemp") + (g.F("ThrottleZero") - axis) * sensitivity;
                var detent = g.Or(g.Greater((g.F("ThrottleAxisLast") - axis) * sensitivity, 0.05f), g.Greater(g.F("Throttle"), g.P("ThrottleAfterburnerPoint")));
                var top = g.Select(g.Or(g.Not(g.ObjVar("HasAfterburner", typeof(bool))), detent), 1f, g.P("ThrottleAfterburnerPoint"));
                g.SetGraphVar("Throttle", g.Clamp(wanted, 0f, top));
                g.SetGraphVar("ThrottleAxisLast", axis);
            }, () => g.SetGraphVar("ThrottleHeld", false));

            // ---- brakes: as hard as the trigger of the dial they are picked on is pulled (DFUNC_Brake)
            g.SetGraphVar("TriggerBrake", g.Max(g.Select(Picked(g, "L", "BrakeSlot"), g.F("TrigL"), 0f), g.Select(Picked(g, "R", "BrakeSlot"), g.F("TrigR"), 0f)));

            // ---- thrust tilt (DFUNC_VTOLAngle): trigger held, the hand forward tilts the thrust ahead, back tilts it up
            var tiltLeft = g.Keep(g.And(Picked(g, "L", "VtolSlot"), g.Greater(g.F("TrigL"), 0.75f)));
            var tiltRight = g.Keep(g.And(Picked(g, "R", "VtolSlot"), g.Greater(g.F("TrigR"), 0.75f)));
            g.If(g.Or(tiltLeft, tiltRight), () =>
            {
                var z = g.Keep(g.Z(HandOffset(g.Select(tiltLeft, left, right, TFT))));
                g.If(g.Not(g.B("VtolHeld")), () =>
                {
                    g.SetGraphVar("VtolHeld", true);
                    g.SetGraphVar("VtolZero", z);
                    g.SetGraphVar("VtolTemp", g.F("VTOLAngleInput"));
                });
                g.SetGraphVar("VTOLAngleInput", g.Clamp01(g.F("VtolTemp") - (g.F("VtolZero") - z) * g.P("ThrottleSensitivity")));
            }, () => g.SetGraphVar("VtolHeld", false));

            // ---- manoeuvring thrusters (SAV_ThreeDThrust): pushed the way the hand has moved since it took hold.
            // Picked on a dial: that trigger, all three ways. Not on a dial: the throttle's grip, which already
            // raises and lowers the thrust, so the hand works the sideways and ahead thrusters with it.
            var thrustLeft = g.Keep(g.And(Picked(g, "L", "ThrustSlot"), g.Greater(g.F("TrigL"), 0.75f)));
            var thrustRight = g.Keep(g.And(Picked(g, "R", "ThrustSlot"), g.Greater(g.F("TrigR"), 0.75f)));
            var onDial = g.Keep(g.ObjVar("ThrustersOnDial", typeof(bool)));
            var byThrottle = g.Keep(g.And(g.And(g.ObjVar("HasThrusters", typeof(bool)), g.Not(onDial)), throttleGrip));
            g.If(g.Or(g.Or(thrustLeft, thrustRight), byThrottle), () =>
            {
                var hand = g.Select(thrustLeft, left, g.Select(thrustRight, right, throttleHand, TFT), TFT);
                var at = g.Keep(InVehicle(g.Get(TFT, "position", hand) - g.Get(TFT, "position")));
                g.If(g.Not(g.B("ThrustHeld")), () =>
                {
                    g.SetGraphVar("ThrustHeld", true);
                    g.SetGraphVar("ThrustZero", at);
                });
                var moved = g.Keep((at - g.GraphVar("ThrustZero", V3)) * g.P("ThrottleSensitivity"));
                g.SetGraphVar("StickThrustX", g.Clamp(g.X(moved), -1f, 1f));
                g.SetGraphVar("StickThrustZ", g.Clamp(g.Z(moved), -1f, 1f));
                g.SetGraphVar("StickThrustY", g.Select(byThrottle, 0f, g.Clamp(g.Y(moved), -1f, 1f)));
            }, () => g.If(g.B("ThrustHeld"), () =>
            {
                g.SetGraphVar("ThrustHeld", false);
                foreach (var name in new[] { "StickThrustX", "StickThrustY", "StickThrustZ" }) g.SetGraphVar(name, 0f);
            }));

            // ---- the other dial functions: gun, flares, bombs, cruise speed, smoke colour, missile lock, guided-bomb aim
            FunctionsFrame(g, g.Keep(g.Get(typeof(Time), "deltaTime")));
        }
    }
}
