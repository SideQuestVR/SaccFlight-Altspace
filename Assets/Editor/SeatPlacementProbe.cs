using System.IO;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Read-only, edit mode: for every vehicle seat in the scene, where its eye point is from the seat's origin in the
    /// seat's own axes. Every seat should show the app's seated eye offset (FleetParts.SeatEyeAbove up, SeatEyeAhead
    /// ahead). Output: Logs/seat_placement_probe.txt.
    /// </summary>
    public static class SeatPlacementProbe
    {
        [MenuItem("FlightWorld/Flight/Test/Probe Seat Placement")]
        public static void Probe()
        {
            var text = new StringBuilder();
            int seats = 0, off = 0;
            float want = FlightGraphs.FleetParts.SeatEyeAbove, ahead = FlightGraphs.FleetParts.SeatEyeAhead;
            foreach (var rootName in new[] { "Aircraft", "GroundVehicles" })
            {
                var fleet = GameObject.Find(rootName);
                if (fleet == null) continue;
                foreach (Transform vehicle in fleet.transform)
                {
                    var seatType = FlightGraphs.GraphTypes.Find("BS.BSSeat");
                    foreach (var seat in vehicle.GetComponentsInChildren(seatType, true))
                    {
                        var eye = seat.transform.parent != null ? seat.transform.parent.Find("EyePoint " + seat.name) : null;
                        if (eye == null) continue;
                        seats++;
                        var local = Quaternion.Inverse(seat.transform.rotation) * (eye.position - seat.transform.position);
                        bool good = Mathf.Abs(local.y - want) < 0.005f && Mathf.Abs(local.z - ahead) < 0.005f && Mathf.Abs(local.x) < 0.005f;
                        if (!good) { off++; text.AppendLine($"OFF {vehicle.name}/{seat.name}: eye at {local.x:F3}, {local.y:F3}, {local.z:F3}"); }
                    }
                }
            }
            text.AppendLine(off == 0 ? $"OK: all {seats} seats put the eyes {want:F2} m above and {ahead:F2} m ahead of their origin" : $"{off} of {seats} seats off");
            File.WriteAllText("Logs/seat_placement_probe.txt", text.ToString());
        }
    }
}
