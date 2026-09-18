# Changelog

## [Unreleased]

### Added

- Agent entry points: `AGENTS.md`, `llms.txt`, and `Skills~/README.md`, so an agent can route to the use/develop skills without guessing APIs.
- Draw-order layout bake (`GpuSpineOrderBaker`) and live-order replay. `AllowDrawOrderTimeline` is unused legacy.
- GPU fragment clipping (`GpuSpineClippingState` + `GpuSpineClip`). `IgnoreClipping` is a per-instance bypass (zero clip ranges), not a CPU-fallback opt-in.
- `GpuSpineRuntimeSwitch` and `IncludeInRuntimeSwitch` for host CPU/GPU toggles.
- `CopyPropertyBlockToCustomData` plus Custom0/Custom1 property names for MPB → instance data.
- `CameraFilter` so a renderer can skip SceneView / preview cameras.
- Example workloads: ComplexCourier, UltraCourier, and a Production 300 stress profile. `Docs/` translations of the README (`zh-Hans`, `ja`).
- Shared per-camera upload groups, draw slices, idle group pool, and combined layout index meshes.
- `GpuSpineDiagnostics` consts (`EnableLogging`, `EnableAutomaticValidation`), both default `false`.
- `GetLifecycleSnapshot()` value-type counters; `GetLifecycleCounters()` kept for compatibility.
- Inspector `Bake Skeleton Data` on `GpuSkeletonRenderer` when the referenced asset has no container.
- `Tools/GPUSpineSkin/Bake All Skeleton Data` rebakes every `SkeletonDataAsset` and skips current containers.

### Changed

- Import of a `SkeletonDataAsset` no longer bakes. GPU data is created only by Inspector bake, Bake All, or Rebake Selected.
- `MaterialOverride` also matches when the page and override shaders share a name (asset-bundle copies).
- Editor menus live under `Tools/GPUSpineSkin` (bake all / rebake / force rebake / audit / dump / inspect) with `Assets/GpuSpine` Project aliases. Host smoke tools hang off `Tools/GPUSpineSkin/Temp`.
- Primary submit is `Graphics.RenderMeshIndirect`. Custom passes may still resubmit with `DrawMeshInstancedIndirect` using the returned material/args pair.
- `GpuSpineInstanceData` is 96 bytes (three affine rows + color + Custom0 + Custom1), not a 112-byte `Matrix4x4`.
- Source fingerprint **v2**: dependency path + length + content hash. Timestamp-only VCS checkouts no longer rebake.
- Instance buffers allocate on first prepare from the actual count (`NextPowerOfTwo`), not a fixed reserve of 32.
- Palette / dyn-slot / deform / color / clip uploads are version-gated. Instance transform/color/custom still upload every frame.
- `BakeFormatVersion` is 5. Incompatible containers stay on the CPU path until rebake.
- `MaterialOverride` applies only when the atlas page material already uses that shader; otherwise `DefaultShader`.
- Deform timelines and RGBA/RGB/Alpha slot-color timelines are supported end to end. Dark color (RGBA2 / RGB2) and Sequences remain hard failures.

### Fixed

- `IgnoreClipping` instances no longer copy from a null `Clipping` state; clip ranges for that instance are cleared.
- RegionAttachment UV pairing matches the CPU fast path (avoids a 90° GPU texture rotate).
- Dynamic-slot and deform tables resolve by attachment `Name`, not the skin placeholder key.

## [0.1.0] - 2026-09-03

### Added

- `GpuSkeletonRenderer` one-click component: automatic audit, CPU/GPU path switching, bone matrix export, skin tracking and automatic CPU fallback.
- Runtime lazy baker (superseded: baking is editor-only on current main).
- `GpuSkinningManager`: palette/instance ComputeBuffers, per-atlas-page batching, configurable depth sorting, indirect submission with no RenderFeature required.
- `SpineGpuSkinning.hlsl` skinning include + generic URP unlit PMA shader.
- Batch source API and per-instance data callback for custom RenderPass / custom shader integration.
- In-package agent skills under `Skills~/` (`gpuspine-use-plugin`, `gpuspine-develop-plugin`).
