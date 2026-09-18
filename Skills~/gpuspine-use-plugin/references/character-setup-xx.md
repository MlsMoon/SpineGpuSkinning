# GpuSpine: Character Setup (xx)

Bake and component choices for fictional character **Xx**. Numbers are a sample
workload, not plugin clamps.

## Bake options

- Every `Xx` animation has a `DrawOrderTimeline`. Layouts are baked; the
  runtime replays them. `AllowDrawOrderTimeline` is unused legacy and is not
  a gate. Alpha-test + ZWrite on `XxLit` can still resolve intra-skeleton
  depth.
- A face clip slot (`xx_head_mask`) can use per-instance `IgnoreClipping` if
  the host accepts the visual difference. Confirm on device before keeping it.
- FX slots (`xx_fx_a`, `xx_fx_b`, …) are `AttachmentTimeline` attachments.
  They use dynamic slots (pre-baked variants + VS fold). No extra container
  field.
- Skin matrix = N base skins × overlay on/off. Fill `DeclaredCombos`.
  `SkinNames` order must match the host `Skin.AddSkin` order exactly.
- GPU `Xx` may skip shadows in v1. Revisit after a visual pass.
- A roster cap is a **config** number, not a code clamp. Leave buffer headroom
  above that cap.

## Fixed bake contract

- Bake `zSpacing = 0`. A non-zero value to fight z-fighting forces CPU.
- Per-attachment depth bias belongs in the host shader, not the baker.
- Import does not bake. Missing container: Inspector `Bake Skeleton Data`,
  `Tools/GPUSpineSkin/Bake All Skeleton Data`, or `Rebake Selected`.
- Stale container with draw-order / clipping but empty `DrawOrderLayouts`:
  Force Rebake. The instance stays on CPU until then.

## Verify this character

- Play Mode, real scene. Toggle the component. `IsGpuActive` follows.
- Same pose CPU vs GPU. Frame Debugger: one `RenderMeshIndirect` per submitted
  slice, not one per skeleton.
- After a plugin rebuild, confirm `Library/ScriptAssemblies/GpuSpine.Runtime.dll`
  is newer than the sources before Play. Entering Play Mode does not always
  recompile.
