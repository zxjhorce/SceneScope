using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SceneScope.Editor
{
    internal sealed class StructureRule : ISceneRule
    {
        public void Analyze(ScanContext context, GameObject go)
        {
            int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
            context.Result.metrics.missingScripts += missing;
            if (missing > 0)
                context.Add("SS001", "存在丢失的脚本", IssueSeverity.Error, IssueCategory.Structure, go,
                    $"该对象包含 {missing} 个 Missing Script。", "检查脚本 GUID / 编译错误并恢复引用；确认废弃后再手动移除，避免丢失序列化数据。");
            int depth = 0;
            for (var parent = go.transform.parent; parent != null; parent = parent.parent) depth++;
            if (depth == context.Settings.maxHierarchyDepth + 1)
                context.Add("SS015", "层级深度超预算", IssueSeverity.Info, IssueCategory.Structure, go,
                    $"此处分支开始超过深度预算 {context.Settings.maxHierarchyDepth}（根节点深度为 0）。",
                    "评估层级组织与 Transform 更新频率；优先整理频繁移动的深层分支。");
            foreach (var listener in go.GetComponents<AudioListener>())
                if (listener.enabled && go.activeInHierarchy) context.ActiveAudioListeners++;
        }
    }

    internal sealed class RenderingRule : ISceneRule
    {
        public void Analyze(ScanContext context, GameObject go)
        {
            foreach (var light in go.GetComponents<Light>())
                if (go.activeInHierarchy && light.enabled && light.lightmapBakeType != LightmapBakeType.Baked && light.shadows != LightShadows.None)
                    context.Result.metrics.realtimeShadowLights++;

            foreach (var renderer in go.GetComponents<Renderer>())
            {
                context.Result.metrics.renderers++;
                var mesh = ScanContext.RendererMesh(renderer);
                long triangles = ScanContext.TriangleCount(mesh);
                context.Result.metrics.triangles += triangles;
                if ((renderer is SkinnedMeshRenderer || (renderer is MeshRenderer && go.GetComponent<TextMesh>() == null)) && mesh == null)
                    context.Add("SS002", "Renderer 缺少网格", IssueSeverity.Error, IssueCategory.Rendering, renderer,
                        "MeshRenderer / SkinnedMeshRenderer 没有关联可渲染的 Mesh。", "关联正确网格，或确认该 Renderer 是否已废弃。");
                if (triangles > context.Settings.maxTrianglesPerMesh)
                    context.Add("SS003", "单个网格三角形超预算", IssueSeverity.Warning, IssueCategory.Rendering, renderer,
                        $"引用网格 {mesh.name}：{triangles:N0} 个三角形；预算 {context.Settings.maxTrianglesPerMesh:N0}。",
                        "结合屏幕占比和 LOD 使用情况判断；简化远景模型或配置 LODGroup。");

                var materials = renderer.sharedMaterials;
                if (materials.Length > context.Settings.maxMaterialSlots)
                    context.Add("SS004", "材质槽位过多", IssueSeverity.Warning, IssueCategory.Rendering, renderer,
                        $"{materials.Length} 个材质槽；预算 {context.Settings.maxMaterialSlots}。槽位数不能直接等同于 Draw Call。",
                        "评估材质复用、贴图图集和子网格合并，在 Frame Debugger 中确认实际提交。");
                int empty = 0;
                foreach (var material in materials)
                {
                    if (material == null) { empty++; continue; }
                    // Inspect each shared material once; never access renderer.material (which clones it).
                    if (!context.Materials.Add(material)) continue;
                    foreach (var property in material.GetTexturePropertyNames())
                    {
                        var texture = material.GetTexture(property);
                        if (texture != null) context.Textures.Add(texture);
                    }
                }
                if (empty > 0)
                    context.Add("SS005", "存在空材质槽", IssueSeverity.Error, IssueCategory.Rendering, renderer,
                        $"材质列表中有 {empty} 个空引用。", "按子网格顺序补齐材质，避免粉色 / 默认材质；工具不会擅自重排槽位。");
                if (renderer.shadowCastingMode != ShadowCastingMode.Off)
                {
                    context.Result.metrics.shadowCasters++;
                    var size = renderer.bounds.size;
                    float largest = Mathf.Max(size.x, size.y, size.z);
                    if (mesh != null && largest > 0 && largest < context.Settings.smallObjectSize && renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
                        context.Add("SS006", "小物体开启了阴影投射", IssueSeverity.Info, IssueCategory.Rendering, renderer,
                            $"世界包围盒最大边 {largest:F2} Unity 单位，小于阈值 {context.Settings.smallObjectSize:F2}。",
                            "可关闭此物体的 Cast Shadows 以减少潜在阴影绘制；会改变画面，先预览清单，再检查光照效果。支持 Undo。", FixKind.DisableShadowCasting);
                }
            }
        }
    }

    internal sealed class PhysicsRule : ISceneRule
    {
        public void Analyze(ScanContext context, GameObject go)
        {
            foreach (var collider in go.GetComponents<MeshCollider>())
            {
                var body = collider.attachedRigidbody;
                // Invalid/inactive shapes may not be registered with the physics world in Edit Mode.
                // Inspect the serialized hierarchy as well so the configuration error is still visible.
                if (body == null) body = collider.GetComponentInParent<Rigidbody>(true);
                if (!collider.convex && body != null && !body.isKinematic)
                    context.Add("SS007", "动态刚体使用非凸 MeshCollider", IssueSeverity.Error, IssueCategory.Physics, collider,
                        "非 Kinematic 的 Rigidbody（可能位于父级）关联了非凸 MeshCollider。",
                        "根据玩法改用基础碰撞体 / 复合碰撞体，或评估凸包与 Kinematic；自动修改可能改变物理行为，因此需人工处理。");
                if (collider.sharedMesh == null)
                    context.Add("SS016", "MeshCollider 缺少网格", IssueSeverity.Warning, IssueCategory.Physics, collider,
                        "MeshCollider 没有关联碰撞网格。", "关联适合碰撞检测的低复杂度网格，或移除确认无用的组件。");
            }
        }
    }
}
