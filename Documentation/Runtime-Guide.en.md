# SceneScope Runtime Performance Guide

[Project overview](../PROJECT.en.md) · [中文指南](运行时排查指南.md)

SceneScope's static diagnostics inspect scene resources and configuration. The runtime module adds frame intervals, CPU main and render thread times, GPU timing, managed allocations, collection counts, memory, and actual Draw Call / Batches / SetPass / triangle / vertex counters. Capture a reproducible hitch, then use Unity Profiler, Frame Debugger, or platform tools to investigate its cause.

The package targets Unity 2022.3. Available counters depend on the platform, graphics API, rendering configuration, and build type. Missing measurements remain unknown rather than becoming zero. **The editor interface and generated diagnostic text are currently in Chinese**; the instructions below include the actual UI labels.

## Install in a project

Copy `Packages/com.scenescope.diagnostics` into the target project's `Packages` directory as an embedded UPM package, or reference that directory as a local UPM package. Wait for Unity to compile the scripts.

The `SceneScope.Runtime` assembly has no UnityEditor dependency. Collection runs only under `UNITY_EDITOR` or `DEVELOPMENT_BUILD`; calling the capture entry point in a normal Release Player does not start sampling. SceneScope does not automatically add capture objects to every scene or enable collection in your game.

## Reproduce a hitch in the Editor

1. Open the game scene and choose **Tools → SceneScope → 运行时性能排查** (Runtime Performance Investigation).
2. Set the target FPS, hitch multiplier, retained sample count, and GC / Draw Call / SetPass warning thresholds. Defaults are 60 FPS and a 1.5× hitch multiplier: a hitch is a frame interval **strictly greater than 25 ms**. These are configurable project budgets, not universal pass/fail standards.
3. Enter Play Mode manually, then click **开始采集** (Start Capture). By default, the first 30 warmup observations are discarded.
4. Reproduce the operation: enter combat, spawn enemies, open a large UI, load resources, or change scenes. Keep quality settings, resolution, camera, and the sequence of actions consistent.
5. Click the timeline or a hitch observation to inspect CPU, GPU, GC, and rendering values. Disable **跟随最新** (Follow Latest) and use the history slider to inspect older retained samples.
6. Click **停止并分析** (Stop and Analyze) for average FPS, P50/P95/P99 frame times, hitch sample counts, and investigation suggestions. You can also analyze a snapshot during capture. Snapshotting, analysis, and export allocate memory and perform extra work, so nearby observations may include the tool's overhead.
7. Export **JSON** for reopening the capture, **CSV** for raw data analysis, or **HTML** for an offline report.

Scene view, Inspector, other editor windows, and SceneScope's own UI can affect measurements. Editor captures help reproduce and narrow down issues. Quantitative performance conclusions require repeatable tests on the target device.

## Capture in a Development Player

Add **SceneScope → Runtime Performance Capture** to a GameObject in your test startup scene. Its component type is `SceneScope.Runtime.SceneScopeCapture`.

| Component setting | Behavior |
| --- | --- |
| `Auto Start` | Starts collection when the component starts running. Add it only to the scenes or debug entry points you intend to test. |
| `Configuration` | Sets target FPS, warmup frames, retained samples, and warning thresholds. |
| `Export On Disable` | Stops and exports JSON / CSV / HTML when a component that is **still recording** is disabled or destroyed. |
| `Export Directory` | Defaults to `Application.persistentDataPath/SceneScope` when empty. An explicit directory must be writable on the target platform. |

Run a **Development Build** on the target device. For GPU data, check **Player Settings → Other Settings → Rendering → Frame Timing Stats**, and confirm that the platform and graphics API support timing. Disabled timing, unsupported configurations, or a lack of valid observations produce N/A. SceneScope does not change your graphics settings automatically.

End the test at a controlled point: disable the recording component through your debug controls, or stop and export explicitly in code. Do not rely on a mobile operating system killing the process to invoke `OnDisable`. A normal scene component is destroyed with its scene; your project must choose the appropriate lifetime for capture across scene changes.

### Explicit stop and export

```csharp
using System.IO;
using SceneScope.Runtime;
using UnityEngine;

public static class SavePerformanceCapture
{
    public static void Finish(SceneScopeCapture capture)
    {
        var report = capture.StopCapture();
        if (report == null) return;

        var directory = Path.Combine(Application.persistentDataPath, "SceneScope");
        RuntimeReportExporter.ExportJson(report, Path.Combine(directory, "last-capture.json"));
        RuntimeReportExporter.ExportCsv(report, Path.Combine(directory, "last-capture.csv"));
        RuntimeReportExporter.ExportHtml(report, Path.Combine(directory, "last-capture.html"));
    }
}
```

These fixed filenames replace previous reports with the same names. Add a run identifier or timestamp to retain multiple runs. Once `StopCapture()` has been called explicitly, `Export On Disable` will not automatically export that same session; export it yourself as shown above. Export is synchronous on the calling thread, so schedule it after the measured portion of the test.

Copy the device JSON to your development machine using your project's existing authorized file retrieval process. Click **导入真机 JSON** (Import Device JSON) to inspect the history, statistics, device metadata, and supported metrics. The device does not need to remain connected. SceneScope does not install builds, establish remote connections, retrieve device files automatically, or change device security settings.

## Interpret the measurements

| Observation | What it can tell you | Next investigation |
| --- | --- | --- |
| Frame interval, P95/P99, longest frame | When hitches occurred and whether tail latency improved | Repeat the same operation and inspect the relevant Profiler time range. |
| CPU Main Thread / Render Thread | Whether thread wall time is high | Use CPU Usage Timeline / Hierarchy to distinguish scripts, physics, loading, driver work, and waiting. |
| Wait marker | Possible frame limiting, VSync, presentation, or thread synchronization waits | Check the actual marker, VSync, and `targetFrameRate`; a long wait alone does not prove a GPU bottleneck. |
| GPU time | Whether received GPU observations exceed the frame budget | Use the target device's GPU Profiler or platform graphics tools to inspect shadows, transparency, post-processing, and shaders. |
| GC Allocated In Frame | Observations with large managed allocations | Inspect GC.Alloc call stacks for strings, temporary collections, boxing, and frequent instantiation. |
| GC collection count | Whether the generation-zero collection counter increased | Inspect GC.Collect / incremental GC markers in CPU Timeline to establish duration and overlap with hitches. |
| Draw Calls / Batches / SetPass | Actual engine rendering and state-change counts relative to project budgets | Use Frame Debugger to inspect draw order, shader passes, materials, and batching reasons. |
| GC Used Memory / Total Used Memory | Memory usage reported by the runtime | Use Memory Profiler to investigate ownership and lifetime. These are not dedicated GPU VRAM measurements. |

Main Thread includes waits; it is neither pure CPU computation time nor CPU utilization. Main Thread, Render Thread, and wait measurements can overlap. Do not add them together, or subtract one from another, to infer exact work duration.

GPU timing arrives asynchronously. The collector uses `gpuTimestamp` to retain only fresh, valid observations. A GPU value on a row means “received at this observation,” not necessarily “rendered for this CPU frame.” High values on the same row do not establish causation.

GC allocations are bytes; collection counts are counts. Neither measures GC pause milliseconds. SceneScope does not automatically attribute a hitch to a particular function, object, or draw call, and it does not change game code or rendering settings in response to a peak. The window provides shortcuts to Unity Profiler and Frame Debugger for deeper investigation.

## Report scope and formats

- **Retained window:** A preallocated ring buffer retains 3,600 samples by default, up to 120,000. New samples overwrite the oldest when full; `droppedSamples` records the number overwritten. All statistics cover only the currently retained window, not the entire session. Session start and snapshot timestamps do not imply continuous data coverage between them.
- **JSON:** Includes `schemaVersion`, device / Unity / graphics API metadata, settings, metric sources and availability, and raw samples. The current major schema version is 2. Import rejects unsupported major versions, missing required structures, files over 128 MiB, more than 120,000 samples, non-finite values, and invalid negative values. Frame IDs must increase strictly, and elapsed time must not go backwards.
- **CSV:** Uses fixed English headers, invariant-culture decimal points, and CRLF line endings. It includes `gpuTimestamp` and preserves unknown values as `-1`. Keep the JSON alongside it because CSV does not include complete device and threshold metadata.
- **HTML:** Opens offline with no external scripts or resources. It includes frame-time percentiles, FPS, GC and Draw Call summaries, investigation suggestions, the 20 slowest hitch samples, metric sources, and limitations. Diagnostic text currently remains in Chinese.
- **Chart reduction:** HTML charts use at most 1,200 bins, retaining each metric's peak per bin. Bins containing missing data are left blank rather than connecting across unknown values; this can omit some valid observations within the same bin. Different metrics' peaks may come from different frames. Use JSON / CSV for the complete retained data.
- **Missing data:** N/A means unknown or unavailable, represented as `-1` in raw samples. A metric marked available does not guarantee a valid observation on every frame.
- **Frame identity:** Observation IDs derive from the capture instance's `Time.frameCount`; they are not the Profiler window's frame indices. Match reproduction steps and time ranges when comparing processes or separate runs.

## Validate an optimization

Use the same device, graphics API, resolution, quality settings, frame limit, and operation sequence. Warm up and capture a baseline. Follow the evidence into Profiler or Frame Debugger, change a confirmed cause, and repeat the same test with a separate report. Compare frame-time distributions, P95/P99, longest frames, allocations, and rendering counts while checking visual and functional regressions.

Development Builds and collection itself add overhead. Verify the final player experience in a Release build as well. A lower counter does not automatically mean smoother gameplay, and staying below a threshold does not prove the absence of other performance problems.

## Recorded validation and limits

On **2026-10-23**, local checks ran with **Unity 2022.3.62f3 / Windows / Direct3D11**. Analysis, import/export, runtime window construction, and Development / Release Player build checks passed **16/16**. Development Player capture and lifecycle checks passed **13/13**. An actual Release Player run passed **1/1**, confirming that the capture entry point started neither recorders nor sample history. Runtime-related checks therefore passed **30/30 in total (16 + 13 + 1)**.

The test fixture injected CPU work, managed allocation, and collection, and rendered offscreen into a RenderTexture to verify rendering counters. It recorded 267 samples, a 55.9282 ms peak Main Thread observation, 2 hitch samples, a longest frame of approximately 55.946 ms, a 4,194,336-byte allocation observation, 2 collections, and a nonzero Draw Call count of 65. These values demonstrate that the collector observed deliberately constructed loads. **They are not performance acceptance results for a real game or a mobile device.**

There were **zero valid GPU timing samples** in this run. Missing GPU data correctly remained unknown and displayed N/A; this validates missing-data handling, not successful GPU measurement. GPU timing still needs a separate test on a supported target configuration.

The following are locally generated evidence files, excluded from the published repository:

- `artifacts/runtime-validation-project/artifacts/runtime/build-validation.json`
- `artifacts/runtime-results/verified-development/validation.json`
- `artifacts/runtime-results/verified-release/validation.json`
- `artifacts/runtime-results/verified-development/capture.html`
- `artifacts/runtime-results/verified-development/capture.json`
- `artifacts/runtime-results/verified-development/capture.csv`
