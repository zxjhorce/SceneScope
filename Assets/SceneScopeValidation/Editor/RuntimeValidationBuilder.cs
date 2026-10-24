using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SceneScope.Runtime;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace SceneScope.Validation
{
    public static class RuntimeValidationBuilder
    {
        [Serializable] sealed class Check { public string name; public bool passed; }
        [Serializable] sealed class Report { public bool passed; public List<Check> checks = new List<Check>(); public string error; }
        static Report report;
        static string output;

        public static void RunBatch()
        {
            report = new Report();
            output = Path.GetFullPath("artifacts/runtime");
            Directory.CreateDirectory(output);
            bool originalTiming = PlayerSettings.enableFrameTimingStats;
            try
            {
                ValidateAnalysis();
                string scenePath = BuildScene();
                PlayerSettings.enableFrameTimingStats = true;
                Build(scenePath, "Development", BuildOptions.Development);
                Build(scenePath, "Release", BuildOptions.None);
                report.passed = report.checks.All(x => x.passed);
            }
            catch (Exception exception) { report.error = exception.ToString(); Debug.LogException(exception); }
            finally
            {
                PlayerSettings.enableFrameTimingStats = originalTiming;
                File.WriteAllText(Path.Combine(output, "build-validation.json"), JsonUtility.ToJson(report, true));
                Debug.Log("SCENESCOPE_RUNTIME_BUILD: " + (report.passed ? "PASS" : "FAIL"));
                EditorApplication.Exit(report.passed ? 0 : 1);
            }
        }

        static void CheckThat(string name, bool condition)
        {
            report.checks.Add(new Check { name = name, passed = condition });
            if (!condition) throw new Exception("Validation failed: " + name);
        }

        static void ValidateAnalysis()
        {
            var ring = new FrameHistory(3);
            for (int i = 0; i < 5; i++) ring.Add(new FrameSample { frame = i });
            CheckThat("Bounded ring preserves chronological tail", ring.Count == 3 && ring.DroppedCount == 2 && ring.GetSample(0).frame == 2 && ring.GetSample(2).frame == 4);
            bool throws = false;
            try { ring.GetSample(3); } catch (ArgumentOutOfRangeException) { throws = true; }
            CheckThat("Ring rejects invalid index", throws);
            var capture = new CaptureReport { samples = new FrameSample[100], settings = new CaptureSettings { targetFps = 60, hitchMultiplier = 1.5f, gcAllocationWarningBytes = 1024, drawCallWarning = 100 } };
            for (int i = 0; i < capture.samples.Length; i++)
            {
                capture.samples[i] = FrameSample.Empty;
                capture.samples[i].frame = i;
                capture.samples[i].frameMs = i + 1;
                capture.samples[i].timeSeconds = (i + 1) * 0.1;
            }
            var summary = PerformanceAnalyzer.Summarize(capture);
            CheckThat("Nearest-rank percentiles", summary.p50Ms == 50 && summary.p95Ms == 95 && summary.p99Ms == 99);
            CheckThat("FPS uses total elapsed duration", Math.Abs(summary.averageFps - 100000d / 5050d) < 0.001);
            CheckThat("Hitch threshold is strict", summary.hitchFrames == 75 && !PerformanceAnalyzer.IsHitch(new FrameSample { frameMs = 25 }, capture.settings));
            CheckThat("Unavailable counters remain unknown", summary.totalGcAllocatedBytes == -1 && summary.gcCollections == -1 && summary.gpuP95Ms == -1 && summary.peakDrawCalls == -1);
            capture.samples[10].gpuMs = 8; capture.samples[10].gpuTimestamp = 123;
            capture.samples[11].gpuMs = 999; capture.samples[11].gpuTimestamp = 123;
            CheckThat("GPU duplicates excluded from percentiles", PerformanceAnalyzer.Summarize(capture).gpuP95Ms == 8);
            capture.samples[12].gcAllocatedBytes = 4096;
            capture.samples[12].drawCalls = 150;
            capture.samples[12].cpuMainMs = 80;
            capture.samples[12].gcCollections = 1;
            var findings = PerformanceAnalyzer.Analyze(capture);
            CheckThat("Measured CPU GC draw counter produce triage", findings.Any(x => x.category == "CPU") && findings.Any(x => x.category == "GC") && findings.Any(x => x.category == "Draw Call"));
            CheckThat("Empty capture has honest data finding", PerformanceAnalyzer.Analyze(new CaptureReport())[0].category == "数据");
            capture.scene = "<img src=x onerror=alert(1)> & test";
            string prefix = Path.Combine(output, "synthetic-unit-test");
            RuntimeReportExporter.ExportJson(capture, prefix + ".json");
            RuntimeReportExporter.ExportHtml(capture, prefix + ".html");
            RuntimeReportExporter.ExportCsv(capture, prefix + ".csv");
            var imported = RuntimeReportExporter.ImportJson(prefix + ".json");
            CheckThat("Report round trip retains unknowns", imported.samples.Length == 100 && imported.samples[0].drawCalls == -1);
            CheckThat("HTML encodes scene text", !File.ReadAllText(prefix + ".html").Contains(capture.scene) && File.ReadAllText(prefix + ".html").Contains("&lt;img"));
            CheckThat("CSV has header plus every observation", File.ReadAllLines(prefix + ".csv").Length == 101);
            File.WriteAllText(prefix + "-invalid.json", "{\"schemaVersion\":\"99.0\"}");
            throws = false;
            try { RuntimeReportExporter.ImportJson(prefix + "-invalid.json"); } catch (Exception) { throws = true; }
            CheckThat("Future schema is rejected", throws);
            var window = ScriptableObject.CreateInstance<SceneScope.Editor.PerformanceWindow>();
            try
            {
                window.CreateGUI();
                window.LoadReport(capture);
                CheckThat("Runtime panel loads report without Play", window.rootVisualElement.childCount > 0);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        static string BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Validation Camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 8, -14);
            camera.transform.LookAt(Vector3.zero);
            camera.backgroundColor = new Color(0.035f, 0.055f, 0.08f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            var light = new GameObject("Validation Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(40, -25, 0);
            var shader = Shader.Find("Standard");
            for (int i = 0; i < 64; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Draw counter fixture " + i;
                go.transform.position = new Vector3((i % 8 - 3.5f) * 1.2f, 0, (i / 8 - 3.5f) * 1.2f);
                var mat = new Material(shader) { color = Color.HSVToRGB(i / 64f, 0.6f, 0.9f), enableInstancing = false };
                string matPath = AssetDatabase.GenerateUniqueAssetPath("Assets/SceneScopeValidation/FixtureMaterial.mat");
                AssetDatabase.CreateAsset(mat, matPath);
                go.GetComponent<Renderer>().sharedMaterial = mat;
                go.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            }
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/SceneScopeValidation/RuntimeFixture.unity");
            if (!EditorSceneManager.SaveScene(scene, path)) throw new Exception("Failed to save runtime fixture scene.");
            return path;
        }

        static void Build(string scenePath, string name, BuildOptions options)
        {
            string directory = Path.Combine(output, name);
            Directory.CreateDirectory(directory);
            var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath }, target = BuildTarget.StandaloneWindows64,
                locationPathName = Path.Combine(directory, "SceneScopeValidation.exe"), options = options
            });
            CheckThat(name + " Player builds", build.summary.result == BuildResult.Succeeded);
        }
    }
}
