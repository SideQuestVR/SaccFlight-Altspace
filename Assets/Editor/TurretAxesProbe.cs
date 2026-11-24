using System.IO;
using System.Linq;
using System.Text;
using SaccFlightAndVehicles;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Read-only: the turret parts' rest orientations (local rotation, and where the muzzle points in each part's own
    /// axes), to decide how a graph can aim them. Output: Logs/turret_axes.txt.
    /// </summary>
    public static class TurretAxesProbe
    {
        static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

        [MenuItem("FlightWorld/Tools/Turret Axes Probe")]
        public static void Run()
        {
            var text = new StringBuilder();
            foreach (var name in new[] { "SH-1Main", "SH-3Main", "SH-2Main", "SaccBalloonMain", "SS-1Main", "ST-Thunder_Main", "SaccTankMain" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Sacc/Prefabs/{name}.prefab");
                text.AppendLine("== " + name);
                var root = prefab.transform;
                foreach (var seat in prefab.GetComponentsInChildren<SaccVehicleSeat>(true))
                {
                    var pf = seat.PassengerFunctions;
                    if (pf == null) continue;
                    var all = (pf.PassengerExtensions ?? new MonoBehaviour[0]).Concat(pf.Dial_Functions_L ?? new MonoBehaviour[0]).Concat(pf.Dial_Functions_R ?? new MonoBehaviour[0]).Where(m => m).ToList();
                    var gun = all.OfType<DFUNC_Gun>().FirstOrDefault();
                    var bomb = all.OfType<DFUNC_Bomb>().FirstOrDefault(b => b.BombLaunchPoints != null && b.BombLaunchPoints.Length > 0);
                    Transform muzzle = gun != null ? gun.GunDamageParticle : bomb != null ? bomb.BombLaunchPoints[0] : null;
                    text.AppendLine($"  seat {seat.name} (seat parent {seat.transform.parent.name}) muzzle {(muzzle ? muzzle.name : "-")} muzzle fwd in vehicle {(muzzle ? V(root.InverseTransformDirection(muzzle.forward)) : "-")}");
                    foreach (var part in all)
                    {
                        Transform[] parts = part is EXT_HandTurret h ? new[] { h.Gun, h.GunPitch, h.TurretTransform }
                            : part is EXT_Turret t ? new[] { t.TurretRotatorHor, t.TurretRotatorVert, t.TurretForwardEmpty } : new Transform[0];
                        foreach (var p in parts.Where(x => x != null))
                        {
                            var anim = p.GetComponentInParent<Animator>(true);
                            bool animated = false;
                            if (anim != null && anim.runtimeAnimatorController != null)
                            {
                                string path = AnimationUtility.CalculateTransformPath(p, anim.transform);
                                animated = anim.runtimeAnimatorController.animationClips.Any(c => AnimationUtility.GetCurveBindings(c).Any(b => b.path == path && b.type == typeof(Transform)));
                            }
                            text.AppendLine($"     {part.GetType().Name} part {p.name}: local euler {V(p.localEulerAngles)} parent {p.parent.name} " +
                                            $"muzzle fwd in its axes {(muzzle ? V(p.InverseTransformDirection(muzzle.forward)) : "-")} its up in vehicle {V(root.InverseTransformDirection(p.up))} animated {animated}");
                        }
                    }
                }
            }
            File.WriteAllText("Logs/turret_axes.txt", text.ToString());
        }
    }
}
