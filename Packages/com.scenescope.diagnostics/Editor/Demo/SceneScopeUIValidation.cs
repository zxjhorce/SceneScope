using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace SceneScope.Editor
{
    /// <summary>Exercises real EditorWindow construction and the editor update driven scan without OS input.</summary>
    public static class SceneScopeUIValidation
    {
        static SceneScopeWindow window;
        static double started;
        static int ticks;
        static bool requested;
        static readonly BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;

        public static void RunBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Batch mode only.");
            try
            {
                EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path);
                SceneScopeWindow.Open();
                window = EditorWindow.GetWindow<SceneScopeWindow>();
                window.CreateGUI();
                if (window.rootVisualElement.Q<Button>(className: "scan-button") == null)
                    throw new Exception("Scan button is missing from the real window tree.");
                if (window.rootVisualElement.styleSheets.count == 0)
                    throw new Exception("SceneScope stylesheet did not load.");
                started = EditorApplication.timeSinceStartup;
                EditorApplication.update += Tick;
            }
            catch (Exception exception) { Finish(exception); }
        }

        static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup - started > 30) throw new TimeoutException("UI scan timed out.");
                if (++ticks < 5) return; // Allow import/object-change notifications to settle.
                if (!requested)
                {
                    requested = true;
                    typeof(SceneScopeWindow).GetMethod("StartScan", Members).Invoke(window, null);
                    return;
                }
                if (SceneScanner.IsScanning) return;
                var result = (ScanResult)typeof(SceneScopeWindow).GetField("result", Members).GetValue(window);
                if (result == null || result.issues.Count == 0) throw new Exception("Window did not receive its asynchronous result.");
                if (window.rootVisualElement.Q<ListView>() == null) throw new Exception("Diagnostic ListView missing.");
                var baseline = JsonUtility.FromJson<ScanResult>(JsonUtility.ToJson(result));
                baseline.settings.includeInactive = !result.settings.includeInactive;
                typeof(SceneScopeWindow).GetField("baseline", Members).SetValue(window, baseline);
                bool matches = (bool)typeof(SceneScopeWindow).GetMethod("BaselineMatches", Members).Invoke(window, null);
                if (matches) throw new Exception("Incompatible baseline scope accepted.");
                // Exercise the same stale-result gate used by hierarchy, Inspector and Undo notifications.
                typeof(SceneScopeWindow).GetMethod("MarkStale", Members).Invoke(window, null);
                if (!(bool)typeof(SceneScopeWindow).GetField("stale", Members).GetValue(window))
                    throw new Exception("Stale-result gate did not engage.");
                Finish(null);
            }
            catch (Exception exception) { Finish(exception); }
        }

        static void Finish(Exception exception)
        {
            EditorApplication.update -= Tick;
            SceneScanner.Cancel();
            if (window != null) window.Close();
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "artifacts"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "ui-validation.txt"), exception == null
                ? "PASS: EditorWindow construction, stylesheet loading, scan control, asynchronous completion, ListView, incompatible baseline rejection, stale-result gate.\nThis is a programmatic UI smoke check, not a rendered screenshot review."
                : "FAIL: " + exception);
            if (exception != null) Debug.LogException(exception);
            else Debug.Log("SCENESCOPE_UI_VALIDATION: PASS");
            EditorApplication.Exit(exception == null ? 0 : 1);
        }
    }
}
