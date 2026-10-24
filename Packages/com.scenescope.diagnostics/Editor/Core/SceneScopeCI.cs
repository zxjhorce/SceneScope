using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SceneScope.Editor
{
    public static class SceneScopeCI
    {
        [Serializable]
        sealed class Summary
        {
            public string scannedAtUtc;
            public int scenes;
            public int errors;
            public int warnings;
            public string[] reports;
        }
        // Unity -batchmode -nographics -projectPath <project> -executeMethod
        // SceneScope.Editor.SceneScopeCI.ScanBuildScenes [-sceneScopeReport <path>] [-sceneScopeFailOnError]
        public static void ScanBuildScenes()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("此入口仅用于 batchmode，交互式诊断请使用 SceneScope 面板。");
            try
            {
                var scenes = EditorBuildSettings.scenes.Where(x => x.enabled).ToArray();
                if (scenes.Length == 0) throw new InvalidOperationException("Build Settings 中没有已启用且有效的场景。");
                var missing = scenes.Where(x => !File.Exists(x.path)).Select(x => x.path).ToArray();
                if (missing.Length > 0) throw new FileNotFoundException("已启用的构建场景不存在，停止扫描：" + string.Join(", ", missing));
                var arguments = Environment.GetCommandLineArgs();
                int reportIndex = Array.IndexOf(arguments, "-sceneScopeReport");
                string directory = reportIndex >= 0 && reportIndex + 1 < arguments.Length
                    ? arguments[reportIndex + 1] : Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ci");
                Directory.CreateDirectory(directory);
                var summary = new Summary { scannedAtUtc = DateTime.UtcNow.ToString("o"), scenes = scenes.Length, reports = new string[scenes.Length] };
                for (int i = 0; i < scenes.Length; i++)
                {
                    // Build scenes are often mutually exclusive levels. Diagnose each separately.
                    EditorSceneManager.OpenScene(scenes[i].path, OpenSceneMode.Single);
                    var result = SceneScanner.ScanSynchronously(new ScanSettings());
                    string reportName = i.ToString("D3") + "-" + Path.GetFileNameWithoutExtension(scenes[i].path);
                    ReportExporter.ExportJson(result, Path.Combine(directory, reportName + ".json"));
                    ReportExporter.ExportHtml(result, Path.Combine(directory, reportName + ".html"));
                    summary.reports[i] = reportName + ".html";
                    summary.errors += result.Count(IssueSeverity.Error);
                    summary.warnings += result.Count(IssueSeverity.Warning);
                }
                File.WriteAllText(Path.Combine(directory, "summary.json"), JsonUtility.ToJson(summary, true));
                bool fail = arguments.Contains("-sceneScopeFailOnError") && summary.errors > 0;
                Debug.Log($"SceneScope CI: scenes={scenes.Length}, errors={summary.errors}, warnings={summary.warnings}, report={directory}");
                EditorApplication.Exit(fail ? 2 : 0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }
    }
}
