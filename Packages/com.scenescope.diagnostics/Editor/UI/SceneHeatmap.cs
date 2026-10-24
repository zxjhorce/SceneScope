using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SceneScope.Editor
{
    /// <summary>Scene-view instance complexity overlay. It intentionally makes no GPU-time claims.</summary>
    [InitializeOnLoad]
    public static class SceneHeatmap
    {
        private struct Entry
        {
            public Renderer renderer;
            public long triangles;
        }

        private const int DrawLimit = 600;
        private static readonly List<Entry> entries = new List<Entry>();
        private static bool enabled;
        private static int budget = 5000;
        private static GUIStyle legendTitle, legendText, objectLabel;
        private static readonly Color Cool = new Color(0.22f, 0.86f, 0.69f);
        private static readonly Color Warm = new Color(1f, 0.73f, 0.36f);
        private static readonly Color Hot = new Color(1f, 0.35f, 0.29f);

        static SceneHeatmap()
        {
            SceneView.duringSceneGui += Draw;
            AssemblyReloadEvents.beforeAssemblyReload += () => Enabled(false);
        }

        public static void Enabled(bool value)
        {
            enabled = value;
            SceneView.RepaintAll();
        }

        public static void SetResult(ScanResult result)
        {
            entries.Clear();
            if (result == null) { SceneView.RepaintAll(); return; }
            budget = Mathf.Max(1, result.settings.maxTrianglesPerMesh);
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(result.settings.includeInactive))
                {
                    if (renderer == null || (renderer.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                    Mesh mesh = null;
                    if (renderer is SkinnedMeshRenderer skin) mesh = skin.sharedMesh;
                    else if (renderer is MeshRenderer)
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter != null) mesh = filter.sharedMesh;
                    }
                    if (mesh == null) continue;
                    long triangles = 0;
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                        if (mesh.GetTopology(submesh) == MeshTopology.Triangles) triangles += (long)mesh.GetIndexCount(submesh) / 3;
                    entries.Add(new Entry { renderer = renderer, triangles = triangles });
                }
            }
            entries.Sort((left, right) => right.triangles.CompareTo(left.triangles));
            SceneView.RepaintAll();
        }

        private static void Draw(SceneView sceneView)
        {
            if (!enabled || EditorApplication.isPlayingOrWillChangePlaymode) return;
            EnsureStyles();
            if (Event.current.type == EventType.Repaint)
            {
                int drawn = 0;
                var previousDepth = Handles.zTest;
                var previousColor = Handles.color;
                try
                {
                    Handles.zTest = CompareFunction.LessEqual;
                    foreach (var entry in entries)
                    {
                        if (entry.renderer == null) continue;
                        if (++drawn > DrawLimit) break;
                        var bounds = entry.renderer.bounds;
                        float ratio = entry.triangles / (float)budget;
                        Color tint = ratio <= 1f ? Color.Lerp(Cool, Warm, ratio) : Color.Lerp(Warm, Hot, Mathf.Clamp01((ratio - 1f) / 3f));
                        Handles.color = tint;
                        Handles.DrawWireCube(bounds.center, bounds.size);
                        if (Selection.Contains(entry.renderer.gameObject))
                        {
                            objectLabel.normal.textColor = tint;
                            Handles.Label(bounds.center + Vector3.up * (bounds.extents.y + 0.08f), "  " + entry.triangles.ToString("N0") + " tris  /  " + ratio.ToString("0.0") + "× 预算", objectLabel);
                        }
                    }
                }
                finally { Handles.zTest = previousDepth; Handles.color = previousColor; }
            }

            Handles.BeginGUI();
            var panel = new Rect(14, 14, 280, 108);
            EditorGUI.DrawRect(panel, new Color(0.045f, 0.09f, 0.12f, 0.94f));
            EditorGUI.DrawRect(new Rect(14, 14, 3, 108), Cool);
            GUI.Label(new Rect(28, 23, 250, 20), "SCENESCOPE  /  网格复杂度", legendTitle);
            GUI.Label(new Rect(28, 47, 245, 18), "实例三角形 / 单网格预算 " + budget.ToString("N0"), legendText);
            EditorGUI.DrawRect(new Rect(28, 73, 10, 10), Cool);
            GUI.Label(new Rect(42, 68, 62, 20), "预算内", legendText);
            EditorGUI.DrawRect(new Rect(108, 73, 10, 10), Warm);
            GUI.Label(new Rect(122, 68, 62, 20), "接近预算", legendText);
            EditorGUI.DrawRect(new Rect(205, 73, 10, 10), Hot);
            GUI.Label(new Rect(219, 68, 62, 20), "高复杂度", legendText);
            GUI.Label(new Rect(28, 92, 252, 18), entries.Count > DrawLimit ? "仅展示复杂度最高的 600 个实例 · 非 GPU 耗时" : "选中对象可查看数量 · 静态估算，非 GPU 耗时", legendText);
            Handles.EndGUI();
        }

        private static void EnsureStyles()
        {
            if (legendTitle != null) return;
            legendTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
            legendTitle.normal.textColor = new Color(0.72f, 0.94f, 0.88f);
            legendText = new GUIStyle(EditorStyles.label) { fontSize = 10 };
            legendText.normal.textColor = new Color(0.61f, 0.73f, 0.79f);
            objectLabel = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
        }
    }
}
