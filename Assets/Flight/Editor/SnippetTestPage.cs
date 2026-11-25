using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BS.SDKEditor;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The page that tries the vehicle snippets in the real app (Tools/snippets/kittest.html): this world's city as the
    /// ground and the snippets on free flat ground near the arrival point. Served from the world's address as an extra
    /// file, https://pilots.worldspace.host/kittest.html; open it with ?qa=&lt;anything&gt; for a private room and
    /// ?kits=slug,slug for some of them.
    ///   FlightWorld/Snippets/Test/Write Test Page    finds the spots (in the open, built scene) -> Library/FlightWorld/kittest.html
    ///   FlightWorld/Snippets/Test/Upload Test Page   sends it to the world selected in the Builder
    /// Report: Logs/snippet_test_page.txt.
    /// </summary>
    public static class SnippetTestPage
    {
        const string Template = "Tools/snippets/kittest.html";
        const string Output = "Library/FlightWorld/kittest.html";
        const string LogPath = "Logs/snippet_test_page.txt";

        [MenuItem("FlightWorld/Snippets/Test/Write Test Page")]
        public static void Write()
        {
            var report = new StringBuilder();
            try
            {
                var spawn = GameObject.Find("RunwaySpawn") ?? throw new Exception("no RunwaySpawn in the open scene");
                Physics.SyncTransforms();
                float heading = spawn.transform.eulerAngles.y;
                var facing = Quaternion.Euler(0f, heading, 0f);
                // candidate places: rings round the arrival point, nearest first
                var candidates = new List<Vector3>();
                for (float r = 60f; r <= 900f; r += 30f)
                    for (float a = 0f; a < 360f; a += 360f / Mathf.Max(8f, Mathf.Floor(2f * Mathf.PI * r / 30f)))
                        candidates.Add(spawn.transform.position + Quaternion.Euler(0f, a, 0f) * Vector3.forward * r);
                var taken = new List<Bounds>();
                var spots = new List<string>();
                foreach (var kit in SnippetKits.All.Where(k => k.Kind != SnippetKits.Kind.Boat))
                {
                    var n = File.ReadAllText($"{SnippetKits.PrefabFolder}/{kit.Slug}.txt").Split(' ').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    // what the kit covers, in its own axes: the vehicle, and its sign and panel behind it, with room round them
                    float halfX = Mathf.Max(Mathf.Abs(n[0] - n[3] * 0.5f), Mathf.Abs(n[0] + n[3] * 0.5f)) + 6f;
                    float front = n[2] + n[5] * 0.5f + 6f, back = Mathf.Max(-(n[2] - n[5] * 0.5f), 4f) + 14f;
                    float tall = Mathf.Min(n[1] + n[4] * 0.5f, 40f);
                    bool placed = false;
                    foreach (var c in candidates)
                    {
                        if (!Physics.Raycast(c + Vector3.up * 300f, Vector3.down, out var hit, 600f, ~0, QueryTriggerInteraction.Ignore) || hit.normal.y < 0.997f) continue;
                        var centre = hit.point + facing * new Vector3(0f, 0f, (front - back) * 0.5f);
                        var half = new Vector3(halfX, tall * 0.5f, (front + back) * 0.5f);
                        var box = new Bounds(centre + Vector3.up * (tall * 0.5f + 0.4f), facing * half * 2f);
                        box.extents = new Vector3(Mathf.Abs(box.extents.x), Mathf.Abs(box.extents.y), Mathf.Abs(box.extents.z)) + new Vector3(half.z, 0f, half.x) * 0f;
                        if (taken.Any(b => b.Intersects(box))) continue;
                        if (Physics.CheckBox(centre + Vector3.up * (tall * 0.5f + 0.4f), half, facing, ~0, QueryTriggerInteraction.Ignore)) continue;
                        // level all over: the ground at the corners within 0.3 m of the middle
                        bool level = true;
                        foreach (var corner in new[] { new Vector3(-halfX, 0, -back), new Vector3(halfX, 0, -back), new Vector3(-halfX, 0, front), new Vector3(halfX, 0, front) })
                        {
                            var p = hit.point + facing * corner;
                            if (!Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out var h2, 100f, ~0, QueryTriggerInteraction.Ignore) || Mathf.Abs(h2.point.y - hit.point.y) > 0.3f) { level = false; break; }
                        }
                        if (!level) continue;
                        taken.Add(new Bounds(centre, new Vector3(2f * Mathf.Max(halfX, (front + back) * 0.5f), 100f, 2f * Mathf.Max(halfX, (front + back) * 0.5f))));
                        spots.Add(FormattableString.Invariant($"[{hit.point.x:F2},{hit.point.y:F2},{hit.point.z:F2},{heading:F1}]"));
                        report.AppendLine(FormattableString.Invariant($"{kit.Slug}: {hit.point:F1}, {Vector3.Distance(hit.point, spawn.transform.position):F0} m from the arrival point"));
                        placed = true;
                        break;
                    }
                    if (!placed) { report.AppendLine($"FAILED {kit.Slug}: no free spot"); spots.Add("null"); }
                }
                string page = File.ReadAllText(Template);
                const string open = "/*SPOTS*/", close = "/*END SPOTS*/";
                int from = page.IndexOf(open, StringComparison.Ordinal), to = page.IndexOf(close, StringComparison.Ordinal);
                page = page.Substring(0, from + open.Length) + "[" + string.Join(",", spots) + "]" + page.Substring(to);
                File.WriteAllText(Output, page);
                report.AppendLine($"written {Output}");
            }
            catch (Exception e) { report.AppendLine("FAILED: " + e); }
            File.WriteAllText(LogPath, report.ToString());
        }

        [MenuItem("FlightWorld/Snippets/Test/Upload Test Page")]
        public static void Upload() => EditorCoroutineUtility.StartCoroutineOwnerless(UploadPage());

        /// <summary>
        /// Sends one file to one of the signed-in account's worlds. Library/FlightWorld/world_upload.txt, three lines: the
        /// world's address slug (e.g. 048a74), the local file, and the name it gets there ("index.html" replaces the
        /// world's page; anything else is an extra file). Report: Logs/world_upload.txt.
        /// </summary>
        [MenuItem("FlightWorld/Snippets/Test/Upload File To World")]
        public static void UploadToWorld() => EditorCoroutineUtility.StartCoroutineOwnerless(UploadFile());

        static IEnumerator UploadFile()
        {
            const string report = "Logs/world_upload.txt";
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var lines = File.ReadAllLines("Library/FlightWorld/world_upload.txt").Select(l => l.Trim()).Where(l => l != "").ToArray();
            string slug = lines[0], local = lines[1], name = lines[2];
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("BuilderWindow", false)).FirstOrDefault(t => t != null);
            var window = type == null ? null : Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().FirstOrDefault();
            var api = window?.GetType().GetField("sq", any)?.GetValue(window) as SqEditorAppApi;
            if (api == null || api.User == null) { File.WriteAllText(report, "FAILED: the Builder is not open or not signed in\n"); yield break; }
            List<SqEditorWorld> worlds = null;
            Exception error = null;
            yield return api.ListWorlds(w => worlds = w, e => error = e);
            var world = worlds?.FirstOrDefault(w => w.Slug == slug);
            if (world == null) { File.WriteAllText(report, $"FAILED: no world '{slug}' on this account ({worlds?.Count ?? 0} worlds; {error?.Message})\n"); yield break; }
            bool ok = false;
            var kind = name == "index.html" ? UploadAssetType.Index : UploadAssetType.Extra;
            yield return api.UploadFileToWorld(name, File.ReadAllBytes(local), world.WorldId, world.Slug, _ => ok = true, e => error = e, kind, UploadAssetTypePlatform.Any, null);
            File.WriteAllText(report, ok ? $"sent {local} -> https://{world.Slug}.worldspace.host/{name} (world {world.WorldId}, '{world.Name}')\nDONE\n" : $"FAILED: {error?.Message}\n");
        }

        static IEnumerator UploadPage()
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("BuilderWindow", false)).FirstOrDefault(t => t != null);
            var window = type == null ? null : Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().FirstOrDefault();
            var api = window?.GetType().GetField("sq", any)?.GetValue(window) as SqEditorAppApi;
            var world = window?.GetType().GetField("selectedWorld", any)?.GetValue(window) as SqEditorWorld;
            if (api == null || api.User == null || world == null) { File.WriteAllText(LogPath, "FAILED: the Builder is not open, signed in, or has no world\n"); yield break; }
            bool ok = false;
            Exception error = null;
            yield return api.UploadFileToWorld("kittest.html", File.ReadAllBytes(Output), world.WorldId, world.Slug, _ => ok = true, e => error = e,
                UploadAssetType.Extra, UploadAssetTypePlatform.Any, null);
            File.WriteAllText(LogPath, ok ? $"sent -> https://{world.Slug}.worldspace.host/kittest.html\nDONE\n" : $"FAILED: {error?.Message}\n");
        }
    }
}

