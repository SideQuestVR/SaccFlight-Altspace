// Vehicle kits: the loader behind the "sacc-..." snippets (one aircraft, helicopter, car, tank or boat each).
//
// A snippet element names a vehicle and says where it stands:
//
//   <bs-snippet name="sacc-sf-1" script="https://pilots.worldspace.host/vehicle-kits.js" single="true"
//               position="0 0 0" rotation="0 0 0"></bs-snippet>
//
// position is the ground under the vehicle's wheels (for the boat: the water's surface) and rotation turns it
// (degrees; the second number is the heading). For each element this script makes one object there and loads the
// vehicle's kit into it: an asset bundle for the Quest and one for Windows, holding one prefab with everything the
// vehicle needs (its flight graph, seats, reset sign, clock and weapons). Nothing else has to be in the world.
//
// It also sends the keyboard to the vehicles' graphs, for players on a desktop (the keys are listed below).
(function () {
  "use strict";
  if (window.__vehicleKits) return;
  window.__vehicleKits = true;

  // Where each kit's bundles are. Written by the project's upload tool (FlightWorld/Snippets/Upload): do not edit by hand.
  /*KITS*/
  const KITS = {
    "sacc-sf-1": { windows: "https://cdn.sidequestvr.com/file/5001953/sacc-sf-1-windows.bin", android: "https://cdn.sidequestvr.com/file/5001952/sacc-sf-1-quest.bin" },
    "sacc-sf-2": { windows: "https://cdn.sidequestvr.com/file/5001955/sacc-sf-2-windows.bin", android: "https://cdn.sidequestvr.com/file/5001954/sacc-sf-2-quest.bin" },
    "sacc-sb-1": { windows: "https://cdn.sidequestvr.com/file/5001957/sacc-sb-1-windows.bin", android: "https://cdn.sidequestvr.com/file/5001956/sacc-sb-1-quest.bin" },
    "sacc-sx-tr3": { windows: "https://cdn.sidequestvr.com/file/5001959/sacc-sx-tr3-windows.bin", android: "https://cdn.sidequestvr.com/file/5001958/sacc-sx-tr3-quest.bin" },
    "sacc-wasp-wx3": { windows: "https://cdn.sidequestvr.com/file/5001961/sacc-wasp-wx3-windows.bin", android: "https://cdn.sidequestvr.com/file/5001960/sacc-wasp-wx3-quest.bin" },
    "sacc-stunt-plane": { windows: "https://cdn.sidequestvr.com/file/5001963/sacc-stunt-plane-windows.bin", android: "https://cdn.sidequestvr.com/file/5001962/sacc-stunt-plane-quest.bin" },
    "sacc-grvr-1": { windows: "https://cdn.sidequestvr.com/file/5001965/sacc-grvr-1-windows.bin", android: "https://cdn.sidequestvr.com/file/5001964/sacc-grvr-1-quest.bin" },
    "sacc-sc-1": { windows: "https://cdn.sidequestvr.com/file/5001967/sacc-sc-1-windows.bin", android: "https://cdn.sidequestvr.com/file/5001966/sacc-sc-1-quest.bin" },
    "sacc-a320": { windows: "https://cdn.sidequestvr.com/file/5001979/sacc-a320-windows.bin", android: "https://cdn.sidequestvr.com/file/5001970/sacc-a320-quest.bin" },
    "sacc-sh-1": { windows: "https://cdn.sidequestvr.com/file/5001982/sacc-sh-1-windows.bin", android: "https://cdn.sidequestvr.com/file/5001981/sacc-sh-1-quest.bin" },
    "sacc-sh-2": { windows: "https://cdn.sidequestvr.com/file/5001985/sacc-sh-2-windows.bin", android: "https://cdn.sidequestvr.com/file/5001983/sacc-sh-2-quest.bin" },
    "sacc-sh-3": { windows: "https://cdn.sidequestvr.com/file/5001987/sacc-sh-3-windows.bin", android: "https://cdn.sidequestvr.com/file/5001986/sacc-sh-3-quest.bin" },
    "sacc-sd-1": { windows: "https://cdn.sidequestvr.com/file/5001990/sacc-sd-1-windows.bin", android: "https://cdn.sidequestvr.com/file/5001988/sacc-sd-1-quest.bin" },
    "sacc-balloon": { windows: "https://cdn.sidequestvr.com/file/5001993/sacc-balloon-windows.bin", android: "https://cdn.sidequestvr.com/file/5001992/sacc-balloon-quest.bin" },
    "sacc-ss-1": { windows: "https://cdn.sidequestvr.com/file/5001996/sacc-ss-1-windows.bin", android: "https://cdn.sidequestvr.com/file/5001995/sacc-ss-1-quest.bin" },
    "sacc-baron": { windows: "https://cdn.sidequestvr.com/file/5001998/sacc-baron-windows.bin", android: "https://cdn.sidequestvr.com/file/5001997/sacc-baron-quest.bin" },
    "sacc-iccarus": { windows: "https://cdn.sidequestvr.com/file/5002000/sacc-iccarus-windows.bin", android: "https://cdn.sidequestvr.com/file/5001999/sacc-iccarus-quest.bin" },
    "sacc-shoreline": { windows: "https://cdn.sidequestvr.com/file/5002005/sacc-shoreline-windows.bin", android: "https://cdn.sidequestvr.com/file/5002004/sacc-shoreline-quest.bin" },
    "sacc-thunder": { windows: "https://cdn.sidequestvr.com/file/5002007/sacc-thunder-windows.bin", android: "https://cdn.sidequestvr.com/file/5002006/sacc-thunder-quest.bin" },
    "sacc-tank": { windows: "https://cdn.sidequestvr.com/file/5002009/sacc-tank-windows.bin", android: "https://cdn.sidequestvr.com/file/5002008/sacc-tank-quest.bin" },
  };
  /*END KITS*/

  const made = new Map(); // element -> the object made for it (or true while it is being made)

  function triple(text) {
    if (!text) return null;
    const parts = text.trim().split(/\s+/).map(Number);
    return parts.length === 3 && !parts.some(isNaN) ? new BS.Vector3(parts[0], parts[1], parts[2]) : null;
  }

  function webAddress(text) {
    return typeof text === "string" && /^https?:\/\//i.test(text.trim()) ? text.trim() : "";
  }

  function ours(el) {
    if (!el || !el.getAttribute) return false;
    const name = el.getAttribute("name") || "";
    return Object.prototype.hasOwnProperty.call(KITS, name) || !!(el.getAttribute("windows") || el.getAttribute("android"));
  }

  // A single-instance snippet's second copy is skipped by the page, and must be here too.
  function skipped(el) {
    const single = el.getAttribute("single");
    if (single === null || single === "false") return false;
    const name = el.getAttribute("name") || "";
    if ("ownsSingleton" in el) return el.ownsSingleton !== name;
    for (const other of made.keys()) if (other !== el && other.getAttribute("name") === name) return true;
    return false;
  }

  async function place(el) {
    if (made.has(el) || !ours(el) || skipped(el)) return;
    made.set(el, true);
    const name = el.getAttribute("name") || "vehicle";
    const kit = KITS[name] || {};
    const windows = webAddress(el.getAttribute("windows")) || kit.windows || "";
    const android = webAddress(el.getAttribute("android")) || kit.android || "";
    if (!windows && !android) {
      console.warn("[vehicle-kits] no bundles known for snippet '" + name + "'");
      return;
    }
    try {
      let position = triple(el.getAttribute("position"));
      // snap-to-ground (default true; the boat floats at the height it is given): set the vehicle down on the first
      // floor at or below its position (from 3 m above it), and wait for that floor to exist. A snippet can load before
      // the world's own scene, and a vehicle put where there is no floor yet had nothing to stand on.
      if (yes(el.getAttribute("snap-to-ground"), name !== "sacc-ss-1")) {
        const at = position || new BS.Vector3(0, 0, 0);
        const scene = BS.Scene.GetInstance();
        let floor = null;
        for (let tries = 0; tries < 60 && floor === null && el.isConnected; tries++) {
          try {
            const hit = await scene.Raycast(new BS.Vector3(at.x, at.y + 3, at.z), new BS.Vector3(0, -1, 0), 200);
            if (hit) {
              const parts = hit.split("¶");
              if (Number(parts[6]) > 0.7) floor = Number(parts[3]);
            }
          } catch (e) {
            /* no physics yet */
          }
          if (floor === null) await new Promise((r) => setTimeout(r, 1000));
        }
        if (floor !== null) position = new BS.Vector3(at.x, floor, at.z);
        else console.warn("[vehicle-kits] " + name + ": no floor found under " + at.x + " " + at.y + " " + at.z + " in 60 s; placed as given");
      }
      if (!el.isConnected) {
        made.delete(el);
        return;
      }
      const holder = new BS.GameObject("snippet:" + name);
      await holder.Async();
      const transform = {};
      const rotation = triple(el.getAttribute("rotation"));
      if (position) transform.localPosition = position;
      if (rotation) transform.localEulerAngles = rotation;
      if (position || rotation) await holder.SetTransform(transform);
      if (!el.isConnected) {
        holder.Destroy();
        made.delete(el);
        return;
      }
      await holder.AddComponent(new BS.AssetBundle(windows, "", "", android, "", "", false, false));
      await holder.AddComponent(new BS.KitItem("*"));
      made.set(el, holder);
      console.log("[vehicle-kits] " + name + " placed");
    } catch (e) {
      console.error("[vehicle-kits] could not place " + name + ": " + (e && e.message ? e.message : e));
    }
  }

  function remove(el) {
    const holder = made.get(el);
    made.delete(el);
    if (holder && holder !== true) {
      try {
        holder.Destroy();
      } catch (e) {
        /* already gone */
      }
    }
  }

  // A kit's clock graph asks for its snippet's settings when it starts (InjectJS): 1 or 0.
  //   advanced-controls="true"   Sacc's advanced VR controls start on for every player (default false)
  //   advanced-button="false"    no switch beside the sign for players to turn them on and off (default true)
  function yes(text, fallback) {
    if (text === null || text === undefined || String(text).trim() === "") return fallback;
    return /^(true|on|yes|1)$/i.test(String(text).trim());
  }
  window.__vehicleKitsSetting = function (slug, what) {
    let el = null;
    for (const candidate of made.keys()) if (candidate.getAttribute("name") === slug) el = candidate;
    if (!el) el = document.querySelector('bs-snippet[name="' + String(slug).replace(/[^a-z0-9-]/gi, "") + '"]');
    if (!el) return -1;
    if (what === "advanced") return yes(el.getAttribute("advanced-controls"), false) ? 1 : 0;
    if (what === "button") return yes(el.getAttribute("advanced-button"), true) ? 1 : 0;
    return -1;
  };

  function scan() {
    document.querySelectorAll("bs-snippet").forEach((el) => place(el));
  }

  // ---- the keyboard, for desktop players --------------------------------------------------------------------
  //   W / S (or Up / Down)     aircraft: nose down / up        cars: accelerate / brake and reverse
  //   A / D (or Left / Right)  aircraft: roll                  cars: steer
  //   Q / E                    rudder                          Left Shift / Left Ctrl   throttle up / down
  //   B wheel brakes   G gear   V flaps   Space handbrake   R / F tilt the engines (VTOL)
  //   I / K, J / L, U / O      thrust forward / back, left / right, down / up (drone, helicopters)
  const down = {};
  const sent = {};

  function axis(positive, negative) {
    return (down[positive] ? 1 : 0) - (down[negative] ? 1 : 0);
  }

  function clamp(value) {
    return Math.max(-1, Math.min(1, value));
  }

  function publish(scene) {
    const controls = {
      fw_pitch: clamp(axis("W", "S") + axis("UpArrow", "DownArrow")),
      fw_roll: clamp(axis("A", "D") + axis("LeftArrow", "RightArrow")),
      fw_yaw: axis("E", "Q"),
      fw_throttle: axis("LeftShift", "LeftControl"),
      fw_brake: down.B ? 1 : 0,
      fw_drive: clamp(axis("W", "S") + axis("UpArrow", "DownArrow")),
      fw_steer: clamp(axis("D", "A") + axis("RightArrow", "LeftArrow")),
      fw_handbrake: down.Space ? 1 : 0,
      fw_vtol: axis("R", "F"),
      fw_thrust_x: axis("L", "J"),
      fw_thrust_y: axis("O", "U"),
      fw_thrust_z: axis("I", "K"),
      fw_gear: down.G ? 1 : 0,
      fw_flaps: down.V ? 1 : 0,
    };
    for (const id in controls) {
      if (sent[id] !== controls[id]) {
        sent[id] = controls[id];
        scene.SendToVisualScripting(id, controls[id]);
      }
    }
  }

  function keyboard(scene) {
    const names = {};
    for (const name in BS.KeyCode) names[BS.KeyCode[name]] = name;
    scene.On("key-press", (e) => {
      down[names[e.detail.key]] = true;
      publish(scene);
    });
    scene.On("key-release", (e) => {
      down[names[e.detail.key]] = false;
      publish(scene);
    });
  }

  window.addEventListener("bs-loaded", () => {
    const scene = BS.Scene.GetInstance();
    try {
      keyboard(scene);
    } catch (e) {
      console.warn("[vehicle-kits] no keyboard controls: " + (e && e.message ? e.message : e));
    }
    scan();
    // snippets added to or taken out of the page later
    new MutationObserver((changes) => {
      for (const change of changes) {
        change.removedNodes.forEach((node) => {
          if (made.has(node)) remove(node);
        });
        if (change.addedNodes.length) setTimeout(scan, 0);
      }
    }).observe(document.documentElement, { childList: true, subtree: true });
  });
})();
