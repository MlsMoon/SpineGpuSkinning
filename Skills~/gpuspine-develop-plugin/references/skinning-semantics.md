# GpuSpine: Baking Semantics (1:1 with the Spine CPU Path)

The baker (`Runtime/Baking/GpuSpineBaker.cs`) reproduces, per slot, the data expansion of
the CPU path — `MeshGenerator.BuildMeshWithArrays` and
`VertexAttachment.ComputeWorldVertices` — except that the weighted sum over bone matrices
is deferred to the GPU. Any deviation shows up as misplaced vertices, wrong colors or
wrong draw order. This is the checklist; the Spine references use upstream relative
paths (`spine-unity/Components/...`, `spine-csharp/...`).

## 1. RegionAttachment: 4 vertices, CPU fast-path order

- Bind-pose local coordinates come from `RegionAttachment.Offset[8]`, uvs from
  `UVs[8]` (both already include x/y/rotation/scale and atlas whitespace/rotation
  fixes — `spine-csharp/Attachments/RegionAttachment.cs`, `UpdateRegion`).
- Bone influence: the slot's bone with weight 1 (the unweighted branch of
  `VertexAttachment.ComputeWorldVertices`).
- Offset/UVs index constants: `BLX/BLY=0/1, ULX/ULY=2/3, URX/URY=4/5, BRX/BRY=6/7`.
- **Vertex order pitfall (easiest to get wrong)**: the CPU fast path
  (`spine-unity/Components/MeshGenerator.cs:944-980`) reads positions in slot order
  BL, BR, UL, UR - but the bundled spine-csharp `RegionAttachment.ComputeWorldVertices`
  crosses the corner slots ("Vertex order is different from RegionAttachment.java",
  `RegionAttachment.cs:190`): the BL slot holds the BR offset, UL holds BL, UR holds
  UL and BR holds UR, while the UVs keep their per-slot layout. Pairing each position
  with the uv of the same slot therefore means, against the raw `Offset`/`UVs` arrays:
  **v0=(BR offset, BL uv), v1=(UR offset, BR uv), v2=(BL offset, UL uv), v3=(UL offset,
  UR uv)**, with triangles **`{0,2,1, 2,3,1}`** relative to the attachment's vertex base
  (`MeshGenerator.cs:1115-1123`). The baker replicates exactly this (`GpuSpineBaker.EmitRegion`).
- Do NOT assume slot name == geometric corner: expanding `Offset[BLX],Offset[BRX],...`
  with `UVs[BLX],UVs[BRX],...` rotates the region texture by 90 degrees on the GPU path.
- Do NOT mix in the slow path's `regionTriangles = {0,1,2, 2,3,0}` - that pairs with a
  different write order. Expanding in Offset array order and applying `{0,1,2, 2,3,0}`
  always connects the triangles wrong.

## 2. MeshAttachment, unweighted (Bones == null)

- `Vertices` are x/y interleaved local coordinates, length == `WorldVerticesLength`;
  single influence = slot bone, weight 1.
- uvs come straight from `UVs` (already converted to full-atlas space, 90/180/270 atlas
  rotation handled) — **no additional atlas conversion**.
- Triangles = `Triangles`, indices relative to this attachment's vertex base.
- Vertex count = `WorldVerticesLength >> 1`.
- **Linked meshes**: while `ParentMesh != null`, dereference first — linked meshes share
  their parent's bones/vertices/triangles; UVs and color stay with the (possibly linked)
  attachment itself.

## 3. MeshAttachment, weighted: per-influence local coordinates (not standard LBS)

- **Bones expansion format** (`spine-csharp/Attachments/VertexAttachment.cs:119-124`):
  per vertex, first the influence count n, then n bone indices — `bones[cursor] = n`,
  `bones[cursor+1 .. cursor+n]` = bone indices, then the next vertex. Never assume a
  fixed stride.
- **Vertices expansion format**: 3 floats `(vx, vy, weight)` per influence, sequential.
  Weights are NOT scaled at load time; use as-is.
- **Key difference from standard LBS**: `(vx, vy)` is the vertex position in the **local
  space of that influence's bone** — the same vertex has different local coordinates per
  bone. A "one local coordinate + bone matrices" bake cannot reproduce this.
- **Chosen representation**: fixed 4 influences. Every influence bakes its own local
  coordinate (POSITION + TEXCOORD1/2), its bone index (TEXCOORD3) and its weight
  (TEXCOORD4). Influences ≤ 4 are filled directly, empty slots get weight 0.
- **> 4 influences**: keep the strongest 4 (descending by weight) and renormalize; the
  baker appends a warning and accumulates `TruncatedVertexCount`.
- **GPU replay formula** (1:1 with `VertexAttachment.cs:131-136`), evaluated in the
  vertex shader from the per-frame exported matrices:

  ```
  wx = sum_i (vx_i * a_i + vy_i * b_i + worldX_i) * w_i
  wy = sum_i (vx_i * c_i + vy_i * d_i + worldY_i) * w_i
  ```

## 4. z layering

- Rule: `z = zSpacing * setup draw order index` (the slot index). Baked at setup order;
  the shader keeps z at the baked value (draw order layering).
- The editor bakes with `zSpacing = 0` (v1 simplification, recorded as `BakedZSpacing`
  on the container). The baker still implements the formula — never hardcode 0 in the
  expansion code.
- Runtime draw order changes invalidate the setup-order assumption — covered by the
  audit's DrawOrderTimeline warning.

## 5. Triangle concatenation and submesh = batch boundary

- Slot iteration follows `SkeletonData.Slots` order — the setup pose draw order —
  replicating the CPU per-slot expansion, including skipped slots consuming a z index.
- Triangles of each attachment are appended with the accumulated vertex base added.
- **Submesh boundary = page material change**: when
  `((AtlasRegion)region).page.rendererObject` changes (reference compare), the current
  submesh closes. Each submesh records `PageMaterial`, `IndexStart`, `IndexCount` and
  `HasPmaAdditiveSlot`.
- Each submesh is exactly one indirect draw batch at runtime; the page material
  reference is the batch key ingredient (same page => same material => mergeable).
- Static-zone submeshes come first; dynamic-zone runs (grouped by page material)
  continue seamlessly after them (indices stay contiguous).

## 6. Visibility culling at bake time

- Slots on **inactive bones** emit no vertices (same culling as the CPU path).
  `ComputeActiveBones` is the data-side equivalent of `Skeleton.UpdateCache`: a bone is
  active unless `SkinRequired`; bones contained in the effective skin, plus their
  ancestors, are active.
- Slots with a null setup attachment name, or whose attachment does not resolve through
  the effective skin (+ default skin fallback — exactly
  `Skeleton.GetAttachment(slotIndex, attachmentName)` semantics), emit no vertices.
- Point/BoundingBox/Path/Clipping attachments emit no vertices.
- Dynamic slots emit no static vertices — their setup attachment is just one variant.

## 7. Color: bake only the attachment color

- CPU composition: `alpha = skeleton.A * slot.A * attachment.A`; with PMA,
  `rgb = skeleton.RGB * slot.RGB * attachment.RGB * color.a`; **additive slots get
  `color.a = 0`**. Skeleton/slot colors are per-instance, per-frame dynamic quantities —
  baking them would kill tinting and fades.
- The bake stores only the attachment's own color (`attachment.R/G/B/A`, default white),
  with **alpha forced to 0 for additive slots** (the PMA additive trick; the default
  shader's `Blend One OneMinusSrcAlpha` then degenerates to `One One`).
- Skeleton color rides in the per-instance data (`GpuSpineInstanceData.Color`); the
  shader recomposes the product. Slot color timelines are unsupported → audit hard
  failure.

## 8. Scale is already applied at load

`SkeletonJson`/`SkeletonBinary` multiply vertex x/y by the asset scale at load time
(weights are not scaled). The baker receives an already-scaled vertex stream — **never
apply the scale again**.

## 9. Content key (GpuSpineBakeKey)

- 64-bit **FNV-1a** (offset basis 14695981039346656037, prime 1099511628211), rendered as
  16 hex chars.
- Hash stream: for each slot in `SkeletonData.Slots` order:
  `slotIndex + ':' + (resolvedAttachment?.Name ?? "~null") + ';'`, where the attachment
  resolves through the effective skin first with default skin fallback.
- Editor baking (`GpuSpineBakerEditorUtility`) and runtime lookup
  (`GpuSpineBakedRuntime.ComputeRuntimeKey`) MUST share this single implementation —
  cross-session stability is the whole point (reference- or instanceID-based hashes
  break after reimport).
- Composite skins are built via `Skin.AddSkin` in declaration order; equal content under
  different display names dedupes to the first target at bake time.

## 10. Dynamic slots (AttachmentTimeline support)

- The audit collects every slot driven by an AttachmentTimeline into
  `DynamicSlots` (slot index order — the position doubles as the stable `dynSlotId`),
  each with the ordinal-sorted union of attachment names across the default skin and
  every named skin.
- Per entry, every renderable variant resolvable through the entry's effective skin
  (+ default fallback) bakes after the static zone in `(dynSlotId, variantId)` order;
  variant ids ascend in ordinal attachment-name order.
- Runtime folding semantics (selection value, `0xFFFFFFFF` hidden state, one-time
  missing-variant warning): see
  `../gpuspine-use-plugin/references/fallback-rules.md`.

## 11. Deform slots (DeformTimeline support)

`slot.Deform` is computed by the CPU `AnimationState` every frame anyway (that CPU cost is
kept by design), so the GPU path only transports it: the baker assigns every deform slot a
segment window (`Prefix`/`Capacity`, float2 units; `Capacity` = the slot's maximum deform
attachment length), every vertex of the slot carries `(deformOffset, deformMode)` in
TEXCOORD6, and the runtime uploads `slot.Deform` verbatim into the per-instance segment
before the batch's palette upload. The vertex shader applies the deform data to each
influence's local coordinate **before** the bone weighting:

- Mode 0 (unweighted attachment: region or unweighted mesh): the deform value replaces the
  local coordinate absolutely — `VertexAttachment.cs:106-108` (`bones == null &&
  deformArray.Count > 0 -> vertices = deformArray.Items`).
- Mode 1 (weighted mesh): the deform value is added per influence —
  `VertexAttachment.cs:139-147` (`vertices[b] + deform[f]`, `f` advancing per influence).
  A vertex stores influence 0's float2 index and influence `i` reads `deformOffset + i`;
  correctness relies on the per-influence deform elements being consecutive in `Vertices`
  expansion order (the baker's `influenceStart` cursor). Vertices truncated to 4 influences
  break that consecutiveness and get deformOffset -1 (deform disabled for them).
- deformOffset indexes the slot's **attachment-native** float2 order — region corner-slot
  order BL,UL,UR,BR (region vertex v0=BR, v1=UR, v2=BL, v3=UL maps to indices 3,2,0,1),
  unweighted vertex order, weighted influence-expansion order — which matches the verbatim
  `slot.Deform` layout, so the runtime copies without any reordering.

Runtime fallback when no deform is active (`slot.Deform.Count == 0`): the current
attachment's baked `DefaultValues` (unweighted: local positions; weighted: all zeros,
which means undeformed), or all zeros when the attachment name is not a baked deform
target. The `Count > 0` match is sound because `DeformTimeline.Apply` only writes when the
slot's attachment resolves to the timeline's target (`Animation.cs:1773-1776`) and the
attachment setter clears deform on an attachment change (`Slot.cs:139-156`).

Audit side: deform timelines are collected into `GpuSpineAuditReport.DeformSlots`
(per-slot union of target attachment names, ordinal sorted) and never flip `Passed`.

## 12. Slot colors (RGBATimeline / RGBTimeline / AlphaTimeline support)

The CPU composes vertex colors as `skeleton.RGBA x slot.RGBA x attachment.RGBA` and
premultiplies rgb by the combined alpha (PMA), with additive slots getting their alpha
zeroed **after** the premultiply (`MeshGenerator.cs:953-972`). The GPU path reproduces
this in the vertex shader instead of the bake:

- COLOR bakes the attachment's **raw** alpha; the additive-blend marker moved to
  TEXCOORD6.z (`additiveFlag`).
- Every vertex carries its slot index in TEXCOORD7; every instance uploads the full
  `slot.R/G/B/A` array every frame into `_GpuSpineSlotColors`
  (`[instanceID * _GpuSpineSlotCount + slotIndex]`), so slot color timelines work for
  free (the CPU `AnimationState` writes the same fields), and setup-static slot colors
  need no baking either.
- Shader composition: `c = COLOR x slotColor x instanceColor`; `a = c.a`; for additive
  slots `a = LinearToSRGB(a)` first (the CPU's linear-space compensation,
  `MeshGenerator.cs:667-670`); `c.rgb *= a`; additive slots output `c.a = 0`, which turns
  `Blend One OneMinusSrcAlpha` into a full add (the PMA additive trick,
  `MeshGenerator.cs:955, 964-965`).
- Dark color timelines (`RGBA2Timeline`/`RGB2Timeline`, tint black) stay a hard failure:
  tint black is not implemented.

**Name-vs-key pitfall (both dynamic slots and deform slots)**: an attachment's `Name`
(e.g. `CatS_01/body`, from the JSON `name` field) can differ from its skin placeholder
key (e.g. `body`). Everything matched at runtime against `Slot.Attachment.Name` — the
dynamic slot table (`GpuSpineAuditor.CollectSlotAttachmentNames`) and the deform segment
resolution (`GpuSpineBaker.FindAttachmentByName`) — must use the **Name**, never the
placeholder key, or every variant/segment silently misses (the skeleton renders nothing
when all dynamic variants fold).
