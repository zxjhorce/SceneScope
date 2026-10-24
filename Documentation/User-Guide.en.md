# SceneScope User Guide

[中文](使用指南.md) · [Architecture](Architecture.en.md) · [Runtime guide](Runtime-Guide.en.md) · [Project overview](../PROJECT.en.md)

SceneScope provides two workflows: static scene diagnostics and runtime performance investigation. Static diagnostics connect findings, Scene view navigation, reversible changes, and offline reports. Version 1.1 adds runtime capture in Editor Play Mode and Development Builds. A static diagnostic score is not an FPS estimate or a measured optimization percentage.

The editor interface currently uses Chinese labels. This guide preserves those labels so you can find each command; the English text in parentheses explains its meaning.

## Install and open

- This project includes the embedded UPM package at `Packages/com.scenescope.diagnostics`.
- To use it in another project, copy the entire package directory into that project's `Packages` directory. The package declares Unity 2022.3 as its minimum version; this does not mean every later version or platform has been tested.
- After compilation, open **Tools → SceneScope → 打开诊断面板** (Open diagnostics panel) for static checks, or **Tools → SceneScope → 运行时性能排查** (Runtime performance investigation) for runtime capture.
- Scene scanning and both windows belong to the Editor assembly. The separate `SceneScope.Runtime` assembly can be included in a Player and does not depend on `UnityEditor` or third-party packages. Capture requires an explicitly added or created `SceneScopeCapture`; sampling is enabled only in the Editor or a Development Build. The normal Release build entry points do not sample.

From PowerShell at this project's root, run `./Open-SceneScope.ps1` to launch Unity and open the diagnostic window. The script reads `ProjectSettings/ProjectVersion.txt`, checks its configured D-drive installation locations and Unity Hub's editor list, and accepts an explicit path when automatic discovery does not match your machine:

```powershell
$sceneScopeUnity = '<full-path-to-Unity.exe>'
./Open-SceneScope.ps1 -UnityPath $sceneScopeUnity
```

The startup entry point opens an existing demo only when the project currently has one clean, unsaved empty scene. It preserves other scene states.

For runtime setup, device JSON import, and detailed counter limitations, use the [runtime guide](Runtime-Guide.en.md). Runtime observations do not automatically identify a function, GC pause cause, or individual draw call. CPU main-thread time includes waits, asynchronous GPU data may not match the CPU observation in the same row, and GC allocation bytes are not pause duration. Unsupported metrics display `N/A`. Confirm conclusions with the target device's Profiler and Frame Debugger.

The remaining rules, score, fixes, and scene CI instructions on this page describe the **static** workflow.

## Five-minute static demo

1. Click **生成演示场景** (Generate demo scene), or use **Tools → SceneScope → 生成演示场景**. Respond to Unity's prompt about unsaved scene changes.
2. Generated assets go under `Assets/SceneScopeDemo/Generated/`; an existing directory receives a unique suffix. The scene is `SceneScope_Demo.unity`. Four areas demonstrate high triangle counts, small shadow casters, deep hierarchies, and physics configuration issues, with additional missing-mesh and inactive-object samples.
3. Click **扫描已加载场景** (Scan loaded scenes). The scan covers loaded ordinary scenes and can be stopped with **取消扫描** (Cancel scan). Avoid editing scenes during a scan if you need comparable results.
4. Filter by severity, category, or text. Select a finding and use **定位对象** (Select object) or **在 Scene 中聚焦** (Frame in Scene view). Enable **Scene 热力图** (Scene heatmap) to connect findings to their locations.
5. Click **固定为基线** (Pin baseline). Select a small-object shadow finding and click **预览并修复** (Preview and fix). Review the object list and visual tradeoff before confirming. The fix disables the selected Renderer’s Cast Shadows setting and supports Unity Undo.
6. Rescan and compare findings and score, then inspect the changed shadows. Use Ctrl+Z and rescan to verify restoration. Fewer findings do not prove a measured runtime performance improvement.
7. Export **HTML** for an independent offline report or **JSON** for structured data. Open the HTML in a browser or use the browser's print command to save a PDF.

## Understand the static metrics

| Metric | Meaning |
| --- | --- |
| GameObjects / Renderers | Counts within the scan scope. Inactive objects are included by default; Renderer counts can also include disabled components. |
| Mesh triangles | Sum of mesh references for each Renderer, including every LOD. Repeated references to a shared mesh are counted repeatedly. This is not the geometry actually submitted in one frame. |
| Materials / textures | Deduplicated shared materials and their referenced textures. The scanner does not instantiate materials through `renderer.material`. |
| Estimated texture memory | Sum of Unity memory API readings for referenced textures loaded in the Editor. This is not measured VRAM on a target device. |
| Realtime shadow lights | Active, enabled, non-Baked lights with shadows enabled. Mixed lights are conservatively included. |
| Shadow casters | Renderers in scope whose Cast Shadows setting is not Off. |
| Score | `max(0, 100 - errors * 12 - warnings * 3 - info)`: a heuristic summary of rule findings. |

Default budgets are 5,000 triangles per mesh, 2,048 pixels per texture edge, 2 material slots per Renderer, hierarchy depth 8, 2 realtime shadow lights, 300 scene Renderers, and 250,000 referenced scene triangles. A small object has a world-space bounding box whose largest dimension is below 0.6 Unity units. Tune these values for your project, target hardware, and art requirements. A Unity unit is not necessarily one physical meter.

Compare baselines only when rules, thresholds, and scope match. A higher score does not demonstrate that a different scene or a larger project runs faster.

## All 16 built-in rules

| ID | Check | Severity and action |
| --- | --- | --- |
| SS001 | Missing Script | Error. Restore the reference or manually confirm that the component is obsolete. |
| SS002 | Renderer has no mesh | Error. Assign the intended mesh. |
| SS003 | Mesh exceeds its triangle budget | Warning. Evaluate LOD and screen coverage. |
| SS004 | Material slots exceed budget | Warning. Verify actual submissions in Frame Debugger. |
| SS005 | Empty material slots | Error. Restore the intended references. |
| SS006 | Small object casts shadows | Info. Supports preview, disabling Cast Shadows, and Undo. |
| SS007 | Dynamic Rigidbody with a non-convex MeshCollider | Error. Manually choose an appropriate collision setup. |
| SS008 | Texture edge exceeds budget | Warning. Review the target platform's Max Size setting. |
| SS009 | Texture Read/Write enabled | Warning. Confirm CPU access requirements before changing it. |
| SS010 | Default-type texture referenced by a scene material has no mipmaps | Info. Evaluate the texture's intended use. |
| SS011 | Realtime shadow lights exceed budget | Warning. Evaluate baking and shadow settings. |
| SS012 | Renderer count exceeds budget | Warning. Review visibility, LOD, and batching. |
| SS013 | Referenced scene geometry exceeds budget | Warning. Investigate using actual camera views. |
| SS014 | Multiple enabled AudioListeners | Error. Review camera-switching logic. |
| SS015 | Hierarchy depth exceeds budget | Info. Reported at the first over-budget point on each branch. |
| SS016 | MeshCollider has no mesh | Warning. Review the collision reference. |

## Changes and reports

Scanning and exporting do not modify scenes. The shadow fix resolves its targets again and applies only to valid Renderers in loaded ordinary scenes. It skips asset objects, Preview Scenes, objects with casting already disabled, and Shadows Only renderers. The change affects appearance and marks scenes dirty; saving remains your decision.

Static HTML includes scan time, scene paths, thresholds, metrics, evidence, and recommendations. It uses no remote assets or scripts and HTML-encodes all dynamic scene and resource text. JSON uses Unity `JsonUtility`: enums are integers, in-memory object references are excluded, and `Score` is a calculated property rather than a JSON field. Recalculate it from severity counts when needed. GlobalObjectId and asset paths preserve location evidence; resolving them still requires the original project objects to exist.

## CI and batch mode

`SceneScope.Editor.SceneScopeCI.ScanBuildScenes` scans production scenes with the default rules and thresholds and must run in Unity `-batchmode`. Close other Unity instances using the same project. Run this example from the project root after replacing the editor placeholder:

```powershell
$sceneScopeUnity = '<full-path-to-Unity.exe>'
$sceneScopeProject = (Get-Location).Path
$sceneScopeArtifacts = Join-Path $sceneScopeProject 'artifacts'
New-Item -ItemType Directory -Force -Path $sceneScopeArtifacts | Out-Null

& $sceneScopeUnity `
  -batchmode -nographics `
  -projectPath $sceneScopeProject `
  -executeMethod SceneScope.Editor.SceneScopeCI.ScanBuildScenes `
  -sceneScopeReport (Join-Path $sceneScopeArtifacts 'ci') `
  -sceneScopeFailOnError `
  -logFile (Join-Path $sceneScopeArtifacts 'ci.log')
```

`-sceneScopeReport` specifies the output directory; the default is `artifacts/ci` under the process working directory. With `-sceneScopeFailOnError`, error-severity findings produce exit code **2**. Successful execution otherwise returns **0**; an execution failure returns **1**. The entry point exits Unity itself, so `-quit` is unnecessary. It does not save changes to scenes.

Each enabled Build Settings scene is opened separately and receives `000-scene-name.json` and `000-scene-name.html`, with the index increasing from 000. `summary.json` records UTC time, scene count, error/warning totals, and HTML report paths. Any missing enabled scene path fails the operation instead of silently accepting a partial scan. Separate scans suit mutually exclusive levels; scenes used together additively also need a scan of the loaded combination.

## Recorded validation and limits

On **2026-10-23, Unity 2022.3.62f3**, the local static integration run passed **34/34 checks**. The demo initially produced 14 findings; disabling shadows on 8 small objects reduced this to 6, and a single Undo restored the original result. This demonstrates diagnostic and Undo behavior, not a measured FPS improvement.

The static validation entry point is `SceneScope.Editor.SceneScopeValidation.RunBatch`. It covers demo scanning, inactive-object filtering, threshold changes, fix/Undo behavior, and report export. Outputs include `artifacts/validation.json`, `artifacts/demo-report.json`, and `artifacts/demo-report.html`.

`SceneScope.Editor.SceneScopeUIValidation.RunBatch` separately passed programmatic checks for a real EditorWindow, USS loading, asynchronous scan completion, ListView behavior, rejection of baselines with different scopes, and stale-result protection. This entry point requires a graphics device; omit `-nographics`. These checks are not native-window screenshot acceptance tests.

The CI run scanned the saved demo and generated per-scene reports plus `summary.json`. Its two intentional errors produced the expected exit code 2 with `-sceneScopeFailOnError`. The static HTML report's layout was inspected in the built-in browser; native editor-window visual interaction has not completed screenshot verification.

Local evidence includes `artifacts/validation.json`, `artifacts/ui-validation.txt`, `artifacts/unity-validation.log`, `artifacts/unity-ui-validation.log`, and `artifacts/ci/summary.json`. These generated artifacts are not published with the reusable package or repository; the paths identify reproducible local outputs, not downloadable evidence.

The separate 1.1 runtime validation recorded **16/16** analysis, import/export, window, and Player build checks, **13/13** Development Player capture/lifecycle checks on Windows with Direct3D11, and **1/1** Release inactivity check, totaling **30/30**. These runs used a controlled synthetic workload. **Valid GPU timing samples were 0**: only unavailable-data handling was verified, and successful GPU timing measurement remains unverified. See the [runtime guide](Runtime-Guide.en.md) for the recorded scope and limitations. Neither these runs nor the static checks establish compatibility or performance on every target device.
