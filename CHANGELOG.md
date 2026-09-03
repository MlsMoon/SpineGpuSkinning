# Changelog

## [0.1.0] - 2026-09-03

### Added

- `GpuSkeletonRenderer` one-click component: automatic audit, CPU/GPU path switching, bone matrix export, skin tracking and automatic CPU fallback.
- Runtime lazy baker: bind-pose prototype meshes (position/uv/color/bone indices/weights) baked from `SkeletonDataAsset` at runtime, no editor step required.
- `GpuSkinningManager`: palette/instance ComputeBuffers, per-atlas-page batching, configurable depth sorting, `Graphics.DrawMeshInstancedIndirect` submission with no RenderFeature required.
- `SpineGpuSkinning.hlsl` skinning include + generic URP unlit PMA shader.
- Batch source API and per-instance data callback for custom RenderPass / custom shader integration.
- In-package agent skills under `Skills~/` (`gpuspine-use-plugin`, `gpuspine-develop-plugin`).
