using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The seated leg pose of every vehicle seat (BSSeat.Pose: feet and knee hints in seat metres). The SDK default puts
    /// the soles 0.64 m under the pelvis and 0.475 m ahead whatever is there, which in a low cockpit is below the floor
    /// and out of the bottom of the hull. Here each seat's feet are planted on the vehicle's own floor (the first surface
    /// under them, from the vehicle's meshes, not its colliders: hulls are a few boxes), at the spot nearest the natural
    /// one where the solved thigh, shin and foot cross no surface. The legs are solved with the app's own leg solver
    /// (SeatPose.SolveLeg) on the SDK seat robot's leg lengths; other avatars get the same targets.
    /// Also measures where each seat's cushion and backrest are, for the report.
    /// </summary>
    public static class SeatLegs
    {
        const float NaturalX = 0.14f, NaturalZ = 0.475f;
        // Outward from the seat's centre line for each foot; small and negative ones move it inward, for seats beside a wall.
        static readonly float[] SpreadXs = { 0.14f, 0.12f, 0.17f, 0.10f, 0.20f, 0.26f, 0.06f, 0.02f };
        static readonly float[] ForwardZs = { 0.475f, 0.425f, 0.525f, 0.375f, 0.575f, 0.625f, 0.68f, 0.75f, 0.82f, 0.90f, 0.98f, 0.32f, 0.27f };
        /// <summary>A seat whose pan is further than this from its origin is moved onto it (builder time only).</summary>
        const float SettleTolerance = 0.03f;
        const float LegRadius = 0.035f;

        public sealed class Result
        {
            public string vehicle, seat;
            public float cushion = float.NaN, back = float.NaN;          // cushion top / backrest, seat metres (y up / z ahead)
            public string cushionHit, backHit;
            public int defaultCrossings;                                // surfaces the SDK default legs pass through
            public string defaultCrossed;
            public Vector3 leftFoot, rightFoot, leftKnee, rightKnee;
            public float floorLeft = float.NaN, floorRight = float.NaN;
            public int crossings;                                       // surfaces the chosen legs pass through (0 is clean)
            public string crossed, floorHit;
            public bool dangling;
            public float shift;                                         // how far the seat was moved up onto its cushion
        }

        /// <summary>Fits the legs of every seat under <paramref name="root"/>; with <paramref name="apply"/> false only measures.</summary>
        public static List<Result> Fit(GameObject root, bool apply, StringBuilder report = null)
        {
            var results = new List<Result>();
            var seats = root.GetComponentsInChildren<BS.BSSeat>(true);
            if (seats.Length == 0) return results;
            using (var geometry = new Geometry(root))
            using (var robot = new BS.SeatPosePreview())
            {
                report?.AppendLine($"{root.name}: {geometry.kept} meshes measured; left out: " +
                    string.Join(", ", geometry.skipped.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));
                foreach (var seat in seats)
                {
                    var result = FitOne(seat, geometry, robot, apply, root.transform);
                    result.vehicle = root.name;
                    results.Add(result);
                    report?.AppendLine(Describe(result));
                }
            }
            return results;
        }

        static readonly MethodInfo LegSegments = typeof(BS.SeatPosePreview).GetMethod("TryGetLegSegments", BindingFlags.Instance | BindingFlags.NonPublic);

        static bool Leg(BS.SeatPosePreview robot, BS.SeatPose pose, bool left, out Vector3 hip, out float upper, out float lower,
            out Vector3 soleOffset, out Quaternion restFoot)
        {
            var args = new object[] { pose, left, null, null, null, null, null };
            bool ok = LegSegments != null && (bool)LegSegments.Invoke(robot, args);
            hip = ok ? (Vector3)args[2] : new Vector3(left ? -0.09f : 0.09f, -0.02f, 0.02f);
            upper = ok ? (float)args[3] : 0.42f;
            lower = ok ? (float)args[4] : 0.42f;
            soleOffset = ok ? (Vector3)args[5] : new Vector3(0f, -0.08f, 0f);
            restFoot = ok ? (Quaternion)args[6] : Quaternion.identity;
            return ok;
        }

        /// <summary>
        /// The top of the seat pan under the pelvis, seat metres (y), or NaN when there is no seat model there (the A320
        /// cabin, jump seats, a tank's heads-out hatches): the median of flat surfaces found by nine rays around the pelvis.
        /// </summary>
        static float Cushion(Geometry geometry, Func<Vector3, Vector3> P, Func<Vector3, Vector3> L, Vector3 up, out string hitName)
        {
            var heights = new List<float>();
            string name = null;
            foreach (var x in new[] { -0.09f, 0f, 0.09f })
            foreach (var z in new[] { -0.04f, 0.04f, 0.12f })
            {
                // From well above, the first flat surface between 15 cm under and 32 cm over the origin: the pan, not an
                // armrest side, a strap or the stick; anything deeper than 15 cm is the floor, not a pan.
                foreach (var hit in geometry.RayAll(P(new Vector3(x, 0.45f, z)), -up, 0.95f).OrderBy(h => h.distance))
                {
                    if (Mathf.Abs(Vector3.Dot(hit.normal, up)) < 0.65f) continue;
                    var y = L(hit.point).y;
                    if (y > 0.32f) continue;
                    if (y >= -0.15f) { heights.Add(y); name = name ?? geometry.Name(hit); }
                    break;
                }
            }
            hitName = name;
            if (heights.Count < 3) return float.NaN;                    // most rays found nothing seat-like
            heights.Sort();
            return heights[heights.Count / 2];
        }

        static Result FitOne(BS.BSSeat seat, Geometry geometry, BS.SeatPosePreview robot, bool apply, Transform root)
        {
            var result = new Result { seat = seat.name };
            var t = seat.transform;
            Vector3 P(Vector3 local) => t.position + t.rotation * local;              // seat metres (never the seat's scale)
            Vector3 L(Vector3 world) => Quaternion.Inverse(t.rotation) * (world - t.position);
            var up = t.up; var forward = t.forward;

            var pose = BS.SeatPose.Default;
            pose.pelvisPosition = seat.Pose.pelvisPosition;

            // ---- the seat on its cushion: the app pins the pelvis 4 cm over the origin, so the origin goes on the pan ----
            result.cushion = Cushion(geometry, P, L, up, out result.cushionHit);
            if (!float.IsNaN(result.cushion) && Mathf.Abs(result.cushion) > SettleTolerance)
            {
                result.shift = result.cushion;
                if (apply)
                {
                    var delta = up * result.cushion;
                    Undo.RecordObject(t, "Seat onto its cushion");
                    t.position += delta;
                    // Its eye point (what the HUD and the graph take as the rider's eyes) moves with it; the exit stays put.
                    var eye = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(e => e.name == "EyePoint " + seat.name);
                    if (eye != null) { Undo.RecordObject(eye, "Seat onto its cushion"); eye.position += delta; }
                    var so = new SerializedObject(seat);
                    var exit = so.FindProperty("exitPosition");
                    if (exit != null) { exit.vector3Value -= Quaternion.Inverse(t.rotation) * delta; so.ApplyModifiedPropertiesWithoutUndo(); }
                    result.cushion = Cushion(geometry, P, L, up, out result.cushionHit);
                }
            }
            if (geometry.Ray(P(pose.pelvisPosition + new Vector3(0f, 0.25f, 0f)), -forward, 0.8f, out var back))
            {
                result.back = L(back.point).z;
                result.backHit = geometry.Name(back);
            }

            // ---- what the default legs pass through ----
            var crossedDefault = new HashSet<string>();
            foreach (var left in new[] { true, false })
            {
                Leg(robot, pose, left, out var hip, out var upper, out var lower, out var soleOffset, out var restFoot);
                var sole = left ? pose.leftFootPosition : pose.rightFootPosition;
                var hint = left ? pose.leftKneeHint : pose.rightKneeHint;
                Chain(hip, hint, sole, upper, lower, soleOffset, restFoot, out var knee, out var ankle, out var solved, out var toe);
                result.defaultCrossings += Crossings(geometry, P, hip, knee, ankle, solved, toe, crossedDefault);
            }
            result.defaultCrossed = string.Join(", ", crossedDefault.Take(4));

            // ---- search for clear feet on the floor (or pedals), nearest the natural spot ----
            bool underRoof = geometry.Ray(P(pose.pelvisPosition + new Vector3(0f, 0.3f, 0f)), up, 3f, out _);
            var crossedChosen = new HashSet<string>();
            var floors = new HashSet<string>();
            foreach (var left in new[] { true, false })
            {
                Leg(robot, pose, left, out var hip, out var upper, out var lower, out var soleOffset, out var restFoot);
                float lowestSole = hip.y - (upper + lower) * 0.995f + soleOffset.y;   // feet hanging straight down (sole offset is negative)
                float bestScore = float.MaxValue;
                Vector3 bestSole = default, bestKnee = default, bestHint = default;
                float bestFloor = float.NaN; int bestCross = 0; string bestFloorHit = null; bool bestDangle = false;
                HashSet<string> bestCrossed = null;
                float side = left ? -1f : 1f;
                foreach (var x in SpreadXs)
                foreach (var z in ForwardZs)
                {
                    // The floor: the first surface a foot can stand on (not steep) at least 12 cm under the hip. Rays start
                    // above the knees, so a seat front or a sunk pan does not hide it; the panel above is skipped by height.
                    float floor = float.NaN; string floorHit = null;
                    var rayFrom = P(new Vector3(side * x, hip.y + 0.30f, z));
                    foreach (var hit in geometry.RayAll(rayFrom, -up, 0.30f + (hip.y - lowestSole) + 0.6f).OrderBy(h => h.distance))
                    {
                        var y = L(hit.point).y;
                        // Normally 12 cm or more under the hip; well past any seat pan, a floor nearly level with the hip too
                        // (riders sitting on a truck bed or a low rear bench: legs straight out along it).
                        if (y > hip.y - 0.03f) continue;
                        if (Mathf.Abs(Vector3.Dot(hit.normal, up)) < 0.30f) continue;   // sloped footwells and pedals count
                        floor = y; floorHit = geometry.Name(hit);
                        break;
                    }
                    float soleY; bool dangle;
                    if (float.IsNaN(floor) || floor < lowestSole)
                    {
                        // No floor to stand on (an open-bottomed cab, a door sill): the feet hang, but never below the bottom
                        // edge of the body around them, so nothing shows under the vehicle.
                        soleY = Mathf.Max(lowestSole, Mathf.Min(-0.6f, hip.y - 0.62f), HullBottom(geometry, P, hip.y, lowestSole, side * x, z) + 0.04f);
                        dangle = true;
                    }
                    else { soleY = floor + 0.005f; dangle = false; }
                    var sole = new Vector3(side * x, soleY, z);
                    var hint = new Vector3(side * x, hip.y + 0.15f, z - 0.025f);
                    Chain(hip, hint, sole, upper, lower, soleOffset, restFoot, out var knee, out var ankle, out var solved, out var toe);
                    if (!dangle && (solved - sole).magnitude > 0.03f) dangle = true;       // out of reach: the foot stops above the floor
                    var crossed = new HashSet<string>();
                    int crossings = Crossings(geometry, P, hip, knee, ankle, solved, toe, crossed);
                    // Knees much above the hips look cramped (and meet the panel): feet go further forward on a low floor.
                    float kneeHigh = Mathf.Max(0f, knee.y - (hip.y + 0.20f));
                    // A foot with anything over it below hip height has gone through a floor, a seat pan or a wall: never that.
                    float soleTop = solved.y + 0.02f;
                    // (well ahead of any seat pan, up to pelvis height: riders sitting on a floor have it level with their hips)
                    float overTop = solved.z >= 0.5f ? hip.y + 0.03f : hip.y - 0.02f;
                    bool buried = soleTop < overTop && geometry.Ray(P(new Vector3(solved.x, soleTop, solved.z)), up, overTop - soleTop, out _);
                    if (buried) crossings += 3;
                    // A rider under a roof keeps their feet under it (door gunners: not out through the doorway).
                    if (underRoof && !geometry.Ray(P(new Vector3(solved.x, solved.y + 0.06f, solved.z)), up, 3f, out _)) crossings += 2;
                    // A floor close under the hips and near the seat is likely its own pan or step: only if nothing better.
                    float tucked = !dangle && soleY > hip.y - 0.12f && z < 0.6f ? 1f : 0f;
                    float score = tucked + crossings * 10f + Mathf.Abs(z - NaturalZ) * 2f + Mathf.Abs(x - NaturalX) * 3f + kneeHigh * 14f + (dangle ? 0.4f : 0f);
                    if (score < bestScore)
                    {
                        bestScore = score; bestSole = solved; bestKnee = knee; bestHint = hint; bestFloor = floor;
                        bestCross = crossings; bestCrossed = crossed; bestFloorHit = floorHit; bestDangle = dangle;
                    }
                }
                if (left) { pose.leftFootPosition = bestSole; pose.leftFootRotation = Quaternion.identity; pose.leftKneeHint = bestHint; result.leftFoot = bestSole; result.leftKnee = bestKnee; result.floorLeft = bestFloor; }
                else { pose.rightFootPosition = bestSole; pose.rightFootRotation = Quaternion.identity; pose.rightKneeHint = bestHint; result.rightFoot = bestSole; result.rightKnee = bestKnee; result.floorRight = bestFloor; }
                result.crossings += bestCross;
                result.dangling |= bestDangle;
                if (bestCrossed != null) crossedChosen.UnionWith(bestCrossed);
                if (bestFloorHit != null) floors.Add(bestFloorHit);
            }
            result.crossed = string.Join(", ", crossedChosen.Take(4));
            result.floorHit = string.Join(", ", floors.Take(2));

            if (apply)
            {
                Undo.RecordObject(seat, "Fit seat legs");
                seat.Pose = pose;
                EditorUtility.SetDirty(seat);
            }
            return result;
        }

        /// <summary>
        /// The lowest height (seat metres) at which the body still surrounds a foot at (x, z): walls found by horizontal
        /// rays to both sides and ahead (two of three must hit within 1.2 m). Hanging feet stop above it.
        /// </summary>
        static float HullBottom(Geometry geometry, Func<Vector3, Vector3> P, float hipY, float lowest, float x, float z)
        {
            var t = P(Vector3.zero);
            var right = P(Vector3.right) - t; var ahead = P(Vector3.forward) - t;
            float bottom = hipY - 0.15f;
            for (float y = hipY - 0.15f; y >= lowest - 0.02f; y -= 0.03f)
            {
                var from = P(new Vector3(x, y, z));
                int walls = (geometry.Ray(from, right, 1.2f, out _) ? 1 : 0) + (geometry.Ray(from, -right, 1.2f, out _) ? 1 : 0)
                    + (geometry.Ray(from, ahead, 1.2f, out _) ? 1 : 0);
                if (walls < 2) break;
                bottom = y;
            }
            return bottom;
        }

        /// <summary>The leg the app would draw for these targets (its own solver), in seat metres.</summary>
        static void Chain(Vector3 hip, Vector3 hint, Vector3 sole, float upper, float lower, Vector3 soleOffset, Quaternion restFoot,
            out Vector3 knee, out Vector3 ankle, out Vector3 solvedSole, out Vector3 toe)
        {
            var offset = restFoot * soleOffset;
            var ankleTarget = sole - offset;
            BS.SeatPose.SolveLeg(hip, hint, ankleTarget, upper, lower, Vector3.forward, Vector3.up, out knee, out ankle);
            solvedSole = ankle + offset;
            toe = solvedSole + new Vector3(0f, 0.04f, 0.17f);
        }

        /// <summary>How many surfaces the thigh, shin and foot pass through or touch (leg thickness LegRadius).</summary>
        static int Crossings(Geometry geometry, Func<Vector3, Vector3> P, Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 sole,
            Vector3 toe, HashSet<string> names)
        {
            int count = 0;
            // The thigh from a third of the way out (its back half lies on the cushion), the shin, the foot just above the sole.
            var footBase = sole + new Vector3(0f, LegRadius + 0.01f, 0f);
            var footTip = toe + new Vector3(0f, 0.0f, 0f);
            var part = new HashSet<string>();
            void Test(string label, Vector3 a, Vector3 b, float radius)
            {
                part.Clear();
                int n = geometry.Touches(P(a), P(b), radius, part);
                count += n;
                foreach (var name in part) names.Add(label + ":" + name);
            }
            Test("thigh", Vector3.Lerp(hip, knee, 0.55f), knee, LegRadius);
            Test("shin", knee, Vector3.Lerp(knee, ankle, 0.92f), LegRadius);
            Test("foot", footBase, footTip, LegRadius * 0.8f);
            return count;
        }

        public static string Describe(Result r)
        {
            string F(float v) => float.IsNaN(v) ? "  -  " : v.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
            string V(Vector3 v) => $"({v.x.ToString("0.00", CultureInfo.InvariantCulture)}, {v.y.ToString("0.00", CultureInfo.InvariantCulture)}, {v.z.ToString("0.00", CultureInfo.InvariantCulture)})";
            var sb = new StringBuilder();
            sb.Append($"{r.vehicle} / {r.seat}: {(r.shift != 0f ? "MOVED " + F(r.shift) + " onto its cushion; " : "")}cushion {F(r.cushion)} [{r.cushionHit}] back {F(r.back)} [{r.backHit}]");
            sb.Append($" | default legs cross {r.defaultCrossings}{(r.defaultCrossings > 0 ? " (" + r.defaultCrossed + ")" : "")}");
            sb.Append($" | feet L{V(r.leftFoot)} R{V(r.rightFoot)} floor {F(r.floorLeft)}/{F(r.floorRight)} [{r.floorHit}]");
            sb.Append($" knees L{V(r.leftKnee)} R{V(r.rightKnee)}{(r.dangling ? " dangling" : "")}");
            sb.Append(r.crossings == 0 ? " | CLEAR" : $" | STILL CROSSES {r.crossings} ({r.crossed})");
            return sb.ToString();
        }

        /// <summary>
        /// World-space copies of a vehicle's meshes (every renderer, also switched-off ones such as aboard-only cockpit
        /// parts; only LOD 0) as concave colliders in a preview scene of their own, hit from both sides.
        /// </summary>
        sealed class Geometry : IDisposable
        {
            readonly Scene scene;
            readonly PhysicsScene physics;
            readonly bool backfaces;
            readonly List<Mesh> meshes = new List<Mesh>();
            readonly Dictionary<Collider, string> names = new Dictionary<Collider, string>();
            readonly Collider[] overlap = new Collider[32];
            public readonly Dictionary<string, int> skipped = new Dictionary<string, int>();
            public int kept;
            void Skip(string why) => skipped[why] = skipped.TryGetValue(why, out var n) ? n + 1 : 1;

            public Geometry(GameObject root)
            {
                backfaces = Physics.queriesHitBackfaces;
                Physics.queriesHitBackfaces = true;
                scene = EditorSceneManager.NewPreviewScene();
                physics = scene.GetPhysicsScene();
                var lesserLods = new HashSet<Renderer>();
                foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
                {
                    var lods = group.GetLODs();
                    for (int i = 1; i < lods.Length; i++) foreach (var r in lods[i].renderers) if (r) lesserLods.Add(r);
                }
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (lesserLods.Contains(renderer)) { Skip("lod>0"); continue; }
                    // Switched-off renderers are collider proxies and the like (aboard-only parts are switched off by their
                    // object, not their renderer, and are kept).
                    if (!renderer.enabled) { Skip("renderer off"); continue; }
                    if (IsColliderProxy(renderer.transform, root.transform)) { Skip("collider proxy"); continue; }
                    Mesh source = null;
                    Matrix4x4 matrix = renderer.transform.localToWorldMatrix;
                    if (renderer is MeshRenderer)
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        source = filter != null ? filter.sharedMesh : null;
                    }
                    else if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    {
                        source = new Mesh();
                        skinned.BakeMesh(source, true);
                        meshes.Add(source);
                        matrix = Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, Vector3.one);
                    }
                    if (source == null || source.vertexCount == 0) { Skip("no mesh " + renderer.GetType().Name); continue; }
                    var why = SeeThrough(renderer);
                    if (why != null) { Skip(why); continue; }
                    var vertices = source.vertices;
                    for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
                    var copy = new Mesh { indexFormat = source.indexFormat, vertices = vertices };
                    var triangles = new List<int>();
                    for (int s = 0; s < source.subMeshCount; s++)
                        if (source.GetTopology(s) == MeshTopology.Triangles) triangles.AddRange(source.GetTriangles(s));
                    if (triangles.Count == 0) { UnityEngine.Object.DestroyImmediate(copy); Skip("no triangles"); continue; }
                    copy.SetTriangles(triangles, 0);
                    copy.RecalculateBounds();
                    meshes.Add(copy);
                    var holder = new GameObject(renderer.name);
                    SceneManager.MoveGameObjectToScene(holder, scene);
                    var collider = holder.AddComponent<MeshCollider>();
                    try { collider.sharedMesh = copy; }
                    catch { Skip("collider failed"); continue; }
                    names[collider] = Path(renderer.transform, root.transform);
                    kept++;
                }
                Physics.SyncTransforms();
            }

            /// <summary>Sacc's collision stand-ins (COLLIDERS/COL1, COL_BODY, SACCTANK_COL_UNDERTURRET): not what anyone sees.</summary>
            static bool IsColliderProxy(Transform t, Transform root)
            {
                for (var p = t; p != null && p != root; p = p.parent)
                {
                    var n = p.name.ToUpperInvariant();
                    if (n == "COLLIDERS" || n.StartsWith("COL_") || n.Contains("_COL_") || n.EndsWith("_COL")
                        || System.Text.RegularExpressions.Regex.IsMatch(n, @"^COL\d*$")) return true;
                }
                return false;
            }

            /// <summary>Glass, prop blur discs, effects: nothing a leg rests on or is stopped by.</summary>
            static string SeeThrough(Renderer renderer)
            {
                if (renderer.GetComponentInParent<Canvas>(true) != null) return "canvas";
                var n = renderer.name.ToLowerInvariant();
                foreach (var word in new[] { "glass", "blur", "hud", "canopy" }) if (n.Contains(word)) return "name " + word;
                // Only when every material is see-through: a hull with one glass slot is still a hull.
                var materials = renderer.sharedMaterials.Where(m => m != null).ToArray();
                if (materials.Length == 0) return null;
                string last = null;
                foreach (var m in materials)
                {
                    var mn = (m.name + " " + (m.shader != null ? m.shader.name : "")).ToLowerInvariant();
                    last = new[] { "glass", "blur", "particle", "hud" }.FirstOrDefault(w => mn.Contains(w));
                    if (last == null) return null;
                }
                return "material " + last;
            }

            static string Path(Transform t, Transform root)
            {
                var parts = new List<string>();
                for (var p = t; p != null && p != root && parts.Count < 3; p = p.parent) parts.Insert(0, p.name);
                return string.Join("/", parts);
            }

            public string Name(RaycastHit hit) => hit.collider != null && names.TryGetValue(hit.collider, out var n) ? n : "?";

            public bool Ray(Vector3 from, Vector3 direction, float distance, out RaycastHit hit) =>
                physics.Raycast(from, direction, out hit, distance, Physics.AllLayers, QueryTriggerInteraction.Ignore);

            public RaycastHit[] RayAll(Vector3 from, Vector3 direction, float distance)
            {
                int n = physics.Raycast(from, direction, rayHits, distance, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                var hits = new RaycastHit[n];
                Array.Copy(rayHits, hits, n);
                return hits;
            }
            readonly RaycastHit[] rayHits = new RaycastHit[64];

            /// <summary>1 if a capsule of this radius from a to b touches any surface (names added), else 0.</summary>
            public int Touches(Vector3 a, Vector3 b, float radius, HashSet<string> hitNames)
            {
                int n = physics.OverlapCapsule(a, b, radius, overlap, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                bool any = n > 0;
                for (int i = 0; i < n; i++) hitNames.Add(names.TryGetValue(overlap[i], out var name) ? name : "?");
                // A thin capsule can sit wholly between two faces of a thick panel; a line both ways catches a crossing too.
                var d = b - a;
                if (!any && d.sqrMagnitude > 1e-6f)
                {
                    if (physics.Raycast(a, d.normalized, out var hit, d.magnitude, Physics.AllLayers, QueryTriggerInteraction.Ignore)
                        || physics.Raycast(b, -d.normalized, out hit, d.magnitude, Physics.AllLayers, QueryTriggerInteraction.Ignore))
                    {
                        any = true;
                        hitNames.Add(Name(hit));
                    }
                }
                return any ? 1 : 0;
            }

            public void Dispose()
            {
                Physics.queriesHitBackfaces = backfaces;
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (var m in meshes) if (m) UnityEngine.Object.DestroyImmediate(m);
            }
        }

        // ---- menus: measure or fit the seats already in the open scene ------------------------------------------

        static IEnumerable<GameObject> Vehicles()
        {
            foreach (var rootName in new[] { "Aircraft", "GroundVehicles" })
            {
                var fleet = GameObject.Find(rootName);
                if (fleet == null) continue;
                foreach (Transform vehicle in fleet.transform)
                    if (vehicle.GetComponentInChildren<BS.BSSeat>(true) != null) yield return vehicle.gameObject;
            }
        }

        static void Run(bool apply)
        {
            var text = new StringBuilder();
            text.AppendLine($"Seat legs {(apply ? "fitted" : "measured")} {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Seat metres: y up from the seat origin, z ahead; pelvis at y +0.04.");
            text.AppendLine("cushion = top of whatever is under the pelvis; back = backrest; default legs = the SDK default pose; feet/knees = the fitted pose.");
            int seats = 0, clear = 0, fixedSeats = 0;
            foreach (var vehicle in Vehicles().ToList())
            {
                foreach (var r in Fit(vehicle, apply, text))
                {
                    seats++;
                    if (r.crossings == 0) clear++;
                    if (r.defaultCrossings > 0 && r.crossings == 0) fixedSeats++;
                }
            }
            text.AppendLine($"{seats} seats: {clear} clear with the fitted legs; {fixedSeats} of them had legs through something with the default pose.");
            if (apply) EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/seat_legs.txt", text.ToString());
            Debug.Log($"[SeatLegs] {seats} seats, {clear} clear; Logs/seat_legs.txt");
        }

        // ---- pictures: a stand-in body on every seat, seen from the side through a cut in the hull, and from outside ----

        [MenuItem("FlightWorld/Flight/Test/Seat Leg Pictures")]
        public static void Pictures()
        {
            const string dir = "Logs/seat_legs";
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "*.jpg")) File.Delete(old);
            string only = File.Exists("Library/FlightWorld/seat_pictures_only.txt") ? File.ReadAllText("Library/FlightWorld/seat_pictures_only.txt").Trim() : "";
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color")) { hideFlags = HideFlags.HideAndDontSave };
            material.color = new Color(1f, 0.15f, 0.75f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(1f, 0.15f, 0.75f));
            var index = new StringBuilder();
            using (var robot = new BS.SeatPosePreview())
            {
                foreach (var vehicle in Vehicles().ToList())
                {
                    if (only.Length > 0 && !only.Split(',').Any(o => vehicle.name == o.Trim())) continue;
                    int n = 0;
                    foreach (var seat in vehicle.GetComponentsInChildren<BS.BSSeat>(true))
                    {
                        // The A320 cabin: one row is enough to judge.
                        if (n++ >= 6 && vehicle.name.Contains("A320")) break;
                        var body = Body(seat, robot, material);
                        try
                        {
                            var t = seat.transform;
                            var focus = t.position + t.up * 0.25f + t.forward * 0.25f;
                            string file = $"{dir}/{Safe(vehicle.name)}__{Safe(seat.name)}";
                            const float d = 4.5f;
                            // Through the hull: the near plane cuts everything nearer than 0.30 m to the seat's left of its centre.
                            Render(file + "_cut.jpg", focus - t.right * d, focus, t.up, d - 0.30f, 32f);
                            // From outside, low on the left: nothing should come out of the bottom.
                            Render(file + "_out.jpg", focus - t.right * 7f - t.up * 1.6f + t.forward * 0.5f, focus - t.up * 0.4f, t.up, 0.3f, 30f);
                            index.AppendLine(file);
                        }
                        finally { UnityEngine.Object.DestroyImmediate(body); }
                    }
                }
            }
            UnityEngine.Object.DestroyImmediate(material);
            File.WriteAllText(dir + "/index.txt", index.ToString());
        }

        static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

        /// <summary>Capsules for the torso, head, thighs, shins and feet where the app would put them for this seat's pose.</summary>
        static GameObject Body(BS.BSSeat seat, BS.SeatPosePreview robot, Material material)
        {
            var t = seat.transform;
            var pose = seat.Pose;
            var root = new GameObject("__SeatLegsBody") { hideFlags = HideFlags.HideAndDontSave };
            Vector3 P(Vector3 local) => t.position + t.rotation * local;
            void Limb(Vector3 a, Vector3 b, float radius)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.SetParent(root.transform, false);
                var wa = P(a); var wb = P(b);
                go.transform.position = (wa + wb) * 0.5f;
                go.transform.rotation = Quaternion.FromToRotation(Vector3.up, (wb - wa).normalized);
                float length = (wb - wa).magnitude + radius * 2f;
                go.transform.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f);
                go.GetComponent<Renderer>().sharedMaterial = material;
            }
            var pelvis = pose.pelvisPosition;
            Limb(pelvis, pelvis + new Vector3(0f, 0.50f, 0.04f), 0.13f);                      // torso
            Limb(pelvis + new Vector3(0f, 0.66f, 0.12f), pelvis + new Vector3(0f, 0.72f, 0.12f), 0.10f);   // head (eyes ~0.70 up, 0.14 ahead)
            foreach (var left in new[] { true, false })
            {
                Leg(robot, pose, left, out var hip, out var upper, out var lower, out var soleOffset, out var restFoot);
                var sole = left ? pose.leftFootPosition : pose.rightFootPosition;
                var hint = left ? pose.leftKneeHint : pose.rightKneeHint;
                Chain(hip, hint, sole, upper, lower, soleOffset, restFoot, out var knee, out var ankle, out var solved, out var toe);
                Limb(hip, knee, 0.07f);
                Limb(knee, ankle, 0.05f);
                Limb(solved + new Vector3(0f, 0.04f, -0.05f), toe, 0.045f);
            }
            return root;
        }

        static void Render(string file, Vector3 from, Vector3 at, Vector3 up, float near, float fov)
        {
            const int width = 960, height = 720;
            var go = new GameObject("__SeatLegsCamera") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.transform.position = from;
                cam.transform.LookAt(at, up);
                cam.fieldOfView = fov;
                cam.nearClipPlane = Mathf.Max(0.05f, near);
                cam.farClipPlane = 3000f;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                File.WriteAllBytes(file, tex.EncodeToJPG(85));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        [MenuItem("FlightWorld/Flight/Test/Probe Seat Legs")]
        public static void ProbeMenu() => Run(false);

        [MenuItem("FlightWorld/Flight/Fit Seat Legs In Scene")]
        public static void FitMenu()
        {
            Run(true);
            EditorSceneManager.SaveOpenScenes();
        }
    }
}
