using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// A test, not a change: moves everything in the open scene so that the arrival point is at the world origin,
    /// WITHOUT saving, so the jitter probe can be run both ways. "Reload Scene From Disk" puts it back.
    /// </summary>
    public static class OriginShiftTest
    {
        static readonly Vector3 Arrival = new Vector3(14804.72f, 0f, 7042.08f);

        [MenuItem("FlightWorld/Tools/Test: Shift Origin To Airfield (unsaved)")]
        public static void Shift()
        {
            var scene = EditorSceneManager.GetActiveScene();
            int moved = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                root.transform.position -= Arrival;
                moved++;
            }
            File.WriteAllText("Logs/origin_shift.txt", $"moved {moved} root objects by {-Arrival} (scene not saved)\n");
        }

        [MenuItem("FlightWorld/Tools/Test: Reload Scene From Disk")]
        public static void Reload()
        {
            var path = EditorSceneManager.GetActiveScene().path;
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            File.WriteAllText("Logs/origin_shift.txt", $"reloaded {path} from disk\n");
        }
    }
}
