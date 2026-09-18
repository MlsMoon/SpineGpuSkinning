---
name: gpuspine-develop-plugin
description: Maintain and extend the SpineGpuSkinning plugin. Covers the four-layer architecture, BakeFormatVersion 5, draw-order layouts, GPU clipping, 96-byte instance data, shared upload groups, fingerprint v2, dirty uploads, and verification. Use when editing Runtime/ or Editor/, changing the baked vertex contract, debugging batching or skinning correctness, or adding a baked feature.
---

# GpuSpine: Develop the Plugin

Maintenance guide for edits under this plugin root. Spine runtime paths below are
upstream relative (`spine-unity/Components/...`, `spine-csharp/...`).

Integration contracts live in the sibling skill `gpuspine-use-plugin`. Current-contract
overrides: [`../../AGENTS.md`](../../AGENTS.md).

## Four-layer architecture

```
SkeletonAnimation (unmodified Spine runtime)
  |
  | 1. CPU PRESERVATION — Runtime/GpuSkeletonRenderer.cs
  |    updateMode = EverythingExceptMesh; MeshRenderer.forceRenderingOff.
  |    AnimationState, mixing, events, physics, bone worlds keep running.
  v
  2. BAKING (editor, one-shot, manual) — Editor/GpuSpineBakerEditorUtility.cs,
     Editor/GpuSpineEditorMenu.cs, Editor/GpuSkeletonRendererEditor.cs,
     Runtime/Baking/GpuSpineAuditor.cs, GpuSpineBaker.cs, GpuSpineOrderBaker.cs,
     GpuSpineBakeKey.cs
     Audit -> bind-pose entry per skin combo -> DrawOrderLayouts + clip capacity
     -> GpuSpineBakedData sub-asset. Semantics: references/skinning-semantics.md
  |
  | 3. UPLOAD (per frame, per instance) — GpuSkeletonRenderer
  |    After UpdateComplete: bone palette, dyn-slot variants, deform, slot colors,
  |    clip ranges. Version counters gate GPU copies.
  v
  4. SUBMISSION (per camera) — GpuSkinningManager, GpuSpineCameraFrame, GpuSpineBatch
     beginCameraRendering: shared upload groups, slice args, Graphics.RenderMeshIndirect.
     Layouts: references/buffer-layouts.md
```

| File | Role |
|---|---|
| `Runtime/GpuSkeletonRenderer.cs` | Admittance, CPU/GPU switch, per-instance export |
| `Runtime/GpuSpineRuntimeSwitch.cs` | Host CPU/GPU toggle set (`IncludeInRuntimeSwitch`) |
| `Runtime/GpuSpineDiagnostics.cs` | `EnableLogging` / `EnableAutomaticValidation` consts |
| `Runtime/Core/GpuSkinningManager.cs` | Play-mode hub; `GetBatches(camera[, source])`; lifecycle counters |
| `Runtime/Core/GpuSpineCameraFrame.cs` | Per-camera groups, idle pool, `RenderMeshIndirect` |
| `Runtime/Core/GpuSpineBatch.cs` | Shared upload group + draw slices |
| `Runtime/Core/GpuSpineInstanceData.cs` | 96-byte instance struct (3 affine rows) |
| `Runtime/Core/GpuSpineClippingState.cs` | Per-instance clip triangles / slot ranges |
| `Runtime/Baking/GpuSpineBaker.cs` | Pure bake; `BakeFormatVersion = 5` |
| `Runtime/Baking/GpuSpineOrderBaker.cs` | Index-only draw-order layouts + clip capacity |
| `Editor/GpuSpineBakerEditorUtility.cs` | Persist, fingerprint v2, ForceRebake |
| `Editor/GpuSkeletonRendererEditor.cs` | Inspector `Bake Skeleton Data` when no container |
| `Editor/GpuSpineEditorMenu.cs` | Bake All / Rebake Selected / Force Rebake / audit dump |
| `Runtime/Shaders/SpineGpuSkinning.hlsl` | `GpuSpineSkinToWorld` (8 args) + `GpuSpineClip` |

## Per-frame data flow

1. Spine updates bones / slots / deform / colors / draw order.
2. `OnSkeletonUpdateComplete`:
   - recompute skin hash + draw-order hash;
   - on change, `FindEntry` then `FindOrderedEntry` (or CPU fallback);
   - export palette / dyn slots / deform / colors / clip; bump versions only when values change (deform and slot colors compare before bumping).
3. `GpuSkinningManager.BeginCamera`:
   - per-camera `Prepare`;
   - upload a channel only when membership/order/count changed or that instance version differs;
   - instance transform/color/custom is untracked and uploads every frame;
   - submit with `Graphics.RenderMeshIndirect`; custom passes reuse the returned material/args pair.

## Design invariants

- Runtime never bakes. Miss = warning (if logging) + CPU path.
- No Spine source edits. Cut is `EverythingExceptMesh` + `forceRenderingOff`. `OnDisable` restores both.
- Buffer bindings live on the cloned batch material. Rebind after every capacity growth, including every offset material.
- C# structs match HLSL field-for-field. `GpuSpineInstanceData` is 96 bytes, not a 112-byte `Matrix4x4`. HLSL rebuilds `float4x4` from three rows + `(0,0,0,1)`.
- Keys are `GpuSpineBakeKey` FNV-1a over resolved attachment **Name**s. Editor and runtime must share that implementation.
- Resource group key = ResourceOwner + actual Mesh + atlas material + override + render state. Layouts only change index ranges. Multi-segment characters draw character-then-segment; do not merge across characters.
- Args slots are unique per (geometry range, instance range) in one frame. A single-source `GetBatches(camera, source)` query must not overwrite the merged draw args.
- Transparent order is the instance write order inside a group.

## Pitfalls

1. **Shared page materials.** Spine flips `enableInstancing` on shared page materials. Batch materials are always clones.
2. **`OnBecameVisible` resets `updateMode`.** Disabled automatic draws + `LateUpdate` re-force `EverythingExceptMesh`. External `Renderer.enabled` maps to `Visible`.
3. **Import does not bake.** There is no AssetPostprocessor. GPU data is created only by Inspector `Bake Skeleton Data`, `Bake All Skeleton Data`, or `Rebake Selected`. Fingerprint v2 still skips unchanged sources; it excludes the SkeletonDataAsset itself because `Rebake` saves that file.
4. **`Skin.AddSkin` order is the content key.** Do not reimplement `GpuSpineBaker.BuildEffectiveSkin`.
5. **MaterialOverride is same-shader-family only.** `ResolveMaterialShader` uses the override shader only when `page.shader == MaterialOverride.shader`; otherwise `DefaultShader`.
6. **Stale containers without layouts.** `ResolveEntry` returns null when `DrawOrderLayouts` is empty and the audit has draw-order or clipping. Force-rebake. Do not revive `AllowDrawOrderTimeline` as a gate.
7. **`IgnoreClipping` is a clip bypass**, not a CPU opt-in. Null `Clipping` + zero ranges. `GpuSpineBatch` must tolerate `renderer.Clipping == null`.
8. **Name vs placeholder key.** Dynamic slots and deform segments match `Attachment.Name` (JSON `name`), never the skin key.

## Verification

Do not add TEST scripts or test asmdefs.

1. **Compile.** After a host Unity compile, reuse the current `GpuSpine.Runtime.rsp` / `GpuSpine.Editor.rsp`. Include `GpuSpineDiagnostics.cs`. Do not write machine install paths into docs.
2. **Smoke.** Real scene, Play Mode. Toggle the component. `IsGpuActive` follows. Exercise an `AttachmentTimeline` and a `DrawOrderTimeline`.
3. **A-B.** Same pose, component off vs on. Checklist: `references/skinning-semantics.md`.
4. **Frame Debugger.** `RenderMeshIndirect` count equals submitted slices, not skeleton count. Confirm args instance count and bound buffers. After a code change, confirm `Library/ScriptAssemblies/GpuSpine.Runtime.dll` is newer than the sources before Play.

Continuous diagnostics: `GpuSkinningManager.GetLifecycleSnapshot()` (value type). `GetLifecycleCounters()` still returns the original 7-int array for compatibility; do not use it for per-frame sampling (it allocates).

## Diagnostics gates

Same rules as the use skill: two consts on `GpuSpineDiagnostics`, both default false.
`EnableLogging` is not a validation kill switch. Host auto-smoke must check
`EnableAutomaticValidation` before EditorPrefs or command-line flags.

## Shared-resource maintenance

- `GpuSpineBakedEntry.ResourceOwner` is identity only; the group key also includes the actual Mesh.
- `ChangeLayout` keeps members when the page set is compatible; otherwise full unregister/register.
- `GpuSpineSliceKey` includes indexStart/indexCount/start/count. `frameSlices` clears each frame; `slicePool` grows to the draw peak.
- A slice owns args only. Offset materials belong to the group and are destroyed once.
- Capacity growth invalidates upload versions and rebinds every offset material.
- Idle pool cap counts resource groups (64), not layout segments.

## Git commits

Use English Conventional Commits (`type(scope): description`). See `AGENTS.md`.

## References

- `references/skinning-semantics.md`
- `references/buffer-layouts.md`
- Sibling use skill for public component / fallback / shader contracts
