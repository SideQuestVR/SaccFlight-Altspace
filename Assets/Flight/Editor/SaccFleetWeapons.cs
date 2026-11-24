using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SaccFlightAndVehicles;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The dial functions' settings, read from a vehicle's Sacc scripts before they are removed, and the pools of
    /// missiles, guided bombs and bombs built from each launcher's own template (a world cannot instantiate at run
    /// time, so every round it can have in the air at once is built here, switched off). FlightGraphsFunctions.cs
    /// and FlightGraphsWeapons.cs are what work them.
    /// </summary>
    public static partial class SaccFleetBuilder
    {
        static string Cap(string kind) => char.ToUpperInvariant(kind[0]) + kind.Substring(1);

        /// <summary>
        /// How many of each round can be in the air at once from one vehicle (a round stays out for its flight and
        /// then ten seconds of explosion); a launcher that has more waits for one to come back.
        /// </summary>
        static readonly Dictionary<string, int> PoolSize = new Dictionary<string, int> { ["aam"] = 6, ["agm"] = 4, ["bomb"] = 6, ["bomb2"] = 8 };

        /// <summary>Everything the graph reads about one vehicle's dial functions, as object variables.</summary>
        sealed class FunctionSettings
        {
            public readonly Dictionary<string, object> values = new Dictionary<string, object>();
            /// <summary>References the vehicle may lack: (variable, kind) to fill with a stand-in that does nothing.</summary>
            public readonly List<(string name, Type type)> spares = new List<(string, Type)>();

            public void Store(VariableDeclarations d, GameObject unused, Func<Transform, string, Transform> spare,
                Func<UnityEngine.UI.Text, string, UnityEngine.UI.Text> spareText)
            {
                foreach (var pair in values)
                {
                    object v = pair.Value;
                    // a reference to something stripped since it was read (Unity's null) counts as missing
                    if (v is UnityEngine.Object o && o == null) v = null;
                    if (v == null)
                    {
                        var (_, type) = spares.First(s => s.name == pair.Key);
                        if (type == typeof(Transform)) v = spare(null, pair.Key);
                        else if (type == typeof(UnityEngine.UI.Text)) v = spareText(null, pair.Key);
                        else if (type == typeof(AudioSource))
                        {
                            var silent = FleetParts.Child(unused, pair.Key).AddComponent<AudioSource>();
                            silent.playOnAwake = false;
                            v = silent;
                        }
                        else if (type == typeof(GameObject)) v = FleetParts.Child(unused, pair.Key);
                        else if (type == typeof(Animator)) v = FleetParts.Child(unused, pair.Key).AddComponent<Animator>();
                        else throw new Exception("no stand-in for " + type.Name);
                    }
                    d.Set(pair.Key, v);
                }
            }

            public void Ref<T>(string name, T value) where T : UnityEngine.Object
            {
                values[name] = value;
                if (!spares.Any(s => s.name == name)) spares.Add((name, typeof(T)));
            }
        }

        static float Num(object owner, string field, float fallback)
        {
            var value = owner?.GetType().GetField(field)?.GetValue(owner);
            return value is float f ? f : value is int i ? i : fallback;
        }

        static bool Flag(object owner, string field, bool fallback) =>
            owner?.GetType().GetField(field)?.GetValue(owner) is bool b ? b : fallback;

        static T Ref<T>(object owner, string field) where T : class
        {
            var value = owner?.GetType().GetField(field)?.GetValue(owner);
            return value is UnityEngine.Object o && o == null ? null : value as T;
        }

        static string Text(object owner, string field, string fallback) =>
            owner?.GetType().GetField(field)?.GetValue(owner) is string s && s.Length > 0 ? s : fallback;

        static FunctionSettings ReadFunctionSettings(GameObject root, Dictionary<string, MonoBehaviour> functionOf, SAV_SoundController sound,
            StringBuilder report, string name)
        {
            var s = new FunctionSettings();
            MonoBehaviour F(string kind) => functionOf.TryGetValue(kind, out var f) ? f : null;

            // ---- engine (DFUNC_ToggleEngine; the airliner's AutoStarter starts it the same way)
            var engine = F("engine");
            s.values["EngineToggleDelay"] = Num(engine, "ToggleMinDelay", 6f);
            s.values["EngineStartTime"] = Num(engine, "StartUpTime", Num(engine, "startupTime", 5f));
            s.Ref("EngineStartSound", sound != null ? sound.EngineStartup : null);
            s.Ref("EngineOffSound", sound != null ? sound.EngineTurnOff : null);

            // ---- canopy (DFUNC_Canopy)
            var canopy = F("canopy");
            s.values["CanopyCloseTime"] = Num(canopy, "CanopyCloseTime", 1.8f);
            s.values["CanopyCanBreak"] = Flag(canopy, "CanopyCanBreakOff", false);
            s.values["CanopyBreakSpeed"] = Num(canopy, "CanopyBreakSpeed", 50f);
            s.values["CanopyAutoCloseSpeed"] = Num(canopy, "CanopyAutoCloseSpeed", 20f);
            s.values["CanopyDrag"] = Flag(canopy, "DoCanopyOpenDrag", true) ? Num(canopy, "CanopyDragMulti", 1.2f) - 1f : 0f;

            // ---- cruise (DFUNC_Cruise's throttle PID)
            var cruise = F("cruise");
            s.values["CruiseP"] = Num(cruise, "CruiseProportional", 0.13f);
            s.values["CruiseI"] = Num(cruise, "CruiseIntegral", 0.1f);
            s.values["CruiseIMax"] = Num(cruise, "CruiseIntegrator_Max", 10f);
            s.values["CruiseIMin"] = Num(cruise, "CruiseIntegrator_Min", -10f);
            s.values["CruiseD"] = Num(cruise, "Derivative", 0.1f);
            s.values["CruiseDMax"] = Num(cruise, "DerivMax", 0f);
            s.values["CruiseDMin"] = Num(cruise, "DerivMin", -1000f);
            s.values["CruiseKnots"] = Num(cruise, "speedMultiplier", 1.943845f);
            s.Ref("CruiseText", Ref<UnityEngine.UI.Text>(cruise, "HUDText_knotstarget"));

            // ---- altitude hold (DFUNC_AltHold's control law)
            var hold = F("althold");
            foreach (var (key, field, fallback) in new (string, string, float)[]
                     {
                         ("AHPitchP", "AltHoldPitchProportional", 1f), ("AHPitchI", "AltHoldPitchIntegral", 1f), ("AHPitchD", "AltHoldPitchDerivative", 0.1f),
                         ("AHRollP", "AltHoldRollProportional", 0.01f), ("AHRollD", "AltHoldRollDerivative", 0.1f),
                         ("AHYawP", "AltHoldYawProportional", 15f), ("AHYawD", "AltHoldYawDerivative", 0.1f),
                         ("AHGLimit", "GLimiter", 12f), ("AHAoALimit", "AoALimiter", 15f),
                         ("AHHoverPitch", "AutoHoverStrengthPitch", 5f), ("AHHoverMaxPitch", "AutoHoverMaxPitch", 10f), ("AHHoverSpeedPitch", "AutoHoverMaxAngleSpeedPitch", 20f),
                         ("AHHoverRoll", "AutoHoverStrengthRoll", 5f), ("AHHoverMaxRoll", "AutoHoverMaxRoll", 10f), ("AHHoverSpeedRoll", "AutoHoverMaxAngleSpeedRoll", 20f),
                         ("AHCollP", "CruiseProportional", 0.1f), ("AHCollI", "CruiseIntegral", 0.1f), ("AHCollIMax", "CruiseIntegratorMax", 5f),
                         ("AHCollIMin", "CruiseIntegratorMin", -5f), ("AHCollD", "CruiseDerivative", 0.1f),
                     })
                s.values[key] = Num(hold, field, fallback);
            s.values["AHHeli"] = Flag(hold, "HelicopterMode", false);
            s.Ref("HudHold", Ref<GameObject>(hold, "HudHold"));

            // ---- hook (DFUNC_Hook): the point it looks for a cable under, how hard a cable stops the vehicle
            var hook = F("hook");
            s.Ref("HookDetector", Ref<Transform>(hook, "HookDetector"));
            s.values["HookBrake"] = Num(hook, "HookedBrakeStrength", 55f);
            s.values["HookSnap"] = Num(hook, "HookedCableSnapDistance", 120f);
            s.Ref("CableSnapSound", Ref<AudioSource>(hook, "CableSnap"));
            // (the detector sat under the function's own object, which is removed with the scripts: keep it)
            var detector = Ref<Transform>(hook, "HookDetector");
            if (detector != null) detector.SetParent(root.transform, true);

            // ---- smoke (DFUNC_Smoke)
            var smoke = F("smoke");
            var smokeSystems = new AotList();
            foreach (var ps in Ref<ParticleSystem[]>(smoke, "DisplaySmoke") ?? new ParticleSystem[0]) if (ps != null) smokeSystems.Add(ps);
            s.values["SmokeSystems"] = smokeSystems;
            s.Ref("SmokeIndicator", Ref<GameObject>(smoke, "HUD_SmokeOnIndicator"));
            foreach (ParticleSystem ps in smokeSystems) { var emission = ps.emission; emission.enabled = false; }

            // ---- gun (DFUNC_Gun). The bullets anyone sees are the vehicle animator's ("gunfiring"); the hits are
            // worked out along the damage particles' line, as many a second as they emit.
            var gun = F("gun");
            s.values["GunAmmoFull"] = Num(gun, "GunAmmoInSeconds", 0f);
            s.values["GunRecoil"] = Num(gun, "RecoilForce", 0f);
            s.values["GunGrounded"] = Flag(gun, "AllowFiringGrounded", true);
            s.values["GunBool"] = Text(gun, "GunFiringBoolName", "gunfiring");
            s.values["GunRange"] = Mathf.Min(Num(gun, "MaxTargetDistance", 1500f), 2000f);
            var muzzle = Ref<Transform>(gun, "GunDamageParticle");
            s.Ref("GunMuzzle", muzzle);
            float rate = 20f, damage = 10f;
            if (muzzle != null && muzzle.TryGetComponent<ParticleSystem>(out var bullets))
            {
                rate = Mathf.Max(bullets.emission.rateOverTime.constantMax, 1f);
                foreach (Transform child in muzzle)
                    if (child.name.StartsWith("d:") && float.TryParse(child.name.Substring(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) damage = d;
            }
            s.values["GunRate"] = rate;
            s.values["GunDamage"] = damage;
            var ammoBars = new AotList();
            foreach (var bar in Ref<Transform[]>(gun, "AmmoBars") ?? new Transform[0]) if (bar != null) ammoBars.Add(bar);
            s.values["AmmoBars"] = ammoBars;

            // ---- launchers: their counts, how they aim, and a pool of their rounds
            var aam = F("aam");
            s.values["AAMFull"] = Num(aam, "NumAAM", 0f);
            s.values["AAMLockAngle"] = Num(aam, "AAMLockAngle", 15f);
            s.values["AAMLockTime"] = Num(aam, "AAMLockTime", 1.5f);
            s.values["AAMRange"] = Num(aam, "AAMMaxTargetDistance", 6000f);
            s.values["AAMGrounded"] = Flag(aam, "AllowFiringWhenGrounded", false);
            s.values["AAMFloat"] = Text(aam, "AnimFloatName", "AAMs");
            s.values["AAMFired"] = Text(aam, "AnimFiredTriggerName", "aamlaunched");
            s.Ref("AAMLaunch", Ref<Transform>(aam, "AAMLaunchPoint"));
            s.Ref("AAMTargetingSound", Ref<AudioSource>(aam, "AAMTargeting"));
            s.Ref("AAMLockSound", Ref<AudioSource>(aam, "AAMTargetLock"));
            s.Ref("AAMIndicator", Ref<Transform>(aam, "AAMTargetIndicator"));
            s.Ref("AAMText", Ref<UnityEngine.UI.Text>(aam, "HUDText_AAM_ammo"));

            var agm = F("agm");
            s.values["AGMFull"] = Num(agm, "NumAGM", 0f);
            s.values["AGMGrounded"] = Flag(agm, "AllowFiringWhenGrounded", false);
            s.values["AGMFloat"] = Text(agm, "AnimFloatName", "AGMs");
            s.values["AGMFired"] = Text(agm, "AnimFiredTriggerName", "agmlaunched");
            s.Ref("AGMLaunch", Ref<Transform>(agm, "AGMLaunchPoint"));
            s.Ref("AGMLockSound", Ref<AudioSource>(agm, "AGMLock"));
            s.Ref("AGMUnlockSound", Ref<AudioSource>(agm, "AGMUnlock"));
            s.Ref("AGMText", Ref<UnityEngine.UI.Text>(agm, "HUDText_AGM_ammo"));

            // bombs, and what Sacc also builds from its bomb launcher: rocket pods and cannons (a second one is "Bomb2")
            var bombPointsAll = new List<Transform>();
            foreach (var (kind, prefix) in new[] { ("bomb", "Bomb"), ("bomb2", "Bomb2") })
            {
                var bomb = F(kind);
                s.values[prefix + "Full"] = Num(bomb, "NumBomb", 0f);
                s.values[prefix + "Hold"] = Mathf.Max(Num(bomb, "BombHoldDelay", 0.5f), 0.05f);
                s.values[prefix + "Grounded"] = Flag(bomb, "AllowFiringWhenGrounded", false);
                s.values[prefix + "Float"] = Text(bomb, "AnimFloatName", "");
                s.values[prefix + "Fired"] = Text(bomb, "AnimFiredTriggerName", "");
                var points = new AotList();
                foreach (var point in Ref<Transform[]>(bomb, "BombLaunchPoints") ?? new Transform[0]) if (point != null) { points.Add(point); bombPointsAll.Add(point); }
                s.values[prefix + "Points"] = points;
                s.values[prefix + "PointCount"] = (float)points.Count;
                s.Ref(prefix + "Text", Ref<UnityEngine.UI.Text>(bomb, "HUDText_Bomb_ammo"));
            }

            // ---- switches (DFUNC_ToggleBool): the vehicle animator's bool and/or objects switched, as authored to start with
            for (int i = 1; i <= MaxToggles; i++)
            {
                var toggle = F("toggle" + i);
                var objects = new AotList();
                foreach (var o in Ref<GameObject[]>(toggle, "ToggleObjects") ?? new GameObject[0]) if (o != null) { objects.Add(o); o.SetActive(Flag(toggle, "OnDefault", false)); }
                s.values[$"Toggle{i}Bool"] = Text(toggle, "AnimBoolName", "");
                s.values[$"Toggle{i}Objects"] = objects;
                s.values[$"Toggle{i}Default"] = Flag(toggle, "OnDefault", false);
                var emitters = new AotList();
                foreach (var ps in Ref<ParticleSystem[]>(toggle, "ToggleEmission") ?? new ParticleSystem[0])
                    if (ps != null) { emitters.Add(ps); var emission = ps.emission; emission.enabled = Flag(toggle, "OnDefault", false); }
                s.values[$"Toggle{i}Emission"] = emitters;
            }
            // ---- boosts (DFUNC_CarBoost on an aircraft: the Wasp's thrusters): a push along the boost point
            for (int i = 1; i <= MaxBoosts; i++)
            {
                var boost = F("boost" + i);
                s.values[$"Boost{i}Accel"] = boost == null ? 0f : Flag(boost, "BoostType_Force", false) ? Num(boost, "BoostForce", 10f) : 8f;
                s.values[$"Boost{i}Seconds"] = Mathf.Clamp(Num(boost, "BoostInSeconds", 10f), 1f, 60f);
                s.values[$"Boost{i}Float"] = Text(boost, "_BoostingAnimFloatName", "");
                var point = Ref<Transform>(boost, "BoostPoint");
                if (point != null) point.SetParent(root.transform, true);        // (it may sit under the function's object)
                s.Ref($"Boost{i}Point", point != null ? point : root.transform);
            }
            // ---- thrust reverser (DFUNC_ToggleHandling setting ThrottleStrength: SD-1, SS-1): thrust times this while on
            var handling = F("handling");
            float handlingRatio = 1f;
            if (handling != null && Ref<string[]>(handling, "floatSGVNames") is string[] handlingNames && Ref<float[]>(handling, "floatSGVToggledValues") is float[] handlingValues)
            {
                int at = Array.IndexOf(handlingNames, "ThrottleStrength");
                var vehicleScript = root.GetComponentsInChildren<MonoBehaviour>(true).FirstOrDefault(m => m != null && m.GetType().GetField("ThrottleStrength") != null && m.GetType().Name.StartsWith("Sacc"));
                float original = Num(vehicleScript, "ThrottleStrength", 0f);
                if (at >= 0 && at < handlingValues.Length && Mathf.Abs(original) > 0.0001f) handlingRatio = handlingValues[at] / original;
            }
            s.values["HandlingRatio"] = handlingRatio;

            // ---- resupply: how long each kind takes to fill from empty (the functions' FullReloadTimeSec)
            s.values["GunReload"] = Num(gun, "FullReloadTimeSec", 20f);
            s.values["AAMReload"] = Num(aam, "FullReloadTimeSec", 10f);
            s.values["AGMReload"] = Num(agm, "FullReloadTimeSec", 8f);
            s.values["BombReload"] = Num(F("bomb"), "FullReloadTimeSec", 8f);
            s.values["Bomb2Reload"] = Num(F("bomb2"), "FullReloadTimeSec", 8f);
            s.values["FlareReload"] = Num(F("flares"), "FullReloadTimeSec", 15f);
            s.Ref("ReSupplySound", sound != null ? sound.ReSupply : null);

            // ---- catapult (DFUNC_Catapult): the function's own object is the point of the vehicle that locks on
            var catapult = F("catapult");
            s.values["HasCatapult"] = catapult != null && catapultShuttle != null;
            s.values["CatapultAngle"] = Num(catapult, "MaxAttachAngle", 15f);
            s.values["CatapultTriggerName"] = Text(catapult, "AnimTriggerLaunchName", "catapultlaunch");
            s.Ref("CatapultLockSound", Ref<AudioSource>(catapult, "CatapultLock"));
            Transform catapultPoint = null;
            if (catapult != null)
            {
                catapultPoint = FleetParts.Child(root, "CatapultPoint").transform;
                catapultPoint.position = catapult.transform.position;
            }
            s.Ref("CatapultPoint", catapultPoint);
            s.Ref("Catapult", catapult != null ? catapultShuttle : null);
            s.Ref("CatapultAnimator", catapult != null ? catapultAnimator : null);

            // ---- flight limits (DFUNC_Limits): how hard the pilot can pull, eased off towards a G and an angle of attack
            var limits = F("limits");
            s.values["HasLimits"] = limits != null;
            s.values["LimitsDefault"] = limits != null && Flag(limits, "DefaultLimitsOn", true);
            float gBegin = Num(limits, "GLimiter_Begin_Pos", 35f), gEnd = Num(limits, "GLimiter_Pos", 40f);
            float gBeginNeg = Num(limits, "GLimiter_Begin_Neg", -1f), gEndNeg = Num(limits, "GLimiter_Neg", -1f);
            if (gBeginNeg < 0f || gEndNeg < 0f) { gBeginNeg = gBegin; gEndNeg = gEnd; }
            bool noG = Flag(limits, "DisableGLimiter", false), noAoA = Flag(limits, "DisableAoALimiter", false);
            s.values["LimitGBegin"] = noG ? 100000f : gBegin;
            s.values["LimitGRange"] = Mathf.Max(gEnd - gBegin, 0.001f);
            s.values["LimitGBeginNeg"] = noG ? 100000f : gBeginNeg;
            s.values["LimitGRangeNeg"] = Mathf.Max(gEndNeg - gBeginNeg, 0.001f);
            s.values["LimitAoABegin"] = noAoA ? 100000f : Num(limits, "AoALimiter_Begin", 10f);
            s.values["LimitAoARange"] = Mathf.Max(Num(limits, "AoALimiter", 14f) - Num(limits, "AoALimiter_Begin", 10f), 0.001f);
            s.Ref("HudLimit", Ref<GameObject>(limits, "HudLimit"));

            var flares = F("flares");
            s.values["FlaresFull"] = Num(flares, "NumFlares", 0f);
            s.values["FlareHold"] = Mathf.Max(Num(flares, "FlareHoldDelay", 0.3f), 0.1f);
            s.values["FlareActiveTime"] = Num(flares, "FlareActiveTime", 4f);
            var flareSystems = new AotList();
            int burst = 1;
            foreach (var ps in Ref<ParticleSystem[]>(flares, "FlareParticles") ?? new ParticleSystem[0])
            {
                if (ps == null) continue;
                flareSystems.Add(ps);
                if (ps.emission.burstCount > 0) burst = Mathf.Max(1, (int)ps.emission.GetBurst(0).count.constantMax);
            }
            s.values["FlareSystems"] = flareSystems;
            s.values["FlareEmit"] = burst;
            s.Ref("FlareSound", Ref<AudioSource>(flares, "FlareLaunch"));
            s.Ref("FlareText", Ref<UnityEngine.UI.Text>(flares, "HUDText_flare_ammo"));

            // the sounds of being hit and of a missile lock, from the vehicle's sound controller
            s.Ref("BulletHitSound", sound != null && sound.BulletHit != null ? sound.BulletHit.FirstOrDefault(a => a) : null);
            s.Ref("MissileHitSound", sound != null && sound.MissileHit != null ? sound.MissileHit.FirstOrDefault(a => a) : null);
            s.Ref("RadarLockedSound", sound != null ? sound.RadarLocked : null);

            // (launch points sat under the launchers' own objects, which go with the scripts: keep them)
            foreach (var point in new[] { Ref<Transform>(aam, "AAMLaunchPoint"), Ref<Transform>(agm, "AGMLaunchPoint") }.Concat(bombPointsAll))
                if (point != null) point.SetParent(root.transform, true);

            // ---- the pools: copies of each launcher's round, held under the vehicle until fired
            var holder = FleetParts.Child(root, "Weapons");
            var all = new AotList();
            foreach (var (kind, templateField, number, code) in new[] { ("aam", "AAM", 1f, 1f), ("agm", "AGM", 2f, 2f), ("bomb", "Bomb", 3f, 3f), ("bomb2", "Bomb", 3f, 3f) })
            {
                var pool = new AotList();
                s.values[Cap(kind) + "Pool"] = pool;
                var launcher = F(kind);
                var template = Ref<GameObject>(launcher, templateField);
                int count = Mathf.Min(Mathf.RoundToInt(Num(launcher, "Num" + templateField, 0f) * Mathf.Max(Num(launcher, "NumBomb_PerShot", 1f), 1f)), PoolSize[kind]);
                s.values[Cap(kind) + "PoolSize"] = (float)(template != null ? count : 0);
                if (template == null || count <= 0) continue;
                var roundAsset = RoundPrefab(template, name, kind);
                for (int i = 0; i < count; i++)
                {
                    var round = NewRound(roundAsset, template, holder.transform, root, kind, code, $"{Cap(kind)} {i + 1}");
                    pool.Add(round);
                    all.Add(round);
                }
            }
            s.values["AllRounds"] = all;
            report.AppendLine($"   [{name}] functions: " + string.Join(", ", functionOf.Keys.OrderBy(k => k)) +
                              $"; rounds aam {((AotList)s.values["AamPool"]).Count}, agm {((AotList)s.values["AgmPool"]).Count}, bomb {((AotList)s.values["BombPool"]).Count}, bomb2 {((AotList)s.values["Bomb2Pool"]).Count}" +
                              " (" + string.Join(", ", ((AotList)s.values["AllRounds"]).Cast<GameObject>().GroupBy(r => r.name.Split(' ')[0]).Select(grp =>
                                  { var d = grp.First().GetComponent<Variables>().declarations; return $"{grp.Key} {d.Get("Thrust")}/s2 for {d.Get("BurnTime")} s, {d.Get("LaunchSpeed")} m/s"; })) + ")" +
                              $"; gun {s.values["GunAmmoFull"]} s at {rate:F0}/s x {damage}");
            return s;
        }

        /// <summary>
        /// One round of a pool: an instance of the launcher's round prefab under `holder`, switched off, with what the
        /// weapons graph needs to fly it, read from the launcher's template (`kind`: aam, agm, bomb, bomb2; `code` its
        /// number in the weapons graph).
        /// </summary>
        internal static GameObject NewRound(GameObject roundAsset, GameObject template, Transform holder, GameObject root, string kind, float code, string label)
        {
            var controller = template.GetComponents<MonoBehaviour>().FirstOrDefault(m => m != null && m.GetType().Name.EndsWith("Controller"));
            var templateBody = template.GetComponent<Rigidbody>();
            var bang = (Ref<AudioSource[]>(controller, "ExplosionSounds") ?? new AudioSource[0]).FirstOrDefault(a => a);
            string bangPath = bang != null ? PathFrom(template.transform, bang.transform) : null;
            // (a prefab instance: the scene keeps a reference and these few settings, not a copy of every
            // particle system; 220 copies took the scene file past 100 MB)
            var round = (GameObject)PrefabUtility.InstantiatePrefab(roundAsset, holder);
            round.name = label;
            round.transform.localPosition = Vector3.zero;
            round.transform.localRotation = Quaternion.identity;
            round.SetActive(false);
            var v = round.AddComponent<Variables>().declarations;
            v.Set("Kind", code);
            foreach (var key in new[] { "Alive", "Exploded", "Listed", "HasTarget", "TargetIsVehicle", "Lost", "Mine", "Warned" }) v.Set(key, false);
            foreach (var key in new[] { "Age", "LastDist", "DumbTime", "Ended" }) v.Set(key, 0f);
            foreach (var key in new[] { "Velocity", "TargetPoint", "TargetOffset", "LastTargetPos", "LaunchPos" }) v.Set(key, Vector3.zero);
            v.Set("Target", root);
            v.Set("Shooter", root);
            v.Set("Home", holder);
            var anim = round.GetComponentInChildren<Animator>(true);
            if (anim == null) anim = FleetParts.Child(round, "NoAnimator").AddComponent<Animator>();
            v.Set("Animator", anim);
            // its explosion sound, found in the copy where the template's was
            var bangCopy = bangPath != null ? round.transform.Find(bangPath)?.GetComponent<AudioSource>() : null;
            if (bangCopy == null) { bangCopy = FleetParts.Child(round, "NoSound").AddComponent<AudioSource>(); bangCopy.playOnAwake = false; }
            v.Set("Sound", bangCopy);
            var trails = new AotList();
            foreach (var trail in round.GetComponentsInChildren<TrailRenderer>(true)) trails.Add(trail);
            v.Set("Trails", trails);
            // flight: a Sacc missile or rocket is pushed by a ConstantForce its own animation turns on for its burn
            var (thrust, burn) = Burn(template, templateBody);
            v.Set("Thrust", thrust);
            v.Set("BurnTime", burn);
            v.Set("LaunchSpeed", Num(controller, "LaunchSpeed", 0f));      // (a cannon's shell leaves the barrel at this)
            v.Set("Drag", templateBody != null ? templateBody.linearDamping : 0.1f);
            v.Set("Gravity", templateBody == null || templateBody.useGravity);
            v.Set("AirPhysics", Num(controller, "AirPhysicsStrength", kind == "aam" ? 0.8f : kind == "agm" ? 3f : 0f));
            v.Set("RotSpeed", Num(controller, "RotSpeed", kind == "aam" ? 180f : 15f));
            v.Set("FlyStraight", Num(controller, "FlyStraightTime", 0.3f));
            v.Set("MaxLife", Num(controller, "MaxLifetime", kind == "aam" ? 12f : kind == "agm" ? 35f : 40f));
            v.Set("ExplodeLife", Num(controller, "ExplosionLifeTime", 10f));
            v.Set("ColliderActive", Num(controller, "ColliderActiveDistance", 8f));
            v.Set("TrackAngle", Num(controller, kind == "aam" ? "MaxTrackingAngle" : "LockAngle", 90f));
            v.Set("FlareEffect", Num(controller, "FlareEffect", 10f));
            v.Set("Proximity", Num(controller, "ProximityExplodeDistance", 20f));
            v.Set("Extension", Num(controller, "TargetVectorExtension", 1.2f));
            v.Set("Damage", Num(controller, kind == "aam" ? "AAMDamage" : kind == "agm" ? "AGMDamage" : "BombDamage", kind == "aam" ? 1f : 320f));
            v.Set("Splash", Num(controller, "SplashRadius", kind == "aam" ? 0f : 10f));
            return round;
        }

        /// <summary>
        /// Arresting cables for the hook (DFUNC_Hook catches anything it finds 2 m under its detector): three across
        /// the long runway at each end, where an aircraft touches down, with a yellow marker board at each side.
        /// The cables are trigger colliders, so wheels roll over them; the graph knows one by its IsCable variable.
        /// </summary>
        static void BuildCables(GameObject parent, StringBuilder report)
        {
            var layout = FlightWorld.CityTools.CitySceneBuilder.LoadLayout();
            if (layout.runways == null || layout.runways.Length == 0) { report.AppendLine("WARNING: no runway, so no arresting cables"); return; }
            var runway = layout.runways.OrderByDescending(r => r.length).First();
            var shift = -FlightWorld.CityTools.CitySceneBuilder.OriginOnMap(layout);
            var a = new Vector3(runway.ax, 0f, runway.ay) + shift;
            var b = new Vector3(runway.bx, 0f, runway.by) + shift;
            var along = (b - a).normalized;
            var side = Vector3.Cross(Vector3.up, along);
            float length = Vector3.Distance(a, b), height = layout.landHeight;
            var holder = FleetParts.Child(parent, "Arresting Cables");
            var cable = FleetParts.FlatMaterial("ArrestingCable", new Color(0.08f, 0.08f, 0.09f));
            var marker = FleetParts.FlatMaterial("CableMarker", new Color(0.95f, 0.78f, 0.10f));
            int made = 0;
            foreach (float fromEnd in new[] { 330f, 345f, 360f })
                foreach (var (start, direction) in new[] { (a, along), (b, -along) })
                {
                    if (fromEnd > length * 0.4f) continue;
                    var centre = start + direction * fromEnd;
                    // (the ground under it, measured: the runway is drawn on the land, which is not quite flat)
                    float ground = UnityEngine.Physics.Raycast(centre + Vector3.up * 50f, Vector3.down, out var hit, 100f) ? hit.point.y : height;
                    var wire = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    wire.name = $"Arresting Cable {++made}";
                    wire.transform.SetParent(holder.transform, true);
                    wire.transform.SetPositionAndRotation(new Vector3(centre.x, ground + 0.06f, centre.z), Quaternion.LookRotation(direction));
                    wire.transform.localScale = new Vector3(runway.width, 0.05f, 0.12f);
                    wire.GetComponent<MeshRenderer>().sharedMaterial = cable;
                    wire.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    var box = wire.GetComponent<BoxCollider>();
                    box.isTrigger = true;
                    box.size = new Vector3(1f, 20f, 6f);           // (scaled: 1 m tall in all, 0.7 m deep, so a 2 m look down finds it)
                    wire.AddComponent<Variables>().declarations.Set("IsCable", true);
                    foreach (float across in new[] { -0.5f, 0.5f })
                    {
                        var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        board.name = "Cable Marker";
                        UnityEngine.Object.DestroyImmediate(board.GetComponent<BoxCollider>());
                        board.transform.SetParent(holder.transform, true);
                        board.transform.SetPositionAndRotation(new Vector3(centre.x, ground + 0.4f, centre.z) + side * (runway.width * across + Mathf.Sign(across) * 2.5f),
                            Quaternion.LookRotation(direction));
                        board.transform.localScale = new Vector3(1.2f, 0.8f, 0.1f);
                        board.GetComponent<MeshRenderer>().sharedMaterial = marker;
                    }
                }
            report.AppendLine($"{made} arresting cables across the runway");
        }

        /// <summary>The world's catapult: the shuttle's trigger (what a vehicle locks to and rides) and its animator. Null without a runway.</summary>
        static Transform catapultShuttle;
        static Animator catapultAnimator;

        /// <summary>
        /// Sacc's catapult (Prefabs/Accessories/Catapult: a 93 m stroke in under two seconds), on the long runway abeam
        /// the apron, pointing the way the fleet is parked, a quarter of the width off the centre line on the apron's
        /// side, with a yellow board at its start. Only its trigger's place and its animation are used: a vehicle's graph finds it by
        /// distance (FlightGraphs.CatapultFrame), so its trigger collider is removed.
        /// </summary>
        static void BuildCatapult(GameObject parent, Vector3 arrival, StringBuilder report)
        {
            catapultShuttle = null;
            catapultAnimator = null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/Accessories/Catapult.prefab");
            var layout = FlightWorld.CityTools.CitySceneBuilder.LoadLayout();
            if (prefab == null || layout.runways == null || layout.runways.Length == 0) { report.AppendLine("WARNING: no catapult (prefab or runway missing)"); return; }
            var runway = layout.runways.OrderByDescending(r => r.length).First();
            var shift = -FlightWorld.CityTools.CitySceneBuilder.OriginOnMap(layout);
            var a = new Vector3(runway.ax, 0f, runway.ay) + shift;
            var b = new Vector3(runway.bx, 0f, runway.by) + shift;
            var flat = new Vector3(arrival.x, 0f, arrival.z);
            var start = Vector3.Distance(a, flat) <= Vector3.Distance(b, flat) ? a : b;
            var along = ((start == a ? b : a) - start).normalized;
            var side = Vector3.Cross(Vector3.up, along);
            float toApron = Mathf.Sign(Vector3.Dot(flat - start, side));
            // abeam the apron: 130 m further along than the arrival point (the rows stand 26 m and 72 m ahead of it,
            // facing the same way), if that leaves the stroke and 300 m of runway beyond it; else 60 m in from the end
            float abeam = Vector3.Dot(flat - start, along) + 130f;
            float fromEnd = abeam > 30f && abeam + 93f + 300f < Vector3.Distance(a, b) ? abeam : 60f;
            var at = start + along * fromEnd + side * (toApron * runway.width * 0.25f);
            float ground = UnityEngine.Physics.Raycast(at + Vector3.up * 50f, Vector3.down, out var hit, 100f) ? hit.point.y : layout.landHeight;

            var catapult = UnityEngine.Object.Instantiate(prefab);
            catapult.name = "Catapult";
            catapult.transform.SetParent(parent.transform, true);
            catapult.transform.SetPositionAndRotation(new Vector3(at.x, ground + 0.04f, at.z), Quaternion.LookRotation(along));
            foreach (var collider in catapult.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var stub in catapult.GetComponentsInChildren<MonoBehaviour>(true).Where(IsStandIn).ToArray()) UnityEngine.Object.DestroyImmediate(stub);
            foreach (var t in catapult.GetComponentsInChildren<Transform>(true))
            {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                t.gameObject.layer = 0;
                if (!t.CompareTag("Untagged")) t.gameObject.tag = "Untagged";
            }
            foreach (var ps in catapult.GetComponentsInChildren<ParticleSystem>(true)) { var c = ps.collision; c.colliderForce = 0f; c.enabled = false; }
            foreach (var renderer in catapult.GetComponentsInChildren<Renderer>(true))
            {
                bool particles = renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer;
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => SaccMaterials.Convert(m, particles)).ToArray();
                if (renderer is ParticleSystemRenderer psr && psr.trailMaterial != null) psr.trailMaterial = SaccMaterials.Convert(psr.trailMaterial, true);
                if (renderer.sharedMaterials.Any(m => m != null && !SaccMaterials.Usable(m))) renderer.enabled = false;
            }
            catapultAnimator = catapult.GetComponentInChildren<Animator>(true);
            catapultShuttle = catapult.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Trigger");
            if (catapultAnimator == null || catapultShuttle == null)
            {
                report.AppendLine("WARNING: the catapult prefab has no animator or no trigger; left out");
                UnityEngine.Object.DestroyImmediate(catapult);
                catapultShuttle = null;
                catapultAnimator = null;
                return;
            }
            // its stroke carries a vehicle whether or not anybody is looking at the catapult itself
            catapultAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // a yellow board on the apron's side of its start, as the cables have
            var marker = FleetParts.FlatMaterial("CableMarker", new Color(0.95f, 0.78f, 0.10f));
            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "Catapult Marker";
            UnityEngine.Object.DestroyImmediate(board.GetComponent<BoxCollider>());
            board.transform.SetParent(parent.transform, true);
            board.transform.SetPositionAndRotation(new Vector3(at.x, ground + 0.5f, at.z) + side * (toApron * (runway.width * 0.25f + 3f)), Quaternion.LookRotation(along));
            board.transform.localScale = new Vector3(0.1f, 1.0f, 2.4f);
            board.GetComponent<MeshRenderer>().sharedMaterial = marker;
            report.AppendLine($"catapult on the runway at {catapult.transform.position}, heading {catapult.transform.eulerAngles.y:F0}, shuttle at {catapultShuttle.position}");
        }

        const string RoundDir = "Assets/Flight/Rounds";

        /// <summary>
        /// How hard a round is pushed and for how long: its ConstantForce's forward force, as its own animation sets it
        /// (missiles' and rockets' burn clips switch it on for a few seconds), per kilogram of its body. None: (0, 0).
        /// </summary>
        static (float thrust, float seconds) Burn(GameObject template, Rigidbody body)
        {
            var force = template.GetComponentInChildren<ConstantForce>(true);
            if (force == null) return (0f, 0f);
            float mass = body != null && body.mass > 0f ? body.mass : 1f;
            float most = force.relativeForce.z, until = most > 0f ? 1000f : 0f;
            var anim = template.GetComponentInChildren<Animator>(true);
            if (anim != null && anim.runtimeAnimatorController != null)
                foreach (var clip in anim.runtimeAnimatorController.animationClips)
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(b => b.type == typeof(ConstantForce) && b.propertyName.Contains("RelativeForce.z")))
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve == null || curve.length == 0) continue;
                        float peak = curve.keys.Max(k => k.value);
                        if (peak <= most) continue;
                        most = peak;
                        float on = curve.keys.Where(k => k.value > 0f).Select(k => k.time).DefaultIfEmpty(0f).Max();
                        until = curve.keys.Where(k => k.time > on && k.value <= 0f).Select(k => k.time).DefaultIfEmpty(clip.length).Min();
                    }
            return (most / mass, until);
        }

        /// <summary>
        /// A launcher's round as this world flies it, saved as a prefab asset: a copy of the template with no body,
        /// no force and nothing to collide with (the weapons graph moves it), none of the original scripts, and the
        /// vehicle's materials converted to this world's shaders. Rebuilt with the vehicle.
        /// </summary>
        static GameObject RoundPrefab(GameObject template, string vehicle, string kind)
        {
            System.IO.Directory.CreateDirectory(RoundDir);
            var clean = UnityEngine.Object.Instantiate(template);
            clean.SetActive(true);
            foreach (var force in clean.GetComponentsInChildren<ConstantForce>(true)) UnityEngine.Object.DestroyImmediate(force);
            foreach (var body in clean.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
            foreach (var collider in clean.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            // (what the vehicle's own clean-up removes, which it cannot do inside a prefab instance)
            foreach (var camera in clean.GetComponentsInChildren<Camera>(true)) UnityEngine.Object.DestroyImmediate(camera.gameObject);
            foreach (var group in clean.GetComponentsInChildren<LODGroup>(true)) UnityEngine.Object.DestroyImmediate(group);
            foreach (var stub in clean.GetComponentsInChildren<MonoBehaviour>(true).Where(IsStandIn).ToArray()) UnityEngine.Object.DestroyImmediate(stub);
            foreach (var t in clean.GetComponentsInChildren<Transform>(true))
            {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                t.gameObject.layer = 0;
                if (!t.CompareTag("Untagged")) t.gameObject.tag = "Untagged";
            }
            foreach (var ps in clean.GetComponentsInChildren<ParticleSystem>(true)) { var c = ps.collision; c.colliderForce = 0f; c.enabled = false; }
            foreach (var renderer in clean.GetComponentsInChildren<Renderer>(true))
            {
                bool particles = renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer;
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => SaccMaterials.Convert(m, particles)).ToArray();
                if (renderer is ParticleSystemRenderer psr && psr.trailMaterial != null) psr.trailMaterial = SaccMaterials.Convert(psr.trailMaterial, true);
                if (renderer.sharedMaterials.Any(m => m != null && !SaccMaterials.Usable(m))) renderer.enabled = false;
            }
            string file = string.Concat((vehicle + "_" + kind).Split(System.IO.Path.GetInvalidFileNameChars())).Replace(' ', '_');
            var asset = PrefabUtility.SaveAsPrefabAsset(clean, $"{RoundDir}/{file}.prefab");
            UnityEngine.Object.DestroyImmediate(clean);
            return asset;
        }

        /// <summary>
        /// A place to stand in a moving vehicle (the balloon's basket). Two parts, as the client's seat code wants them
        /// (sidequest-greenfield-unity-60, 2026-11-23):
        ///  - "StandingFloor": a solid copy of the named object's lowest box collider, a direct child of the vehicle,
        ///    never switched off by this world. Friction on it is what carries a standing player.
        ///  - "RideZone": a BSAttachedObject with avatar attachment type 2 (Ride Zone, newer clients; older ones ignore it
        ///    with autoAttach off) and a trigger box from that floor to 2.2 m above it, on layer 2 (Ignore Raycast: the
        ///    player's body still sets it off, the player's ground probes do not see it as a ceiling). While the local
        ///    player stands inside, their pose is sent relative to it, so every client draws them in the basket, and the
        ///    vehicle keeps its colliders on for them even while someone is seated in it. Its network id is the stable
        ///    "fw-{vehicle}-RideZone" that Park gives it. It is a sibling of the seats, outside every seat's own attachment.
        /// </summary>
        static void StandingFloor(GameObject root, string under, StringBuilder report, string name)
        {
            var source = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == under);
            var lowest = source != null ? source.GetComponents<BoxCollider>().OrderBy(b => b.transform.TransformPoint(b.center).y).FirstOrDefault() : null;
            if (lowest == null) { report.AppendLine($"WARNING {name}: no box collider under {under} to stand on"); return; }
            var centre = root.transform.InverseTransformPoint(lowest.transform.TransformPoint(lowest.center));
            var size = Vector3.Scale(lowest.size, lowest.transform.lossyScale);
            float top = centre.y + size.y * 0.5f;

            // (exactly the original box: a floor any lower would be a new lowest point, and the vehicle parks by its
            // measured resting pose, so it would stand in the ground and be thrown out of it)
            var floor = FleetParts.Child(root, "StandingFloor");
            floor.transform.localPosition = centre;
            var solid = floor.AddComponent<BoxCollider>();
            solid.size = size;

            var zone = FleetParts.Child(root, "RideZone");
            zone.layer = 2;                                                   // Ignore Raycast
            zone.transform.localPosition = new Vector3(centre.x, top + 1.1f, centre.z);
            var trigger = zone.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(size.x, 2.2f, size.z);
            var attached = zone.AddComponent(GraphTypes.Find("BS.BSAttachedObject"));
            var so = new SerializedObject(attached);
            so.FindProperty("avatarAttachmentType").intValue = 2;            // Ride Zone (the SDK here has no name for it yet)
            so.FindProperty("autoAttach").boolValue = false;                  // (so an app without Ride Zones never attaches it)
            so.ApplyModifiedPropertiesWithoutUndo();
            report.AppendLine($"   [{name}] standing floor {size.x:F2} x {size.z:F2} m at {top:F2} m, ride zone over it");
        }

        static string PathFrom(Transform top, Transform t)
        {
            string path = t.name;
            for (var up = t.parent; up != null && up != top; up = up.parent) path = up.name + "/" + path;
            return path;
        }
    }
}
