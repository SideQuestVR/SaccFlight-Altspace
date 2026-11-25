using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Each vehicle of the built scene as a kit: one prefab that needs nothing else from this world, built as an asset
    /// bundle for the Quest and for Windows, so that a snippet can put the vehicle into anybody's world.
    ///
    /// In the scene a vehicle leans on four things outside itself: the fleet watch (the clock that sends it frames,
    /// steps and ticks, and the objects that follow the player's hands), the weapons object (which flies its missiles
    /// and bombs), its reset sign, and the point a player is sent to after a crash. A kit carries its own copy of each,
    /// with the lists cut down to the one vehicle, so several kits in one world run side by side without knowing of each
    /// other (a missile from one kit cannot lock on to a vehicle of another).
    ///
    /// The kit's origin is on the ground under the vehicle, facing the way the vehicle faces: a snippet's position is
    /// where the wheels stand. For the boat it is the water's surface (see BuildKitWater).
    ///
    ///   FlightWorld/Snippets/Build Kit Prefabs     the prefabs, in Assets/Flight/Kits (from the open, built scene)
    ///   FlightWorld/Snippets/Build Kit Bundles     Library/FlightWorld/kits/{android,windows}/<slug>, and the app's check
    /// Report: Logs/snippet_kits.txt. One kit only: write its slug to Library/FlightWorld/kit_only.txt first.
    /// </summary>
    public static class SnippetKits
    {
        public const string PrefabFolder = "Assets/Flight/Kits";
        public const string BundleRoot = "Library/FlightWorld/kits";
        public const string KitWaterPath = "Assets/Flight/Graphs/KitWater.asset";
        public const string NoticesPath = "Assets/Flight/THIRD-PARTY-NOTICES.md";
        const string LogPath = "Logs/snippet_kits.txt";
        const string OnlyPath = "Library/FlightWorld/kit_only.txt";

        /// <summary>How a vehicle is worked, for the panel of instructions beside its sign.</summary>
        public enum Kind { Plane, TiltThrust, Thrusters, Helicopter, Balloon, Boat, Car, Tank }

        public sealed class Kit
        {
            public readonly string Slug, Fleet, Vehicle, Name;
            public readonly Kind Kind;
            /// <summary>Extra lines for the panel: "gunners", "boost", "jump".</summary>
            public readonly string Extras;
            public Kit(string slug, string fleet, string vehicle, Kind kind, string name = null, string extras = "")
            {
                Slug = slug; Fleet = fleet; Vehicle = vehicle; Kind = kind; Name = name ?? vehicle; Extras = extras;
            }
            public string PrefabPath => $"{PrefabFolder}/{Slug}.prefab";
        }

        public static readonly Kit[] All =
        {
            new Kit("sacc-sf-1", "Aircraft", "SF-1 1", Kind.Plane, "SF-1"),
            new Kit("sacc-sf-2", "Aircraft", "SF-2", Kind.TiltThrust),
            new Kit("sacc-sb-1", "Aircraft", "SB-1", Kind.Plane),
            new Kit("sacc-sx-tr3", "Aircraft", "SX-TR3", Kind.Thrusters),
            new Kit("sacc-wasp-wx3", "Aircraft", "Wasp WX3", Kind.Plane),
            new Kit("sacc-stunt-plane", "Aircraft", "Stunt Plane", Kind.Plane),
            new Kit("sacc-grvr-1", "Aircraft", "GRVR-1", Kind.Thrusters),
            new Kit("sacc-sc-1", "Aircraft", "SC-1", Kind.TiltThrust),
            new Kit("sacc-a320", "Aircraft", "A320", Kind.Plane),
            new Kit("sacc-sh-1", "Aircraft", "SH-1", Kind.Helicopter, extras: "gunners"),
            new Kit("sacc-sh-2", "Aircraft", "SH-2", Kind.Helicopter, extras: "gunners"),
            new Kit("sacc-sh-3", "Aircraft", "SH-3", Kind.Helicopter, extras: "gunners"),
            new Kit("sacc-sd-1", "Aircraft", "SD-1", Kind.Helicopter),
            new Kit("sacc-balloon", "Aircraft", "Balloon", Kind.Balloon),
            new Kit("sacc-ss-1", "Aircraft", "SS-1", Kind.Boat, extras: "gunners"),
            new Kit("sacc-baron", "GroundVehicles", "Baron", Kind.Car),
            new Kit("sacc-iccarus", "GroundVehicles", "Iccarus", Kind.Car, extras: "boost jump"),
            new Kit("sacc-shoreline", "GroundVehicles", "Shoreline", Kind.Car, extras: "boost"),
            new Kit("sacc-thunder", "GroundVehicles", "Thunder", Kind.Car, extras: "gunners"),
            new Kit("sacc-tank", "GroundVehicles", "Tank", Kind.Tank, extras: "gunners"),
        };

        /// <summary>The panel beside a kit's sign: a world that takes the kit has none of this world's notice boards.</summary>
        public static (string title, string body) HowTo(Kit kit)
        {
            const string N = "\n";
            var t = new StringBuilder();
            bool flies = kit.Kind != Kind.Boat && kit.Kind != Kind.Car && kit.Kind != Kind.Tank;
            string seat = kit.Kind == Kind.Boat ? "helm seat" : flies ? "pilot's seat" : "driver's seat";
            t.Append($"Point at the {seat} and pull the trigger (or click it) to get in. Jump to get out. The other seats are for passengers." + N + N);
            if (flies || kit.Kind == Kind.Boat)
            {
                t.Append("<b>VR</b>  Left stick: up / down = throttle, left / right = rudder" + N);
                if (kit.Kind == Kind.Boat) t.Append("<b>Keyboard</b>  Shift / Ctrl = throttle    Q / E = rudder" + N);
                else
                {
                    if (kit.Kind == Kind.Balloon) t.Append("Right stick: pushes it ahead, astern and sideways" + N);
                    else t.Append("Right stick: back = nose up, forward = nose down, left / right = roll" + N);
                    if (kit.Kind == Kind.Plane || kit.Kind == Kind.TiltThrust || kit.Kind == Kind.Thrusters)
                        t.Append("Left trigger: brakes    Click the right stick: landing gear    Click the left stick: flaps" + N);
                    if (kit.Kind == Kind.TiltThrust) t.Append("Left grip + left stick: tilt the thrust up or forward" + N);
                    if (kit.Kind == Kind.Thrusters) t.Append("Left grip + left stick: thrusters ahead, astern and sideways" + N);
                    t.Append("<b>Keyboard</b>  Shift / Ctrl = throttle    S / W = nose up / down    A / D = roll    Q / E = rudder" + N);
                    if (kit.Kind == Kind.Plane) t.Append("B = brakes    G = gear    V = flaps" + N);
                    if (kit.Kind == Kind.TiltThrust) t.Append("B = brakes    G = gear    V = flaps    R / F = tilt the thrust up / forward" + N);
                    if (kit.Kind == Kind.Thrusters) t.Append("B = brakes    G = gear    I K J L U O = thrusters" + N);
                    if (kit.Kind == Kind.Balloon) t.Append("I K J L = ahead, astern, sideways" + N);
                    t.Append(N);
                    if (kit.Kind == Kind.Plane) t.Append("Full throttle, keep straight, ease the nose up as the speed builds." + N);
                    if (kit.Kind == Kind.TiltThrust) t.Append("Tilt the thrust up to lift off, forward to fly on the wings. Or take off along a runway." + N);
                    if (kit.Kind == Kind.Thrusters) t.Append("Raise the throttle to lift off; the thrusters push it along." + N);
                    if (kit.Kind == Kind.Helicopter) t.Append("Raise the throttle and it lifts; tip the nose down to fly forward." + N);
                    if (kit.Kind == Kind.Balloon) t.Append("Raise the throttle and it lifts. It turns slowly." + N);
                    t.Append("<b>ADVANCED CONTROLS</b> (the blue button, VR): grip the cockpit's stick and throttle with your hands;" + N +
                             "a thumbstick picks a function or weapon on its dial, and that side's trigger works it." + N);
                }
            }
            else
            {
                if (kit.Kind == Kind.Tank) t.Append("<b>VR</b>  The left and right sticks drive the left and right tracks" + N);
                else t.Append("<b>VR</b>  Right trigger (or right stick forward) = accelerate    Either stick sideways = steer" + N +
                              "Left trigger (or a stick back) = brake; hold it at a standstill to reverse    Left grip = handbrake" + N);
                t.Append("Click the right stick = horn    Click the left stick = headlights    X = back on your wheels" + N);
                if (kit.Extras.Contains("boost")) t.Append("B = boost" + (kit.Extras.Contains("jump") ? "    Y = jump    Right grip + sticks = turn in the air" : "") + N);
                t.Append("<b>Keyboard</b>  W / S = accelerate / brake and reverse    A / D = steer    Space = handbrake" + N);
            }
            if (kit.Extras.Contains("gunners"))
                t.Append(N + "<b>Gunner seats</b>  " + (kit.Kind == Kind.Tank ? "The turret turns with the right thumbstick." : kit.Kind == Kind.Boat
                             ? "The guns point where your right hand points; the back gun turns with the right thumbstick."
                             : "The gun points where your right hand points.") + " The triggers fire. Click a thumbstick for the searchlight." + N);
            t.Append(N + "Crash, and it comes back here after a few seconds. The red RESET board brings it back when nobody is in it." +
                     (flies ? N + "On the ground near here it refuels, rearms and repairs itself." : ""));
            string title = flies ? "HOW TO FLY THE " : kit.Kind == Kind.Boat ? "HOW TO SAIL THE " : "HOW TO DRIVE THE ";
            return (title + kit.Name.ToUpperInvariant(), t.ToString());
        }

        /// <summary>The panel itself: beside the reset board, on the side its label is on.</summary>
        static void HowToPanel(GameObject sign, Kit kit)
        {
            const float width = 2.5f, height = 1.6f, centreX = -2.2f, centreY = 1.35f;
            var (title, body) = HowTo(kit);
            var holder = FleetParts.Child(sign, "How To");
            holder.transform.localPosition = new Vector3(centreX, 0f, 0f);
            foreach (float x in new[] { -width * 0.42f, width * 0.42f })
            {
                var leg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                leg.name = "Leg";
                leg.transform.SetParent(holder.transform, false);
                leg.transform.localPosition = new Vector3(x, (centreY - height * 0.5f + 0.1f) * 0.5f, 0.06f);
                leg.transform.localScale = new Vector3(0.08f, centreY - height * 0.5f + 0.1f, 0.08f);
                leg.GetComponent<MeshRenderer>().sharedMaterial = FleetParts.FlatMaterial("SignLeg", new Color(0.32f, 0.33f, 0.36f));
            }
            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "Panel";
            panel.transform.SetParent(holder.transform, false);
            panel.transform.localPosition = new Vector3(0f, centreY, 0.04f);
            panel.transform.localScale = new Vector3(width, height, 0.08f);
            panel.GetComponent<MeshRenderer>().sharedMaterial = FleetParts.FlatMaterial("BoardPanel", new Color(0.10f, 0.14f, 0.22f));
            var heading = FleetParts.Child(holder, "Title").AddComponent<TMPro.TextMeshPro>();
            heading.transform.localPosition = new Vector3(0f, centreY + height * 0.5f - 0.14f, -0.01f);
            heading.rectTransform.sizeDelta = new Vector2(width - 0.16f, 0.2f);
            heading.fontSize = 1.3f;
            heading.fontStyle = TMPro.FontStyles.Bold;
            heading.alignment = TMPro.TextAlignmentOptions.Center;
            heading.color = new Color(1f, 0.78f, 0.30f);
            heading.text = title;
            var text = FleetParts.Child(holder, "Text").AddComponent<TMPro.TextMeshPro>();
            text.transform.localPosition = new Vector3(0f, centreY - 0.12f, -0.01f);
            text.rectTransform.sizeDelta = new Vector2(width - 0.16f, height - 0.34f);
            text.fontSize = 0.72f;
            text.enableAutoSizing = true;
            text.fontSizeMin = 0.3f;
            text.fontSizeMax = 0.72f;
            text.overflowMode = TMPro.TextOverflowModes.Truncate;
            text.enableWordWrapping = true;
            text.richText = true;
            text.alignment = TMPro.TextAlignmentOptions.TopLeft;
            text.color = new Color(0.93f, 0.95f, 0.98f);
            text.text = body;
        }

        public static IEnumerable<Kit> Chosen()
        {
            string only = File.Exists(OnlyPath) ? File.ReadAllText(OnlyPath).Trim() : "";
            if (only.Length == 0) return All;
            var names = only.Split(new[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            return All.Where(k => names.Contains(k.Slug));
        }

        // ------------------------------------------------------------------ the boat's water level

        /// <summary>
        /// The boat floats on a level it is given (WaterLevel, a number set when the scene is built). In a kit the water
        /// is wherever the kit was put: this graph, on the kit's root, sets it when the kit starts, from the root's height.
        /// </summary>
        public static GraphWriter BuildKitWater()
        {
            var g = new GraphWriter();
            g.On(new Start(), _ =>
                g.SetObjVar("WaterLevel", g.Y(g.Get(typeof(Transform), "position")), g.ObjVar("Boat", typeof(GameObject)), typeof(float)));
            return g;
        }

        // ------------------------------------------------------------------ prefabs

        [MenuItem("FlightWorld/Snippets/Build Kit Prefabs")]
        public static void BuildPrefabs()
        {
            var report = new StringBuilder();
            try
            {
                BuildKitWater().Save(KitWaterPath, report);
                AssetDatabase.SaveAssets();
                Directory.CreateDirectory(PrefabFolder);
                foreach (var kit in Chosen())
                {
                    try { BuildPrefab(kit, report); }
                    catch (Exception e) { report.AppendLine($"FAILED {kit.Slug}: {e}"); }
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            catch (Exception e) { report.AppendLine("FAILED: " + e); }
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath, report.ToString());
        }

        static Type T(string name) => GraphTypes.Find(name);

        static string IdOf(Component objectId) => new SerializedObject(objectId).FindProperty("Id").stringValue;

        static void SetId(Component objectId, string id)
        {
            var so = new SerializedObject(objectId);
            so.FindProperty("Id").stringValue = id;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>A copy of a scene object in the stage, standing where the original stands; `map` learns original -> copy for every object and component.</summary>
        static GameObject Copy(GameObject original, Scene stage, Dictionary<UnityEngine.Object, UnityEngine.Object> map)
        {
            var copy = UnityEngine.Object.Instantiate(original);
            copy.name = original.name;
            SceneManager.MoveGameObjectToScene(copy, stage);
            copy.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
            var from = original.GetComponentsInChildren<Transform>(true);
            var to = copy.GetComponentsInChildren<Transform>(true);
            if (from.Length != to.Length) throw new Exception($"copy of {original.name} has {to.Length} objects, the original {from.Length}");
            for (int i = 0; i < from.Length; i++)
            {
                map[from[i].gameObject] = to[i].gameObject;
                var a = from[i].GetComponents<Component>();
                var b = to[i].GetComponents<Component>();
                if (a.Length != b.Length) throw new Exception($"copy of {from[i].name} has {b.Length} components, the original {a.Length}");
                for (int c = 0; c < a.Length; c++)
                    if (a[c] != null && b[c] != null) map[a[c]] = b[c];
            }
            return copy;
        }

        /// <summary>The vehicle's solid shape (its colliders; what is drawn, for one without any), in `frame`'s axes.</summary>
        static Bounds LocalBounds(GameObject vehicle, Matrix4x4 toFrame)
        {
            bool any = false;
            var bounds = new Bounds();
            void Add(Bounds b)
            {
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = toFrame.MultiplyPoint3x4(corner);
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            Physics.SyncTransforms();
            foreach (var c in vehicle.GetComponentsInChildren<Collider>(false))
                if (c.enabled && !c.isTrigger && !(c is WheelCollider)) Add(c.bounds);
            foreach (var w in vehicle.GetComponentsInChildren<WheelCollider>(false))
                Add(new Bounds(w.transform.position - w.transform.up * w.suspensionDistance * 0.5f, Vector3.one * w.radius * 2f));
            if (!any || bounds.size.magnitude < 1f)
                foreach (var r in vehicle.GetComponentsInChildren<MeshRenderer>(false))
                    if (r.enabled) Add(r.bounds);
            return bounds;
        }

        static void BuildPrefab(Kit kit, StringBuilder report)
        {
            var fleet = GameObject.Find(kit.Fleet) ?? throw new Exception($"no '{kit.Fleet}' in the scene: build the fleet first");
            var source = fleet.transform.Find(kit.Vehicle) ?? throw new Exception($"no '{kit.Vehicle}' under '{kit.Fleet}'");
            var sourceVars = source.GetComponent<Variables>().declarations;
            var sourceBoard = (GameObject)sourceVars.Get("ResetSign");
            var sourceSign = sourceBoard.transform.parent.gameObject;
            var sourceWatch = GameObject.Find("FleetWatch") ?? throw new Exception("no FleetWatch in the scene");
            var sourceWeapons = GameObject.Find("WeaponsWorld") ?? throw new Exception("no WeaponsWorld in the scene");
            bool ticked = sourceVars.IsDefined("IsBoat");                    // on the aircraft graph (a boat too)
            bool boat = ticked && sourceVars.Get("IsBoat") is bool isBoat && isBoat;
            float ground = boat ? Convert.ToSingle(sourceVars.Get("WaterLevel")) : sourceSign.transform.position.y;
            var origin = new Vector3(source.position.x, ground, source.position.z);
            var facing = Quaternion.Euler(0f, source.eulerAngles.y, 0f);

            var stage = EditorSceneManager.NewPreviewScene();
            try
            {
                var map = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
                var root = new GameObject(kit.Slug);
                SceneManager.MoveGameObjectToScene(root, stage);
                root.transform.SetPositionAndRotation(origin, facing);

                var vehicle = Copy(source.gameObject, stage, map);
                var sign = Copy(sourceSign, stage, map);
                var watch = Copy(sourceWatch, stage, map);
                var weapons = Copy(sourceWeapons, stage, map);
                foreach (var part in new[] { vehicle, sign, watch, weapons }) part.transform.SetParent(root.transform, true);
                watch.transform.localPosition = Vector3.zero;
                weapons.transform.localPosition = Vector3.zero;
                // from here on the kit stands at the origin, unturned: what is measured and placed below is in its own axes
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                sign.name = "Reset " + kit.Name;
                var signLabel = sign.transform.Find("Label")?.GetComponent<TMPro.TextMeshPro>();
                if (signLabel != null) signLabel.text = "RESET\n" + kit.Name;

                // ---- the boat: its sign on a pontoon of its own beside it, and a sea of its own to ignore ----
                if (boat)
                {
                    var hull = LocalBounds(vehicle, Matrix4x4.identity);
                    const float deck = 0.45f;
                    var beside = new Vector3(hull.min.x - 2.2f, 0f, hull.center.z);
                    var pontoon = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pontoon.name = "Pontoon";
                    pontoon.transform.SetParent(root.transform, false);
                    pontoon.transform.localPosition = beside + new Vector3(0f, deck - 0.4f, 0f);
                    pontoon.transform.localScale = new Vector3(3f, 0.8f, 5f);
                    pontoon.GetComponent<MeshRenderer>().sharedMaterial = FleetParts.FlatMaterial("Pontoon", new Color(0.45f, 0.40f, 0.33f));
                    sign.transform.localPosition = beside + new Vector3(-0.9f, deck, -1.6f);
                    sign.transform.localRotation = Quaternion.identity;
                    var sea = new GameObject("Sea");
                    sea.transform.SetParent(root.transform, false);
                    sea.transform.localPosition = new Vector3(0f, -3f, 0f);
                    sea.layer = 2;                                               // Ignore Raycast
                    var seaBox = sea.AddComponent<BoxCollider>();
                    seaBox.isTrigger = true;
                    seaBox.size = Vector3.one * 0.01f;
                    map[(UnityEngine.Object)sourceVars.Get("SeaCollider")] = seaBox;
                    var water = root.AddComponent<Variables>();
                    water.declarations.Set("Boat", vehicle);
                    root.AddComponent<ScriptMachine>().nest.SwitchToMacro(AssetDatabase.LoadAssetAtPath<ScriptGraphAsset>(KitWaterPath));
                }

                HowToPanel(sign, kit);

                // ---- where a player lands after a crash: in front of the sign ----
                var respawn = new GameObject("Respawn");
                respawn.transform.SetParent(root.transform, false);
                respawn.transform.SetPositionAndRotation(sign.transform.position + sign.transform.rotation * new Vector3(0f, 0.1f, -2f), sign.transform.rotation);
                var sourceRespawn = (Transform)sourceVars.Get("RespawnPoint");
                map[sourceRespawn] = respawn.transform;
                map[sourceRespawn.gameObject] = respawn;

                // ---- no catapult in a kit: the vehicle's catapult settings point at stand-ins that do nothing ----
                var vars = vehicle.GetComponent<Variables>().declarations;
                if (vars.IsDefined("HasCatapult") && vars.Get("HasCatapult") is bool catapult && catapult)
                {
                    vars.Set("HasCatapult", false);
                    // (and its place on the dial is one that cannot be picked)
                    foreach (var side in new[] { "L", "R" })
                        if (vars.IsDefined(side + "CatapultSlot") && Convert.ToSingle(vars.Get(side + "CatapultSlot")) is float slot && slot >= 0f)
                        {
                            if (vars.Get(side + "DialValid") is IList valid && slot < valid.Count) valid[(int)slot] = false;
                            vars.Set(side + "CatapultSlot", -1f);
                        }
                    var unused = vehicle.transform.Find("Unused")?.gameObject ?? FleetParts.Child(vehicle, "Unused");
                    map[(UnityEngine.Object)sourceVars.Get("Catapult")] = FleetParts.Child(unused, "Catapult").transform;
                    map[(UnityEngine.Object)sourceVars.Get("CatapultAnimator")] = FleetParts.Child(unused, "CatapultAnimator").AddComponent<Animator>();
                }

                // ---- every variable that pointed at the scene points at the kit's own copy ----
                foreach (var holder in root.GetComponentsInChildren<Variables>(true))
                    foreach (var d in holder.declarations.ToList())
                    {
                        if (d.value is UnityEngine.Object o && !ReferenceEquals(o, null) && map.TryGetValue(o, out var mine)) holder.declarations.Set(d.name, mine);
                        else if (d.value is IList list && !(d.value is Array))
                            for (int i = 0; i < list.Count; i++)
                                if (list[i] is UnityEngine.Object item && !ReferenceEquals(item, null) && map.TryGetValue(item, out var mineItem)) list[i] = mineItem;
                    }

                // ---- the kit's own clock and weapons: lists of one ----
                var clock = watch.GetComponent<Variables>().declarations;
                clock.Set("Vehicles", new AotList { vehicle });
                clock.Set("TickVehicles", ticked ? new AotList { vehicle } : new AotList());
                clock.Set("BusyVehicles", boat ? new AotList { vehicle } : new AotList());
                clock.Set("TickCount", ticked ? 1f : 0f);                    // (none: the clock never looks in the empty list)
                clock.Set("PilotVehicle", watch);
                clock.Set("HasPilotVehicle", false);
                clock.Set("Gunner", watch);
                clock.Set("HasGunner", false);
                clock.Set("AdvancedControls", false);
                clock.Set("AdvancedButton", watch);
                clock.Set("AdvancedLabel", null);
                if (ticked) AdvancedButton(sign, clock);
                // the snippet's advanced-controls and advanced-button settings, asked of the page when the kit starts
                // (cars and the tank have no advanced controls: their watch asks nothing)
                clock.Set("KitName", ticked ? kit.Slug : "");
                if (!ticked) clock.Set("AdvancedLabelObject", null);
                var world = weapons.GetComponent<Variables>().declarations;
                world.Set("LiveShots", new AotList());
                world.Set("LiveCount", 0f);
                world.Set("Aircraft", ticked ? new AotList { vehicle } : new AotList());
                if (vars.IsDefined("TargetIndex")) vars.Set("TargetIndex", 0f);

                // ---- network ids: the scene's, marked as a kit's (the same in every build, and never the world's own) ----
                var idType = T("BS.BSObjectId");
                var ids = new HashSet<string>();
                foreach (var pair in map)
                    if (pair.Key != null && pair.Key.GetType() == idType && pair.Value is Component copyId && copyId.transform.IsChildOf(vehicle.transform))
                        SetId(copyId, "k" + IdOf((Component)pair.Key));
                foreach (var id in root.GetComponentsInChildren(idType, true))
                {
                    if (!id.transform.IsChildOf(vehicle.transform))
                    {
                        string path = id.name.Replace(' ', '-');
                        for (var t = id.transform.parent; t != null && t != root.transform; t = t.parent) path = t.name.Replace(' ', '-') + "-" + path;
                        SetId(id, $"kfw-{kit.Slug}-{path}");
                    }
                    string value = IdOf(id);
                    if (string.IsNullOrEmpty(value) || !ids.Add(value)) throw new Exception($"network id '{value}' on {id.name} is empty or used twice");
                }

                // ---- the body starts frozen ----
                // In the app a snippet's kit can arrive before the world it is put in has its ground (seen on the Quest,
                // 2026-11-25: kits placed at 12:45:34-46, the world loaded at 12:45:49), and before the vehicle's graph
                // has started and frozen it (its Start does). A free body then fell through the world; the app took
                // the fallen place as the vehicle's home and "respawned" it there for good, below the world. Saved
                // frozen, it waits where it was put; its graph releases it when somebody takes the controls (Wake), as
                // it does in this world.
                var body = vehicle.GetComponent<Rigidbody>();
                body.constraints = RigidbodyConstraints.FreezeAll;
                body.interpolation = RigidbodyInterpolation.None;
                var loose = root.GetComponentsInChildren<Rigidbody>(false).Where(b => b != body && !b.isKinematic && b.constraints != RigidbodyConstraints.FreezeAll).ToList();
                if (loose.Count > 0) report.AppendLine($"   {kit.Slug}: other free bodies that could fall: {string.Join(", ", loose.Select(b => b.name))}");

                // ---- canvases: no solid box ----
                // The kit loader (BSKitItem) gives every world-space canvas without a BoxCollider a solid one the size of
                // its rect, so that kit UI can be clicked. The vehicles' head-up displays, gauges and dials are
                // display-only canvases inside the vehicle's body: as solid boxes they became part of its physics (the
                // Iccarus's HUD, on while driving, propped the car on its nose with the driven wheels in the air) and
                // were in the way of the seated players' bodies and hands. A disabled BoxCollider of their own stops it.
                int canvases = 0;
                foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
                {
                    if (canvas.GetComponent<BoxCollider>() != null) { canvas.GetComponent<BoxCollider>().enabled = false; canvases++; continue; }
                    var box = canvas.gameObject.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    box.size = Vector3.zero;
                    box.enabled = false;
                    canvases++;
                }
                report.AppendLine($"   {kit.Slug}: {canvases} canvases given a switched-off box collider");

                // ---- nothing may still point outside the kit, and nothing in it may be a script the app does not have ----
                int outside = 0, foreign = 0, missing = 0;
                foreach (var holder in root.GetComponentsInChildren<Variables>(true))
                    foreach (var d in holder.declarations)
                    {
                        IEnumerable<object> values = d.value is IList l && !(d.value is Array) ? l.Cast<object>() : new[] { d.value };
                        foreach (var value in values)
                        {
                            if (!(value is UnityEngine.Object o) || o == null || EditorUtility.IsPersistent(o)) continue;
                            var t = o is GameObject go ? go.transform : o is Component c ? c.transform : null;
                            if (t == null || t.IsChildOf(root.transform)) continue;
                            outside++;
                            report.AppendLine($"   OUTSIDE: {holder.name}.{d.name} -> {t.name}");
                        }
                    }
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    foreach (var c in t.GetComponents<Component>())
                    {
                        if (c == null) { missing++; continue; }
                        string assembly = c.GetType().Assembly.GetName().Name;
                        if (assembly.StartsWith("Assembly-CSharp"))
                        {
                            foreign++;
                            report.AppendLine($"   PROJECT SCRIPT: {c.GetType().FullName} on {t.name}");
                        }
                        var so = new SerializedObject(c);
                        var p = so.GetIterator();
                        while (p.NextVisible(true))
                        {
                            if (p.propertyType != SerializedPropertyType.ObjectReference || p.objectReferenceValue == null) continue;
                            var target = p.objectReferenceValue;
                            if (EditorUtility.IsPersistent(target)) continue;
                            var tt = target is GameObject go ? go.transform : target is Component tc ? tc.transform : null;
                            if (tt == null || tt.IsChildOf(root.transform)) continue;
                            outside++;
                            report.AppendLine($"   OUTSIDE: {t.name} {c.GetType().Name}.{p.propertyPath} -> {tt.name}");
                        }
                    }
                if (outside + foreign + missing > 0)
                    throw new Exception($"{outside} references outside the kit, {foreign} project scripts, {missing} missing scripts");

                var bounds = LocalBounds(vehicle, Matrix4x4.identity);
                PrefabUtility.SaveAsPrefabAsset(root, kit.PrefabPath, out bool saved);
                if (!saved) throw new Exception("the prefab was not saved");
                File.WriteAllText($"{PrefabFolder}/{kit.Slug}.txt",
                    FormattableString.Invariant($"{bounds.center.x:F2} {bounds.center.y:F2} {bounds.center.z:F2} {bounds.size.x:F2} {bounds.size.y:F2} {bounds.size.z:F2}"));
                report.AppendLine($"ok {kit.Slug}: {root.GetComponentsInChildren<Transform>(true).Length} objects, {ids.Count} network ids, vehicle at {vehicle.transform.localPosition:F2}, " +
                                  $"sign at {sign.transform.localPosition:F2}, size {bounds.size:F1} centre {bounds.center:F1}{(boat ? ", boat" : "")}{(ticked ? "" : ", ground vehicle")}");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(stage);
            }
        }

        /// <summary>The switch for Sacc's own VR controls, as on the world's How To Fly board: a small panel beside the reset sign.</summary>
        static void AdvancedButton(GameObject sign, VariableDeclarations clock)
        {
            var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
            button.name = "Advanced Controls Button";
            button.transform.SetParent(sign.transform, false);
            button.transform.localPosition = new Vector3(1.42f, 1.4f, 0f);
            button.transform.localScale = new Vector3(1.0f, 0.9f, 0.08f);
            button.GetComponent<MeshRenderer>().sharedMaterial = FleetParts.FlatMaterial("AdvancedButton", new Color(0.16f, 0.30f, 0.46f));
            button.layer = 5;
            var range = button.AddComponent(T("BS.BSPointerRange"));
            var so = new SerializedObject(range);
            so.FindProperty("distance").floatValue = 6f;
            so.ApplyModifiedPropertiesWithoutUndo();
            var label = FleetParts.Child(sign, "Advanced Controls Label").AddComponent<TMPro.TextMeshPro>();
            label.transform.localPosition = new Vector3(1.42f, 1.4f, -0.05f);
            label.rectTransform.sizeDelta = new Vector2(0.94f, 0.84f);
            label.fontSize = 1.5f;
            label.fontStyle = TMPro.FontStyles.Bold;
            label.alignment = TMPro.TextAlignmentOptions.Center;
            label.richText = true;
            label.color = Color.white;
            label.text = FleetParts.AdvancedOff;
            clock.Set("AdvancedButton", button);
            clock.Set("AdvancedLabel", label);
            clock.Set("AdvancedLabelObject", label.gameObject);
        }

        // ------------------------------------------------------------------ photographs

        /// <summary>
        /// A picture of each vehicle of the scene, alone: the rest of the fleet and the signs are not drawn while it is
        /// taken. Take them in Play mode: the vehicles then look as they do parked in the world (in the editor every
        /// afterburner flame shows), and each is stood on the one stretch of apron without a building's shadow painted
        /// on it for its picture. Nothing is switched off or moved by physics: a vehicle switched off and on again in
        /// Play mode forgets its brakes and its animator's state. Library/FlightWorld/kits/photos/&lt;slug&gt;.jpg, 1280 x 720.
        /// </summary>
        [MenuItem("FlightWorld/Snippets/Take Photos")]
        public static void Photos()
        {
            var report = new StringBuilder();
            const int width = 1280, height = 720;
            const float fov = 32f;
            Directory.CreateDirectory($"{BundleRoot}/photos");
            var hidden = new List<Renderer>();
            var go = new GameObject("__KitPhotoCamera") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.fieldOfView = fov;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 22000f;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.targetTexture = rt;
                var sun = RenderSettings.sun != null ? RenderSettings.sun
                    : UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional && l.isActiveAndEnabled);
                var studio = GameObject.Find("Aircraft")?.transform.Find("SB-1");
                foreach (var kit in Chosen())
                {
                    var source = GameObject.Find(kit.Fleet)?.transform.Find(kit.Vehicle);
                    if (source == null) { report.AppendLine($"FAILED {kit.Slug}: not in the scene"); continue; }
                    foreach (var fleetName in new[] { "Aircraft", "GroundVehicles" })
                        foreach (var drawn in GameObject.Find(fleetName).GetComponentsInChildren<Renderer>(false))
                            if (!drawn.transform.IsChildOf(source) && !drawn.forceRenderingOff) { drawn.forceRenderingOff = true; hidden.Add(drawn); }
                    var sourceVars = source.GetComponent<Variables>().declarations;
                    bool boat = sourceVars.IsDefined("IsBoat") && sourceVars.Get("IsBoat") is bool isBoat && isBoat;
                    float ground = boat ? Convert.ToSingle(sourceVars.Get("WaterLevel")) : ((GameObject)sourceVars.Get("ResetSign")).transform.parent.position.y;
                    var parked = source.position;
                    bool moved = Application.isPlaying && !boat && studio != null && studio != source;
                    if (moved) source.position = new Vector3(studio.position.x, parked.y, studio.position.z);
                    var frame = Matrix4x4.TRS(new Vector3(source.position.x, ground, source.position.z), Quaternion.Euler(0f, source.eulerAngles.y, 0f), Vector3.one);
                    var n = File.ReadAllText($"{PrefabFolder}/{kit.Slug}.txt").Split(' ').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var bounds = new Bounds(new Vector3(n[0], n[1], n[2]), new Vector3(n[3], n[4], n[5]));
                    var centre = frame.MultiplyPoint3x4(bounds.center);
                    Vector3 best = Vector3.zero;
                    float bestLit = float.NegativeInfinity;
                    foreach (float side in new[] { -1f, 1f })
                    {
                        var towards = frame.MultiplyVector(new Vector3(side * 0.85f, 0.38f, 1f).normalized);
                        float lit = sun != null ? Vector3.Dot(towards, -sun.transform.forward) : -side;
                        if (lit > bestLit) { bestLit = lit; best = towards; }
                    }
                    // as near as keeps every corner of the vehicle's box in the picture, with a margin
                    cam.transform.position = centre + best * 10f;
                    cam.transform.LookAt(centre);
                    float tanV = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), tanH = tanV * width / height, reach = 1f;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = frame.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))) - centre;
                        float across = Mathf.Abs(Vector3.Dot(corner, cam.transform.right)), up = Mathf.Abs(Vector3.Dot(corner, cam.transform.up));
                        float towardsCamera = Vector3.Dot(corner, best);
                        reach = Mathf.Max(reach, Mathf.Max(across / tanH, up / tanV) + towardsCamera);
                    }
                    reach *= 1.12f;
                    cam.transform.position = centre + best * reach;
                    cam.transform.LookAt(centre);
                    cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes($"{BundleRoot}/photos/{kit.Slug}.jpg", tex.EncodeToJPG(90));
                    report.AppendLine($"photo {kit.Slug}: size {bounds.size:F1}, camera {reach:F0} m away{(moved ? "" : ", where it is parked")}");
                    if (moved) source.position = parked;
                    foreach (var other in hidden) if (other != null) other.forceRenderingOff = false;
                    hidden.Clear();
                }
                cam.targetTexture = null;
            }
            catch (Exception e) { report.AppendLine("FAILED: " + e); }
            finally
            {
                foreach (var other in hidden) if (other != null) other.forceRenderingOff = false;
                UnityEngine.Object.DestroyImmediate(go);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath.Replace(".txt", "_photos.txt"), report.ToString());
        }

        // ------------------------------------------------------------------ bundles

        static readonly (BuildTarget target, string folder)[] Targets =
        {
            (BuildTarget.Android, "android"),
            (BuildTarget.StandaloneWindows64, "windows"),
        };

        [MenuItem("FlightWorld/Snippets/Build Kit Bundles")]
        public static void BuildBundles()
        {
            var report = new StringBuilder();
            try
            {
                // One build per kit: each bundle then holds everything its vehicle needs (what two kits share is in both),
                // and the licence notices can go into every one (an asset may be named for one bundle only in a build).
                var kits = Chosen().Where(k => File.Exists(k.PrefabPath)).ToList();
                foreach (var (target, folder) in Targets)
                {
                    string output = $"{BundleRoot}/{folder}";
                    Directory.CreateDirectory(output);
                    foreach (var kit in kits)
                    {
                        var build = new AssetBundleBuild { assetBundleName = kit.Slug, assetNames = new[] { kit.PrefabPath, NoticesPath } };
                        var manifest = BuildPipeline.BuildAssetBundles(output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, target);
                        if (manifest == null) { report.AppendLine($"FAILED {folder} {kit.Slug}: no bundle"); continue; }
                        string file = $"{output}/{kit.Slug}";
                        report.AppendLine($"{folder} {kit.Slug}: {new FileInfo(file).Length / 1048576f:F1} MB; check: {Check(file)}");
                        File.WriteAllText(LogPath.Replace(".txt", "_bundles.txt"), report.ToString());
                    }
                }
            }
            catch (Exception e) { report.AppendLine("FAILED: " + e); }
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath.Replace(".txt", "_bundles.txt"), report.ToString());
        }

        /// <summary>The app's own check of a kit bundle before it loads one (BS.Security.BundleChecker, kit rules).</summary>
        static string Check(string file)
        {
            try
            {
                const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                                                           System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var checker = T("BS.Security.BundleChecker");
                var options = T("BS.Security.BundleCheckOptions");
                var kitOptions = options.GetMethod("Kit", any).Invoke(null, null);
                var check = checker.GetMethods(any).First(m => m.Name == "Check" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(byte[]));
                var verdict = check.Invoke(null, new[] { File.ReadAllBytes(file), kitOptions, null });
                var text = new StringBuilder();
                foreach (var member in verdict.GetType().GetFields(any))
                {
                    var value = member.GetValue(verdict);
                    if (value is IEnumerable many && !(value is string)) text.Append($"{member.Name}=[{string.Join("; ", many.Cast<object>().Take(12))}] ");
                    else text.Append($"{member.Name}={value} ");
                }
                return text.ToString();
            }
            catch (Exception e) { return "check failed to run: " + (e.InnerException ?? e).Message; }
        }
    }
}
