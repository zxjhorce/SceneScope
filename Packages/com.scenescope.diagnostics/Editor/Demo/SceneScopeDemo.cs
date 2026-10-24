using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace SceneScope.Editor
{
    /// <summary>Creates a self-contained, intentionally imperfect scene for demonstrating the analyzer.</summary>
    public static class SceneScopeDemo
    {
        private static readonly Vector3 CameraPosition = new Vector3(13f, 13f, -18f);

        [MenuItem("Tools/SceneScope/生成演示场景", priority = 20)]
        public static void CreateDemoScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string path = CreateScene();
            Debug.Log("SceneScope 演示场景已保存到 " + path + "。通过 Tools > SceneScope > 打开诊断面板 开始分析。");
        }

        internal static string CreateScene()
        {
            if (!AssetDatabase.IsValidFolder("Assets/SceneScopeDemo"))
                AssetDatabase.CreateFolder("Assets", "SceneScopeDemo");
            string folder = AssetDatabase.GenerateUniqueAssetPath("Assets/SceneScopeDemo/Generated");
            AssetDatabase.CreateFolder("Assets/SceneScopeDemo", System.IO.Path.GetFileName(folder));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("SceneScope // Performance Observatory");

            var graphite = Material(folder, "Graphite", new Color(0.045f, 0.067f, 0.1f), 0.65f, 0.5f);
            var platform = Material(folder, "Platform", new Color(0.12f, 0.17f, 0.23f), 0.6f, 0.5f);
            var white = Material(folder, "Porcelain", new Color(0.78f, 0.88f, 0.94f), 0.35f, 0.55f);
            var cyan = Material(folder, "Cyan", new Color(0.12f, 0.77f, 0.9f), 0.55f, 0.7f, new Color(0.05f, 0.8f, 1f));
            var amber = Material(folder, "Amber", new Color(0.98f, 0.48f, 0.12f), 0.4f, 0.55f, new Color(1f, 0.28f, 0.015f));
            var violet = Material(folder, "Violet", new Color(0.48f, 0.3f, 0.87f), 0.5f, 0.55f, new Color(0.45f, 0.13f, 0.85f));
            var mint = Material(folder, "Mint", new Color(0.15f, 0.8f, 0.63f), 0.4f, 0.6f, new Color(0.05f, 0.65f, 0.35f));

            Primitive("Observatory deck", PrimitiveType.Cube, root.transform, new Vector3(0, -0.2f, 0), new Vector3(17, 0.4f, 15), graphite);
            for (int x = -7; x <= 7; x++)
                Primitive("Deck seam X " + x, PrimitiveType.Cube, root.transform, new Vector3(x, 0.008f, 0), new Vector3(0.012f, 0.01f, 14.3f), platform);
            for (int z = -7; z <= 7; z++)
                Primitive("Deck seam Z " + z, PrimitiveType.Cube, root.transform, new Vector3(0, 0.008f, z), new Vector3(16.3f, 0.01f, 0.012f), platform);
            Primitive("Front luminous rail", PrimitiveType.Cube, root.transform, new Vector3(0, 0.05f, -7.15f), new Vector3(16.2f, 0.05f, 0.05f), cyan);
            Primitive("Back luminous rail", PrimitiveType.Cube, root.transform, new Vector3(0, 0.05f, 7.15f), new Vector3(16.2f, 0.05f, 0.05f), cyan);

            var polyBay = Bay(root.transform, "01 // Geometry", new Vector3(-4, 0, -3), cyan, platform);
            var shadowBay = Bay(root.transform, "02 // Shadow budget", new Vector3(4, 0, -3), amber, platform);
            var hierarchyBay = Bay(root.transform, "03 // Hierarchy", new Vector3(-4, 0, 3.1f), violet, platform);
            var physicsBay = Bay(root.transform, "04 // Physics", new Vector3(4, 0, 3.1f), mint, platform);

            var denseMesh = SphereMesh(64, 96, 1.08f);
            AssetDatabase.CreateAsset(denseMesh, folder + "/DenseSphere.asset");
            var sculpture = MeshObject("Dense sculpture // 12,096 triangles", polyBay, denseMesh, cyan, new Vector3(0, 1.85f, 0));
            sculpture.transform.localScale = new Vector3(1f, 1.35f, 1f);
            sculpture.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.On;
            Ring(polyBay, 1.65f, 0.7f, cyan);
            Label(polyBay, "01  GEOMETRY", new Vector3(-1.95f, 0.55f, -2.1f), 0.27f, Color.white);
            Label(polyBay, "12K triangles / one mesh", new Vector3(-1.95f, 0.25f, -2.1f), 0.15f, new Color(0.45f, 0.85f, 1f));

            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI * 2 / 8;
                var chip = Primitive("Micro caster " + (i + 1).ToString("00"), PrimitiveType.Cube, shadowBay,
                    new Vector3(Mathf.Cos(angle) * 1.2f, 0.85f + (i % 3) * 0.12f, Mathf.Sin(angle) * 1.2f),
                    new Vector3(0.22f, 0.22f, 0.22f), amber);
                chip.transform.localRotation = Quaternion.Euler(15, i * 18, 12);
                chip.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.On;
            }
            Primitive("Shadow budget core", PrimitiveType.Cylinder, shadowBay, new Vector3(0, 0.9f, 0), new Vector3(1.15f, 0.35f, 1.15f), white);
            Ring(shadowBay, 1.65f, 0.7f, amber);
            Label(shadowBay, "02  SHADOWS", new Vector3(-1.95f, 0.55f, -2.1f), 0.27f, Color.white);
            Label(shadowBay, "8 tiny casters / 3 shadow lights", new Vector3(-1.95f, 0.25f, -2.1f), 0.15f, new Color(1f, 0.65f, 0.35f));

            Transform parent = hierarchyBay;
            for (int i = 0; i < 11; i++)
            {
                var node = new GameObject("Nested transform " + (i + 1).ToString("00"));
                node.transform.SetParent(parent, false);
                parent = node.transform;
            }
            Primitive("Deep hierarchy payload", PrimitiveType.Cube, parent, new Vector3(0, 1.45f, 0), new Vector3(1.15f, 1.15f, 1.15f), violet);
            for (int i = 0; i < 6; i++)
            {
                float height = 0.5f + i * 0.35f;
                var step = Primitive("Hierarchy layer " + i, PrimitiveType.Cube, hierarchyBay,
                    new Vector3(0, height, 0), new Vector3(2.05f - i * 0.2f, 0.055f, 2.05f - i * 0.2f), white);
                step.transform.localRotation = Quaternion.Euler(0, i * 13, 0);
            }
            Label(hierarchyBay, "03  STRUCTURE", new Vector3(-1.95f, 0.55f, -2.1f), 0.27f, Color.white);
            Label(hierarchyBay, "11 nested transforms", new Vector3(-1.95f, 0.25f, -2.1f), 0.15f, new Color(0.73f, 0.62f, 1f));

            var body = Primitive("Dynamic body // non-convex mesh collider", PrimitiveType.Cube, physicsBay,
                new Vector3(0, 1.65f, 0), new Vector3(1.5f, 1.5f, 1.5f), mint);
            body.transform.localRotation = Quaternion.Euler(10, 28, 12);
            var collider = body.AddComponent<MeshCollider>();
            collider.sharedMesh = body.GetComponent<MeshFilter>().sharedMesh;
            collider.convex = false;
            var rigidbody = body.AddComponent<Rigidbody>();
            rigidbody.useGravity = false;
            rigidbody.isKinematic = false;
            Ring(physicsBay, 1.65f, 0.7f, mint);
            Label(physicsBay, "04  PHYSICS", new Vector3(-1.95f, 0.55f, -2.1f), 0.27f, Color.white);
            Label(physicsBay, "Dynamic / non-convex collider", new Vector3(-1.95f, 0.25f, -2.1f), 0.15f, new Color(0.4f, 1f, 0.76f));

            var missingMesh = new GameObject("Missing mesh // intentionally empty MeshFilter");
            missingMesh.transform.SetParent(root.transform, false);
            missingMesh.AddComponent<MeshFilter>();
            missingMesh.AddComponent<MeshRenderer>().sharedMaterial = white;
            var inactive = MeshObject("INACTIVE // spare detail mesh", root.transform, denseMesh, cyan, new Vector3(0, -5, 0));
            inactive.SetActive(false);

            Label(root.transform, "SCENESCOPE", new Vector3(-3.6f, 0.5f, 6.55f), 0.53f, Color.white);
            Label(root.transform, "PERFORMANCE OBSERVATORY", new Vector3(-3.6f, 0.17f, 6.55f), 0.19f, new Color(0.4f, 0.8f, 0.95f));
            Label(root.transform, "SCAN  /  INSPECT  /  FIX  /  MEASURE", new Vector3(-3.5f, 0.14f, -6.5f), 0.22f, new Color(0.5f, 0.7f, 0.82f));

            var cameraObject = new GameObject("Main Camera");
            cameraObject.transform.SetParent(root.transform, false);
            cameraObject.transform.position = CameraPosition;
            cameraObject.transform.LookAt(new Vector3(0, 0.5f, 0));
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 43;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.022f, 0.034f, 0.057f);
            camera.allowHDR = true;
            CreateLight(root.transform, "Realtime key // shadow light 1", LightType.Directional,
                new Vector3(0, 8, 0), Quaternion.Euler(48, -28, 0), new Color(0.83f, 0.9f, 1f), 1.3f);
            CreateLight(root.transform, "Realtime cyan // shadow light 2", LightType.Spot,
                new Vector3(-5, 6, -4), Quaternion.Euler(65, 18, 0), new Color(0.18f, 0.7f, 1f), 3.5f);
            CreateLight(root.transform, "Realtime amber // shadow light 3", LightType.Spot,
                new Vector3(5, 7, 2), Quaternion.Euler(72, -20, 0), new Color(1f, 0.55f, 0.24f), 3.5f);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.23f, 0.29f, 0.38f);
            RenderSettings.fog = false;

            AssetDatabase.SaveAssets();
            string scenePath = folder + "/SceneScope_Demo.unity";
            if (!EditorSceneManager.SaveScene(scene, scenePath))
                throw new InvalidOperationException("Could not save SceneScope demo to " + scenePath);
            if (EditorBuildSettings.scenes.Length == 0)
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
            Selection.activeGameObject = root;
            if (!Application.isBatchMode && SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.LookAt(new Vector3(0, 0.5f, 0), cameraObject.transform.rotation, 20f);
            return scenePath;
        }

        private static Transform Bay(Transform parent, string name, Vector3 position, Material accent, Material surface)
        {
            var bay = new GameObject(name);
            bay.transform.SetParent(parent, false);
            bay.transform.localPosition = position;
            Primitive("Raised plinth", PrimitiveType.Cylinder, bay.transform, new Vector3(0, 0.23f, 0), new Vector3(4.1f, 0.23f, 4.1f), surface);
            Primitive("Status light", PrimitiveType.Cube, bay.transform, new Vector3(-1.75f, 0.5f, -1.3f), new Vector3(0.8f, 0.025f, 0.07f), accent);
            return bay.transform;
        }

        private static GameObject Primitive(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            var collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        private static GameObject MeshObject(string name, Transform parent, Mesh mesh, Material material, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        private static void Ring(Transform parent, float radius, float y, Material material)
        {
            for (int i = 0; i < 24; i++)
            {
                float angle = i * Mathf.PI / 12;
                var piece = Primitive("Indicator arc " + i, PrimitiveType.Cube, parent,
                    new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius), new Vector3(0.33f, 0.035f, 0.045f), material);
                piece.transform.localRotation = Quaternion.Euler(0, -angle * Mathf.Rad2Deg + 90f, 0);
            }
        }

        private static void Label(Transform parent, string text, Vector3 position, float size, Color color)
        {
            var go = new GameObject(text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.rotation = Quaternion.LookRotation(go.transform.position - CameraPosition);
            var label = go.AddComponent<TextMesh>();
            label.text = text;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 64;
            label.characterSize = size;
            label.anchor = TextAnchor.MiddleLeft;
            label.color = color;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = label.font.material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private static Material Material(string folder, string name, Color color, float metallic, float gloss, Color? emission = null)
        {
            var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("SceneScope demo requires a Standard or URP Lit shader.");
            var material = new Material(shader) { name = name, color = color };
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", gloss);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", gloss);
            if (emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission.Value * 0.8f);
            }
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static void CreateLight(Transform parent, string name, LightType type, Vector3 position, Quaternion rotation, Color color, float intensity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            var light = go.AddComponent<Light>();
            light.type = type;
            light.color = color;
            light.intensity = intensity;
            light.range = 22;
            light.spotAngle = 80;
            light.shadows = LightShadows.Soft;
            light.lightmapBakeType = LightmapBakeType.Realtime;
        }

        private static Mesh SphereMesh(int latitude, int longitude, float radius)
        {
            int stride = longitude + 1;
            var vertices = new Vector3[(latitude + 1) * stride];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var indices = new int[(latitude - 1) * longitude * 6];
            int cursor = 0;
            for (int lat = 0; lat <= latitude; lat++)
            for (int lon = 0; lon <= longitude; lon++)
            {
                float phi = Mathf.PI * lat / latitude;
                float theta = Mathf.PI * 2 * lon / longitude;
                int i = lat * stride + lon;
                normals[i] = new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
                vertices[i] = normals[i] * radius;
                uv[i] = new Vector2((float)lon / longitude, 1f - (float)lat / latitude);
                if (lat == latitude || lon == longitude) continue;
                if (lat > 0)
                {
                    indices[cursor++] = i;
                    indices[cursor++] = i + 1;
                    indices[cursor++] = i + stride;
                }
                if (lat < latitude - 1)
                {
                    indices[cursor++] = i + 1;
                    indices[cursor++] = i + stride + 1;
                    indices[cursor++] = i + stride;
                }
            }
            var mesh = new Mesh { name = "DenseSphere_12096Triangles", vertices = vertices, normals = normals, uv = uv, triangles = indices };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
