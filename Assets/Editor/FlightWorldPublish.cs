using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Drives the Creator SDK's Builder window without clicking: runs its checklist, then the same
    /// build-and-upload path its "Build & upload" button uses (scene, world and sign-in are whatever
    /// the open Builder window has). Uses reflection because the Builder exposes no public API.
    /// Reports go to Logs/publish_report.txt; build and upload progress is in the Builder's own log.
    /// </summary>
    public static class FlightWorldPublish
    {
        const string ReportPath = "Logs/publish_report.txt";
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static EditorWindow Builder(StringBuilder report)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("BuilderWindow", false)).FirstOrDefault(t => t != null);
            if (type == null)
            {
                report.AppendLine("Creator SDK BuilderWindow type not found");
                return null;
            }
            var window = Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().FirstOrDefault();
            if (window == null) report.AppendLine("Builder window is not open (Creator SDK > Builder)");
            return window;
        }

        static object Field(object target, string name) => target.GetType().GetField(name, Any)?.GetValue(target);
        static object Property(object target, string name) => target?.GetType().GetProperty(name, Any)?.GetValue(target);

        static void Describe(EditorWindow window, StringBuilder report)
        {
            var world = Field(window, "selectedWorld");
            var api = Field(window, "sq");
            var autoUpload = Field(window, "autoUpload");
            report.AppendLine($"scene: {Field(window, "scenePath")}");
            report.AppendLine($"world: {(world == null ? "none selected" : Property(world, "Slug") ?? world.GetType().GetField("Slug", Any)?.GetValue(world))}");
            report.AppendLine($"signed in: {(api != null && Property(api, "User") != null)}");
            report.AppendLine($"upload after building: {Property(autoUpload, "value")}");
        }

        static object RunChecklist(EditorWindow window, StringBuilder report, bool forBuild)
        {
            var method = window.GetType().GetMethod("RunChecklist", Any);
            var result = method.Invoke(window, new object[] { forBuild, true });
            if (result == null)
            {
                report.AppendLine("checklist did not run");
                return null;
            }
            report.AppendLine($"checklist: {Property(result, "Summary")}");
            var blockers = ((IEnumerable)Property(result, "Blockers")).Cast<object>().ToList();
            foreach (var issue in (IEnumerable)Property(result, "Issues"))
            {
                bool blocks = blockers.Contains(issue);
                // (the issue type's members have been renamed between SDK versions: print whatever it has)
                var members = issue.GetType().GetFields(Any).Select(f => (f.Name, value: f.GetValue(issue)))
                    .Concat(issue.GetType().GetProperties(Any).Where(p => p.GetIndexParameters().Length == 0).Select(p => (p.Name, value: p.GetValue(issue))))
                    .Where(m => m.value != null && !m.Name.Contains("BackingField") && !(m.value is Delegate))
                    .Select(m => $"{m.Name}={(m.value is UnityEngine.Object o ? o.name : m.value)}");
                report.AppendLine($"  [{(blocks ? "BLOCKS BUILD" : "issue")}] {string.Join(" | ", members)}");
            }
            return result;
        }

        static void Finish(StringBuilder report)
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(ReportPath, report.ToString());
            Debug.Log("[Publish] " + report);
        }

        [MenuItem("FlightWorld/Publish/Run Builder Checklist")]
        public static void Check()
        {
            var report = new StringBuilder();
            try
            {
                var window = Builder(report);
                if (window != null)
                {
                    Describe(window, report);
                    RunChecklist(window, report, false);
                }
            }
            catch (Exception e)
            {
                report.AppendLine("FAILED: " + (e.InnerException ?? e));
            }
            Finish(report);
        }

        [MenuItem("FlightWorld/Publish/Build And Upload")]
        public static void BuildAndUpload()
        {
            var report = new StringBuilder();
            try
            {
                var window = Builder(report);
                if (window == null)
                {
                    Finish(report);
                    return;
                }
                Describe(window, report);
                EditorSceneManager.SaveOpenScenes();                      // otherwise the Builder asks with a dialog
                window.GetType().GetMethod("BuildAssetBundles", Any).Invoke(window, null);
                var confirm = Field(window, "confirmCallback") as Action;
                if (confirm == null)
                {
                    report.AppendLine("NOT STARTED: the Builder did not reach its confirmation (blocked by the checklist, or nothing selected)");
                    RunChecklist(window, report, true);
                    Finish(report);
                    return;
                }
                report.AppendLine("STARTED: build confirmed; see the Builder log for progress");
                Finish(report);                                           // written before the long, blocking build
                var dialog = Field(window, "buildConfirm") as UnityEngine.UIElements.VisualElement;
                if (dialog != null) dialog.style.display = UnityEngine.UIElements.DisplayStyle.None;
                confirm();
            }
            catch (Exception e)
            {
                report.AppendLine("FAILED: " + (e.InnerException ?? e));
                Finish(report);
            }
        }
    }
}
