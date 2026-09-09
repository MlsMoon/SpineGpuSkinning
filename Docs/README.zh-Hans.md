# SpineGpuSkinning

[English](../README.md) | 简体中文 | [日本語](README.ja.md)

为 [spine-unity](http://esotericsoftware.com/spine-unity) 的 SkeletonAnimation 提供 GPU 蒙皮：把每帧 CPU 蒙皮 / 网格重建 / 顶点上传改到 GPU 顶点着色器，且不修改任何 Spine 源码。

> CPU 继续做骨骼求解（AnimationState、混合、事件、物理、BoneFollower、运行时换肤）。GPU 负责顶点蒙皮、网格组装和上传。共享同一图集页的实例走一次 `DrawMeshInstancedIndirect`。

## 环境要求

- Unity 2022.3+
- Universal Render Pipeline (URP) 14
- 工程内已安装 spine-unity **4.2**（spine-csharp 4.2）。本仓库**不附带** Spine 运行时，请自行安装并遵守 [Spine Runtimes License](http://esotericsoftware.com/spine-runtimes-license)。

## 快速开始

1. 把 `SpineGpuSkinning` 文件夹拷到工程 `Assets/` 下（例如 `Assets/Plugins/`）。
2. 选中带 `SkeletonAnimation` 的物体。
3. Add Component → `Gpu Skeleton Renderer`。

导入时编辑器会自动审计并烘焙每个 `SkeletonDataAsset`；容器（审计报告 + 每种皮肤组合一份原型网格）作为该资产的子资源。组件会：

- 按当前皮肤组合查找已烘焙入口（运行时从不烘焙；找不到则警告并留在 CPU 路径）；
- 有入口则切到 GPU：`updateMode = EverythingExceptMesh`，并关闭 `MeshRenderer`；
- 每帧在 `UpdateComplete` 后导出 3x2 骨骼调色板；
- 按图集页合批提交。

去掉或禁用组件即回到原 CPU 路径，无残留。

审计失败时实例**静默留在 CPU 路径**，无需处理。

## 示例

`Example/` 内含原创冒险家 **SampleDude** 和小机器人 **SampleRobot**，各有 3 套 Spine 皮肤。
打开 `Example/GpuSpineComparison.unity` 后进入 Play Mode。

- 显示实时 **FPS**、平均帧时间与最近一次 CPU/GPU 采样值。
- 同一批角色切换 CPU/GPU 蒙皮，保留位置和动画进度。
- 默认 32 人，UI 可调整 0–1000 人，并选择混合皮肤或指定配色。
- 提供 SVG 源稿、Spine 4.2 导出、Prefab 与 GPU 烘焙数据。
- 只需 Unity、URP 和 spine-unity，不依赖躺瓶业务框架。

通过 **Tools / GPUSpineSkin / Build Comparison Example** 重建。详见 `Example/README.md`。

## 适用 Case 与性能边界

**GPU 蒙皮不保证比 CPU 快。少量、低顶点角色使用 CPU 可能更省。**
两种模式的动画状态、骨骼与约束求解仍在 CPU 上；GPU 模式另外需要上传骨骼、Deform、槽颜色，
并维护可见性、排序、缓冲和绘制批次。必须让省掉的 CPU 网格计算与上传成本超过这些新增开销。

| Case | 建议 |
| --- | --- |
| 少量角色，主要是 Region 附件，网格很轻 | 优先 CPU；用简单示例核对效果与开销。 |
| 大量可见角色，共享骨架、皮肤和图集材质 | 值得评估 GPU；检查实际合批和网格计算收益。 |
| 高密度加权网格，披风、配饰等有 Deform | 更接近 GPU 的目标负载；但 Deform 求值和上传仍有成本。 |
| 频繁换肤、附件/绘制顺序变化、多材质、透明交错遮挡 | 批次会被拆分，不能用角色数量推断 DrawCall。 |
| 裁切、阴影、自定义多 Pass | 有额外开销与接入要求，先确认 CPU/GPU 画面一致。 |
| 瓶颈在动画求解、AI、填充率或其他系统 | 仅切换蒙皮可能没有整体帧时间收益。 |

示例提供两类真实负载：

- **Simple duo**：小人＋机器人，各 20 根骨骼、16 个槽、159 个可见顶点、3 套皮肤。
  用于基本功能和开销对照，不是提速证明。
- **Complex courier**（默认）：48 根骨骼、24 个槽、每皮肤约 1170 个烘焙顶点；
  披风、围巾、背带、发束采用多骨骼加权与 Deform；护目镜有 24 点非凸裁切；
  行走循环同时包含附件切换、槽颜色、DrawOrder 和 3 套皮肤。

复杂案例覆盖生产角色常见的技术特征，但不等价于某个游戏的完整灯光、阴影、描边、相机和玩法负载。
资产中有多少皮肤或动画不直接决定逐帧开销，应看当前实际执行的动画、网格、裁切与绘制工作。

比较时固定 **案例、数量、皮肤分布、分辨率和镜头**，预热后重复观察 FPS 与帧时间，并用 Profiler
核对 CPU 网格热点、渲染线程、GPU 时间、GC 和实际绘制批次。FPS 不能直接证明 CPU 占用降低。
Editor Play Mode 可交互对照；正式性能结论应优先基于 Player，避免编辑器开销干扰。

## Windows Player 对照结果

在相同 Player、1280×720、相同正交相机、相同 UltraCourier 案例和 32 个角色下，实测一次：

- GPU：约 84.5–92.7 FPS，10.79–11.83 ms/frame。
- CPU：约 151.8 FPS，6.59 ms/frame。

这说明当前 32 个复杂实例还不足以覆盖 GPU 批次准备、骨骼/Deform 数据上传和间接绘制的额外成本。
GPU 蒙皮已经省掉了 Spine CPU Mesh 重建，但总帧时间仍可能更高。该结果来自 Windows DX11 Player，
不包含 Unity Editor 的 SceneView、Inspector 和编辑器循环影响；它只代表本机、本分辨率、本案例和本数量。

测试方式：先等待场景稳定，再记录 GPU；点击同一位置切换 CPU，等待稳定后记录 CPU；不改变镜头、数量、
皮肤和窗口大小。正式性能决策仍需在目标硬件上重复多组数量（例如 32、100、300、1000），并结合
Profiler 的 CPU、Render Thread、GPU 时间、GC 与实际 DrawCall 判断。
## 编辑器菜单

通用工具在 **`Tools/GPUSpineSkin`** 下，作用于选中的 `SkeletonDataAsset`：

| 菜单 | 作用 |
|---|---|
| `Tools/GPUSpineSkin/Rebake Selected` | 指纹或入口键变化时重烘 |
| `Tools/GPUSpineSkin/Force Rebake Selected` | 先清指纹再强制重烘 |
| `Tools/GPUSpineSkin/Log Audit Report` | 只打审计日志，不烘焙 |
| `Tools/GPUSpineSkin/Dump Baked Data` | 打印容器、入口和声明组合 |
| `Tools/GPUSpineSkin/Inspect Default Shader` | 检查 `GpuSpine/URP/Skeleton` 编译状态 |
| `Tools/GPUSpineSkin/Build Comparison Example` | 重建双角色导入物与人群对照场景 |

选中 `SkeletonDataAsset` 时，Project 右键也有 `Assets/GpuSpine/` 下的 Rebake / Audit。宿主工程的临时冒烟请用 `Tools/GPUSpineSkin/Temp/...`。

## 自动回退 CPU 的规则

| 检测到的特性 | 行为 |
|---|---|
| Deform 时间轴 | 支持：CPU 算出的 `slot.Deform` 每帧进 deform 缓冲，在骨骼加权前应用 |
| 槽颜色时间轴 (RGBA / RGB / Alpha) | 支持：每槽 `slot.R/G/B/A` 每帧上传并乘进顶点色 |
| Dark color (RGBA2 / RGB2) | CPU 回退（未实现 tint black） |
| Texture sequence | CPU 回退 |
| 当前皮肤组合没有烘焙入口 | CPU 回退并警告 |
| `zSpacing` 与烘焙值 0 不一致 | CPU 回退并警告 |
| Attachment 时间轴 | 支持（动态槽）：变体预烘，未选中的在顶点着色器折叠 |
| Draw order 时间轴 | 默认 CPU 回退；组件开启 `AllowDrawOrderTimeline` 才走 GPU |
| Clipping | 默认 CPU 回退；开启 `IgnoreClipping` 才走 GPU（不裁切） |
| 顶点超过 4 根骨骼影响 | 截断并重归一化，警告后仍走 GPU |

## 自定义 Shader / RenderPass

在顶点函数开头 include `Runtime/Shaders/SpineGpuSkinning.hlsl` 并调用蒙皮函数。完整例子见 `Skills~/gpuspine-use-plugin/references/integration-examples.md`。

用批次 API 枚举存活批次，再提交到自己的 RT。同上文档。

## Agent skills

仓库在 `Skills~/` 提供：

- `gpuspine-use-plugin` — 接入、回退、排错
- `gpuspine-develop-plugin` — 架构、烘焙语义、缓冲布局

拷到工程的 agent skill 目录（例如 `.agents/skills/`）即可。

## 许可

MIT（见 `LICENSE`）。Spine 运行时仍受 Spine Runtimes License 约束，不属于本仓库。
