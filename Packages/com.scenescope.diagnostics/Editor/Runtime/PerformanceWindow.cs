using System;
using System.Collections.Generic;
using System.Globalization;
using SceneScope.Runtime;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace SceneScope.Editor
{
    /// <summary>Bounded live observations and offline device report inspection. No profiler work runs in edit mode.</summary>
    public sealed class PerformanceWindow : EditorWindow
    {
        const string StylePath = "Packages/com.scenescope.diagnostics/Editor/Runtime/PerformanceWindow.uss";
        static readonly Color FrameColor = new Color(.28f, .84f, .74f);
        static readonly Color CpuColor = new Color(.40f, .65f, 1f);
        static readonly Color GpuColor = new Color(1f, .70f, .35f);
        [SerializeField] CaptureSettings settings = new CaptureSettings();
        [SerializeField] CaptureReport report;
        SceneScopeCapture capture;
        GameObject ownedCapture;
        CaptureSummary summary;
        PerformanceFinding[] findings = Array.Empty<PerformanceFinding>();
        readonly List<int> hitchIndices = new List<int>();
        Label state, context, footer, graphCaption, selection, summaryText, findingsTitle;
        readonly Label[] values = new Label[6];
        readonly Label[] captions = new Label[6];
        Button startButton, stopButton, analyzeButton, importButton;
        readonly List<Button> exportButtons = new List<Button>();
        VisualElement settingsContainer, findingsContainer, hitchesContainer, supportContainer;
        IMGUIContainer graph;
        SliderInt historySlider;
        Toggle followToggle;
        int selectedIndex = -1;
        int selectedFrame = -1;
        int historyStart;
        const int GraphSamples = 300;
        bool followLatest = true;
        bool observingCapture;
        bool updatingSlider;
        double nextRefresh, nextHitches;

        [MenuItem("Tools/SceneScope/运行时性能排查", priority = 11)]
        public static void Open()
        {
            var window = GetWindow<PerformanceWindow>();
            window.titleContent = new GUIContent("SceneScope · Runtime");
            window.minSize = new Vector2(1120, 760);
            window.Show();
        }

        void OnEnable()
        {
            titleContent = new GUIContent("SceneScope · Runtime");
            minSize = new Vector2(1120, 760);
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            if (report == null) report = SceneScopeCapture.LastReport;
            if (report != null) AnalyzeReport();
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            if (ownedCapture != null)
            {
                if (capture != null && capture.gameObject == ownedCapture && capture.IsRecording)
                    report = capture.StopCapture();
                DestroyImmediate(ownedCapture);
                ownedCapture = null;
            }
        }

        void PlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode && capture != null && capture.IsRecording)
                StopRecording();
            Refresh();
        }

        bool Recording => capture != null && capture.IsRecording;
        int Count => Recording ? capture.Count : report?.samples?.Length ?? 0;
        FrameSample Sample(int index) => Recording ? capture.GetSample(index) : report.samples[index];
        CaptureSettings CurrentSettings => Recording ? capture.Settings : report?.settings ?? settings;

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            exportButtons.Clear();
            root.AddToClassList("runtime-scope");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (sheet != null) root.styleSheets.Add(sheet);
            var masthead = Box("masthead");
            var brand = Row();
            brand.Add(Text("S", "brand-mark"));
            brand.Add(Text("SCENESCOPE", "brand-name"));
            brand.Add(Text("RUNTIME LAB", "brand-sub"));
            masthead.Add(brand);
            state = Text("等待采集", "state");
            masthead.Add(state);
            root.Add(masthead);
            var body = Row("body");
            body.Add(BuildControls());
            var workspace = new ScrollView(ScrollViewMode.Vertical);
            workspace.AddToClassList("workspace");
            var heading = Row("heading");
            var copy = Box();
            copy.Add(Text("运行时性能排查", "title"));
            copy.Add(Text("捕获卡顿现场，再沿证据定位原因。", "muted"));
            heading.Add(copy);
            var exports = Row("exports");
            foreach (var kind in new[] { "JSON", "CSV", "HTML" })
            {
                var format = kind;
                var button = Action(kind, () => Export(format), "compact");
                exportButtons.Add(button);
                exports.Add(button);
            }
            heading.Add(exports);
            workspace.Add(heading);
            context = Text("在 Unity 顶部手动进入 Play，然后开始采集；也可导入真机 JSON 报告。", "context");
            workspace.Add(context);
            var metrics = Box("metric-grid");
            string[] names = { "FRAME / FPS", "CPU 主线程", "GPU 时间", "GC 分配 / 帧", "DRAW CALLS", "SET PASS" };
            for (int i = 0; i < names.Length; i++)
            {
                var card = Box("metric-card");
                card.Add(Text(names[i], "eyebrow"));
                values[i] = Text("N/A", "metric-value");
                captions[i] = Text("等待有效计数器", "metric-caption");
                card.Add(values[i]); card.Add(captions[i]); metrics.Add(card);
            }
            workspace.Add(metrics);
            workspace.Add(BuildTimeline());
            var summaryPanel = Box("panel");
            summaryPanel.Add(Text("采样统计", "section-title"));
            summaryText = Text("停止采集后生成完整统计；实时卡片展示最新观测。", "summary");
            summaryPanel.Add(summaryText);
            workspace.Add(summaryPanel);
            var columns = Row("columns");
            var hitches = Box("panel", "hitch-panel");
            hitches.Add(Text("卡顿观察点", "section-title"));
            hitches.Add(Text("最近 8 个超预算观测 · 点击跳转时间轴", "hint"));
            hitchesContainer = Box(); hitches.Add(hitchesContainer);
            columns.Add(hitches);
            var findingsPanel = Box("panel", "findings-panel");
            findingsTitle = Text("排查建议", "section-title"); findingsPanel.Add(findingsTitle);
            findingsContainer = Box(); findingsPanel.Add(findingsContainer);
            columns.Add(findingsPanel);
            workspace.Add(columns);
            var support = new Foldout { text = "数据来源与设备支持", value = false };
            support.AddToClassList("support-foldout");
            supportContainer = Box(); support.Add(supportContainer); workspace.Add(support);
            workspace.Add(Text("读数约束：CPU 主线程含等待；GPU 异步返回，与所选帧不保证对齐。GC 分配和回收次数不代表暂停耗时。N/A 表示平台或当前配置未提供数据。", "limitations"));
            body.Add(workspace); root.Add(body);
            footer = Text("就绪 · 数据保留在有界环形缓冲区；导出 JSON 可用于后续复查。", "footer"); root.Add(footer);
            RefreshAnalysis(); RefreshHitches(); RefreshSupport(); Refresh();
        }

        VisualElement BuildControls()
        {
            var sidebar = new ScrollView(ScrollViewMode.Vertical);
            sidebar.AddToClassList("sidebar");
            sidebar.Add(Text("CAPTURE SESSION", "eyebrow"));
            sidebar.Add(Text("采集控制", "sidebar-title"));
            sidebar.Add(Text("Play Mode / Development Build", "hint"));
            startButton = Action("●  开始采集", StartRecording, "primary");
            stopButton = Action("■  停止并分析", StopRecording);
            analyzeButton = Action("分析当前快照", AnalyzeSnapshot);
            importButton = Action("导入真机 JSON", Import);
            sidebar.Add(startButton); sidebar.Add(stopButton); sidebar.Add(analyzeButton); sidebar.Add(importButton);
            sidebar.Add(Text("真机：在开发包中添加 SceneScopeCapture，结束采集后将 JSON 文件导回本窗口。", "hint"));
            sidebar.Add(Divider());
            sidebar.Add(Text("CAPTURE BUDGET", "eyebrow"));
            settingsContainer = Box();
            settingsContainer.Add(IntField("目标 FPS", settings.targetFps, value => settings.targetFps = Mathf.Clamp(value, 1, 1000)));
            var multiplier = new FloatField("卡顿倍率") { value = settings.hitchMultiplier };
            multiplier.RegisterValueChangedCallback(evt => settings.hitchMultiplier = float.IsNaN(evt.newValue) || float.IsInfinity(evt.newValue) ? 1.5f : Mathf.Max(1, evt.newValue));
            settingsContainer.Add(multiplier);
            settingsContainer.Add(IntField("保留帧数", settings.maxSamples, value => settings.maxSamples = Mathf.Clamp(value, 60, 120000)));
            settingsContainer.Add(IntField("预热帧数", settings.warmupFrames, value => settings.warmupFrames = Mathf.Clamp(value, 0, 600)));
            settingsContainer.Add(IntField("GC 警戒 / KB", (int)(settings.gcAllocationWarningBytes / 1024), value => settings.gcAllocationWarningBytes = Math.Max(0L, value) * 1024));
            settingsContainer.Add(IntField("Draw Call 警戒", settings.drawCallWarning, value => settings.drawCallWarning = Math.Max(0, value)));
            settingsContainer.Add(IntField("SetPass 警戒", settings.setPassWarning, value => settings.setPassWarning = Math.Max(0, value)));
            sidebar.Add(settingsContainer);
            sidebar.Add(Text("卡顿阈值 = 帧预算 × 倍率。超出保留帧数后覆盖最早数据。设置仅应用于下一次采集。", "hint"));
            sidebar.Add(Divider());
            sidebar.Add(Text("FOLLOW THE EVIDENCE", "eyebrow"));
            sidebar.Add(Action("打开 Unity Profiler ↗", () => OpenUnityTool("Window/Analysis/Profiler")));
            sidebar.Add(Action("打开 Frame Debugger ↗", () => OpenUnityTool("Window/Analysis/Frame Debugger")));
            sidebar.Add(Text("CPU / GC：查看 Timeline 与分配调用栈。\n渲染：用 Frame Debugger 检查实际绘制与 SetPass。\nGPU：在目标设备上核实 GPU 时间。", "hint"));
            return sidebar;
        }

        VisualElement BuildTimeline()
        {
            var panel = Box("panel", "timeline");
            var heading = Row("heading");
            heading.Add(Text("帧时间时间轴", "section-title"));
            var legend = Row();
            legend.Add(Text("● 帧耗时", "legend-frame")); legend.Add(Text("● 主线程", "legend-cpu")); legend.Add(Text("● GPU 异步", "legend-gpu"));
            heading.Add(legend); panel.Add(heading);
            graphCaption = Text("点击曲线选择观察点；虚线为目标帧预算。", "hint"); panel.Add(graphCaption);
            graph = new IMGUIContainer(DrawGraph); graph.AddToClassList("graph"); panel.Add(graph);
            var navigation = Row("navigation");
            followToggle = new Toggle("跟随最新") { value = true };
            followToggle.RegisterValueChangedCallback(evt => { followLatest = evt.newValue; Refresh(); });
            navigation.Add(followToggle);
            historySlider = new SliderInt(0, 0) { showInputField = true };
            historySlider.AddToClassList("history-slider");
            historySlider.RegisterValueChangedCallback(evt => { if (updatingSlider) return; historyStart = evt.newValue; followToggle.value = false; graph.MarkDirtyRepaint(); });
            navigation.Add(historySlider); panel.Add(navigation);
            selection = Text("选择观察点以查看帧号、CPU、GPU、GC 和渲染计数。", "selection"); panel.Add(selection);
            return panel;
        }

        public void StartRecording()
        {
            if (!Application.isPlaying)
            {
                ShowNotification(new GUIContent("请先在 Unity 顶部手动进入 Play 模式。"));
                return;
            }
            try
            {
                if (SceneScopeCapture.Active != null && SceneScopeCapture.Active.IsRecording)
                {
                    if (ownedCapture != null && ownedCapture != SceneScopeCapture.Active.gameObject)
                    {
                        DestroyImmediate(ownedCapture);
                        ownedCapture = null;
                    }
                    capture = SceneScopeCapture.Active;
                }
                else
                {
                    if (ownedCapture != null) DestroyImmediate(ownedCapture);
                    ownedCapture = new GameObject("SceneScope Runtime Capture") { hideFlags = HideFlags.DontSave };
                    ownedCapture.SetActive(false);
                    capture = ownedCapture.AddComponent<SceneScopeCapture>();
                    capture.autoStart = false;
                    ownedCapture.SetActive(true);
                    UnityEngine.Object.DontDestroyOnLoad(ownedCapture);
                    capture.StartCapture(settings.Copy());
                }
                selectedIndex = -1; selectedFrame = -1; followLatest = true;
                followToggle?.SetValueWithoutNotify(true);
                report = null;
                observingCapture = true;
                summary = null; findings = Array.Empty<PerformanceFinding>();
                RefreshAnalysis(); RefreshSupport(); Refresh();
                SetFooter("正在采集 · 请在游戏中复现卡顿，完成后点击「停止并分析」。");
            }
            catch (Exception exception) { Error("无法开始采集", exception); }
        }

        public void StopRecording()
        {
            try
            {
                if (capture != null) report = capture.StopCapture();
                if (ownedCapture != null) { DestroyImmediate(ownedCapture); ownedCapture = null; }
                capture = null;
                observingCapture = false;
                AnalyzeReport(); RefreshAnalysis(); RefreshSupport(); RefreshHitches(); Refresh();
                SetFooter("采集结束 · 已生成统计和排查建议，可导出报告保存现场。");
            }
            catch (Exception exception) { Error("停止采集失败", exception); }
        }

        public void LoadReport(CaptureReport value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (Recording) StopRecording();
            report = value;
            selectedIndex = -1; selectedFrame = -1; followLatest = true;
            followToggle?.SetValueWithoutNotify(true);
            AnalyzeReport(); RefreshAnalysis(); RefreshSupport(); RefreshHitches(); Refresh();
        }

        void AnalyzeSnapshot()
        {
            if (!Recording) return;
            report = capture.Snapshot();
            AnalyzeReport(); RefreshAnalysis(); RefreshSupport();
            SetFooter("快照分析已更新 · 采集仍继续；摘要仅代表点击时保留的数据。");
        }

        void AnalyzeReport()
        {
            if (report == null) { summary = null; findings = Array.Empty<PerformanceFinding>(); return; }
            summary = PerformanceAnalyzer.Summarize(report);
            findings = PerformanceAnalyzer.Analyze(report);
        }

        void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextRefresh) return;
            nextRefresh = EditorApplication.timeSinceStartup + .2;
            if (observingCapture && !Recording)
            {
                observingCapture = false;
                if (SceneScopeCapture.LastReport != null) report = SceneScopeCapture.LastReport;
                AnalyzeReport(); RefreshAnalysis(); RefreshSupport(); RefreshHitches(); Refresh();
            }
            if (!Recording) return;
            Refresh();
            if (EditorApplication.timeSinceStartup >= nextHitches)
            {
                nextHitches = EditorApplication.timeSinceStartup + 1;
                RefreshHitches();
            }
            if (Recording) Repaint();
        }

        void Refresh()
        {
            if (state == null) return;
            bool recording = Recording;
            int count = Count;
            startButton.SetEnabled(Application.isPlaying && !recording);
            stopButton.SetEnabled(recording); analyzeButton.SetEnabled(recording && count > 0);
            importButton.SetEnabled(!recording); settingsContainer.SetEnabled(!recording);
            foreach (var button in exportButtons) button.SetEnabled(count > 0);
            state.text = recording ? "● 正在采集  /  " + count + " 帧" : count > 0 ? "● 报告就绪  /  " + count + " 帧" : "○ 等待采集";
            state.EnableInClassList("live", recording);
            if (recording)
                context.text = "EDITOR PLAY MODE · 读数包含编辑器开销 · 预热 " + capture.Settings.warmupFrames + " 帧 · 环形容量 " + capture.Settings.maxSamples;
            else if (report != null)
                context.text = (report.isEditor ? "EDITOR" : "DEVICE") + "  ·  " + report.platform + "  ·  " + report.scene + "  ·  " + report.graphicsApi + "  ·  覆盖最早 " + report.droppedSamples + " 帧";
            int maxStart = Math.Max(0, count - GraphSamples);
            if (followLatest) historyStart = maxStart;
            historyStart = Math.Min(historyStart, maxStart);
            updatingSlider = true;
            historySlider.highValue = maxStart; historySlider.SetValueWithoutNotify(historyStart);
            historySlider.SetEnabled(maxStart > 0); updatingSlider = false;
            if (count > 0)
            {
                if (recording && selectedFrame >= 0)
                {
                    selectedIndex = FindFrame(selectedFrame);
                    if (selectedIndex < 0)
                    {
                        selectedFrame = -1;
                        SetFooter("所选观察帧已被环形缓冲区覆盖，详情已回到最新帧。");
                    }
                }
                else selectedIndex = Math.Min(selectedIndex, count - 1);
                var latest = Sample(count - 1);
                values[0].text = Ms(latest.frameMs);
                captions[0].text = (latest.frameMs > 0 ? (1000 / latest.frameMs).ToString("F1", CultureInfo.InvariantCulture) : "N/A") + " FPS · 目标 " + CurrentSettings.targetFps;
                values[1].text = Ms(latest.cpuMainMs);
                captions[1].text = "渲染 " + Ms(latest.cpuRenderMs) + " · 等待 " + Ms(latest.mainWaitMs);
                values[2].text = Ms(latest.gpuMs);
                captions[2].text = latest.gpuMs >= 0 ? "异步 GPU 样本 · 非同帧对应" : "当前观察点无新 GPU 样本";
                values[3].text = Bytes(latest.gcAllocatedBytes);
                captions[3].text = "回收 " + Number(latest.gcCollections) + " 次 · 堆 " + Bytes(latest.gcUsedBytes);
                values[4].text = Number(latest.drawCalls);
                captions[4].text = "Batches " + Number(latest.batches) + " · 实际计数器";
                values[5].text = Number(latest.setPassCalls);
                captions[5].text = "三角形 " + Number(latest.triangles) + " · 顶点 " + Number(latest.vertices);
                graphCaption.text = "保留 " + count + " 帧 · 视窗 " + (historyStart + 1) + "–" + Math.Min(count, historyStart + GraphSamples) + " · 点击曲线选择观察点";
                RefreshSelection();
            }
            else
            {
                for (int i = 0; i < values.Length; i++)
                {
                    values[i].text = "N/A";
                    captions[i].text = recording ? "预热中 / 等待有效计数器" : "等待采样数据";
                }
                graphCaption.text = "点击曲线选择观察点；虚线为目标帧预算。";
                selection.text = "选择观察点以查看帧号、CPU、GPU、GC 和渲染计数。";
            }
            graph.MarkDirtyRepaint();
        }

        void RefreshSelection()
        {
            if (selection == null || Count == 0) return;
            int index = selectedIndex < 0 ? Count - 1 : selectedIndex;
            var sample = Sample(index);
            selection.text = "观察帧 #" + sample.frame + "  ·  t " + sample.timeSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s  ·  帧 " + Ms(sample.frameMs) + "  ·  主线程 " + Ms(sample.cpuMainMs) + "  ·  渲染线程 " + Ms(sample.cpuRenderMs) + "\n等待 " + Ms(sample.mainWaitMs) + "  ·  GPU 异步 " + Ms(sample.gpuMs) + "  ·  GC " + Bytes(sample.gcAllocatedBytes) + " / 回收 " + Number(sample.gcCollections) + "  ·  Draw " + Number(sample.drawCalls) + " / Batch " + Number(sample.batches) + " / SetPass " + Number(sample.setPassCalls);
        }

        void RefreshAnalysis()
        {
            if (summaryText == null) return;
            summaryText.text = summary == null ? "停止采集后生成完整统计；也可手动分析当前快照。" : summary.frames == 0 ? "当前报告没有有效帧时间样本，FPS / 分位数 / 最慢帧均为 N/A。" :
                (Recording ? "当前快照 · " : "完整保留数据 · ") + summary.frames + " 帧  |  平均 " + summary.averageFps.ToString("F1", CultureInfo.InvariantCulture) + " FPS\nP50  " + Ms(summary.p50Ms) + "     P95  " + Ms(summary.p95Ms) + "     P99  " + Ms(summary.p99Ms) + "     最慢  " + Ms(summary.maxFrameMs) + "\n卡顿  " + summary.hitchFrames + " 帧     GC 分配合计  " + Bytes(summary.totalGcAllocatedBytes) + "     回收  " + Number(summary.gcCollections) + " 次     GPU P95  " + Ms(summary.gpuP95Ms);
            findingsContainer.Clear();
            findingsTitle.text = "排查建议" + (summary == null ? "" : "  /  " + findings.Length);
            if (summary == null) findingsContainer.Add(Text("停止采集或分析快照后，根据实际观测生成建议。判断瓶颈还需要 Profiler / Frame Debugger 进一步验证。", "hint"));
            else if (findings.Length == 0) findingsContainer.Add(Text("当前阈值下未发现明显异常；请在目标设备和典型负载下继续验证。", "hint"));
            foreach (var finding in findings)
            {
                var item = Box("finding");
                item.Add(Text(finding.category + " · " + finding.title, "finding-title"));
                item.Add(Text(finding.evidence, "hint"));
                item.Add(Text("下一步：" + finding.nextStep, "next-step"));
                if (finding.observationFrame >= 0)
                {
                    int frame = finding.observationFrame;
                    item.Add(Action("定位观察帧 #" + frame, () => SelectFrame(frame), "link"));
                }
                findingsContainer.Add(item);
            }
        }

        void RefreshHitches()
        {
            if (hitchesContainer == null) return;
            hitchIndices.Clear();
            // Live UI has a fixed scan budget; full report analysis is explicit, never a per-frame sort.
            int count = Count;
            int lowerBound = Recording ? Math.Max(0, count - 600) : 0;
            for (int i = count - 1; i >= lowerBound && hitchIndices.Count < 8; i--)
                if (PerformanceAnalyzer.IsHitch(Sample(i), CurrentSettings)) hitchIndices.Add(i);
            hitchesContainer.Clear();
            if (hitchIndices.Count == 0) hitchesContainer.Add(Text(count == 0 ? "等待采样数据。" : "检查范围内无超阈值观察点。", "hint"));
            foreach (int index in hitchIndices)
            {
                var sample = Sample(index);
                int frame = sample.frame;
                hitchesContainer.Add(Action("#" + frame + "  ·  " + Ms(sample.frameMs) + "  ·  GC " + Bytes(sample.gcAllocatedBytes), () => SelectFrame(frame), "hitch"));
            }
        }

        void RefreshSupport()
        {
            if (supportContainer == null) return;
            supportContainer.Clear();
            if (report == null) { supportContainer.Add(Text("停止或分析快照后显示计数器来源、支持状态与设备信息。", "hint")); return; }
            supportContainer.Add(Text("Unity " + report.unityVersion + " · CPU " + report.cpu + " · GPU " + report.gpu + "\nvSync " + report.vSyncCount + " · targetFrameRate " + report.applicationTargetFrameRate + " · Development Build " + report.isDevelopmentBuild, "hint"));
            foreach (var metric in report.metrics ?? Array.Empty<MetricSupport>())
                if (metric != null) supportContainer.Add(Text((metric.available ? "✓ " : "N/A  ") + metric.name + " · " + metric.source + " · " + metric.note, "support-row"));
        }

        void SelectFrame(int frame)
        {
            int index = FindFrame(frame);
            if (index >= 0)
            {
                selectedIndex = index; selectedFrame = frame;
                followLatest = false; followToggle.SetValueWithoutNotify(false);
                historyStart = Math.Max(0, index - GraphSamples / 2);
                Refresh(); return;
            }
            SetFooter("该观察帧已被环形缓冲区覆盖，请停止采集后复查保留数据。");
        }

        int FindFrame(int frame)
        {
            int left = 0, right = Count - 1;
            while (left <= right)
            {
                int middle = left + (right - left) / 2;
                int value = Sample(middle).frame;
                if (value == frame) return middle;
                if (value < frame) left = middle + 1; else right = middle - 1;
            }
            return -1;
        }

        void DrawGraph()
        {
            Rect area = GUILayoutUtility.GetRect(200, 195, GUILayout.ExpandWidth(true));
            Rect plot = new Rect(area.x + 42, area.y + 9, Mathf.Max(1, area.width - 54), area.height - 32);
            EditorGUI.DrawRect(area, new Color(.045f, .075f, .105f));
            int count = Count;
            int first = Math.Min(historyStart, Math.Max(0, count - 1));
            int last = Math.Min(count, first + GraphSamples);
            double budget = 1000.0 / Math.Max(1, CurrentSettings.targetFps);
            double maximum = budget * 2;
            for (int i = first; i < last; i++)
            {
                var sample = Sample(i);
                maximum = Math.Max(maximum, Math.Max(sample.frameMs, Math.Max(sample.cpuMainMs, sample.gpuMs)));
            }
            maximum = Math.Ceiling(maximum / 10) * 10;
            Handles.BeginGUI();
            for (int grid = 0; grid <= 4; grid++)
            {
                float y = plot.yMax - grid * plot.height / 4;
                Handles.color = new Color(.14f, .21f, .27f);
                Handles.DrawLine(new Vector3(plot.x, y), new Vector3(plot.xMax, y));
                GUI.Label(new Rect(area.x + 1, y - 9, 40, 18), (maximum * grid / 4).ToString("F0", CultureInfo.InvariantCulture), EditorStyles.miniLabel);
            }
            float budgetY = plot.yMax - (float)(budget / maximum) * plot.height;
            Handles.color = new Color(.55f, .65f, .70f, .8f);
            Handles.DrawDottedLine(new Vector3(plot.x, budgetY), new Vector3(plot.xMax, budgetY), 4);
            if (last - first > 1)
            {
                for (int i = first + 1; i < last; i++)
                {
                    var previous = Sample(i - 1); var current = Sample(i);
                    float x1 = plot.x + (i - first - 1) * plot.width / (last - first - 1);
                    float x2 = plot.x + (i - first) * plot.width / (last - first - 1);
                    Segment(plot, maximum, x1, x2, previous.frameMs, current.frameMs, FrameColor);
                    Segment(plot, maximum, x1, x2, previous.cpuMainMs, current.cpuMainMs, CpuColor);
                    // GPU samples are asynchronous observations: isolated marks avoid inventing frame-aligned values.
                    if (current.gpuMs >= 0)
                    {
                        float y = plot.yMax - (float)(current.gpuMs / maximum) * plot.height;
                        EditorGUI.DrawRect(new Rect(x2 - 1.5f, y - 1.5f, 3, 3), GpuColor);
                    }
                }
                int chosen = selectedIndex < 0 ? last - 1 : selectedIndex;
                if (chosen >= first && chosen < last)
                {
                    float x = plot.x + (chosen - first) * plot.width / (last - first - 1);
                    Handles.color = new Color(.85f, .91f, .96f, .7f);
                    Handles.DrawLine(new Vector3(x, plot.y), new Vector3(x, plot.yMax));
                }
                GUI.Label(new Rect(plot.x, plot.yMax + 4, 130, 18), "#" + Sample(first).frame, EditorStyles.miniLabel);
                GUI.Label(new Rect(plot.xMax - 100, plot.yMax + 4, 100, 18), "#" + Sample(last - 1).frame, EditorStyles.miniLabel);
                var evt = Event.current;
                if ((evt.type == EventType.MouseDown || evt.type == EventType.MouseDrag) && evt.button == 0 && plot.Contains(evt.mousePosition))
                {
                    selectedIndex = first + Mathf.RoundToInt(Mathf.Clamp01((evt.mousePosition.x - plot.x) / plot.width) * (last - first - 1));
                    selectedFrame = Sample(selectedIndex).frame;
                    followLatest = false; followToggle.SetValueWithoutNotify(false);
                    RefreshSelection(); evt.Use();
                }
            }
            else GUI.Label(new Rect(plot.x + 15, plot.y + plot.height / 2 - 10, plot.width - 30, 35), count == 0 ? "等待采集 · 时间轴将显示真实运行数据" : "等待更多采样…", EditorStyles.centeredGreyMiniLabel);
            Handles.EndGUI();
        }

        static void Segment(Rect plot, double maximum, float x1, float x2, double a, double b, Color color)
        {
            if (a < 0 || b < 0 || double.IsNaN(a) || double.IsNaN(b)) return;
            Handles.color = color;
            Handles.DrawLine(new Vector3(x1, plot.yMax - (float)(a / maximum) * plot.height), new Vector3(x2, plot.yMax - (float)(b / maximum) * plot.height));
        }

        void Import()
        {
            string path = EditorUtility.OpenFilePanel("导入 SceneScope 运行时报告", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            try { LoadReport(RuntimeReportExporter.ImportJson(path)); SetFooter("已导入：" + path); }
            catch (Exception exception) { Error("报告导入失败", exception); }
        }

        void Export(string format)
        {
            string extension = format.ToLowerInvariant();
            string path = EditorUtility.SaveFilePanel("导出运行时 " + format, "", "SceneScope-Runtime-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), extension);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var data = Recording ? capture.Snapshot() : report;
                if (data == null) return;
                if (format == "JSON") RuntimeReportExporter.ExportJson(data, path);
                else if (format == "CSV") RuntimeReportExporter.ExportCsv(data, path);
                else RuntimeReportExporter.ExportHtml(data, path);
                SetFooter("已导出 " + format + "：" + path);
            }
            catch (Exception exception) { Error("报告导出失败", exception); }
        }

        void OpenUnityTool(string menu)
        {
            if (!EditorApplication.ExecuteMenuItem(menu)) SetFooter("未找到 Unity 菜单：" + menu + "，请从 Window → Analysis 手动打开。");
        }
        void Error(string message, Exception exception) { SetFooter(message + "：" + exception.Message); Debug.LogWarning("SceneScope: " + message + "：" + exception.Message); }
        void SetFooter(string text) { if (footer != null) footer.text = text; }
        static string Ms(double value) => value < 0 || double.IsNaN(value) || double.IsInfinity(value) ? "N/A" : value.ToString("F2", CultureInfo.InvariantCulture) + " ms";
        static string Number(long value) => value < 0 ? "N/A" : value.ToString("N0", CultureInfo.InvariantCulture);
        static string Bytes(long value) => value < 0 ? "N/A" : value >= 1048576 ? (value / 1048576.0).ToString("F2", CultureInfo.InvariantCulture) + " MB" : value >= 1024 ? (value / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB" : value + " B";
        static Label Text(string value, params string[] classes) { var label = new Label(value); foreach (var style in classes) label.AddToClassList(style); return label; }
        static VisualElement Box(params string[] classes) { var box = new VisualElement(); foreach (var style in classes) box.AddToClassList(style); return box; }
        static VisualElement Row(params string[] classes) { var row = Box(classes); row.AddToClassList("row"); return row; }
        static VisualElement Divider() => Box("divider");
        static Button Action(string text, System.Action callback, params string[] classes) { var button = new Button(callback) { text = text }; foreach (var style in classes) button.AddToClassList(style); return button; }
        static IntegerField IntField(string label, int value, Action<int> changed) { var field = new IntegerField(label) { value = value }; field.RegisterValueChangedCallback(evt => changed(evt.newValue)); return field; }
    }
}
