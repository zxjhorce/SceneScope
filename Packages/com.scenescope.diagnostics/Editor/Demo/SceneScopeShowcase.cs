using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SceneScope.Editor
{
    public static class SceneScopeShowcase
    {
        public static void Open()
        {
            var scene = SceneManager.GetActiveScene();
            if (SceneManager.sceneCount == 1 && string.IsNullOrEmpty(scene.path) && !scene.isDirty)
            {
                var path = AssetDatabase.FindAssets("SceneScope_Demo t:Scene", new[] { "Assets/SceneScopeDemo" })
                    .Select(AssetDatabase.GUIDToAssetPath).OrderBy(x => x).FirstOrDefault();
                if (!string.IsNullOrEmpty(path)) EditorSceneManager.OpenScene(path);
            }
            SceneScopeWindow.Open();
        }
    }
}
