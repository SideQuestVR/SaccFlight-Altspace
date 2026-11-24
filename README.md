# SaccFlight for Altspace

A multi-user flight world for [Altspace](https://altvr.app) (SideQuest): fly, drive and sail over a city laid out
like Dubai. Live at **[altvr.app/worlds/@pilots](https://altvr.app/worlds/@pilots)**.

The aircraft, cars, tank and boat are the vehicles from
**[SaccFlightAndVehicles](https://github.com/Sacchan-VRC/SaccFlightAndVehicles)** by **Sacchan-VRC**, and the airliner
is the **[VAU v320neo](https://github.com/vrcau/VAU320)** by **VRChat Aerospace University**. Both were made for
VRChat (UdonSharp). This project brings them to Altspace: their models, cockpits, instruments, animations, sounds and
effects are used as they are, and their flight and driving behaviour is ported to Altspace's Visual Scripting.

## Credits

- **SaccFlightAndVehicles**: Copyright (c) 2021 Sacchan-VRC, MIT licence. Vehicle models, textures, animations,
  sounds, effects, prefabs, and the flight, ground-vehicle and boat models (SaccAirVehicle, SaccGroundVehicle,
  SaccWheel, SaccSeaVehicle and their effects, sound, HUD and function scripts) that the graphs here are ported from.
  Reference commit `3216e53`. Licence: [`Assets/Sacc/LICENSE.md`](Assets/Sacc/LICENSE.md).
- **VAU v320neo**: Copyright (c) 2023 VRChat Aerospace University, MIT licence. The A320neo airliner's model,
  cockpit, displays, textures, animations and sounds. Reference commit `a5e7951` (package 0.6.0).
  Licence: [`Assets/VAU320/LICENSE.txt`](Assets/VAU320/LICENSE.txt).
- **Map data**: © [OpenStreetMap](https://www.openstreetmap.org/copyright) contributors, Open Database Licence.
  The coast, islands, roads, runways and building plots follow OpenStreetMap; every building is an original design.

What came from where, in detail: [`Assets/Flight/THIRD-PARTY-NOTICES.md`](Assets/Flight/THIRD-PARTY-NOTICES.md).
The same credits are on a board in the world.

## Licence

Code and content written for this project are under the MIT licence in [`LICENSE`](LICENSE) (Copyright (c) 2026
SideQuest). The parts that come from SaccFlightAndVehicles and VAU v320neo stay under their own MIT licences and
copyright notices (linked above), which must be kept with any copy. The map data is under the ODbL.

## What is in the world

- 14 Sacc aircraft: jets (SF-1 ×2, SF-2, SB-1), propeller aircraft (Stunt Plane, Wasp WX3), the SX-TR3, the SC-1
  tilt-thrust transport, helicopters (SH-1, SH-2, SH-3), the SD-1 drone, the GRVR-1 and the balloon.
- The VAU A320neo airliner (34 of its seats, flight deck and front cabin).
- Four cars (Baron, Iccarus, Shoreline, Thunder) and the tank.
- The SS-1 boat, moored in the creek (blue BOAT DOCK sign).
- Seats for passengers, head-up displays and instruments, gear, flaps, canopies, crash and respawn, sounds heard by
  everyone, and notice boards with the controls and credits.
- Sacc's dial functions on the advanced controls: engine start and stop, cruise, altitude hold, flight limits, hook
  and arresting cables, catapult, smoke, flares, gun, missiles, guided bombs, bombs, rockets and cannons, switches
  (lights, doors, lasers), boosters and reverse thrust.
- Gunner seats with Sacc's turrets: door guns that follow the gunner's hand, stick-turned turrets on the tank and
  the boat, each with its own guns, rockets or cannon and searchlight.
- Fuel, and resupply (rearm, refuel, repair) on the ground near a vehicle's parking spot.
- For the cars: horn, headlights, boost, jump, air control and reset.

Not ported (yet): wind, the grappling hook, the SX-TR3's laser and tractor beam, co-pilots taking control, the
gunners' guided bomb, smoke screen and zoom sights, manual gears, the helmet sight, race courses, the AA gun and
hand-held weapons, and the A320's flight management systems and cockpit switches.

## How it works

Altspace worlds cannot ship their own C#, so every behaviour is a Visual Scripting graph. The graphs are not drawn by
hand: they are **generated from C#** in `Assets/Flight/Editor/`, which reads like the original scripts.

| Path | What it is |
|---|---|
| `Assets/Flight/Editor/FlightGraphs.cs` | Aircraft and boat: the port of SaccAirVehicle (lift, drag, AoA, thrust, VTOL, helicopters, floats, gear, flaps, brakes, G), effects, sound, HUD, gauges, the A320's displays, and the fleet clock |
| `Assets/Flight/Editor/FlightGraphsFunctions.cs`, `FlightGraphsHands.cs`, `FlightGraphsWeapons.cs` | The advanced controls: Sacc's dial functions, fuel, resupply and catapult; the hand-held stick and throttle; the rounds in the air |
| `Assets/Flight/Editor/GunnerGraphs.cs`, `SaccGunnerSeats.cs` | Gunner seats: one graph per seat for Sacc's hand turrets, stick turrets and the gunners' weapons |
| `Assets/Flight/Editor/GroundGraphs.cs`, `GroundGraphsExtras.cs` | Cars and tank: SaccGroundVehicle and SaccWheel (engine, gearbox, suspension, tyres, tracks); horn, lights, boost, jump, air control, reset |
| `Assets/Flight/Editor/SeatLegs.cs` | Seats set on their cushions and riders' legs kept inside each vehicle |
| `Assets/Flight/Editor/SaccFleetBuilder.cs`, `SaccGroundBuilder.cs` | Build the vehicles from the original prefabs into the scene: read their settings, strip the VRChat scripts, convert materials, add seats, sync and the graphs, park them |
| `Assets/Flight/Graphs/` | The generated graph assets |
| `Assets/Flight/Shaders/` | Quest-friendly stand-ins for the vehicles' shaders (lit aircraft, glass, particles, HUD) |
| `Assets/Sacc/`, `Assets/VAU320/` | The upstream assets, with data-only stand-ins for their scripts (`Scripts/`) so the prefabs' settings can be read |
| `Assets/City/`, `Tools/citygen/` | The city: OpenStreetMap to Blender to Unity pipeline |
| `Tools/sacc_import/` | Import scripts for the upstream projects |
| `Tools/quest/` | Unattended tests on a Quest headset through the app's test harness |
| `Tools/remote.sh` and `Assets/Editor/` | Editor automation and probes (flight, crash, drive, jitter, performance tests) |

## Building

1. Unity **6000.3.21f1** (URP) with the SideQuest **Creator SDK 4.0.29**; open the project and run
   *Creator SDK > Setup* (including *Generate nodes*).
2. Fetch the upstream projects (they are not in this repository as clones; their assets are, under `Assets/`):
   `git clone https://github.com/Sacchan-VRC/SaccFlightAndVehicles Tools/sacc-full` and
   `git clone --depth 1 https://github.com/vrcau/VAU320 Tools/vau320`. Only needed to re-import them, with
   `Tools/sacc_import/import_sacc.py` and `import_vau320.py`.
3. *FlightWorld > Flight > Build Fleet In Scene* and *Build Ground Vehicles In Scene* regenerate the graphs and
   rebuild every vehicle in `Assets/Scenes/SampleScene.unity`.
4. Build and upload with the Creator SDK Builder.
