using System;
using Unity.VisualScripting;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Gunner seats: Sacc's passenger functions (SAV_PassengerFunctionsController) for a seat with a turret and its own
    /// weapons, as one graph per gunner seat (GunnerSeats.cs builds them and gives each its settings as object
    /// variables). Ported from EXT_HandTurret, EXT_Turret, DFUNC_Gun, DFUNC_Bomb and DFUNC_ToggleBool:
    ///  - The seat is the gunner's once they click it (the seat component sits them), until the app lifts them off it.
    ///  - A hand turret (door guns) points where the gunner's hand points (Aim_HandDirection) or from their head through
    ///    their hand, within the original's angle limits. A stick turret (the tank's, the boat's back gun) turns at the
    ///    original's speed while the thumbstick is held over.
    ///  - The right trigger fires what the original has on its right dial, the left trigger what is on its left: a gun
    ///    (its animator bool, hits scanned along its line as the pilot's gun is) or a launcher (rockets, cannon shells,
    ///    from a pool built for the seat). A thumbstick click works that side's switch (searchlight, door, hatch).
    ///  - Aim, firing and switches go to everyone else in one-shots: "{BoomMessage}|{kind}|...".
    /// Not ported here: the gunner's guided bomb (SH-3), smoke screen and zoom (tank), and dial picking: each trigger has
    /// one weapon.
    /// </summary>
    public static partial class FlightGraphs
    {
        public const string GunnerPath = "Assets/Flight/Graphs/GunnerCore.asset";

        public static GraphWriter BuildGunner()
        {
            var g = new GraphWriter();
            foreach (var (name, value) in new (string, object)[]
                     {
                         ("Gunning", false), ("Yaw", 0f), ("Pitch", 0f), ("SentYaw", 0f), ("SentPitch", 0f), ("SendTimer", 0f), ("LastFrame", 0f),
                         ("TrigL", 0f), ("TrigR", 0f), ("StickLX", 0f), ("StickLY", 0f), ("StickRX", 0f), ("StickRY", 0f),
                         ("YawSpeed", 0f), ("PitchSpeed", 0f), ("WakeTimer", 0f),
                         ("GunFiring", false), ("GunAmmo", 0f), ("GunScan", 0f), ("GunQueue", 0f), ("GunSendTimer", 0f), ("HasGunTarget", false),
                         ("GunBestAlong", 0f), ("GunBestFound", false),
                         ("BombCount", 0f), ("BombNext", 0f), ("BombPoint", 0f), ("Bomb2Count", 0f), ("Bomb2Next", 0f), ("Bomb2Point", 0f),
                         ("ToggleLOn", false), ("ToggleROn", false),
                         ("PoolIndex", 0f), ("PickedIndex", -1f), ("Picked", false),
                     })
                g.Declare(name, value);
            foreach (var name in new[] { "GunTarget", "GunBest", "PickedRound" }) g.Declare(name, null);

            Val Vehicle() => g.ObjVar("Vehicle", GO);
            Val Watch() => g.ObjVar("Watch", GO);

            g.On(new Start(), _ =>
            {
                GunnerRefill(g);
                AimApply(g);
            });

            // ---- the seat: the click that seats the player makes them its gunner ------------------------------------
            g.On(g.Platform("OnClick", ("banterPlayerEvents", g.ObjVar("Seat", GO))), _ =>
                g.If(g.And(g.Not(g.B("Gunning")), g.Not(g.Get(Seat, "IsOccupiedByOther", g.ObjVar("SeatComponent", Seat)))), () =>
                {
                    g.SetGraphVar("Gunning", true);
                    GunnerRefill(g);
                    g.SetGraphVar("LastFrame", Now(g));
                    g.SetObjVar("Gunner", Self(g), Watch());
                    g.SetObjVar("HasGunner", true, Watch());
                    // the hands are followed by the fleet watch's two hand objects, put on this player's hands
                    foreach (var hand in new[] { "LeftHand", "RightHand" })
                        g.PlatformDo("AttachToPlayer", ("Object", g.Get(TF, "gameObject", g.ObjVar(hand, TF))));
                    Trigger(g, Vehicle(), "fw_wake");
                    g.PlatformDo("AddToastMessage", ("Message", g.ObjVar("HowTo", typeof(string))));
                }));
            // getting off a seat always lifts the player (OnPlayerTeleported); sitting down does not
            g.On(g.Platform("OnPlayerTeleported"), _ => g.If(g.B("Gunning"), () => GunnerLeave(g)));

            // ---- controls, only while gunning ------------------------------------------------------------------------
            g.On(g.Platform("OnTriggerAxisUpdate"), e => g.If(g.B("Gunning"), () =>
                g.If(g.Same(g.EventOut(e, "Hand Side", typeof(int)), 1),
                    () => g.SetGraphVar("TrigR", g.EventOut(e, "Trigger Value", typeof(float))),
                    () => g.SetGraphVar("TrigL", g.EventOut(e, "Trigger Value", typeof(float))))));
            g.On(g.Platform("OnControllerAxisUpdate"), e => g.If(g.B("Gunning"), () =>
            {
                var x = g.EventOut(e, "X Axis", typeof(float));
                var y = g.EventOut(e, "Y Axis", typeof(float));
                g.If(g.Same(g.EventOut(e, "Hand Side", typeof(int)), 1), () =>
                {
                    g.SetGraphVar("StickRX", x);
                    g.SetGraphVar("StickRY", y);
                }, () =>
                {
                    g.SetGraphVar("StickLX", x);
                    g.SetGraphVar("StickLY", y);
                });
            }));
            // a thumbstick click works the switch on that side's dial (or the other side's, if that side has none)
            g.On(g.Platform("OnControllerButtonPressed"), e => g.If(g.And(g.B("Gunning"), g.Same(g.EventOut(e, "Button Type", typeof(int)), 4)), () =>
            {
                var right = g.Keep(g.Same(g.EventOut(e, "Hand Side", typeof(int)), 1));
                var useRight = g.Keep(g.Select(right, g.ObjVar("HasToggleR", typeof(bool)), g.Not(g.ObjVar("HasToggleL", typeof(bool))), typeof(bool)));
                g.If(useRight, () => g.If(g.ObjVar("HasToggleR", typeof(bool)), () => ToggleSet(g, "R", g.Not(g.B("ToggleROn")), true)),
                    () => g.If(g.ObjVar("HasToggleL", typeof(bool)), () => ToggleSet(g, "L", g.Not(g.B("ToggleLOn")), true)));
            }));

            // ---- every frame while gunning, after the hand objects have moved (the fleet watch's LateUpdate) ----------
            g.On(Custom(g, "fw_gun"), _ => g.If(g.B("Gunning"), () =>
            {
                var now = g.Keep(Now(g));
                var dt = g.Keep(g.Clamp(now - g.F("LastFrame"), 0f, 0.1f));
                g.SetGraphVar("LastFrame", now);
                AimStep(g, dt);
                GunnerFire(g, dt, now);
                // others see the turret turn: ten times a second while it moves
                var send = g.SetGraphVar("SendTimer", g.F("SendTimer") + dt);
                g.If(g.And(g.Greater(send, 0.1f), g.Greater(g.Abs(g.F("Yaw") - g.F("SentYaw")) + g.Abs(g.F("Pitch") - g.F("SentPitch")), 0.3f)), () =>
                {
                    g.SetGraphVar("SendTimer", 0f);
                    g.SetGraphVar("SentYaw", g.F("Yaw"));
                    g.SetGraphVar("SentPitch", g.F("Pitch"));
                    Say(g, "aim", g.Concat(g.NumberText(g.F("Yaw")), "|", g.NumberText(g.F("Pitch"))));
                });
                // keep the vehicle awake (its animator draws the gun's fire and plays its sound)
                var wake = g.SetGraphVar("WakeTimer", g.F("WakeTimer") + dt);
                g.If(g.Greater(wake, 1f), () =>
                {
                    g.SetGraphVar("WakeTimer", 0f);
                    Trigger(g, Vehicle(), "fw_wake");
                });
            }));

            // ---- what everyone else does with the gunner's one-shots ----------------------------------------------------
            g.On(g.Platform("OnOneShot"), e =>
            {
                var data = g.Keep(g.EventOut(e, "Data", typeof(string)));
                g.If(g.StartsWith(data, g.Concat(g.ObjVar("BoomMessage", typeof(string)), "|")), () =>
                {
                    var parts = g.Keep(g.Split(data, "|"));
                    var kind = g.Keep(g.ListItem(parts, 1, typeof(string)));
                    Val Arg(int i) => g.ListItem(parts, i, typeof(string));
                    Trigger(g, Vehicle(), "fw_wake");
                    g.If(g.Same(kind, "aim"), () =>
                    {
                        g.SetGraphVar("Yaw", g.ParseNumber(Arg(2)));
                        g.SetGraphVar("Pitch", g.ParseNumber(Arg(3)));
                        AimApply(g);
                    });
                    g.If(g.Same(kind, "gun"), () => AnimBoolNamed(g, g.ObjVar("GunBool", typeof(string)), g.Same(Arg(2), "1")));
                    foreach (var (prefix, message) in new[] { ("Bomb", "bomb"), ("Bomb2", "bomb2") })
                        g.If(g.Same(kind, message), () =>
                        {
                            var from = g.ListItem(g.ObjVar(prefix + "Points", LIST), IndexOf(g, Arg(3)), TF);
                            GunnerLaunch(g, g.ListItem(g.ObjVar(prefix + "Pool", LIST), IndexOf(g, Arg(2)), GO), from, false);
                            Fired(g, prefix + "Count", prefix + "Full", prefix + "Float", prefix + "Fired");
                        });
                    foreach (var side in new[] { "L", "R" })
                        g.If(g.And(g.Same(kind, "toggle"), g.Same(Arg(2), side)), () => ToggleSet(g, side, g.Same(Arg(3), "1"), false));
                });
            });
            return g;
        }

        static void GunnerRefill(GraphWriter g)
        {
            g.SetGraphVar("GunAmmo", g.P("GunAmmoFull"));
            g.SetGraphVar("BombCount", g.P("BombFull"));
            g.SetGraphVar("Bomb2Count", g.P("Bomb2Full"));
        }

        static void GunnerLeave(GraphWriter g)
        {
            g.If(g.B("GunFiring"), () =>
            {
                g.SetGraphVar("GunFiring", false);
                AnimBoolNamed(g, g.ObjVar("GunBool", typeof(string)), false);
                Say(g, "gun", "0");
            });
            GunFlush(g);
            g.SetGraphVar("Gunning", false);
            g.SetGraphVar("TrigL", 0f);
            g.SetGraphVar("TrigR", 0f);
            foreach (var axis in new[] { "StickLX", "StickLY", "StickRX", "StickRY", "YawSpeed", "PitchSpeed" }) g.SetGraphVar(axis, 0f);
            var watch = g.Keep(g.ObjVar("Watch", GO));
            g.If(g.Same(g.ObjVarOf(watch, "Gunner", GO), Self(g)), () => g.SetObjVar("HasGunner", false, watch));
        }

        static Val Deg(GraphWriter g, Val y, Val x) =>
            g.Call(typeof(Mathf), "Atan2", new[] { typeof(float), typeof(float) }, null, y, x) * Mathf.Rad2Deg;

        /// <summary>Where the turret points this frame: from the hand (hand turrets) or by the sticks (stick turrets).</summary>
        static void AimStep(GraphWriter g, Val dt)
        {
            var mode = g.Keep(g.P("AimMode"));
            // 1: along the hand; 2: from the head through the hand (EXT_HandTurret)
            g.If(g.And(g.Greater(mode, 0.5f), g.Less(mode, 2.5f)), () =>
            {
                var hand = g.Keep(g.ObjVar("AimHand", TF));
                var head = g.PlatformValue("GetLocalUserState", "Position", V3T);
                var dir = g.Keep(g.Select(g.Less(mode, 1.5f), g.Get(TF, "forward", hand), g.Get(TF, "position", hand) - head, V3T));
                var local = g.Keep(g.Call(TF, "InverseTransformDirection", Vec, g.ObjVar("YawParent", TF), dir));
                var flat = g.Keep(g.Call(typeof(Mathf), "Sqrt", new[] { typeof(float) }, null, g.X(local) * g.X(local) + g.Z(local) * g.Z(local)));
                var yaw = g.Call(typeof(Mathf), "DeltaAngle", new[] { typeof(float), typeof(float) }, null, g.P("RestYaw"), Deg(g, g.X(local), g.Z(local)));
                var wantYaw = g.Keep(g.Clamp(yaw, -g.P("LimitLeft"), g.P("LimitRight")));
                var wantPitch = g.Keep(g.Clamp(Deg(g, g.Y(local), flat), -g.P("LimitDown"), g.P("LimitUp")));
                var blend = g.Keep(g.Blend(18f, dt));
                g.SetGraphVar("Yaw", g.Lerp(g.F("Yaw"), wantYaw, blend));
                g.SetGraphVar("Pitch", g.Lerp(g.F("Pitch"), wantPitch, blend));
                AimApply(g);
            });
            // 3: turned by the thumbsticks (EXT_Turret): speed eased towards the stick, the angle moved by the speed
            g.If(g.Greater(mode, 2.5f), () =>
            {
                var x = g.Keep(g.Clamp(g.F("StickRX") * g.P("UseStickR") + g.F("StickLX") * g.P("UseStickL"), -1f, 1f));
                var y = g.Keep(g.Clamp(g.F("StickRY") * g.P("UseStickR") + g.F("StickLY") * g.P("UseStickL"), -1f, 1f));
                var ease = g.Keep(g.Blend(g.P("TurnFriction"), dt));
                var yawSpeed = g.SetGraphVar("YawSpeed", g.Lerp(g.F("YawSpeed"), x * g.P("TurnRateX"), ease));
                var pitchSpeed = g.SetGraphVar("PitchSpeed", g.Lerp(g.F("PitchSpeed"), y * g.P("TurnRateY"), ease));
                var yaw = g.Keep(g.F("Yaw") + yawSpeed * dt);
                // all the way round when the original allows it, else between its side limits
                g.SetGraphVar("Yaw", g.Select(g.Greater(g.P("LimitRight"), 179f),
                    g.Call(typeof(Mathf), "DeltaAngle", new[] { typeof(float), typeof(float) }, null, 0f, yaw),
                    g.Clamp(yaw, -g.P("LimitLeft"), g.P("LimitRight")), typeof(float)));
                g.SetGraphVar("Pitch", g.Clamp(g.F("Pitch") + pitchSpeed * dt, -g.P("LimitDown"), g.P("LimitUp")));
                AimApply(g);
            });
        }

        /// <summary>The turret's parts turned to Yaw and Pitch (degrees from where they rest; muzzles along their +Z).</summary>
        static void AimApply(GraphWriter g)
        {
            g.If(g.Greater(g.P("AimMode"), 0.5f), () =>
            {
                var yaw = g.Keep(g.P("RestYaw") + g.F("Yaw"));
                g.If(g.ObjVar("HasPitchPart", typeof(bool)), () =>
                {
                    g.Set(TF, "localEulerAngles", g.ObjVar("YawPart", TF), g.V3(0f, yaw, 0f));
                    g.Set(TF, "localEulerAngles", g.ObjVar("PitchPart", TF), g.V3(-g.F("Pitch"), 0f, 0f));
                }, () => g.Set(TF, "localEulerAngles", g.ObjVar("YawPart", TF), g.V3(-g.F("Pitch"), yaw, 0f)));
            });
        }

        /// <summary>The gun while its trigger is held, and each launcher (one round on the pull, more while held).</summary>
        static void GunnerFire(GraphWriter g, Val dt, Val now)
        {
            Val Trig(string sideVar) => g.Select(g.Greater(g.P(sideVar), 0.5f), g.F("TrigR"), g.F("TrigL"), typeof(float));

            g.If(g.ObjVar("HasGun", typeof(bool)), () =>
            {
                var shooting = g.Keep(g.And(g.Greater(Trig("GunSide"), 0.75f), g.Greater(g.F("GunAmmo"), 0f)));
                g.If(g.Not(g.Same(shooting, g.B("GunFiring"))), () =>
                {
                    g.SetGraphVar("GunFiring", shooting);
                    AnimBoolNamed(g, g.ObjVar("GunBool", typeof(string)), shooting);
                    Say(g, "gun", g.Select(shooting, "1", "0", typeof(string)));
                });
                g.If(shooting, () =>
                {
                    g.SetGraphVar("GunAmmo", g.F("GunAmmo") - dt);
                    GunScan(g, dt, () => g.ObjVar("Vehicle", GO));
                }, () => GunFlush(g));
            });

            foreach (var (prefix, message) in new[] { ("Bomb", "bomb"), ("Bomb2", "bomb2") })
            {
                var held = g.Keep(g.And(g.Greater(g.P(prefix + "PoolSize"), 0.5f), g.Greater(Trig(prefix + "Side"), 0.75f)));
                g.If(g.And(g.And(held, g.Greater(now, g.F(prefix + "Next"))), g.Greater(g.F(prefix + "Count"), 0.5f)), () =>
                {
                    g.SetGraphVar(prefix + "Next", now + g.P(prefix + "Hold"));
                    PickRound(g, prefix + "Pool");
                    g.If(g.B("Picked"), () =>
                    {
                        var point = g.Keep(g.Call(typeof(Mathf), "Repeat", new[] { typeof(float), typeof(float) }, null, g.F(prefix + "Point"), g.Max(g.P(prefix + "PointCount"), 1f)));
                        g.SetGraphVar(prefix + "Point", point + 1f);
                        var from = g.ListItem(g.ObjVar(prefix + "Points", LIST), g.Call(typeof(Mathf), "FloorToInt", new[] { typeof(float) }, null, point), TF);
                        GunnerLaunch(g, g.GraphVar("PickedRound", GO), from, true);
                        Say(g, message, g.Concat(g.NumberText(g.F("PickedIndex")), "|", g.NumberText(point)));
                        Fired(g, prefix + "Count", prefix + "Full", prefix + "Float", prefix + "Fired");
                    });
                });
                g.If(g.Not(held), () => g.SetGraphVar(prefix + "Next", 0f));
            }
        }

        /// <summary>A searchlight, door or hatch: the vehicle animator's bool and/or the objects the original switches.</summary>
        static void ToggleSet(GraphWriter g, string side, Val on, bool tell)
        {
            var state = g.SetGraphVar("Toggle" + side + "On", on, typeof(bool));
            g.If(g.Greater(g.Get(typeof(string), "Length", g.ObjVar("Toggle" + side + "Bool", typeof(string))), 0.5f), () =>
                AnimBoolNamed(g, g.ObjVar("Toggle" + side + "Bool", typeof(string)), state));
            ShowAll(g, "Toggle" + side + "Objects", state);
            if (tell) Say(g, "toggle", g.Concat(side + "|", g.Select(state, "1", "0", typeof(string))));
        }

        /// <summary>
        /// A gunner's round on its way (every client, from the firer's message): as the pilot's Launch, from a launch point
        /// on the turret, with the vehicle's velocity; the vehicle is its shooter.
        /// </summary>
        static void GunnerLaunch(GraphWriter g, Val round, Val from, bool mine)
        {
            var shot = g.Keep(round);
            var point = g.Keep(from);
            var world = g.Keep(g.ObjVar("WeaponsWorld", GO));
            var vehicle = g.Keep(g.ObjVar("Vehicle", GO));
            var start = g.Keep(g.Get(TF, "position", point));
            var velocity = g.Get(RB, "linearVelocity", g.ObjVar("VehicleBody", RB)) + g.Scale(g.Get(TF, "forward", point), g.ObjVarOf(shot, "LaunchSpeed", typeof(float)));
            foreach (var (name, value) in new (string, object)[]
                     {
                         ("Alive", true), ("Exploded", false), ("Age", 0f), ("Lost", false), ("DumbTime", 0f), ("Mine", mine),
                         ("Shooter", vehicle), ("LaunchPos", start), ("Velocity", velocity), ("HasTarget", false), ("Target", vehicle),
                         ("TargetIsVehicle", false), ("TargetPoint", Vector3Zero(g)), ("TargetOffset", Vector3Zero(g)), ("LastTargetPos", start),
                     })
                g.SetObjVar(name, value, shot);
            g.SetObjVar("LastDist", 0f, shot);
            var shotTransform = g.Keep(g.Get(GO, "transform", shot));
            g.Do(TF, "SetParent", new[] { TF, typeof(bool) }, shotTransform, g.Null(TF), true);
            g.Do(TF, "SetPositionAndRotation", new[] { V3T, typeof(Quaternion) }, shotTransform, start, g.Get(TF, "rotation", point));
            g.Do(GO, "SetActive", new[] { typeof(bool) }, shot, true);
            g.ForEach(g.ObjVarOf(shot, "Trails", ENUM), typeof(TrailRenderer), trail => g.Do(typeof(TrailRenderer), "Clear", Type.EmptyTypes, trail));
            g.If(g.Not(g.ObjVarOf(shot, "Listed", typeof(bool))), () =>
            {
                g.ListAdd(g.ObjVarOf(world, "LiveShots", LIST), shot);
                g.SetObjVar("Listed", true, shot);
            });
            g.SetObjVar("LiveCount", g.ObjVarOf(world, "LiveCount", typeof(float)) + 1f, world);
        }
    }
}
