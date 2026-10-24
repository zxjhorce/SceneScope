using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SceneScope.Runtime
{
    /// <summary>Portable capture reports. Export runs outside the sampling hot path and needs no UnityEditor APIs.</summary>
    public static class RuntimeReportExporter
    {
        public const int MaximumSamples = 120000;
        public const int MaximumImportBytes = 128 * 1024 * 1024;
        const int MaximumChartBins = 1200;
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static void ExportJson(CaptureReport report, string path)
        {
            Validate(report);
            string json = JsonUtility.ToJson(report, true);
            if (Encoding.UTF8.GetByteCount(json) > MaximumImportBytes)
                throw new InvalidDataException("JSON 超过 128 MiB 可导入上限，请减少保留样本后重试。");
            Write(path, json);
        }

        public static CaptureReport ImportJson(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择 JSON 报告。", nameof(path));
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumImportBytes) throw new InvalidDataException("报告超过 128 MiB 导入上限。");
                var bytes = new byte[(int)stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new InvalidDataException("读取报告时文件内容不完整。");
                    read += count;
                }
                json = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            }
            PreflightSamples(json);
            // Clear field initializers so omitted required fields are rejected, not silently replaced.
            var report = new CaptureReport { schemaVersion = null, settings = null, metrics = null, samples = null };
            try { JsonUtility.FromJsonOverwrite(json, report); }
            catch (ArgumentException exception) { throw new InvalidDataException("无法解析 SceneScope 运行时 JSON 报告。", exception); }
            Validate(report);
            return report;
        }

        public static void ExportCsv(CaptureReport report, string path)
        {
            Validate(report);
            var csv = new StringBuilder(Math.Max(1024, report.samples.Length * 120));
            csv.Append("frame,timeSeconds,frameMs,cpuMainMs,cpuRenderMs,mainWaitMs,gpuMs,gpuTimestamp,gcAllocatedBytes,gcUsedBytes,totalUsedBytes,gcCollections,drawCalls,batches,setPassCalls,triangles,vertices\r\n");
            foreach (var s in report.samples)
            {
                csv.Append(s.frame.ToString(Invariant)).Append(',')
                    .Append(R(s.timeSeconds)).Append(',').Append(R(s.frameMs)).Append(',')
                    .Append(R(s.cpuMainMs)).Append(',').Append(R(s.cpuRenderMs)).Append(',')
                    .Append(R(s.mainWaitMs)).Append(',').Append(R(s.gpuMs)).Append(',')
                    .Append(s.gpuTimestamp.ToString(Invariant)).Append(',')
                    .Append(s.gcAllocatedBytes.ToString(Invariant)).Append(',')
                    .Append(s.gcUsedBytes.ToString(Invariant)).Append(',')
                    .Append(s.totalUsedBytes.ToString(Invariant)).Append(',')
                    .Append(s.gcCollections.ToString(Invariant)).Append(',')
                    .Append(s.drawCalls.ToString(Invariant)).Append(',')
                    .Append(s.batches.ToString(Invariant)).Append(',')
                    .Append(s.setPassCalls.ToString(Invariant)).Append(',')
                    .Append(s.triangles.ToString(Invariant)).Append(',')
                    .Append(s.vertices.ToString(Invariant)).Append("\r\n");
            }
            Write(path, csv.ToString());
        }

        public static void ExportHtml(CaptureReport report, string path)
        {
            Validate(report);
            CaptureSummary summary = PerformanceAnalyzer.Summarize(report);
            PerformanceFinding[] findings = PerformanceAnalyzer.Analyze(report);
            var html = new StringBuilder(65536);
            html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
                .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">")
                .Append("<title>SceneScope · 运行时性能报告</title><style>").Append(Styles)
                .Append("</style></head><body><main><header><a class=\"brand\" href=\"#top\"><b>S</b> SCENESCOPE</a><span class=\"eyebrow\">RUNTIME / PERFORMANCE EVIDENCE</span></header>")
                .Append("<section class=\"hero\" id=\"top\"><div><p class=\"eyebrow accent\">运行时性能观测</p><h1>找到卡顿，<br>保留证据。</h1><p class=\"lead\">把帧时间、CPU、GPU、内存分配和渲染计数放在同一个观察窗口，为下一步深入分析提供线索。</p><p class=\"muted\">")
                .Append(H(report.scene)).Append(" · Unity ").Append(H(report.unityVersion)).Append(" · ").Append(H(report.platform))
                .Append("</p></div><aside class=\"panel headline\"><span class=\"eyebrow\">P95 FRAME TIME</span><strong>")
                .Append(summary.frames == 0 ? "N/A" : F(summary.p95Ms)).Append("<small> ms</small></strong><p>目标 ")
                .Append(N(report.settings.targetFps)).Append(" FPS · 每帧预算 ").Append(F(1000d / report.settings.targetFps))
                .Append(" ms</p><span class=\"pill\">").Append(report.isEditor ? "Editor 观测 · 含编辑器干扰" : report.isDevelopmentBuild ? "Development Player 观测" : "Player 观测")
                .Append("</span></aside></section><div class=\"notice\"><strong>证据边界</strong><p>这是保留窗口内的采样快照。Main Thread 包含等待，不等于纯 CPU 工作；GPU 时间异步返回，不能断言与该行观测帧对齐；GC 分配和回收次数不等于 GC 暂停耗时。计数器缺失为 −1，显示为 N/A。启发式线索不能替代 Profiler 的调用栈与 GPU 分析。</p></div>")
                .Append("<section><div class=\"section-heading\"><div><p class=\"eyebrow\">01 / WINDOW</p><h2>采样窗口</h2></div><span class=\"muted\">").Append(N(report.samples.Length)).Append(" 个保留样本</span></div><div class=\"metrics\">");
            Card(html, "平均 FPS", summary.frames == 0 ? "N/A" : F(summary.averageFps), "1000 ÷ 保留样本平均帧时间");
            Card(html, "P50 / P95 / P99", summary.frames == 0 ? "N/A" : F(summary.p50Ms) + " / " + F(summary.p95Ms) + " / " + F(summary.p99Ms), "帧时间，单位 ms");
            Card(html, "最慢帧", summary.frames == 0 ? "N/A" : F(summary.maxFrameMs) + " ms", "保留窗口中的帧时间峰值");
            Card(html, "卡顿样本", N(summary.hitchFrames), "阈值 " + F(HitchThreshold(report)) + " ms；不是卡顿事件个数");
            Card(html, "GPU P95", MetricMs(summary.gpuP95Ms), "仅统计有效 GPU 观测，异步计时");
            Card(html, "Draw Call 峰值", MetricCount(summary.peakDrawCalls), "运行时计数器观测，不是 Renderer 数量");
            Card(html, "累计 GC 分配", HasMetric(report.samples, s => s.gcAllocatedBytes) ? Bytes(summary.totalGcAllocatedBytes) : "N/A", "保留窗口内有效观测之和，不是 GC 暂停");
            Card(html, "GC 回收次数", HasMetric(report.samples, s => s.gcCollections) ? N(summary.gcCollections) : "N/A", "第 0 代回收计数变化，不提供暂停耗时");
            html.Append("</div><p class=\"muted window-note\">开始 UTC：").Append(H(report.startedAtUtc)).Append(" · 快照结束 UTC：").Append(H(report.endedAtUtc))
                .Append("<br>环形缓冲区上限 ").Append(N(report.settings.maxSamples)).Append(" 个样本；已覆盖 ").Append(N(report.droppedSamples))
                .Append(" 个旧样本。上述汇总只包含当前保留的窗口，不代表从开始到结束的完整会话。");
            if (report.samples.Length > 0)
                html.Append(" 保留帧号 ").Append(N(report.samples[0].frame)).Append("–").Append(N(report.samples[report.samples.Length - 1].frame))
                    .Append("；会话经过时间 ").Append(F(report.samples[0].timeSeconds)).Append("–").Append(F(report.samples[report.samples.Length - 1].timeSeconds)).Append(" s。");
            html.Append("</p></section><section><div class=\"section-heading\"><div><p class=\"eyebrow\">02 / TIMELINE</p><h2>帧时间与执行线索</h2></div></div>");
            Chart(html, report);
            html.Append("</section><section><div class=\"section-heading\"><div><p class=\"eyebrow\">03 / FINDINGS</p><h2>下一步查什么</h2></div><span class=\"muted\">基于当前保留窗口的启发式线索</span></div><div class=\"findings\">");
            if (findings.Length == 0) html.Append("<div class=\"panel\"><h3>当前阈值下未生成线索</h3><p>这不代表游戏没有性能问题。检查目标设备、复现场景、采样覆盖范围和计数器可用性。</p></div>");
            foreach (var finding in findings)
            {
                html.Append("<article class=\"panel finding\"><span class=\"eyebrow accent\">").Append(H(finding.category)).Append("</span><h3>")
                    .Append(H(finding.title)).Append("</h3><p>").Append(H(finding.evidence)).Append("</p><div class=\"next\"><b>进一步验证</b><p>")
                    .Append(H(finding.nextStep)).Append("</p></div>");
                if (finding.observationFrame >= 0) html.Append("<span class=\"muted\">观测帧 #").Append(N(finding.observationFrame)).Append("；不同来源计数器不保证严格同帧</span>");
                html.Append("</article>");
            }
            html.Append("</div></section><section><div class=\"section-heading\"><div><p class=\"eyebrow\">04 / HITCH SAMPLES</p><h2>最慢的 20 个卡顿样本</h2></div><span class=\"muted\">按帧时间降序</span></div>");
            HitchTable(html, report);
            html.Append("</section><section><div class=\"section-heading\"><div><p class=\"eyebrow\">05 / REPRODUCIBILITY</p><h2>设备、阈值与能力</h2></div></div><div class=\"context-grid\"><div class=\"panel\"><h3>采集环境</h3><table><tbody>");
            Row(html, "Schema", report.schemaVersion);
            Row(html, "Unity", report.unityVersion);
            Row(html, "平台 / 场景", report.platform + " / " + report.scene);
            Row(html, "CPU", report.cpu);
            Row(html, "GPU", report.gpu);
            Row(html, "图形 API", report.graphicsApi);
            Row(html, "运行模式", report.isEditor ? "Editor（可能包含编辑器开销）" : report.isDevelopmentBuild ? "Development Build" : "非 Development Player");
            Row(html, "VSync / targetFrameRate", N(report.vSyncCount) + " / " + N(report.applicationTargetFrameRate));
            html.Append("</tbody></table></div><div class=\"panel\"><h3>本次阈值</h3><table><tbody>");
            Row(html, "目标帧率", N(report.settings.targetFps) + " FPS");
            Row(html, "卡顿倍数 / 阈值", F(report.settings.hitchMultiplier) + " × / " + F(HitchThreshold(report)) + " ms");
            Row(html, "预热帧数", N(report.settings.warmupFrames));
            Row(html, "保留样本上限", N(report.settings.maxSamples));
            Row(html, "GC 分配警戒值", Bytes(report.settings.gcAllocationWarningBytes) + " / 观测");
            Row(html, "Draw Call 警戒值", N(report.settings.drawCallWarning));
            Row(html, "SetPass 警戒值", N(report.settings.setPassWarning));
            html.Append("</tbody></table></div></div><div class=\"table-wrap capability\"><table><thead><tr><th>指标</th><th>能力</th><th>来源</th><th>单位</th><th>说明</th></tr></thead><tbody>");
            if (report.metrics.Length == 0) html.Append("<tr><td colspan=\"5\">未记录能力描述；应结合原始样本中的 −1 判断缺失数据。</td></tr>");
            foreach (var metric in report.metrics)
                html.Append("<tr><td>").Append(H(metric.name)).Append("</td><td><span class=\"pill\">").Append(metric.available ? "可用" : "不可用 / N/A")
                    .Append("</span></td><td>").Append(H(metric.source)).Append("</td><td>").Append(H(metric.unit)).Append("</td><td>").Append(H(metric.note)).Append("</td></tr>");
            html.Append("</tbody></table></div><div class=\"notice\"><strong>采集器记录的限制</strong><p>").Append(H(report.limitations))
                .Append("</p><p>能力可用不保证每帧都有有效数据。报告不包含函数级 CPU 调用栈、GC 暂停归因、逐 Draw Call 的对象或材质列表；请在目标设备上结合 Unity Profiler、Frame Debugger 和平台 GPU 工具验证。</p></div></section><footer><span>SCENESCOPE / 观测 · 定位 · 验证</span><span>独立离线报告 · 无外部资源 · 可打印</span></footer></main></body></html>");
            Write(path, html.ToString());
        }

        static void Chart(StringBuilder html, CaptureReport report)
        {
            var samples = report.samples;
            if (samples.Length == 0) { html.Append("<div class=\"panel empty\">没有样本可绘制。</div>"); return; }
            int bins = Math.Min(MaximumChartBins, samples.Length);
            var frames = new double[bins];
            var mains = new double[bins];
            var gpus = new double[bins];
            double ceiling = Math.Max(1, HitchThreshold(report));
            for (int b = 0; b < bins; b++)
            {
                int from = b * samples.Length / bins;
                int to = (b + 1) * samples.Length / bins;
                frames[b] = Peak(samples, from, to, s => s.frameMs);
                mains[b] = Peak(samples, from, to, s => s.cpuMainMs);
                gpus[b] = Peak(samples, from, to, s => s.gpuMs);
                ceiling = Math.Max(ceiling, Math.Max(frames[b], Math.Max(mains[b], gpus[b])));
            }
            if (ceiling < double.MaxValue / 1.1) ceiling *= 1.1;
            html.Append("<div class=\"panel chart\"><div class=\"legend\"><span class=\"frame\">● 帧间隔</span><span class=\"main\">● Main Thread（含等待）</span><span class=\"gpu\">● GPU（异步观测）</span><span>┄ 卡顿阈值</span></div><svg viewBox=\"0 0 1100 320\" role=\"img\" aria-label=\"保留窗口帧时间、主线程和 GPU 的采样峰值曲线\">");
            for (int i = 0; i <= 4; i++)
            {
                double y = 20 + i * 60;
                html.Append("<line class=\"grid\" x1=\"65\" x2=\"1075\" y1=\"").Append(F(y)).Append("\" y2=\"").Append(F(y)).Append("\"/><text x=\"55\" y=\"")
                    .Append(F(y + 4)).Append("\" text-anchor=\"end\">").Append(F(ceiling * ((4 - i) / 4d))).Append("</text>");
            }
            html.Append("<text x=\"12\" y=\"15\">ms</text><text x=\"65\" y=\"288\">1</text><text x=\"1075\" y=\"288\" text-anchor=\"end\">")
                .Append(N(samples.Length)).Append("</text><text x=\"570\" y=\"310\" text-anchor=\"middle\">保留窗口采样序号</text>");
            double thresholdY = 260 - HitchThreshold(report) / ceiling * 240;
            html.Append("<line class=\"threshold\" x1=\"65\" x2=\"1075\" y1=\"").Append(F(thresholdY)).Append("\" y2=\"").Append(F(thresholdY)).Append("\"/>");
            Series(html, frames, ceiling, "frame-line");
            Series(html, mains, ceiling, "main-line");
            Series(html, gpus, ceiling, "gpu-line");
            html.Append("</svg><p class=\"muted\">最多 ").Append(N(MaximumChartBins)).Append(" 个等样本数区间，每条线取区间内最大值以保留尖峰。含缺失值的区间留空，避免跨过 N/A 连接曲线；因此可能省略同一区间内部分有效观测。不同线的区间峰值可能来自不同帧，GPU 也不保证同帧。完整数据见 JSON / CSV。</p></div>");
        }

        static double Peak(FrameSample[] samples, int from, int to, Func<FrameSample, double> select)
        {
            double peak = -1;
            for (int i = from; i < to; i++)
            {
                double value = select(samples[i]);
                if (value < 0) return -1;
                peak = Math.Max(peak, value);
            }
            return peak;
        }

        static void Series(StringBuilder html, double[] values, double ceiling, string cssClass)
        {
            html.Append("<path class=\"").Append(cssClass).Append("\" d=\"");
            bool connected = false;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] < 0) { connected = false; continue; }
                double x = values.Length == 1 ? 570 : 65 + i * 1010d / (values.Length - 1);
                double y = 260 - values[i] / ceiling * 240;
                html.Append(connected ? " L" : " M").Append(F(x)).Append(' ').Append(F(y));
                // A very short horizontal segment also makes isolated valid observations visible.
                if (!connected) html.Append(" l0.4 0");
                connected = true;
            }
            html.Append("\"/>");
        }

        static void HitchTable(StringBuilder html, CaptureReport report)
        {
            var worst = new List<FrameSample>(20);
            foreach (var sample in report.samples)
            {
                if (!PerformanceAnalyzer.IsHitch(sample, report.settings)) continue;
                int insert = worst.FindIndex(s => s.frameMs < sample.frameMs);
                if (insert < 0) insert = worst.Count;
                if (insert < 20) worst.Insert(insert, sample);
                if (worst.Count > 20) worst.RemoveAt(20);
            }
            if (worst.Count == 0) { html.Append("<div class=\"panel empty\">保留窗口中没有达到卡顿阈值的样本。</div>"); return; }
            html.Append("<div class=\"table-wrap\"><table class=\"hitches\"><thead><tr><th>观测帧</th><th>帧间隔 ms</th><th>Main ms</th><th>Render ms</th><th>Wait ms</th><th>GPU ms*</th><th>GC 分配</th><th>GC 次数</th><th>Draw Call</th><th>SetPass</th><th>细节</th></tr></thead><tbody>");
            foreach (var s in worst)
            {
                html.Append("<tr><td>#").Append(N(s.frame)).Append("</td><td class=\"accent\">").Append(F(s.frameMs))
                    .Append("</td><td>").Append(MetricMs(s.cpuMainMs, false)).Append("</td><td>").Append(MetricMs(s.cpuRenderMs, false))
                    .Append("</td><td>").Append(MetricMs(s.mainWaitMs, false)).Append("</td><td>").Append(MetricMs(s.gpuMs, false))
                    .Append("</td><td>").Append(Bytes(s.gcAllocatedBytes)).Append("</td><td>").Append(MetricCount(s.gcCollections))
                    .Append("</td><td>").Append(MetricCount(s.drawCalls)).Append("</td><td>").Append(MetricCount(s.setPassCalls))
                    .Append("</td><td><details><summary>原始观测</summary><dl><dt>经过时间</dt><dd>").Append(F(s.timeSeconds)).Append(" s</dd><dt>GPU timestamp</dt><dd>")
                    .Append(s.gpuTimestamp.ToString(Invariant)).Append("</dd><dt>Batches</dt><dd>").Append(MetricCount(s.batches))
                    .Append("</dd><dt>三角形 / 顶点</dt><dd>").Append(MetricCount(s.triangles)).Append(" / ").Append(MetricCount(s.vertices))
                    .Append("</dd><dt>GC 已用内存</dt><dd>").Append(Bytes(s.gcUsedBytes)).Append("</dd><dt>总已用内存</dt><dd>").Append(Bytes(s.totalUsedBytes))
                    .Append("</dd></dl></details></td></tr>");
            }
            html.Append("</tbody></table></div><p class=\"muted\">* GPU 为异步返回的有效计时；表格同一行只表示采集时一起记录，不保证对应同一个实际渲染帧。Main / Render / Wait 不应相加作为帧耗时。</p>");
        }

        static void Validate(CaptureReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            int major;
            if (string.IsNullOrEmpty(report.schemaVersion) || !int.TryParse(report.schemaVersion.Split('.')[0], NumberStyles.None, Invariant, out major) || major != 2)
                throw new InvalidDataException("不支持此报告 Schema；需要 SceneScope 运行时 2.x 报告。");
            if (report.settings == null || report.metrics == null || report.samples == null)
                throw new InvalidDataException("报告缺少 settings、metrics 或 samples。");
            if (report.samples.Length > MaximumSamples) throw new InvalidDataException("报告样本超过 120000 上限。");
            if (report.metrics.Length > 1024) throw new InvalidDataException("报告指标描述过多。");
            var settings = report.settings;
            if (settings.targetFps < 1 || settings.targetFps > 1000 || !Finite(settings.hitchMultiplier) || settings.hitchMultiplier < 1 ||
                settings.maxSamples < 60 || settings.maxSamples > MaximumSamples || settings.warmupFrames < 0 || settings.warmupFrames > 600 ||
                settings.gcAllocationWarningBytes < 0 || settings.drawCallWarning < 0 || settings.setPassWarning < 0 || report.droppedSamples < 0)
                throw new InvalidDataException("报告包含无效采集设置。");
            CheckText(report.schemaVersion); CheckText(report.startedAtUtc); CheckText(report.endedAtUtc);
            CheckText(report.unityVersion); CheckText(report.platform); CheckText(report.scene); CheckText(report.cpu);
            CheckText(report.gpu); CheckText(report.graphicsApi); CheckText(report.limitations);
            foreach (var metric in report.metrics)
            {
                if (metric == null || string.IsNullOrWhiteSpace(metric.name)) throw new InvalidDataException("报告包含无效指标能力描述。");
                CheckText(metric.name); CheckText(metric.source); CheckText(metric.unit); CheckText(metric.note);
            }
            int previousFrame = -1;
            double previousTime = -1;
            foreach (var s in report.samples)
            {
                if (s.frame < 0 || !Finite(s.timeSeconds) || s.timeSeconds < 0 || !Finite(s.frameMs) || s.frameMs < 0 ||
                    !ValidMetric(s.cpuMainMs) || !ValidMetric(s.cpuRenderMs) || !ValidMetric(s.mainWaitMs) || !ValidMetric(s.gpuMs) ||
                    s.gcAllocatedBytes < -1 || s.gcUsedBytes < -1 || s.totalUsedBytes < -1 || s.gcCollections < -1 ||
                    s.drawCalls < -1 || s.batches < -1 || s.setPassCalls < -1 || s.triangles < -1 || s.vertices < -1)
                    throw new InvalidDataException("报告包含无效样本：帧间隔必须为有限非负数，其他指标只允许非负数或 −1（未知）。");
                if (s.frame <= previousFrame || s.timeSeconds < previousTime)
                    throw new InvalidDataException("报告样本必须按帧号严格递增排列，且经过时间不能倒退。");
                previousFrame = s.frame;
                previousTime = s.timeSeconds;
            }
        }

        // Count the canonical samples array before JsonUtility can allocate a huge struct array.
        // Reports exported by this tool use plain ASCII field names; required escaped field names are not accepted.
        static void PreflightSamples(string json)
        {
            string trimmed = json.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}') throw new InvalidDataException("报告不是 JSON 对象。");
            int depth = 0;
            bool found = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    int start = i;
                    i = StringEnd(json, i);
                    if (depth != 1 || i - start != 8 || string.CompareOrdinal(json, start + 1, "samples", 0, 7) != 0) continue;
                    int value = i + 1;
                    while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
                    if (value >= json.Length || json[value++] != ':') continue;
                    while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
                    if (value >= json.Length || json[value] != '[') throw new InvalidDataException("报告 samples 必须为数组。");
                    CountArray(json, value);
                    found = true;
                }
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                if (depth < 0 || depth > 64) throw new InvalidDataException("报告 JSON 嵌套结构无效或过深。");
            }
            if (!found || depth != 0) throw new InvalidDataException("报告缺少有效 samples 数组。");
        }

        static void CountArray(string json, int start)
        {
            int depth = 1;
            int count = 0;
            bool expecting = true;
            for (int i = start + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (depth == 1 && c == ']') return;
                if (depth == 1 && c == ',') { expecting = true; continue; }
                if (char.IsWhiteSpace(c)) continue;
                if (depth == 1 && expecting)
                {
                    if (++count > MaximumSamples) throw new InvalidDataException("报告样本超过 120000 上限。");
                    expecting = false;
                }
                if (c == '"') i = StringEnd(json, i);
                else if (c == '[' || c == '{') { if (++depth > 64) throw new InvalidDataException("报告 JSON 嵌套过深。"); }
                else if (c == ']' || c == '}') depth--;
            }
            throw new InvalidDataException("报告 samples 数组不完整。");
        }

        static int StringEnd(string text, int start)
        {
            for (int i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return i;
            }
            throw new InvalidDataException("报告 JSON 字符串不完整。");
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool ValidMetric(double value) => Finite(value) && (value == -1 || value >= 0);
        static void CheckText(string value) { if (value != null && value.Length > 16384) throw new InvalidDataException("报告文本字段超过长度上限。"); }
        static bool HasMetric(FrameSample[] samples, Func<FrameSample, long> select) { foreach (var sample in samples) if (select(sample) >= 0) return true; return false; }
        static double HitchThreshold(CaptureReport report) => 1000d / report.settings.targetFps * report.settings.hitchMultiplier;
        static string F(double value) => value.ToString("0.##", Invariant);
        static string R(double value) => value.ToString("R", Invariant);
        static string N(long value) => value.ToString(Invariant);
        static string MetricMs(double value, bool unit = true) => value < 0 ? "N/A" : F(value) + (unit ? " ms" : "");
        static string MetricCount(long value) => value < 0 ? "N/A" : N(value);
        static string Bytes(long value) => value < 0 ? "N/A" : value >= 1048576 ? F(value / 1048576d) + " MiB" : value >= 1024 ? F(value / 1024d) + " KiB" : N(value) + " B";
        static string H(string value) => string.IsNullOrEmpty(value) ? "—" : value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        static void Card(StringBuilder html, string title, string value, string note) => html.Append("<article class=\"metric\"><span>").Append(H(title)).Append("</span><strong>").Append(H(value)).Append("</strong><p>").Append(H(note)).Append("</p></article>");
        static void Row(StringBuilder html, string key, string value) => html.Append("<tr><th>").Append(H(key)).Append("</th><td>").Append(H(value)).Append("</td></tr>");
        static void Write(string path, string value)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择报告保存路径。", nameof(path));
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, value, new UTF8Encoding(false));
        }

        const string Styles = @"
:root{color-scheme:dark;--bg:#0b111c;--panel:#121c2b;--line:#29364a;--text:#eef3fa;--muted:#a8b6c8;--accent:#f7c66c;--cyan:#70d7e6;--purple:#c7a5ff}*{box-sizing:border-box}body{margin:0;background:radial-gradient(ellipse at 85% 0,#192b43,transparent 45%),var(--bg);color:var(--text);font:15px/1.65 'Segoe UI','Microsoft YaHei',sans-serif}main{max-width:1380px;padding:0 48px;margin:auto}header{display:flex;align-items:center;justify-content:space-between;padding:30px 0;border-bottom:1px solid var(--line)}a{color:inherit;text-decoration:none}.brand{font-size:17px;letter-spacing:3px;font-weight:700}.brand b{display:inline-grid;place-items:center;background:var(--accent);color:#162030;width:32px;height:32px;border-radius:9px;letter-spacing:0;margin-right:9px}.eyebrow{font-size:11px;letter-spacing:2px;font-weight:700;color:var(--muted)}.accent{color:var(--accent)}.hero{display:grid;grid-template-columns:1.5fr 1fr;gap:72px;align-items:center;padding:65px 0 48px}h1{font-size:58px;line-height:1.12;letter-spacing:-2px;margin:20px 0 26px}h2{font-size:28px;line-height:1.3;margin:5px 0 0}h3{font-size:18px;margin:10px 0 14px}.lead{max-width:620px;color:#c3cede;font-size:17px}.muted{color:var(--muted);font-size:13px}.panel{background:var(--panel);border:1px solid var(--line);border-radius:16px;padding:25px}.headline{border-top:3px solid var(--accent);padding:35px}.headline>strong{display:block;font-size:64px;font-weight:600;letter-spacing:-3px;color:var(--accent);line-height:1.4}.headline small{font-size:19px;letter-spacing:0}.pill{display:inline-block;padding:4px 9px;border:1px solid #42526b;border-radius:5px;font-size:11px;color:#dce6f3}.notice{padding:20px 25px;border-left:3px solid var(--accent);background:#182131;border-radius:0 12px 12px 0}.notice strong{color:var(--accent);font-size:13px}.notice p{color:#c4cfe0;font-size:13px;margin:5px 0}section{margin-bottom:48px}.section-heading{display:flex;align-items:flex-end;justify-content:space-between;gap:20px;margin:44px 0 23px}.section-heading p{margin-bottom:7px}.metrics{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px}.metric{padding:21px;background:var(--panel);border:1px solid var(--line);border-radius:12px}.metric>span{color:var(--muted);font-size:12px}.metric strong{display:block;font-size:24px;letter-spacing:-.5px;margin:13px 0 9px;overflow-wrap:anywhere}.metric p{font-size:11px;color:var(--muted);line-height:1.6;margin:0}.window-note{line-height:1.9}.chart{padding:22px 20px 15px;overflow:hidden}.legend{display:flex;flex-wrap:wrap;gap:22px;font-size:12px;padding:0 5px 16px;color:var(--muted)}.frame{color:var(--accent)}.main{color:var(--cyan)}.gpu{color:var(--purple)}svg{width:100%;height:auto;display:block}svg text{font-size:11px;fill:#a8b6c8}.grid{stroke:#29364a;stroke-width:1}.threshold{stroke:#9dabbc;stroke-width:1;stroke-dasharray:6 5}.frame-line,.main-line,.gpu-line{fill:none;stroke-width:1.6;stroke-linecap:round;stroke-linejoin:round}.frame-line{stroke:var(--accent)}.main-line{stroke:var(--cyan)}.gpu-line{stroke:var(--purple)}.chart>p{font-size:11px;margin-bottom:0}.findings{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:16px}.finding p{font-size:13px;color:#cbd5e5}.next{border-top:1px solid var(--line);padding-top:14px;margin-top:18px}.next b{font-size:12px;color:var(--cyan)}.table-wrap{overflow-x:auto;border:1px solid var(--line);border-radius:12px;background:var(--panel)}table{width:100%;border-collapse:collapse;font-size:12px}th,td{padding:13px 12px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}thead{background:#1b283b;color:var(--muted)}tbody tr:last-child>*{border-bottom:0}.hitches{min-width:1050px}.hitches td{font-variant-numeric:tabular-nums}.hitches summary{cursor:pointer;white-space:nowrap;color:var(--cyan)}details[open]{min-width:185px}dl{margin:8px 0;font-size:11px}dt{color:var(--muted)}dd{margin:0 0 6px;overflow-wrap:anywhere}.context-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:20px}.context-grid th{width:37%;color:var(--muted);font-weight:400}.context-grid td,.capability td{overflow-wrap:anywhere}.capability{margin:20px 0}.empty{color:var(--muted);text-align:center;padding:36px}footer{border-top:1px solid var(--line);padding:28px 0 38px;color:var(--muted);font-size:11px;display:flex;justify-content:space-between;gap:20px}@media(max-width:900px){main{padding:0 22px}.hero{gap:24px;grid-template-columns:1fr 1fr}h1{font-size:45px}.metrics{grid-template-columns:repeat(2,minmax(0,1fr))}.headline{padding:23px}.headline>strong{font-size:50px}.findings,.context-grid{grid-template-columns:1fr}}@media(max-width:600px){header>.eyebrow{display:none}.hero{grid-template-columns:1fr;padding:35px 0}.section-heading{align-items:flex-start;flex-direction:column}.metric{padding:15px}.metric strong{font-size:21px}footer{flex-direction:column}.panel{padding:19px}}@media print{body{background:white;color:#162030}main{max-width:none;padding:0}header,.hero,.panel,.metric,.notice,.table-wrap{break-inside:avoid}.hero{padding:25px 0;gap:25px}h1{font-size:42px}.panel,.metric,.notice,.table-wrap{background:#f4f6f9;border-color:#c8d0dd}.muted,.metric>span,.metric p,.finding p,.notice p,.eyebrow,.context-grid th,footer{color:#42536c}.headline>strong,.accent,.notice strong{color:#81520a}.pill{color:#344762}.table-wrap{overflow:visible}.hitches{min-width:0;font-size:8px}th,td{padding:6px}thead{background:#e7edf4}.chart{background:#111d30}.chart .muted{color:#c5d0df}.legend{color:#c5d0df}.table-wrap details{display:none}.capability{font-size:10px}.findings{grid-template-columns:1fr}.section-heading{margin-top:24px}section{margin-bottom:24px}}
";
    }
}
