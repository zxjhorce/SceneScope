using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SceneScope.Runtime;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SceneScope.Validation
{
    // Project-only harness, excluded from the distributed package. Workload injection is explicit via CLI.
    public sealed class RuntimeValidationDriver : MonoBehaviour
    {
        [Serializable] public sealed class Check { public string name; public bool passed; public string detail; }
        [Serializable] public sealed class Result { public bool passed; public string unityVersion; public string graphicsApi; public bool development; public List<Check> checks = new List<Check>(); }
        SceneScopeCapture capture;
        string output;
        int frames;
        double started;
        byte[] allocation;
        Camera fixtureCamera;
        RenderTexture fixtureTarget;
        static readonly ProfilerMarker intermittentMarker = new ProfilerMarker("SceneScope.Validation.Intermittent");
        ProfilerRecorder intermittentRecorder;
        readonly long[] intermittentValues = new long[4];
        readonly Result result = new Result();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-scenescopeValidationOutput");
            if (Application.isEditor || index < 0 || index + 1 >= args.Length) return;
            var driver = new GameObject("SceneScope explicit validation workload").AddComponent<RuntimeValidationDriver>();
            driver.output = args[index + 1];
        }

        void Start()
        {
            try
            {
                Directory.CreateDirectory(output);
                Application.runInBackground = true;
                Application.targetFrameRate = 120;
                QualitySettings.vSyncCount = 0;
                result.unityVersion = Application.unityVersion;
                result.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
                result.development = Debug.isDebugBuild;
                capture = gameObject.AddComponent<SceneScopeCapture>();
                capture.autoStart = false;
                capture.StartCapture(new CaptureSettings { warmupFrames = 30, maxSamples = 600, drawCallWarning = 10, setPassWarning = 10 });
                started = Time.realtimeSinceStartupAsDouble;
                if (!Debug.isDebugBuild)
                {
                    CheckResult("Release capture remains inert", !capture.IsRecording && capture.Count == 0 && SceneScopeCapture.Active == null, "No recorders or history started in a release Player.");
                    Finish();
                }
                else
                {
                    // Hidden/headless validation has no presented backbuffer. Explicitly issue real rendering work.
                    fixtureCamera = Camera.allCameras[0];
                    fixtureTarget = new RenderTexture(960, 540, 24);
                    fixtureTarget.Create();
                    fixtureCamera.targetTexture = fixtureTarget;
                    fixtureCamera.enabled = false;
                    intermittentRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "SceneScope.Validation.Intermittent", 1);
                }
            }
            catch (Exception exception) { CheckResult("Startup", false, exception.ToString()); Finish(); }
        }

        void Update()
        {
            if (capture == null || !Debug.isDebugBuild) return;
            try
            {
                frames++;
                if (frames >= 61 && frames <= 64 && intermittentRecorder.Valid && intermittentRecorder.Count > 0)
                    intermittentValues[frames - 61] = intermittentRecorder.LastValue;
                if (frames == 60)
                    using (intermittentMarker.Auto())
                    {
                        var probe = Stopwatch.StartNew();
                        while (probe.Elapsed.TotalMilliseconds < 2) { }
                    }
                if (fixtureCamera != null) fixtureCamera.Render();
                // Known test signals, not claimed as results from a real game.
                if (frames == 100 || frames == 180)
                {
                    var timer = Stopwatch.StartNew();
                    while (timer.Elapsed.TotalMilliseconds < 55) { }
                }
                if (frames == 125) allocation = new byte[4 * 1024 * 1024];
                if (frames == 140) { allocation = null; GC.Collect(); }
                if (frames >= 300 || Time.realtimeSinceStartupAsDouble - started > 20)
                {
                    var report = capture.StopCapture();
                    CheckResult("Development capture produced history", report != null && report.samples.Length > 180, report == null ? "null report" : report.samples.Length.ToString());
                    if (report != null)
                    {
                        var summary = PerformanceAnalyzer.Summarize(report);
                        long maxAlloc = -1, maxDraws = -1;
                        double maxCpu = -1;
                        int gpuCount = 0;
                        var gpuIds = new HashSet<ulong>();
                        bool distinctGpu = true;
                        bool frameOrder = true;
                        for (int i = 0; i < report.samples.Length; i++)
                        {
                            var s = report.samples[i];
                            maxCpu = Math.Max(maxCpu, s.cpuMainMs);
                            maxAlloc = Math.Max(maxAlloc, s.gcAllocatedBytes);
                            maxDraws = Math.Max(maxDraws, s.drawCalls);
                            if (s.gpuMs > 0) { gpuCount++; distinctGpu &= s.gpuTimestamp > 0 && gpuIds.Add(s.gpuTimestamp); }
                            if (i > 0) frameOrder &= s.frame > report.samples[i - 1].frame;
                        }
                        CheckResult("CPU marker observes injected workload", maxCpu >= 40, maxCpu + " ms");
                        CheckResult("Frame history catches injected hitches", summary.hitchFrames >= 2 && summary.maxFrameMs >= 40, summary.hitchFrames + " hitches; worst=" + summary.maxFrameMs);
                        CheckResult("GC allocation counter observes 4MiB allocation", maxAlloc >= 4 * 1024 * 1024, maxAlloc.ToString());
                        CheckResult("GC collection count observes forced collection", summary.gcCollections >= 1, summary.gcCollections.ToString());
                        CheckResult("Actual rendering counter is nonzero", maxDraws > 0, maxDraws.ToString());
                        CheckResult("Intermittent marker does not retain stale timing", intermittentValues[0] > 0 && intermittentValues[1] == 0 && intermittentValues[2] == 0 && intermittentValues[3] == 0,
                            string.Join(",", intermittentValues));
                        CheckResult("Frame order is chronological", frameOrder, "Observation frame IDs increase.");
                        CheckResult("GPU timestamps are fresh and unique", distinctGpu, gpuCount + " GPU samples; unavailable hardware is allowed and stays -1.");
                        CheckResult("Stopped capture releases active instance", !capture.IsRecording && SceneScopeCapture.Active == null, "Native recorders disposed on Stop.");
                        RuntimeReportExporter.ExportJson(report, Path.Combine(output, "capture.json"));
                        RuntimeReportExporter.ExportCsv(report, Path.Combine(output, "capture.csv"));
                        RuntimeReportExporter.ExportHtml(report, Path.Combine(output, "capture.html"));
                        var imported = RuntimeReportExporter.ImportJson(Path.Combine(output, "capture.json"));
                        CheckResult("Player report round trips", imported.samples.Length == report.samples.Length, "Import preserves retained history.");
                        capture.StartCapture(new CaptureSettings { warmupFrames = 0, maxSamples = 60 });
                        CheckResult("Capture can restart cleanly", capture.IsRecording && capture.Count == 0, "Fresh capture owns fresh history.");
                        capture.enabled = false;
                        CheckResult("Disable stops capture", !capture.IsRecording && SceneScopeCapture.Active == null, "No active collector remains.");
                    }
                    Finish();
                }
            }
            catch (Exception exception) { CheckResult("Runtime execution", false, exception.ToString()); Finish(); }
        }

        void CheckResult(string name, bool passed, string detail)
        {
            result.checks.Add(new Check { name = name, passed = passed, detail = detail });
            Debug.Log("[SceneScope runtime " + (passed ? "PASS" : "FAIL") + "] " + name + ": " + detail);
        }

        void Finish()
        {
            enabled = false;
            if (intermittentRecorder.Valid) intermittentRecorder.Dispose();
            if (fixtureCamera != null) fixtureCamera.targetTexture = null;
            if (fixtureTarget != null) { fixtureTarget.Release(); Destroy(fixtureTarget); }
            if (capture != null && capture.IsRecording) capture.StopCapture();
            result.passed = result.checks.Count > 0 && result.checks.TrueForAll(x => x.passed);
            File.WriteAllText(Path.Combine(output, "validation.json"), JsonUtility.ToJson(result, true));
            Application.Quit(result.passed ? 0 : 1);
        }
    }
}
