using UnityEditor;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Stops every armed scripted test from running at the next Play. A test that never reaches its end (the editor
    /// left Play mode under it) stays armed for the editor session and then runs alongside the next one, taking over
    /// the pilot vehicle and the controller events.
    /// </summary>
    public static class DisarmTests
    {
        [MenuItem("FlightWorld/Flight/Test/Disarm All Tests")]
        public static void Disarm()
        {
            foreach (var key in new[] { "FlightWorld.FunctionsTest.Armed", "FlightWorld.AdvancedControlsTest.Armed", "FlightWorld.BalloonPushTest.Armed",
                                        "FlightWorld.SeatOpenProbe.Armed" })
                SessionState.SetBool(key, false);
            UnityEngine.Debug.Log("[DisarmTests] every scripted test disarmed (restart Play to drop any running now)");
        }
    }
}
