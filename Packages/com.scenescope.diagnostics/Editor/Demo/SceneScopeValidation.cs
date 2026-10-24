using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SceneScope.Editor
{
    /// <summary>Offline integration checks; run with Unity -batchmode -executeMethod SceneScope.Editor.SceneScopeValidation.RunBatch.</summary>
    public static class SceneScopeValidation
    {
        [Serializable]
        private sealed class Check
        {
            public string name;
            public bool passed;
            public string detail;
        }

        [Serializable]
        private sealed class ValidationReport
        {
            public string utc;
            public string unityVersion;
            public string scenePath;
            public bool passed;
            public int checks;
            public int failed;
            public int issuesBeforeFix;
            public int issuesAfterFix;
            public int fixedRenderers;
            public SceneMetrics metrics;
            public List<Check> results = new List<Check>();
        }

        public static void RunBatch()
        {
            if (!Application.isBatchMode)
            {
                Debug.LogError("SceneScopeValidation.RunBatch is batch-only: it opens a fresh demo scene. Run it from a separate Unity -batchmode invocation.");
                return;
            }
            string artifactDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "artifacts"));
            Directory.CreateDirectory(artifactDirectory);
            var report = new ValidationReport { utc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion };
            try
            {
                report.scenePath = SceneScopeDemo.CreateScene();
                var scene = SceneManager.GetActiveScene();
                string savedScenePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", report.scenePath));
                Assert(report, "Demo scene saved", File.Exists(savedScenePath) && !scene.isDirty, report.scenePath);
                var settings = new ScanSettings();
                var initial = SceneScanner.ScanSynchronously(settings);
                report.issuesBeforeFix = initial.issues.Count;
                report.metrics = initial.metrics;
                Assert(report, "Read-only scan", !scene.isDirty, "Scanning must not mark the saved scene dirty.");
                Assert(report, "Scene scope", initial.scenePaths.Length == 1 && initial.scenePaths[0] == report.scenePath,
                    string.Join(", ", initial.scenePaths));
                Assert(report, "Metrics populated", initial.metrics.gameObjects > 100 && initial.metrics.renderers > 100 && initial.metrics.triangles >= 24192,
                    $"Objects={initial.metrics.gameObjects}; renderers={initial.metrics.renderers}; triangles={initial.metrics.triangles}");
                Assert(report, "Realtime light count", initial.metrics.realtimeShadowLights == 3, initial.metrics.realtimeShadowLights.ToString());
                foreach (string rule in new[] { "SS002", "SS003", "SS006", "SS007", "SS011", "SS015" })
                    Assert(report, "Intentional diagnostic " + rule, initial.issues.Any(x => x.ruleId == rule), "Expected deliberate demo issue.");
                Assert(report, "TextMesh labels are valid geometry", !initial.issues.Any(x => x.ruleId == "SS002"
                    && x.ResolveTarget() is Component component && component.GetComponent<TextMesh>() != null),
                    "TextMesh renders without a MeshFilter and must not be reported as a broken renderer.");
                Assert(report, "Settings are snapshotted", !ReferenceEquals(initial.settings, settings), "Results must own their settings snapshot.");

                var activeOnlySettings = settings.Copy();
                activeOnlySettings.includeInactive = false;
                var activeOnly = SceneScanner.ScanSynchronously(activeOnlySettings);
                Assert(report, "Inactive objects excluded", initial.metrics.gameObjects == activeOnly.metrics.gameObjects + 1
                    && initial.metrics.renderers == activeOnly.metrics.renderers + 1,
                    $"All/active objects={initial.metrics.gameObjects}/{activeOnly.metrics.gameObjects}; renderers={initial.metrics.renderers}/{activeOnly.metrics.renderers}");
                Assert(report, "Inactive geometry excluded", initial.metrics.triangles - activeOnly.metrics.triangles == 12096,
                    "The inactive spare shares the generated 12,096-triangle mesh.");
                Assert(report, "Inactive issues excluded", !activeOnly.issues.Any(x => x.objectPath.Contains("INACTIVE"))
                    && initial.issues.Any(x => x.objectPath.Contains("INACTIVE")), "The object filter also applies to diagnostics.");

                var relaxed = settings.Copy();
                relaxed.maxTrianglesPerMesh = 1000000;
                relaxed.maxHierarchyDepth = 100;
                relaxed.maxRealtimeShadowLights = 100;
                relaxed.smallObjectSize = 0.001f;
                var relaxedResult = SceneScanner.ScanSynchronously(relaxed);
                Assert(report, "Thresholds affect diagnostics", !relaxedResult.issues.Any(x => x.ruleId == "SS003" || x.ruleId == "SS006"
                    || x.ruleId == "SS011" || x.ruleId == "SS015"), "Relaxing mesh, shadow, light and hierarchy budgets removes only those findings.");
                Assert(report, "Thresholds preserve structural errors", relaxedResult.issues.Any(x => x.ruleId == "SS002")
                    && relaxedResult.issues.Any(x => x.ruleId == "SS007"), "Broken references and incompatible physics remain errors.");
                Assert(report, "Snapshot isolation", initial.settings.maxTrianglesPerMesh == 5000 && initial.settings.smallObjectSize == 0.6f,
                    "Later settings edits do not change an existing result.");

                var strict = settings.Copy();
                strict.maxSceneRenderers = 1;
                strict.maxSceneTriangles = 1;
                var strictResult = SceneScanner.ScanSynchronously(strict);
                Assert(report, "Whole-scene budgets", strictResult.issues.Any(x => x.ruleId == "SS012") && strictResult.issues.Any(x => x.ruleId == "SS013"),
                    "Both scene-level budget rules respond to configured limits.");

                bool completed = false;
                SceneScanner.Start(settings, null, result => completed = true);
                Assert(report, "Async scan scheduled", SceneScanner.IsScanning && !completed, "Start schedules incremental work on EditorApplication.update.");
                SceneScanner.Cancel();
                SceneScanner.Cancel();
                Assert(report, "Cancellation suppresses completion", !SceneScanner.IsScanning && !completed, "Cancel is idempotent and drops the completion callback.");

                var extension = new ValidationRule();
                SceneScanner.RegisterRule(extension);
                SceneScanner.RegisterRule(extension);
                try
                {
                    var extended = SceneScanner.ScanSynchronously(settings);
                    Assert(report, "Custom rules run once", extended.issues.Count(x => x.ruleId == "VALIDATION") == 1,
                        "Registering the same ISceneRule instance twice must not duplicate its findings.");
                }
                finally { SceneScanner.UnregisterRule(extension); }

                var fixable = initial.issues.Where(x => x.fix == FixKind.DisableShadowCasting).ToList();
                var originals = fixable.Select(x => x.ResolveTarget() as Renderer).Where(x => x != null).Distinct()
                    .ToDictionary(x => x, x => x.shadowCastingMode);
                Assert(report, "Demo exposes safe fixes", originals.Count == 8, "Eight intentionally small cubes cast shadows.");
                if (fixable.Count > 0)
                {
                    var issue = fixable[0];
                    var originalTarget = issue.ResolveTarget();
                    issue.target = null;
                    Assert(report, "Stable object navigation", issue.ResolveTarget() == originalTarget, "GlobalObjectId resolves a saved scene target.");
                }
                string preview = SceneFixer.Preview(fixable);
                Assert(report, "Fix preview is read-only", !scene.isDirty && originals.All(x => x.Key.shadowCastingMode == x.Value)
                    && preview.Contains("Off"), "Preview describes the change without modifying the scene.");
                // Duplicate rows exercise the fixer's target de-duplication.
                report.fixedRenderers = SceneFixer.Apply(fixable.Concat(fixable));
                Assert(report, "Apply changes each target once", report.fixedRenderers == originals.Count
                    && originals.Keys.All(x => x.shadowCastingMode == ShadowCastingMode.Off), report.fixedRenderers.ToString());
                var after = SceneScanner.ScanSynchronously(settings);
                report.issuesAfterFix = after.issues.Count;
                Assert(report, "Rescan reflects fixes", !after.issues.Any(x => x.fix == FixKind.DisableShadowCasting)
                    && after.issues.Count == initial.issues.Count - originals.Count, $"Issues {initial.issues.Count} → {after.issues.Count}");
                Assert(report, "Fix application is idempotent", SceneFixer.Apply(fixable) == 0, "Already fixed renderers are skipped.");
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert(report, "Undo restores all targets", originals.All(x => x.Key != null && x.Key.shadowCastingMode == x.Value),
                    "One Undo restores the complete batch.");
                var restored = SceneScanner.ScanSynchronously(settings);
                Assert(report, "Undo restores diagnostics", restored.issues.Count == initial.issues.Count
                    && restored.issues.Count(x => x.fix == FixKind.DisableShadowCasting) == originals.Count, "The original findings return after Undo.");

                string jsonPath = Path.Combine(artifactDirectory, "demo-report.json");
                string htmlPath = Path.Combine(artifactDirectory, "demo-report.html");
                ReportExporter.ExportJson(initial, jsonPath);
                ReportExporter.ExportHtml(initial, htmlPath);
                var jsonResult = JsonUtility.FromJson<ScanResult>(File.ReadAllText(jsonPath));
                Assert(report, "JSON round-trip", jsonResult != null && jsonResult.issues.Count == initial.issues.Count
                    && jsonResult.metrics.triangles == initial.metrics.triangles, jsonPath);
                Assert(report, "HTML report generated", File.Exists(htmlPath) && new FileInfo(htmlPath).Length > 1000, htmlPath);

                var hostileResult = JsonUtility.FromJson<ScanResult>(JsonUtility.ToJson(initial));
                const string hostileTitle = "<img src=x onerror=alert(1)>";
                hostileResult.issues[0].title = hostileTitle;
                hostileResult.issues[0].objectPath = "<script>alert('path')</script> & \"quoted\"";
                string encodedPath = Path.Combine(artifactDirectory, "html-encoding-check.html");
                ReportExporter.ExportHtml(hostileResult, encodedPath);
                string encodedHtml = File.ReadAllText(encodedPath);
                Assert(report, "HTML escapes user-controlled values", !encodedHtml.Contains(hostileTitle)
                    && !encodedHtml.Contains("<script>alert('path')</script>") && encodedHtml.Contains("&lt;img"),
                    "Object names and diagnostic strings must be encoded before entering an HTML report.");
                EditorSceneManager.SaveScene(scene);
            }
            catch (Exception exception)
            {
                report.results.Add(new Check { name = "Unhandled validation error", passed = false, detail = exception.ToString() });
                Debug.LogException(exception);
            }
            finally
            {
                SceneScanner.Cancel();
                report.checks = report.results.Count;
                report.failed = report.results.Count(x => !x.passed);
                report.passed = report.failed == 0;
                File.WriteAllText(Path.Combine(artifactDirectory, "validation.json"), JsonUtility.ToJson(report, true), new UTF8Encoding(false));
                Debug.Log($"SCENESCOPE_VALIDATION: {(report.passed ? "PASS" : "FAIL")} ({report.checks - report.failed}/{report.checks}); {artifactDirectory}");
                EditorApplication.Exit(report.passed ? 0 : 1);
            }
        }

        private static void Assert(ValidationReport report, string name, bool condition, string detail)
        {
            report.results.Add(new Check { name = name, passed = condition, detail = detail });
            if (condition) Debug.Log("[SceneScope PASS] " + name + ": " + detail);
            else Debug.LogError("[SceneScope FAIL] " + name + ": " + detail);
        }

        private sealed class ValidationRule : ISceneRule
        {
            public void Analyze(ScanContext context, GameObject gameObject)
            {
                if (gameObject.transform.parent == null)
                    context.Add("VALIDATION", "Extension rule", IssueSeverity.Info, IssueCategory.Structure,
                        gameObject, "Integration test marker.", "No action needed.");
            }
        }
    }
}
