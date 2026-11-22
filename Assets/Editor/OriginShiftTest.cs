using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Puts the scene's origin under the arrival point (see CitySceneBuilder.OriginOnMap for why). The city
    /// builder places everything that way itself; this moves a scene that was built with the origin in the middle
    /// of the city, once: every root object by the same amount, and the sea's map alignment with it. Vehicles,
    /// signs and seats were placed relative to the arrival point, so they come along unchanged. Saves the scene.
    /// Doing it again does nothing (the arrival point is already at the origin).
    /// </summary>
    public static class OriginShiftTest
    {
        const string ReportPath = "Logs/origin_shift.txt";

        [MenuItem("FlightWorld/City/Move World Origin To Arrival Point")]
        public static void Shift()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var spawn = GameObject.Find("RunwaySpawn");
            if (spawn == null) { File.WriteAllText(ReportPath, "FAILED: no RunwaySpawn in the scene\n"); return; }
            var offset = new Vector3(spawn.transform.position.x, 0f, spawn.transform.position.z);
            if (offset.magnitude < 0.01f) { File.WriteAllText(ReportPath, $"already done: the arrival point is at {spawn.transform.position}\nOK\n"); return; }

            int moved = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                root.transform.position -= offset;
                EditorUtility.SetDirty(root.transform);
                moved++;
            }
            var sea = AssetDatabase.LoadAssetAtPath<Material>("Assets/City/Materials/Sea.mat");
            string seaNote = "no sea material";
            if (sea != null)
            {
                var rect = sea.GetVector("_WorldRect");
                sea.SetVector("_WorldRect", new Vector4(rect.x - offset.x, rect.y - offset.z, rect.z, rect.w));
                EditorUtility.SetDirty(sea);
                AssetDatabase.SaveAssets();
                seaNote = $"sea map corner {rect.x:F1}, {rect.y:F1} -> {rect.x - offset.x:F1}, {rect.y - offset.z:F1}";
            }
            Physics.SyncTransforms();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            File.WriteAllText(ReportPath, $"moved {moved} root objects by {-offset}; {seaNote}; arrival point now at {spawn.transform.position}; scene saved\nOK\n");
        }

        [MenuItem("FlightWorld/Tools/Test: Reload Scene From Disk")]
        public static void Reload()
        {
            var path = EditorSceneManager.GetActiveScene().path;
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            File.WriteAllText(ReportPath, $"reloaded {path} from disk\n");
        }
    }
}
