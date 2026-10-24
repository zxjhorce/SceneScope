using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace SceneScope.Editor
{
    public sealed class SceneScopeWindow : EditorWindow
    {
        private const string StylePath = "Packages/com.scenescope.diagnostics/Editor/UI/SceneScope.uss";
        [SerializeField] private ScanSettings settings = new ScanSettings();
        [SerializeField] private ScanResult result;
        [SerializeField] private ScanResult baseline;
        [SerializeField] private bool stale;
        private readonly List<DiagnosticIssue> visibleIssues = new List<DiagnosticIssue>();
        private readonly List<VisualElement> editControls = new List<VisualElement>();
        private ListView issueList;
        private VisualElement details, metrics, scanNotice;
        private Label issueCount, scanState, footer, baselineLabel, scoreValue, scoreCaption;
        private Button scanButton, cancelButton, baselineButton, jsonButton, htmlButton, fixButton;
        private ProgressBar progress;
        private Toggle heatmap;
        private DiagnosticIssue selectedIssue;
        private string query = "";
        private int severityFilter, categoryFilter;
        private float scanProgress;

        [MenuItem("Tools/SceneScope/打开诊断面板", priority = 10)]
        public static void Open()
        {
            var window = GetWindow<SceneScopeWindow>();
            window.titleContent = new GUIContent("SceneScope", EditorGUIUtility.IconContent("Profiler.Rendering").image);
            window.minSize = new Vector2(1080, 700);
            window.Show();
        }

        private string PrefKey
        {
            get
            {
                unchecked
                {
                    uint hash = 2166136261;
                    foreach (var character in Application.dataPath) hash = (hash ^ character) * 16777619;
                    return "SceneScope." + hash.ToString("x8");
                }
            }
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("SceneScope");
            minSize = new Vector2(1080, 700);
            try
            {
                if (EditorPrefs.HasKey(PrefKey + ".settings"))
                    settings = JsonUtility.FromJson<ScanSettings>(EditorPrefs.GetString(PrefKey + ".settings")) ?? new ScanSettings();
                if (EditorPrefs.HasKey(PrefKey + ".baseline"))
                    baseline = JsonUtility.FromJson<ScanResult>(EditorPrefs.GetString(PrefKey + ".baseline"));
            }
            catch (Exception exception) { Debug.LogWarning("SceneScope: 无法恢复本地设置：" + exception.Message); }
            EditorApplication.hierarchyChanged += MarkStale;
            EditorApplication.projectChanged += MarkStale;
            Undo.undoRedoPerformed += MarkStale;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            SceneScanner.Failed += OnScanFailed;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkStale;
            EditorApplication.projectChanged -= MarkStale;
            Undo.undoRedoPerformed -= MarkStale;
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            SceneScanner.Failed -= OnScanFailed;
            SceneScanner.Cancel();
            SceneHeatmap.Enabled(false);
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            editControls.Clear();
            SceneHeatmap.Enabled(false);
            root.AddToClassList("scenescope");
            var stylesheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (stylesheet != null && !root.styleSheets.Contains(stylesheet)) root.styleSheets.Add(stylesheet);

            var masthead = Element("masthead");
            var brand = Element("brand");
            brand.Add(Text("S", "brand-mark"));
            brand.Add(Text("SCENESCOPE", "brand-name"));
            brand.Add(Text("场景性能工作台", "brand-subtitle"));
            masthead.Add(brand);
            masthead.Add(Text("EDITOR TOOLKIT  /  01", "edition"));
            root.Add(masthead);

            var body = Element("body");
            body.Add(BuildSidebar());
            body.Add(BuildWorkspace());
            root.Add(body);
            footer = Text("准备就绪 · 扫描已加载场景，检查渲染、纹理、物理与层级预算。", "footer");
            root.Add(footer);
            RefreshResult();
        }

        private VisualElement BuildSidebar()
        {
            var sidebar = Element("sidebar");
            sidebar.Add(Text("WORKSPACE", "eyebrow"));
            sidebar.Add(Text("诊断控制台", "sidebar-title"));
            sidebar.Add(Text("从发现问题，到验证优化。", "muted"));
            sidebar.Add(ActionButton("运行时卡顿排查 →", PerformanceWindow.Open, "secondary"));
            scanButton = ActionButton("扫描已加载场景", StartScan, "primary", "scan-button");
            sidebar.Add(scanButton);
            cancelButton = ActionButton("取消扫描", CancelScan, "secondary");
            sidebar.Add(cancelButton);
            progress = new ProgressBar { lowValue = 0, highValue = 100, title = "准备扫描" };
            progress.AddToClassList("scan-progress");
            sidebar.Add(progress);
            scanState = Text("等待首次扫描", "scan-state");
            sidebar.Add(scanState);
            var demoButton = ActionButton("生成演示场景", () =>
            {
                try { SceneScopeDemo.CreateDemoScene(); }
                catch (Exception exception) { ShowError("生成演示场景失败", exception); }
            }, "secondary", "demo-button");
            editControls.Add(demoButton);
            sidebar.Add(demoButton);

            sidebar.Add(Separator());
            sidebar.Add(Text("VISUALIZATION", "eyebrow"));
            heatmap = new Toggle("Scene 热力图");
            heatmap.AddToClassList("heatmap-toggle");
            heatmap.RegisterValueChangedCallback(evt =>
            {
                SceneHeatmap.SetResult(result);
                SceneHeatmap.Enabled(evt.newValue);
            });
            sidebar.Add(heatmap);
            sidebar.Add(Text("按实例网格三角形预算着色，\n在 Scene 视图中定位复杂对象。", "hint"));

            sidebar.Add(Separator());
            sidebar.Add(Text("BUDGET PROFILE", "eyebrow"));
            var budgetHeader = Element("budget-header");
            budgetHeader.Add(Text("预算阈值", "section-title"));
            var reset = ActionButton("重置", () =>
            {
                settings = new ScanSettings();
                SaveSettings();
                MarkStale();
                CreateGUI();
            }, "text-button");
            editControls.Add(reset);
            budgetHeader.Add(reset);
            sidebar.Add(budgetHeader);
            var budgetScroll = new ScrollView(ScrollViewMode.Vertical);
            budgetScroll.AddToClassList("budget-scroll");
            var inactive = new Toggle("包含未激活对象") { value = settings.includeInactive };
            inactive.AddToClassList("budget-toggle");
            inactive.RegisterValueChangedCallback(evt => { settings.includeInactive = evt.newValue; SettingsChanged(); });
            editControls.Add(inactive);
            budgetScroll.Add(inactive);
            AddBudget(budgetScroll, "单网格三角形", settings.maxTrianglesPerMesh, value => settings.maxTrianglesPerMesh = value);
            AddBudget(budgetScroll, "场景三角形", settings.maxSceneTriangles, value => settings.maxSceneTriangles = value);
            AddBudget(budgetScroll, "Renderer 数量", settings.maxSceneRenderers, value => settings.maxSceneRenderers = value);
            AddBudget(budgetScroll, "纹理最大边长", settings.maxTextureSize, value => settings.maxTextureSize = value);
            AddBudget(budgetScroll, "单物体材质槽", settings.maxMaterialSlots, value => settings.maxMaterialSlots = value);
            AddBudget(budgetScroll, "层级深度", settings.maxHierarchyDepth, value => settings.maxHierarchyDepth = value);
            AddBudget(budgetScroll, "实时阴影灯光", settings.maxRealtimeShadowLights, value => settings.maxRealtimeShadowLights = value, 0);
            var size = new FloatField("小物体 / Unity 单位") { value = settings.smallObjectSize, isDelayed = true };
            size.AddToClassList("budget-field");
            size.RegisterValueChangedCallback(evt =>
            {
                settings.smallObjectSize = float.IsNaN(evt.newValue) || float.IsInfinity(evt.newValue) ? 0.6f : Mathf.Clamp(evt.newValue, 0.01f, 1000f);
                size.SetValueWithoutNotify(settings.smallObjectSize);
                SettingsChanged();
            });
            budgetScroll.Add(size);
            editControls.Add(size);
            sidebar.Add(budgetScroll);

            var baselineBox = Element("baseline-box");
            baselineBox.Add(Text("OPTIMIZATION BASELINE", "eyebrow"));
            baselineLabel = Text("固定一次扫描，量化优化前后变化。", "hint");
            baselineBox.Add(baselineLabel);
            baselineButton = ActionButton("固定为基线", PinBaseline, "secondary");
            baselineBox.Add(baselineButton);
            sidebar.Add(baselineBox);
            return sidebar;
        }

        private VisualElement BuildWorkspace()
        {
            var workspace = Element("workspace");
            var heading = Element("workspace-heading");
            var title = Element("heading-copy");
            title.Add(Text("场景健康概览", "page-title"));
            title.Add(Text("面向优化决策的静态分析与可追溯报告", "subtitle"));
            heading.Add(title);
            var export = Element("export-actions");
            export.Add(Text("导出报告", "muted"));
            jsonButton = ActionButton("JSON", () => Export(false), "secondary", "compact");
            htmlButton = ActionButton("HTML", () => Export(true), "secondary", "compact");
            export.Add(jsonButton);
            export.Add(htmlButton);
            heading.Add(export);
            workspace.Add(heading);

            metrics = Element("metrics");
            workspace.Add(metrics);
            scanNotice = Element("scan-notice");
            workspace.Add(scanNotice);

            var resultsHeading = Element("results-heading");
            resultsHeading.Add(Text("诊断结果", "section-title"));
            issueCount = Text("0 项", "count-chip");
            resultsHeading.Add(issueCount);
            var spacer = Element("spacer");
            resultsHeading.Add(spacer);
            fixButton = ActionButton("批量预览可修复项", () => PreviewFix(visibleIssues), "secondary", "compact");
            resultsHeading.Add(fixButton);
            workspace.Add(resultsHeading);

            var filters = Element("filters");
            var search = new ToolbarSearchField { value = query };
            search.tooltip = "搜索规则、对象路径、描述或建议";
            search.AddToClassList("search");
            search.RegisterValueChangedCallback(evt => { query = evt.newValue; RefreshIssues(); });
            filters.Add(search);
            var severity = new PopupField<string>(new List<string> { "全部严重度", "高优先级", "待优化", "提示" }, severityFilter);
            severity.AddToClassList("filter-popup");
            severity.RegisterValueChangedCallback(evt => { severityFilter = severity.index; RefreshIssues(); });
            filters.Add(severity);
            var category = new PopupField<string>(new List<string> { "全部分类", "渲染", "纹理", "物理", "结构" }, categoryFilter);
            category.AddToClassList("filter-popup");
            category.RegisterValueChangedCallback(evt => { categoryFilter = category.index; RefreshIssues(); });
            filters.Add(category);
            workspace.Add(filters);

            var resultPanels = Element("result-panels");
            var listPanel = Element("list-panel");
            var columns = Element("list-columns");
            columns.Add(Text("优先级 / 规则", "muted"));
            columns.Add(Text("对象与资源路径", "muted"));
            listPanel.Add(columns);
            issueList = new ListView(visibleIssues, 84, MakeIssueRow, BindIssueRow)
            {
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight
            };
            issueList.AddToClassList("issue-list");
            issueList.selectionChanged += objects =>
            {
                selectedIssue = objects.FirstOrDefault() as DiagnosticIssue;
                RefreshDetails();
            };
            listPanel.Add(issueList);
            resultPanels.Add(listPanel);
            details = new ScrollView(ScrollViewMode.Vertical);
            details.AddToClassList("detail-panel");
            resultPanels.Add(details);
            workspace.Add(resultPanels);
            workspace.Add(Text("诊断边界：分数是规则权重；三角形按 Renderer 实例累计；纹理内存为编辑器估算。实际帧耗时请配合 Profiler 验证。", "methodology"));
            return workspace;
        }

        private void AddBudget(VisualElement parent, string label, int value, Action<int> setter, int minimum = 1)
        {
            var field = new IntegerField(label) { value = value, isDelayed = true };
            field.AddToClassList("budget-field");
            field.RegisterValueChangedCallback(evt =>
            {
                var next = Math.Max(minimum, evt.newValue);
                field.SetValueWithoutNotify(next);
                setter(next);
                SettingsChanged();
            });
            editControls.Add(field);
            parent.Add(field);
        }

        private void SettingsChanged() { SaveSettings(); MarkStale(); }
        private void SaveSettings() => EditorPrefs.SetString(PrefKey + ".settings", JsonUtility.ToJson(settings));

        private void StartScan()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || SceneScanner.IsScanning) return;
            scanProgress = 0;
            scanState.text = "正在逐项分析场景…";
            try
            {
                SceneScanner.Start(settings.Copy(), value =>
                {
                    if (this == null) return;
                    scanProgress = Mathf.Clamp01(value);
                    UpdateControls();
                }, scanned =>
                {
                    if (this == null) return;
                    if (scanned == null) { scanState.text = "扫描已取消，保留上次结果。"; UpdateControls(); return; }
                    result = scanned;
                    stale = false;
                    selectedIssue = null;
                    SceneHeatmap.SetResult(result);
                    RefreshResult();
                    scanState.text = "扫描完成 · " + result.elapsedMilliseconds.ToString("N0") + " ms";
                    footer.text = "已分析 " + result.metrics.gameObjects.ToString("N0") + " 个对象 · " + (result.scenePaths?.Length ?? 0) + " 个已加载场景 · " + LocalTime(result.scannedAtUtc);
                });
            }
            catch (Exception exception) { ShowError("扫描失败", exception); }
            UpdateControls();
        }

        private void CancelScan()
        {
            SceneScanner.Cancel();
            scanState.text = "扫描已取消，保留上次结果。";
            UpdateControls();
        }

        private void OnInspectorUpdate()
        {
            if (scanButton != null) UpdateControls();
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            MarkStale();
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                SceneScanner.Cancel();
                SceneHeatmap.Enabled(false);
                heatmap?.SetValueWithoutNotify(false);
            }
            UpdateControls();
        }

        private void OnObjectChanges(ref ObjectChangeEventStream stream)
        {
            if (stream.length > 0) MarkStale();
        }

        private void MarkStale()
        {
            if (SceneScanner.IsScanning)
            {
                SceneScanner.Cancel();
                if (scanState != null) scanState.text = "场景或资源发生变化，扫描已取消，请重试。";
            }
            if (result == null || stale) return;
            stale = true;
            SceneHeatmap.Enabled(false);
            heatmap?.SetValueWithoutNotify(false);
            if (scanNotice != null) { RefreshNotice(); RefreshDetails(); UpdateControls(); }
        }

        private void OnScanFailed(Exception exception)
        {
            if (scanState != null) scanState.text = "扫描失败：" + exception.Message;
            UpdateControls();
        }

        private void UpdateControls()
        {
            if (scanButton == null) return;
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            bool scanning = SceneScanner.IsScanning;
            bool editable = !playing && !scanning;
            scanButton.SetEnabled(editable);
            scanButton.text = scanning ? "正在扫描…" : "扫描已加载场景";
            cancelButton.style.display = scanning ? DisplayStyle.Flex : DisplayStyle.None;
            progress.style.display = scanning ? DisplayStyle.Flex : DisplayStyle.None;
            progress.value = scanProgress * 100;
            progress.title = "分析进度 " + Mathf.RoundToInt(scanProgress * 100) + "%";
            foreach (var element in editControls) element.SetEnabled(editable);
            baselineButton.SetEnabled(editable && result != null && !stale);
            jsonButton.SetEnabled(editable && result != null && !stale);
            htmlButton.SetEnabled(editable && result != null && !stale);
            heatmap.SetEnabled(editable && result != null && !stale);
            fixButton.SetEnabled(editable && result != null && !stale && visibleIssues.Any(x => x.fix == FixKind.DisableShadowCasting));
            var selectedFix = details?.Q<Button>("selected-fix");
            selectedFix?.SetEnabled(editable && !stale);
            if (playing) scanState.text = "请退出 Play 模式后继续诊断。";
        }

        private void RefreshResult()
        {
            if (metrics == null) return;
            metrics.Clear();
            var score = Element("score-card");
            score.Add(Text("预算健康度", "metric-label"));
            scoreValue = Text(result == null ? "—" : result.Score.ToString(), "score-value");
            score.Add(scoreValue);
            scoreCaption = Text(result == null ? "等待扫描" : "RULE SCORE / 100", "score-caption");
            score.Add(scoreCaption);
            metrics.Add(score);
            if (result == null)
            {
                AddMetric("Renderer", "—", "场景渲染实例", null);
                AddMetric("三角形", "—", "实例累计", null);
                AddMetric("纹理内存", "—", "编辑器估算", null);
                AddMetric("独立材质", "—", "去重资源", null);
            }
            else
            {
                bool compare = BaselineMatches();
                AddMetric("Renderer", result.metrics.renderers.ToString("N0"), "预算 " + result.settings.maxSceneRenderers.ToString("N0"), compare ? (long?)(result.metrics.renderers - baseline.metrics.renderers) : null);
                AddMetric("三角形", Compact(result.metrics.triangles), "预算 " + Compact(result.settings.maxSceneTriangles), compare ? (long?)(result.metrics.triangles - baseline.metrics.triangles) : null);
                AddMetric("纹理内存", Memory(result.metrics.textureMemoryBytes), result.metrics.textures + " 个独立纹理", compare ? (long?)(result.metrics.textureMemoryBytes - baseline.metrics.textureMemoryBytes) : null, true);
                AddMetric("独立材质", result.metrics.materials.ToString("N0"), result.metrics.shadowCasters + " 个阴影投射者", compare ? (long?)(result.metrics.materials - baseline.metrics.materials) : null);
            }
            baselineLabel.text = baseline == null ? "固定一次扫描，量化优化前后变化。" : "已固定 " + LocalTime(baseline.scannedAtUtc) + (result != null && !BaselineMatches() ? "\n场景、阈值或范围不同，暂不比较。" : "\n指标下方显示相对基线的变化。");
            RefreshNotice();
            RefreshIssues();
            UpdateControls();
        }

        private void AddMetric(string title, string value, string caption, long? delta, bool memory = false)
        {
            var card = Element("metric-card");
            card.Add(Text(title, "metric-label"));
            card.Add(Text(value, "metric-value"));
            if (delta.HasValue)
            {
                var difference = delta.Value;
                var label = difference == 0 ? "较基线持平" : (difference > 0 ? "+" : "−") + (memory ? Memory(Math.Abs(difference)) : Compact(Math.Abs(difference))) + " 较基线";
                card.Add(Text(label, "metric-delta", difference < 0 ? "improved" : difference > 0 ? "regressed" : "muted"));
            }
            else card.Add(Text(caption, "metric-caption"));
            metrics.Add(card);
        }

        private void RefreshNotice()
        {
            scanNotice.Clear();
            scanNotice.EnableInClassList("notice-stale", stale);
            if (result == null)
            {
                scanNotice.Add(Text("开始一次扫描", "notice-title"));
                scanNotice.Add(Text("打开已有场景，或生成内置演示场景。扫描只读取数据；修复会先展示变更预览。", "notice-copy"));
            }
            else if (stale)
            {
                scanNotice.Add(Text("结果需要更新", "notice-title"));
                scanNotice.Add(Text("场景、资源、预算或撤销状态已变化。重新扫描后可修复、导出与固定基线。", "notice-copy"));
            }
            else
            {
                scanNotice.Add(Text(result.Count(IssueSeverity.Error) + " 高优先级  /  " + result.Count(IssueSeverity.Warning) + " 待优化  /  " + result.Count(IssueSeverity.Info) + " 提示", "notice-title"));
                scanNotice.Add(Text("扫描 " + result.metrics.gameObjects.ToString("N0") + " 个对象 · " + result.elapsedMilliseconds.ToString("N0") + " ms · 点击结果查看证据与处理建议", "notice-copy"));
            }
        }

        private void RefreshIssues()
        {
            if (issueList == null) return;
            visibleIssues.Clear();
            if (result != null)
            {
                foreach (var issue in result.issues.OrderByDescending(x => x.severity).ThenBy(x => x.category).ThenBy(x => x.title))
                {
                    if (severityFilter > 0 && (int)issue.severity != 3 - severityFilter) continue;
                    if (categoryFilter > 0 && (int)issue.category != categoryFilter - 1) continue;
                    string searchable = issue.title + " " + issue.ruleId + " " + issue.objectPath + " " + issue.assetPath + " " + issue.description + " " + issue.recommendation;
                    if (!string.IsNullOrWhiteSpace(query) && searchable.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    visibleIssues.Add(issue);
                }
            }
            issueCount.text = visibleIssues.Count + (result == null ? " 项" : " / " + result.issues.Count + " 项");
            issueList.Rebuild();
            if (selectedIssue != null && visibleIssues.Contains(selectedIssue)) issueList.SetSelection(visibleIssues.IndexOf(selectedIssue));
            else
            {
                selectedIssue = null;
                issueList.ClearSelection();
                if (visibleIssues.Count > 0) issueList.SetSelection(0);
            }
            RefreshDetails();
            UpdateControls();
        }

        private VisualElement MakeIssueRow()
        {
            var row = Element("issue-row");
            var severity = Text("", "issue-severity");
            severity.name = "severity";
            row.Add(severity);
            var copy = Element("issue-copy");
            var title = Text("", "issue-title"); title.name = "title";
            var path = Text("", "issue-path"); path.name = "path";
            var code = Text("", "issue-code"); code.name = "code";
            copy.Add(title); copy.Add(path); copy.Add(code);
            row.Add(copy);
            return row;
        }

        private void BindIssueRow(VisualElement row, int index)
        {
            var issue = visibleIssues[index];
            row.EnableInClassList("severity-error", issue.severity == IssueSeverity.Error);
            row.EnableInClassList("severity-warning", issue.severity == IssueSeverity.Warning);
            row.EnableInClassList("severity-info", issue.severity == IssueSeverity.Info);
            row.Q<Label>("severity").text = SeverityName(issue.severity);
            row.Q<Label>("title").text = issue.title;
            row.Q<Label>("path").text = string.IsNullOrEmpty(issue.objectPath) ? (string.IsNullOrEmpty(issue.assetPath) ? "已加载场景" : issue.assetPath) : issue.objectPath;
            row.Q<Label>("code").text = CategoryName(issue.category) + "  /  " + issue.ruleId + (issue.fix != FixKind.None ? "  ·  支持修复" : "");
            row.tooltip = issue.description;
        }

        private void RefreshDetails()
        {
            if (details == null) return;
            details.Clear();
            details.Add(Text("INSPECTOR", "eyebrow"));
            if (selectedIssue == null)
            {
                details.Add(Text(result == null ? "每一个问题，都有依据。" : visibleIssues.Count == 0 && result.issues.Count > 0 ? "没有匹配的诊断项" : "当前规则未发现问题", "detail-title"));
                details.Add(Text(result == null ? "扫描后，在左侧选择一条结果，查看触发原因、对象位置和处理建议。" : result.issues.Count == 0 ? "场景在当前预算内。静态检查无法替代真机 Profiler，请继续验证帧时间和内存峰值。" : "调整搜索词或分类，查看其他结果。", "detail-copy"));
                return;
            }
            var issue = selectedIssue;
            details.Add(Text(CategoryName(issue.category) + " / " + issue.ruleId, "detail-rule"));
            details.Add(Text(issue.title, "detail-title"));
            details.Add(Text(SeverityName(issue.severity), "detail-badge", "badge-" + issue.severity.ToString().ToLowerInvariant()));
            details.Add(Separator());
            details.Add(Text("触发依据", "detail-heading"));
            details.Add(Text(issue.description, "detail-copy"));
            details.Add(Text("优化建议", "detail-heading"));
            details.Add(Text(issue.recommendation, "detail-copy"));
            var path = string.IsNullOrEmpty(issue.assetPath) ? issue.objectPath : issue.assetPath;
            if (!string.IsNullOrEmpty(path))
            {
                details.Add(Text("对象 / 资源", "detail-heading"));
                var pathField = new TextField { value = path, isReadOnly = true, multiline = true };
                pathField.AddToClassList("path-field");
                details.Add(pathField);
            }
            var actions = Element("detail-actions");
            var locate = ActionButton("定位对象", () => Locate(issue, false), "secondary");
            var frame = ActionButton("在 Scene 中聚焦", () => Locate(issue, true), "secondary");
            var target = issue.ResolveTarget();
            locate.SetEnabled(target != null);
            frame.SetEnabled(target is Component || target is GameObject);
            actions.Add(locate); actions.Add(frame);
            details.Add(actions);
            if (issue.fix == FixKind.DisableShadowCasting)
            {
                details.Add(Separator());
                details.Add(Text("可撤销修复", "detail-heading"));
                details.Add(Text("仅关闭此对象的阴影投射。操作前展示目标清单，可使用 Unity Undo 撤销。", "detail-copy"));
                var fix = ActionButton("预览并修复", () => PreviewFix(new[] { issue }), "primary");
                fix.name = "selected-fix";
                fix.SetEnabled(!stale && !SceneScanner.IsScanning && !EditorApplication.isPlayingOrWillChangePlaymode);
                details.Add(fix);
            }
        }

        private void PreviewFix(IEnumerable<DiagnosticIssue> candidates)
        {
            if (stale || SceneScanner.IsScanning || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var fixes = candidates.Where(x => x.fix == FixKind.DisableShadowCasting).ToList();
            if (fixes.Count == 0) return;
            try
            {
                var preview = SceneFixer.Preview(fixes);
                if (!EditorUtility.DisplayDialog("SceneScope · 修复预览", preview + "\n\n修复会改变场景的阴影外观，可通过 Edit > Undo 撤销。是否应用？", "应用修复", "取消")) return;
                int changed = SceneFixer.Apply(fixes);
                MarkStale();
                footer.text = "已修复 " + changed + " 个对象，正在重新扫描以验证变化。";
                EditorApplication.delayCall += () => { if (this != null) StartScan(); };
            }
            catch (Exception exception) { ShowError("修复失败", exception); }
        }

        private static void Locate(DiagnosticIssue issue, bool frame)
        {
            var target = issue.ResolveTarget();
            if (target == null) { EditorUtility.DisplayDialog("对象已不存在", "该对象可能已被删除或卸载，请重新扫描。", "确定"); return; }
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
            if (!frame) return;
            var view = SceneView.lastActiveSceneView ?? GetWindow<SceneView>();
            view.Show();
            view.FrameSelected();
            view.Repaint();
        }

        private void PinBaseline()
        {
            if (result == null || stale) return;
            baseline = JsonUtility.FromJson<ScanResult>(JsonUtility.ToJson(result));
            EditorPrefs.SetString(PrefKey + ".baseline", JsonUtility.ToJson(baseline));
            RefreshResult();
            footer.text = "已固定当前扫描为优化基线；后续扫描将显示数量与内存变化。";
        }

        private bool BaselineMatches()
        {
            if (baseline?.metrics == null || result == null) return false;
            return JsonUtility.ToJson(baseline.settings) == JsonUtility.ToJson(result.settings)
                && (baseline.scenePaths ?? Array.Empty<string>()).OrderBy(x => x).SequenceEqual((result.scenePaths ?? Array.Empty<string>()).OrderBy(x => x));
        }

        private void Export(bool html)
        {
            if (result == null || stale) return;
            string extension = html ? "html" : "json";
            string path = EditorUtility.SaveFilePanel("导出 SceneScope 报告", "", "SceneScope-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), extension);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (html) ReportExporter.ExportHtml(result, path); else ReportExporter.ExportJson(result, path);
                footer.text = "报告已导出：" + path;
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception exception) { ShowError("导出报告失败", exception); }
        }

        private static void ShowError(string title, Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("SceneScope · " + title, exception.Message, "确定");
        }

        private static string SeverityName(IssueSeverity severity) => severity == IssueSeverity.Error ? "高优先级" : severity == IssueSeverity.Warning ? "待优化" : "提示";
        private static string CategoryName(IssueCategory category) => category == IssueCategory.Rendering ? "渲染" : category == IssueCategory.Texture ? "纹理" : category == IssueCategory.Physics ? "物理" : "结构";
        private static string Compact(long value) => value >= 1000000 ? (value / 1000000d).ToString("0.##") + " M" : value >= 10000 ? (value / 1000d).ToString("0.#") + " K" : value.ToString("N0");
        private static string Memory(long bytes) => bytes >= 1073741824 ? (bytes / 1073741824d).ToString("0.##") + " GB" : (bytes / 1048576d).ToString("0.#") + " MB";
        private static string LocalTime(string utc) => DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time) ? time.ToLocalTime().ToString("MM-dd HH:mm:ss") : "时间未知";
        private static VisualElement Element(params string[] classes) { var element = new VisualElement(); foreach (string name in classes) element.AddToClassList(name); return element; }
        private static Label Text(string value, params string[] classes) { var label = new Label(value); foreach (string name in classes) label.AddToClassList(name); return label; }
        private static VisualElement Separator() => Element("separator");
        private static Button ActionButton(string title, Action action, params string[] classes) { var button = new Button(action) { text = title }; foreach (string name in classes) button.AddToClassList(name); return button; }
    }
}
