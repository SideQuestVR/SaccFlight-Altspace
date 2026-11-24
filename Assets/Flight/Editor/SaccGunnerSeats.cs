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
    /// Gunner seats (FlightGraphs.BuildGunner): read from a vehicle's Sacc seats while their passenger functions are still
    /// there (ReadGunners, before the builders strip the scripts and the gunner dial objects), then given a graph object
    /// each once the vehicle's own variables exist (BuildGunners). Used by both builders.
    /// </summary>
    public static partial class SaccFleetBuilder
    {
        internal sealed class GunnerSpec
        {
            public GameObject seat;
            public string label;
            public readonly Dictionary<string, object> values = new Dictionary<string, object>();
            public string howTo;
        }

        /// <summary>Round pools per gunner launcher: as many as can be out at once.</summary>
        const int GunnerPoolSize = 8;

        static string FunctionWord(MonoBehaviour f, GameObject template = null)
        {
            if (f is DFUNC_Gun) return "gun";
            string n = (template != null ? template.name : f.gameObject.name).ToLowerInvariant();
            if (n.Contains("rocket")) return "rockets";
            if (n.Contains("bomb") && template != null && !n.Contains("projectile")) return "bombs";
            if (n.Contains("projectile") || n.Contains("shot") || n.Contains("cannon") || n.Contains("shell")) return "cannon";
            string own = f.gameObject.name;
            foreach (var cut in new[] { "DFUNCP_", "DFUNC_" }) if (own.StartsWith(cut)) own = own.Substring(cut.Length);
            return own.ToLowerInvariant();
        }

        internal static List<GunnerSpec> ReadGunners(GameObject root, string vehicleName, StringBuilder report)
        {
            var specs = new List<GunnerSpec>();
            GameObject roundHolder = null;
            foreach (var seat in root.GetComponentsInChildren<SaccVehicleSeat>(true))
            {
                var pf = seat.PassengerFunctions;
                if (pf == null || seat.IsPilotSeat) continue;
                var ext = (pf.PassengerExtensions ?? new MonoBehaviour[0]).Where(m => m != null).ToList();
                var leftDial = (pf.Dial_Functions_L ?? new MonoBehaviour[0]).Where(m => m != null).ToList();
                var rightDial = (pf.Dial_Functions_R ?? new MonoBehaviour[0]).Where(m => m != null).ToList();
                var hand = ext.OfType<EXT_HandTurret>().FirstOrDefault(h => h.Gun != null);
                var turret = ext.OfType<EXT_Turret>().FirstOrDefault(t => t.TurretRotatorHor != null);
                var gun = rightDial.OfType<DFUNC_Gun>().Concat(leftDial.OfType<DFUNC_Gun>()).FirstOrDefault(x => x.GunDamageParticle != null);
                bool Usable(DFUNC_Bomb b) => b.Bomb != null && b.BombLaunchPoints != null && b.BombLaunchPoints.Any(p => p != null);
                var launchers = rightDial.OfType<DFUNC_Bomb>().Where(Usable).Select(b => (b, right: true))
                    .Concat(leftDial.OfType<DFUNC_Bomb>().Where(Usable).Select(b => (b, right: false))).Take(2).ToList();
                // (each trigger has one weapon: a second launcher on the same dial goes to the other trigger if that is free)
                if (launchers.Count == 2 && launchers[0].right == launchers[1].right)
                {
                    bool otherSide = !launchers[1].right;
                    bool gunThere = gun != null && (otherSide ? rightDial.Contains(gun) : leftDial.Contains(gun));
                    if (!gunThere) launchers[1] = (launchers[1].b, otherSide);
                }
                // (a seat whose functions are only the copilot's, or the SX-TR3's laser and tractor, is not a gunner seat here)
                if (hand == null && turret == null && gun == null && launchers.Count == 0) continue;

                var spec = new GunnerSpec { seat = seat.gameObject, label = seat.name };
                var v = spec.values;
                var how = new List<string>();

                // ---- aim
                Transform yawPart = null, pitchPart = null;
                float mode = 0f, limitLeft = 180f, limitRight = 180f, limitUp = 90f, limitDown = 90f;
                v["AimHand"] = FleetParts.HandTracker(false);
                v["TurnRateX"] = 0f; v["TurnRateY"] = 0f; v["TurnFriction"] = 4f; v["UseStickL"] = 0f; v["UseStickR"] = 0f;
                if (hand != null)
                {
                    yawPart = hand.Gun;
                    pitchPart = hand.GunPitch;
                    mode = hand.Aim_HandDirection ? 1f : 2f;
                    if (hand.LimitTurnAngle) { limitLeft = hand.AngleLimitLeft; limitRight = hand.AngleLimitRight; limitUp = hand.AngleLimitUp; limitDown = hand.AngleLimitDown; }
                    v["AimHand"] = FleetParts.HandTracker(hand.UseLeftHand);
                    how.Add(hand.Aim_HandDirection ? $"point your {(hand.UseLeftHand ? "left" : "right")} hand to aim" : $"aim past your {(hand.UseLeftHand ? "left" : "right")} hand");
                }
                else if (turret != null)
                {
                    yawPart = turret.TurretRotatorHor;
                    pitchPart = turret.TurretRotatorVert;
                    mode = 3f;
                    limitLeft = limitRight = turret.SideAngleMax;
                    limitUp = turret.UpAngleMax;
                    limitDown = turret.DownAngleMax;
                    float friction = Mathf.Max(turret.TurnFriction, 0.1f);
                    v["TurnFriction"] = friction;
                    v["TurnRateX"] = turret.TurnSpeedMultiX / friction;        // EXT_Turret's steady speed at full stick
                    v["TurnRateY"] = turret.TurnSpeedMultiY / friction;
                    bool l = turret.UseControlStickL, r = turret.UseControlStickR || !turret.UseControlStickL;
                    // (the thumbsticks are this world's only way to turn it; Sacc's tank gunner mostly grabs a virtual
                    // joystick and has its thumbstick at 3 deg/s: at full stick, never under 30 deg/s here)
                    float fullStick = Mathf.Max(turret.TurnSpeedMultiX / friction, 1f);
                    float Sensitivity(float s) => Mathf.Max(s * fullStick, 30f) / fullStick;
                    v["UseStickL"] = l ? Sensitivity(turret.ControlStickSensitivityL) : 0f;
                    v["UseStickR"] = r ? Sensitivity(turret.ControlStickSensitivityR) : 0f;
                    how.Add($"{(l && r ? "thumbsticks" : l ? "left thumbstick" : "right thumbstick")} turn the turret");
                }
                v["AimMode"] = mode;
                v["YawPart"] = yawPart;
                v["YawParent"] = yawPart != null ? yawPart.parent : root.transform;
                v["PitchPart"] = pitchPart;
                v["HasPitchPart"] = pitchPart != null;
                v["RestYaw"] = yawPart != null ? Mathf.DeltaAngle(0f, yawPart.localEulerAngles.y) : 0f;
                v["LimitLeft"] = limitLeft; v["LimitRight"] = limitRight; v["LimitUp"] = limitUp; v["LimitDown"] = limitDown;

                // ---- gun (DFUNC_Gun): as the pilot's (SaccFleetWeapons)
                v["HasGun"] = gun != null;
                v["GunSide"] = gun != null && rightDial.Contains(gun) ? 1f : 0f;
                v["GunBool"] = gun != null && !string.IsNullOrEmpty(gun.GunFiringBoolName) ? gun.GunFiringBoolName : "gunfiring";
                v["GunMuzzle"] = gun != null ? gun.GunDamageParticle : null;
                v["GunAmmoFull"] = gun != null ? gun.GunAmmoInSeconds : 0f;
                v["GunRange"] = gun != null ? Mathf.Min(gun.MaxTargetDistance, 2000f) : 0f;
                float rate = 20f, damage = 10f;
                if (gun != null && gun.GunDamageParticle.TryGetComponent<ParticleSystem>(out var bullets))
                {
                    rate = Mathf.Max(bullets.emission.rateOverTime.constantMax, 1f);
                    foreach (Transform child in gun.GunDamageParticle)
                        if (child.name.StartsWith("d:") && float.TryParse(child.name.Substring(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) damage = d;
                }
                v["GunRate"] = rate;
                v["GunDamage"] = damage;
                if (gun != null) how.Add($"{(rightDial.Contains(gun) ? "right" : "left")} trigger: gun");

                // ---- launchers (DFUNC_Bomb: rockets, cannon shells): pools built now, while the templates are here
                for (int i = 0; i < 2; i++)
                {
                    string prefix = i == 0 ? "Bomb" : "Bomb2";
                    var pool = new AotList();
                    var points = new AotList();
                    v[prefix + "Pool"] = pool;
                    v[prefix + "Points"] = points;
                    v[prefix + "PoolSize"] = 0f;
                    v[prefix + "Full"] = 0f;
                    v[prefix + "Hold"] = 1f;
                    v[prefix + "PointCount"] = 0f;
                    v[prefix + "Float"] = "";
                    v[prefix + "Fired"] = "";
                    v[prefix + "Side"] = 0f;
                    if (i >= launchers.Count) continue;
                    var (b, onRight) = launchers[i];
                    if (roundHolder == null) roundHolder = FleetParts.Child(root, "Gunner Rounds");
                    var asset = RoundPrefab(b.Bomb, vehicleName + "_" + seat.name, "gunner" + (i + 1));
                    int count = Mathf.Min(Mathf.RoundToInt(b.NumBomb * Mathf.Max(b.NumBomb_PerShot, 1)), GunnerPoolSize);
                    for (int k = 0; k < count; k++)
                        pool.Add(NewRound(asset, b.Bomb, roundHolder.transform, root, "bomb", 3f, $"{seat.name} {prefix} {k + 1}"));
                    foreach (var p in b.BombLaunchPoints) if (p != null) points.Add(p);
                    v[prefix + "PoolSize"] = (float)count;
                    v[prefix + "Full"] = (float)b.NumBomb;
                    v[prefix + "Hold"] = Mathf.Max(b.BombHoldDelay, 0.05f);
                    v[prefix + "PointCount"] = (float)points.Count;
                    v[prefix + "Float"] = b.AnimFloatName ?? "";
                    v[prefix + "Fired"] = b.AnimFiredTriggerName ?? "";
                    v[prefix + "Side"] = onRight ? 1f : 0f;
                    how.Add($"{(onRight ? "right" : "left")} trigger: {FunctionWord(b, b.Bomb)}");
                }

                // ---- switches (DFUNC_ToggleBool): searchlight, door, hatch; as authored to start with
                foreach (var (side, dial) in new[] { ("L", leftDial), ("R", rightDial) })
                {
                    var toggle = dial.OfType<DFUNC_ToggleBool>().FirstOrDefault();
                    var objects = new AotList();
                    if (toggle != null)
                        foreach (var o in toggle.ToggleObjects ?? new GameObject[0])
                            if (o != null) { objects.Add(o); o.SetActive(toggle.OnDefault); }
                    v["HasToggle" + side] = toggle != null;
                    v["Toggle" + side + "Bool"] = toggle != null ? toggle.AnimBoolName ?? "" : "";
                    v["Toggle" + side + "Objects"] = objects;
                    if (toggle != null) how.Add($"{(side == "L" ? "left" : "right")} thumbstick click: {FunctionWord(toggle)}");
                }
                spec.howTo = "Gunner: " + string.Join(", ", how);
                specs.Add(spec);
            }
            if (specs.Count > 0)
                report.AppendLine($"   [{vehicleName}] gunner seats: " + string.Join("; ", specs.Select(s => s.label + " (" + s.howTo.Substring(8) + ")")));
            return specs;
        }

        /// <summary>
        /// One graph object per gunner seat, under the vehicle, once the vehicle's seats are platform seats and its own
        /// variables (its one-shot name) are set. A seat that was not made (over a vehicle's passenger seat limit) is left out.
        /// </summary>
        internal static void BuildGunners(GameObject root, List<GunnerSpec> specs, Rigidbody body, Animator animator, StringBuilder report)
        {
            if (specs == null || specs.Count == 0) return;
            var graph = AssetDatabase.LoadAssetAtPath<ScriptGraphAsset>(FlightGraphs.GunnerPath);
            if (graph == null) throw new Exception("gunner graph missing: run FlightWorld/Flight/Regenerate Flight Graphs");
            var vehicleVariables = root.GetComponent<Variables>().declarations;
            string boom = (string)vehicleVariables.Get("BoomMessage");
            var seatType = GraphTypes.Find("BS.BSSeat");
            int made = 0;
            foreach (var spec in specs)
            {
                var seatComponent = spec.seat != null ? spec.seat.GetComponent(seatType) : null;
                if (seatComponent == null) continue;
                var holder = FleetParts.Child(root, "Gunner " + spec.label);
                var spare = FleetParts.Child(holder, "Spare").transform;
                var d = holder.AddComponent<Variables>().declarations;
                foreach (var pair in spec.values)
                {
                    object value = pair.Value;
                    if (value is UnityEngine.Object o && o == null) value = null;
                    if (value == null && (pair.Key.EndsWith("Part") || pair.Key.EndsWith("Parent") || pair.Key.EndsWith("Muzzle") || pair.Key.EndsWith("Hand"))) value = spare;
                    d.Set(pair.Key, value);
                }
                d.Set("Vehicle", root);
                d.Set("VehicleBody", body);
                d.Set("VehicleAnimator", animator);
                d.Set("Seat", spec.seat);
                d.Set("SeatComponent", seatComponent);
                d.Set("BoomMessage", boom + "#" + spec.label);
                d.Set("Watch", FleetParts.Watch());
                d.Set("WeaponsWorld", FleetParts.WeaponsWorld());
                d.Set("LeftHand", FleetParts.HandTracker(true));
                d.Set("RightHand", FleetParts.HandTracker(false));
                d.Set("HowTo", spec.howTo);
                holder.AddComponent<ScriptMachine>().nest.SwitchToMacro(graph);
                made++;
            }
            report.AppendLine($"   [{root.name}] {made} gunner seat graph(s)");
        }
    }
}
