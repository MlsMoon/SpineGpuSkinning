---
name: gpuspine-use-plugin
description: Integrate the SpineGpuSkinning plugin to render spine-unity SkeletonAnimation characters through GPU vertex skinning and DrawMeshInstancedIndirect batching. Covers one-click component setup, automatic editor baking of SkeletonDataAsset sub-assets, runtime registration of baked data, skin combination declarations, component options, CPU fallback rules, custom shader and custom RenderPass integration, and troubleshooting. Use when adding GpuSkeletonRenderer to a skeleton, declaring skin combos, diagnosing why a skeleton stays on the CPU path, writing a custom GPU skinning shader, or re-submitting Spine batches into a private render pass.
---

# GpuSpine: Use the Plugin

Integration guide for the SpineGpuSkinning plugin, written for the consuming project. All
paths below are relative to the plugin root (`SpineGpuSkinning/`); adjust them to where the
folder lives in your project (e.g. `Assets/Plugins/SpineGpuSkinning/`).

## What the plugin does

- Moves the per-frame CPU skinning / mesh rebuild / vertex upload of a spine-unity
  `SkeletonAnimation` to the GPU vertex shader, without modifying any Spine source code.
- The CPU keeps bone evaluation (AnimationState, mixing, events, physics, BoneFollower,
  runtime skinning). The GPU takes over vertex skinning, mesh assembly and vertex upload.
- Instances sharing an atlas page are drawn in one `Graphics.DrawMeshInstancedIndirect`
  batch — no RenderFeature required.
- Baking happens once in the editor; the runtime never bakes. Anything it cannot render on
  the GPU path stays on the stock CPU path automatically (see
  `references/fallback-rules.md`).

## Requirements

- Unity 2022.3+
- Universal Render Pipeline (URP) 14
- spine-unity 4.2 (spine-csharp 4.2) installed in the project. Spine runtimes are NOT
  bundled with the plugin.

## 1. One-click usage

1. Select the GameObject that has your `SkeletonAnimation`.
2. Add component → `Gpu Skeleton Renderer` (menu `Spine/Gpu Skeleton Renderer`).
3. Enter Play Mode. Done.

Source: `Runtime/GpuSkeletonRenderer.cs`. On enable (play mode only) the component:

- looks up the editor-baked entry for the current skin combination (a miss logs a warning
  and stays on the CPU path);
- switches the skeleton to GPU rendering when an entry exists — `updateMode` becomes
  `EverythingExceptMesh` and the `MeshRenderer` is disabled (both restored on disable);
- exports the 3x2 bone palette every frame after `SkeletonAnimation.UpdateComplete`;
- submits the skeleton into per-atlas-page instanced batches automatically.

✅ Correct integration:

- `SkeletonAnimation` and its `MeshRenderer` on the same GameObject as the component or on
  a child (the component searches both).
- `SkeletonRenderer.zSpacing` left at `0` (baking is fixed to 0; a mismatch falls back to
  the CPU path with a warning).
- Skeleton data imported after the plugin is installed, so the automatic bake has run
  (otherwise use `Tools/GPUSpineSkin/Rebake Selected` or `Assets/GpuSpine/Rebake Skeleton Data` once).

❌ Common mistakes:

- Expecting edit-mode preview: the component does nothing at all in edit mode by design —
  editor previews keep the original CPU path.
- Driving `MeshRenderer.enabled` from your own code to hide/show the skeleton. The
  component force-disables the renderer every frame; use the `Visible` property instead
  (see options below).
- Re-enabling `updateMode` manually. The component re-forces `EverythingExceptMesh` in
  `LateUpdate`; fighting it revives the CPU mesh chain and double-renders.

## 2.1 合批与项目 Pass 排错

- 每个 `GpuSpineDrawSlice` 都必须重新绑定 `_GpuSpineInstanceFilter = -1`；材质复制不会可靠保留运行时 uniform。否则同一批次只会显示实例 0，实例排序变化时表现为猫闪现。
- GPU Shader 的实例 ID 必须与 `SV_InstanceID` 和切片 `_GpuSpineInstanceOffset` 一起验证，不能只检查 `IsGpuActive`。
- 连续帧验证至少记录相机绘制批次、可见源数量和 0 绘制帧；固定姿态下 CPU/GPU/再次启用 GPU 的 Mask 应保持一致。

## 2. Editor baking (automatic)

Source: `Editor/GpuSpineBakeProcessor.cs`, `Editor/GpuSpineBakerEditorUtility.cs`.

- Whenever a `SkeletonDataAsset` is imported or updated, an `AssetPostprocessor` audits the
  shared `SkeletonData` and bakes one **entry** (bind-pose prototype mesh) per skin
  combination: the default entry, one per skin, plus every declared combination.
- The container `GpuSpineBakedData` (audit report + entries) is stored as a **sub-asset of
  the SkeletonDataAsset's `.asset`**; every entry mesh hangs off the container. Select the
  SkeletonDataAsset to inspect the whole baked state.
- A failed audit still writes the container — report only, no meshes — so you can read the
  failure reasons in the Inspector.
- A no-change check (source fingerprint of skeleton/atlas/texture dependencies + scale,
  plus the set of entry keys) skips no-op rebakes; user edits to `DeclaredCombos` survive
  rebakes.
- Baking is fixed to `zSpacing = 0` (recorded on the container as `BakedZSpacing`). Keep
  `SkeletonRenderer.zSpacing` at 0 on GPU-path skeletons.

Editor menus (selection of one or more `SkeletonDataAsset`):

- `Tools/GPUSpineSkin/Rebake Selected` (also `Assets/GpuSpine/Rebake Skeleton Data`) —
  rebakes when the source fingerprint or entry keys changed.
- `Tools/GPUSpineSkin/Force Rebake Selected` — clears the fingerprint first, so the
  no-change check cannot skip. Use this to repair containers whose entry meshes were
  deleted by hand, or when the baked state must be rebuilt without any source change.
- `Tools/GPUSpineSkin/Log Audit Report` (also `Assets/GpuSpine/Log Audit Report`) —
  logs the graded audit (failures, warnings, dynamic slots and their variants)
  without baking.
- `Tools/GPUSpineSkin/Dump Baked Data` — logs the baked container, entries and
  declared combos.
- `Tools/GPUSpineSkin/Inspect Default Shader` — logs compile status of
  `GpuSpine/URP/Skeleton`.

Host-project smoke tools should hang off `Tools/GPUSpineSkin/Temp`, not a separate
top-level menu.

The plugin ships a visible MIT example under `Example/`: original character
`SampleDude` and `GpuSpineComparison.unity` (CPU left, GPU right). Rebuild with
`Tools/GPUSpineSkin/Build Comparison Example`. This is not Esoteric Spineboy.

### Declaring skin combinations (DeclaredCombos)

The bake set covers the default skin and every single skin automatically. **Composite
skins you build at runtime via `Skin.AddSkin` must be declared** on the container's
`DeclaredCombos` list, or the runtime lookup misses and the instance falls back to the CPU
path with a warning.

- `SkinNames` — skin names stacked via `Skin.AddSkin` in array order; later skins override
  earlier ones per placeholder. **The declaration order must match the runtime `AddSkin`
  call order exactly** — the content key hashes the resolved attachment set produced by
  that order.
- `DisplayName` — optional label; falls back to the joined skin names.
- A declaration referencing a skin name that does not exist in the SkeletonData is skipped
  with a warning at bake time.

## 3. Runtime registration of baked data

The component resolves its baked data in this order (`Runtime/GpuSkeletonRenderer.cs`,
`Runtime/Baking/GpuSpineBakedRuntime.cs`):

1. The serialized `BakedData` field on the component (prefab / scene instances: assign the
   container sub-asset in the Inspector — the component self-registers it).
2. The `GpuSpineBakedRuntime` registry (runtime-spawned instances: register the container
   before the first component enables).
3. Neither available → warning, stays on the CPU path.

The registry is keyed by the `SkeletonDataAsset` reference and is **cleared on every play
mode start**, so registrations must be renewed per session:

```csharp
using GpuSpine.Baking;
using Spine.Unity;
using UnityEngine;

/// <summary>Registers baked data for skeletons spawned at runtime.</summary>
public sealed class GpuSpineSpawner : MonoBehaviour {
    [SerializeField] SkeletonDataAsset skeletonDataAsset;
    [SerializeField] GpuSpineBakedData bakedData; // Sub-asset of the SkeletonDataAsset.
    [SerializeField] GameObject skeletonPrefab;   // SkeletonAnimation + GpuSkeletonRenderer.

    void Awake () {
        GpuSpineBakedRuntime.Register(skeletonDataAsset, bakedData);
    }

    public GameObject Spawn (Vector3 position) {
        return Instantiate(skeletonPrefab, position, Quaternion.identity);
    }
}
```

## 4. Component options

All on `GpuSkeletonRenderer` (`Runtime/GpuSkeletonRenderer.cs`):

| Member | Type | Effect |
|---|---|---|
| `MaterialOverride` | field | Optional custom GPU material template. Its **shader** replaces the cloned page material's shader; per-page atlas textures still come from the page material. Null uses the plugin default shader `GpuSpine/URP/Skeleton`. |
| `BakedData` | field | Baked data container (sub-asset of the SkeletonDataAsset). Null falls back to the `GpuSpineBakedRuntime` registry. |
| `SortMode` | field | Draw-order strategy of the batches this instance joins: `CameraDepth` (default), `WorldZBackToFront`, `None` (registration order). The **first submitted instance of a batch decides the batch's sort mode** — mixing modes within one (entry, submesh) batch is not supported. |
| `AllowDrawOrderTimeline` | field | Allow the GPU path despite a draw order timeline (possible overlap-order artifacts). Off (default): CPU fallback with a warning. |
| `IgnoreClipping` | field | Allow the GPU path despite clipping attachments (rendered unclipped). Off (default): CPU fallback with a warning. |
| `Visible` | property | False removes the instance from the submitted batches while bone matrix export keeps running (the skeleton stays animated, just not drawn). |
| `IsGpuActive` | property | True while this skeleton is submitted through the GPU instanced path. |
| `LastAudit` | property | The admittance audit evaluated on enable (null while never audited). |
| `WriteInstanceData` | event | Per-frame callback to fill the `Custom0`/`Custom1` slots of this instance's draw data (see `references/integration-examples.md`). |
| `IncludeInRuntimeSwitch` | field | When true, the instance self-registers with `GpuSpineRuntimeSwitch` so host commands can toggle CPU/GPU. Off by default so examples stay out. Unregister happens on destroy, not disable, so a switch-off can re-enable later. |
| `ApplyCameraRenderingLayerFilter` | field | Honor URP Camera Rendering Layer Filter when submitting. On by default. Cameras that do not enable the filter are unaffected. |
| `CopyPropertyBlockToCustomData` | field | Copy MeshRenderer MaterialPropertyBlock values into instance Custom0/Custom1 before `WriteInstanceData`. Off by default. |
| `Custom0Property` | field | Vector property copied into Custom0.xyz (and .w unless `Custom0WProperty` is set). |
| `Custom0WProperty` | field | Optional float property packed into Custom0.w. |
| `Custom1Property` | field | Vector property copied into Custom1. |

Removing or disabling the component restores the original CPU path completely (original
`updateMode`, original `MeshRenderer.enabled`) — zero residue.

## 5. CPU fallback rules (summary)

Full table with exact warning texts: `references/fallback-rules.md`.

| Audit finding | Behavior |
|---|---|
| Deform timeline | Hard failure → always CPU path |
| Slot color timeline (RGBA / RGB / Alpha / RGBA2 / RGB2) | Hard failure → always CPU path |
| Texture sequence on a Region/Mesh attachment | Hard failure → always CPU path |
| Bake-time vertex count overflow (> 1,048,576) | Hard failure for that combination (null mesh) |
| Draw order timeline | CPU path with a warning, unless `AllowDrawOrderTimeline` |
| Clipping attachment | CPU path with a warning, unless `IgnoreClipping` |
| Vertex influenced by > 4 bones | Strongest 4 kept, weights renormalized, warning; GPU path continues |
| Attachment timeline | Supported via dynamic slots (variants pre-baked, folded in the vertex shader) |
| No baked entry for the current skin combination | CPU path with a warning |
| `zSpacing` ≠ baked zSpacing (0) | CPU path with a warning |

"CPU fallback" means the instance renders exactly as without the component — no action
required, no partial state.

## 6. Skin switching at runtime

`GpuSkeletonRenderer` tracks the effective skin combination every `UpdateComplete`: it
recomputes the content key and moves the instance to the pre-baked entry's batches. Single
skins need no declaration; composite skins must be declared (see section 2).

```csharp
using Spine;
using Spine.Unity;
using UnityEngine;

public static class GpuSpineSkinUtil {
    /// <summary>Switches to a composite skin. Requires a DeclaredCombos entry whose
    /// SkinNames are { "Base", "Dirty" } in this exact order.</summary>
    public static void ApplyComboSkin (SkeletonAnimation skeletonAnimation, string baseSkin, string overlaySkin) {
        Skeleton skeleton = skeletonAnimation.Skeleton;
        Skin combo = new Skin(baseSkin + "+" + overlaySkin);
        combo.AddSkin(skeleton.Data.FindSkin(baseSkin));    // Same order as DeclaredCombos.
        combo.AddSkin(skeleton.Data.FindSkin(overlaySkin));
        skeleton.SetSkin(combo);
        skeleton.SetSlotsToSetupPose();
    }
}
```

✅ Declare the combo, rebake, then switch — the instance just changes batches.
❌ Switch to an undeclared combination — the instance falls back to the CPU path with a
warning naming the missing key.

## 7. Custom shader integration (summary)

Complete, copy-paste-ready examples: `references/integration-examples.md`.

- Include `Runtime/Shaders/SpineGpuSkinning.hlsl` in your shader.
- Declare the vertex struct exactly matching the baked layout
  (`POSITION`, `TEXCOORD0`–`TEXCOORD5`, `COLOR`, `SV_InstanceID`).
- First line of your vertex function: call
  `GpuSpineSkinToWorld(positionOS, influence12, influence3, boneIndices, boneWeights, dynInfo, instanceID)`
  (7 arguments — note the `dynInfo` parameter for dynamic slot folding).
- Assign the material (or a template) to the component's `MaterialOverride`; only its
  shader is taken, the atlas texture still comes from the page material clone.
- The skinning buffers (`_GpuSpineBones`, `_GpuSpineInstances`, `_GpuSpineDynSlots`) are
  bound at material level by the runtime — never bind them yourself.

## 8. Custom RenderPass integration (summary)

Complete example: `references/integration-examples.md`.

- Enumerate the live batches via `GpuSkinningManager.GetBatches()`
  (`Runtime/Core/GpuSkinningManager.cs`).
- Resolve a GPU instance from a scene `Renderer` with `GpuSkinningManager.TryGetByRenderer`.
- Each `GpuSpineBatchInfo` carries `Mesh`, `SubmeshIndex`, `Material`, `ArgsBuffer`,
  `Bounds`, `InstanceCount`.
- Re-submit with `CommandBuffer.DrawMeshInstancedIndirect`; the batch material already
  carries the bound buffers, so the draw works as-is.
- Do not cache the returned list across frames (it is reused).

## 9. Troubleshooting

| Symptom | Likely cause → fix |
|---|---|
| Skeleton invisible / renders as before | The instance is on the CPU path. Read the Console: every refusal logs a warning starting with `GpuSkeletonRenderer stays on the CPU path:` naming the reason. |
| Warning "no GpuSpineBakedData available" | Container not assigned and not registered. Assign `BakedData` or call `GpuSpineBakedRuntime.Register` before enable (runtime-spawned instances). |
| Warning "audit failed" | A hard failure (deform/slot-color/sequence). Run `Tools/GPUSpineSkin/Log Audit Report` on the SkeletonDataAsset for the full reasons. This skeleton cannot use the GPU path. |
| Warning "no baked entry for the current skin combination (key ...)" | Composite skin not declared, or `AddSkin` order differs from `DeclaredCombos.SkinNames`. Fix the declaration order, then `Tools/GPUSpineSkin/Force Rebake Selected`. |
| Warning "zSpacing ... differs from the baked zSpacing 0" | Set `SkeletonRenderer.zSpacing` to 0. |
| Renders on GPU but wrong overlap between instances | `SortMode`. Remember the first submitted instance decides the batch's mode; keep one mode per skeleton family. |
| GPU-path skeleton disappears at distance | Bounds are the union of bind-pose bounds plus a fixed margin; extreme pose deviation beyond the margin can be culled. Check `Visible` first — most reports are a visibility toggle driving `MeshRenderer.enabled` (mapped to `Visible`, see section 1). |
| Dynamic slot shows nothing for an attachment | One-time warning "not a baked variant; folding all variants of the slot": the attachment name is live but was never baked as a variant (stale bake) — rebake. |
| `MaterialOverride` shader has no effect on texture | By design: only the shader is taken from the override; atlas textures come from the page material clone. |
| Nothing GPU-renders in a standalone build | The default shader `GpuSpine/URP/Skeleton` (or your override shader) must be included in the build; check the Console for "default shader ... not found" in the editor. |
| Edit mode shows the old CPU path | By design: the component does nothing in edit mode. |

## References

- `references/fallback-rules.md` — every audit finding, its grade, the exact runtime
  behavior and warning text, and the dynamic slot folding semantics.
- `references/integration-examples.md` — complete custom shader and custom RenderPass
  examples, including the `WriteInstanceData` callback.

## 自动验证与日志门禁

- `Runtime/GpuSpineDiagnostics.cs` 是两个 const 开关的唯一来源；默认均为 false。
- `EnableLogging` 只控制已接入的日志调用，不代表停止截图、协程或 CPU/GPU 切换。
- 宿主自动验证入口必须先检查 `EnableAutomaticValidation`，再检查 EditorPrefs、验证宏或命令行参数。
  Bootstrap 在创建对象前返回；场景中已有验证组件的 Start 应禁用自身，Update/LateUpdate 不得改渲染状态。
- 开关不会自动约束未接入的第三方脚本。新增自动截图、冒烟或性能驱动必须接入同一总门禁。
- 正常 GPU 注册、蒙皮、手动工具和资源烘焙不受自动验证门禁影响。
- 改 const 后等待宿主程序集重编译；可核对关闭后的 Bootstrap IL 只剩 ret。
- 测量正常玩法前确认没有自动截图/回读/PNG 编码和 CPU/GPU 对照；关闭 Console 日志不足以净化采样。
- 完整语义和宿主代码示例见 [插件 README](../../README.md#诊断日志与自动验证总开关)。

## 布局共享后的自定义 Pass 合同

- 资源组不等于 DrawCall；一个资源组可服务同一骨架不同布局的多段绘制。
- 继续使用 `GetBatches(camera)` 或 `GetBatches(camera, source)`，保留每项的材质、args 和 Mesh 配对。
- `SubmeshIndex=0` 提供拓扑，实际几何范围来自 args；不要自行用 mesh 子网格索引替换绘制范围。
- 返回视图和 args 属于当前相机当前帧，不跨帧缓存，不手动释放插件拥有的材质/缓冲。
- `BatchInstanceIndex` 仅作最近上传组的诊断值；单角色重绘以 `GetBatches(camera, source)` 为准。
- 绘制顺序变化不会重建整套资源；新骨架/图集/相机仍可初始化资源，需区分启动与稳定运行数据。
