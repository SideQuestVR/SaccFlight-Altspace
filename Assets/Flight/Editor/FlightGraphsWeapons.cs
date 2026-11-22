using System;
using Unity.VisualScripting;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The weapons graph: one for the world, on the WeaponsWorld object. Every frame that anything is in the air it
    /// flies each missile, guided bomb and bomb a vehicle has launched (FlightGraphsFunctions.Launch puts them on its
    /// list), on every client alike:
    ///  - missiles burn for three seconds and steer at RotSpeed toward where the target will be
    ///    (SAV_AAMController: closing-speed time to go, at most 3 s; aim extended 1.2 against the drift); flares
    ///    the target has just dropped make the seeker go blind for a moment, each with a ten per cent chance a frame,
    ///    and a seeker blind for 0.1 s in all, or looking more than 90 degrees off, gives up and flies straight;
    ///  - guided bombs steer the same way at their own slow rate toward a point (or a point on a vehicle);
    ///  - bombs fall.
    /// A round explodes on the ground or anything solid (a line between its positions this frame), on reaching its
    /// target (its path this frame passing within the target's size; another player's vehicle has no colliders here),
    /// or at the end of its life. The firer's client then decides the damage: a missile its share of the target's
    /// health (less with distance, if it went off near rather than on it), a bomb or guided bomb its damage falling
    /// off to nothing at its blast radius, to every vehicle within it. Ten seconds later the round, its explosion done,
    /// goes back to its vehicle's pool.
    /// </summary>
    public static partial class FlightGraphs
    {
        public const string WeaponsPath = "Assets/Flight/Graphs/WeaponsWorld.asset";

        public static GraphWriter BuildWeaponsWorld()
        {
            var g = new GraphWriter();
            g.Declare("Clean", false);
            g.On(new Update(), _ => g.If(g.Greater(g.P("LiveCount"), 0.5f), () =>
            {
                var dt = g.Keep(g.Max(g.Get(typeof(Time), "deltaTime"), 0.0001f));
                var now = g.Keep(Now(g));
                g.ForEach(g.ObjVar("LiveShots", ENUM), GO, round => g.If(g.ObjVarOf(round, "Alive", typeof(bool)), () => Fly(g, round, dt, now)));
                // all of them back: empty the list (rounds that come back stay on it until then, so it never changes while looked through)
                g.If(g.Less(g.P("LiveCount"), 0.5f), () =>
                {
                    g.ForEach(g.ObjVar("LiveShots", ENUM), GO, round => g.SetObjVar("Listed", false, round));
                    g.ListClear(g.ObjVar("LiveShots", LIST));
                    g.SetObjVar("LiveCount", 0f);
                });
            }));
            return g;
        }

        static Val V(GraphWriter g, Val round, string name, Type type) => g.ObjVarOf(round, name, type);
        static Val Fv(GraphWriter g, Val round, string name) => g.ObjVarOf(round, name, typeof(float));
        static Val Bv(GraphWriter g, Val round, string name) => g.ObjVarOf(round, name, typeof(bool));

        static void Fly(GraphWriter g, Val round, Val dt, Val now)
        {
            var kind = g.Keep(Fv(g, round, "Kind"));
            var age = g.SetObjVar("Age", Fv(g, round, "Age") + dt, round, typeof(float));
            var body = g.Keep(g.Get(GO, "transform", round));
            g.If(Bv(g, round, "Exploded"), () => g.If(g.Greater(age, Fv(g, round, "ExplodeLife")), () => Finish(g, round)), () =>
            {
                var pos = g.Keep(g.Get(TF, "position", body));
                var nose = g.Keep(g.Get(TF, "forward", body));
                var vel0 = V(g, round, "Velocity", V3T);
                var push = g.Select(g.Less(age, Fv(g, round, "BurnTime")), Fv(g, round, "Thrust"), 0f);
                var fall = g.Select(Bv(g, round, "Gravity"), new Vector3(0f, -9.81f, 0f), Vector3.zero, V3T);
                var vel1 = g.Keep(vel0 + g.Scale(nose, push * dt) + g.Scale(fall, dt));
                var vel2 = g.Keep(g.Scale(vel1, 1f / (1f + Fv(g, round, "Drag") * dt)));
                // the air turns its flight toward where it points (Sacc pushes against the sideways and vertical drift)
                var drift = g.Keep(vel2 - g.Scale(nose, g.Dot(vel2, nose)));
                var vel = g.Keep(vel2 - g.Scale(drift, g.Min(Fv(g, round, "AirPhysics") * dt, 1f)));
                var hasTarget = g.Keep(g.And(Bv(g, round, "HasTarget"), g.Less(kind, 2.5f)));
                var onVehicle = g.Keep(g.And(hasTarget, Bv(g, round, "TargetIsVehicle")));
                var target = g.Keep(V(g, round, "Target", GO));
                var goal = g.Keep(g.Select(onVehicle, g.Get(TF, "position", g.Get(GO, "transform", target)) + V(g, round, "TargetOffset", V3T),
                    V(g, round, "TargetPoint", V3T), V3T));

                // ---- steering
                g.If(g.And(g.And(hasTarget, g.Not(Bv(g, round, "Lost"))), g.Greater(age, Fv(g, round, "FlyStraight"))), () =>
                {
                    var distance = g.Keep(g.Distance(goal, pos));
                    var closing = (Fv(g, round, "LastDist") - distance) / dt;
                    var toGo = g.Min(distance / g.Max(closing, 0.001f), 3f);
                    var targetVel = g.Scale(goal - V(g, round, "LastTargetPos", V3T), 1f / dt);
                    var aim = g.Keep(g.Select(g.Same(kind, 1f), goal + g.Scale(targetVel, toGo) - pos, goal - pos, V3T));
                    var flares = g.Select(g.And(onVehicle, g.Less(now - g.ObjVarOf(target, "FlareTime", typeof(float)), 4f)), g.ObjVarOf(target, "FlareBurst", typeof(float)), 0f);
                    var blind = g.Or(g.Greater(g.Angle(nose, aim), Fv(g, round, "TrackAngle")),
                        g.And(g.Same(kind, 1f), g.Less(g.RandomValue() * 100f, flares * Fv(g, round, "FlareEffect"))));
                    // (a heat-seeker blind for long enough gives up for good; a guided bomb just does not steer while it is off to one side)
                    g.If(blind, () => g.If(g.Same(kind, 1f), () =>
                    {
                        var dumb = g.SetObjVar("DumbTime", Fv(g, round, "DumbTime") + dt, round, typeof(float));
                        g.If(g.Greater(dumb, 0.1f), () => g.SetObjVar("Lost", true, round));
                    }), () =>
                    {
                        var want = g.Get(V3T, "normalized", g.Scale(g.Get(V3T, "normalized", aim), Fv(g, round, "Extension")) - g.Get(V3T, "normalized", vel));
                        var turned = g.Call(V3T, "RotateTowards", new[] { V3T, V3T, typeof(float), typeof(float) }, null, nose, want,
                            Fv(g, round, "RotSpeed") * Mathf.Deg2Rad * dt, 0f);
                        g.Set(TF, "rotation", body, g.Call(typeof(Quaternion), "LookRotation", new[] { V3T }, null, turned));
                    });
                    g.SetObjVar("LastDist", distance, round);
                    g.SetObjVar("LastTargetPos", goal, round);
                });
                // a bomb points along its fall
                g.If(g.And(g.Same(kind, 3f), g.Greater(g.Magnitude(vel), 1f)), () =>
                    g.Set(TF, "rotation", body, g.Call(typeof(Quaternion), "LookRotation", new[] { V3T }, null, vel)));

                var next = g.Keep(pos + g.Scale(vel, dt));
                g.SetObjVar("Velocity", vel, round);
                // ---- what it meets: its target (how close this frame's path passes), else anything solid, else old age
                var step = g.Keep(next - pos);
                var share = g.Clamp01(g.Dot(goal - pos, step) / g.Max(g.Dot(step, step), 0.0001f));
                var closest = g.Keep(pos + g.Scale(step, share));
                var miss = g.Keep(g.Distance(closest, goal));
                var reach = g.Keep(g.ObjVarOf(target, "HitRadius", typeof(float)) + 2f);
                var lost = g.Keep(Bv(g, round, "Lost"));
                // (a missile that gave up still goes off near its target: its proximity fuse)
                var reached = g.Keep(g.And(onVehicle, g.Or(g.And(g.Not(lost), g.Less(miss, reach)),
                    g.And(g.And(lost, g.Same(kind, 1f)), g.Less(miss, Fv(g, round, "Proximity"))))));
                var armed = g.Greater(g.Distance(pos, V(g, round, "LaunchPos", V3T)), Fv(g, round, "ColliderActive"));
                var cast = g.DoOut(typeof(Physics), "Linecast", new[] { V3T, V3T, typeof(RaycastHit).MakeByRefType(), typeof(int), typeof(QueryTriggerInteraction) },
                    null, pos, next, null, UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                var struck = g.Keep(g.And(armed, g.Result(cast)));
                var ground = g.Keep(g.Get(typeof(RaycastHit), "point", g.OutParam(cast, 2, typeof(RaycastHit))));
                g.If(reached, () => Burst(g, round, kind, closest, true, miss, reach, target, 1f),
                    () => g.If(struck, () => Burst(g, round, kind, ground, false, miss, reach, target, 2f),
                        () => g.If(g.Greater(age, Fv(g, round, "MaxLife")), () => Burst(g, round, kind, pos, false, miss, reach, target, 3f),
                            () => g.Set(TF, "position", body, next))));
            });
        }

        /// <summary>The round goes off at `point` (its animator's "explode", its sound); the firer's client works out the damage.</summary>
        static void Burst(GraphWriter g, Val round, Val kind, Val point, bool onTarget, Val miss, Val reach, Val target, float why)
        {
            var at = g.Keep(point);
            g.SetObjVar("Ended", why, round);           // (for tests: 1 = reached its target, 2 = hit something, 3 = old age)
            g.Set(TF, "position", g.Get(GO, "transform", round), at);
            g.SetObjVar("Exploded", true, round);
            g.SetObjVar("Age", 0f, round);
            g.Do(AN, "SetTrigger", new[] { typeof(string) }, V(g, round, "Animator", AN), "explode");
            g.Do(AS, "Play", Type.EmptyTypes, V(g, round, "Sound", AS));
            Unwarn(g, round);
            g.If(Bv(g, round, "Mine"), () =>
            {
                // missile: its damage is a share of the target's full health, full on it, falling off with distance near it
                if (onTarget)
                    g.If(g.Same(kind, 1f), () =>
                    {
                        var near = g.Select(Bv(g, round, "Lost"), g.Clamp01(1f - g.Max(miss - reach, 0f) / g.Max(Fv(g, round, "Proximity"), 1f)), 1f);
                        SendDamage(g, target, Fv(g, round, "Damage") * g.ObjVarOf(target, "FullHealth", typeof(float)) * near);
                    });
                // guided bomb and bomb: a blast, the same damage to everything within it, falling off to its edge
                g.If(g.Greater(kind, 1.5f), () =>
                {
                    var radius = g.Keep(g.Max(Fv(g, round, "Splash"), 1f));
                    var damage = g.Keep(Fv(g, round, "Damage"));
                    g.ForEach(g.ObjVar("Aircraft", ENUM), GO, other =>
                        g.If(g.And(g.ObjVarOf(other, "Targetable", typeof(bool)), g.Not(g.ObjVarOf(other, "Wrecked", typeof(bool)))), () =>
                        {
                            var gap = g.Keep(g.Max(g.Distance(at, g.Get(TF, "position", g.Get(GO, "transform", other))) - g.ObjVarOf(other, "HitRadius", typeof(float)), 0f));
                            g.If(g.Less(gap, radius), () => SendDamage(g, other, damage * (1f - gap / radius)));
                        }));
                });
            });
        }

        /// <summary>A missile at the vehicle this player flies is no longer coming: its cockpit warning counts one fewer.</summary>
        static void Unwarn(GraphWriter g, Val round)
        {
            g.If(Bv(g, round, "Warned"), () =>
            {
                var target = g.Keep(V(g, round, "Target", GO));
                var incoming = g.SetObjVar("Incoming", g.Max(g.ObjVarOf(target, "Incoming", typeof(float)) - 1f, 0f), target, typeof(float));
                g.Do(AN, "SetInteger", new[] { typeof(string), typeof(int) }, g.ObjVarOf(target, "VehicleAnimator", AN), "missilesincoming",
                    g.Call(typeof(Mathf), "RoundToInt", new[] { typeof(float) }, null, incoming));
                g.SetObjVar("Warned", false, round);
            });
        }

        /// <summary>Back to its vehicle's pool, switched off, ready to be fired again.</summary>
        static void Finish(GraphWriter g, Val round)
        {
            Unwarn(g, round);
            g.Do(GO, "SetActive", new[] { typeof(bool) }, round, false);
            g.Do(TF, "SetParent", new[] { TF, typeof(bool) }, g.Get(GO, "transform", round), V(g, round, "Home", TF), false);
            g.SetObjVar("Alive", false, round);
            g.SetObjVar("Exploded", false, round);
            g.SetObjVar("LiveCount", g.Max(g.P("LiveCount") - 1f, 0f));
        }
    }
}
