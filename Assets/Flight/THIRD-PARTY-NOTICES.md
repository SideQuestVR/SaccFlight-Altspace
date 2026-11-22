# Third-party notices

## SaccFlightAndVehicles (aircraft, ground vehicles, their sounds, effects and behaviour)

The vehicles in this world are the ones from
[SaccFlightAndVehicles](https://github.com/Sacchan-VRC/SaccFlightAndVehicles) (commit
`3216e53fecb1d9cd3c9c740688e0b8743bb283cb`), used under its MIT licence:

- `Assets/Sacc/` is a copy of that project's `AssetFiles`, `Prefabs`, `Textures` and `Shaders` folders
  (models, textures, animations and animator controllers, sounds, particle effects, prefabs), made by
  `Tools/sacc_import/import_sacc.py`. Its scripts are not copied: `Assets/Sacc/Scripts` holds
  generated data-only stand-ins with the same file IDs and field names, so the prefabs' settings can
  be read.
- The vehicles' behaviour is a port of that project's scripts (`SaccAirVehicle`, `SaccGroundVehicle`,
  `SaccWheel`, the effects, sound, head-up display and function scripts) to Visual Scripting graphs,
  written as code in `Assets/Flight/Editor/FlightGraphs.cs` and `GroundGraphs.cs` and generated into
  `Assets/Flight/Graphs/`.
- The shaders in `Assets/Flight/Shaders/` named `Hud*` re-implement that project's head-up display
  shaders for this render pipeline.

Changes made here: materials rebuilt on this world's shaders, VRChat components replaced by this
platform's seats and networking, weapons and their menus left out. The project's font ("F1.8", SIL
Open Font Licence) is not included. The notice below is also shown on the credits board at the
world's arrival point.

```
Copyright (c) 2021 Sacchan-VRC

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## OpenStreetMap (city layout)

The coastline, islands, water, roads, runways and building footprints and heights that the city is
generated from come from OpenStreetMap.

Map data © OpenStreetMap contributors, available under the Open Database Licence (ODbL):
https://www.openstreetmap.org/copyright

The building designs, facades and textures are original; no real building is reproduced and no
satellite or aerial imagery is used.

## VAU v320neo (the airliner)

The airliner is the [VAU v320neo](https://github.com/vrcau/VAU320) by VRChat Aerospace University (commit
`a5e7951`, package version 0.6.0), an aircraft built on SaccFlightAndVehicles 1.64 and EsnyaSFAddons, used under
its MIT licence:

- `Assets/VAU320/` holds the files its main prefab (`Prefab/v320neo_main 1217.prefab`) uses - model, textures,
  materials, animations and animator controllers, sounds, fonts, nested prefabs - copied with their `.meta` files by
  `Tools/sacc_import/import_vau320.py`. Its scripts are not copied: `Assets/VAU320/Scripts` holds generated
  data-only stand-ins with the same file IDs and field names, so the prefab's settings can be read.
- It flies on this world's port of SaccAirVehicle. Its tuning was written as forces for its 60 t (Sacc 1.64 with
  `AutoAdjustValuesToMass` off) and is converted to the per-mass form; thrust comes from its two engine scripts'
  `maxThrust`; one flap position is ported (lever position 2); the landing gear and flaps travel as its EsnyaSFAddons
  scripts moved them; its primary flight, navigation, standby and engine displays are fed the numbers its own
  display scripts fed them (`PFDBasicDisplay`, `NDDisplay`, `ECAMDisplay`).
- Not ported: its flight management and guidance computers, autothrust and autobrake, fly-by-wire trim, warnings,
  the MCDU and electronic flight bag, thrust reversers, radios, cabin signs, and every cockpit switch. Of its 156
  seats, 34 are seats here (the flight deck and the first five rows).

```
MIT License

Copyright (c) 2023 VRChat Aerospace University

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
