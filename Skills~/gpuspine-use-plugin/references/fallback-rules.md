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
| Deform slot | DeformTimeline-driven slot | Deform segment layout pre-baked (per-slot prefix/capacity, per-attachment defaults) | GPU path: the CPU-computed `slot.Deform` is uploaded per instance per frame and applied in the vertex shader before the bone weighting |
| Slot color | RGBATimeline / RGBTimeline / AlphaTimeline-driven slot | Nothing extra (all slots' colors ride the buffer anyway) | GPU path: `slot.R/G/B/A` of every slot is uploaded per instance per frame and multiplied into the vertex color |
| Slot color | RGBATimeline / RGBTimeline / AlphaTimeline-driven slot | Nothing extra (all slots' colors ride the buffer anyway) | GPU path: `slot.R/G/B/A` of every slot is uploaded per instance per frame and multiplied into the vertex color |

## Hard failures (audit.Passed = false)

Detected by `GpuSpineAuditor.Audit` unless noted. A failed audit writes the container with
the report only — no entry meshes — and every `GpuSkeletonRenderer` on that skeleton logs
a warning and stays on the CPU path.

| Finding | Why baking breaks | Report text pattern |
|---|---|---|
| Dark color timeline: `RGBA2Timeline`, `RGB2Timeline` | Tint black (second color) is not implemented by the GPU path | `Animation '{anim}' contains a dark color timeline ({type}, slot '{slot}'): tint black is not supported by the GPU path.` |
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

## Deform slots (DeformTimeline support)

A deform hit no longer blocks baking. `slot.Deform` is computed by the CPU `AnimationState`
every frame anyway; the component copies it into a per-instance deform segment
(`Vector2[entry.DeformStride]`) that the batch uploads alongside the bone palette, and the
vertex shader applies it to each influence's local coordinate **before** the bone weighting:

- Unweighted attachment (region / unweighted mesh, mode 0): the deform value replaces the
  local coordinate absolutely (`VertexAttachment.cs:106-108`).
- Weighted mesh (mode 1): the deform value is added per influence (`VertexAttachment.cs:139-147`);
  a vertex stores influence 0's float2 index and influence `i` reads `deformOffset + i`
  (per-influence deform elements are consecutive in `Vertices` expansion order). Vertices
  truncated to 4 influences have deform disabled (deformOffset -1).
- No active deform (`slot.Deform.Count == 0`): the runtime writes the current attachment's
  baked `DefaultValues` (unweighted: local positions; weighted: all zeros = undeformed),
  or all zeros when the attachment is not a baked deform target. Correctness of the
  `Count > 0` match: `DeformTimeline.Apply` only writes when the slot's attachment resolves
  to the timeline's target (`Animation.cs:1773-1776`), and the attachment setter clears
  deform on an attachment change (`Slot.cs:139-156`).
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

## Slot colors (RGBATimeline / RGBTimeline / AlphaTimeline support)

Every slot's `slot.R/G/B/A` is uploaded per instance per frame in the `_GpuSpineSlotColors`
buffer (stride 16, `[instanceID * _GpuSpineSlotCount + slotIndex]`; every vertex carries its
slot index in TEXCOORD7). The vertex shader composes the final color exactly like the CPU
(`MeshGenerator.cs:953-972`): `attachment COLOR x slot color x instance skeleton color`,
then premultiplies rgb by the combined alpha (PMA). Additive-blend slots no longer bake
alpha 0 into COLOR: they carry `additiveFlag = 1` in TEXCOORD6.z, and the shader applies
the CPU's additive trick at draw time (`alpha = LinearToSRGB(alpha)` compensation from
`MeshGenerator.cs:667-670`, premultiply, then `color.a = 0` so
`Blend One OneMinusSrcAlpha` adds fully, `MeshGenerator.cs:955, 964-965`). Dark color
timelines (RGBA2/RGB2, tint black) stay a hard failure.

**Name-vs-key pitfall**: an attachment's `Name` (e.g. `CatS_01/body`, from the JSON `name`
field) can differ from its skin placeholder key (e.g. `body`). The dynamic slot table and
the deform segment resolution both match by `Name` — matching by key silently folds every
dynamic variant and resolves every deform segment empty (the skeleton renders nothing).
