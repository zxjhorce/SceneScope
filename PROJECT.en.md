# SceneScope

**English** · [简体中文](PROJECT.zh-CN.md)

SceneScope is a Unity scene diagnostics and runtime performance investigation tool. It combines static checks, object navigation, undoable changes, frame capture, and offline reports for development workflows and demonstrations of Unity editor engineering.

Package version: **1.1.0**. Project editor: **Unity 2022.3.62f3**. The editor UI and diagnostic messages are currently primarily in Chinese; this repository includes Chinese and English documentation.

## Features

| Module | Capabilities |
| --- | --- |
| Static diagnostics | 16 rules covering script references, meshes, materials, textures, shadows, hierarchy, and physics; incremental scanning, filtering, object focus, Scene heatmaps, and baseline comparison |
| Scoped fixes | Preview and disable shadow casting on small objects, with Undo and Prefab instance property recording |
| Runtime observations | Frame intervals; CPU main/render thread and wait times; GPU time when available; GC allocations and collection counts; memory; actual Draw Calls, Batches, SetPass, triangles, and vertices |
| Hitch investigation | Bounded frame history, hitch observations, P50/P95/P99, configurable thresholds, and shortcuts to Profiler and Frame Debugger |
| Reports and automation | Static JSON/HTML; runtime JSON/CSV/HTML and imported device reports; batch checking of enabled Build Settings scenes |

## Open the project

```sh
git clone https://github.com/zxjhorce/SceneScope.git
```

1. This repository is private. Your GitHub account must have access to clone it.
2. Add the cloned `SceneScope` directory in Unity Hub and open it with Unity 2022.3.62f3. Unity regenerates Library during the first import.
3. Open `Assets/SceneScopeDemo/Generated 2/SceneScope_Demo.unity`, the complete demonstration scene retained in the repository.
4. Choose **Tools → SceneScope → 打开诊断面板** (Open diagnostics), then **扫描已加载场景** (Scan loaded scenes).
5. For runtime capture, choose **Tools → SceneScope → 运行时性能排查** (Runtime performance investigation), enter Play Mode, click **开始采集** (Start capture), reproduce the issue, and click **停止并分析** (Stop and analyze).

On Windows, you can also run `./Open-SceneScope.ps1 -UnityPath 'full path to Unity.exe'`. Its automatic editor discovery is a local convenience, not a Unity installer.

## Use in an existing game

Copy the entire `Packages/com.scenescope.diagnostics` directory into the target project's `Packages` directory as an embedded UPM package. The declared minimum Unity version is 2022.3; validate other versions and platforms in your own environment. No third-party plugins are required.

For a device or standalone development build, explicitly add a `SceneScope.Runtime.SceneScopeCapture` component and build with **Development Build** enabled. Stop and export a capture after reproduction, then import its JSON in the editor. GPU timing also requires suitable Frame Timing Stats settings and platform support. Starting the collector in a normal Release build does not start sampling.

See the [runtime guide](Documentation/Runtime-Guide.en.md) for setup and a complete export example.

## Documentation

| Topic | English | 中文 |
| --- | --- | --- |
| Installation, static rules, and CI | [User guide](Documentation/User-Guide.en.md) | [使用指南](Documentation/使用指南.md) |
| CPU/GPU/GC/Draw Call investigation | [Runtime guide](Documentation/Runtime-Guide.en.md) | [运行时排查指南](Documentation/运行时排查指南.md) |
| Design and engineering decisions | [Architecture](Documentation/Architecture.en.md) | [技术设计](Documentation/技术设计.md) |

A [Chinese demonstration walkthrough](Documentation/演示讲解.md) is also included.

## Validation and limits

On October 23, 2026, static integration checks passed **34/34** in Unity 2022.3.62f3. The demo went from 14 findings to 6 after eight shadow changes; a single Undo restored the changes. This is a rule-check result, not a measured FPS improvement.

The runtime module passed **30 additional checks**: 16 analysis/report/window-construction/build checks, 13 Windows Direct3D11 Development Player checks, and one Release inactivity check. An offscreen fixture captured roughly 56 ms hitches, approximately 4 MiB of allocation in one frame, two collections, and a peak of 65 actual Draw Calls. That run produced zero valid GPU timing samples and verified N/A handling only. It does not establish successful GPU timing measurement or mobile-device performance validation.

- A static score summarizes rules; it cannot be converted to FPS. Renderer counts are not Draw Calls.
- Main-thread time includes waits. GPU timings arrive asynchronously and are not guaranteed to match the CPU observation on the same row.
- GC allocation bytes and collection counts are not GC pause durations.
- Missing metrics remain N/A. Findings guide investigation; function call stacks and individual draw inspection still require Profiler, Frame Debugger, or platform tools.
- The Editor, Development builds, and the tool itself add overhead. Validate real-game conclusions on target hardware under repeatable conditions.

Validation source is included. Logs, reports, and executables in `artifacts/` are local outputs and are not committed. The user and runtime guides describe validation entry points and conditions.

## Repository contents

```text
Assets/SceneScopeDemo/Generated 2/  Ready-to-open demo scene and resources
Assets/SceneScopeValidation/       Runtime validation source
Packages/com.scenescope.diagnostics/  Reusable package source and .meta files
Packages/manifest.json             Project dependencies
Packages/packages-lock.json        Dependency lockfile
ProjectSettings/                  Unity project settings
Documentation/                    Chinese and English documentation
Open-SceneScope.ps1                Windows launch helper
```

Library, Temp, Logs, UserSettings, builds, duplicate generated demos, and local tool settings are ignored. The repository introductions are `PROJECT.en.md` and `PROJECT.zh-CN.md`; local README files are not committed.
