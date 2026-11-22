// Flight World page script.
//
// Desktop flight controls: Visual Scripting graphs cannot read the keyboard, so this page listens for
// keys and forwards the state of each control to the aircraft graph. Only changes are sent.
//
//   W / S (or Up / Down)   nose down / nose up
//   A / D (or Left / Right) roll left / roll right
//   Q / E                   rudder left / right (also steers the nose wheel on the ground)
//   Left Shift / Left Ctrl  throttle up / down (the last fifth of the lever is afterburner)
//   B                       wheel brakes
(function () {
  "use strict";

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
      // cars and the tank: W / S = accelerate / brake (hold S at a standstill to reverse), A / D = steer,
      // Space = handbrake
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

  window.addEventListener("bs-loaded", () => {
    const scene = BS.Scene.GetInstance();
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
  });
})();
