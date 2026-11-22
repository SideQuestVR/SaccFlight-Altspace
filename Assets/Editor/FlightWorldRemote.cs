using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Tiny file-based remote for driving this editor from outside (used by the coding agent when the
    /// Unity MCP server is not running). Drop a one-line command into Library/FlightWorld/command.txt:
    ///     refresh            re-scan assets and recompile scripts
    ///     menu:Some/Menu     run a menu item
    ///     play / stop        enter or leave Play mode
    ///     shot:name;x,y,z;tx,ty,tz;fov   render the scene into Library/FlightWorld/shots/name.jpg
    /// The outcome is written to Library/FlightWorld/result.txt; compiler errors and console errors are
    /// appended to Library/FlightWorld/log.txt. Local files only; nothing here ships in the world.
    /// </summary>
    [InitializeOnLoad]
    public static class FlightWorldRemote
    {
        const string Dir = "Library/FlightWorld";
        static readonly string CommandPath = Dir + "/command.txt";
        static readonly string ResultPath = Dir + "/result.txt";
        static readonly string LogPath = Dir + "/log.txt";
        static double nextPoll;

        static FlightWorldRemote()
        {
            Directory.CreateDirectory(Dir);
            EditorApplication.update += Poll;
            Application.logMessageReceived += OnLog;
            CompilationPipeline.assemblyCompilationFinished += OnCompiled;
            File.WriteAllText(Dir + "/ready.txt", DateTime.Now.ToString("o"));
        }

        static void OnCompiled(string assembly, CompilerMessage[] messages)
        {
            var errors = messages.Where(m => m.type == CompilerMessageType.Error).ToArray();
            if (errors.Length == 0) return;
            var sb = new StringBuilder();
            foreach (var e in errors) sb.AppendLine($"[compile] {e.file}({e.line}): {e.message}");
            File.AppendAllText(LogPath, sb.ToString());
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                File.AppendAllText(LogPath, $"[{type}] {condition}\n");
            }
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1.0;
            if (!File.Exists(CommandPath)) return;
            string command;
            try
            {
                command = File.ReadAllText(CommandPath).Trim();
                File.Delete(CommandPath);
            }
            catch (IOException)
            {
                return; // still being written; try again next tick
            }
            if (command.Length == 0) return;

            string result;
            try
            {
                if (command == "refresh")
                {
                    File.WriteAllText(LogPath, "");
                    AssetDatabase.Refresh();
                    result = "refresh requested";
                }
                else if (command.StartsWith("menu:"))
                {
                    result = EditorApplication.ExecuteMenuItem(command.Substring(5)) ? "menu ok" : "menu not found";
                }
                else if (command == "play")
                {
                    EditorApplication.isPlaying = true;
                    result = "entering play mode";
                }
                else if (command == "stop")
                {
                    EditorApplication.isPlaying = false;
                    result = "leaving play mode";
                }
                else if (command.StartsWith("shot:"))
                {
                    result = Shot(command.Substring(5));
                }
                else
                {
                    result = "unknown command";
                }
            }
            catch (Exception e)
            {
                result = "error: " + e.Message;
            }
            File.WriteAllText(ResultPath, $"{DateTime.Now:o} {command} -> {result}\n");
        }

        static Vector3 ParseVector(string text)
        {
            var v = text.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
            return new Vector3(v[0], v[1], v[2]);
        }

        /// <summary>shot:name;x,y,z;tx,ty,tz;fov - render the open scene from a point, looking at a target.</summary>
        static string Shot(string args)
        {
            var parts = args.Split(';');
            float fov = parts.Length > 3 ? float.Parse(parts[3], CultureInfo.InvariantCulture) : 60f;
            const int width = 1600, height = 900;
            var go = new GameObject("__RemoteShotCamera") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.transform.position = ParseVector(parts[1]);
                cam.transform.LookAt(ParseVector(parts[2]));
                cam.fieldOfView = fov;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 22000f;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                Directory.CreateDirectory(Dir + "/shots");
                File.WriteAllBytes($"{Dir}/shots/{parts[0]}.jpg", tex.EncodeToJPG(88));
                return "shot saved";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
