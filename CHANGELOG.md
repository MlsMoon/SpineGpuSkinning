# Changelog

## [Unreleased]

### Added

- `DeformTimeline` (free-form deformation) support, end to end: the audit collects deform slots instead of failing; the baker emits TEXCOORD6 `(deformOffset, deformMode)` plus a per-entry deform segment layout (`GpuSpineDeformSlotEntry`/`GpuSpineDeformAttachmentInfo`, `DeformStride`); the component uploads `slot.Deform` (or the attachment's baked `DefaultValues`) per frame through a new `_GpuSpineDeform` structured buffer; the vertex shader applies the deform data to each influence's local coordinate before the bone weighting (absolute for unweighted attachments, per-influence offsets for weighted meshes). The editor source fingerprint now mixes in `GpuSpineBaker.BakeFormatVersion` so baked-layout changes rebuild stale containers automatically.

### Added

- Slot color timeline (`RGBATimeline`/`RGBTimeline`/`AlphaTimeline`) support, end to end: COLOR bakes the attachment's raw alpha again (the additive marker moved to TEXCOORD6.z), every vertex carries its slot index in TEXCOORD7, and every instance uploads the full `slot.R/G/B/A` array each frame through the new `_GpuSpineSlotColors` structured buffer; the vertex shader composes `attachment COLOR x slot color x instance skeleton color` with the CPU's PMA premultiply and the additive gamma trick (`MeshGenerator.cs:953-972`, `:667-670`). Dark color timelines (`RGBA2Timeline`/`RGB2Timeline`, tint black) remain a hard failure. `BakeFormatVersion` bumped to 2 so containers rebuild automatically. The editor `Rebake` now calls `asset.Clear()` first so json/atlas edits within one editor session can no longer bake stale data.

### Changed

- Baking moved from runtime to the editor: an `AssetPostprocessor` audits and bakes every imported/updated `SkeletonDataAsset`; the container (`GpuSpineBakedData`: audit report + one entry per skin combination) and its entry meshes are stored as sub-assets of the `SkeletonDataAsset`. The runtime never bakes; a missing entry logs a warning and falls back to the CPU path.
- `AttachmentTimeline` is no longer a hard failure: driven slots become dynamic slots, every attachment variant is pre-baked into the entry (vertices after the static zone, variant id in TEXCOORD5). At runtime each instance resolves one variant id per dynamic slot from `Slot.Attachment` (0xFFFFFFFF = fold all variants when the slot has no attachment or the name is not a baked variant) and uploads it through a per-batch `_GpuSpineDynSlots` structured buffer; the vertex shader folds unselected variant vertices to a single point (zero-area triangles, no fragments).
- `GpuSkeletonRenderer` gained `AllowDrawOrderTimeline` / `IgnoreClipping` options: a hit draw-order-timeline or clipping warning now falls back to the CPU path unless the matching option allows the artifacts.
- `DrawOrderTimeline` and `ClippingAttachment` demoted from hard failures to tolerated warnings.
- Entry lookup keys are cross-session stable content hashes (`GpuSpineBakeKey`, FNV-1a over the per-slot resolved setup attachment names); skin combinations are declared on the container and baked via composite skins.

### Fixed

- `GpuSpineBaker.EmitRegion` paired region positions and uvs by slot name, but the bundled spine-csharp `RegionAttachment.ComputeWorldVertices` crosses the corner slots, which rotated `RegionAttachment` textures by 90 degrees on the GPU path; the emitted vertices now pair (BR, UR, BL, UL) offsets with (BL, BR, UL, UR) slot uvs, vertex-for-vertex with the CPU fast path (`MeshGenerator.cs:944-980`, triangles `{0,2,1, 2,3,1}`).
- The dynamic slot table and the deform segment resolution matched skin **placeholder keys** (e.g. `body`) while the runtime matches `Slot.Attachment.Name` (e.g. `CatS_01/body` from the JSON `name` field): every dynamic variant folded (invisible skeleton) and every deform segment resolved empty. Both tables now collect and resolve by attachment `Name`.

## [0.1.0] - 2026-09-03

### Added

- `GpuSkeletonRenderer` one-click component: automatic audit, CPU/GPU path switching, bone matrix export, skin tracking and automatic CPU fallback.
- Runtime lazy baker: bind-pose prototype meshes (position/uv/color/bone indices/weights) baked from `SkeletonDataAsset` at runtime, no editor step required.
- `GpuSkinningManager`: palette/instance ComputeBuffers, per-atlas-page batching, configurable depth sorting, `Graphics.DrawMeshInstancedIndirect` submission with no RenderFeature required.
- `SpineGpuSkinning.hlsl` skinning include + generic URP unlit PMA shader.
- Batch source API and per-instance data callback for custom RenderPass / custom shader integration.
- In-package agent skills under `Skills~/` (`gpuspine-use-plugin`, `gpuspine-develop-plugin`).
