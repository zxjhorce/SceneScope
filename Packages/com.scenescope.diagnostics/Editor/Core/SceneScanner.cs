using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace SceneScope.Editor
{
    /// <summary>Rules execute on the editor main thread and must never mutate scene data.</summary>
    public interface ISceneRule
    {
        void Analyze(ScanContext context, GameObject gameObject);
    }

    public sealed class ScanContext
    {
        public ScanResult Result { get; }
        public ScanSettings Settings => Result.settings;
        internal readonly HashSet<Material> Materials = new HashSet<Material>();
        internal readonly HashSet<Texture> Textures = new HashSet<Texture>();
        internal int ActiveAudioListeners;

        internal ScanContext(ScanSettings settings, string[] scenePaths)
        {
            Result = new ScanResult
            {
                settings = settings.Copy(), scenePaths = scenePaths,
                scannedAtUtc = DateTime.UtcNow.ToString("o")
            };
        }

        public void Add(string ruleId, string title, IssueSeverity severity, IssueCategory category,
            Object target, string description, string recommendation, FixKind fix = FixKind.None)
        {
            var go = target as GameObject;
            if (target is Component component) go = component.gameObject;
            Result.issues.Add(new DiagnosticIssue
            {
                ruleId = ruleId, title = title, severity = severity, category = category,
                target = target, objectPath = go != null ? HierarchyPath(go.transform) : target != null ? target.name : "已加载场景",
                assetPath = target != null ? AssetDatabase.GetAssetPath(target) : "",
                globalObjectId = target != null ? GlobalObjectId.GetGlobalObjectIdSlow(target).ToString() : "",
                description = description, recommendation = recommendation, fix = fix
            });
        }

        public static string HierarchyPath(Transform transform)
        {
            var names = new List<string>();
            for (var current = transform; current != null; current = current.parent) names.Add(current.name);
            names.Reverse();
            return transform.gameObject.scene.name + "/" + string.Join("/", names);
        }

        public static long TriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long result = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                if (mesh.GetTopology(i) == MeshTopology.Triangles) result += (long)mesh.GetIndexCount(i) / 3;
            return result;
        }

        public static Mesh RendererMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        internal void InspectTexture(Texture texture)
        {
            if (texture == null) return;
            Result.metrics.textureMemoryBytes += Math.Max(0, Profiler.GetRuntimeMemorySizeLong(texture));
            if (texture.width > Settings.maxTextureSize || texture.height > Settings.maxTextureSize)
                Add("SS008", "纹理尺寸超预算", IssueSeverity.Warning, IssueCategory.Texture, texture,
                    $"{texture.width} × {texture.height}；单边预算 {Settings.maxTextureSize}px。",
                    "在目标平台的导入设置中降低 Max Size，并结合近景画质验证。");
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
            if (importer == null) return;
            if (importer.isReadable)
                Add("SS009", "纹理保留了 CPU 可读副本", IssueSeverity.Warning, IssueCategory.Texture, texture,
                    "Read/Write 已开启，可能增加常驻内存。", "确认没有 GetPixels / SetPixels 等运行时需求后关闭 Read/Write；工具不会自动修改导入器。");
            if (importer.textureType == TextureImporterType.Default && !importer.mipmapEnabled)
                Add("SS010", "3D 纹理未启用 Mip Map", IssueSeverity.Info, IssueCategory.Texture, texture,
                    "场景材质引用的 Default 类型纹理没有 Mip Map。", "若用于远近变化的 3D 表面，考虑启用 Mip Map；UI 和特殊数据纹理需按用途判断。");
        }

        internal void Complete()
        {
            var m = Result.metrics;
            m.materials = Materials.Count;
            m.textures = Textures.Count;
            if (m.realtimeShadowLights > Settings.maxRealtimeShadowLights)
                Add("SS011", "实时阴影灯超预算", IssueSeverity.Warning, IssueCategory.Rendering, null,
                    $"启用且激活的非 Baked 阴影灯 {m.realtimeShadowLights} 盏；预算 {Settings.maxRealtimeShadowLights} 盏。",
                    "减少实时阴影灯或调整烘焙策略；Mixed 灯也纳入保守估计，实际成本以目标设备 Profiler 为准。");
            if (m.renderers > Settings.maxSceneRenderers)
                Add("SS012", "Renderer 数量超预算", IssueSeverity.Warning, IssueCategory.Rendering, null,
                    $"扫描范围内 {m.renderers} 个 Renderer；预算 {Settings.maxSceneRenderers}。",
                    "检查可见性、LOD 和材质复用；在 Frame Debugger 中验证 SRP Batcher、实例化与合批效果。");
            if (m.triangles > Settings.maxSceneTriangles)
                Add("SS013", "引用几何量超预算", IssueSeverity.Warning, IssueCategory.Rendering, null,
                    $"按 Renderer 累计引用 {m.triangles:N0} 个三角形；预算 {Settings.maxSceneTriangles:N0}。包含全部 LOD，不等于单帧实际提交量。",
                    "按相机视角检查 LOD / 遮挡剔除，并在 GPU 捕获中确认真正的几何开销。");
            if (ActiveAudioListeners > 1)
                Add("SS014", "存在多个激活的 AudioListener", IssueSeverity.Error, IssueCategory.Structure, null,
                    $"同时启用的 AudioListener 为 {ActiveAudioListeners} 个。", "按相机切换逻辑保留一个激活的监听器。");
            Result.issues.Sort((a, b) =>
            {
                int severity = b.severity.CompareTo(a.severity);
                if (severity != 0) return severity;
                int rule = string.CompareOrdinal(a.ruleId, b.ruleId);
                return rule != 0 ? rule : string.CompareOrdinal(a.objectPath, b.objectPath);
            });
        }
    }

    public static class SceneScanner
    {
        const double FrameBudgetMilliseconds = 6;
        static ScanSession session;
        static Action<float> progress;
        static Action<ScanResult> completed;
        static readonly List<ISceneRule> extensions = new List<ISceneRule>();
        public static bool IsScanning => session != null;
        public static event Action<Exception> Failed;

        public static void RegisterRule(ISceneRule rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            if (!extensions.Contains(rule)) extensions.Add(rule);
        }

        public static void UnregisterRule(ISceneRule rule) => extensions.Remove(rule);

        public static ScanResult ScanSynchronously(ScanSettings settings)
        {
            var scan = new ScanSession(settings, extensions.ToArray());
            while (!scan.Step()) { }
            return scan.Context.Result;
        }

        public static void Start(ScanSettings settings, Action<float> onProgress, Action<ScanResult> onCompleted)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请退出 Play Mode 后扫描。");
            Cancel();
            session = new ScanSession(settings, extensions.ToArray());
            progress = onProgress;
            completed = onCompleted;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }

        public static void Cancel()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            session = null;
            progress = null;
            completed = null;
        }

        static void PlayModeChanged(PlayModeStateChange state) => Cancel();

        static void Tick()
        {
            try
            {
                var timer = Stopwatch.StartNew();
                while (session != null && timer.Elapsed.TotalMilliseconds < FrameBudgetMilliseconds)
                {
                    if (!session.Step()) continue;
                    var result = session.Context.Result;
                    var callback = completed;
                    Cancel();
                    callback?.Invoke(result);
                    return;
                }
                if (session != null) progress?.Invoke(session.Progress);
            }
            catch (Exception exception)
            {
                Cancel();
                Debug.LogException(exception);
                Failed?.Invoke(exception);
            }
        }

        sealed class ScanSession
        {
            internal readonly ScanContext Context;
            readonly Stack<Transform> pending = new Stack<Transform>();
            readonly List<GameObject> objects = new List<GameObject>();
            readonly ISceneRule[] rules;
            readonly Stopwatch timer = Stopwatch.StartNew();
            Texture[] textures;
            int objectIndex;
            int textureIndex;
            bool discovered;
            internal float Progress => !discovered ? 0.02f : textures == null
                ? 0.1f + 0.75f * objectIndex / Math.Max(1, objects.Count)
                : 0.85f + 0.14f * textureIndex / Math.Max(1, textures.Length);

            internal ScanSession(ScanSettings settings, ISceneRule[] extraRules)
            {
                if (settings == null) throw new ArgumentNullException(nameof(settings));
                var paths = new List<string>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;
                    paths.Add(string.IsNullOrEmpty(scene.path) ? scene.name + " (未保存)" : scene.path);
                    foreach (var root in scene.GetRootGameObjects()) pending.Push(root.transform);
                }
                Context = new ScanContext(settings, paths.ToArray());
                rules = new ISceneRule[] { new StructureRule(), new RenderingRule(), new PhysicsRule() }.Concat(extraRules).ToArray();
            }

            internal bool Step()
            {
                if (!discovered)
                {
                    if (pending.Count > 0)
                    {
                        var transform = pending.Pop();
                        if (transform == null) return false;
                        var go = transform.gameObject;
                        if ((go.hideFlags & HideFlags.DontSaveInEditor) != 0 || (!Context.Settings.includeInactive && !go.activeInHierarchy)) return false;
                        objects.Add(go);
                        for (int i = transform.childCount - 1; i >= 0; i--) pending.Push(transform.GetChild(i));
                        return false;
                    }
                    discovered = true;
                }
                if (objectIndex < objects.Count)
                {
                    var go = objects[objectIndex++];
                    if (go != null && go.scene.IsValid() && go.scene.isLoaded)
                    {
                        Context.Result.metrics.gameObjects++;
                        foreach (var rule in rules) rule.Analyze(Context, go);
                    }
                    return false;
                }
                if (textures == null) textures = Context.Textures.ToArray();
                if (textureIndex < textures.Length)
                {
                    Context.InspectTexture(textures[textureIndex++]);
                    return false;
                }
                Context.Complete();
                Context.Result.elapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
                return true;
            }
        }
    }
}
