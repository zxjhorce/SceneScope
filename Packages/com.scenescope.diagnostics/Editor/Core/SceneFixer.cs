using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace SceneScope.Editor
{
    public static class SceneFixer
    {
        static List<Renderer> Resolve(IEnumerable<DiagnosticIssue> issues)
        {
            var result = new List<Renderer>();
            if (issues == null) return result;
            foreach (var issue in issues)
            {
                if (issue == null || issue.fix != FixKind.DisableShadowCasting) continue;
                var renderer = issue.ResolveTarget() as Renderer;
                if (renderer == null || EditorUtility.IsPersistent(renderer) || !renderer.gameObject.scene.IsValid()
                    || !renderer.gameObject.scene.isLoaded || EditorSceneManager.IsPreviewScene(renderer.gameObject.scene)
                    || renderer.shadowCastingMode == ShadowCastingMode.Off || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly
                    || result.Contains(renderer)) continue;
                result.Add(renderer);
            }
            return result;
        }

        public static string Preview(IEnumerable<DiagnosticIssue> issues)
        {
            var renderers = Resolve(issues);
            var text = new StringBuilder($"将修改 {renderers.Count} 个场景 Renderer：\n\n");
            foreach (var renderer in renderers)
                text.AppendLine($"{ScanContext.HierarchyPath(renderer.transform)}\n  Cast Shadows: {renderer.shadowCastingMode} → Off");
            text.Append("\n会改变阴影画面。修改仅写入当前场景，不自动保存；可通过 Ctrl+Z 撤销。");
            return text.ToString();
        }

        public static int Apply(IEnumerable<DiagnosticIssue> issues)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return 0;
            var renderers = Resolve(issues);
            if (renderers.Count == 0) return 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("SceneScope: 关闭小物体阴影投射");
            foreach (var renderer in renderers)
            {
                Undo.RecordObject(renderer, "关闭阴影投射");
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
            }
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
            SceneView.RepaintAll();
            return renderers.Count;
        }
    }
}
