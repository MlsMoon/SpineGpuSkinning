---
name: gpuspine-develop-plugin
description: Maintain and extend the SpineGpuSkinning plugin itself — the GPU skinning path for spine-unity SkeletonAnimation. Covers the four-layer architecture (CPU preservation, editor baking, per-frame upload, indirect submission), the 1:1 baking semantics against the Spine CPU path, the C#/HLSL buffer and vertex layouts, the known pitfalls (material cloning, updateMode resets, import-loop reentrancy, AddSkin key order, row_major matrices), and the verification workflow (csc compile, in-Unity smoke, CPU/GPU A-B, Frame Debugger). Use when modifying plugin code under Runtime/ or Editor/, changing the baked vertex contract, debugging batching or skinning correctness, or adding a new baked feature.
---

# GpuSpine: Develop the Plugin

Maintenance guide for the SpineGpuSkinning plugin, written for whoever edits the plugin
itself. All paths are relative to the plugin root (`SpineGpuSkinning/`). References to
Spine runtime sources use their upstream relative paths (e.g.
`spine-unity/Components/SkeletonRenderer.cs`, `spine-csharp/Attachments/VertexAttachment.cs`).

For integration-side usage (component options, fallback rules, custom shader/RenderPass
contracts), see the sibling skill `gpuspine-use-plugin`.

## Four-layer architecture

```
SkeletonAnimation (unmodified Spine runtime)
  |
  | 1. CPU PRESERVATION LAYER — Runtime/GpuSkeletonRenderer.cs
  |    updateMode = EverythingExceptMesh  -> SkeletonRenderer.LateUpdate returns before
  |    LateUpdateMesh (spine-unity/Components/SkeletonRenderer.cs:587): bone evaluation,
  |    AnimationState, mixing, events, physics keep running untouched; the CPU mesh
  |    rebuild/upload path is skipped. MeshRenderer disabled as a second cut.
  v
  2. BAKING LAYER (editor only, one-shot) — Editor/GpuSpineBakeProcessor.cs,
     Editor/GpuSpineBakerEditorUtility.cs, Editor/GpuSpineEditorMenu.cs,
     Runtime/Baking/GpuSpineAuditor.cs,
     Runtime/Baking/GpuSpineBaker.cs, Runtime/Baking/GpuSpineBakeKey.cs
     Audit -> bake one bind-pose prototype mesh per skin combination -> GpuSpineBakedData
     container as sub-asset of the SkeletonDataAsset; entry meshes hang off the container.
     Semantics: references/skinning-semantics.md
  |
  | 3. UPLOAD LAYER (per frame, per instance) — Runtime/GpuSkeletonRenderer.cs
  |    On SkeletonAnimation.UpdateComplete (bone world transforms final, constraints and
  |    UpdateLocal writers included): export GpuBoneMatrix palette (Bone.A/B/C/D/WorldX/
  |    WorldY) and resolve the dynamic slot variant selection; dirty flags gate uploads.
  v
  4. SUBMISSION LAYER (per frame, per batch) — Runtime/Core/GpuSkinningManager.cs,
     Runtime/Core/GpuSpineBatch.cs
     SRP beginCameraRendering, after skeleton updates:
     per-(entry, submesh) batches, back-to-front CPU sort, palette/instance/dynSlot
     buffer uploads, Graphics.DrawMeshInstancedIndirect. Zero cost with no registrations.
     Layouts: references/buffer-layouts.md
```

File map:

| File | Role |
|---|---|
| `Runtime/GpuSkeletonRenderer.cs` | One-component switch: admittance, CPU/GPU switching, palette + dynamic slot export, skin tracking, fallback restore |
| `Runtime/Core/GpuSkinningManager.cs` | Play-mode submission hub; batch registry; cloned batch materials; `GetBatches()` batch source API |
| `Runtime/Core/GpuSpineBatch.cs` | One indirect draw batch: buffers, sort, upload, submit; `GpuSpineBatchInfo` view struct |
| `Runtime/Core/GpuBoneMatrix.cs` | 24-byte palette entry struct (C# side) |
| `Runtime/Core/GpuSpineInstanceData.cs` | 112-byte per-instance struct + `GpuSpineInstanceDataWriter` delegate |
| `Runtime/Core/GpuSpineSortMode.cs` | None / CameraDepth / WorldZBackToFront |
| `Runtime/Baking/GpuSpineAuditor.cs` + `GpuSpineAuditReport.cs` | Graded admittance audit (failures / warnings / dynamic slots) |
| `Runtime/Baking/GpuSpineBaker.cs` | Pure bake function: SkeletonData + skin combination -> entry |
| `Runtime/Baking/GpuSpineBakeKey.cs` | FNV-1a content key, shared by editor bake and runtime lookup |
| `Runtime/Baking/GpuSpineBakedData.cs` | Container SO + entry/submesh/variant serializable types |
| `Runtime/Baking/GpuSpineBakedRuntime.cs` | Runtime registry keyed by SkeletonDataAsset |
| `Editor/GpuSpineBakeProcessor.cs` | AssetPostprocessor auto-bake |
| `Editor/GpuSpineBakerEditorUtility.cs` | Bake orchestration, persistence, no-change check, fingerprint, ForceRebake |
| `Editor/GpuSpineEditorMenu.cs` | `Tools/GPUSpineSkin` generic menus + `Assets/GpuSpine` context aliases |
| `Runtime/Shaders/SpineGpuSkinning.hlsl` | Skinning include: buffer declarations + `GpuSpineSkinToWorld` |
| `Runtime/Shaders/SpineGpu-URP-Skeleton.shader` | Default URP unlit PMA shader `GpuSpine/URP/Skeleton` |

## Per-frame data flow (play mode)

1. Spine runtime updates each skeleton (animation, constraints, bone world transforms).
2. `SkeletonAnimation.UpdateComplete` fires → `GpuSkeletonRenderer.OnSkeletonUpdateComplete`:
   - `ExportBoneMatrices`: `GpuBoneMatrix(bone.A, bone.B, bone.C, bone.D, bone.WorldX, bone.WorldY)`
     per bone → `paletteDirty`;
   - `RefreshDynamicSlots`: resolve each dynamic slot's variant id from
     `Slot.Attachment.Name` (`0xFFFFFFFF` = fold all) → `dynamicSlotsDirty`;
   - recompute the content key; on change, move to the new entry's batches
     (`GpuSkinningManager.ChangeEntry`) or restore the CPU path.
3. `GpuSkinningManager.BeginCamera` (SRP beginCameraRendering, after skeleton updates):
   - build the submission set per batch (`ShouldSubmit` = gpuActive && Visible && enabled);
   - stable insertion sort back-to-front by the batch's sort mode (first submitted
     instance decides the mode);
   - upload palette/dynSlot buffers only when dirty (reorder, membership change, count
     change, or a dirty instance); instance data is uploaded every frame (untracked);
   - 每帧按实际绘制范围复用 args 槽位，按实例偏移复用材质；不同范围同帧不能改写同一槽位。
   - 多段角色严格逐角色展开；单段且几何一致时才能合并连续实例。
   - 使用 Graphics.RenderMeshIndirect 提交；后续自定义 Pass 复用返回的材质/args 配对。

## Design invariants (do not break)

- **The runtime never bakes.** Baked data comes only from the editor layer; a miss is a
  warning + CPU path. This keeps the runtime free of Mesh allocation and atlas work.
- **The CPU path is never modified** — no Spine source edits, no mesh chain surgery.
  `EverythingExceptMesh` + disabled MeshRenderer is the whole cut, and `OnDisable`
  restores both.
- **Buffer bindings live on the batch material** (`material.SetBuffer` in
  `GpuSpineBatch.EnsureCapacity`), following the ES2DInstance convention. Rebinding is
  mandatory after buffer (re)creation on capacity growth.
- **C# structs mirror HLSL 1:1** (`LayoutKind.Sequential`, `Marshal.SizeOf` strides;
  `row_major float4x4` on the HLSL side). Any layout change must touch both sides plus
  the struct docs. Details: `references/buffer-layouts.md`.
- **Keys are content hashes** (`GpuSpineBakeKey`, FNV-1a over the per-slot resolved setup
  attachment names), stable across sessions and machines. Editor baking and runtime
  lookup MUST keep using this single implementation.
- **资源组与绘制范围分离**：组键为 ResourceOwner、实际 Mesh、图集材质和渲染状态。
  布局 entry 不再各自拥有一套缓冲；通过独立 args 选择其 IndexStart/IndexCount。
- **Transparent rendering relies on the painter's algorithm within a batch**: the
  instance buffer write order is the draw order, so batches are sorted on the CPU before
  upload; a sort that moved instances must trigger a palette re-upload.

## Pitfalls (all learned the hard way — read before editing)

1. **Shared page materials get their `enableInstancing` flipped by the Spine runtime**
   (`SkeletonRenderer.SetMaterialSettingsToFixDrawOrder`). Batch materials must therefore
   always be **clones** (`GpuSkinningManager.CreateBatchMaterial`), never the shared page
   material. The material override contributes only its shader; property values (atlas
   texture included) stay with the page material clone.
2. **`SkeletonRenderer.OnBecameVisible` resets `updateMode` to `FullUpdate`**
   (spine-unity/Components/SkeletonRenderer.cs:714-721), which would revive the CPU mesh
   chain. The component guards twice: the disabled MeshRenderer blocks the callback path,
   and `LateUpdate` re-forces `EverythingExceptMesh` defensively. External code driving
   `Renderer.enabled` is intercepted the same way and mapped to the `Visible` property.
3. **`OnPostprocessAllAssets` re-entrancy**: `Rebake` saves the asset file, which
   re-enters the postprocessor. The static `rebaking` flag covers synchronous reentrancy;
   the deferred second pass then hits the no-change check (`SourceFingerprint` +
   entry keys) and returns without saving, terminating the import loop. The fingerprint
   deliberately excludes the SkeletonDataAsset's own `.asset` file — including it would
   loop forever. Dependency entries carry a content hash (v2), so VCS checkouts and
   branch switches that only rewrite file times no longer trigger spurious rebakes.
4. **`Skin.AddSkin` order is the content key.** A declared combination whose `SkinNames`
   order differs from the runtime `AddSkin` order resolves different (or equal-but-mis-
   labeled) attachments. Editor keys are precomputed with the same
   `GpuSpineBaker.BuildEffectiveSkin` construction the baker uses — never duplicate that
   logic elsewhere.
5. **`float4x4` must be `row_major` in HLSL.** `UnityEngine.Matrix4x4` is uploaded as a
   raw 64-byte copy; without `row_major` the shader reads it transposed and every
   instance renders at the wrong place/orientation. Same class of bug for any future
   matrix field.

## Verification workflow

Run these in order after any plugin change. Do not add automated test scaffolding
(TEST scripts, test asmdefs) to the plugin package.

### 1. Compile check without opening Unity (csc)

Compile the runtime and editor assemblies directly against the Unity engine DLLs, the
already-compiled spine/URP assembly DLLs from `Library/ScriptAssemblies/`, and
`netstandard`. Reference set mirrors the asmdefs (`GpuSpine.Runtime.asmdef` references
`spine-csharp`, `spine-unity`, `Unity.RenderPipelines.Universal.Runtime`; the editor
assembly additionally references the runtime assembly):

宿主工程先执行一次 Unity 编译，然后从 `Library/Bee/artifacts/` 读取当前生成的
`GpuSpine.Runtime.rsp` / `GpuSpine.Editor.rsp` 与实际编译器命令。
直接编译必须复用当前源码清单、define 与依赖；包括 `GpuSpineDiagnostics.cs`，不能沿用旧文件清单。
Unity Editor 和编译器位置在运行时发现，不把机器安装路径写入文档。
临时编译输出放宿主 Library 或系统临时目录，不覆盖正在使用的 ScriptAssemblies。

Exact UnityEngine module DLL layout varies by editor version — let Unity compile once,
then reuse the reference list from a fresh `Library/ScriptAssemblies` plus the editor's
`Managed` folder. If Unity itself compiles the plugin cleanly, that result wins.

### 2. In-Unity smoke test

- Use a **real scene with real skeleton assets** in Play Mode. No TEST scripts.
- Toggle `GpuSkeletonRenderer` on/off on a live skeleton: the visual must not change, the
  Console must stay clean, `IsGpuActive` must reflect the switch.
- Check the baked container sub-asset on the SkeletonDataAsset: entries for default +
  each skin + declared combos, audit passed.
- Exercise one `AttachmentTimeline` asset: variants fold/unfold; a missing variant logs
  once and hides the slot.

### 3. CPU/GPU A-B comparison

Render the same pose twice — once with the component disabled (CPU path), once enabled
(GPU path) — and compare screenshots pixel-wise (or by eye for animation spot checks).
Any vertex offset, color shift or draw-order difference points at the 1:1 semantics
checklist in `references/skinning-semantics.md`.

### 4. Frame Debugger batch check

- One `DrawMeshInstancedIndirect` per (entry, submesh) batch — count must equal the
  number of atlas page boundaries actually on screen, not the number of skeletons.
- N identical skeletons sharing a skin combination must collapse into the same batches
  (instance count = N).
- Verify the args buffer instance count and the bound buffers on the draw.

## References

- `references/skinning-semantics.md` — the 1:1 baking semantics checklist against the
  Spine CPU path (vertex orders, expansion formats, color/z rules, content key).
- `references/buffer-layouts.md` — C#/HLSL struct layouts, buffer index formulas, args
  buffer, TEXCOORD vertex streams, binding rules.

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

## 共享资源维护约束

- `GpuSpineBakedEntry.ResourceOwner` 只归一化数据身份，组键还必须包含实际 Mesh，防止容器释放后复用旧网格资源。
- `GpuSpineCameraFrame.ChangeLayout` 对兼容页面集合只替换布局引用；其他变化完整解除/注册。
- `GpuSpineSliceKey` 包含 indexStart/indexCount/start/count；frameSlices 每帧清理，slicePool 按实际绘制峰值复用。
- `GpuSpineDrawSlice` 仅拥有 args，借用组的偏移材质；材质由组统一销毁，禁止切片重复销毁。
- `GetBatches(camera, source)` 的额外单实例查询不能覆盖已提交的合并实例 args；校验时应回读前后 args。
- 骨骼/动态槽/变形数据的尺寸与版本合同仍属于同一 ResourceOwner；扩容必须重绑所有偏移材质并标记上传失效。
- 使用真实布局切换、1/多实例、换肤、CPU 回退和多相机核对几何范围与骨骼缓冲，不只观察平均帧率。
