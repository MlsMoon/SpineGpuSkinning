# GpuSpine: CPU Fallback Rules

Every feature of a skeleton that interacts with a statically baked prototype mesh is
graded by the audit (`Runtime/Baking/GpuSpineAuditor.cs`, report in
`Runtime/Baking/GpuSpineAuditReport.cs`) and by the component's admittance checks
(`Runtime/GpuSkeletonRenderer.cs`). This file is the complete rule set.

## Grade model

| Grade | Meaning | Baking | Runtime behavior |
|---|---|---|---|
| Hard failure | Feature rewrites the baked vertex stream at runtime | Blocked entirely (container keeps the report only); a per-combination overflow blocks just that entry | Component refuses the GPU path |
| Warning | Tolerated deviation | Baked normally | GPU path allowed; draw-order/clipping warnings additionally require an opt-in component option |
| Dynamic slot | AttachmentTimeline-driven slot | Every attachment variant pre-baked | GPU path with per-instance variant folding in the vertex shader |

## Hard failures (audit.Passed = false)

Detected by `GpuSpineAuditor.Audit` unless noted. A failed audit writes the container with
the report only — no entry meshes — and every `GpuSkeletonRenderer` on that skeleton logs
a warning and stays on the CPU path.

| Finding | Why baking breaks | Report text pattern |
|---|---|---|
| `DeformTimeline` | Free-form deformation rewrites the bind-pose vertex stream at runtime (slot.Deform) | `Animation '{anim}' contains a DeformTimeline (slot '{slot}', attachment '{att}'): free-form deformation rewrites the bind-pose vertex stream at runtime.` |
| Slot color timeline: `RGBATimeline`, `RGBTimeline`, `AlphaTimeline`, `RGBA2Timeline`, `RGB2Timeline` | Slot color animation is a per-frame dynamic quantity; the GPU path does not support it yet | `Animation '{anim}' contains a slot color timeline ({type}, slot '{slot}'): slot color animation is not supported by the GPU path yet.` |
| `Sequence` on a `RegionAttachment` or `MeshAttachment` | uvs change per frame; baked uvs would be invalid | `'{Region|Mesh}Attachment' '{att}' (skin '{skin}', slot '{slot}') has a Sequence: uv changes per frame, baked uvs would be invalid.` |
| Vertex count overflow, detected by the baker | Defensive cap `MaxVertexCount = 1 << 20` (1,048,576) per entry (static zone + all dynamic variants) | `Entry '{combo}' would bake {n} vertices (limit 1048576): vertex count overflow.` The entry is recorded with a null mesh; other combinations still bake. |
| `SkeletonData` null | Nothing to audit | `SkeletonData is null.` |

## Tolerated warnings

Warnings never flip `Passed`; baking proceeds. Two of them are gated again at runtime:

| Finding | Runtime default | Opt-in | Consequence of opting in |
|---|---|---|---|
| `DrawOrderTimeline` found | CPU path with a warning | `GpuSkeletonRenderer.AllowDrawOrderTimeline = true` | Runtime draw order changes may reorder overlapping attachments against the baked setup order (overlap artifacts) |
| `ClippingAttachment` in any skin | CPU path with a warning | `GpuSkeletonRenderer.IgnoreClipping = true` | Clipped regions render unclipped |
| Vertex with > 4 bone influences | GPU path continues | — | Baker keeps the strongest 4 influences and renormalizes the weights; count reported as `TruncatedVertexCount` |

Report text patterns:

- `Animation '{anim}' contains a DrawOrderTimeline: tolerated, runtime draw order changes may reorder overlapping attachments against the baked setup order.`
- `ClippingAttachment '{att}' (skin '{skin}', slot '{slot}'): tolerated, clipped regions render unclipped on the GPU path.`
- `MeshAttachment '{att}' (slot '{slot}') has {n} vertex(es) with more than 4 bone influences; kept the strongest 4 and renormalized.`

## Runtime admittance gates (component OnEnable, in evaluation order)

Even with a passed audit, the component refuses the GPU path when any of these hits
(`Runtime/GpuSkeletonRenderer.cs`). All messages are prefixed with
`GpuSkeletonRenderer stays on the CPU path`:

| Gate | Severity | Message |
|---|---|---|
| No `SkeletonAnimation`/`MeshRenderer` on the same GameObject or a child | `Debug.Log` | `... it requires a SkeletonAnimation and a MeshRenderer on the same GameObject or a child.` |
| No `SkeletonDataAsset` assigned | `Debug.Log` | `... no SkeletonDataAsset assigned.` |
| No container: `BakedData` field null and registry miss | `LogWarning` | `... no GpuSpineBakedData available for the SkeletonDataAsset (assign BakedData or register one via GpuSpineBakedRuntime).` |
| Audit null or `Passed == false` | `LogWarning` | `..., audit failed. First reason: {reason}` |
| `HasDrawOrderTimeline` and `!AllowDrawOrderTimeline` | `LogWarning` | `... the skeleton uses a draw order timeline, which can reorder overlapping attachments against the baked setup order. Set AllowDrawOrderTimeline to accept the artifacts.` |
| `HasClipping` and `!IgnoreClipping` | `LogWarning` | `... the skeleton uses a clipping attachment, which the GPU path renders unclipped. Set IgnoreClipping to accept unclipped rendering.` |
| `SkeletonRenderer.zSpacing != BakedZSpacing` (0) | `LogWarning` | `... zSpacing {x} differs from the baked zSpacing 0 (baking is fixed to 0 in v1).` |
| Skeleton not initialized | `Debug.Log` | `... the skeleton is not initialized.` |
| No entry whose content key matches the current skin combination | `LogWarning` | `... no baked entry for the current skin combination (key {key}). Rebake the SkeletonDataAsset or declare the combination on its GpuSpineBakedData.` |

## Mid-play fallback (skin combination change)

The component recomputes the content key every `UpdateComplete`. When the effective skin
combination changes to one without a baked entry, it restores the CPU path and logs:

`GpuSkeletonRenderer fell back to the CPU path: no baked entry for the new skin combination (key {key}).`

With an entry, the instance just moves to the new entry's batches — no CPU round-trip.

## AttachmentTimeline → dynamic slots (not a failure)

An `AttachmentTimeline` never fails the audit. The driven slot becomes a **dynamic slot**:

- Baking: the slot emits no static vertices. Every attachment variant registered for the
  slot (default skin + every named skin, ordinal-sorted) that resolves through the entry's
  effective skin (default skin fallback) and is renderable (Region/Mesh) is baked after the
  static zone, in `(dynSlotId, variantId)` order, and recorded in
  `GpuSpineBakedEntry.DynamicVariants`. Variant vertices carry `(dynSlotId, variantId)` in
  `TEXCOORD5`; static vertices carry `(-1, -1)`.
- `dynSlotId` is the slot's position in the audit's `DynamicSlots` table (slot index
  order) — stable for a given SkeletonData. `variantId` numbers the renderable variants of
  one slot ascending in ordinal attachment-name order.
- Runtime: every `UpdateComplete`, the component resolves `Slot.Attachment.Name` against
  the entry's variant lookup and uploads one selected variant id per dynamic slot through
  the per-batch `_GpuSpineDynSlots` buffer.
- The vertex shader folds every vertex of an unselected variant to a single point; the
  triangle degenerates to zero area and produces no fragments.
- **Hidden state**: a slot with no attachment (timeline keyframe null) selects
  `0xFFFFFFFF` (`FoldAllVariants`), folding every variant of the slot.
- **Stale bake**: an attachment live on the slot whose name is not a baked variant also
  folds the slot (hidden), with a one-time warning per (slot, attachment) per entry:
  `GpuSkeletonRenderer: attachment '{att}' of dynamic slot '{slot}' is not a baked variant; folding all variants of the slot. Rebake the SkeletonDataAsset.`

## What "CPU fallback" means

The instance renders exactly as if the component were not there: `updateMode` and the
`MeshRenderer` are never touched (or fully restored when the fallback happens mid-play or
on disable). There is no partial GPU state, no residual buffer, and no required action —
fixing the cause (rebake, declare the combo, set the option) lets the next enable switch
to the GPU path automatically.
