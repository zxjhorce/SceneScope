# SceneScope

[English](PROJECT.en.md) · **简体中文**

SceneScope 是面向 Unity 的场景诊断与运行时性能排查工具。它把静态规则检查、对象定位、可撤销修改、帧时间采集和离线报告连接起来，适合项目开发期间排查问题，也可用于展示 Unity 编辑器工具与性能分析能力。

当前包版本 **1.1.0**；项目使用 **Unity 2022.3.62f3**。编辑器界面和诊断信息目前以中文为主；本仓库提供中英文说明文档。

## 主要能力

| 模块 | 功能 |
| --- | --- |
| 静态场景诊断 | 16 条规则，检查脚本引用、网格、材质、纹理、阴影、层级与物理配置；增量扫描、筛选、对象聚焦、Scene 热力图及基线比较 |
| 有限修复 | 对小物体阴影进行修改预览、批量关闭投影，支持 Undo 和 Prefab 实例属性记录 |
| 运行时观测 | 帧间隔、CPU 主线程/渲染线程/等待、可用时的 GPU 时间、GC 分配与回收次数、内存、实际 Draw Call/Batches/SetPass/三角形/顶点 |
| 卡顿分析 | 有界帧历史、卡顿观察点、P50/P95/P99、阈值提示及 Profiler / Frame Debugger 快捷入口 |
| 报告与自动化 | 静态 JSON/HTML；运行时 JSON/CSV/HTML、设备报告回导；Build Settings 场景批处理检查 |

## 打开完整工程

```sh
git clone https://github.com/zxjhorce/SceneScope.git
```

1. 当前仓库为私有仓库，克隆需要账号具有访问权限。
2. 在 Unity Hub 中添加克隆得到的 `SceneScope` 文件夹，使用 Unity 2022.3.62f3 打开。首次启动会重新生成 Library。
3. 打开 `Assets/SceneScopeDemo/Generated 2/SceneScope_Demo.unity`，这是仓库保留的一份完整演示场景。
4. 选择 **Tools → SceneScope → 打开诊断面板**，点击 **扫描已加载场景**。
5. 运行时排查选择 **Tools → SceneScope → 运行时性能排查**；进入 Play Mode 后点击 **开始采集**，复现问题，再点击 **停止并分析**。

Windows 下也可运行 `./Open-SceneScope.ps1 -UnityPath '编辑器 Unity.exe 的完整路径'`。自动查找逻辑只是本机便利入口，不是安装 Unity 的步骤。

## 接入现有游戏项目

复制 `Packages/com.scenescope.diagnostics` 整个目录到目标项目的 `Packages` 下，作为 embedded UPM package 使用。项目最低声明版本为 Unity 2022.3；其他版本和平台应自行验证。工具不依赖第三方插件。

在开发包中，需要显式添加 `SceneScope.Runtime.SceneScopeCapture` 组件，并使用 **Development Build**。复现后停止并导出 JSON，将报告带回编辑器分析。需要 GPU 时间时，还应检查 Frame Timing Stats 及目标平台支持情况。正常 Release 构建中采集入口不会启动采样。

详细步骤与可复制的导出代码见 [运行时排查指南](Documentation/运行时排查指南.md)。

## 文档

| 内容 | 中文 | English |
| --- | --- | --- |
| 安装、静态规则与 CI | [使用指南](Documentation/使用指南.md) | [User guide](Documentation/User-Guide.en.md) |
| CPU/GPU/GC/Draw Call 排查 | [运行时排查指南](Documentation/运行时排查指南.md) | [Runtime guide](Documentation/Runtime-Guide.en.md) |
| 模块设计与技术取舍 | [技术设计](Documentation/技术设计.md) | [Architecture](Documentation/Architecture.en.md) |

另有 [中文演示讲解](Documentation/演示讲解.md)，用于组织作品集演示。

## 验证与适用边界

2026-10-23 在 Unity 2022.3.62f3 上完成静态集成检查 **34/34**。演示诊断从 14 条变为 6 条，8 个阴影修改可以一次 Undo 撤销；这不是帧率提升数据。

运行时模块另有 **30 项检查通过**：16 项分析/报告/窗口构造/构建检查、13 项 Windows Direct3D11 Development Player 检查，以及 1 项 Release 停用检查。离屏测试场景捕获了约 56 ms 卡顿、单帧约 4 MiB 托管分配、2 次回收和峰值 65 次实际 Draw Call。该次测试 GPU 有效样本为 0，仅验证了 N/A 处理；没有证明 GPU 时间采集或移动平台性能已经通过实测。

- 静态评分是规则摘要，不能转换成 FPS；静态 Renderer 数不是 Draw Call。
- CPU 主线程时间包含等待。GPU 异步返回，不保证与同一行 CPU 观测对应。
- GC 分配字节数及回收次数不是 GC 暂停耗时。
- 不可用指标显示 N/A；分析提供线索，函数调用栈和逐绘制定位仍需 Profiler、Frame Debugger 或平台工具。
- Editor、Development Build 和工具本身都有开销。真实游戏结论需在目标设备、固定测试条件下验证。

验证源码随工程提供；`artifacts/` 内的日志、报告和可执行文件是本地产物，不提交到仓库。验证入口和执行条件见两份使用指南。

## 仓库内容

```text
Assets/SceneScopeDemo/Generated 2/  一份可直接打开的演示场景及资源
Assets/SceneScopeValidation/       运行时验证源码
Packages/com.scenescope.diagnostics/  可复用工具包源码与 .meta
Packages/manifest.json             工程依赖
Packages/packages-lock.json        依赖锁文件
ProjectSettings/                  Unity 工程设置
Documentation/                    中英文说明
Open-SceneScope.ps1                Windows 启动辅助脚本
```

Library、Temp、Logs、UserSettings、构建产物、重复演示资源及本地工具设置均已忽略。仓库入口使用 `PROJECT.zh-CN.md` 和 `PROJECT.en.md`，不提交本地 README 文件。
