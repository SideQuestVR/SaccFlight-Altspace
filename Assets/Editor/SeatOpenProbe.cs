using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Can a player click every vehicle's seats? Arm it and enter Play mode: after 6 s (the graphs' seat checks run
    /// twice a second) it lists, per vehicle, how many pilot and passenger seat click areas are switched on, and
    /// leaves Play mode. Nobody is aboard, so all of them should be. Output: Logs/seat_open_probe.txt.
    /// </summary>
    [InitializeOnLoad]
    public static class SeatOpenProbe
    {
        const string ArmedKey = "FlightWorld.SeatOpenProbe.Armed";
        static double started = -1;

        static SeatOpenProbe()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Flight/Test/Probe Seats Clickable")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText("Logs/seat_open_probe.txt", "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { started = -1; return; }
            if (started < 0) { started = EditorApplication.timeSinceStartup; return; }
            if (EditorApplication.timeSinceStartup - started < 6) return;
            var text = new StringBuilder();
            int closed = 0;
            foreach (var rootName in new[] { "Aircraft", "GroundVehicles" })
            {
                var fleet = GameObject.Find(rootName);
                if (fleet == null) continue;
                foreach (Transform vehicle in fleet.transform)
                {
                    var variables = vehicle.GetComponent<Variables>();
                    if (variables == null || !variables.declarations.IsDefined("PassengerSeatColliders")) continue;
                    var pilot = variables.declarations.Get("PilotSeatCollider") as Collider;
                    var passengers = ((System.Collections.IEnumerable)variables.declarations.Get("PassengerSeatColliders")).Cast<Collider>().ToList();
                    int open = passengers.Count(c => c != null && c.enabled);
                    closed += passengers.Count - open + (pilot != null && pilot.enabled ? 0 : 1);
                    text.AppendLine($"{vehicle.name,-12} pilot seat {(pilot != null && pilot.enabled ? "open" : "CLOSED")}, passenger seats open {open} of {passengers.Count}");
                }
            }
            text.AppendLine(closed == 0 ? "OK: every seat can be clicked" : $"CLOSED: {closed} seat(s) cannot be clicked");
            File.WriteAllText("Logs/seat_open_probe.txt", text.ToString());
            SessionState.SetBool(ArmedKey, false);
            EditorApplication.update -= Tick;
            started = -1;
            EditorApplication.isPlaying = false;
        }
    }
}
