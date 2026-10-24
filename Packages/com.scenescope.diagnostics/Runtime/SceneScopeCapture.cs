using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneScope.Runtime
{
    /// <summary>Opt-in capture for Play Mode and Development Players. No collector is created automatically.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("SceneScope/Runtime Performance Capture")]
    public sealed class SceneScopeCapture : MonoBehaviour
    {
        public bool autoStart = true;
        public CaptureSettings configuration = new CaptureSettings();
        public bool exportOnDisable;
        [Tooltip("Empty uses Application.persistentDataPath/SceneScope.")]
        public string exportDirectory = "";

        public static SceneScopeCapture Active { get; private set; }
        public static CaptureReport LastReport { get; private set; }
        public bool IsRecording { get; private set; }
        public int Count => history == null ? 0 : history.Count;
        public FrameSample Latest => Count == 0 ? FrameSample.Empty : history.GetSample(Count - 1);
        public CaptureSettings Settings { get; private set; }

        FrameHistory history;
        CaptureReport metadata;
        CaptureReport stoppedReport;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Native slots and managed history are allocated once at capture start.
        readonly RecorderSlot[] slots = new RecorderSlot[11];
        readonly FrameTiming[] timings = new FrameTiming[1];
        const int Main = 0, Render = 1, Wait = 2, Alloc = 3, GcUsed = 4, TotalUsed = 5;
        const int Draws = 6, Batches = 7, SetPass = 8, Triangles = 9, Vertices = 10;
        int warmupRemaining;
        int lastUpdateFrame;
        int lastCollectionCount;
        double startTime;
        ulong lastGpuTimestamp;
        bool frameTimingEnabled;
        bool observedGpu;
        MetricSupport gpuSupport;
        static int playGeneration;
        int generation;

        sealed class RecorderSlot : IDisposable
        {
            public ProfilerRecorder recorder;
            public MetricSupport support;
            public bool observed;
            public bool opened;

            public long Read()
            {
                if (!recorder.Valid || !recorder.IsRunning || recorder.Count == 0) return -1;
                long value = recorder.LastValue;
                if (value < 0) return -1;
                observed = true;
                return value;
            }

            public void Dispose()
            {
                if (recorder.Valid) recorder.Dispose();
                recorder = default;
            }
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Active != null)
            {
                Active.DisposeRecorders();
                Active.IsRecording = false;
            }
            playGeneration++;
#endif
            Active = null;
            LastReport = null;
        }

        void Start()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            generation = playGeneration;
            if (autoStart && !IsRecording) StartCapture(configuration);
#endif
        }

        public void StartCapture(CaptureSettings settings)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!Application.isPlaying || !isActiveAndEnabled)
                throw new InvalidOperationException("SceneScope capture requires an enabled component in Play Mode or a Development Player.");
            if (IsRecording) return;
            if (Active != null && Active != this && Active.IsRecording)
                throw new InvalidOperationException("A SceneScope capture is already running. Stop it before starting another.");

            DisposeRecorders();
            Settings = (settings ?? new CaptureSettings()).Copy();
            history = new FrameHistory(Settings.maxSamples);
            metadata = null;
            stoppedReport = null;
            observedGpu = false;
            lastGpuTimestamp = 0;
            // The next Update observes the partially recorded start frame. Always discard it,
            // including when the user requests zero additional warmup frames.
            warmupRemaining = Settings.warmupFrames + 1;
            lastUpdateFrame = Time.frameCount;
            lastCollectionCount = GC.CollectionCount(0);
            startTime = Time.realtimeSinceStartupAsDouble;
            generation = playGeneration;
            try
            {
                var handles = new List<ProfilerRecorderHandle>(256);
                ProfilerRecorderHandle.GetAvailable(handles);
                slots[Main] = Open(handles, "Main Thread", ProfilerMarkerDataUnit.TimeNanoseconds, true,
                    "主线程墙钟时间，包含等待及 Editor 开销；不是 CPU 利用率或纯脚本耗时。");
                slots[Render] = Open(handles, "Render Thread", ProfilerMarkerDataUnit.TimeNanoseconds, false,
                    "渲染线程墙钟时间可能包含等待；单线程渲染时可能不可用。");
                slots[Wait] = Open(handles, "Gfx.WaitForPresentOnGfxThread", ProfilerMarkerDataUnit.TimeNanoseconds, true,
                    "仅当前主线程上的等待标记；可能包括呈现、VSync 或渲染线程等待，不能直接判断 GPU 瓶颈。");
                if (!slots[Wait].recorder.Valid)
                    slots[Wait] = Open(handles, "WaitForTargetFPS", ProfilerMarkerDataUnit.TimeNanoseconds, true,
                        "主线程限帧/VSync 等待标记；只取一个等待标记，避免嵌套样本重复相加。");
                slots[Alloc] = Open(handles, "GC Allocated In Frame", ProfilerMarkerDataUnit.Bytes);
                slots[GcUsed] = Open(handles, "GC Used Memory", ProfilerMarkerDataUnit.Bytes);
                slots[TotalUsed] = Open(handles, "Total Used Memory", ProfilerMarkerDataUnit.Bytes);
                slots[Draws] = Open(handles, "Draw Calls Count", ProfilerMarkerDataUnit.Count);
                slots[Batches] = Open(handles, "Batches Count", ProfilerMarkerDataUnit.Count);
                slots[SetPass] = Open(handles, "SetPass Calls Count", ProfilerMarkerDataUnit.Count);
                slots[Triangles] = Open(handles, "Triangles Count", ProfilerMarkerDataUnit.Count);
                slots[Vertices] = Open(handles, "Vertices Count", ProfilerMarkerDataUnit.Count);
                frameTimingEnabled = FrameTimingManager.IsFeatureEnabled();
                gpuSupport = new MetricSupport
                {
                    name = "GPU Frame Time", source = "FrameTimingManager.gpuFrameTime", unit = "ms",
                    note = frameTimingEnabled
                        ? "等待异步 GPU 样本；仅保留新时间戳且大于 0 的数据，不与当前帧强行对齐。"
                        : "Frame Timing Stats 未启用或当前平台/API 不支持；GPU 记为 -1。"
                };
                if (frameTimingEnabled)
                {
                    // A prior capture may have left timings in Unity's global buffer.
                    if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                        lastGpuTimestamp = timings[0].frameStartTimestamp;
                    FrameTimingManager.CaptureFrameTimings();
                }
                metadata = CreateMetadata();
                Active = this;
                IsRecording = true;
            }
            catch
            {
                DisposeRecorders();
                IsRecording = false;
                if (Active == this) Active = null;
                throw;
            }
#else
            // Release builds are deliberately inert, including explicit StartCapture calls.
            IsRecording = false;
#endif
        }

        public FrameSample GetSample(int chronological)
        {
            if (history == null) throw new ArgumentOutOfRangeException(nameof(chronological));
            return history.GetSample(chronological);
        }

        /// <summary>Allocates an immutable-by-convention copy. Do not call every frame.</summary>
        public CaptureReport Snapshot()
        {
            if (metadata == null) return null;
            var result = JsonUtility.FromJson<CaptureReport>(JsonUtility.ToJson(metadata));
            result.endedAtUtc = IsRecording ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) : metadata.endedAtUtc;
            result.settings = Settings.Copy();
            result.samples = history.ToArray();
            result.droppedSamples = history.DroppedCount;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                result.metrics[i].available = slots[i].observed;
                if (!slots[i].observed && slots[i].opened)
                    result.metrics[i].note += " 尚未收到有效样本；数值为 -1。";
            }
            result.metrics[slots.Length].available = observedGpu;
            if (observedGpu)
                result.metrics[slots.Length].note = "已收到 GPU 时间；异步返回，每个 frameStartTimestamp 仅采纳一次。";
#endif
            return result;
        }

        public CaptureReport StopCapture()
        {
            if (!IsRecording) return stoppedReport;
            IsRecording = false;
            metadata.endedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            try
            {
                stoppedReport = Snapshot();
                LastReport = stoppedReport;
                return stoppedReport;
            }
            finally
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                DisposeRecorders();
#endif
                if (Active == this) Active = null;
            }
        }

        void Update()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Supports Enter Play Mode with both domain and scene reload disabled.
            if (generation != playGeneration)
            {
                generation = playGeneration;
                if (autoStart && !IsRecording) StartCapture(configuration);
            }
            if (!IsRecording || Time.frameCount == lastUpdateFrame) return;
            lastUpdateFrame = Time.frameCount;
            try
            {
                FrameSample sample = FrameSample.Empty;
                sample.frame = Time.frameCount - 1;
                sample.timeSeconds = Time.realtimeSinceStartupAsDouble - startTime;
                sample.frameMs = Time.unscaledDeltaTime * 1000.0;
                sample.cpuMainMs = ReadMilliseconds(slots[Main]);
                sample.cpuRenderMs = ReadMilliseconds(slots[Render]);
                sample.mainWaitMs = ReadMilliseconds(slots[Wait]);
                sample.gcAllocatedBytes = slots[Alloc].Read();
                sample.gcUsedBytes = slots[GcUsed].Read();
                sample.totalUsedBytes = slots[TotalUsed].Read();
                sample.drawCalls = slots[Draws].Read();
                sample.batches = slots[Batches].Read();
                sample.setPassCalls = slots[SetPass].Read();
                sample.triangles = slots[Triangles].Read();
                sample.vertices = slots[Vertices].Read();
                int collections = GC.CollectionCount(0);
                sample.gcCollections = collections >= lastCollectionCount ? collections - lastCollectionCount : -1;
                lastCollectionCount = collections;
                ReadGpu(ref sample);
                if (warmupRemaining > 0)
                {
                    warmupRemaining--;
                    // Availability must describe retained observations, not discarded warmup samples.
                    for (int i = 0; i < slots.Length; i++) slots[i].observed = false;
                    observedGpu = false;
                    return;
                }
                history.Add(sample);
            }
            catch (Exception exception)
            {
                // Never leave native recorder handles running after a capture failure.
                try { StopCapture(); }
                finally { Debug.LogException(exception, this); }
            }
#endif
        }

        void OnDisable()
        {
            bool wasRecording = IsRecording;
            try
            {
                var report = StopCapture();
                if (wasRecording && exportOnDisable && report != null)
                {
                    string directory = string.IsNullOrWhiteSpace(exportDirectory)
                        ? Path.Combine(Application.persistentDataPath, "SceneScope") : exportDirectory;
                    Directory.CreateDirectory(directory);
                    string prefix = Path.Combine(directory, "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
                    RuntimeReportExporter.ExportJson(report, prefix + ".json");
                    RuntimeReportExporter.ExportCsv(report, prefix + ".csv");
                    RuntimeReportExporter.ExportHtml(report, prefix + ".html");
                    Debug.Log("SceneScope capture exported: " + prefix, this);
                }
            }
            catch (Exception exception) { Debug.LogException(exception, this); }
            finally
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                DisposeRecorders();
#endif
                if (Active == this) Active = null;
            }
        }

        void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            DisposeRecorders();
#endif
            if (Active == this) Active = null;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static double ReadMilliseconds(RecorderSlot slot)
        {
            long value = slot.Read();
            return value < 0 ? -1 : value * 0.000001;
        }

        void ReadGpu(ref FrameSample sample)
        {
            if (!frameTimingEnabled) return;
            uint returned = FrameTimingManager.GetLatestTimings(1, timings);
            if (returned > 0)
            {
                FrameTiming timing = timings[0];
                if (timing.frameStartTimestamp > lastGpuTimestamp)
                {
                    lastGpuTimestamp = timing.frameStartTimestamp;
                    if (timing.gpuFrameTime > 0 && !double.IsNaN(timing.gpuFrameTime) && !double.IsInfinity(timing.gpuFrameTime))
                    {
                        sample.gpuMs = timing.gpuFrameTime;
                        sample.gpuTimestamp = timing.frameStartTimestamp;
                        observedGpu = true;
                    }
                }
            }
            FrameTimingManager.CaptureFrameTimings();
        }

        static RecorderSlot Open(List<ProfilerRecorderHandle> handles, string name, ProfilerMarkerDataUnit unit,
            bool currentThreadOnly = false, string note = "上一完成帧的 Unity Profiler 计数器；Editor 中可能包含其他编辑器视图的工作。")
        {
            var slot = new RecorderSlot
            {
                support = new MetricSupport { name = name, source = "ProfilerRecorder / " + name, unit = unit.ToString(), note = note }
            };
            for (int i = 0; i < handles.Count; i++)
            {
                var description = ProfilerRecorderHandle.GetDescription(handles[i]);
                if (!string.Equals(description.Name, name, StringComparison.Ordinal)) continue;
                if (description.UnitType != unit)
                {
                    slot.support.note = "当前运行时计数器单位与预期不符，拒绝解释；数值为 -1。";
                    return slot;
                }
                var options = ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame |
                              ProfilerRecorderOptions.WrapAroundWhenCapacityReached;
                if (currentThreadOnly) options |= ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
                try { slot.recorder = new ProfilerRecorder(handles[i], 1, options); }
                catch (Exception exception) { slot.support.note = "计数器无法启动：" + exception.Message; }
                slot.opened = slot.recorder.Valid;
                if (!slot.recorder.Valid) slot.support.note += " 当前运行时不可用；数值为 -1。";
                return slot;
            }
            slot.support.note = "当前平台、图形 API 或构建没有公开此计数器；数值为 -1。";
            return slot;
        }

        CaptureReport CreateMetadata()
        {
            var metrics = new MetricSupport[slots.Length + 3];
            for (int i = 0; i < slots.Length; i++) metrics[i] = slots[i].support;
            metrics[slots.Length] = gpuSupport;
            metrics[slots.Length + 1] = new MetricSupport
            {
                name = "Frame Time", source = "Time.unscaledDeltaTime", unit = "ms", available = true,
                note = "引擎报告的未缩放帧间隔；不能据此区分 CPU、GPU 或系统调度原因。"
            };
            metrics[slots.Length + 2] = new MetricSupport
            {
                name = "GC Collections", source = "GC.CollectionCount(0) delta", unit = "count", available = true,
                note = "托管运行时报告的第 0 代回收次数增量；不是 GC 暂停毫秒，也不表示增量 GC 每片耗时。"
            };
            return new CaptureReport
            {
                startedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                scene = SceneManager.GetActiveScene().path,
                cpu = SystemInfo.processorType,
                gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                isEditor = Application.isEditor,
                isDevelopmentBuild = Debug.isDebugBuild,
                vSyncCount = QualitySettings.vSyncCount,
                applicationTargetFrameRate = Application.targetFrameRate,
                settings = Settings.Copy(),
                metrics = metrics
            };
        }

        void DisposeRecorders()
        {
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != null) slots[i].Dispose();
        }
#endif
    }
}
