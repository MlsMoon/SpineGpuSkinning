# SpineGpuSkinning

English | [简体中文](Docs/README.zh-Hans.md) | [日本語](Docs/README.ja.md)

GPU skinning for [spine-unity](http://esotericsoftware.com/spine-unity) SkeletonAnimation — moves the per-frame CPU skinning / mesh rebuild / vertex upload path to the GPU vertex shader, without modifying any Spine source code.

> CPU keeps bone evaluation (AnimationState, mixing, events, physics, BoneFollower, runtime skinning). The GPU takes over vertex skinning, mesh assembly and vertex upload. Compatible instances share upload resources; draw ranges retain per-character transparent ordering.

## Requirements

- Unity 2022.3+
- Universal Render Pipeline (URP) 14
- spine-unity **4.2** (spine-csharp 4.2) installed in the project. Spine runtimes are **not** bundled; you must install spine-unity yourself and comply with the [Spine Runtimes License](http://esotericsoftware.com/spine-runtimes-license).

## Quick Start

1. Copy the `SpineGpuSkinning` folder anywhere under your project's `Assets/` (e.g. `Assets/Plugins/`).
2. Select the GameObject that has your `SkeletonAnimation`.
3. Add component → `Gpu Skeleton Renderer`.

That is all. On import, every `SkeletonDataAsset` is audited and baked automatically in the editor; the baked container (audit report + one prototype mesh per skin combination) lives as a sub-asset of the `SkeletonDataAsset`. The component will:

- look up the editor-baked entry for the current skin combination (the runtime never bakes; a miss logs a warning and stays on the CPU path);
- switch the skeleton to GPU rendering when an entry exists — `updateMode` becomes `EverythingExceptMesh` and the `MeshRenderer` is disabled;
- export the 3x2 bone palette every frame after `UpdateComplete`;
- submit it into per-atlas-page instanced batches automatically.

Remove the component (or disable it) to fall back to the stock CPU rendering path — zero residue.

If the audit fails (see table below), the instance **silently stays on the CPU path**. No action required.

## Example

`Example/` ships a ready-to-play crowd scene with two original characters: **SampleDude**
(the adventurer) and **SampleRobot**. Open `Example/GpuSpineComparison.unity` and press Play.

- Live **FPS** and **ms / frame**, with the last CPU/GPU sample kept for comparison.
- Switch the same crowd between CPU and GPU skinning without resetting animation.
- Default 32 characters; adjust the total from 0 to 1000 in the minimal UI.
- Three actual Spine skins per character, with mixed or specific skin selection.
- Original SVG source, Spine 4.2 exports, prefabs and baked GPU data are included.
- Independent of game frameworks; requires Unity, URP and spine-unity only.

Rebuild with **Tools / GPUSpineSkin / Build Comparison Example**. See `Example/README.md`.

## When to use GPU skinning

This plugin is a workload-dependent rendering option, not a universal FPS upgrade.
**CPU skinning can be cheaper for small crowds and low-vertex characters.** Bone evaluation
and AnimationState still run on the CPU in both modes. GPU mode adds palette/deform/color
uploads, visibility checks, sorting, buffer management and draw submission.

| Case | What to expect |
| --- | --- |
| Few characters, mostly region attachments, lightweight meshes | Start with the stock CPU path; GPU overhead can exceed the saved mesh work. |
| Many visible instances sharing meshes, skins and atlas materials | A candidate for GPU skinning; measure CPU mesh generation/upload savings against plugin overhead. |
| Dense weighted meshes and deforming cloth/accessories | More CPU vertex work can move to the GPU, but deform evaluation and data uploads remain costs. |
| Frequent skin/attachment/draw-order changes, many materials or interleaved transparency | Batches may split; more characters do not imply one draw. Measure real submissions and spikes. |
| Clipping, shadows or custom multipass rendering | GPU can support these paths, but each has additional cost and integration requirements. Verify visual parity first. |
| CPU animation/constraints, AI, fill-rate or other systems dominate | Moving mesh skinning alone may provide little or no frame-time benefit. |

The Example has two explicit workloads:

- **Simple duo**: original adventurer and robot, each 20 bones, 16 slots and 159 visible
  vertices, with three skins. This is a basic correctness/control sample, not proof of speedup.
- **Complex courier** (default): 48 bones, 24 slots, about 1170 baked vertices per skin,
  four deforming weighted cloth/accessory meshes, a 24-point non-convex visor clip,
  attachment/color/draw-order timelines and three skins. All of those animations run in
  the walk loop. Mesh density supports visible cloth deformation rather than duplicate geometry.


- **Ultra courier**: 100 bones, 26 slots and about 2490 baked vertices per skin. It extends
  the complex courier with segmented cloth, scarf, hair and two ribbon chains; these bones
  animate through the walk loop and participate in weighted Deform meshes. Use it when
  evaluating high bone-count and deform upload cost. It is intentionally a stress case,
  not a recommendation to use 100 bones for every production character.

The complex case exercises the same *kinds* of features as a production Spine character.
It does not reproduce any host game's full lighting, shadows, outline, camera or gameplay costs,
nor does it promise that GPU will win. Skeleton/skin/animation counts describe content;
only the active mesh, animations, clipping and rendering work determine per-frame cost.

Compare CPU and GPU with the **same case, count, skin distribution, resolution and camera**.
Allow warm-up and compare repeated FPS and frame-time samples. Check Profiler CPU mesh work,
render thread, GPU time, GC and actual draw count before choosing a mode. Do not infer lower
CPU consumption from FPS alone. Player results are preferable for final performance decisions;
Editor Play Mode is sufficient for interactive exploration but includes Editor overhead.



Editor note: the example restricts GPU submissions to its Game Camera through
`GpuSkeletonRenderer.CameraFilter`. This avoids paying the GPU batch preparation cost for
SceneView and preview cameras while editing. The CPU renderer remains visible in those views.
The plugin itself keeps multi-camera support; production integrations should set an explicit
filter when a renderer should belong to only one camera.

## Editor menus

All generic tools hang under **`Tools/GPUSpineSkin`**. They operate on the selected `SkeletonDataAsset`(s):

| Menu | What it does |
|---|---|
| `Tools/GPUSpineSkin/Rebake Selected` | Rebake if the source fingerprint or entry keys changed. |
| `Tools/GPUSpineSkin/Force Rebake Selected` | Clear the fingerprint and rebuild even when the no-change check would skip. |
| `Tools/GPUSpineSkin/Log Audit Report` | Log the graded audit without baking. |
| `Tools/GPUSpineSkin/Dump Baked Data` | Log the container, entries and declared combos. |
| `Tools/GPUSpineSkin/Inspect Default Shader` | Log compile status of `GpuSpine/URP/Skeleton`. |

The same Rebake / Audit commands also appear as Project context items under `Assets/GpuSpine/` when a `SkeletonDataAsset` is selected. Host-project smoke tools should use `Tools/GPUSpineSkin/Temp/...`, not a separate top-level menu.

## Automatic CPU fallback rules

| Feature detected | Behavior |
|---|---|
| Deform timeline | Supported: the CPU-computed `slot.Deform` rides the per-instance deform buffer every frame and is applied in the vertex shader before the bone weighting (absolute replacement for unweighted attachments, per-influence offsets for weighted ones) |
| Slot color timeline (RGBA / RGB / Alpha) | Supported: every slot's `slot.R/G/B/A` rides the per-instance slot color buffer every frame and is multiplied into the vertex color in the shader (CPU-identical composition) |
| Dark color timeline (RGBA2 / RGB2, tint black) | CPU fallback (tint black is not implemented) |
| Texture sequence | CPU fallback |
| No baked entry for the current skin combination | CPU fallback with a warning |
| SkeletonRenderer.zSpacing ≠ baked zSpacing (0) | CPU fallback with a warning |
| Attachment timeline | Supported (dynamic slots): every attachment variant of the slot is pre-baked; each instance selects one variant per slot, unselected variants are folded in the vertex shader |
| Draw order timeline | CPU fallback with a warning, unless `AllowDrawOrderTimeline` is enabled on the component (possible overlap-order artifacts) |
| Clipping attachment | CPU fallback with a warning, unless `IgnoreClipping` is enabled on the component (renders unclipped) |
| Vertex influenced by > 4 bones | Weights truncated & renormalized with a warning |

## Custom shader integration

Include `Runtime/Shaders/SpineGpuSkinning.hlsl` and call one function at the top of your vertex function. See `Skills~/gpuspine-use-plugin/references/integration-examples.md` for a complete example.

## Custom RenderPass integration

Enumerate live batches via the batch source API and re-submit them into your own render targets (mask RTs etc.). See `Skills~/gpuspine-use-plugin/references/integration-examples.md`.

## 诊断日志与自动验证总开关

统一入口：[GpuSpineDiagnostics.cs](Runtime/GpuSpineDiagnostics.cs)。两个编译期常量默认均为 `false`：

```csharp
public const bool EnableLogging = false;
public const bool EnableAutomaticValidation = false;
```

- `EnableLogging` 控制已接入的运行时日志和自动烘焙诊断文字；关闭日志不会停止验证流程。
  显式菜单报告与烘焙异常并非全部由该开关过滤。
- `EnableAutomaticValidation` 是宿主自动冒烟、截图、性能采集和 CPU/GPU 对照的总门禁。
  宿主必须主动检查它；定义常量本身不会自动拦截任意外部脚本。
- 在自动 Bootstrap 的第一步检查总开关，早于读取 EditorPrefs、命令行参数和创建 GameObject。
  已挂载的验证组件也应在 Start 禁用自身；Update/LateUpdate 不得继续改帧率或渲染状态。
- 关闭时正常 GPU 蒙皮、运行时注册和编辑器资源烘焙仍工作；不要给这些生产入口加验证门禁。
- 需要验证时显式把自动验证开关改为 `true` 并等待 Unity 重编译；验证完成后恢复 `false`。
  EditorPrefs、验证宏和命令行选项只能作为总门禁之后的二级条件，不能绕过它。
- 性能基准中不要运行自动截图：同步 ReadPixels、PNG 编码和 CPU/GPU 切换会制造额外卡顿。

宿主接入示例（验证驱动不随插件分发）：

```csharp
static void Bootstrap() {
    if (!GpuSpineDiagnostics.EnableAutomaticValidation) return;
    // 仅在此后启动宿主验证。
}
void Start() {
    if (!GpuSpineDiagnostics.EnableAutomaticValidation) { enabled = false; return; }
    // 仅在此后注册采集事件、创建截图目标或启动协程。
}
```

## Agent skills

This repository ships two agent skills under `Skills~/`:

- `gpuspine-use-plugin` — integration, fallback rules, troubleshooting.
- `gpuspine-develop-plugin` — architecture, baking semantics, buffer layouts, verification workflow.

Copy the skill folder(s) into your project's agent skill directory (e.g. `.agents/skills/`) to use them.

## License

MIT (see `LICENSE`). Spine runtimes remain under the Spine Runtimes License and are not part of this repository.

## 布局资源共享与冷切换

- 每个相机独立管理资源组，键包含骨架 ResourceOwner、实际 Mesh、图集页材质、覆盖材质及渲染状态。
  同一骨架的不同绘制顺序视图共用骨骼/实例缓冲，布局切换只更新索引范围。
- 兼容布局保留成员；页面集合、Mesh 或状态不兼容时仍按完整注册路径处理。
- 多段角色始终按角色顺序逐段绘制；仅单段、几何范围一致、实例连续时合并绘制。
- args 在同帧按几何与实例范围独立分配槽位，跨帧按绘制峰值复用；不按历史布局无限累积。
  同帧不得改写已有槽位，否则正常绘制和单角色描边会互相覆盖。
- 材质按资源组内实例偏移共享，扩容后重绑全部偏移材质；不再为每个布局段复制材质。
- `GetBatches(camera, source)` 返回可直接重绘的范围；`SubmeshIndex=0` 提供三角形拓扑，
  实际范围由 args 的 IndexStart/IndexCount 决定，调用者必须保留返回的材质与 args 配对。
- 初次加载新的骨架、图集或相机仍需要资源初始化。此优化消除布局冷切换的重复创建，
  不承诺全游戏零分配、零 GC，也不把 Editor 的材质回调成本等同于 Player 成本。
