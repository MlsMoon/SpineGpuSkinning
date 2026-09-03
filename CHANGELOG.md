# Changelog

## [Unreleased]

### Changed

- Baking moved from runtime to the editor: an `AssetPostprocessor` audits and bakes every imported/updated `SkeletonDataAsset`; the container (`GpuSpineBakedData`: audit report + one entry per skin combination) and its entry meshes are stored as sub-assets of the `SkeletonDataAsset`. The runtime never bakes; a missing entry logs a warning and falls back to the CPU path.
- `AttachmentTimeline` is no longer a hard failure: driven slots become dynamic slots, every attachment variant is pre-baked into the entry (vertices after the static zone, variant id in TEXCOORD5). At runtime each instance resolves one variant id per dynamic slot from `Slot.Attachment` (0xFFFFFFFF = fold all variants when the slot has no attachment or the name is not a baked variant) and uploads it through a per-batch `_GpuSpineDynSlots` structured buffer; the vertex shader folds unselected variant vertices to a single point (zero-area triangles, no fragments).
- `GpuSkeletonRenderer` gained `AllowDrawOrderTimeline` / `IgnoreClipping` options: a hit draw-order-timeline or clipping warning now falls back to the CPU path unless the matching option allows the artifacts.
- `DrawOrderTimeline` and `ClippingAttachment` demoted from hard failures to tolerated warnings.
- Entry lookup keys are cross-session stable content hashes (`GpuSpineBakeKey`, FNV-1a over the per-slot resolved setup attachment names); skin combinations are declared on the container and baked via composite skins.

## [0.1.0] - 2026-09-03

### Added

- `GpuSkeletonRenderer` one-click component: automatic audit, CPU/GPU path switching, bone matrix export, skin tracking and automatic CPU fallback.
- Runtime lazy baker: bind-pose prototype meshes (position/uv/color/bone indices/weights) baked from `SkeletonDataAsset` at runtime, no editor step required.
- `GpuSkinningManager`: palette/instance ComputeBuffers, per-atlas-page batching, configurable depth sorting, `Graphics.DrawMeshInstancedIndirect` submission with no RenderFeature required.
- `SpineGpuSkinning.hlsl` skinning include + generic URP unlit PMA shader.
- Batch source API and per-instance data callback for custom RenderPass / custom shader integration.
- In-package agent skills under `Skills~/` (`gpuspine-use-plugin`, `gpuspine-develop-plugin`).
