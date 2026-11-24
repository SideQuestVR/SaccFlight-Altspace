using System;
using Unity.VisualScripting;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The drivers' own dial functions of SaccGroundVehicle's cars and tank, on buttons (a car has no dial here):
    ///  - horn (DFUNC_Horn): right thumbstick held down;
    ///  - headlights (DFUNC_ToggleBool): a click of the left thumbstick;
    ///  - boost (DFUNC_CarBoost): B held, for as long as the boost lasts (it fills again while not used);
    ///  - jump (DFUNC_CarJump): Y; a push up at once and more while held, for a moment;
    ///  - air control (SGV_AirControl): with the right grip held the right stick pitches and rolls the car and the left
    ///    stick's sideways turns it, wheels on the ground or not (as the original allows);
    ///  - reset (DFUNC_ResetCar): X stands the car upright where it is, facing the way it faced (a car on its roof).
    /// Others hear the horn and see the lights, the boost's flame and the jump from one-shots "{BoomMessage}|{kind}|...".
    /// The numbers are each vehicle's own (SaccGroundBuilder.ReadDriverExtras).
    /// </summary>
    public static partial class GroundGraphs
    {
        static void DeclareExtras(GraphWriter g)
        {
            foreach (var (name, value) in new (string, object)[]
                     {
                         ("HornOn", false), ("LightsOn", false), ("BoostHeld", false), ("Boosting", false), ("BoostLeft", 1f),
                         ("JumpHeld", false), ("JumpTime", 0f), ("AirHeld", 0f),
                     })
                g.Declare(name, value);
        }

        static void ExtrasSay(GraphWriter g, string kind, object argument)
        {
            var head = g.Concat(g.ObjVar("BoomMessage", typeof(string)), "|" + kind + "|");
            g.PlatformDo("SendOneShot", ("Data", g.Concat(head, argument)), ("All Instances", false));
        }

        static void HornSet(GraphWriter g, Val on)
        {
            g.SetGraphVar("HornOn", on);
            g.If(on, () => g.Do(typeof(AudioSource), "Play", Type.EmptyTypes, g.ObjVar("HornSound", typeof(AudioSource))),
                () => g.Do(typeof(AudioSource), "Stop", Type.EmptyTypes, g.ObjVar("HornSound", typeof(AudioSource))));
        }

        static void LightsSet(GraphWriter g, Val on)
        {
            var state = g.SetGraphVar("LightsOn", on, typeof(bool));
            g.ForEach(g.ObjVar("LightObjects", List), GO, item => g.Do(GO, "SetActive", new[] { typeof(bool) }, item, state));
            g.If(g.Greater(g.Get(typeof(string), "Length", g.ObjVar("LightBool", typeof(string))), 0.5f), () =>
                g.Do(AN, "SetBool", new[] { typeof(string), typeof(bool) }, Anim(g), g.ObjVar("LightBool", typeof(string)), state));
        }

        static void BoostShow(GraphWriter g, Val on) =>
            g.If(g.Greater(g.Get(typeof(string), "Length", g.ObjVar("BoostFloat", typeof(string))), 0.5f), () =>
                g.Do(AN, "SetFloat", new[] { typeof(string), typeof(float) }, Anim(g), g.ObjVar("BoostFloat", typeof(string)), g.Select(on, 1f, 0f)));

        /// <summary>The buttons, and the one-shots from other clients' drivers. Event handlers of their own.</summary>
        static void ExtrasEvents(GraphWriter g)
        {
            // (button 3 = secondary, B / Y; 4 = thumbstick click; 1 = grip; hand 1 = right)
            foreach (var (unit, pressed) in new[] { ("OnControllerButtonPressed", true), ("OnControllerButtonReleased", false) })
                g.On(g.Platform(unit), e => g.If(g.B("Driving"), () =>
                {
                    var button = g.Keep(g.EventOut(e, "Button Type", typeof(int)));
                    var right = g.Keep(g.Same(g.EventOut(e, "Hand Side", typeof(int)), 1));
                    g.If(g.And(g.And(g.Same(button, 4), right), g.ObjVar("HasHorn", typeof(bool))), () =>
                    {
                        HornSet(g, g.Same(1, pressed ? 1 : 2));
                        ExtrasSay(g, "horn", pressed ? "1" : "0");
                    });
                    if (pressed)
                        g.If(g.And(g.And(g.Same(button, 4), g.Not(right)), g.ObjVar("HasLights", typeof(bool))), () =>
                        {
                            LightsSet(g, g.Not(g.B("LightsOn")));
                            ExtrasSay(g, "lights", g.Select(g.B("LightsOn"), "1", "0", typeof(string)));
                        });
                    g.If(g.And(g.Same(button, 3), right), () => g.SetGraphVar("BoostHeld", pressed));
                    g.If(g.And(g.Same(button, 3), g.Not(right)), () =>
                    {
                        g.SetGraphVar("JumpHeld", pressed);
                        if (pressed)
                            g.If(g.And(g.ObjVar("HasJump", typeof(bool)), g.Less(g.F("JumpTime"), 0.0001f)), () =>
                            {
                                // the push up at once (DFUNC_CarJump: JumpForce as a change of speed), more while held
                                g.SetGraphVar("JumpTime", g.P("JumpSeconds"));
                                g.Do(RB, "AddForce", new[] { V3T, typeof(ForceMode) }, null, g.Scale(g.Get(TF, "up"), g.P("JumpImpulse")), ForceMode.VelocityChange);
                                g.If(g.Greater(g.Get(typeof(string), "Length", g.ObjVar("JumpTrigger", typeof(string))), 0.5f), () =>
                                    g.Do(AN, "SetTrigger", new[] { typeof(string) }, Anim(g), g.ObjVar("JumpTrigger", typeof(string))));
                                g.Do(typeof(AudioSource), "Play", Type.EmptyTypes, g.ObjVar("JumpSound", typeof(AudioSource)));
                                ExtrasSay(g, "jump", "1");
                            });
                    });
                    g.If(g.And(g.Same(button, 1), right), () => g.SetGraphVar("AirHeld", pressed ? 1f : 0f));
                    // (button 2 = primary: X on the left hand; A on the right is the app's way out of a seat)
                    if (pressed)
                        g.If(g.And(g.And(g.Same(button, 2), g.Not(right)), g.And(g.ObjVar("HasReset", typeof(bool)),
                            g.Less(g.Magnitude(g.Get(RB, "linearVelocity")), g.P("ResetMaxSpeed")))), () =>
                        {
                            g.If(g.ObjVar("ResetStop", typeof(bool)), () => g.Set(RB, "linearVelocity", null, g.V3(0f, 0f, 0f)));
                            g.If(g.ObjVar("ResetStopSpin", typeof(bool)), () => g.Set(RB, "angularVelocity", null, g.V3(0f, 0f, 0f)));
                            g.Set(TF, "eulerAngles", null, g.V3(0f, g.Y(g.Get(TF, "eulerAngles")), 0f));
                            var place = g.Keep(g.Get(TF, "position") + g.V3(0f, g.P("ResetHeight"), 0f));
                            g.Set(TF, "position", null, place);
                            g.Set(RB, "position", null, place);
                            g.Set(RB, "rotation", null, g.Get(TF, "rotation"));
                            g.SetGraphVar("YawInput", 0f);
                        });
                }));

            g.On(g.Platform("OnOneShot"), e =>
            {
                var data = g.Keep(g.EventOut(e, "Data", typeof(string)));
                g.If(g.StartsWith(data, g.Concat(g.ObjVar("BoomMessage", typeof(string)), "|")), () =>
                {
                    var parts = g.Keep(g.Split(data, "|"));
                    var kind = g.Keep(g.ListItem(parts, 1, typeof(string)));
                    var on = g.Keep(g.Same(g.ListItem(parts, 2, typeof(string)), "1"));
                    Wake(g);
                    g.If(g.Same(kind, "horn"), () => HornSet(g, on));
                    g.If(g.Same(kind, "lights"), () => LightsSet(g, on));
                    g.If(g.Same(kind, "boost"), () => BoostShow(g, on));
                    g.If(g.Same(kind, "jump"), () =>
                    {
                        g.If(g.Greater(g.Get(typeof(string), "Length", g.ObjVar("JumpTrigger", typeof(string))), 0.5f), () =>
                            g.Do(AN, "SetTrigger", new[] { typeof(string) }, Anim(g), g.ObjVar("JumpTrigger", typeof(string))));
                        g.Do(typeof(AudioSource), "Play", Type.EmptyTypes, g.ObjVar("JumpSound", typeof(AudioSource)));
                    });
                });
            });

            // every physics step while driving: the boost's push, the held jump, air control
            g.On(new FixedUpdate(), _ => g.If(g.B("Driving"), () =>
            {
                var dt = g.Keep(g.Get(typeof(Time), "fixedDeltaTime"));
                g.If(g.ObjVar("HasBoost", typeof(bool)), () =>
                {
                    var boosting = g.Keep(g.And(g.B("BoostHeld"), g.Greater(g.F("BoostLeft"), 0f)));
                    g.If(boosting, () =>
                    {
                        g.SetGraphVar("BoostLeft", g.Max(g.F("BoostLeft") - dt / g.P("BoostSeconds"), 0f));
                        var point = g.Keep(g.ObjVar("BoostPoint", TF));
                        g.Do(RB, "AddForceAtPosition", new[] { V3T, V3T, typeof(ForceMode) }, null,
                            g.Scale(g.Get(TF, "forward", point), g.P("BoostAccel") * g.Get(RB, "mass")), g.Get(TF, "position", point), ForceMode.Force);
                    }, () => g.SetGraphVar("BoostLeft", g.Min(g.F("BoostLeft") + dt / g.P("BoostRefill"), 1f)));
                    g.If(g.Not(g.Same(boosting, g.B("Boosting"))), () =>
                    {
                        g.SetGraphVar("Boosting", boosting);
                        BoostShow(g, boosting);
                        ExtrasSay(g, "boost", g.Select(boosting, "1", "0", typeof(string)));
                    });
                });
                g.If(g.Greater(g.F("JumpTime"), 0f), () =>
                {
                    g.SetGraphVar("JumpTime", g.Max(g.F("JumpTime") - dt, 0f));
                    g.If(g.B("JumpHeld"), () =>
                        g.Do(RB, "AddForce", new[] { V3T, typeof(ForceMode) }, null, g.Scale(g.Get(TF, "up"), g.P("JumpHold")), ForceMode.Acceleration));
                });
                g.If(g.And(g.ObjVar("HasAirControl", typeof(bool)), g.Greater(g.F("AirHeld"), 0.5f)), () =>
                {
                    // (SGV_AirControl: pitch and roll from the stick, yaw from the other; as a turn rate pushed towards)
                    var torque = g.V3(-g.F("StickRY") * g.P("AirPitch"), g.F("StickSteerL") * g.P("AirYaw"), -g.F("StickSteerR") * g.P("AirRoll"));
                    g.Do(RB, "AddRelativeTorque", new[] { V3T, typeof(ForceMode) }, null, torque, ForceMode.Acceleration);
                });
            }));
        }

        /// <summary>Let go of everything when the driver gets out.</summary>
        static void ExtrasRelease(GraphWriter g)
        {
            g.If(g.B("HornOn"), () =>
            {
                HornSet(g, g.Same(1, 2));
                ExtrasSay(g, "horn", "0");
            });
            g.If(g.B("Boosting"), () =>
            {
                g.SetGraphVar("Boosting", false);
                BoostShow(g, g.Same(1, 2));
                ExtrasSay(g, "boost", "0");
            });
            g.SetGraphVar("BoostHeld", false);
            g.SetGraphVar("JumpHeld", false);
            g.SetGraphVar("AirHeld", 0f);
        }
    }
}
