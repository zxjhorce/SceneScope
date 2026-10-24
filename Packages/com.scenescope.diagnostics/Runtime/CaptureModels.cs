using System;

namespace SceneScope.Runtime
{
    [Serializable]
    public sealed class CaptureSettings
    {
        public int targetFps = 60;
        public float hitchMultiplier = 1.5f;
        public int maxSamples = 3600;
        public int warmupFrames = 30;
        public long gcAllocationWarningBytes = 1048576;
        public int drawCallWarning = 1000;
        public int setPassWarning = 100;
        public CaptureSettings Copy()
        {
            var value = (CaptureSettings)MemberwiseClone();
            value.targetFps = Math.Max(1, Math.Min(1000, targetFps));
            value.hitchMultiplier = float.IsNaN(hitchMultiplier) || float.IsInfinity(hitchMultiplier) ? 1.5f : Math.Max(1f, hitchMultiplier);
            value.maxSamples = Math.Max(60, Math.Min(120000, maxSamples));
            value.warmupFrames = Math.Max(0, Math.Min(600, warmupFrames));
            value.gcAllocationWarningBytes = Math.Max(0, gcAllocationWarningBytes);
            value.drawCallWarning = Math.Max(0, drawCallWarning);
            value.setPassWarning = Math.Max(0, setPassWarning);
            return value;
        }
    }

    /// <summary>One observation. Recorder counters describe the last completed frame;
    /// GPU timings arrive asynchronously and are not asserted to match frame exactly. -1 means unavailable.</summary>
    [Serializable]
    public struct FrameSample
    {
        public int frame;
        public double timeSeconds;
        public double frameMs;
        public double cpuMainMs;
        public double cpuRenderMs;
        public double mainWaitMs;
        public double gpuMs;
        public ulong gpuTimestamp;
        public long gcAllocatedBytes;
        public long gcUsedBytes;
        public long totalUsedBytes;
        public int gcCollections;
        public long drawCalls;
        public long batches;
        public long setPassCalls;
        public long triangles;
        public long vertices;

        public static FrameSample Empty => new FrameSample
        {
            frameMs = -1, cpuMainMs = -1, cpuRenderMs = -1, mainWaitMs = -1, gpuMs = -1,
            gcAllocatedBytes = -1, gcUsedBytes = -1, totalUsedBytes = -1, gcCollections = -1,
            drawCalls = -1, batches = -1, setPassCalls = -1, triangles = -1, vertices = -1
        };
    }

    [Serializable]
    public sealed class MetricSupport
    {
        public string name;
        public string source;
        public string unit;
        public bool available;
        public string note;
    }

    [Serializable]
    public sealed class CaptureReport
    {
        public string schemaVersion = "2.0";
        public string startedAtUtc;
        public string endedAtUtc;
        public string unityVersion;
        public string platform;
        public string scene;
        public string cpu;
        public string gpu;
        public string graphicsApi;
        public bool isEditor;
        public bool isDevelopmentBuild;
        public int vSyncCount;
        public int applicationTargetFrameRate;
        public int droppedSamples;
        public CaptureSettings settings = new CaptureSettings();
        public MetricSupport[] metrics = Array.Empty<MetricSupport>();
        public FrameSample[] samples = Array.Empty<FrameSample>();
        public string limitations = "计数器为上个完成帧的观测；GPU 时间异步返回，通过 gpuTimestamp 去重，不保证与该观测帧对齐。Main Thread 包括等待，不等于纯 CPU 工作。GC 分配与回收次数不等于 GC 暂停耗时。Editor 观测可能包含编辑器开销；未知值为 -1。";
    }

    /// <summary>Preallocated bounded history. The hot path stores structs without collection growth.</summary>
    public sealed class FrameHistory
    {
        readonly FrameSample[] samples;
        int next;
        public int Count { get; private set; }
        public int DroppedCount { get; private set; }
        public int Capacity => samples.Length;
        public FrameHistory(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            samples = new FrameSample[capacity];
        }
        public void Add(FrameSample sample)
        {
            samples[next] = sample;
            next = (next + 1) % Capacity;
            if (Count < Capacity) Count++; else DroppedCount++;
        }
        public FrameSample GetSample(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            int start = Count == Capacity ? next : 0;
            return samples[(start + index) % Capacity];
        }
        public FrameSample[] ToArray()
        {
            var result = new FrameSample[Count];
            for (int i = 0; i < Count; i++) result[i] = GetSample(i);
            return result;
        }
    }

    [Serializable]
    public sealed class CaptureSummary
    {
        public int frames;
        public double averageFps;
        public double p50Ms;
        public double p95Ms;
        public double p99Ms;
        public double maxFrameMs;
        public int hitchFrames;
        public long totalGcAllocatedBytes;
        public int gcCollections;
        public double gpuP95Ms = -1;
        public long peakDrawCalls = -1;
    }

    [Serializable]
    public sealed class PerformanceFinding
    {
        public string category;
        public string title;
        public string evidence;
        public string nextStep;
        public int observationFrame;
    }
}
