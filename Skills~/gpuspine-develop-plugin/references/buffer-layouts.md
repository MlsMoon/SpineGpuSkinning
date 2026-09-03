# GpuSpine: Buffer and Vertex Layouts

Byte-exact layouts shared between C# (`Runtime/Core/`) and HLSL
(`Runtime/Shaders/SpineGpuSkinning.hlsl`). Rule: every C# struct is
`[StructLayout(LayoutKind.Sequential)]`, mirrored field-by-field and byte-by-byte by its
HLSL counterpart; buffer strides come from `Marshal.SizeOf`. Change one side and you must
change the other plus the doc comments.

## GpuBoneMatrix — 24 bytes (palette entry)

The bone's 2D world affine matrix in skeleton space, exported from
`Bone.A/B/C/D/WorldX/WorldY` after `SkeletonAnimation.UpdateComplete`
(`Runtime/Core/GpuBoneMatrix.cs`).

| C# field | HLSL field | Type | Offset | Content |
|---|---|---|---|---|
| `Row0` | `row0` | Vector2 / float2 | 0 | (a, b) |
| `Row1` | `row1` | Vector2 / float2 | 8 | (c, d) |
| `Row2` | `row2` | Vector2 / float2 | 16 | (worldX, worldY) |

Apply: `wx = vx * a + vy * b + worldX ; wy = vx * c + vy * d + worldY`
(`GpuSpineApplyBoneMatrix` in the hlsl include).

## GpuSpineInstanceData — 112 bytes (per-instance draw data)

`Runtime/Core/GpuSpineInstanceData.cs`. Indexed by `SV_InstanceID`.

| C# field | HLSL field | Type | Offset | Content |
|---|---|---|---|---|
| `LocalToWorld` | `localToWorld` | Matrix4x4 / **row_major** float4x4 | 0 | `transform.localToWorldMatrix`: skeleton space -> world space |
| `Color` | `color` | Vector4 / float4 | 64 | Skeleton color (skeleton.R/G/B/A) |
| `Custom0` | `custom0` | Vector4 / float4 | 80 | Extension slot, via `GpuSpineInstanceDataWriter` |
| `Custom1` | `custom1` | Vector4 / float4 | 96 | Extension slot, via `GpuSpineInstanceDataWriter` |

**`row_major` is load-bearing**: the C# `Matrix4x4` is uploaded as a raw 64-byte copy;
only `row_major float4x4` matches that byte order, so
`mul(localToWorld, pos)` applies the C# matrix as-is. Dropping `row_major` transposes
every instance transform.

## Structured buffers (material-level bindings)

Bound by `GpuSpineBatch.EnsureCapacity` via `material.SetBuffer` / `material.SetInt`
(`Runtime/Core/GpuSpineBatch.cs`). Rebinding is mandatory after any buffer recreation
(capacity growth). Initial capacity 32 instances, grown to `NextPowerOfTwo(required)`.

| Binding | Type | Layout / index formula |
|---|---|---|
| `_GpuSpineBones` | `StructuredBuffer<GpuBoneMatrix>` | `[instanceID * _GpuSpineBoneCount + boneIndex]` — per-instance palette slice of `Entry.BoneCount` entries |
| `_GpuSpineInstances` | `StructuredBuffer<GpuSpineInstanceData>` (C# buffer type `ComputeBufferType.Default`) | `[instanceID]` |
| `_GpuSpineBoneCount` | `uint` | Palette stride per instance |
| `_GpuSpineDynSlots` | `StructuredBuffer<uint>` | `[instanceID * _GpuSpineDynSlotCount + dynSlotId]` — selected variant id, or `0xFFFFFFFF` to fold every variant of the slot |
| `_GpuSpineDynSlotCount` | `uint` | Dynamic slot count per instance; 0 for static-only entries — the buffer is then left unbound and every shader read is short-circuited by this count (v1 targets Windows/D3D11, where reading an unbound StructuredBuffer additionally returns 0) |
| `_GpuSpineDeform` | `StructuredBuffer<float2>` (stride 8) | `[instanceID * _GpuSpineDeformStride + deformOffset]` — per-instance deform segment of `Entry.DeformStride` float2 entries; per deform slot `Capacity` entries at `Prefix`, copied verbatim from `slot.Deform` or filled with the attachment's baked `DefaultValues` |
| `_GpuSpineDeformStride` | `uint` | Deform segment length per instance; 0 for deform-less entries — the buffer is then left unbound and every shader read is short-circuited by the baked deformOffset of -1 |
| `_GpuSpineSlotColors` | `StructuredBuffer<float4>` (stride 16) | `[instanceID * _GpuSpineSlotCount + slotIndex]` — per-instance slot colors (`slot.R/G/B/A` of every slot, setup-static colors included) |
| `_GpuSpineSlotCount` | `uint` | Slot color segment length per instance (`Entry.SlotCount`); 0 never happens in practice — the slot color multiply is then short-circuited (slot colors effectively all 1) |

Upload cadence (`GpuSpineBatch.SubmitFrame`):

- Palette + dynSlot buffers: only when dirty (sort moved instances, membership version
  changed, instance count changed, or an instance reports `paletteDirty` /
  `dynamicSlotsDirty`). The dynSlot staging shares the palette's reorder triggers because
  both are filled in the same sorted loop: `[instanceIndex * dynamicSlotCount + dynSlotId]`.
  The deform and slot color buffers share the same dirty model and staging layouts
  `[instanceIndex * deformStride]` / `[instanceIndex * slotCount]`; an instance marks
  `deformDirty` and `slotColorsDirty` on every `UpdateComplete` (both segments are
  rebuilt from the live skeleton each frame).
- Instance buffer: every frame (transform/color/custom are untracked).

## Indirect args buffer — uint[5]

`ComputeBufferType.IndirectArguments`, one per batch:

| Slot | Content |
|---|---|
| `args[0]` | Index count per instance (`submesh.IndexCount`) |
| `args[1]` | Instance count — rewritten every frame |
| `args[2]` | Start index location (`submesh.IndexStart`) |
| `args[3]` | Base vertex (`Mesh.GetBaseVertex(submeshIndex)`; 0 for `SetTriangles`-built meshes) |
| `args[4]` | Reserved (0) |

## Baked vertex streams (prototype mesh)

Written by `Runtime/Baking/GpuSpineBaker.cs` (`mesh.SetVertices` + `SetUVs(0..5)` +
`colors32`), consumed by the shader's `Attributes` struct. UInt32 index format when the
entry exceeds 65535 vertices.

| Stream | Type | Content |
|---|---|---|
| POSITION | Vector3 | Local (x, y) of influence 0; z = zSpacing * setup draw order index (baked z layering; the shader passes z through) |
| TEXCOORD0 | Vector2 | Atlas uv, copied verbatim from the attachment |
| COLOR | Color32 | Attachment color with its **raw alpha** (additive slots no longer bake alpha 0; the additive flag moved to TEXCOORD6.z) |
| TEXCOORD1 | Vector4 | (vx1, vy1, vx2, vy2) — local coords of influences 1 and 2 |
| TEXCOORD2 | Vector2 | (vx3, vy3) — local coords of influence 3 |
| TEXCOORD3 | Vector4 | 4 bone indices, integers stored as floats |
| TEXCOORD4 | Vector4 | 4 weights; normalized, empty influences are 0 |
| TEXCOORD5 | Vector2 | (dynSlotId, variantId) for dynamic slot variant vertices; (-1, -1) for static vertices |
| TEXCOORD6 | Vector3 | (deformOffset, deformMode, additiveFlag): float2 index into the instance deform segment, the application mode, and 1 for additive-blend slots; (-1, -1, additiveFlag) for vertices of non-deform slots. deformMode 0 = absolute replacement (unweighted), 1 = offset add (weighted; the vertex stores influence 0's index and influence i reads deformOffset + i, the per-influence deform elements being consecutive in `Vertices` expansion order). The index counts in the slot's attachment-native order (region corner-slot order BL,UL,UR,BR; unweighted vertex order; weighted influence-expansion order), so the runtime uploads `slot.Deform` verbatim with no reordering |
| TEXCOORD7 | Vector2 | (slotIndex, 0): the vertex's slot index in `SkeletonData.Slots` order, addressing the `_GpuSpineSlotColors` segment |

Vertex order: static zone first (setup draw order), then every dynamic slot variant in
(dynSlotId, variantId) order; `GpuSpineBakedEntry.DynamicVariants` records each variant's
`VertexStart`/`VertexCount` inside the dynamic zone.

## Shader-side folding math (for orientation)

- Dynamic slot folding runs first in `GpuSpineSkinPosition`: if
  `_GpuSpineDynSlotCount > 0 && dynInfo.x > -0.5`, an unselected variant vertex returns
  `float3(0,0,0)` — all three vertices of its triangle collapse to one point, the
  triangle degenerates to zero area and produces no fragments.
- Skinned (x, y) keeps the baked z; `GpuSpineSkinToWorld` then applies the instance's
  `localToWorld`.
