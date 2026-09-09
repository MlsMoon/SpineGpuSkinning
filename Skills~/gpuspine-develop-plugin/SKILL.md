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
     [DefaultExecutionOrder(32000)] LateUpdate, later than every UpdateComplete:
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
3. `GpuSkinningManager.LateUpdate` (execution order 32000, after every UpdateComplete):
   - build the submission set per batch (`ShouldSubmit` = gpuActive && Visible && enabled);
   - stable insertion sort back-to-front by the batch's sort mode (first submitted
     instance decides the mode);
   - upload palette/dynSlot buffers only when dirty (reorder, membership change, count
     change, or a dirty instance); instance data is uploaded every frame (untracked);
   - update args `uint[5]` (instanceCount), compute union bounds (+1 unit margin);
   - `Graphics.DrawMeshInstancedIndirect(mesh, submesh, clonedMaterial, bounds, args, 0,
     null, ShadowCastingMode.Off, true, layer)` — shadow casting off (the indirect path
     cannot join the cullResults shadow passes), receiving on.

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
- **Batch = (baked entry, submesh index)**. A submesh is an atlas page material boundary,
  so this equals the CPU path's per-page batching. The page material reference is the
  batch key ingredient.
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
   loop forever. A content change preserving both file length and write time is not
   detected (accepted, documented; the manual Rebake menu covers it).
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

```bat
set UNITY_MANAGED=C:\Program Files\Unity\Hub\Editor\2022.3.x\Editor\Data\Managed
set SA=Library\ScriptAssemblies

csc -nologo -target:library -nostdlib -noconfig ^
  -r:"%UNITY_MANAGED%\UnityEngine.dll" ^
  -r:"%UNITY_MANAGED%\UnityEngine\CoreModule.dll" ^
  -r:"%UNITY_MANAGED%\UnityEngine\UnityEngine.CoreModule.dll" ^
  -r:"%SA%\spine-csharp.dll" -r:"%SA%\spine-unity.dll" ^
  -r:"%SA%\Unity.RenderPipelines.Universal.Runtime.dll" ^
  -r:"%UNITY_MANAGED%\NetStandard\ref\2.1.0\netstandard.dll" ^
  -out:%TEMP%\GpuSpine.Runtime.dll ^
  Runtime\GpuSkeletonRenderer.cs Runtime\Core\*.cs Runtime\Baking\*.cs

csc -nologo -target:library -nostdlib -noconfig ^
  -r:"%UNITY_MANAGED%\UnityEngine.dll" -r:"%UNITY_MANAGED%\UnityEditor.dll" ^
  -r:"%SA%\spine-csharp.dll" -r:"%SA%\spine-unity.dll" ^
  -r:"%SA%\Unity.RenderPipelines.Universal.Runtime.dll" ^
  -r:"%UNITY_MANAGED%\NetStandard\ref\2.1.0\netstandard.dll" ^
  -r:%TEMP%\GpuSpine.Runtime.dll ^
  -out:%TEMP%\GpuSpine.Editor.dll ^
  Editor\GpuSpineBakeProcessor.cs Editor\GpuSpineBakerEditorUtility.cs Editor\GpuSpineEditorMenu.cs
```

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
