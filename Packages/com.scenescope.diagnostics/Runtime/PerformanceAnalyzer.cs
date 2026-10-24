using System;
using System.Collections.Generic;
using System.Globalization;

namespace SceneScope.Runtime
{
    /// <summary>Observation-based triage, never a causal bottleneck detector. Runs on explicit analysis/export.</summary>
    public static class PerformanceAnalyzer
    {
        public static bool IsHitch(FrameSample sample, CaptureSettings settings)
        {
            var fps = Math.Max(1, settings?.targetFps ?? 60);
            var multiplier = Math.Max(1, settings?.hitchMultiplier ?? 1.5f);
            return Valid(sample.frameMs) && sample.frameMs > 1000d / fps * multiplier;
        }

        public static CaptureSummary Summarize(CaptureReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var summary = new CaptureSummary { totalGcAllocatedBytes = -1, gcCollections = -1 };
            var frames = new List<double>();
            var gpu = new List<double>();
            var gpuIds = new HashSet<ulong>();
            double total = 0;
            foreach (var sample in report.samples ?? Array.Empty<FrameSample>())
            {
                if (Valid(sample.frameMs))
                {
                    frames.Add(sample.frameMs);
                    total += sample.frameMs;
                    if (IsHitch(sample, report.settings)) summary.hitchFrames++;
                }
                if (Valid(sample.gpuMs) && sample.gpuTimestamp != 0 && gpuIds.Add(sample.gpuTimestamp)) gpu.Add(sample.gpuMs);
                if (sample.gcAllocatedBytes >= 0)
                {
                    if (summary.totalGcAllocatedBytes < 0) summary.totalGcAllocatedBytes = 0;
                    summary.totalGcAllocatedBytes = SaturatingAdd(summary.totalGcAllocatedBytes, sample.gcAllocatedBytes);
                }
                if (sample.gcCollections >= 0)
                    summary.gcCollections = (int)Math.Min(int.MaxValue, (long)Math.Max(0, summary.gcCollections) + sample.gcCollections);
                if (sample.drawCalls >= 0) summary.peakDrawCalls = Math.Max(summary.peakDrawCalls, sample.drawCalls);
            }
            frames.Sort();
            gpu.Sort();
            summary.frames = frames.Count;
            summary.averageFps = total > 0 ? frames.Count * 1000d / total : 0;
            summary.p50Ms = Percentile(frames, 0.50);
            summary.p95Ms = Percentile(frames, 0.95);
            summary.p99Ms = Percentile(frames, 0.99);
            summary.maxFrameMs = frames.Count == 0 ? 0 : frames[frames.Count - 1];
            summary.gpuP95Ms = gpu.Count == 0 ? -1 : Percentile(gpu, 0.95);
            return summary;
        }

        public static PerformanceFinding[] Analyze(CaptureReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var samples = report.samples ?? Array.Empty<FrameSample>();
            var settings = (report.settings ?? new CaptureSettings()).Copy();
            var summary = Summarize(report);
            var findings = new List<PerformanceFinding>();
            if (samples.Length == 0)
            {
                Add(findings, "数据", "尚无采样数据", "采样窗口为空，不能判断运行状态。", "进入 Play 或 Development Player，预热结束后复现问题，再停止记录。", -1);
                return findings.ToArray();
            }
            double budget = 1000d / settings.targetFps;
            FrameSample worst = FrameSample.Empty, cpu = FrameSample.Empty, render = FrameSample.Empty,
                wait = FrameSample.Empty, gpu = FrameSample.Empty, allocation = FrameSample.Empty,
                draws = FrameSample.Empty, setPass = FrameSample.Empty;
            var gpuIds = new HashSet<ulong>();
            foreach (var sample in samples)
            {
                if (sample.frameMs > worst.frameMs) worst = sample;
                if (sample.cpuMainMs > cpu.cpuMainMs) cpu = sample;
                if (sample.cpuRenderMs > render.cpuRenderMs) render = sample;
                if (sample.mainWaitMs > wait.mainWaitMs) wait = sample;
                if (sample.gpuTimestamp != 0 && gpuIds.Add(sample.gpuTimestamp) && sample.gpuMs > gpu.gpuMs) gpu = sample;
                if (sample.gcAllocatedBytes > allocation.gcAllocatedBytes) allocation = sample;
                if (sample.drawCalls > draws.drawCalls) draws = sample;
                if (sample.setPassCalls > setPass.setPassCalls) setPass = sample;
            }
            if (summary.hitchFrames > 0)
                Add(findings, "帧时间", "捕获到超阈值帧", $"保留窗口内 {summary.hitchFrames}/{summary.frames} 帧超过 {F(budget * settings.hitchMultiplier)} ms；最长 {F(worst.frameMs)} ms。",
                    "选择峰值附近的观测，复现同一操作；在 CPU Profiler Timeline 中查找对应时间段。观测帧号不等于 Profiler 窗口中的帧索引。", worst.frame);
            if (cpu.cpuMainMs > budget)
                Add(findings, "CPU", "主线程耗时超出帧预算", $"主线程最大 {F(cpu.cpuMainMs)} ms；目标预算 {F(budget)} ms。该 marker 包括同步与帧率等待。",
                    "打开 Profiler 的 CPU Usage / Timeline，区分脚本、物理、加载和等待；用 Hierarchy 的 Total/Self 时间继续定位函数，不能仅凭主线程总耗时断定 CPU 算力瓶颈。", cpu.frame);
            if (render.cpuRenderMs > budget)
                Add(findings, "CPU / 渲染", "渲染线程耗时偏高", $"渲染线程最大 {F(render.cpuRenderMs)} ms。等待 GPU 或驱动同步也可能包含在内。",
                    "检查 Render Thread 时间线及等待 marker，并结合实际 Draw Call、SetPass 和 GPU 时间分析。", render.frame);
            if (wait.mainWaitMs > budget * 0.5)
                Add(findings, "同步 / 限帧", "等待 marker 占用较长时间", $"等待 marker 最大 {F(wait.mainWaitMs)} ms；VSync={report.vSyncCount}，targetFrameRate={report.applicationTargetFrameRate}。",
                    "核对垂直同步、帧率上限与 GPU 完成等待。该值不是纯 CPU 计算，也不能直接从 Main Thread 相减得到准确工作时间。具体 marker 见指标来源。", wait.frame);
            if (gpu.gpuMs > budget)
                Add(findings, "GPU", "收到超预算的 GPU 时间样本", $"去重 GPU 样本 P95 为 {F(summary.gpuP95Ms)} ms，最大 {F(gpu.gpuMs)} ms；此处帧号是收到数据时的观测帧。",
                    "GPU 数据异步回传，不能据此锁定同一 CPU 帧。使用目标设备 GPU Profiler 或图形捕获工具检查阴影、透明叠加、后处理和 Shader。", gpu.frame);
            if (allocation.gcAllocatedBytes > 0 && allocation.gcAllocatedBytes >= settings.gcAllocationWarningBytes)
                Add(findings, "GC", "发现较大的单帧托管分配", $"最大观测分配 {F(allocation.gcAllocatedBytes / 1024d)} KiB；窗口累计 {F(summary.totalGcAllocatedBytes / 1048576d)} MiB。",
                    "在 Profiler 开启 GC.Alloc 调用栈，检查临时数组、字符串、LINQ、装箱和频繁实例化；分配量不能当作 GC 暂停时长。", allocation.frame);
            if (summary.gcCollections > 0)
                Add(findings, "GC", "采样期间发生了托管回收", $"GC.CollectionCount(0) 的增量合计为 {summary.gcCollections}；不包含回收耗时或调用栈。",
                    "在 CPU Timeline 检查 GC.Collect / 增量 GC 标记，确认是否与卡顿重合，再决定对象复用和内存生命周期优化。", -1);
            if (draws.drawCalls > settings.drawCallWarning)
                Add(findings, "Draw Call", "实际绘制调用超出设定阈值", $"Draw Calls Count 最大 {draws.drawCalls}；阈值 {settings.drawCallWarning}。这是引擎运行时计数器。",
                    "打开 Frame Debugger 检查实际绘制顺序、材质与批次原因，再评估实例化、SRP Batcher、UI 合批或遮挡剔除。", draws.frame);
            if (setPass.setPassCalls > settings.setPassWarning)
                Add(findings, "SetPass", "SetPass 调用偏多", $"SetPass Calls Count 最大 {setPass.setPassCalls}；阈值 {settings.setPassWarning}。",
                    "用 Frame Debugger 检查 Shader Pass、材质和渲染状态切换。SetPass 数量本身不等于耗时。", setPass.frame);
            var missing = new List<string>();
            if (cpu.cpuMainMs < 0) missing.Add("CPU 主线程");
            if (gpu.gpuMs < 0) missing.Add("GPU 时间");
            if (allocation.gcAllocatedBytes < 0) missing.Add("GC 分配");
            if (draws.drawCalls < 0) missing.Add("Draw Call");
            if (missing.Count > 0)
                Add(findings, "数据可用性", "部分指标没有有效样本", string.Join("、", missing) + " 不可用；不可用不代表开销为零。",
                    "查看指标来源和状态；在支持的图形 API 上使用 Development Build。GPU 需要有效 Frame Timing 数据；无图形设备的批处理环境不能验证 GPU 性能。", -1);
            if (report.isEditor)
                Add(findings, "采样环境", "当前为 Editor 观测", "编辑器窗口、Scene 视图和工具绘制可能影响时间、分配及渲染计数。",
                    "用于复现和初步定位；定量比较请在相同设备、相同图形设置的 Development Player 中复测。", -1);
            if (findings.Count == 0)
                Add(findings, "概览", "当前窗口未超过已配置阈值", "仅代表保留样本在本次阈值内，不代表游戏不存在其他性能问题。",
                    "覆盖加载、战斗高峰、UI 切换等真实操作，继续记录并检查更长时间的趋势。", -1);
            return findings.ToArray();
        }

        static bool Valid(double value) => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        static long SaturatingAdd(long left, long right) => long.MaxValue - left < right ? long.MaxValue : left + right;
        static double Percentile(List<double> sorted, double percentile) => sorted.Count == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * percentile) - 1)];
        static void Add(List<PerformanceFinding> target, string category, string title, string evidence, string nextStep, int frame)
            => target.Add(new PerformanceFinding { category = category, title = title, evidence = evidence, nextStep = nextStep, observationFrame = frame });
    }
}
