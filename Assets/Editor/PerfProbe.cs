using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>
    /// Where does a frame's main-thread time go with the world just sitting there? Arm it, enter Play mode:
    /// after a settling time it records a few seconds with the Profiler and writes the costliest samples
    /// (average self time per frame, main thread) to Logs/profile.txt, then leaves Play mode.
    /// Editor timings are not headset timings, but the ranking shows what to look at.
    /// </summary>
    [InitializeOnLoad]
    public static class PerfProbe
    {
        const string ArmedKey = "FlightWorld.PerfProbe.Armed";
        const string ReportPath = "Logs/profile.txt";
        const float Settle = 12f, Record = 4f;
        static double started = -1;
        static int firstFrame = -1;

        static PerfProbe()
        {
            if (SessionState.GetBool(ArmedKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("FlightWorld/Tools/Profile Idle World")]
        public static void Arm()
        {
            SessionState.SetBool(ArmedKey, true);
            File.WriteAllText(ReportPath, "armed\n");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!Application.isPlaying) return;
            if (started < 0) started = EditorApplication.timeSinceStartup;
            double t = EditorApplication.timeSinceStartup - started;
            if (t > Settle && firstFrame < 0)
            {
                ProfilerDriver.ClearAllFrames();
                ProfilerDriver.enabled = true;
                UnityEngine.Profiling.Profiler.enabled = true;
                firstFrame = Time.frameCount;
            }
            if (t <= Settle + Record || firstFrame < 0) return;

            var report = new StringBuilder();
            try
            {
                ProfilerDriver.enabled = false;
                int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
                var self = new Dictionary<string, double>();
                var total = new Dictionary<string, double>();
                var calls = new Dictionary<string, double>();
                int frames = 0;
                double frameMs = 0;
                for (int frame = Math.Max(first, last - 120); frame <= last; frame++)
                {
                    using (var view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                               HierarchyFrameDataView.columnSelfTime, false))
                    {
                        if (view == null || !view.valid) continue;
                        frames++;
                        frameMs += view.frameTimeMs;
                        var stack = new Stack<int>();
                        stack.Push(view.GetRootItemID());
                        var children = new List<int>();
                        while (stack.Count > 0)
                        {
                            int id = stack.Pop();
                            string name = view.GetItemName(id);
                            double selfMs = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);
                            double totalMs = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
                            double count = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnCalls);
                            self[name] = (self.TryGetValue(name, out var s) ? s : 0) + selfMs;
                            total[name] = (total.TryGetValue(name, out var tt) ? tt : 0) + totalMs;
                            calls[name] = (calls.TryGetValue(name, out var c) ? c : 0) + count;
                            children.Clear();
                            view.GetItemChildren(id, children);
                            foreach (int child in children) stack.Push(child);
                        }
                    }
                }
                if (frames == 0) throw new Exception("no profiler frames recorded");
                report.AppendLine($"{frames} frames, average frame {frameMs / frames:F2} ms (editor, main thread)");
                report.AppendLine("self ms/frame | total ms/frame | calls/frame | sample");
                foreach (var pair in self.OrderByDescending(p => p.Value).Take(70))
                    report.AppendLine($"{pair.Value / frames,9:F3} | {total[pair.Key] / frames,9:F3} | {calls[pair.Key] / frames,8:F1} | {pair.Key}");
                report.AppendLine();
                report.AppendLine("by total time:");
                foreach (var pair in total.OrderByDescending(p => p.Value).Take(60))
                    report.AppendLine($"{pair.Value / frames,9:F3} | {calls[pair.Key] / frames,8:F1} | {pair.Key}");
                report.AppendLine("DONE");
            }
            catch (Exception e)
            {
                report.AppendLine("FAILED: " + e);
                report.AppendLine("DONE");
            }
            File.WriteAllText(ReportPath, report.ToString());
            SessionState.SetBool(ArmedKey, false);
            EditorApplication.update -= Tick;
            started = -1;
            firstFrame = -1;
            EditorApplication.isPlaying = false;
        }
    }
}
