using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SceneScope.Editor
{
    public enum IssueSeverity { Info, Warning, Error }
    public enum IssueCategory { Rendering, Texture, Physics, Structure }
    public enum FixKind { None, DisableShadowCasting }

    [Serializable]
    public sealed class ScanSettings
    {
        public bool includeInactive = true;
        public int maxTrianglesPerMesh = 5000;
        public int maxTextureSize = 2048;
        public int maxMaterialSlots = 2;
        public int maxHierarchyDepth = 8;
        public int maxRealtimeShadowLights = 2;
        public int maxSceneRenderers = 300;
        public int maxSceneTriangles = 250000;
        public float smallObjectSize = 0.6f;
        public ScanSettings Copy() => (ScanSettings)MemberwiseClone();
    }

    [Serializable]
    public sealed class DiagnosticIssue
    {
        public string ruleId;
        public string title;
        public string description;
        public string recommendation;
        public IssueSeverity severity;
        public IssueCategory category;
        public string objectPath;
        public string assetPath;
        public string globalObjectId;
        public FixKind fix;
        [NonSerialized] public UnityEngine.Object target;
        public UnityEngine.Object ResolveTarget()
        {
            if (target != null) return target;
            if (!string.IsNullOrEmpty(globalObjectId) && GlobalObjectId.TryParse(globalObjectId, out var id))
                target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
            if (target == null && !string.IsNullOrEmpty(assetPath))
                target = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return target;
        }
    }

    [Serializable]
    public sealed class SceneMetrics
    {
        public int gameObjects;
        public int renderers;
        public long triangles;
        public int materials;
        public int textures;
        public long textureMemoryBytes;
        public int realtimeShadowLights;
        public int shadowCasters;
        public int missingScripts;
    }

    [Serializable]
    public sealed class ScanResult
    {
        public string toolVersion = "1.1.0";
        public string scannedAtUtc;
        public string[] scenePaths;
        public ScanSettings settings;
        public SceneMetrics metrics = new SceneMetrics();
        public List<DiagnosticIssue> issues = new List<DiagnosticIssue>();
        public double elapsedMilliseconds;
        public int Count(IssueSeverity severity) => issues.FindAll(x => x.severity == severity).Count;
        public int Score => Math.Max(0, 100 - Count(IssueSeverity.Error) * 12 - Count(IssueSeverity.Warning) * 3 - Count(IssueSeverity.Info));
    }
}
