#!/usr/bin/env python3
"""Writes Tools/snippets/kits.json: the title, the short summary (shown in Unity's Inspector) and the description
(shown on the snippet's page) of every vehicle snippet. The upload tool (Assets/Flight/Editor/SnippetUpload.cs)
reads that file. Sizes come from the built bundles when they are there.

    python Tools/snippets/make_texts.py
"""
import json
import os

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
KITS = os.path.join(ROOT, 'Library', 'FlightWorld', 'kits')

SACC = ('Model, cockpit, sounds, effects and flight model from SaccFlightAndVehicles by Sacchan-VRC (MIT licence), '
        'brought to Altspace for the Pilots world: altvr.app/worlds/@pilots')
VAU = ('The airliner is the VAU v320neo by VRChat Aerospace University (MIT licence), flying on the flight model of '
       'SaccFlightAndVehicles by Sacchan-VRC (MIT licence). Brought to Altspace for the Pilots world: '
       'altvr.app/worlds/@pilots')

FLY_VR = ('VR: left stick = throttle (up / down) and rudder; right stick = nose up / down and roll.')
ADVANCED = ('Advanced controls (the blue button beside its sign, VR): hold the cockpit\'s stick and throttle with your '
            'hands; a thumbstick picks a function or weapon on its dial and that side\'s trigger works it.')
FLY_KEYS = 'Keyboard: Shift / Ctrl = throttle, S / W = nose up / down, A / D = roll, Q / E = rudder'

CONTROLS = {
    'plane': [FLY_VR + ' Left trigger = brakes, click the right stick = landing gear, click the left stick = flaps.',
              ADVANCED, FLY_KEYS + ', B = brakes, G = gear, V = flaps.',
              'Taking off: full throttle, keep straight, ease the nose up as the speed builds.'],
    'tilt': [FLY_VR + ' Left trigger = brakes, click the right stick = landing gear, click the left stick = flaps. '
             'Left grip + left stick = tilt the thrust up or forward.',
             ADVANCED, FLY_KEYS + ', B = brakes, G = gear, V = flaps, R / F = tilt the thrust up / forward.',
             'Taking off: tilt the thrust up and raise the throttle to lift straight off, then tilt it forward to fly '
             'on the wings. A runway works too.'],
    'thrusters': [FLY_VR + ' Left trigger = brakes, click the right stick = landing gear. '
                  'Left grip + left stick = thrusters ahead, astern and sideways.',
                  ADVANCED, FLY_KEYS + ', B = brakes, G = gear, I K J L U O = thrusters.',
                  'Taking off: raise the throttle and it lifts; the thrusters push it along.'],
    'heli': [FLY_VR, ADVANCED, FLY_KEYS + '.',
             'Taking off: raise the throttle and it lifts; tip the nose down to fly forward.'],
    'balloon': ['VR: left stick = burner (up / down) and turning; right stick pushes it ahead, astern and sideways.',
                'Keyboard: Shift / Ctrl = burner, Q / E = turn, I K J L = ahead, astern, sideways.',
                'Raise the throttle and it lifts. It turns slowly, as a balloon should.'],
    'boat': ['VR: left stick = throttle (up / down) and rudder.', 'Keyboard: Shift / Ctrl = throttle, Q / E = rudder.'],
    'car': ['VR: right trigger (or right stick forward) = accelerate; left trigger (or a stick back) = brake, and '
            'reverse when held at a standstill; either stick sideways = steer; left grip = handbrake; click the right '
            'stick = horn; click the left stick = headlights; X = back on its wheels.{extra}',
            'Keyboard: W / S = accelerate / brake and reverse, A / D = steer, Space = handbrake.'],
    'tank': ['VR: the left and right sticks drive the left and right tracks; click the right stick = horn; click the '
             'left stick = headlights; X = back on its tracks.',
             'Keyboard: W / S = accelerate / brake and reverse, A / D = steer.'],
}

GUNNERS = ('Gunner seats: sit in one like any other seat. {how} The triggers fire its weapons; click a thumbstick '
           'for its searchlight.')

# slug: title, what it is, seats and equipment, kind, where it goes, extras
VEHICLES = [
    ('sacc-sf-1', 'SF-1 Jet Fighter',
     'A twin-tail jet fighter to fly, with afterburner, a working cockpit and head-up display, and a full load of weapons.',
     'Two seats: the pilot and a passenger behind. Gun, 6 air-to-air missiles, 4 guided bombs, 4 bombs, flares and '
     'smoke; retractable gear, flaps, an opening canopy, an arrestor hook, cruise control, altitude hold and flight limits.',
     'plane', 'It needs a runway, or a long flat stretch of ground.', {}),
    ('sacc-sf-2', 'SF-2 VTOL Fighter',
     'A two-seat jet fighter that tilts its thrust: it lifts straight off the ground, then flies away on its wings.',
     'Two seats: the pilot, and a weapons officer in the back seat who drops the rockets and bombs. Gun, 4 air-to-air '
     'missiles, 4 guided bombs, bombs, rockets and flares; afterburner, gear, flaps, canopy, cruise control, altitude '
     'hold and flight limits.',
     'tilt', 'It takes off and lands on the spot, or along a runway.', {}),
    ('sacc-sb-1', 'SB-1 Mini Jet',
     'A tiny two-seat jet, barely bigger than a car, with an afterburner and floats: it can land on water.',
     'Two seats. Gun, flares and smoke; flaps; an afterburner.',
     'plane', 'It needs a short flat strip, or water, to take off.', {}),
    ('sacc-sx-tr3', 'SX-TR3 Black Triangle',
     'A big black triangular craft that rises on its own thrust and is pushed about by thrusters; it can cloak.',
     'Four seats: the pilot and three passengers. Gun, bombs and flares; a cloak; landing gear, canopy, cruise control. '
     'It also sits on water.',
     'thrusters', 'It lifts off on the spot.', {}),
    ('sacc-wasp-wx3', 'Wasp WX3 Racer',
     'A red propeller racer with three rocket boosters.',
     'Two seats. Three boosters that push while held, guns, 2 air-to-air missiles, bombs, flares and smoke.',
     'plane', 'It needs a flat strip to take off.', {}),
    ('sacc-stunt-plane', 'Stunt Plane',
     'A red and yellow propeller aerobatic plane. Its wings fold up while it is parked and spread when it flies.',
     'Two seats. Smoke for sky-writing, flares, light switches and things to drop; flaps.',
     'plane', 'It needs a flat strip to take off.', {}),
    ('sacc-grvr-1', 'GRVR-1 Hover Gunship',
     'A stubby gunship that hovers on its own thrust and scoots about on thrusters.',
     'Two seats: the pilot and a passenger. Gun, bombs and flares; landing gear and an opening canopy.',
     'thrusters', 'It lifts off on the spot.', {}),
    ('sacc-sc-1', 'SC-1 Tilt-Thrust Transport',
     'A five-seat transport with engines that tilt: it lifts off like a helicopter and cruises like an aeroplane, '
     'and its floats let it land on water.',
     'Five seats: the pilot and four passengers. Gun and flares; flaps, canopy, a cabin that moves, cruise control, '
     'altitude hold and flight limits.',
     'tilt', 'It takes off and lands on the spot, along a runway, or on water.', {}),
    ('sacc-a320', 'A320neo Airliner',
     'A full-size airliner to fly together: a flight deck with working displays, and a cabin for the passengers.',
     '34 seats: captain, first officer, two observers and the front of the cabin. Working flight displays and '
     'instruments, landing gear, flaps, cabin signs, cruise control and altitude hold. No weapons.',
     'plane', 'It is 37 m long with a 36 m wingspan: it needs a real runway (2 km is comfortable) and room to turn. '
              'Point up at the captain\'s window to take the left seat.', {'credit': VAU}),
    ('sacc-sh-1', 'SH-1 Light Helicopter',
     'A light helicopter with a bubble cockpit, a cabin for passengers and a gunner at the door.',
     'Six seats: the pilot, four passengers and a door gunner with a gun, rockets and a searchlight. The pilot has a '
     'gun, 2 air-to-air missiles, 2 guided bombs, rockets, flares and smoke.',
     'heli', 'It lifts off on the spot.', {'gunners': 'The door gun points where your right hand points.'}),
    ('sacc-sh-2', 'SH-2 Heavy Helicopter',
     'A heavy twin-rotor helicopter that carries a whole crew: eight gunners as well as the pilots.',
     'Ten seats: the pilot, a co-pilot\'s seat and eight gunner stations (two door guns and six cannon turrets, each '
     'aimed by hand). The pilot has a gun, 8 guided bombs and flares; landing gear and a back door that opens.',
     'heli', 'It is big: give it plenty of room. It lifts off on the spot.',
     {'gunners': 'Each gun points where its gunner\'s right hand points.'}),
    ('sacc-sh-3', 'SH-3 Attack Helicopter',
     'A two-seat attack helicopter: the pilot flies, the gunner in front works the turret.',
     'Two seats. The gunner has a cannon turret, rockets and a searchlight; the pilot a gun, 4 air-to-air missiles, '
     'rockets, flares and smoke. Retractable gear.',
     'heli', 'It lifts off on the spot.', {'gunners': 'The turret points where the gunner\'s right hand points.'}),
    ('sacc-sd-1', 'SD-1 Quadcopter',
     'A drone big enough to ride: four rotors, an open seat, and room for one passenger.',
     'Two seats. Altitude hold, a seat that moves, and one thing to drop.',
     'heli', 'It lifts off on the spot and fits almost anywhere.', {}),
    ('sacc-balloon', 'Hot-Air Balloon',
     'A hot-air balloon with a basket to ride in. Slow, quiet and very tall: a sightseeing trip for a few friends.',
     'Three seats in the basket, and room to stand beside them. No weapons.',
     'balloon', 'It is nearly 60 m tall and 40 m across: give it open sky.', {}),
    ('sacc-ss-1', 'SS-1 Patrol Boat',
     'A fast patrol boat with a helm, seats for passengers and two gun positions.',
     'Seven seats: the helm, four passengers, a gunner with a hand-aimed gun and rockets, and a turret at the back '
     'turned with the thumbstick. The helm has 8 guided missiles and flares.',
     'boat', None, {'gunners': "The gunner's gun points where the right hand points; the back turret turns with the right thumbstick."}),
    ('sacc-baron', 'Baron Buggy',
     'A red open-wheel buggy with a roll cage: light, quick and happy off the road.',
     'Four seats. Horn and headlights.', 'car', None, {}),
    ('sacc-iccarus', 'Iccarus Supercar',
     'A low red supercar with a rocket boost, and a jump for when the road runs out.',
     'Three seats. Boost, jump, steering in the air, horn and headlights.', 'car', None,
     {'extra': ' B = boost; Y = jump; right grip + sticks = turn in the air.'}),
    ('sacc-shoreline', 'Shoreline Sports Car',
     'A red sports coupe for five, with a boost.',
     'Five seats. Boost, horn and headlights.', 'car', None, {'extra': ' B = boost.'}),
    ('sacc-thunder', 'Thunder Pickup Truck',
     'A red pickup truck with seats in the cab and on the bed, and a machine gun mounted behind the cab.',
     'Seven seats: the driver, five passengers and a gunner with a hand-aimed gun and searchlight. Horn and headlights.',
     'car', None, {'gunners': 'The gun points where your right hand points.'}),
    ('sacc-tank', 'Tank',
     'A battle tank for a crew of three: one drives the tracks, one turns the turret and fires, one commands.',
     'Three seats: the driver, the gunner (main gun and machine gun) and the commander. Horn and headlights.',
     'tank', None, {'gunners': 'The turret turns with the gunner\'s right thumbstick.'}),
]


def megabytes(folder, slug):
    path = os.path.join(KITS, folder, slug)
    return os.path.getsize(path) / 1048576 if os.path.exists(path) else None


def main():
    out = {}
    for slug, title, lead, equipment, kind, where, extras in VEHICLES:
        flies = kind in ('plane', 'tilt', 'thrusters', 'heli', 'balloon')
        boat = kind == 'boat'
        seat = "pilot's seat" if flies else 'helm seat' if boat else "driver's seat"
        footprint = ('the water\'s surface: put the snippet on open water, at the height of the water. The boat floats '
                     'at that height wherever it goes, so it suits a flat sea or lake. Its reset sign stands on a '
                     'small pontoon beside it') if boat else ('where it stands: it is set down on the first floor at '
                     'or below that point (snap-to-ground), once the world has loaded its floor')
        parts = [lead, equipment]
        parts.append(f'Point at the {seat} and pull the trigger (or click it) to get in; jump to get out. Everybody in '
                     f'the world sees and hears it move, and whoever is in the {seat} controls it. It comes with a red '
                     f'RESET sign that brings it back when nobody is aboard, and a panel of instructions beside it.')
        placing = (f'Placing it: the snippet\'s position is {footprint}. The second number of rotation is the way it '
                   f'faces. ' + ('' if boat else 'It needs flat ground with a collider, clear of the spawn point: the default '
                   'position puts its sign about 12 m ahead of a spawn at the origin and the vehicle beyond it. ') +
                   (where + ' ' if where else '') + 'One of each vehicle per world.')
        parts.append(placing)
        controls = [line.replace('{extra}', extras.get('extra', '')) for line in CONTROLS[kind]]
        parts.append('\n'.join(controls))
        if 'gunners' in extras:
            parts.append(GUNNERS.format(how=extras['gunners']))
        notes = []
        if flies or boat:
            notes.append('Fuel burns as it goes; ' + ('back at its mooring' if boat else 'on the ground near where it parks')
                         + ' it refuels, rearms and repairs itself.')
        notes.append('Crash it and it blows up, then comes back to its place after a few seconds.')
        if 'missiles' in equipment or 'Gun' in equipment or 'gun' in equipment:
            notes.append('Its weapons fire and explode for everyone to see, but vehicles from different snippets '
                         'cannot damage or lock on to each other yet.')
        parts.append(' '.join(notes))
        quest, windows = megabytes('android', slug), megabytes('windows', slug)
        if quest and windows:
            parts.append(f'For Quest and PC. Download: {quest:.0f} MB on Quest, {windows:.0f} MB on PC.')
        else:
            parts.append('For Quest and PC.')
        parts.append(extras.get('credit', SACC))
        description = '\n\n'.join(parts)
        if len(description) > 4000:
            raise SystemExit(f'{slug}: description is {len(description)} characters (4000 at most)')
        what = 'the water\'s surface' if boat else 'where it stands (set down on the floor below)'
        summary = (f'{lead} Position = {what}; the second number of rotation = the way it faces. '
                   + ('Put it on open water. ' if boat else 'Needs flat ground, clear of the spawn. ') +
                   'advanced-controls / advanced-button: Sacc\'s VR controls on at the start, and the switch for them. One per world.')
        out[slug] = {'title': title, 'summary': summary, 'description': description}
    path = os.path.join(os.path.dirname(__file__), 'kits.json')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write('\n')
    print(f'{len(out)} snippets written to {os.path.normpath(path)}; longest description '
          f'{max(len(v["description"]) for v in out.values())} characters')


if __name__ == '__main__':
    main()
