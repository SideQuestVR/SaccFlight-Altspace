using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BS.SDKEditor;
using Newtonsoft.Json.Linq;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// Publishes the vehicle kits (SnippetKits.cs) as snippets, with the account the Creator SDK's Builder window is
    /// signed in to:
    ///
    ///   1  Bundles And Photos To CDN     each kit's Quest and Windows bundle and its photograph, to cdn.sidequestvr.com
    ///                                    (a file that has not changed since it was last sent is not sent again); then
    ///                                    writes the addresses into Assets/WebRoot/vehicle-kits.js
    ///   2  Loader To World               vehicle-kits.js to the world selected in the Builder, as one of its extra
    ///                                    files: https://&lt;world&gt;.worldspace.host/vehicle-kits.js (the snippets' script)
    ///   3  Create Or Update Snippets     one snippet per kit (api.sidequestvr.com/v2/snippets): created the first
    ///                                    time, updated after that
    ///
    /// What was sent where is kept in Tools/snippets/manifest.json; titles and descriptions come from
    /// Tools/snippets/kits.json. Progress: Logs/snippet_upload.txt (ends with DONE or FAILED).
    /// A snippet's slug can never be used again once it exists, even after the snippet is deleted.
    /// </summary>
    public static class SnippetUpload
    {
        const string ManifestPath = "Tools/snippets/manifest.json";
        const string TextsPath = "Tools/snippets/kits.json";
        const string LogPath = "Logs/snippet_upload.txt";
        const string CdnRoot = "https://cdn.sidequestvr.com/";
        const string LoaderName = "vehicle-kits.js";
        const string LoaderPath = "Assets/WebRoot/" + LoaderName;
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static StringBuilder log;
        static bool running;

        static void Say(string line)
        {
            log.AppendLine(line);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath, log.ToString());
        }

        static JObject Manifest() => File.Exists(ManifestPath) ? JObject.Parse(File.ReadAllText(ManifestPath)) : new JObject { ["kits"] = new JObject() };

        static void Save(JObject manifest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath));
            File.WriteAllText(ManifestPath, manifest.ToString(Newtonsoft.Json.Formatting.Indented) + "\n");
        }

        static string Sha(byte[] data)
        {
            using (var sha = SHA1.Create()) return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        static EditorWindow Builder()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("BuilderWindow", false)).FirstOrDefault(t => t != null);
            return type == null ? null : Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().FirstOrDefault();
        }

        /// <summary>The Builder's signed-in session, or null with the reason logged.</summary>
        static SqEditorAppApi Api(out EditorWindow window)
        {
            window = Builder();
            if (window == null) { Say("FAILED: the Builder window is not open (Creator SDK > Builder)"); return null; }
            var api = window.GetType().GetField("sq", Any)?.GetValue(window) as SqEditorAppApi;
            if (api == null || api.User == null) { Say("FAILED: the Builder is not signed in"); return null; }
            return api;
        }

        static void Start(Func<IEnumerator> work)
        {
            if (running) { Debug.LogWarning("[Snippets] an upload is already running"); return; }
            running = true;
            log = new StringBuilder();
            EditorCoroutineUtility.StartCoroutineOwnerless(Run(work()));
        }

        static IEnumerator Run(IEnumerator work)
        {
            // (stepped by hand so that an exception ends the run with a line in the log instead of silently)
            var stack = new Stack<IEnumerator>();
            stack.Push(work);
            while (stack.Count > 0)
            {
                bool more;
                try { more = stack.Peek().MoveNext(); }
                catch (Exception e) { Say("FAILED: " + e); break; }
                if (!more) { stack.Pop(); continue; }
                if (stack.Peek().Current is IEnumerator inner) stack.Push(inner);
                else yield return stack.Peek().Current;
            }
            running = false;
        }

        // ------------------------------------------------------------------ 1: files

        [MenuItem("FlightWorld/Snippets/Upload/1 Bundles And Photos To CDN")]
        public static void Files() => Start(UploadFiles);

        static IEnumerator UploadFiles()
        {
            var api = Api(out _);
            if (api == null) yield break;
            var manifest = Manifest();
            var kits = (JObject)manifest["kits"];
            int sent = 0, kept = 0, failed = 0;
            foreach (var kit in SnippetKits.Chosen())
            {
                var entry = kits[kit.Slug] as JObject ?? (JObject)(kits[kit.Slug] = new JObject());
                foreach (var (key, file, name) in new[]
                         {
                             ("android", $"{SnippetKits.BundleRoot}/android/{kit.Slug}", $"{kit.Slug}-quest.bin"),
                             ("windows", $"{SnippetKits.BundleRoot}/windows/{kit.Slug}", $"{kit.Slug}-windows.bin"),
                             ("photo", $"{SnippetKits.BundleRoot}/photos/{kit.Slug}.jpg", $"{kit.Slug}.jpg"),
                         })
                {
                    if (!File.Exists(file)) { Say($"FAILED {kit.Slug} {key}: no file {file}"); failed++; continue; }
                    var data = File.ReadAllBytes(file);
                    string sha = Sha(data);
                    if ((string)entry[key]?["sha"] == sha) { kept++; continue; }
                    SqEditorCreateUpload done = null;
                    Exception error = null;
                    yield return api.UploadFile(name, data, null, u => done = u, e => error = e, null);
                    if (done == null || string.IsNullOrEmpty(done.Path))
                    {
                        Say($"FAILED {kit.Slug} {key}: {error?.Message ?? "no answer"}");
                        failed++;
                        continue;
                    }
                    entry[key] = new JObject { ["sha"] = sha, ["path"] = done.Path, ["file"] = done.FileId, ["bytes"] = data.Length };
                    Save(manifest);
                    sent++;
                    Say($"sent {kit.Slug} {key}: {data.Length / 1048576f:F1} MB -> {CdnRoot}{done.Path}");
                }
            }
            WriteLoaderTable(manifest);
            Say($"{sent} files sent, {kept} unchanged, {failed} failed");
            Say(failed == 0 ? "DONE" : "FAILED");
        }

        /// <summary>The loader's table of bundle addresses, from the manifest.</summary>
        static void WriteLoaderTable(JObject manifest)
        {
            const string open = "/*KITS*/", close = "/*END KITS*/";
            string script = File.ReadAllText(LoaderPath);
            int from = script.IndexOf(open, StringComparison.Ordinal), to = script.IndexOf(close, StringComparison.Ordinal);
            if (from < 0 || to < from) throw new Exception($"{LoaderPath} has lost its {open} ... {close} markers");
            var table = new StringBuilder(open + "\n  const KITS = {\n");
            foreach (var kit in SnippetKits.All)
            {
                var entry = manifest["kits"]?[kit.Slug];
                string windows = (string)entry?["windows"]?["path"], android = (string)entry?["android"]?["path"];
                if (windows == null || android == null) continue;
                table.Append($"    \"{kit.Slug}\": {{ windows: \"{CdnRoot}{windows}\", android: \"{CdnRoot}{android}\" }},\n");
            }
            table.Append("  };\n  ");
            string updated = script.Substring(0, from) + table + script.Substring(to);
            if (updated != script) File.WriteAllText(LoaderPath, updated);
        }

        // ------------------------------------------------------------------ 2: the loader script

        [MenuItem("FlightWorld/Snippets/Upload/2 Loader To World")]
        public static void Loader() => Start(UploadLoader);

        static IEnumerator UploadLoader()
        {
            var api = Api(out var window);
            if (api == null) yield break;
            var world = window.GetType().GetField("selectedWorld", Any)?.GetValue(window) as SqEditorWorld;
            if (world == null || string.IsNullOrEmpty(world.Slug)) { Say("FAILED: no world is selected in the Builder"); yield break; }
            var manifest = Manifest();
            WriteLoaderTable(manifest);
            var data = File.ReadAllBytes(LoaderPath);
            bool ok = false;
            Exception error = null;
            yield return api.UploadFileToWorld(LoaderName, data, world.WorldId, world.Slug, _ => ok = true, e => error = e, UploadAssetType.Extra, UploadAssetTypePlatform.Any, null);
            if (!ok) { Say($"FAILED: {LoaderName} to {world.Slug}: {error?.Message ?? "no answer"}"); yield break; }
            string address = $"https://{world.Slug}.worldspace.host/{LoaderName}";
            manifest["loader"] = new JObject { ["sha"] = Sha(data), ["url"] = address };
            Save(manifest);
            Say($"sent {LoaderName} ({data.Length} bytes) -> {address}");
            Say("DONE");
        }

        // ------------------------------------------------------------------ 3: the snippets

        static string Xml(string text) => System.Security.SecurityElement.Escape(text);

        /// <summary>A kit's snippet markup: what a world's page gets when somebody adds the snippet.</summary>
        public static string Code(SnippetKits.Kit kit, JObject texts, string script)
        {
            var n = File.ReadAllText($"{SnippetKits.PrefabFolder}/{kit.Slug}.txt").Split(' ').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            // the box is drawn about the snippet's position: wide and long enough to cover the vehicle either side of it
            float wide = 2f * Mathf.Max(Mathf.Abs(n[0] - n[3] * 0.5f), Mathf.Abs(n[0] + n[3] * 0.5f));
            float deep = 2f * Mathf.Max(Mathf.Abs(n[2] - n[5] * 0.5f), Mathf.Abs(n[2] + n[5] * 0.5f));
            string size = FormattableString.Invariant($"{wide:F1} 0.2 {deep:F1}");
            return $"<bs-snippet name=\"{kit.Slug}\" title=\"{Xml((string)texts["title"])}\" single=\"true\"\n" +
                   $"  description=\"{Xml((string)texts["summary"])}\"\n" +
                   $"  script=\"{script}\"\n" +
                   // ahead of the world's origin, where most worlds put their spawn: the sign about 12 m in front of an
                   // arriving player, the vehicle beyond it (at 0 0 0 a vehicle stood on the players arriving)
                   FormattableString.Invariant($"  position=\"0 0 {Mathf.Round(Mathf.Max(0f, -(n[2] - n[5] * 0.5f)) + 15f):F0}\"\n") +
                   "  rotation=\"0 0 0\"" +
                   (kit.Kind == SnippetKits.Kind.Boat ? "" : "\n  snap-to-ground=\"true\"") +
                   // (the aircraft and the boat: Sacc's advanced VR controls on from the start, and the switch for them by the sign)
                   (kit.Kind == SnippetKits.Kind.Car || kit.Kind == SnippetKits.Kind.Tank ? "" : "\n  advanced-controls=\"false\"\n  advanced-button=\"true\"") +
                   ">\n" +
                   "  <bs-gizmo type=\"position\" attribute=\"position\"/>\n" +
                   $"  <bs-gizmo type=\"box\" attribute=\"position\" size=\"{size}\"/>\n" +
                   "</bs-snippet>\n";
        }

        [MenuItem("FlightWorld/Snippets/Upload/3 Create Or Update Snippets")]
        public static void Snippets() => Start(UploadSnippets);

        /// <summary>Writes every snippet's markup to Logs/snippet_code.txt without sending anything.</summary>
        [MenuItem("FlightWorld/Snippets/Upload/Preview Snippet Markup")]
        public static void Preview()
        {
            var manifest = Manifest();
            var texts = JObject.Parse(File.ReadAllText(TextsPath));
            var text = new StringBuilder();
            foreach (var kit in SnippetKits.Chosen())
            {
                var t = texts[kit.Slug] as JObject;
                if (t == null) { text.AppendLine($"NO TEXTS for {kit.Slug}"); continue; }
                text.AppendLine($"=== {kit.Slug}: \"{t["title"]}\" ({((string)t["description"]).Length} characters of description), image {manifest["kits"]?[kit.Slug]?["photo"]?["path"]}");
                text.AppendLine(Code(kit, t, (string)manifest["loader"]?["url"] ?? "https://pilots.worldspace.host/" + LoaderName));
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/snippet_code.txt", text.ToString());
        }

        static IEnumerator UploadSnippets()
        {
            var api = Api(out _);
            if (api == null) yield break;
            var manifest = Manifest();
            string script = (string)manifest["loader"]?["url"];
            if (string.IsNullOrEmpty(script)) { Say("FAILED: the loader script has not been sent yet (step 2)"); yield break; }
            var texts = JObject.Parse(File.ReadAllText(TextsPath));
            var send = typeof(SqEditorAppApi).GetMethod("JsonPost", Any).MakeGenericMethod(typeof(JObject));
            int created = 0, updated = 0, kept = 0, failed = 0;
            foreach (var kit in SnippetKits.Chosen())
            {
                var entry = manifest["kits"]?[kit.Slug] as JObject;
                var t = texts[kit.Slug] as JObject;
                string photo = (string)entry?["photo"]?["path"];
                if (entry == null || t == null || photo == null || entry["windows"] == null || entry["android"] == null)
                {
                    Say($"FAILED {kit.Slug}: its files have not all been sent, or it has no texts");
                    failed++;
                    continue;
                }
                var body = new JObject
                {
                    ["name"] = (string)t["title"],
                    ["description"] = (string)t["description"],
                    ["image_url"] = photo,
                    ["code"] = Code(kit, t, script),
                };
                string sha = Sha(Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None)));
                long id = (long?)entry["snippet"] ?? 0;
                if (id > 0 && (string)entry["snippet_sha"] == sha) { kept++; continue; }
                if (id == 0) body["slug"] = kit.Slug;
                JObject answer = null;
                Exception error = null;
                yield return (IEnumerator)send.Invoke(api, new object[]
                {
                    id == 0 ? "/v2/snippets" : $"/v2/snippets/{id}", body, (Action<JObject>)(r => answer = r), (Action<Exception>)(e => error = e),
                    true, false, id == 0 ? "POST" : "PATCH",
                });
                long got = (long?)answer?["snippets_id"] ?? 0;
                if (got == 0)
                {
                    Say($"FAILED {kit.Slug}: {error?.Message ?? answer?.ToString(Newtonsoft.Json.Formatting.None) ?? "no answer"}");
                    failed++;
                    continue;
                }
                entry["snippet"] = got;
                entry["snippet_sha"] = sha;
                Save(manifest);
                if (id == 0) created++; else updated++;
                Say($"{(id == 0 ? "created" : "updated")} {kit.Slug}: snippet {got}, slug {answer["slug"]}");
            }
            Say($"{created} created, {updated} updated, {kept} unchanged, {failed} failed");
            Say(failed == 0 ? "DONE" : "FAILED");
        }
    }
}
