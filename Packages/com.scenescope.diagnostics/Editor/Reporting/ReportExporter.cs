using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SceneScope.Editor
{
    /// <summary>Self-contained reports; no object access or scene mutations during export.</summary>
    public static class ReportExporter
    {
        public static void ExportJson(ScanResult result, string path)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            Write(path, JsonUtility.ToJson(result, true));
        }

        public static void ExportHtml(ScanResult result, string path)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var issues = result.issues ?? new List<DiagnosticIssue>();
            var errors = Count(issues, IssueSeverity.Error);
            var warnings = Count(issues, IssueSeverity.Warning);
            var info = Count(issues, IssueSeverity.Info);
            var score = Math.Max(0, 100 - errors * 12 - warnings * 3 - info);
            var html = new StringBuilder(32768);
            html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
                .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">")
                .Append("<title>SceneScope · 场景诊断报告</title><style>")
                .Append(Styles)
                .Append("</style></head><body><main>")
                .Append("<header><a class=\"brand\" href=\"#top\"><span class=\"brand-mark\">S</span>SCENESCOPE</a><span class=\"eyebrow\">UNITY EDITOR / SCENE DIAGNOSTICS</span></header>")
                .Append("<section class=\"hero\" id=\"top\"><div><p class=\"eyebrow accent\">SCENE AUDIT REPORT</p><h1>让场景问题，<br>变得可见。</h1><p class=\"subtitle\">从资源、渲染、物理与层级结构中提取可定位的证据，为性能优化建立检查起点。</p><div class=\"meta\">扫描时间（UTC） <strong>")
                .Append(H(FormatDate(result.scannedAtUtc)))
                .Append("</strong><br>SceneScope ").Append(H(result.toolVersion))
                .Append(" · 扫描耗时 ").Append(H(result.elapsedMilliseconds.ToString("N1", CultureInfo.InvariantCulture)))
                .Append(" ms</div></div><aside class=\"score-card\"><span class=\"eyebrow\">HEURISTIC HEALTH</span><div class=\"score\">")
                .Append(score.ToString(CultureInfo.InvariantCulture))
                .Append("<small>/ 100</small></div><strong>启发式检查评分</strong><p>100 − 错误 × 12 − 警告 × 3 − 提示，最低为 0。仅用于同一规则与阈值下的检查比较。</p></aside></section>")
                .Append("<div class=\"notice\"><strong>数据边界</strong><span>本报告为静态场景检查，不采集 CPU/GPU 帧耗时，不预测 FPS，也不等同于 Draw Call 或实际显存。优化结果需结合目标设备上的 Profiler 验证。</span></div>")
                .Append("<section><div class=\"section-heading\"><div><p class=\"eyebrow\">01 / OVERVIEW</p><h2>场景概览</h2></div><span class=\"muted\">扫描范围内的快照</span></div><div class=\"metrics\">");
            if (result.metrics != null)
            {
                var m = result.metrics;
                Metric(html, "GameObject", N(m.gameObjects), "按当前扫描范围计数");
                Metric(html, "Renderer", N(m.renderers), "包含禁用组件，不等同于 Draw Call");
                Metric(html, "网格三角形", N(m.triangles), "累计全部 LOD 引用，不是单帧提交量");
                Metric(html, "材质", N(m.materials), "去重后的共享材质");
                Metric(html, "纹理", N(m.textures), "引用纹理去重统计");
                Metric(html, "纹理内存估计", MiB(m.textureMemoryBytes), "编辑器加载纹理的估计，不等同于显存");
                Metric(html, "实时阴影灯光", N(m.realtimeShadowLights), "按场景组件状态统计");
                Metric(html, "投射阴影的 Renderer", N(m.shadowCasters), "阴影参与对象数量");
                Metric(html, "缺失脚本", N(m.missingScripts), "当前范围内未解析脚本");
            }
            else html.Append("<p class=\"muted\">该报告不包含场景指标。</p>");
            html.Append("</div><div class=\"risk-summary\">");
            Risk(html, "error", "错误", errors);
            Risk(html, "warning", "警告", warnings);
            Risk(html, "info", "提示", info);
            html.Append("</div></section><section><div class=\"section-heading\"><div><p class=\"eyebrow\">02 / EVIDENCE</p><h2>问题与优化建议</h2></div><span class=\"muted\">优先核实错误，再评估视觉与性能取舍</span></div><nav class=\"categories\" aria-label=\"问题分类\">");
            foreach (IssueCategory category in Enum.GetValues(typeof(IssueCategory)))
            {
                html.Append("<a href=\"#category-").Append(((int)category).ToString(CultureInfo.InvariantCulture)).Append("\">")
                    .Append(H(CategoryName(category))).Append(" <span>").Append(CountCategory(issues, category).ToString(CultureInfo.InvariantCulture)).Append("</span></a>");
            }
            html.Append("</nav>");
            foreach (IssueCategory category in Enum.GetValues(typeof(IssueCategory)))
                Category(html, issues, category, false);
            // Preserve unexpected categories from future model versions instead of silently omitting evidence.
            Category(html, issues, default(IssueCategory), true);
            html.Append("</section><section><div class=\"section-heading\"><div><p class=\"eyebrow\">03 / CONTEXT</p><h2>复查条件</h2></div></div><div class=\"context-grid\"><div class=\"panel\"><h3>场景路径</h3><ul class=\"paths\">");
            if (result.scenePaths != null && result.scenePaths.Length > 0)
                foreach (var scene in result.scenePaths) html.Append("<li><code>").Append(H(string.IsNullOrEmpty(scene) ? "未保存场景" : scene)).Append("</code></li>");
            else html.Append("<li>未记录场景路径</li>");
            html.Append("</ul><p class=\"muted\">重新扫描前请保持相同的场景、扫描范围及阈值。报告不会自动刷新。</p></div><div class=\"panel\"><h3>本次阈值</h3><table><tbody>");
            if (result.settings != null)
            {
                var s = result.settings;
                Setting(html, "包含未激活对象", s.includeInactive ? "是" : "否");
                Setting(html, "单网格三角形上限", N(s.maxTrianglesPerMesh));
                Setting(html, "纹理最大边长", N(s.maxTextureSize) + " px");
                Setting(html, "单 Renderer 材质槽上限", N(s.maxMaterialSlots));
                Setting(html, "层级深度上限", N(s.maxHierarchyDepth));
                Setting(html, "实时阴影灯光上限", N(s.maxRealtimeShadowLights));
                Setting(html, "场景 Renderer 上限", N(s.maxSceneRenderers));
                Setting(html, "场景三角形上限", N(s.maxSceneTriangles));
                Setting(html, "小物体尺寸阈值", s.smallObjectSize.ToString("0.###", CultureInfo.InvariantCulture) + " Unity 单位");
            }
            else Setting(html, "阈值", "未记录");
            html.Append("</tbody></table></div></div></section><footer><span>SCENESCOPE / 工程诊断 · 可解释 · 可复查</span><span>独立离线报告 · 可使用浏览器打印为 PDF</span></footer></main></body></html>");
            Write(path, html.ToString());
        }

        private static void Category(StringBuilder html, List<DiagnosticIssue> issues, IssueCategory category, bool unknown)
        {
            var selected = issues.FindAll(issue => issue != null && (unknown
                ? !Enum.IsDefined(typeof(IssueCategory), issue.category)
                : issue.category == category));
            if (unknown && selected.Count == 0) return;
            selected.Sort((left, right) => right.severity.CompareTo(left.severity));
            html.Append("<div class=\"category\" id=\"category-").Append(unknown ? "other" : ((int)category).ToString(CultureInfo.InvariantCulture))
                .Append("\"><h3>").Append(H(unknown ? "其他" : CategoryName(category))).Append("<span>").Append(selected.Count.ToString(CultureInfo.InvariantCulture)).Append(" 项</span></h3>");
            if (selected.Count == 0) html.Append("<p class=\"empty\">当前规则与阈值下未发现此类问题。</p>");
            foreach (var issue in selected)
            {
                var severity = issue.severity == IssueSeverity.Error ? "error" : issue.severity == IssueSeverity.Warning ? "warning" : "info";
                html.Append("<article class=\"issue ").Append(severity).Append("\"><div class=\"issue-heading\"><span class=\"badge ").Append(severity).Append("\">")
                    .Append(H(SeverityName(issue.severity))).Append("</span><h4>").Append(H(issue.title)).Append("</h4><code class=\"rule\">").Append(H(issue.ruleId)).Append("</code></div>");
                if (!string.IsNullOrEmpty(issue.objectPath))
                    html.Append("<p class=\"location\"><span>对象</span><code>").Append(H(issue.objectPath)).Append("</code></p>");
                if (!string.IsNullOrEmpty(issue.assetPath))
                    html.Append("<p class=\"location\"><span>资源</span><code>").Append(H(issue.assetPath)).Append("</code></p>");
                html.Append("<div class=\"evidence\"><strong>检查证据</strong><p>").Append(H(issue.description)).Append("</p></div>")
                    .Append("<div class=\"advice\"><strong>优化建议</strong><p>").Append(H(issue.recommendation)).Append("</p></div>");
                if (issue.fix == FixKind.DisableShadowCasting)
                    html.Append("<p class=\"fix-note\">编辑器支持预览后关闭投影，可通过 Undo 撤销。此操作会改变阴影表现，请先评估视觉效果。</p>");
                if (!string.IsNullOrEmpty(issue.globalObjectId))
                    html.Append("<details><summary>对象标识</summary><code>").Append(H(issue.globalObjectId)).Append("</code></details>");
                html.Append("</article>");
            }
            html.Append("</div>");
        }

        private static void Metric(StringBuilder html, string label, string value, string detail) => html
            .Append("<div class=\"metric\"><span>").Append(H(label)).Append("</span><strong>").Append(H(value)).Append("</strong><small>").Append(H(detail)).Append("</small></div>");

        private static void Risk(StringBuilder html, string style, string label, int count) => html
            .Append("<div class=\"risk ").Append(style).Append("\"><span class=\"dot\"></span><span>").Append(H(label)).Append("</span><strong>").Append(count.ToString(CultureInfo.InvariantCulture)).Append("</strong></div>");

        private static void Setting(StringBuilder html, string label, string value) => html
            .Append("<tr><th scope=\"row\">").Append(H(label)).Append("</th><td>").Append(H(value)).Append("</td></tr>");

        private static int Count(List<DiagnosticIssue> issues, IssueSeverity severity) => issues.FindAll(issue => issue != null && issue.severity == severity).Count;
        private static int CountCategory(List<DiagnosticIssue> issues, IssueCategory category) => issues.FindAll(issue => issue != null && issue.category == category).Count;
        private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
        private static string MiB(long bytes) => (bytes / (1024d * 1024d)).ToString("N2", CultureInfo.InvariantCulture) + " MiB";
        private static string H(string value) => (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

        private static string FormatDate(string date)
        {
            return DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
                : date ?? "未记录";
        }

        private static string CategoryName(IssueCategory category)
        {
            switch (category)
            {
                case IssueCategory.Rendering: return "渲染";
                case IssueCategory.Texture: return "纹理";
                case IssueCategory.Physics: return "物理";
                case IssueCategory.Structure: return "场景结构";
                default: return category.ToString();
            }
        }

        private static string SeverityName(IssueSeverity severity)
        {
            switch (severity)
            {
                case IssueSeverity.Error: return "错误";
                case IssueSeverity.Warning: return "警告";
                case IssueSeverity.Info: return "提示";
                default: return severity.ToString();
            }
        }

        private static void Write(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择有效的报告文件路径。", nameof(path));
            var absolutePath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(absolutePath, content, new UTF8Encoding(false));
        }

        private const string Styles = @"
:root{color-scheme:dark;--bg:#0c1519;--panel:#132126;--line:#283b41;--ink:#e7f0ef;--muted:#94aaa9;--accent:#70e1c9;--error:#ff9898;--warning:#f3ca80;--info:#89bddb}
*{box-sizing:border-box}html{scroll-behavior:smooth}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.65 -apple-system,BlinkMacSystemFont,'Segoe UI','Microsoft YaHei',sans-serif}main{max-width:1180px;padding:0 44px;margin:auto}a{color:inherit}header{display:flex;justify-content:space-between;align-items:center;gap:20px;padding:30px 0;border-bottom:1px solid var(--line)}.brand{display:flex;gap:12px;align-items:center;letter-spacing:3px;font-size:15px;font-weight:700;text-decoration:none}.brand-mark{display:grid;place-items:center;background:var(--accent);color:var(--bg);width:32px;height:32px;border-radius:8px;letter-spacing:0}.eyebrow{font:11px/1.5 'Segoe UI',sans-serif;letter-spacing:2px;color:var(--muted)}.accent{color:var(--accent)}.hero{display:grid;grid-template-columns:1fr 280px;gap:80px;align-items:center;padding:55px 0 40px}h1{font-size:48px;line-height:1.28;letter-spacing:-1px;margin:16px 0 22px}h2{font-size:26px;margin:0;line-height:1.5}h3{font-size:17px}h4{font-size:16px;margin:0}.subtitle{font-size:16px;max-width:650px;color:#b5c8c6}.meta{font-size:12px;line-height:2.1;color:var(--muted);margin-top:25px}.meta strong{font-weight:500;color:var(--ink)}.score-card{background:linear-gradient(150deg,#173934,#132126);padding:26px;text-align:center;border:1px solid #2f5e54;border-radius:18px}.score{font:700 76px/1.1 'Segoe UI',sans-serif;color:var(--accent);margin:22px 0 20px}.score small{font-size:18px;color:var(--muted);font-weight:400}.score-card p{font-size:12px;color:var(--muted);margin:12px 0 0}.notice{display:flex;gap:20px;background:#142a29;border-left:3px solid var(--accent);padding:17px 20px;border-radius:0 8px 8px 0;color:#b4d2ca;font-size:13px}.notice strong{white-space:nowrap;color:var(--accent)}section{margin-bottom:44px}.section-heading{display:flex;align-items:end;justify-content:space-between;gap:24px;margin:32px 0 20px}.section-heading .eyebrow{margin:0 0 7px}.muted{color:var(--muted);font-size:12px}.metrics{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}.metric{display:flex;flex-direction:column;padding:20px 22px;border:1px solid var(--line);background:var(--panel);border-radius:10px}.metric>span{color:#baccca;font-size:13px}.metric strong{font:600 30px/1.5 'Segoe UI',sans-serif;letter-spacing:-.4px;margin:6px 0}.metric small{font-size:11px;color:var(--muted)}.risk-summary{display:grid;grid-template-columns:repeat(3,1fr);gap:12px;margin-top:16px}.risk{display:flex;align-items:center;gap:10px;border:1px solid var(--line);padding:12px 18px;border-radius:8px}.risk strong{margin-left:auto;font-size:20px}.error{--status:var(--error)}.warning{--status:var(--warning)}.info{--status:var(--info)}.dot{width:7px;height:7px;border-radius:50%;background:var(--status)}.categories{display:flex;gap:10px;flex-wrap:wrap;margin-bottom:25px}.categories a{text-decoration:none;border:1px solid var(--line);background:var(--panel);padding:8px 16px;border-radius:7px;font-size:13px}.categories a:hover{border-color:var(--accent)}.categories span{margin-left:10px;color:var(--accent)}.category{scroll-margin-top:20px;margin:26px 0}.category>h3{display:flex;align-items:center;gap:14px;margin:0 0 14px}.category>h3>span{font-size:11px;font-weight:400;color:var(--muted)}.issue{border:1px solid var(--line);border-left:3px solid var(--status);background:var(--panel);padding:20px 24px;border-radius:8px;margin-bottom:12px}.issue-heading{display:flex;align-items:center;gap:12px;flex-wrap:wrap}.badge{font-size:11px;white-space:nowrap;color:var(--status);border:1px solid currentColor;border-radius:4px;padding:1px 7px}.rule{font-size:11px;color:var(--muted);margin-left:auto}.location{display:flex;gap:14px;font-size:12px;color:var(--muted);margin:12px 0}.location>span{flex-shrink:0}.location code{color:#b7ceca}.evidence,.advice{display:grid;grid-template-columns:70px 1fr;gap:14px;margin-top:15px;font-size:13px}.evidence strong,.advice strong{font-size:12px;font-weight:500;color:var(--muted)}.evidence p,.advice p{margin:0;white-space:pre-wrap}.advice{padding-top:13px;border-top:1px solid var(--line)}.advice strong{color:var(--accent)}.fix-note{font-size:12px;color:var(--warning);margin:14px 0 0}.empty{border:1px dashed var(--line);border-radius:8px;padding:18px 22px;color:var(--muted);font-size:13px}code{font-family:Consolas,'SFMono-Regular',monospace;overflow-wrap:anywhere;white-space:pre-wrap}details{font-size:11px;margin-top:14px;color:var(--muted)}summary{cursor:pointer}details code{display:block;margin-top:10px}.context-grid{display:grid;grid-template-columns:1fr 1.15fr;gap:16px}.panel{border:1px solid var(--line);border-radius:10px;background:var(--panel);padding:22px}.panel h3{margin:0 0 14px}.paths{list-style:none;margin:0;padding:0}.paths li{padding:9px 0;border-bottom:1px solid var(--line);font-size:12px}table{width:100%;border-collapse:collapse;font-size:12px}th,td{padding:8px 0;border-bottom:1px solid var(--line);vertical-align:top}th{text-align:left;font-weight:400;color:var(--muted)}td{text-align:right;padding-left:16px}footer{border-top:1px solid var(--line);padding:24px 0 36px;display:flex;justify-content:space-between;gap:15px;color:var(--muted);font-size:10px;letter-spacing:1px}
@media(max-width:760px){main{padding:0 20px}header>.eyebrow{display:none}.hero{grid-template-columns:1fr;gap:26px;padding-top:32px}h1{font-size:38px}.score-card{max-width:100%}.metrics{grid-template-columns:repeat(2,1fr)}.metric{padding:16px}.metric strong{font-size:25px}.context-grid{grid-template-columns:1fr}.section-heading{align-items:start;flex-direction:column;gap:8px}.notice{flex-direction:column;gap:5px}.issue{padding:17px}.evidence,.advice{grid-template-columns:1fr;gap:5px}.rule{margin-left:0}footer{flex-direction:column}.risk{padding:10px;gap:6px}.risk strong{font-size:17px}}
@media print{:root{color-scheme:light;--bg:#fff;--panel:#fff;--line:#ccd4d4;--ink:#15282b;--muted:#526868;--accent:#16715f;--error:#a72c2c;--warning:#8c5b0b;--info:#285f83}body{font-size:10pt}main{max-width:none;padding:0}.hero{padding:20px 0;gap:25px;grid-template-columns:1fr 220px}h1{font-size:30px}header{padding:0 0 15px}.score-card{background:#f1f7f5;border-color:#8ea9a2}.score{font-size:58px}.subtitle,.notice,.meta strong,.location code,.metric>span{color:var(--ink)}.notice{background:#f3f8f6}.metric strong{font-size:24px}.metrics{gap:8px}.metric{padding:12px}.categories{display:none}.issue,.metric,.panel{break-inside:avoid}.issue{background:white}.section-heading{margin-top:20px}section{margin-bottom:24px}.context-grid{grid-template-columns:1fr 1fr}a{text-decoration:none}footer{padding-bottom:0}details{display:none}}
";
    }
}
