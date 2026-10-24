# SceneScope Architecture

[中文](技术设计.md) · [User guide](User-Guide.en.md) · [Runtime guide](Runtime-Guide.en.md) · [Project overview](../PROJECT.en.md)

## Purpose and boundaries

SceneScope demonstrates Unity editor engineering and performance investigation. Static diagnostics access scene objects on the main thread, produce explainable findings, locate objects in the Scene view, and make limited changes through Undo. The 1.1 runtime module records observations in Editor Play Mode or Development Builds. Each workflow exports its own reviewable report.

Static scanning covers loaded ordinary scenes and textures/materials referenced by their Renderers. It does not measure actual draw calls, GPU time, or FPS. Runtime capture separately records frame intervals, CPU/GPU timing, allocations and collection counts, memory, and rendering counters. It does not provide function call stacks, GC pause attribution, per-draw-call analysis, or automatic CPU/GPU bottleneck diagnosis. Thresholds are configurable project constraints, not universal Unity limits.

The current UI is Chinese. Its entry points are **Tools → SceneScope → 打开诊断面板** for static diagnostics and **Tools → SceneScope → 运行时性能排查** for runtime investigation. Installation and operational steps are in the [user guide](User-Guide.en.md) and [runtime guide](Runtime-Guide.en.md).

## Package and responsibilities

The embedded UPM package lives at `Packages/com.scenescope.diagnostics`. Its Editor assembly contains the scanner and both windows and compiles only for the Editor. The separate `SceneScope.Runtime` assembly and `SceneScopeCapture` component can be included in a Player without referencing `UnityEditor`. Actual capture is guarded by `UNITY_EDITOR || DEVELOPMENT_BUILD`; normal Release build entry points do not sample. No component is automatically added to every scene. The package requires no remote service or third-party dependency.

Paths in this table are relative to the package root:

| Component | Responsibility |
| --- | --- |
| `Editor/Core/DiagnosticModels.cs` | Settings snapshots, scene metrics, evidence, severity, categories, and fix types. |
| `Editor/Core/SceneScanner.cs` | Traversal, incremental scheduling, resource deduplication, texture checks, aggregate rules, and extension registration. |
| `Editor/Core/BuiltInRules.cs` | Object-level structure, rendering, and physics rules. |
| `Editor/Core/SceneFixer.cs` | Change previews, target resolution, Undo grouping, Prefab overrides, and scene dirty state. |
| Editor window and Scene visualization | Interaction, filters, baseline comparison, selection/framing, and heatmap. |
| `Editor/Reporting/ReportExporter.cs` | Static JSON and offline HTML reports. |
| `Editor/Runtime/PerformanceWindow.cs` | Capture controls, history charts, slow-frame observations, analysis, and device JSON import. |
| `Runtime/CaptureModels.cs` | Capture configuration, metric capabilities, observations, and bounded ring buffer. |
| `Runtime/SceneScopeCapture.cs` | ProfilerRecorder and asynchronous GPU timing collection, explicit lifecycle, and native-handle disposal. |
| `Runtime/PerformanceAnalyzer.cs` | Retained-window statistics, percentiles, and explainable investigation hints. |
| `Runtime/RuntimeReportExporter.cs` | Player-compatible JSON/CSV/HTML export and bounded JSON import validation. |
| Demo and batch validation | Reproducible samples and behavioral checks. |

## Runtime collection and evidence

Explicitly starting capture creates ProfilerRecorder instances and checks counter names and units. Unsupported, unavailable, or not-yet-observed metrics remain `-1`. Per-frame observations enter a preallocated struct ring buffer: 3,600 samples by default, at most 120,000. Once full, it overwrites older observations. Snapshot creation, sorted statistics, analysis, and export run on request or stop and introduce additional work. Preallocation does not establish zero total allocation or overhead for the tool; Unity APIs, the Profiler, and the editor UI also need measurement. Stop, disable, destruction, and error paths release recorder handles.

Frame intervals come from `Time.unscaledDeltaTime`; CPU and rendering observations come from ProfilerRecorder. GPU timings arrive asynchronously through FrameTimingManager and are deduplicated by `frameStartTimestamp`. Main-thread time includes synchronization and frame-limiting waits, not just CPU computation. A GPU value is not forcibly aligned with the CPU frame in the same row. Allocation bytes and `GC.CollectionCount(0)` deltas are not GC pause durations. The Editor window and collector can affect the measurements themselves.

Reports preserve device information, graphics API, VSync, target frame rate, counter sources, and availability. Statistics cover only the samples currently retained in the ring buffer; `droppedSamples` records overwritten observations. Analysis offers limited statements such as exceeding a budget, observing allocations, or having unavailable counters. It directs the user to Profiler, Frame Debugger, or platform GPU tools for attribution rather than claiming causation from one metric.

Runtime JSON has a separate 2.x schema. Import limits files to 128 MiB and 120,000 samples and validates required structure, finite values, allowed negative sentinels, and sample ordering. CSV preserves raw `-1` values and GPU timestamps. HTML encodes dynamic text, uses no external resources, and displays trends in at most 1,200 peak-preserving intervals, leaving intervals containing missing observations blank. Static and runtime reports use different models; they cannot be imported interchangeably or treated as the same kind of evidence.

## Incremental static scanning

Unity object access stays on the Editor main thread. Interactive scans run from `EditorApplication.update` with a **6 ms soft budget** per update. Steps discover Transforms, inspect one GameObject or texture at a time, then perform aggregate checks. A synchronous entry point reuses the same rules for batch mode and validation.

The budget is not a hard latency guarantee: one rule, Unity API call, root discovery, or final sort may exceed it. Large scenes still require responsiveness measurements. This design uses cancellable main-thread work rather than moving Unity object access to an unsafe background thread. Cancellation, assembly reload, and entering Play Mode detach update callbacks; exceptions stop the scan and notify the window.

At scan start, settings are copied and UTC time and scene paths are recorded. The result is a snapshot, and scene edits during scanning can invalidate comparisons. Inactive objects follow the selected scope; Preview Scenes are excluded. Rescan stable scene states for meaningful before/after comparisons.

## Rules and static statistics

`ISceneRule.Analyze(ScanContext context, GameObject gameObject)` is the extension interface. Register with `SceneScanner.RegisterRule` and remove with `UnregisterRule`. Extensions use `context.Add` to record an identifier, severity, description, recommendation, and location evidence. Rules must not modify the scene during scanning. The complete SS001–SS016 catalog is in the [user guide](User-Guide.en.md#all-16-built-in-rules).

Rendering rules read `sharedMaterials` and `sharedMesh` without creating material instances. Triangle counts use submesh topology and index counts, so enabling mesh Read/Write is unnecessary. HashSets deduplicate materials and textures; geometry is counted per Renderer reference and includes every LOD, so it is not actual per-frame rendering work.

`Profiler.GetRuntimeMemorySizeLong` supplies an estimate for Editor-loaded texture objects, not target-device VRAM. Unity documents this as native memory used by an object and returns 0 when the Profiler is unavailable; zero therefore does not prove zero memory cost. See [Unity 2022.3 memory API documentation](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Profiling.Profiler.GetRuntimeMemorySizeLong.html). Physics checks account for a Rigidbody on a parent; hierarchy checks report the first over-budget point on a branch to reduce repetition.

The static score is `max(0, 100 - 12 * errors - 3 * warnings - info)`. It summarizes rule findings and does not predict runtime performance. Comparable results require the same rules, thresholds, and scan scope. Scores from scenes of different sizes or content do not directly establish relative performance.

## Reversible changes and Scene visualization

The only automatic static fix disables shadow casting on small Renderers. A preview identifies targets and property changes and explains the visual tradeoff. Application resolves targets again, deduplicates them, and skips asset objects, invalid or unloaded scenes, Preview Scenes, renderers already set to Off, and Shadows Only renderers.

Changes use `Undo.RecordObject`, collapse into one undo group, record Prefab instance property modifications, and mark affected scenes dirty without saving them. Unity explicitly requires recording Prefab instance changes when using this approach; see [Unity 2022.3 Undo.RecordObject](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Undo.RecordObject.html). Texture importers, physics setup, and script references receive recommendations rather than automatic changes because they can affect asset import or behavior.

Scene visualization uses `SceneView.duringSceneGui` to relate object bounds to diagnostic severity. The event supports custom Scene GUI and Handles; see [Unity 2022.3 SceneView.duringSceneGui](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SceneView-duringSceneGui.html).

## Static report design

JSON serializes model fields through `JsonUtility`, excludes in-memory Unity object references, and retains GlobalObjectId and paths as evidence. Enums are integer values. The score is calculated from issue severities rather than serialized as a field.

HTML is a standalone UTF-8 document containing metrics, severity counts, every finding grouped by category, recommendations, object and asset paths, scan time, and thresholds. Dynamic text is HTML-encoded. No external fonts, images, or scripts are loaded, and a Content Security Policy disallows network resources and scripts. Responsive and print styles support browser review and PDF archiving.

## Demo, CI, and validation

The demo generates its own Assets directory, adding a unique suffix when a directory already exists. Its four areas provide visible objects for navigation and deterministic rule samples. `SceneScope.Editor.SceneScopeValidation.RunBatch` validates these controlled fixtures.

`SceneScope.Editor.SceneScopeCI.ScanBuildScenes` validates all enabled Build Scene paths, opens each scene separately, and writes numbered JSON/HTML reports. This avoids combining listeners or budgets from mutually exclusive levels. `summary.json` aggregates errors, warnings, and report paths, while `-sceneScopeFailOnError` turns error-severity findings into exit code 2. Additive scene combinations still require a separate combined scan. The [user guide](User-Guide.en.md#ci-and-batch-mode) provides the command. `Open-SceneScope.ps1` is the interactive launcher: it finds an editor matching the project version and calls `SceneScopeShowcase.Open`.

Static integration validation on **2026-10-23 with Unity 2022.3.62f3 passed 34/34 checks**. It covered intended findings, inactive filtering, configurable thresholds, shadow fixes and Undo, and report serialization/encoding. Separate programmatic window and CI checks also ran; their scope is recorded in the [user guide](User-Guide.en.md#recorded-validation-and-limits).

Runtime validation separately passed **16/16** analysis, report, window, and Player build checks, **13/13** Development Player capture/lifecycle checks on Windows/Direct3D11 using a synthetic workload, and **1/1** Release inactivity check, totaling **30/30**. **No valid GPU timing samples were obtained**. The run verified missing-data behavior, not successful GPU measurement. See the [runtime guide](Runtime-Guide.en.md) for detailed evidence boundaries.

Generated evidence such as `artifacts/validation.json` and Unity logs is local and excluded from the published repository. These checks do not substitute for real-device CPU/GPU profiling, complex-project compatibility tests, native-window screenshot verification, or large-scene stress tests. Untested devices and configurations must not be described as validated.
