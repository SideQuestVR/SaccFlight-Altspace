using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Things the apron needs besides the vehicles themselves: the notice boards at the arrival point, the
    /// reset sign behind each parking spot, and small helpers shared by the builders.
    /// </summary>
    public static class FleetParts
    {
        const string MaterialDir = "Assets/Flight/Materials";
        const string AudioDir = "Assets/Flight/Audio";

        const string HowToFly =
            "<b>Getting in and out</b>\n" +
            "Point at a pilot seat and pull the trigger (or click it). Jump to get out.\n" +
            "Passengers can take any of the other seats.\n\n" +
            "<b>VR controllers</b>\n" +
            "Left stick: up / down = throttle, left / right = rudder (it steers on the ground)\n" +
            "Right stick: back = nose up, forward = nose down, left / right = roll\n" +
            "Left trigger: brakes    Left grip + left stick: tilt the thrust (SC-1, SF-2),\n" +
            "or fire the thrusters ahead / astern / sideways (GRVR-1, SX-TR3)\n" +
            "Click the right stick: landing gear    Click the left stick: flaps\n\n" +
            "<b>Advanced controls</b> (the button beside this board, VR only)\n" +
            "Hold the right grip and move your hand to work the cockpit's stick: tilt to pitch and roll, twist to yaw.\n" +
            "Double-tap the grip to keep hold of it. Hold the left grip and push the throttle forward or pull it back\n" +
            "(helicopters: raise or lower it). A thumbstick picks a function on its dial display; that side's trigger\n" +
            "works it: gear, flaps, canopy, hook, smoke, cruise, altitude hold and switches toggle; brakes, gun, flares and bombs\n" +
            "while held; thrust tilt, thrusters and the cruise speed by moving your hand. Engine: one pull starts it, a\n" +
            "double pull stops it. Missiles lock on a crewed aircraft ahead (a tone), then pull. Guided bomb: look at the\n" +
            "target, pull to lock, double pull to fire. A hook catches the cables across the runway ends.\n\n" +
            "<b>Keyboard</b>\n" +
            "Shift / Ctrl = throttle    S / W = nose up / down    A / D = roll\n" +
            "Q / E = rudder    B = brakes    G = gear    V = flaps\n" +
            "R / F = tilt the thrust up / forward    I K J L U O = thrusters (GRVR-1, SX-TR3)\n\n" +
            "<b>Taking off</b>\n" +
            "Aeroplanes: full throttle, keep straight, ease the nose up as the speed builds.\n" +
            "Helicopters, the drone and the balloon: raise the throttle and they lift.\n" +
            "The balloon: the right stick (or I K J L) pushes it ahead, astern and sideways;\n" +
            "the left stick sideways (or Q / E) turns it, slowly.\n\n" +
            "<b>The airliner</b>\n" +
            "The blue AIRLINER sign takes you to its nose. Point up at the captain's window to take the left seat;\n" +
            "passengers point at the front of the cabin. Flaps out for take-off and landing.\n\n" +
            "Crash, and the vehicle comes back to its spot after a few seconds.\n" +
            "A red RESET sign brings back a vehicle nobody is in.";

        const string HowToDrive =
            "<b>Cars and the tank</b> are parked behind the arrival point.\n" +
            "Point at the driver's seat and pull the trigger. Jump to get out.\n\n" +
            "<b>VR controllers</b>\n" +
            "Right trigger (or right stick forward) = accelerate\n" +
            "Left trigger (or a stick back) = brake; hold it at a standstill to reverse\n" +
            "Either stick sideways = steer    Left grip = handbrake\n" +
            "Tank: the left and right sticks drive the left and right tracks\n" +
            "Click the right stick = horn    Click the left stick = headlights    X = back on your wheels\n" +
            "B = boost (Iccarus, Shoreline)    Y = jump, and right grip + sticks = turn in the air (Iccarus)\n\n" +
            "<b>Keyboard</b>\n" +
            "W / S = accelerate / brake and reverse    A / D = steer    Space = handbrake\n\n" +
            "<b>The boat</b>\n" +
            "The blue BOAT DOCK sign takes you to a pontoon on the creek; the sign there brings you back.\n" +
            "Take the helm seat: left stick up / down = throttle, left / right = rudder\n" +
            "(keyboard: Shift / Ctrl and Q / E).\n\n" +
            "A vehicle left alone goes back to its place after a while.";

        const string HowToCrew =
            "<b>Gunners</b>\n" +
            "The helicopters (SH-1, SH-2, SH-3), the boat, the truck and the tank have gunner seats: point at one\n" +
            "and pull the trigger to sit, as for any seat. A door gun or turret points where your right hand points;\n" +
            "the tank's turret and the boat's back gun turn with the right thumbstick.\n" +
            "Right trigger and left trigger fire the seat's two weapons (gun, rockets or cannon).\n" +
            "Click a thumbstick for its searchlight, door or hatch. The SF-2's back seat drops its rockets and bombs.\n\n" +
            "<b>Fuel, ammunition and repairs</b>\n" +
            "Fuel burns with the throttle, three times as fast in afterburner; the cockpit gauge shows it.\n" +
            "Low on fuel the engine fades, and with none it stops.\n" +
            "Standing on the ground near where your vehicle parks refuels, rearms and repairs it, second by second.\n\n" +
            "<b>The catapult</b> (advanced controls: SF-1, Stunt Plane, Wasp)\n" +
            "It is the grey track on the runway beside the yellow board, to the left of the parked aircraft.\n" +
            "Pick CATAPULT on the left dial and roll slowly onto the start of the track, pointing along it: it locks on.\n" +
            "Throttle up, then pull that trigger: 0 to 300 km/h in two seconds.\n\n" +
            "<b>More on the dials</b> (advanced controls)\n" +
            "Switches for lights, doors, lasers and cloaks toggle with a pull. Boosters (Wasp) push while held.\n" +
            "Reverse thrust (SD-1, the boat) toggles with a pull.";

        const string Credits =
            "<b>Map</b>\n" +
            "The coast, islands, roads, runways and building plots follow the real layout of Dubai.\n" +
            "Map data © OpenStreetMap contributors, Open Database Licence.\n" +
            "openstreetmap.org/copyright\n" +
            "Every building here is an original design.\n\n" +
            "<b>Aircraft and vehicles</b>\n" +
            "Models, cockpits, sounds, effects and flight model from SaccFlightAndVehicles by Sacchan-VRC.\n" +
            "The airliner is the VAU v320neo by VRChat Aerospace University (github.com/vrcau/VAU320).\n" +
            "Both adapted for this world, under the MIT licence:\n" +
            "<size=70%>Copyright (c) 2021 Sacchan-VRC\n" +
            "Copyright (c) 2023 VRChat Aerospace University\n" +
            "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated " +
            "documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation " +
            "the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to " +
            "permit persons to whom the Software is furnished to do so, subject to the following conditions:\n" +
            "The above copyright notice and this permission notice shall be included in all copies or substantial portions of " +
            "the Software.\n" +
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO " +
            "THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE " +
            "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, " +
            "TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE " +
            "SOFTWARE.</size>";

        /// <summary>Two boards just ahead of the arrival point: how to fly on the left, credits on the right.</summary>
        public static void BuildBoards(GameObject parent, Vector3 origin, Quaternion facing, float ground)
        {
            var howTo = Board(parent, "How To Fly", "HOW TO FLY", HowToFly, origin, origin + facing * new Vector3(-4.3f, 0f, 7.5f), ground, 0.125f);
            Board(parent, "Credits", "CREDITS", Credits, origin, origin + facing * new Vector3(4.3f, 0f, 7.5f), ground, 0.125f);
            Board(parent, "Cars And Boats", "CARS, TANK, BOAT", HowToDrive, origin, origin + facing * new Vector3(-12.6f, 0f, 5.2f), ground, 0.125f);
            Board(parent, "Gunners And Fuel", "GUNNERS, FUEL, CATAPULT", HowToCrew, origin, origin + facing * new Vector3(17.0f, 0f, 3.0f), ground, 0.125f);
            AdvancedButton(howTo);
        }

        public const string AdvancedOff = "ADVANCED\nCONTROLS\n<color=#ff9a7a>OFF</color>", AdvancedOn = "ADVANCED\nCONTROLS\n<color=#7dffa0>ON</color>";

        /// <summary>
        /// The switch for Sacc's own VR controls (the fleet watch's graph flips it): a panel standing just to the
        /// right of the How To Fly board. Each player sets it for themselves; it is not shared and starts off.
        /// </summary>
        static void AdvancedButton(GameObject board)
        {
            var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
            button.name = "Advanced Controls Button";
            button.transform.SetParent(board.transform, false);
            button.transform.localPosition = new Vector3(2.95f, 1.55f, 0.04f);
            button.transform.localScale = new Vector3(1.2f, 0.8f, 0.08f);
            button.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial("AdvancedButton", new Color(0.16f, 0.30f, 0.46f));
            button.layer = 5;                                            // UI layer so it can be clicked
            // (close by only: from a cockpit parked nearby the right trigger is also the pointer's click)
            var range = button.AddComponent(GraphTypes.Find("BS.BSPointerRange"));
            var so = new SerializedObject(range);
            so.FindProperty("distance").floatValue = 6f;
            so.ApplyModifiedPropertiesWithoutUndo();
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "Leg";
            post.transform.SetParent(board.transform, false);
            post.transform.localPosition = new Vector3(2.95f, 0.575f, 0.06f);
            post.transform.localScale = new Vector3(0.08f, 1.15f, 0.08f);
            post.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial("SignLeg", new Color(0.32f, 0.33f, 0.36f));
            var label = Child(board, "Advanced Controls Label").AddComponent<TMPro.TextMeshPro>();
            label.transform.localPosition = new Vector3(2.95f, 1.55f, -0.01f);
            label.rectTransform.sizeDelta = new Vector2(1.1f, 0.72f);
            label.fontSize = 1.5f;
            label.fontStyle = TMPro.FontStyles.Bold;
            label.alignment = TMPro.TextAlignmentOptions.Center;
            label.richText = true;
            label.color = Color.white;
            label.text = AdvancedOff;
            var watch = Watch().GetComponent<Unity.VisualScripting.Variables>().declarations;
            watch.Set("AdvancedButton", button);
            watch.Set("AdvancedLabel", label);
            EditorUtility.SetDirty(Watch());
        }

        static GameObject Board(GameObject parent, string name, string title, string body, Vector3 viewer, Vector3 position, float ground, float bodySize)
        {
            const float width = 4.4f, height = 3.3f, bottom = 0.5f;
            position.y = ground;
            var away = position - viewer;
            away.y = 0f;
            var board = Child(parent, name);
            board.transform.SetPositionAndRotation(position, Quaternion.LookRotation(away.normalized, Vector3.up));

            foreach (float x in new[] { -width * 0.42f, width * 0.42f })
            {
                var leg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                leg.name = "Leg";
                leg.transform.SetParent(board.transform, false);
                leg.transform.localPosition = new Vector3(x, (bottom + 0.1f) * 0.5f, 0.06f);
                leg.transform.localScale = new Vector3(0.1f, bottom + 0.1f, 0.1f);
                leg.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial("SignLeg", new Color(0.32f, 0.33f, 0.36f));
            }
            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "Panel";
            panel.transform.SetParent(board.transform, false);
            panel.transform.localPosition = new Vector3(0f, bottom + height * 0.5f, 0.04f);
            panel.transform.localScale = new Vector3(width, height, 0.08f);
            panel.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial("BoardPanel", new Color(0.10f, 0.14f, 0.22f));

            var heading = Child(board, "Title").AddComponent<TMPro.TextMeshPro>();
            heading.transform.localPosition = new Vector3(0f, bottom + height - 0.24f, -0.01f);
            heading.rectTransform.sizeDelta = new Vector2(width - 0.3f, 0.36f);
            heading.fontSize = 2.6f;
            heading.fontStyle = TMPro.FontStyles.Bold;
            heading.alignment = TMPro.TextAlignmentOptions.Center;
            heading.color = new Color(1f, 0.78f, 0.30f);
            heading.text = title;

            var text = Child(board, "Text").AddComponent<TMPro.TextMeshPro>();
            text.transform.localPosition = new Vector3(0f, bottom + (height - 0.46f) * 0.5f, -0.01f);
            text.rectTransform.sizeDelta = new Vector2(width - 0.3f, height - 0.56f);
            text.fontSize = bodySize * 10f;
            text.enableAutoSizing = true;                 // shrink to fit the panel, never spill over its edge
            text.fontSizeMin = 0.4f;
            text.fontSizeMax = bodySize * 10f;
            text.overflowMode = TMPro.TextOverflowModes.Truncate;
            text.enableWordWrapping = true;
            text.richText = true;
            text.alignment = TMPro.TextAlignmentOptions.TopLeft;
            text.color = new Color(0.93f, 0.95f, 0.98f);
            text.text = body;
            return board;
        }

        /// <summary>
        /// A red sign on a post: click it to bring back a vehicle nobody is in. Returns the board, which is
        /// the part that takes the click.
        /// </summary>
        public static GameObject ResetSign(GameObject parent, string vehicleName, Vector3 position, float ground, Quaternion facing)
        {
            var board = Sign(parent, "Reset " + vehicleName, "RESET\n" + vehicleName, new Color(0.86f, 0.30f, 0.10f), position, ground, facing);
            // Within reach of the pointer from close by only (everything else in the world: 30 m). A vehicle whose
            // graph is dozing is woken for a player near its sign, and that must be everybody who can click it.
            var range = board.AddComponent(GraphTypes.Find("BS.BSPointerRange"));
            var so = new SerializedObject(range);
            so.FindProperty("distance").floatValue = SignReach;
            so.ApplyModifiedPropertiesWithoutUndo();
            return board;
        }

        /// <summary>
        /// Where a seated player's eyes come out relative to a platform seat's origin, from the app's seat code
        /// (FlexaMover, 2026-11-22): the pelvis is pinned at the seat pose's pelvis point (SeatPose.Default: 4 cm above
        /// the origin), and the view hangs the avatar's own pelvis-to-eye reach above it (AvatarSystem.SeatedEyeReach,
        /// about 0.70 m for a human avatar), a fifth of that reach ahead (SeatHeadForwardShare 0.20). A seat is put so
        /// those eyes land on the vehicle's eye point. (Until 2026-11-22 this assumed 0.45 m up and 0.30 m ahead, measured
        /// on the client of 2026-11-21: on the current one that sat every player about 0.3 m too high and 0.15 m back.)
        /// </summary>
        public const float SeatPelvisAbove = 0.04f, SeatEyeReach = 0.70f, SeatHeadForwardShare = 0.20f;
        public const float SeatEyeAbove = SeatPelvisAbove + SeatEyeReach, SeatEyeAhead = SeatHeadForwardShare * SeatEyeReach;

        /// <summary>The seat origin that puts a seated player's eyes on `eye` (in the vehicle's axes `up` / `forward`).</summary>
        public static Vector3 SeatFor(Vector3 eye, Vector3 up, Vector3 forward) => eye - up * SeatEyeAbove - forward * SeatEyeAhead;

        /// <summary>
        /// The way an original seat faces, turned about the vehicle's up only (its yaw in the vehicle, no pitch or roll):
        /// Sacc's door gunners and some passengers face sideways or backwards.
        /// </summary>
        public static Quaternion UprightFacing(Transform seat, Transform vehicle)
        {
            var ahead = Vector3.ProjectOnPlane(seat.forward, vehicle.up);
            return ahead.sqrMagnitude < 1e-4f ? vehicle.rotation : Quaternion.LookRotation(ahead.normalized, vehicle.up);
        }

        /// <summary>A seat's click area, in the seat's own axes: around where the sitter's head and chest are.</summary>
        public static Vector3 SeatClickCentre => new Vector3(0f, SeatEyeAbove - 0.05f, SeatEyeAhead - 0.2f);

        /// <summary>How far away a reset sign can be clicked, and how near a player must be for its vehicle to be woken.</summary>
        public const float SignReach = 8f, SignWakeDistance = 11f;
        const string WatchName = "FleetWatch";

        /// <summary>
        /// Whether vehicles doze without being told to. Off: on the headset (build 56d03fdd, 2026-11-21) every
        /// vehicle dozed off and was woken again every 3.5 s, and switching a graph off and on there costs
        /// 65 ms and 150 ms frames for the fleet. The page can still turn dozing on for a client (fw_doze 1).
        /// </summary>
        public const bool DozeByDefault = false;

        /// <summary>The object whose graph wakes dozing vehicles (FlightGraphs.BuildFleetWatch); made on first use.</summary>
        public static GameObject Watch()
        {
            var watch = GameObject.Find(WatchName);
            if (watch == null) watch = new GameObject(WatchName);
            if (!watch.TryGetComponent<Unity.VisualScripting.Variables>(out var variables)) variables = watch.AddComponent<Unity.VisualScripting.Variables>();
            if (!variables.declarations.IsDefined("Vehicles")) variables.declarations.Set("Vehicles", new Unity.VisualScripting.AotList());
            variables.declarations.Set("DozeOn", DozeByDefault);
            // the clock's side: who gets every frame and every step, and whose turn it is for the slow tick
            foreach (var listName in new[] { "TickVehicles", "BusyVehicles" })
                if (!variables.declarations.IsDefined(listName)) variables.declarations.Set(listName, new Unity.VisualScripting.AotList());
            if (!variables.declarations.IsDefined("TickCount")) variables.declarations.Set("TickCount", 1f);
            variables.declarations.Set("PilotVehicle", watch);          // (any object: it only counts while HasPilotVehicle is set)
            variables.declarations.Set("HasPilotVehicle", false);
            variables.declarations.Set("AdvancedControls", false);      // the board's button: Sacc's VR controls, for this player
            variables.declarations.Set("Gunner", watch);                // the gunner seat the local player is in (only while HasGunner)
            variables.declarations.Set("HasGunner", false);
            if (!variables.declarations.IsDefined("AdvancedButton")) variables.declarations.Set("AdvancedButton", watch);
            if (!variables.declarations.IsDefined("AdvancedLabel")) variables.declarations.Set("AdvancedLabel", null);
            // Where the local player's hands are. The app attaches an object to a hand bone by following the physics
            // hand with a parent constraint (BSAttachedObject, attached to "me" by the graph when the controls are
            // switched on); nothing else in a graph can see the controllers' pose.
            foreach (var (side, bone, id) in new[] { ("Left", 8, "fw-hand-left"), ("Right", 12, "fw-hand-right") })
            {
                var t = watch.transform.Find(side + "HandTracker");
                var hand = t != null ? t.gameObject : Child(watch, side + "HandTracker");
                var attachType = GraphTypes.Find("BS.BSAttachedObject");
                var attached = hand.GetComponent(attachType) ?? hand.AddComponent(attachType);
                var so = new SerializedObject(attached);
                so.FindProperty("uid").stringValue = "me";
                so.FindProperty("avatarAttachmentType").intValue = 0;   // AttachToAvatar: the object goes on the player
                so.FindProperty("attachmentType").intValue = 1;         // NonPhysics
                so.FindProperty("avatarAttachmentPoint").intValue = bone; // LEFTARM_HAND / RIGHTARM_HAND
                so.FindProperty("autoAttach").boolValue = false;
                so.FindProperty("autoSync").boolValue = false;          // nobody else needs it
                so.ApplyModifiedPropertiesWithoutUndo();
                var objectId = hand.GetComponent(GraphTypes.Find("BS.BSObjectId"));
                if (objectId != null)
                {
                    var ids = new SerializedObject(objectId);
                    ids.FindProperty("Id").stringValue = id;
                    ids.ApplyModifiedPropertiesWithoutUndo();
                }
                variables.declarations.Set(side + "Hand", hand.transform);
            }
            var graph = AssetDatabase.LoadAssetAtPath<Unity.VisualScripting.ScriptGraphAsset>(FlightGraphs.WatchPath);
            if (graph == null) throw new Exception("fleet watch graph missing: run FlightWorld/Flight/Regenerate Flight Graphs");
            if (!watch.TryGetComponent<Unity.VisualScripting.ScriptMachine>(out var machine)) machine = watch.AddComponent<Unity.VisualScripting.ScriptMachine>();
            machine.nest.SwitchToMacro(graph);
            EditorUtility.SetDirty(watch);
            return watch;
        }

        /// <summary>
        /// The object whose graph flies every missile, guided bomb and bomb in the air (FlightGraphs.BuildWeaponsWorld),
        /// whoever fired it: the rounds a vehicle launches are put on its list. Made on first use.
        /// </summary>
        public static GameObject WeaponsWorld()
        {
            const string name = "WeaponsWorld";
            var world = GameObject.Find(name);
            if (world == null) world = new GameObject(name);
            if (!world.TryGetComponent<Unity.VisualScripting.Variables>(out var variables)) variables = world.AddComponent<Unity.VisualScripting.Variables>();
            variables.declarations.Set("LiveShots", new Unity.VisualScripting.AotList());
            variables.declarations.Set("LiveCount", 0f);
            if (!variables.declarations.IsDefined("Aircraft")) variables.declarations.Set("Aircraft", new Unity.VisualScripting.AotList());
            // the guided bomb's aim, seen only by the pilot aiming it: green where they look, red where they locked
            foreach (var (marker, colour) in new[] { ("AimMarker", new Color(0.2f, 1f, 0.35f)), ("LockMarker", new Color(1f, 0.2f, 0.15f)) })
            {
                var t = world.transform.Find(marker);
                var ball = t != null ? t.gameObject : GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = marker;
                ball.transform.SetParent(world.transform, false);
                foreach (var c in ball.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(c);
                var material = FlatMaterial(marker, colour);
                material.SetColor("_EmissionColor", colour);
                material.EnableKeyword("_EMISSION");
                var drawn = ball.GetComponent<MeshRenderer>();
                drawn.sharedMaterial = material;
                drawn.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ball.SetActive(false);
                variables.declarations.Set(marker, ball);
            }
            var graph = AssetDatabase.LoadAssetAtPath<Unity.VisualScripting.ScriptGraphAsset>(FlightGraphs.WeaponsPath);
            if (graph == null) throw new Exception("weapons graph missing: run FlightWorld/Flight/Regenerate Flight Graphs");
            if (!world.TryGetComponent<Unity.VisualScripting.ScriptMachine>(out var machine)) machine = world.AddComponent<Unity.VisualScripting.ScriptMachine>();
            machine.nest.SwitchToMacro(graph);
            EditorUtility.SetDirty(world);
            return world;
        }

        /// <summary>The object that follows the local player's left or right hand (see Watch).</summary>
        public static Transform HandTracker(bool left) =>
            (Transform)Watch().GetComponent<Unity.VisualScripting.Variables>().declarations.Get(left ? "LeftHand" : "RightHand");

        /// <summary>What a vehicle's graph and the fleet watch share about dozing: set on every vehicle as it is built.</summary>
        public static void WatchVariables(Unity.VisualScripting.Variables variables, Unity.VisualScripting.ScriptMachine machine, Bounds bounds,
            bool alwaysBusy = false)
        {
            // (a vehicle that says it is busy gets every frame and every step from the fleet clock; a boat always is)
            variables.declarations.Set("Busy", alwaysBusy);
            if (machine == null) throw new Exception("vehicle has no script machine yet");
            float halfAcross = 0.5f * Mathf.Sqrt(bounds.size.x * bounds.size.x + bounds.size.z * bounds.size.z);
            variables.declarations.Set("Machine", machine);
            variables.declarations.Set("Watch", Watch());
            variables.declarations.Set("DozeBeyond", 18f + halfAcross);     // the watch wakes it 6 m inside this
            variables.declarations.Set("Dozing", false);
            variables.declarations.Set("DozePosition", Vector3.zero);
            variables.declarations.Set("DozeLod", 0f);
        }

        /// <summary>Give the fleet watch its list: every vehicle in the scene that can doze. Returns how many.</summary>
        public static int RegisterWithWatch()
        {
            var watch = Watch();
            var list = new Unity.VisualScripting.AotList();
            foreach (var rootName in new[] { "Aircraft", "GroundVehicles" })
            {
                var root = GameObject.Find(rootName);
                if (root == null) continue;
                foreach (Transform child in root.transform)
                    if (child.TryGetComponent<Unity.VisualScripting.ScriptMachine>(out _) &&
                        child.TryGetComponent<Unity.VisualScripting.Variables>(out var variables) && variables.declarations.IsDefined("Dozing"))
                        list.Add(child.gameObject);
            }
            watch.GetComponent<Unity.VisualScripting.Variables>().declarations.Set("Vehicles", list);
            // The clock's lists. The slow tick goes to the vehicles whose graph takes it (the aircraft graph); every
            // frame and every step go to those of them that can be busy without a local pilot (boats).
            var ticked = new Unity.VisualScripting.AotList();
            var busy = new Unity.VisualScripting.AotList();
            foreach (GameObject vehicle in list)
            {
                var declarations = vehicle.GetComponent<Unity.VisualScripting.Variables>().declarations;
                if (!declarations.IsDefined("IsBoat")) continue;                 // (not on the aircraft graph)
                ticked.Add(vehicle);
                if (declarations.Get("IsBoat") is bool boat && boat) busy.Add(vehicle);
            }
            var clock = watch.GetComponent<Unity.VisualScripting.Variables>().declarations;
            clock.Set("TickVehicles", ticked);
            clock.Set("BusyVehicles", busy);
            clock.Set("TickCount", (float)Mathf.Max(ticked.Count, 1));
            EditorUtility.SetDirty(watch);
            // what weapons can hit and missiles can lock on to: the vehicles on the aircraft graph (a boat too)
            var weapons = WeaponsWorld();
            var aircraft = new Unity.VisualScripting.AotList();
            foreach (GameObject vehicle in ticked) aircraft.Add(vehicle);
            weapons.GetComponent<Unity.VisualScripting.Variables>().declarations.Set("Aircraft", aircraft);
            foreach (GameObject vehicle in ticked)
                vehicle.GetComponent<Unity.VisualScripting.Variables>().declarations.Set("TargetIndex", (float)aircraft.IndexOf(vehicle));
            EditorUtility.SetDirty(weapons);
            return list.Count;
        }

        /// <summary>A coloured board on a post with a label; returns the board, which is what a click lands on.</summary>
        public static GameObject Sign(GameObject parent, string name, string text, Color colour, Vector3 position, float ground, Quaternion facing)
        {
            position.y = ground;
            var sign = Child(parent, name);
            sign.transform.SetPositionAndRotation(position, facing);
            var leg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leg.name = "Leg";
            leg.transform.SetParent(sign.transform, false);
            leg.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            leg.transform.localScale = new Vector3(0.1f, 1.0f, 0.1f);
            leg.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial("SignLeg", new Color(0.32f, 0.33f, 0.36f));
            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "Board";
            board.transform.SetParent(sign.transform, false);
            board.transform.localPosition = new Vector3(0f, 1.4f, 0f);
            board.transform.localScale = new Vector3(1.7f, 0.9f, 0.08f);
            board.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial("SignBoard_" + ColorUtility.ToHtmlStringRGB(colour), colour);
            board.layer = 5;                                             // UI layer so it can be clicked
            var label = Child(sign, "Label").AddComponent<TMPro.TextMeshPro>();
            label.transform.localPosition = new Vector3(0f, 1.4f, -0.05f);   // on the side that faces the arrival point
            label.rectTransform.sizeDelta = new Vector2(1.6f, 0.85f);
            label.fontSize = 2.6f;
            label.fontStyle = TMPro.FontStyles.Bold;
            label.alignment = TMPro.TextAlignmentOptions.Center;
            label.color = Color.white;
            label.text = text;
            return board;
        }

        /// <summary>
        /// Unity leaves every object tagged EditorOnly out of a build, with its children. The Sacc prefabs tag
        /// their unused source meshes that way (collision shapes' models, spare propellers and HUD glass), so
        /// they exist in the editor and not in the published world, where anything pointing at one finds
        /// nothing. Deleting them here keeps the two the same. Returns how many objects went.
        /// </summary>
        public static int RemoveStrippedFromBuilds(GameObject root)
        {
            int removed = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.gameObject == root || !t.CompareTag("EditorOnly")) continue;
                UnityEngine.Object.DestroyImmediate(t.gameObject);
                removed++;
            }
            return removed;
        }

        public static GameObject Child(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        /// <summary>A synthesised clip from Assets/Flight/Audio, imported mono and compressed (fallback sounds).</summary>
        public static AudioClip Clip(string name)
        {
            var path = $"{AudioDir}/{name}.wav";
            if (AssetImporter.GetAtPath(path) is AudioImporter importer)
            {
                var settings = importer.defaultSampleSettings;
                bool oneShot = name == "explosion";
                var loadType = oneShot ? AudioClipLoadType.DecompressOnLoad : AudioClipLoadType.CompressedInMemory;
                if (!importer.forceToMono || settings.loadType != loadType || settings.compressionFormat != AudioCompressionFormat.Vorbis)
                {
                    importer.forceToMono = true;
                    settings.loadType = loadType;
                    settings.compressionFormat = AudioCompressionFormat.Vorbis;
                    settings.quality = 0.55f;
                    importer.defaultSampleSettings = settings;
                    importer.SaveAndReimport();
                }
            }
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path) ?? throw new Exception("sound missing: " + path + " (run Tools/aircraft/make_sounds.py)");
        }

        /// <summary>A looping 3D source that starts silent; the graph sets its volume and pitch.</summary>
        public static AudioSource Loop(GameObject go, AudioClip clip, float fullVolumeWithin, float silentBeyond, float doppler)
        {
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = true;
            source.volume = 0f;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = fullVolumeWithin;
            source.maxDistance = silentBeyond;
            source.dopplerLevel = doppler;
            return source;
        }

        /// <summary>A plain coloured material kept as an asset, so it ships with the world.</summary>
        public static Material FlatMaterial(string name, Color colour)
        {
            Directory.CreateDirectory(MaterialDir);
            var path = $"{MaterialDir}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetColor("_BaseColor", colour);
            material.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(material);
            return material;
        }

        public static void SetSerialized(Component component, StringBuilder report, params (string name, object value)[] values)
        {
            var so = new SerializedObject(component);
            foreach (var (name, value) in values)
            {
                var property = so.FindProperty(name);
                if (property == null)
                {
                    report.AppendLine($"WARNING: {component.GetType().Name} has no serialized field '{name}'");
                    continue;
                }
                if (value is bool b) property.boolValue = b;
                else if (value is int i) property.intValue = i;
                else if (value is float f) property.floatValue = f;
                else if (value is Vector3 v) property.vector3Value = v;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
