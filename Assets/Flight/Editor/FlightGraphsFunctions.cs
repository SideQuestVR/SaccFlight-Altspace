using System;
using Unity.VisualScripting;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The rest of SaccEntity's dial functions, for the advanced controls (FlightGraphsHands.cs picks them and reads
    /// the triggers): engine start and stop, canopy, cruise, altitude hold, arrestor hook, display smoke, flares, gun,
    /// heat-seeking missiles (AAM), guided bombs (AGM) and bombs. Ported from the Sacc scripts of the same names; the
    /// numbers are each vehicle's own, read by SaccFleetWeapons.cs. Differences, where this platform needs them:
    ///  - Hits are worked out from where vehicles are, not from colliders (another player's vehicle has none on this
    ///    client): the gun along its line ten times a second, a missile by how close its path passes its target, a
    ///    bomb's or guided bomb's blast by distance. Damage is decided by the client that fired, and applied by the
    ///    client responsible for the vehicle hit (its pilot or its owner), as Sacc does.
    ///  - Rounds in the air are flown by one graph for the whole world (FlightGraphsWeapons.cs) on every client, from
    ///    the launch message; only the firer's copy does damage.
    ///  - The guided bomb is aimed where the pilot looks (Sacc aims a camera shown on a cockpit screen with the hand).
    ///  - A vehicle hit until its health runs out explodes (Sacc first wrecks it, spinning, until it hits something).
    /// Everything else goes to everyone in one-shots: "{BoomMessage}|{kind}|{arguments}".
    /// </summary>
    public static partial class FlightGraphs
    {
        static readonly Type GO = typeof(GameObject), PS = typeof(ParticleSystem), AS = typeof(AudioSource), TXT = typeof(UnityEngine.UI.Text);
        static readonly Type LIST = typeof(System.Collections.IList), ENUM = typeof(System.Collections.IEnumerable);

        static void DeclareFunctions(GraphWriter g)
        {
            foreach (var (name, value) in new (string, object)[]
                     {
                         ("EngineOff", false), ("EngineStarting", 0f), ("EngineToggleTime", -100f), ("EngineTapTime", -100f),
                         ("CanopyOpen", false), ("CanopyBroken", false), ("CanopyToggleTime", -100f),
                         ("CruiseOn", false), ("CruiseSpeed", 0f), ("CruiseInt", 0f), ("CruiseLastErr", 0f), ("CruiseTapTime", -100f),
                         ("CruiseHeld", false), ("CruiseZero", 0f), ("CruiseTemp", 0f),
                         ("AltHoldOn", false), ("AHInt", 0f), ("AHLastErr", 0f), ("AHLastRoll", 0f), ("AHLastYaw", 0f), ("AHCollInt", 0f), ("AHCollLast", 0f),
                         ("AHOutPitch", 0f), ("AHOutYaw", 0f), ("AHOutRoll", 0f),
                         ("HookDown", false), ("Hooked", false), ("HookedAt", Vector3.zero), ("HookedTime", -100f),
                         ("SmokeOn", false), ("SmokeColor", Vector3.one), ("SmokePressTime", -100f), ("SmokePicking", false),
                         ("SmokeZero", Vector3.zero), ("SmokeTemp", Vector3.one), ("SmokeSendTime", 0f), ("SmokeSent", Vector3.one),
                         ("GunFiring", false), ("GunAmmo", 0f), ("GunScan", 0f), ("GunQueue", 0f), ("GunSendTimer", 0f), ("HasGunTarget", false),
                         ("GunBestAlong", 0f), ("GunBestFound", false),
                         ("AAMCount", 0f), ("AGMCount", 0f), ("BombCount", 0f), ("FlareCount", 0f),
                         ("AAMHasTarget", false), ("AAMLockTimer", 0f), ("AAMLocked", false), ("AAMScan", 0f), ("AAMAlarm", 0f),
                         ("AAMBestAngle", 0f), ("AAMBestFound", false),
                         ("AGMLocked", false), ("AGMPoint", Vector3.zero), ("AGMTargetIsVehicle", false), ("AGMOffset", Vector3.zero),
                         ("AGMPressTime", -100f), ("AGMUnlockAt", -1f), ("AGMAim", Vector3.zero),
                         ("BombNext", 0f), ("BombPoint", 0f), ("FlareNext", 0f),
                         ("PoolIndex", 0f), ("PickedIndex", -1f), ("Picked", false), ("NearFound", false), ("NearDistance", 0f),
                     })
                g.Declare(name, value);
            // (object references start as this vehicle: "none" is said by the flag beside each one)
            foreach (var name in new[] { "GunTarget", "GunBest", "AAMTarget", "AAMBest", "AGMTarget", "PickedRound", "NearVehicle" })
                g.Declare(name, null);
        }

        static Val Self(GraphWriter g) => g.Get(TF, "gameObject");
        static Val Now(GraphWriter g) => g.Get(typeof(Time), "time");
        static Val VtolActiveNow(GraphWriter g) =>
            g.And(g.ObjVar("VTOLenabled", typeof(bool)), g.Or(g.ObjVar("VTOLOnly", typeof(bool)), g.Greater(g.F("VTOLAngle"), 0f)));

        /// <summary>Tell everyone else about this vehicle: "{BoomMessage}|{kind}|{argument}".</summary>
        static void Say(GraphWriter g, string kind, object argument)
        {
            var head = g.Concat(g.ObjVar("BoomMessage", typeof(string)), "|" + kind + "|");
            g.PlatformDo("SendOneShot", ("Data", g.Concat(head, argument)), ("All Instances", false));
        }

        static void AnimBoolNamed(GraphWriter g, Val name, object value) =>
            g.Do(AN, "SetBool", new[] { typeof(string), typeof(bool) }, Anim(g), name, value);
        static void AnimFloatNamed(GraphWriter g, Val name, object value) =>
            g.Do(AN, "SetFloat", new[] { typeof(string), typeof(float) }, Anim(g), name, value);
        static void AnimTriggerNamed(GraphWriter g, Val name) =>
            g.Do(AN, "SetTrigger", new[] { typeof(string) }, Anim(g), name);
        static void Play(GraphWriter g, string source) => g.Do(AS, "Play", Type.EmptyTypes, g.ObjVar(source, AS));
        static void Stop(GraphWriter g, string source) => g.Do(AS, "Stop", Type.EmptyTypes, g.ObjVar(source, AS));
        static void ShowAll(GraphWriter g, string list, Val on) =>
            g.ForEach(g.ObjVar(list, ENUM), GO, item => g.Do(GO, "SetActive", new[] { typeof(bool) }, item, on));

        /// <summary>
        /// Damage to `target`, decided here: given to it on this client at once (if this client is responsible for it,
        /// it takes it), and to everyone else in a one-shot (where its pilot or owner takes it).
        /// </summary>
        internal static void SendDamage(GraphWriter g, Val target, Val amount)
        {
            var hurt = g.Keep(amount);
            g.SetObjVar("IncomingDamage", g.ObjVarOf(target, "IncomingDamage", typeof(float)) + hurt, target);
            Trigger(g, target, "fw_hit");
            g.PlatformDo("SendOneShot", ("Data", g.Concat(g.ObjVarOf(target, "BoomMessage", typeof(string)), "|hit|", g.NumberText(hurt))),
                ("All Instances", false));
        }

        /// <summary>This vehicle is hit (SaccAirVehicle's SFEXT_G_BulletHit): everyone sees it, the responsible client counts it.</summary>
        static void TakeDamage(GraphWriter g, Val amount)
        {
            var hurt = g.Keep(amount);
            g.If(g.Not(g.B("Dead")), () =>
            {
                Wake(g);
                AnimTrigger(g, "bullethit");
                g.If(g.Or(g.B("Piloting"), g.B("Riding")), () => Play(g, "BulletHitSound"));
                g.If(Responsible(g), () =>
                {
                    var health = g.SetGraphVar("Health", g.F("Health") - hurt);
                    g.If(g.Less(health, 0.01f), () => Explode(g));
                });
            });
        }

        /// <summary>Ammunition and switches as a vehicle starts: full, everything off, gear and canopy as parked. Every client.</summary>
        static void FunctionsHome(GraphWriter g)
        {
            g.SetGraphVar("AAMCount", g.P("AAMFull"));
            g.SetGraphVar("AGMCount", g.P("AGMFull"));
            g.SetGraphVar("BombCount", g.P("BombFull"));
            g.SetGraphVar("FlareCount", g.P("FlaresFull"));
            g.SetGraphVar("GunAmmo", g.P("GunAmmoFull"));
            foreach (var (count, name) in new[] { ("AAMFull", "AAMFloat"), ("AGMFull", "AGMFloat"), ("BombFull", "BombFloat") })
                g.If(g.Greater(g.P(count), 0f), () => AnimFloatNamed(g, g.ObjVar(name, typeof(string)), 1f));
            g.SetGraphVar("EngineOff", false);
            g.SetGraphVar("EngineStarting", 0f);
            g.SetGraphVar("CanopyOpen", false);
            g.SetGraphVar("CanopyBroken", false);
            AnimBool(g, "canopybroken", false);
            g.SetGraphVar("HookDown", false);
            g.SetGraphVar("Hooked", false);
            AnimBool(g, "hookdown", false);
            g.SetGraphVar("CruiseOn", false);
            g.SetGraphVar("AltHoldOn", false);
            Smoke(g, false);
            g.SetGraphVar("GunFiring", false);
            AnimBoolNamed(g, g.ObjVar("GunBool", typeof(string)), false);
            g.SetGraphVar("AGMLocked", false);
            g.SetObjVar("Wrecked", false);
        }

        /// <summary>The pilot lets go of everything held (out of the cockpit, or the advanced controls switched off).</summary>
        static void FunctionsRelease(GraphWriter g)
        {
            g.If(g.B("GunFiring"), () =>
            {
                g.SetGraphVar("GunFiring", false);
                AnimBoolNamed(g, g.ObjVar("GunBool", typeof(string)), false);
                Say(g, "gun", "0");
            });
            g.If(g.B("SmokeOn"), () =>
            {
                Smoke(g, false);
                Say(g, "smoke", "0|100|100|100");
            });
            g.SetGraphVar("SmokePicking", false);
            g.SetGraphVar("CruiseHeld", false);
            AAMClear(g);
            g.SetGraphVar("AGMLocked", false);
            g.SetGraphVar("AGMUnlockAt", -1f);
            Markers(g, false, false);
        }

        // ---- engine (DFUNC_ToggleEngine) -------------------------------------------------------------------------
        static void EngineSwitch(GraphWriter g, bool on)
        {
            g.SetGraphVar("EngineOff", !on);
            g.SetGraphVar("EngineStarting", 0f);
            AnimBool(g, "EngineOn", on);
            Say(g, "engine", on ? "1" : "0");
            if (!on)
            {
                Play(g, "EngineOffSound");
                g.SetGraphVar("CruiseOn", false);
                g.SetGraphVar("AltHoldOn", false);
            }
        }

        /// <summary>A pull: an engine that is off starts (it takes StartUpTime); one that is starting stops starting; a running one needs a double pull to stop.</summary>
        static void EnginePress(GraphWriter g)
        {
            var now = g.Keep(Now(g));
            g.If(g.Greater(g.F("EngineStarting"), 0f), () =>
            {
                g.SetGraphVar("EngineStarting", 0f);
                Stop(g, "EngineStartSound");
            }, () => g.If(g.B("EngineOff"), () =>
                g.If(g.Greater(now - g.F("EngineToggleTime"), g.P("EngineToggleDelay")), () =>
                {
                    g.SetGraphVar("EngineStarting", g.Max(g.P("EngineStartTime"), 0.01f));
                    g.SetGraphVar("EngineToggleTime", now);
                    Play(g, "EngineStartSound");
                }), () =>
            {
                g.If(g.Less(now - g.F("EngineTapTime"), 0.4f), () =>
                {
                    EngineSwitch(g, false);
                    g.SetGraphVar("EngineToggleTime", now - g.P("EngineStartTime"));
                });
                g.SetGraphVar("EngineTapTime", now);
            }));
        }

        // ---- canopy (DFUNC_Canopy) -------------------------------------------------------------------------------
        static void CanopySet(GraphWriter g, Val open)
        {
            var opened = g.SetGraphVar("CanopyOpen", open, typeof(bool));
            g.SetGraphVar("CanopyToggleTime", Now(g));
            Say(g, "canopy", g.Select(opened, "1", "0", typeof(string)));
        }

        static void CanopyPress(GraphWriter g)
        {
            var settled = g.Greater(Now(g) - g.F("CanopyToggleTime"), g.P("CanopyCloseTime") + 0.1f);
            var tooFast = g.And(g.Not(g.ObjVar("CanopyCanBreak", typeof(bool))), g.Greater(g.F("Speed"), g.P("CanopyAutoCloseSpeed")));
            g.If(g.And(g.And(settled, g.Not(g.B("CanopyBroken"))), g.Not(tooFast)), () => CanopySet(g, g.Not(g.B("CanopyOpen"))));
        }

        /// <summary>Four times a second while flying: an open canopy breaks off above its speed, or shuts itself above another.</summary>
        static void CanopyWatch(GraphWriter g)
        {
            g.If(g.And(g.B("CanopyOpen"), g.Not(g.B("CanopyBroken"))), () =>
                g.If(g.And(g.ObjVar("CanopyCanBreak", typeof(bool)), g.Greater(g.F("Speed"), g.P("CanopyBreakSpeed"))), () =>
                {
                    g.SetGraphVar("CanopyBroken", true);
                    AnimBool(g, "canopybroken", true);
                    Say(g, "canopy", "2");
                }, () => g.If(g.And(g.Greater(g.F("Speed"), g.P("CanopyAutoCloseSpeed")), g.Greater(Now(g) - g.F("CanopyToggleTime"), 1.9f)),
                    () => CanopySet(g, g.Same(1, 2)))));
        }

        // ---- cruise (DFUNC_Cruise) -------------------------------------------------------------------------------
        static void CruisePress(GraphWriter g)
        {
            var now = g.Keep(Now(g));
            g.If(g.And(g.B("CruiseOn"), g.Less(now - g.F("CruiseTapTime"), 0.4f)), () => g.SetGraphVar("CruiseOn", false), () =>
                g.If(g.And(g.Not(g.B("CruiseOn")), g.And(g.And(g.Not(g.B("Taxiing")), g.Not(g.B("EngineOff"))), g.Not(VtolActiveNow(g)))), () =>
                {
                    g.SetGraphVar("CruiseOn", true);
                    g.SetGraphVar("CruiseSpeed", g.F("Speed"));
                    g.SetGraphVar("CruiseInt", 0f);
                    g.SetGraphVar("CruiseLastErr", 0f);
                }));
            g.SetGraphVar("CruiseTapTime", now);
        }

        /// <summary>The throttle as cruise sets it this step (its PID on speed), or as it was.</summary>
        static Val CruiseThrottle(GraphWriter g, Val throttle, Val speed, Val dt)
        {
            var active = g.Keep(g.And(g.B("CruiseOn"), g.Not(g.B("ThrottleHeld"))));
            g.If(active, () =>
            {
                var error = g.Keep(g.F("CruiseSpeed") - speed);
                var integral = g.SetGraphVar("CruiseInt", g.Clamp(g.F("CruiseInt") + error * dt, g.P("CruiseIMin"), g.P("CruiseIMax")));
                var derivative = g.Clamp((error - g.F("CruiseLastErr")) / dt, g.P("CruiseDMin"), g.P("CruiseDMax"));
                g.SetGraphVar("CruiseLastErr", error);
                g.SetGraphVar("Throttle", g.Clamp01(g.P("CruiseP") * error + g.P("CruiseI") * integral + g.P("CruiseD") * derivative));
            });
            // it lets go on the ground, with the engine off, or in a hover
            g.If(g.And(g.B("CruiseOn"), g.Or(g.Or(g.B("Taxiing"), g.B("EngineOff")), VtolActiveNow(g))), () => g.SetGraphVar("CruiseOn", false));
            return g.Select(active, g.F("Throttle"), throttle);
        }

        // ---- altitude hold (DFUNC_AltHold) -----------------------------------------------------------------------
        static void AltHoldPress(GraphWriter g)
        {
            var heli = g.ObjVar("AHHeli", typeof(bool));
            g.If(g.B("AltHoldOn"), () => g.SetGraphVar("AltHoldOn", false), () =>
                g.If(g.Not(g.Or(g.Or(g.And(VtolActiveNow(g), g.Not(heli)), g.B("Taxiing")), g.B("EngineOff"))), () =>
                {
                    g.SetGraphVar("AltHoldOn", true);
                    foreach (var name in new[] { "AHInt", "AHLastErr", "AHLastRoll", "AHLastYaw", "AHCollInt", "AHCollLast" }) g.SetGraphVar(name, 0f);
                }));
        }

        /// <summary>
        /// SaccAirVehicle's stick override from DFUNC_AltHold, every step it is on and the stick is not held: pitch
        /// holds the climb at nothing (a helicopter: the nose level, leaning against its drift), roll levels the wings,
        /// yaw stops any turn. A helicopter's collective also holds its height, unless the pilot has the lever.
        /// Leaves AHOutPitch / AHOutYaw / AHOutRoll for the stick to take.
        /// </summary>
        static void AltHoldStep(GraphWriter g, Val dt, Val vel, Val forward, Val up, Val right)
        {
            g.If(g.And(g.B("AltHoldOn"), g.Or(g.Or(g.B("Taxiing"), g.B("EngineOff")), g.And(VtolActiveNow(g), g.Not(g.ObjVar("AHHeli", typeof(bool)))))),
                () => g.SetGraphVar("AltHoldOn", false));
            g.If(g.And(g.B("AltHoldOn"), g.Not(g.B("StickHeld"))), () =>
            {
                var heli = g.Keep(g.ObjVar("AHHeli", typeof(bool)));
                var spin = g.Keep(g.Call(TF, "InverseTransformDirection", Vec, null, g.Get(RB, "angularVelocity")));
                var local = g.Keep(g.Call(TF, "InverseTransformDirection", Vec, null, vel));
                var upright = g.Select(g.Greater(g.Dot(g.V3(0f, 1f, 0f), up), 0f), 1f, -1f);
                var drift = g.Clamp(g.Clamp(g.Z(local) * g.P("AHHoverPitch"), -g.P("AHHoverSpeedPitch"), g.P("AHHoverSpeedPitch")), -g.P("AHHoverMaxPitch"), g.P("AHHoverMaxPitch"));
                var noseUp = g.Call(V3T, "SignedAngle", new[] { V3T, V3T, V3T }, null, forward,
                    g.Call(V3T, "ProjectOnPlane", new[] { V3T, V3T }, null, forward, g.V3(0f, 1f, 0f)), right);
                var climb = g.Y(g.Get(V3T, "normalized", vel)) - g.X(spin) * upright * 2.5f;
                var error = g.Keep(g.Select(heli, noseUp - drift, climb));
                var integral = g.SetGraphVar("AHInt", g.F("AHInt") + error * dt);
                var derivative = (error - g.F("AHLastErr")) / dt;
                g.SetGraphVar("AHLastErr", error);
                var pitch = g.Keep(g.Clamp(g.P("AHPitchP") * error + g.P("AHPitchI") * integral + g.P("AHPitchD") * derivative, -1f, 1f));
                // roll: level the wings (a helicopter leans against its sideways drift); upside down, level it upside down
                var bank = g.Keep(g.Call(typeof(Mathf), "DeltaAngle", new[] { typeof(float), typeof(float) }, null, 0f, g.Z(g.Get(TF, "eulerAngles"))));
                var lean = g.Clamp(g.Clamp(g.X(local), -g.P("AHHoverSpeedRoll"), g.P("AHHoverSpeedRoll")) * g.P("AHHoverRoll"), -g.P("AHHoverMaxRoll"), g.P("AHHoverMaxRoll"));
                var over = g.Keep(g.And(g.Not(heli), g.Greater(g.Abs(bank), 90f)));
                var levelled = g.Select(heli, bank - lean, g.Select(over, g.Select(g.Greater(bank, 0f), bank - 180f, bank + 180f), bank));
                var rollError = g.Keep(-levelled);
                var rollRate = (rollError - g.F("AHLastRoll")) / dt;
                g.SetGraphVar("AHLastRoll", rollError);
                g.SetGraphVar("AHOutRoll", g.Clamp(g.P("AHRollP") * rollError + rollRate * g.P("AHRollD"), -1f, 1f));
                var yawError = g.Keep(-g.Y(spin));
                var yawRate = (yawError - g.F("AHLastYaw")) / dt;
                g.SetGraphVar("AHLastYaw", yawError);
                g.SetGraphVar("AHOutYaw", g.Clamp(g.P("AHYawP") * yawError + yawRate * g.P("AHYawD"), -1f, 1f));
                // (the old flight limits: no more pull than the G and angle of attack it allows)
                var limit = g.Min(g.Clamp01(1f - g.F("VertGs") / g.P("AHGLimit")), g.Clamp01(1f - g.Abs(g.F("AoA")) / g.P("AHAoALimit")));
                var flipped = g.Select(over, -pitch, pitch);
                g.SetGraphVar("AHOutPitch", g.Select(heli, pitch, flipped * limit));
                g.If(g.And(heli, g.Not(g.B("ThrottleHeld"))), () =>
                {
                    var sink = g.Keep(-g.Y(vel));
                    var collective = g.SetGraphVar("AHCollInt", g.Clamp(g.F("AHCollInt") + sink * dt, g.P("AHCollIMin"), g.P("AHCollIMax")));
                    var change = (sink - g.F("AHCollLast")) / dt;
                    g.SetGraphVar("AHCollLast", sink);
                    g.SetGraphVar("Throttle", g.Clamp01(g.P("AHCollP") * sink + g.P("AHCollI") * collective + g.P("AHCollD") * change));
                });
            });
        }

        // ---- hook (DFUNC_Hook) -----------------------------------------------------------------------------------
        static void HookPress(GraphWriter g)
        {
            var down = g.SetGraphVar("HookDown", g.Not(g.B("HookDown")), typeof(bool));
            AnimBool(g, "hookdown", down);
            g.If(g.Not(down), () => g.SetGraphVar("Hooked", false));
            Say(g, "hook", g.Select(down, "1", "0", typeof(string)));
        }

        /// <summary>Every step with the hook down: catch a cable 2 m under the hook; caught on the ground, stop at HookedBrakeStrength; pulled 120 m on, it snaps.</summary>
        static void HookStep(GraphWriter g, Val dt, Val vel, Val speed)
        {
            g.If(g.B("HookDown"), () => g.If(g.Not(g.B("Hooked")), () =>
            {
                var cast = g.DoOut(typeof(Physics), "Raycast",
                    new[] { V3T, V3T, typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int), typeof(QueryTriggerInteraction) }, null,
                    g.Get(TF, "position", g.ObjVar("HookDetector", TF)), g.V3(0f, -1f, 0f), null, 2f, UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
                g.If(g.Result(cast), () =>
                {
                    var thing = g.Keep(g.Get(typeof(Component), "gameObject", g.Get(typeof(RaycastHit), "collider", g.OutParam(cast, 2, typeof(RaycastHit)))));
                    g.If(g.IsDefined(thing, "IsCable"), () =>
                    {
                        g.SetGraphVar("Hooked", true);
                        g.SetGraphVar("HookedAt", g.Get(TF, "position"));
                        g.SetGraphVar("HookedTime", Now(g));
                        AnimTrigger(g, "hooked");
                        Say(g, "hooked", "1");
                    });
                });
            }, () =>
            {
                g.If(g.B("Taxiing"), () => g.Set(RB, "linearVelocity", null,
                    g.Select(g.Greater(speed, g.P("HookBrake") * dt), vel - g.Scale(g.Get(V3T, "normalized", vel), g.P("HookBrake") * dt), Vector3.zero, V3T)));
                g.If(g.Greater(g.Distance(g.Get(TF, "position"), g.GraphVar("HookedAt", V3T)), g.P("HookSnap")), () =>
                {
                    // caught far too fast: the cable takes most of the airframe with it
                    var soon = g.Keep(Now(g) - g.F("HookedTime"));
                    g.If(g.Less(soon, 2f), () =>
                    {
                        var health = g.SetGraphVar("Health", g.F("Health") - g.P("FullHealth") * (2f - soon) * 0.5f);
                        g.If(g.Less(health, 0.01f), () => Explode(g));
                    });
                    g.SetGraphVar("Hooked", false);
                    Play(g, "CableSnapSound");
                });
            }));
        }

        // ---- smoke (DFUNC_Smoke) ---------------------------------------------------------------------------------
        static void Smoke(GraphWriter g, object on)
        {
            var smoking = g.SetGraphVar("SmokeOn", on, typeof(bool));
            g.ForEach(g.ObjVar("SmokeSystems", ENUM), PS, system =>
                g.Set(typeof(ParticleSystem.EmissionModule), "enabled", g.Get(PS, "emission", system), smoking));
            g.Do(GO, "SetActive", new[] { typeof(bool) }, g.ObjVar("SmokeIndicator", GO), smoking);
        }

        static void SmokeColour(GraphWriter g, Val rgb)
        {
            var colour = g.Keep(rgb);
            var bright = g.Call(typeof(Color), ".ctor", new[] { typeof(float), typeof(float), typeof(float) }, null, g.X(colour), g.Y(colour), g.Z(colour));
            var dim = g.Call(typeof(Color), ".ctor", new[] { typeof(float), typeof(float), typeof(float) }, null, g.X(colour) * 0.8f, g.Y(colour) * 0.8f, g.Z(colour) * 0.8f);
            var gradient = g.Keep(g.Call(typeof(ParticleSystem.MinMaxGradient), ".ctor", new[] { typeof(Color), typeof(Color) }, null, bright, dim));
            g.ForEach(g.ObjVar("SmokeSystems", ENUM), PS, system =>
                g.Set(typeof(ParticleSystem.MainModule), "startColor", g.Get(PS, "main", system), gradient));
        }

        static Val SmokeMessage(GraphWriter g, Val on) =>
            g.Concat(g.Concat(g.Select(on, "1|", "0|", typeof(string)), g.NumberText(g.X(g.GraphVar("SmokeColor", V3T))), "|"),
                g.Concat(g.NumberText(g.Y(g.GraphVar("SmokeColor", V3T))), "|", g.NumberText(g.Z(g.GraphVar("SmokeColor", V3T)))));

        static void SmokePress(GraphWriter g)
        {
            var on = g.Keep(g.Not(g.B("SmokeOn")));
            Smoke(g, on);
            SmokeColour(g, g.GraphVar("SmokeColor", V3T));
            g.SetGraphVar("SmokePressTime", Now(g));
            g.SetGraphVar("SmokeSent", g.GraphVar("SmokeColor", V3T));
            Say(g, "smoke", SmokeMessage(g, on));
        }

        // ---- flares (DFUNC_Flares): what every client shows, and what decoys missiles ------------------------------
        static void FlareShow(GraphWriter g)
        {
            var now = g.Keep(Now(g));
            g.ForEach(g.ObjVar("FlareSystems", ENUM), PS, system => g.Do(PS, "Emit", new[] { typeof(int) }, system, g.ObjVar("FlareEmit", typeof(int))));
            Play(g, "FlareSound");
            g.SetObjVar("FlareBurst", g.Select(g.Less(now - g.P("FlareTime"), g.P("FlareActiveTime")), g.P("FlareBurst") + 1f, 1f));
            g.SetObjVar("FlareTime", now);
        }

        // ---- launchers ---------------------------------------------------------------------------------------------
        /// <summary>Find the first round in `pool` that is not out (PickedRound / PickedIndex; Picked says whether one was found).</summary>
        static void PickRound(GraphWriter g, string pool)
        {
            g.SetGraphVar("Picked", false);
            g.SetGraphVar("PoolIndex", 0f);
            g.ForEach(g.ObjVar(pool, ENUM), GO, round =>
            {
                g.If(g.And(g.Not(g.B("Picked")), g.Not(g.ObjVarOf(round, "Alive", typeof(bool)))), () =>
                {
                    g.SetGraphVar("Picked", true);
                    g.SetGraphVar("PickedRound", round);
                    g.SetGraphVar("PickedIndex", g.F("PoolIndex"));
                });
                g.SetGraphVar("PoolIndex", g.F("PoolIndex") + 1f);
            });
        }

        /// <summary>
        /// Send a round on its way (every client, from the firer's message): out of its holder into the world at the
        /// launch point, with the vehicle's own velocity, onto the weapons graph's list.
        /// </summary>
        static void Launch(GraphWriter g, Val round, Val from, Val target, Val hasTarget, Val targetIsVehicle, Val targetPoint, Val targetOffset, bool mine)
        {
            var shot = g.Keep(round);
            var point = g.Keep(from);
            var world = g.Keep(g.ObjVar("WeaponsWorld", GO));
            // (another client's copy of this vehicle is moved, not simulated: its velocity is the measured speed along its nose)
            var velocity = g.Select(g.Get(RB, "isKinematic"), g.Scale(g.Get(TF, "forward"), g.F("MovedSpeed")), g.Get(RB, "linearVelocity"), V3T);
            var start = g.Keep(g.Get(TF, "position", point));
            foreach (var (name, value) in new (string, object)[]
                     {
                         ("Alive", true), ("Exploded", false), ("Age", 0f), ("Lost", false), ("DumbTime", 0f), ("Mine", mine),
                         ("Shooter", Self(g)), ("LaunchPos", start), ("Velocity", velocity), ("HasTarget", hasTarget), ("Target", target),
                         ("TargetIsVehicle", targetIsVehicle), ("TargetPoint", targetPoint), ("TargetOffset", targetOffset),
                         ("LastTargetPos", g.Select(targetIsVehicle, g.Get(TF, "position", g.Get(GO, "transform", target)), targetPoint, V3T)),
                     })
                g.SetObjVar(name, value, shot);
            g.SetObjVar("LastDist", g.Distance(start, g.ObjVarOf(shot, "LastTargetPos", V3T)), shot);
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
            // a missile at the vehicle this player flies: the cockpit's warning (SAV_EffectsController's "missilesincoming")
            var watch = g.ObjVar("Watch", GO);
            g.If(g.And(g.And(hasTarget, targetIsVehicle), g.And(g.ObjVarOf(watch, "HasPilotVehicle", typeof(bool)),
                g.Same(g.ObjVarOf(watch, "PilotVehicle", GO), target))), () =>
            {
                var incoming = g.SetObjVar("Incoming", g.ObjVarOf(target, "Incoming", typeof(float)) + 1f, target, typeof(float));
                g.Do(AN, "SetInteger", new[] { typeof(string), typeof(int) }, g.ObjVarOf(target, "VehicleAnimator", AN), "missilesincoming",
                    g.Call(typeof(Mathf), "RoundToInt", new[] { typeof(float) }, null, incoming));
                g.SetObjVar("Warned", true, shot);
            });
        }

        static Val Aircraft(GraphWriter g) => g.ObjVarOf(g.ObjVar("WeaponsWorld", GO), "Aircraft", LIST);
        static Val IndexOf(GraphWriter g, Val text) => g.Call(typeof(Mathf), "FloorToInt", new[] { typeof(float) }, null, g.ParseNumber(text) + 0.5f);

        /// <summary>Unfired rounds of each kind shown on the pylons (the animator's "AAMs" etc. count down from 1).</summary>
        static void Fired(GraphWriter g, string count, string full, string floatName, string triggerName)
        {
            var left = g.SetGraphVar(count, g.Max(g.F(count) - 1f, 0f));
            AnimFloatNamed(g, g.ObjVar(floatName, typeof(string)), left / g.Max(g.P(full), 1f));
            g.If(g.Greater(g.Get(typeof(string), "Length", g.ObjVar(triggerName, typeof(string))), 0.5f), () => AnimTriggerNamed(g, g.ObjVar(triggerName, typeof(string))));
        }

        // ---- heat-seeking missiles (DFUNC_AAM) -------------------------------------------------------------------
        static void AAMClear(GraphWriter g)
        {
            g.If(g.B("AAMHasTarget"), () =>
            {
                Stop(g, "AAMTargetingSound");
                Stop(g, "AAMLockSound");
            });
            g.SetGraphVar("AAMHasTarget", false);
            g.SetGraphVar("AAMLocked", false);
            g.SetGraphVar("AAMLockTimer", 0f);
            g.Do(GO, "SetActive", new[] { typeof(bool) }, g.Get(TF, "gameObject", g.ObjVar("AAMIndicator", TF)), false);
        }

        /// <summary>
        /// Ten times a second with missiles picked: the crewed aircraft nearest the nose inside the lock cone (and in
        /// range and in sight) is the target; held there for the lock time it is locked (a tone), and its pilot is
        /// warned once a second. Every frame the head-up display's box sits over it.
        /// </summary>
        static void AAMScan(GraphWriter g, Val dt)
        {
            var scan = g.SetGraphVar("AAMScan", g.F("AAMScan") + dt);
            g.If(g.Greater(scan, 0.1f), () =>
            {
                g.SetGraphVar("AAMScan", 0f);
                var me = g.Keep(Self(g));
                var here = g.Keep(g.Get(TF, "position"));
                var nose = g.Keep(g.Get(TF, "forward"));
                g.SetGraphVar("AAMBestFound", false);
                g.SetGraphVar("AAMBestAngle", g.P("AAMLockAngle"));
                g.ForEach(Aircraft(g), GO, other =>
                    g.If(g.And(g.Not(g.Same(other, me)), g.And(g.ObjVarOf(other, "Targetable", typeof(bool)),
                        g.And(g.ObjVarOf(other, "Crewed", typeof(bool)), g.Not(g.ObjVarOf(other, "Wrecked", typeof(bool)))))), () =>
                    {
                        var to = g.Keep(g.Get(TF, "position", g.Get(GO, "transform", other)) - here);
                        var angle = g.Keep(g.Angle(nose, to));
                        g.If(g.And(g.Less(angle, g.F("AAMBestAngle")), g.Less(g.Magnitude(to), g.P("AAMRange"))), () =>
                        {
                            g.SetGraphVar("AAMBestFound", true);
                            g.SetGraphVar("AAMBestAngle", angle);
                            g.SetGraphVar("AAMBest", other);
                        });
                    }));
                // in sight: nothing solid between (the city's buildings, the ground), short of the target's own size
                g.If(g.B("AAMBestFound"), () =>
                {
                    var best = g.Keep(g.GraphVar("AAMBest", GO));
                    var start = g.Keep(here + g.Scale(nose, g.P("HitRadius") + 2f));
                    var toward = g.Keep(g.Get(TF, "position", g.Get(GO, "transform", best)) - start);
                    var cast = g.DoOut(typeof(Physics), "Raycast",
                        new[] { V3T, V3T, typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int), typeof(QueryTriggerInteraction) }, null,
                        start, toward, null, g.Magnitude(toward) - g.ObjVarOf(best, "HitRadius", typeof(float)), UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    g.If(g.Result(cast), () => g.SetGraphVar("AAMBestFound", false));
                });
                // a new target starts the lock again
                g.If(g.B("AAMBestFound"), () =>
                {
                    g.If(g.Or(g.Not(g.B("AAMHasTarget")), g.Not(g.Same(g.GraphVar("AAMBest", GO), g.GraphVar("AAMTarget", GO)))), () =>
                    {
                        g.SetGraphVar("AAMTarget", g.GraphVar("AAMBest", GO));
                        g.SetGraphVar("AAMLockTimer", 0f);
                        g.SetGraphVar("AAMLocked", false);
                        g.SetGraphVar("AAMHasTarget", true);
                        Stop(g, "AAMLockSound");
                        Play(g, "AAMTargetingSound");
                        g.Do(GO, "SetActive", new[] { typeof(bool) }, g.Get(TF, "gameObject", g.ObjVar("AAMIndicator", TF)), true);
                    });
                    var timer = g.SetGraphVar("AAMLockTimer", g.F("AAMLockTimer") + 0.1f);
                    g.If(g.And(g.Not(g.B("AAMLocked")), g.Greater(timer, g.P("AAMLockTime"))), () =>
                    {
                        g.SetGraphVar("AAMLocked", true);
                        Stop(g, "AAMTargetingSound");
                        Play(g, "AAMLockSound");
                    });
                    var alarm = g.SetGraphVar("AAMAlarm", g.F("AAMAlarm") + 0.1f);
                    g.If(g.Greater(alarm, 1f), () =>
                    {
                        g.SetGraphVar("AAMAlarm", 0f);
                        g.PlatformDo("SendOneShot", ("Data", g.Concat(g.ObjVarOf(g.GraphVar("AAMTarget", GO), "BoomMessage", typeof(string)), "|locked|1")),
                            ("All Instances", false));
                    });
                }, () => AAMClear(g));
            });
            g.If(g.B("AAMHasTarget"), () =>
            {
                var hud = g.Keep(g.Get(TF, "position", g.ObjVar("HudRoot", TF)));
                var toward = g.Keep(g.Get(V3T, "normalized", g.Get(TF, "position", g.Get(GO, "transform", g.GraphVar("AAMTarget", GO))) - hud));
                g.Do(TF, "SetPositionAndRotation", new[] { V3T, typeof(Quaternion) }, g.ObjVar("AAMIndicator", TF),
                    hud + g.Scale(toward, g.P("HudDistance")),
                    g.Call(typeof(Quaternion), "LookRotation", new[] { V3T, V3T }, null, toward, g.Get(TF, "up")));
            });
        }

        static void AAMPress(GraphWriter g)
        {
            g.If(g.And(g.And(g.B("AAMLocked"), g.B("AAMHasTarget")), g.And(g.Greater(g.F("AAMCount"), 0.5f),
                g.Or(g.ObjVar("AAMGrounded", typeof(bool)), g.Not(g.B("Taxiing"))))), () =>
            {
                PickRound(g, "AamPool");
                g.If(g.B("Picked"), () =>
                {
                    var target = g.Keep(g.GraphVar("AAMTarget", GO));
                    Launch(g, g.GraphVar("PickedRound", GO), g.ObjVar("AAMLaunch", TF), target, g.Same(1, 1), g.Same(1, 1), Vector3Zero(g), Vector3Zero(g), true);
                    Say(g, "aam", g.Concat(g.NumberText(g.ObjVarOf(target, "TargetIndex", typeof(float))), "|", g.NumberText(g.F("PickedIndex"))));
                    Fired(g, "AAMCount", "AAMFull", "AAMFloat", "AAMFired");
                });
            });
        }

        static Val Vector3Zero(GraphWriter g) => g.V3(0f, 0f, 0f);

        // ---- guided bombs (DFUNC_AGM), aimed by where the pilot looks ----------------------------------------------
        /// <summary>Where the pilot is looking: the first solid thing along their gaze, or 3 km out.</summary>
        static Val GazePoint(GraphWriter g)
        {
            var head = g.Keep(g.PlatformValue("GetLocalUserState", "Position", V3T));
            var angles = g.Keep(g.Get(typeof(Quaternion), "eulerAngles", g.PlatformValue("GetLocalUserState", "Rotation", typeof(Quaternion))));
            Val Trig(string f, Val degrees) => g.Call(typeof(Mathf), f, new[] { typeof(float) }, null, degrees * Mathf.Deg2Rad);
            var cosPitch = g.Keep(Trig("Cos", g.X(angles)));
            var look = g.Keep(g.V3(Trig("Sin", g.Y(angles)) * cosPitch, -Trig("Sin", g.X(angles)), Trig("Cos", g.Y(angles)) * cosPitch));
            var start = g.Keep(head + g.Scale(look, g.P("HitRadius") * 2f + 2f));
            var cast = g.DoOut(typeof(Physics), "Raycast",
                new[] { V3T, V3T, typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int), typeof(QueryTriggerInteraction) }, null,
                start, look, null, 8000f, UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            return g.SetGraphVar("AGMAim", g.Select(g.Result(cast), g.Get(typeof(RaycastHit), "point", g.OutParam(cast, 2, typeof(RaycastHit))),
                start + g.Scale(look, 3000f), V3T));
        }

        /// <summary>The aim marks the pilot sees (a green ball where they look, red where they have locked), sized for their distance.</summary>
        static void Markers(GraphWriter g, object aiming, object locked)
        {
            var world = g.ObjVar("WeaponsWorld", GO);
            g.Do(GO, "SetActive", new[] { typeof(bool) }, g.ObjVarOf(world, "AimMarker", GO), aiming);
            g.Do(GO, "SetActive", new[] { typeof(bool) }, g.ObjVarOf(world, "LockMarker", GO), locked);
        }

        static void PlaceMarker(GraphWriter g, string marker, Val at)
        {
            var point = g.Keep(at);
            var size = g.Max(g.Distance(point, g.PlatformValue("GetLocalUserState", "Position", V3T)) * 0.012f, 0.3f);
            var t = g.Keep(g.Get(GO, "transform", g.ObjVarOf(g.ObjVar("WeaponsWorld", GO), marker, GO)));
            g.Set(TF, "position", t, point);
            g.Set(TF, "localScale", t, g.V3(size, size, size));
        }

        static Val AGMTargetPoint(GraphWriter g) =>
            g.Select(g.B("AGMTargetIsVehicle"), g.Get(TF, "position", g.Get(GO, "transform", g.GraphVar("AGMTarget", GO))) + g.GraphVar("AGMOffset", V3T),
                g.GraphVar("AGMPoint", V3T), V3T);

        /// <summary>
        /// A pull with the guided bomb picked locks where the pilot looks (on a vehicle within its blast, the vehicle).
        /// Locked, a double pull fires; a single pull lets go of the lock (DFUNC_AGM's 0.4 s wait for a second pull).
        /// </summary>
        static void AGMPress(GraphWriter g)
        {
            var now = g.Keep(Now(g));
            g.If(g.Not(g.B("AGMLocked")), () =>
            {
                var point = g.Keep(g.GraphVar("AGMAim", V3T));
                g.SetGraphVar("AGMLocked", true);
                g.SetGraphVar("AGMPoint", point);
                g.SetGraphVar("AGMUnlockAt", -1f);
                Nearest(g, point, 15f);
                g.SetGraphVar("AGMTargetIsVehicle", g.B("NearFound"));
                g.If(g.B("NearFound"), () =>
                {
                    g.SetGraphVar("AGMTarget", g.GraphVar("NearVehicle", GO));
                    g.SetGraphVar("AGMOffset", point - g.Get(TF, "position", g.Get(GO, "transform", g.GraphVar("NearVehicle", GO))));
                });
                Play(g, "AGMLockSound");
            }, () => g.If(g.Less(now - g.F("AGMPressTime"), 0.4f), () =>
            {
                g.SetGraphVar("AGMUnlockAt", -1f);
                g.If(g.And(g.Greater(g.F("AGMCount"), 0.5f), g.Or(g.ObjVar("AGMGrounded", typeof(bool)), g.Not(g.B("Taxiing")))), () =>
                {
                    PickRound(g, "AgmPool");
                    g.If(g.B("Picked"), () =>
                    {
                        var vehicle = g.Keep(g.B("AGMTargetIsVehicle"));
                        var target = g.Keep(g.Select(vehicle, g.GraphVar("AGMTarget", GO), Self(g), GO));
                        Launch(g, g.GraphVar("PickedRound", GO), g.ObjVar("AGMLaunch", TF), target, g.Same(1, 1), vehicle,
                            g.GraphVar("AGMPoint", V3T), g.GraphVar("AGMOffset", V3T), true);
                        var p = g.Keep(g.GraphVar("AGMPoint", V3T));
                        var o = g.Keep(g.GraphVar("AGMOffset", V3T));
                        var targetIndex = g.Select(vehicle, g.ObjVarOf(target, "TargetIndex", typeof(float)), -1f);
                        Say(g, "agm", g.Concat(
                            g.Concat(g.NumberText(g.F("PickedIndex")), "|", g.Concat(g.NumberText(g.X(p)), "|", g.Concat(g.NumberText(g.Y(p)), "|", g.NumberText(g.Z(p))))),
                            "|",
                            g.Concat(g.NumberText(targetIndex), "|", g.Concat(g.NumberText(g.X(o)), "|", g.Concat(g.NumberText(g.Y(o)), "|", g.NumberText(g.Z(o)))))));
                        Fired(g, "AGMCount", "AGMFull", "AGMFloat", "AGMFired");
                    });
                });
            }, () => g.SetGraphVar("AGMUnlockAt", now + 0.4f)));
            g.SetGraphVar("AGMPressTime", now);
        }

        /// <summary>The targetable vehicle nearest `point`, if one is within `reach` of its edge (NearFound / NearVehicle / NearDistance).</summary>
        static void Nearest(GraphWriter g, Val point, float reach)
        {
            g.SetGraphVar("NearFound", false);
            g.SetGraphVar("NearDistance", reach);
            g.ForEach(Aircraft(g), GO, other => g.If(g.ObjVarOf(other, "Targetable", typeof(bool)), () =>
            {
                var gap = g.Keep(g.Distance(point, g.Get(TF, "position", g.Get(GO, "transform", other))) - g.ObjVarOf(other, "HitRadius", typeof(float)));
                g.If(g.Less(gap, g.F("NearDistance")), () =>
                {
                    g.SetGraphVar("NearFound", true);
                    g.SetGraphVar("NearDistance", gap);
                    g.SetGraphVar("NearVehicle", other);
                });
            }));
        }

        // ---- the gun (DFUNC_Gun) ---------------------------------------------------------------------------------
        /// <summary>
        /// Ten times a second while firing: the first targetable vehicle the gun's line passes through (within its
        /// size of the line, in range, in sight) takes as many bullets as the damage particles fire in that time, at
        /// half of them hitting. Hits on one vehicle are sent at most five times a second.
        /// </summary>
        static void GunScan(GraphWriter g, Val dt)
        {
            var scan = g.SetGraphVar("GunScan", g.F("GunScan") + dt);
            g.If(g.Greater(scan, 0.1f), () =>
            {
                g.SetGraphVar("GunScan", 0f);
                var muzzle = g.Keep(g.ObjVar("GunMuzzle", TF));
                var from = g.Keep(g.Get(TF, "position", muzzle));
                var line = g.Keep(g.Get(TF, "forward", muzzle));
                var me = g.Keep(Self(g));
                g.SetGraphVar("GunBestFound", false);
                g.SetGraphVar("GunBestAlong", g.P("GunRange"));
                g.ForEach(Aircraft(g), GO, other =>
                    g.If(g.And(g.Not(g.Same(other, me)), g.And(g.ObjVarOf(other, "Targetable", typeof(bool)), g.Not(g.ObjVarOf(other, "Wrecked", typeof(bool))))), () =>
                    {
                        var to = g.Keep(g.Get(TF, "position", g.Get(GO, "transform", other)) - from);
                        var along = g.Keep(g.Dot(to, line));
                        var off = g.Magnitude(to - g.Scale(line, along));
                        g.If(g.And(g.And(g.Greater(along, 0f), g.Less(along, g.F("GunBestAlong"))), g.Less(off, g.ObjVarOf(other, "HitRadius", typeof(float)) * 0.6f)), () =>
                        {
                            g.SetGraphVar("GunBestFound", true);
                            g.SetGraphVar("GunBestAlong", along);
                            g.SetGraphVar("GunBest", other);
                        });
                    }));
                g.If(g.B("GunBestFound"), () =>
                {
                    var best = g.Keep(g.GraphVar("GunBest", GO));
                    var cast = g.DoOut(typeof(Physics), "Raycast",
                        new[] { V3T, V3T, typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int), typeof(QueryTriggerInteraction) }, null,
                        from, line, null, g.F("GunBestAlong") - g.ObjVarOf(best, "HitRadius", typeof(float)), UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    // (something solid on the way, short of the vehicle aimed at: the shots stop there)
                    g.If(g.Not(g.Result(cast)), () =>
                    {
                        g.If(g.And(g.B("HasGunTarget"), g.Not(g.Same(best, g.GraphVar("GunTarget", GO)))), () => GunFlush(g));
                        g.SetGraphVar("GunTarget", best);
                        g.SetGraphVar("HasGunTarget", true);
                        g.SetGraphVar("GunQueue", g.F("GunQueue") + g.P("GunDamage") * g.P("GunRate") * 0.05f);
                    });
                });
            });
            var send = g.SetGraphVar("GunSendTimer", g.F("GunSendTimer") + dt);
            g.If(g.Greater(send, 0.2f), () =>
            {
                g.SetGraphVar("GunSendTimer", 0f);
                GunFlush(g);
            });
        }

        static void GunFlush(GraphWriter g)
        {
            g.If(g.And(g.B("HasGunTarget"), g.Greater(g.F("GunQueue"), 0f)), () =>
            {
                SendDamage(g, g.GraphVar("GunTarget", GO), g.F("GunQueue"));
                g.SetGraphVar("GunQueue", 0f);
            });
        }

        // ---- every frame of the advanced controls: the held functions --------------------------------------------
        /// <summary>
        /// Gun (fires while its trigger is held), flares and bombs (one on the pull, more while held), cruise's set
        /// speed (moved with the hand while its trigger is held), smoke's colour (the right hand moved while the
        /// trigger stays held past 0.4 s), the missiles' search and lock, and the guided bomb's aim.
        /// </summary>
        static void FunctionsFrame(GraphWriter g, Val dt)
        {
            var now = g.Keep(Now(g));
            Val Held(string kind) => g.Or(g.And(Picked(g, "L", kind + "Slot"), g.Greater(g.F("TrigL"), 0.75f)), g.And(Picked(g, "R", kind + "Slot"), g.Greater(g.F("TrigR"), 0.75f)));
            Val Either(string kind) => g.Or(Picked(g, "L", kind + "Slot"), Picked(g, "R", kind + "Slot"));
            var grounded = g.Keep(g.B("Taxiing"));

            // gun
            var shooting = g.Keep(g.And(Held("Gun"), g.And(g.Greater(g.F("GunAmmo"), 0f), g.Or(g.ObjVar("GunGrounded", typeof(bool)), g.Not(grounded)))));
            g.If(g.Not(g.Same(shooting, g.B("GunFiring"))), () =>
            {
                g.SetGraphVar("GunFiring", shooting);
                AnimBoolNamed(g, g.ObjVar("GunBool", typeof(string)), shooting);
                Say(g, "gun", g.Select(shooting, "1", "0", typeof(string)));
            });
            g.If(shooting, () =>
            {
                g.SetGraphVar("GunAmmo", g.F("GunAmmo") - dt);
                GunScan(g, dt);
            }, () => GunFlush(g));

            // flares: one now, another every FlareHoldDelay while held
            g.If(g.And(Held("Flares"), g.And(g.Greater(now, g.F("FlareNext")), g.Greater(g.F("FlareCount"), 0.5f))), () =>
            {
                g.SetGraphVar("FlareNext", now + g.P("FlareHold"));
                g.SetGraphVar("FlareCount", g.F("FlareCount") - 1f);
                FlareShow(g);
                Say(g, "flare", "1");
            });
            g.If(g.Not(Held("Flares")), () => g.SetGraphVar("FlareNext", 0f));

            // bombs: one now, another every BombHoldDelay while held, from each launch point in turn
            g.If(g.And(g.And(Held("Bomb"), g.Greater(now, g.F("BombNext"))), g.And(g.Greater(g.F("BombCount"), 0.5f),
                g.Or(g.ObjVar("BombGrounded", typeof(bool)), g.Not(grounded)))), () =>
            {
                g.SetGraphVar("BombNext", now + g.P("BombHold"));
                PickRound(g, "BombPool");
                g.If(g.B("Picked"), () =>
                {
                    var point = g.Keep(g.Call(typeof(Mathf), "Repeat", new[] { typeof(float), typeof(float) }, null, g.F("BombPoint"), g.Max(g.P("BombPointCount"), 1f)));
                    g.SetGraphVar("BombPoint", point + 1f);
                    var from = g.ListItem(g.ObjVar("BombPoints", LIST), g.Call(typeof(Mathf), "FloorToInt", new[] { typeof(float) }, null, point), TF);
                    Launch(g, g.GraphVar("PickedRound", GO), from, Self(g), g.Same(1, 2), g.Same(1, 2), Vector3Zero(g), Vector3Zero(g), true);
                    Say(g, "bomb", g.Concat(g.NumberText(g.F("PickedIndex")), "|", g.NumberText(point)));
                    Fired(g, "BombCount", "BombFull", "BombFloat", "BombFired");
                });
            });
            g.If(g.Not(Held("Bomb")), () => g.SetGraphVar("BombNext", 0f));

            // cruise: the set speed follows the hand while its trigger is held, 250 m/s for every metre
            var cruiseLeft = g.Keep(g.And(Picked(g, "L", "CruiseSlot"), g.Greater(g.F("TrigL"), 0.75f)));
            g.If(g.And(g.B("CruiseOn"), g.Or(cruiseLeft, g.And(Picked(g, "R", "CruiseSlot"), g.Greater(g.F("TrigR"), 0.75f)))), () =>
            {
                var hand = g.Select(cruiseLeft, g.ObjVar("LeftHand", TF), g.ObjVar("RightHand", TF), TF);
                var z = g.Keep(g.Z(g.Call(TF, "InverseTransformDirection", Vec, null, g.Get(TF, "position") - g.Get(TF, "position", hand))));
                g.If(g.Not(g.B("CruiseHeld")), () =>
                {
                    g.SetGraphVar("CruiseHeld", true);
                    g.SetGraphVar("CruiseZero", z);
                    g.SetGraphVar("CruiseTemp", g.F("CruiseSpeed"));
                });
                g.SetGraphVar("CruiseSpeed", g.Max(g.F("CruiseTemp") + (g.F("CruiseZero") - z) * 250f, 0f));
            }, () => g.SetGraphVar("CruiseHeld", false));

            // smoke colour: hold the trigger past 0.4 s and move the right hand (right = red, up = green, forward = blue)
            g.If(g.And(Held("Smoke"), g.Greater(now - g.F("SmokePressTime"), 0.4f)), () =>
            {
                var at = g.Keep(g.Call(TF, "InverseTransformDirection", Vec, null, g.Get(TF, "position", g.ObjVar("RightHand", TF)) - g.Get(TF, "position")));
                g.If(g.Not(g.B("SmokePicking")), () =>
                {
                    g.SetGraphVar("SmokePicking", true);
                    g.SetGraphVar("SmokeZero", at);
                    g.SetGraphVar("SmokeTemp", g.GraphVar("SmokeColor", V3T));
                });
                var moved = g.Keep(g.GraphVar("SmokeTemp", V3T) + g.Scale(at - g.GraphVar("SmokeZero", V3T), g.P("ThrottleSensitivity")));
                var colour = g.SetGraphVar("SmokeColor", g.V3(g.Clamp01(g.X(moved)), g.Clamp01(g.Y(moved)), g.Clamp01(g.Z(moved))), V3T);
                SmokeColour(g, colour);
                g.If(g.And(g.B("SmokeOn"), g.And(g.Greater(now - g.F("SmokeSendTime"), 0.5f), g.Greater(g.Distance(colour, g.GraphVar("SmokeSent", V3T)), 0.01f))), () =>
                {
                    g.SetGraphVar("SmokeSendTime", now);
                    g.SetGraphVar("SmokeSent", colour);
                    Say(g, "smoke", SmokeMessage(g, g.Same(1, 1)));
                });
            }, () => g.SetGraphVar("SmokePicking", false));

            // missiles: search and lock while picked (and there are any left)
            g.If(g.And(Either("Aam"), g.Greater(g.F("AAMCount"), 0.5f)), () => AAMScan(g, dt), () => g.If(g.B("AAMHasTarget"), () => AAMClear(g)));

            // guided bomb: the aim follows the eyes while picked and not locked; the lock follows its vehicle
            g.If(g.And(g.Greater(g.F("AGMUnlockAt"), 0f), g.Greater(now, g.F("AGMUnlockAt"))), () =>
            {
                g.SetGraphVar("AGMUnlockAt", -1f);
                g.SetGraphVar("AGMLocked", false);
                Play(g, "AGMUnlockSound");
            });
            g.If(g.And(Either("Agm"), g.Greater(g.P("AGMFull"), 0f)), () =>
            {
                g.If(g.B("AGMLocked"), () =>
                {
                    Markers(g, false, true);
                    PlaceMarker(g, "LockMarker", AGMTargetPoint(g));
                }, () =>
                {
                    Markers(g, true, false);
                    PlaceMarker(g, "AimMarker", GazePoint(g));
                });
            }, () => Markers(g, false, false));
        }

        /// <summary>Four times a second in the cockpit: the dial display's lit marks, the head-up display's counts and the canopy's speed limits.</summary>
        static void FunctionsTick(GraphWriter g)
        {
            CanopyWatch(g);
            g.Do(GO, "SetActive", new[] { typeof(bool) }, g.ObjVar("HudHold", GO), g.B("AltHoldOn"));
            g.If(g.B("Advanced"), () =>
            {
                ShowAll(g, "EngineLit", g.Not(g.B("EngineOff")));
                ShowAll(g, "CanopyLit", g.Or(g.B("CanopyOpen"), g.B("CanopyBroken")));
                ShowAll(g, "CruiseLit", g.B("CruiseOn"));
                ShowAll(g, "AltholdLit", g.B("AltHoldOn"));
                ShowAll(g, "HookLit", g.B("HookDown"));
                ShowAll(g, "SmokeLit", g.B("SmokeOn"));
                ShowAll(g, "AgmLit", g.B("AGMLocked"));
                var format = new[] { typeof(string), typeof(object) };
                foreach (var (text, count) in new[] { ("AAMText", "AAMCount"), ("AGMText", "AGMCount"), ("BombText", "BombCount"), ("FlareText", "FlareCount") })
                    g.Set(TXT, "text", g.ObjVar(text, TXT), g.Call(typeof(string), "Format", format, null, "{0:F0}", g.F(count)));
                g.Set(TXT, "text", g.ObjVar("CruiseText", TXT), g.Select(g.B("CruiseOn"),
                    g.Call(typeof(string), "Format", format, null, "{0:F0}", g.F("CruiseSpeed") * g.P("CruiseKnots")), "", typeof(string)));
                var share = g.Keep(g.Clamp01(g.F("GunAmmo") / g.Max(g.P("GunAmmoFull"), 0.01f)));
                g.ForEach(g.ObjVar("AmmoBars", ENUM), TF, bar =>
                {
                    var scale = g.Keep(g.Get(TF, "localScale", bar));
                    g.Set(TF, "localScale", bar, g.V3(share, g.Y(scale), g.Z(scale)));
                });
            });
        }

        /// <summary>A pull of a trigger on the function picked on its dial: the functions that act on the pull.</summary>
        static void FunctionsPress(GraphWriter g, string side)
        {
            g.If(Picked(g, side, "EngineSlot"), () => EnginePress(g));
            g.If(Picked(g, side, "CanopySlot"), () => CanopyPress(g));
            g.If(Picked(g, side, "CruiseSlot"), () => CruisePress(g));
            g.If(Picked(g, side, "AltholdSlot"), () => AltHoldPress(g));
            g.If(Picked(g, side, "HookSlot"), () => HookPress(g));
            g.If(Picked(g, side, "SmokeSlot"), () => SmokePress(g));
            g.If(Picked(g, side, "AamSlot"), () => AAMPress(g));
            g.If(Picked(g, side, "AgmSlot"), () => AGMPress(g));
        }

        /// <summary>
        /// What everyone else hears from this vehicle's pilot, and the hits and warnings sent to it: one handler for
        /// every "{BoomMessage}|..." one-shot, so the rest of the fleet pays one string test for each.
        /// </summary>
        static void FunctionMessages(GraphWriter g)
        {
            g.On(g.Platform("OnOneShot"), e =>
            {
                var data = g.Keep(g.EventOut(e, "Data", typeof(string)));
                g.If(g.StartsWith(data, g.Concat(g.ObjVar("BoomMessage", typeof(string)), "|")), () =>
                {
                    var parts = g.Keep(g.Split(data, "|"));
                    var kind = g.Keep(g.ListItem(parts, 1, typeof(string)));
                    Val Arg(int i) => g.ListItem(parts, i, typeof(string));
                    Val On(int i) => g.Same(Arg(i), "1");
                    g.If(g.Same(kind, "hit"), () => TakeDamage(g, g.ParseNumber(Arg(2))));
                    g.If(g.Same(kind, "gun"), () =>
                    {
                        Wake(g);
                        AnimBoolNamed(g, g.ObjVar("GunBool", typeof(string)), On(2));
                    });
                    g.If(g.Same(kind, "engine"), () =>
                    {
                        Wake(g);
                        g.SetGraphVar("EngineOff", g.Not(On(2)));
                    });
                    g.If(g.Same(kind, "canopy"), () =>
                    {
                        g.SetGraphVar("CanopyOpen", g.Not(g.Same(Arg(2), "0")));
                        g.If(g.Same(Arg(2), "2"), () =>
                        {
                            g.SetGraphVar("CanopyBroken", true);
                            AnimBool(g, "canopybroken", true);
                        });
                    });
                    g.If(g.Same(kind, "hook"), () =>
                    {
                        g.SetGraphVar("HookDown", On(2));
                        AnimBool(g, "hookdown", On(2));
                    });
                    g.If(g.Same(kind, "hooked"), () => AnimTrigger(g, "hooked"));
                    g.If(g.Same(kind, "smoke"), () =>
                    {
                        Wake(g);
                        g.SetGraphVar("SmokeColor", g.V3(g.ParseNumber(Arg(3)), g.ParseNumber(Arg(4)), g.ParseNumber(Arg(5))));
                        SmokeColour(g, g.GraphVar("SmokeColor", V3T));
                        Smoke(g, On(2));
                    });
                    g.If(g.Same(kind, "flare"), () =>
                    {
                        Wake(g);
                        FlareShow(g);
                    });
                    g.If(g.Same(kind, "locked"), () => g.If(g.B("Piloting"), () =>
                    {
                        AnimTrigger(g, "radarlocked");
                        Play(g, "RadarLockedSound");
                    }));
                    // rounds this vehicle's pilot fired, flown here too (only the firer's copy does damage)
                    g.If(g.Same(kind, "aam"), () =>
                    {
                        Wake(g);
                        var target = g.ListItem(Aircraft(g), IndexOf(g, Arg(2)), GO);
                        Launch(g, g.ListItem(g.ObjVar("AamPool", LIST), IndexOf(g, Arg(3)), GO), g.ObjVar("AAMLaunch", TF), target,
                            g.Same(1, 1), g.Same(1, 1), Vector3Zero(g), Vector3Zero(g), false);
                        Fired(g, "AAMCount", "AAMFull", "AAMFloat", "AAMFired");
                    });
                    g.If(g.Same(kind, "agm"), () =>
                    {
                        Wake(g);
                        var index = g.Keep(IndexOf(g, Arg(6)));
                        var vehicle = g.Keep(g.Greater(index, -0.5f));
                        var target = g.Select(vehicle, g.ListItem(Aircraft(g), g.Call(typeof(Mathf), "Max", new[] { typeof(int), typeof(int) }, null, index, 0), GO), Self(g), GO);
                        Launch(g, g.ListItem(g.ObjVar("AgmPool", LIST), IndexOf(g, Arg(2)), GO), g.ObjVar("AGMLaunch", TF), target, g.Same(1, 1), vehicle,
                            g.V3(g.ParseNumber(Arg(3)), g.ParseNumber(Arg(4)), g.ParseNumber(Arg(5))),
                            g.V3(g.ParseNumber(Arg(7)), g.ParseNumber(Arg(8)), g.ParseNumber(Arg(9))), false);
                        Fired(g, "AGMCount", "AGMFull", "AGMFloat", "AGMFired");
                    });
                    g.If(g.Same(kind, "bomb"), () =>
                    {
                        Wake(g);
                        var from = g.ListItem(g.ObjVar("BombPoints", LIST), IndexOf(g, Arg(3)), TF);
                        Launch(g, g.ListItem(g.ObjVar("BombPool", LIST), IndexOf(g, Arg(2)), GO), from, Self(g), g.Same(1, 2), g.Same(1, 2),
                            Vector3Zero(g), Vector3Zero(g), false);
                        Fired(g, "BombCount", "BombFull", "BombFloat", "BombFired");
                    });
                });
            });
            // damage this client decided and that this vehicle is to take here (SendDamage)
            g.On(Custom(g, "fw_hit"), _ =>
            {
                var amount = g.Keep(g.P("IncomingDamage"));
                g.SetObjVar("IncomingDamage", 0f);
                TakeDamage(g, amount);
            });
        }
    }
}
