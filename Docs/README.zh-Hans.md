# SpineGpuSkinning

[English](../README.md) | 简体中文 | [日本語](README.ja.md)

为 [spine-unity](http://esotericsoftware.com/spine-unity) 的 `SkeletonAnimation` 提供 GPU 蒙皮：把每帧 CPU 网格重建 / 顶点上传改到 GPU 顶点着色器，且不修改任何 Spine 源码。

> CPU 继续做骨骼求解（AnimationState、混合、事件、物理、BoneFollower、运行时换肤）。GPU 负责顶点蒙皮、网格组装和上传。兼容实例共享上传资源，绘制范围仍保留逐角色透明排序。

**给 Agent：** 先读仓库根目录 [`AGENTS.md`](../AGENTS.md)。技能在 [`Skills~/`](../Skills~/README.md)（Unity 会忽略 `~` 目录）。机器可读索引：[`llms.txt`](../llms.txt)。不要按旧 README 猜测 API，以 `AGENTS.md` 的当前合同为准。

## 环境要求

- Unity 2022.3+
- Universal Render Pipeline (URP) 14
- 工程内已安装 spine-unity **4.2**（spine-csharp 4.2）。本仓库**不附带** Spine 运行时，请自行安装并遵守 [Spine Runtimes License](http://esotericsoftware.com/spine-runtimes-license)。

## 快速开始

1. 把 `SpineGpuSkinning` 文件夹拷到工程 `Assets/` 下（例如 `Assets/Plugins/`）。
2. 选中带 `SkeletonAnimation` 的物体。
3. Add Component → `Gpu Skeleton Renderer`。

导入**不会**烘焙。没有容器时在 Inspector 点 `Bake Skeleton Data`，或用 `Tools/GPUSpineSkin/Bake All Skeleton Data` / `Rebake Selected`。容器（审计报告 + 每种皮肤组合一份原型网格 + 绘制顺序布局）作为该资产的子资源。组件会：

- 按当前皮肤组合和绘制顺序布局查找已烘焙入口（运行时从不烘焙；找不到则留在 CPU 路径）；
- 有入口则切到 GPU：`updateMode = EverythingExceptMesh`，并抑制 `MeshRenderer` 自动绘制；
- 每帧在 `UpdateComplete` 后导出 3x2 骨骼调色板、Deform、槽颜色和裁切范围；
- 按相机用 `Graphics.RenderMeshIndirect` 合批提交。

去掉或禁用组件即回到原 CPU 路径，无残留。审计失败时实例**留在 CPU 路径**，无需处理。

## 示例

`Example/` 含 **SampleDude**、**SampleRobot**、**ComplexCourier**、**UltraCourier**。打开 `Example/GpuSpineComparison.unity` 后进入 Play Mode。

- 显示实时 **FPS**、平均帧时间与最近一次 CPU/GPU 采样值。
- 同一批角色切换 CPU/GPU，保留位置和动画进度。
- 默认 32 人，UI 可调 0–1000；**Production 300** 是一键压力档。
- 只需 Unity、URP 和 spine-unity，不依赖宿主游戏框架。

通过 **Tools / GPUSpineSkin / Build Comparison Example** 重建。详见 `Example/README.md`。

示例用 `GpuSkeletonRenderer.CameraFilter` 只向 Game Camera 提交，避免 SceneView / 预览相机承担批次准备成本。插件本身仍支持多相机。

## 适用 Case 与性能边界

**GPU 蒙皮不保证比 CPU 快。** 少量、低顶点角色用 CPU 可能更省。两种模式的动画与骨骼求解仍在 CPU 上。

示例负载：

- **Simple duo**：各 20 骨、16 槽、159 可见顶点、3 套皮肤。功能对照，不是提速证明。
- **Complex courier**（默认）：48 骨、24 槽、每皮肤约 1170 烘焙顶点，含 Deform、24 点非凸裁切、附件 / 颜色 / DrawOrder 时间轴。
- **Ultra courier**：100 骨、26 槽、约 2490 烘焙顶点。压力案例，不是生产角色建议。

比较时固定案例、数量、皮肤分布、分辨率和镜头。FPS 不能直接证明 CPU 占用降低。正式结论优先 Player。

同机 Windows DX11 Player、1280×720、32 个 UltraCourier 的一次记录：GPU 约 84.5–92.7 FPS（10.79–11.83 ms），CPU 约 151.8 FPS（6.59 ms）。该数量尚未覆盖 GPU 准备与上传成本，不能外推到其他硬件。

## 编辑器菜单

通用工具在 **`Tools/GPUSpineSkin`**。`Bake All Skeleton Data` 不依赖选中项，会对工程内每个 `SkeletonDataAsset` 调用 Rebake（已是最新则跳过）。源指纹 **v2** 哈希依赖文件**内容**（加路径和长度），只改时间戳的 VCS checkout 不会重烘。网格被手删、或源文件没变也要重建时，用 Force Rebake。

宿主临时冒烟请用 `Tools/GPUSpineSkin/Temp/...`。

## 自动回退 CPU 的规则

| 检测到的特性 | 行为 |
|---|---|
| Deform 时间轴 | 支持：CPU 的 `slot.Deform` 有变化才上传，在骨骼加权前应用 |
| 槽颜色 (RGBA / RGB / Alpha) | 支持：每槽 `R/G/B/A` 上传并乘进顶点色 |
| Dark color (RGBA2 / RGB2) | CPU 回退（未实现 tint black） |
| Texture sequence | CPU 回退 |
| 没有烘焙入口 / `FormatVersion` 不兼容（当前为 5） | CPU 回退 |
| `zSpacing` 与烘焙值 0 不一致 | CPU 回退 |
| Attachment 时间轴 | 支持（动态槽）：变体预烘，未选中的在顶点着色器折叠 |
| Draw order 时间轴 | 支持：烘焙 `DrawOrderLayouts` 回放实时顺序。没有布局的旧容器回退 CPU。`AllowDrawOrderTimeline` 是遗留字段，不再作为门禁 |
| Clipping | 支持：GPU 片元裁切。`IgnoreClipping` 只跳过该实例的裁切（范围上传为零）。没有布局的旧容器回退 CPU |
| 顶点超过 4 根骨骼影响 | 截断并重归一化，仍走 GPU |

完整规则见 `Skills~/gpuspine-use-plugin/references/fallback-rules.md`。

## 自定义 Shader / RenderPass

顶点函数开头 include `Runtime/Shaders/SpineGpuSkinning.hlsl` 并调用 8 参数的 `GpuSpineSkinToWorld`。需要裁切时在片元函数调用 `GpuSpineClip`。

自定义 GPU shader 必须先挂在图集页材质上，再把使用**同一 shader** 的材质赋给 `MaterialOverride`。不同 shader 家族不会替换页材质 shader，会落到 `DefaultShader`（`GpuSpine/URP/Skeleton`）。

用 `GpuSkinningManager.GetBatches(camera)` 或 `GetBatches(camera, source)` 枚举绘制。`SubmeshIndex` 为 0，真实范围在 args 的 IndexStart/IndexCount。完整例子见 `Skills~/gpuspine-use-plugin/references/integration-examples.md`。

## 运行时切换与实例数据

- `IncludeInRuntimeSwitch`（默认关）把实例登记进 `GpuSpineRuntimeSwitch`，供宿主切换 CPU/GPU。
- `CopyPropertyBlockToCustomData` 在 `WriteInstanceData` 之前把 MeshRenderer MPB 抄到 Custom0/Custom1。

## 诊断开关

`Runtime/GpuSpineDiagnostics.cs` 两个编译期常量，默认都是 `false`。`EnableLogging` 只管已接入的日志；`EnableAutomaticValidation` 是宿主自动冒烟总门禁。正常 GPU 蒙皮不受验证门禁影响。

## Agent skills

`Skills~/` 提供 `gpuspine-use-plugin`（接入）和 `gpuspine-develop-plugin`（改插件）。拷到宿主 `.agents/skills/` 或 `.cursor/skills/` 即可被多数 Agent 自动加载。本仓库请先读 `AGENTS.md`。

## 许可

MIT（见 `LICENSE`）。Spine 运行时仍受 Spine Runtimes License 约束，不属于本仓库。
