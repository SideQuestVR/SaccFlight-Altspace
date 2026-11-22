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
        static readonly Dictionary<string, int> PoolSize = new Dictionary<string, int> { ["aam"] = 6, ["agm"] = 4, ["bomb"] = 6 };

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

            var bomb = F("bomb");
            s.values["BombFull"] = Num(bomb, "NumBomb", 0f);
            s.values["BombHold"] = Mathf.Max(Num(bomb, "BombHoldDelay", 0.5f), 0.1f);
            s.values["BombGrounded"] = Flag(bomb, "AllowFiringWhenGrounded", false);
            s.values["BombFloat"] = Text(bomb, "AnimFloatName", "");
            s.values["BombFired"] = Text(bomb, "AnimFiredTriggerName", "");
            var bombPoints = new AotList();
            foreach (var point in Ref<Transform[]>(bomb, "BombLaunchPoints") ?? new Transform[0]) if (point != null) bombPoints.Add(point);
            s.values["BombPoints"] = bombPoints;
            s.values["BombPointCount"] = (float)bombPoints.Count;
            s.Ref("BombText", Ref<UnityEngine.UI.Text>(bomb, "HUDText_Bomb_ammo"));

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
            foreach (var point in new[] { Ref<Transform>(aam, "AAMLaunchPoint"), Ref<Transform>(agm, "AGMLaunchPoint") }.Concat(bombPoints.Cast<Transform>()))
                if (point != null) point.SetParent(root.transform, true);

            // ---- the pools: copies of each launcher's round, held under the vehicle until fired
            var holder = FleetParts.Child(root, "Weapons");
            var all = new AotList();
            foreach (var (kind, templateField, number, code) in new[] { ("aam", "AAM", 1f, 1f), ("agm", "AGM", 2f, 2f), ("bomb", "Bomb", 3f, 3f) })
            {
                var pool = new AotList();
                s.values[Cap(kind) + "Pool"] = pool;
                var launcher = F(kind);
                var template = Ref<GameObject>(launcher, templateField);
                int count = Mathf.Min(Mathf.RoundToInt(Num(launcher, "Num" + templateField, 0f) * Mathf.Max(Num(launcher, "NumBomb_PerShot", 1f), 1f)), PoolSize[kind]);
                s.values[Cap(kind) + "PoolSize"] = (float)(template != null ? count : 0);
                if (template == null || count <= 0) continue;
                var controller = template.GetComponents<MonoBehaviour>().FirstOrDefault(m => m != null && m.GetType().Name.EndsWith("Controller"));
                var templateBody = template.GetComponent<Rigidbody>();
                var bang = (Ref<AudioSource[]>(controller, "ExplosionSounds") ?? new AudioSource[0]).FirstOrDefault(a => a);
                string bangPath = bang != null ? PathFrom(template.transform, bang.transform) : null;
                var roundAsset = RoundPrefab(template, name, kind);
                for (int i = 0; i < count; i++)
                {
                    // (a prefab instance: the scene keeps a reference and these few settings, not a copy of every
                    // particle system; 220 copies took the scene file past 100 MB)
                    var round = (GameObject)PrefabUtility.InstantiatePrefab(roundAsset, holder.transform);
                    round.name = $"{Cap(kind)} {i + 1}";
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
                    v.Set("Home", holder.transform);
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
                    // flight: Sacc's missiles burn 500 N on 1 kg for 3 s (their burn animation drives a ConstantForce)
                    v.Set("Thrust", kind == "bomb" ? 0f : 500f);
                    v.Set("BurnTime", 3f);
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
                    pool.Add(round);
                    all.Add(round);
                }
            }
            s.values["AllRounds"] = all;
            report.AppendLine($"   [{name}] functions: " + string.Join(", ", functionOf.Keys.OrderBy(k => k)) +
                              $"; rounds aam {((AotList)s.values["AamPool"]).Count}, agm {((AotList)s.values["AgmPool"]).Count}, bomb {((AotList)s.values["BombPool"]).Count}" +
                              $"; gun {s.values["GunAmmoFull"]} s at {rate:F0}/s x {damage}");
            return s;
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

        const string RoundDir = "Assets/Flight/Rounds";

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

        static string PathFrom(Transform top, Transform t)
        {
            string path = t.name;
            for (var up = t.parent; up != null && up != top; up = up.parent) path = up.name + "/" + path;
            return path;
        }
    }
}
