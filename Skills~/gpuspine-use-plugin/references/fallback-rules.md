# GpuSpine: CPU Fallback Rules

Graded by `Runtime/Baking/GpuSpineAuditor.cs` and by component admittance
(`Runtime/GpuSkeletonRenderer.cs`). This file is the complete rule set.

## Grade model

| Grade | Baking | Runtime |
|---|---|---|
| Hard failure | Container keeps the report only (or that entry has a null mesh) | GPU path refused |
| Warning | Baked normally | GPU path continues. Draw-order and clipping warnings are informational; they are not opt-in gates |
| Dynamic slot | Every attachment variant pre-baked | Per-instance variant folding in the vertex shader |
| Deform slot | Segment layout pre-baked | `slot.Deform` uploaded and applied before bone weighting |
| Slot color | Nothing extra (every slot rides the buffer) | `slot.R/G/B/A` uploaded and multiplied into vertex color |

## Hard failures (`audit.Passed = false`)

A failed audit writes the container with the report only. Every `GpuSkeletonRenderer`
on that skeleton stays on the CPU path.

| Finding | Why | Report text pattern |
|---|---|---|
| Dark color: `RGBA2Timeline`, `RGB2Timeline` | Tint black is not implemented | `Animation '{anim}' contains a dark color timeline ({type}, slot '{slot}'): tint black is not supported by the GPU path.` |
| `Sequence` on Region/Mesh | UVs change per frame | `'{Region\|Mesh}Attachment' '{att}' (skin '{skin}', slot '{slot}') has a Sequence: uv changes per frame, baked uvs would be invalid.` |
| Vertex overflow (`MaxVertexCount = 1 << 20`) | Baker-side | `Entry '{combo}' would bake {n} vertices (limit 1048576): vertex count overflow.` That entry stores a null mesh. |
| `SkeletonData` null | Nothing to audit | `SkeletonData is null.` |

Deform timelines and RGBA/RGB/Alpha slot-color timelines are **not** failures.

## Tolerated warnings

Warnings never flip `Passed`. Current runtime does **not** require
`AllowDrawOrderTimeline` or `IgnoreClipping` to stay on the GPU path.

| Finding | Runtime | Notes |
|---|---|---|
| `DrawOrderTimeline` | GPU: live order hash selects a baked `DrawOrderLayouts` entry | `AllowDrawOrderTimeline` is unused legacy. Missing layout (stale bake) → CPU |
| `ClippingAttachment` | GPU: `GpuSpineClippingState` + `GpuSpineClip` | `IgnoreClipping` uploads zero ranges (`range.y == 0` keeps fragments). It does not force CPU |
| Vertex with > 4 influences | GPU continues | Strongest 4 kept, weights renormalized; `TruncatedVertexCount` |

Current report text still says "tolerated". Treat the table above as the behavior, not
the historical "unclipped / overlap artifacts" wording in older skill copies.

## Deform slots

`slot.Deform` is computed by CPU `AnimationState`. The component copies it into a
per-instance `Vector2[entry.DeformStride]` segment.

- Unweighted (mode 0): deform replaces the local coordinate (`VertexAttachment.cs:106-108`).
- Weighted (mode 1): deform adds per influence (`:139-147`). Influence `i` reads
  `deformOffset + i`. Vertices truncated to 4 influences have deform disabled (`-1`).
- `slot.Deform.Count == 0`: write the attachment's baked `DefaultValues` (unweighted:
  local positions; weighted: zeros), or zeros when the name is not a baked deform target.
- Match attachments by `Name`, never the skin placeholder key.

## Slot colors

Every slot's `slot.R/G/B/A` uploads into `_GpuSpineSlotColors`
(`[instanceID * _GpuSpineSlotCount + slotIndex]`; TEXCOORD7 carries the slot index).
Shader composition matches CPU PMA (`MeshGenerator.cs:953-972`) including the additive
gamma trick (`:667-670`). Dark color timelines stay a hard failure.

## Runtime admittance (OnEnable, in order)

Messages are prefixed with `GpuSkeletonRenderer stays on the CPU path` and are compiled
out unless `GpuSpineDiagnostics.EnableLogging`.

| Gate | Message |
|---|---|
| No `SkeletonAnimation` / `MeshRenderer` | `... it requires a SkeletonAnimation and a MeshRenderer on the same GameObject or a child.` |
| No `SkeletonDataAsset` | `... no SkeletonDataAsset assigned.` |
| No container | `... no GpuSpineBakedData available ...` |
| `!bakedData.IsCompatible` or `SourceAsset` mismatch | `... incompatible baked format or source asset. Rebake the skeleton.` (`FormatVersion` must equal `GpuSpineBaker.BakeFormatVersion`, currently 5.) |
| Audit null or `Passed == false` | `..., audit failed. First reason: {reason}` |
| `zSpacing != BakedZSpacing` (0) | `... zSpacing {x} differs from the baked zSpacing 0 ...` |
| Skeleton not initialized | `... the skeleton is not initialized.` |
| No entry, or `ResolveEntry` returned null | `... no baked entry for the current skin combination (key {key}).` |

`ResolveEntry`:

1. If `entry.DrawOrderLayouts` is non-empty, return `FindOrderedEntry(orderKey)`.
2. Else if `HasDrawOrderTimeline || HasClipping`, return null (stale bake).
3. Else return the setup-order entry.

There is **no** admittance check on `AllowDrawOrderTimeline` or `IgnoreClipping`.

## Mid-play fallback

Skin or draw-order change without a baked layout restores the CPU path:
`GpuSkeletonRenderer: missing baked skin or draw-order layout.`
With a layout, the instance moves groups — no CPU round-trip.

## AttachmentTimeline → dynamic slots

Not a failure. Driven slots emit no static vertices. Variants bake after the static zone
in `(dynSlotId, variantId)` order (`TEXCOORD5`). Runtime uploads one selected variant id
per slot (`0xFFFFFFFF` = fold all). Unselected variants collapse to a point.

Stale bake (live `Attachment.Name` not in the variant table) folds the slot and, when
logging is on, warns once per (slot, name) per entry.

## What "CPU fallback" means

`updateMode` and `forceRenderingOff` are never touched, or are fully restored. No partial
GPU state remains.
