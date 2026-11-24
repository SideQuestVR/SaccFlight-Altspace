using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Do the guns make a sound? Arm it and enter Play mode: after 3 s it sets every aircraft's gun animator bool (as the
    /// graph does when the trigger is held), waits a second, and lists each vehicle's gun sound objects (any AudioSource
    /// whose clip or object name says gun) with their state; then clears the bools and leaves Play mode.
    /// Output: Logs/gun_sound_probe.txt.
    /// </summary>
    [InitializeOnLoad]
    public static class GunSoundProbe
    {
        const string ArmedKey = "FlightWorld.GunSoundProbe.Armed";
        static double started = -1, fired = -1;

        static GunSoundProbe()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Probe Gun Sounds")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText("Logs/gun_sound_probe.txt", "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static string Describe(AudioSource a) =>
            $"{a.transform.parent?.name}/{a.name}: active {a.gameObject.activeInHierarchy} enabled {a.enabled} playing {a.isPlaying} " +
            $"clip {(a.clip ? a.clip.name : "-")} vol {a.volume:F2} loop {a.loop} blend {a.spatialBlend:F1} max {a.maxDistance:F0} " +
            $"mixer {(a.outputAudioMixerGroup ? a.outputAudioMixerGroup.name : "-")} mute {a.mute}";

        static bool IsGun(AudioSource a) =>
            a.name.ToLowerInvariant().Contains("gun") || (a.clip != null && a.clip.name.ToLowerInvariant().Contains("gun"));

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; return; }
            if (started < 0) started = EditorApplication.timeSinceStartup;
            var now = EditorApplication.timeSinceStartup;
            var fleet = GameObject.Find("Aircraft");
            if (fleet == null) return;
            if (fired < 0 && now - started > 3)
            {
                foreach (Transform v in fleet.transform)
                {
                    var an = v.GetComponentInChildren<Animator>(true);
                    if (an == null) continue;
                    an.enabled = true;
                    if (an.parameters.Any(p => p.name == "gunfiring")) an.SetBool("gunfiring", true);
                }
                fired = now;
                return;
            }
            if (fired > 0 && now - fired > 1.0)
            {
                var text = new StringBuilder();
                foreach (Transform v in fleet.transform)
                {
                    var an = v.GetComponentInChildren<Animator>(true);
                    bool hasParam = an != null && an.parameters.Any(p => p.name == "gunfiring");
                    var guns = v.GetComponentsInChildren<AudioSource>(true).Where(IsGun).ToList();
                    text.AppendLine($"== {v.name}: animator {(an ? (an.enabled ? "on" : "off") : "none")} gunfiring param {hasParam} culling {(an ? an.cullingMode.ToString() : "-")}");
                    foreach (var a in guns) text.AppendLine("   " + Describe(a));
                    if (an != null && hasParam) an.SetBool("gunfiring", false);
                }
                File.WriteAllText("Logs/gun_sound_probe.txt", text.ToString());
                SessionState.SetBool(ArmedKey, false);
                EditorApplication.update -= Tick;
                fired = -1;
                started = -1;
                EditorApplication.isPlaying = false;
            }
        }
    }
}
