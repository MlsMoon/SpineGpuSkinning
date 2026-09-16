# SpineGpuSkinning — agent entry

This repository is a Unity plugin: GPU skinning for spine-unity `SkeletonAnimation`.
Read a skill before writing code. Do not invent APIs from memory or from older README copies.

## Route

| Task | Read first |
| --- | --- |
| Add the component, declare skins, custom shader / RenderPass, CPU fallback, runtime CPU/GPU switch | [`Skills~/gpuspine-use-plugin/SKILL.md`](Skills~/gpuspine-use-plugin/SKILL.md) |
| Edit `Runtime/` or `Editor/`, change bake/buffer contracts, debug batches | [`Skills~/gpuspine-develop-plugin/SKILL.md`](Skills~/gpuspine-develop-plugin/SKILL.md) |
| Human overview | [`README.md`](README.md) |

Skills live in `Skills~/` so Unity ignores them when this folder sits under `Assets/`.
Copy a skill folder into the host project's `.agents/skills/` or `.cursor/skills/` if the agent only auto-loads those paths.

## Hard rules

- Do not modify any Spine runtime file.
- Do not add Unity Test scripts, test asmdefs, or Test Runner scaffolding.
- The runtime never bakes. Missing or incompatible baked data stays on the CPU path.
- Verify signatures in this repo before calling them. Cross-session memory is untrusted.
- Host-project smoke tools hang under `Tools/GPUSpineSkin/Temp`, not a new top-level menu.
- Agent-facing text in this repository is English: `AGENTS.md`, `Skills~/`, XML comments,
  and git commit subjects/bodies.

## Git commits

Format: `type(scope): description`

- `type`: `feat` / `fix` / `docs` / `refactor` / `perf` / `test` / `chore` / `style` / `build` / `ci` / `revert`
- `scope` (optional): `gpuspine`, `runtime`, `editor`, `example`, `docs`
- Description: English, imperative, no trailing period, subject ≤ 72 characters
- Examples:
  - `fix(runtime): skip clip-range copy when IgnoreClipping is set`
  - `docs(gpuspine): add AGENTS.md and sync current contract`

## Current contract (do not use older docs)

These facts supersede any README, changelog, or skill text that still mentions the old behavior.

- `GpuSpineBaker.BakeFormatVersion` is **5**. Incompatible containers refuse the GPU path until rebake.
- Primary submit is `Graphics.RenderMeshIndirect`. Custom passes may still resubmit with `CommandBuffer.DrawMeshInstancedIndirect` using the returned material/args pair.
- Draw order is **replayed**. The baker writes `DrawOrderLayouts`; the component hashes the live order and selects a layout. `AllowDrawOrderTimeline` is legacy serialized compatibility only and is not a runtime gate.
- Clipping is **evaluated on the GPU** (`GpuSpineClip` + clip vertex/range buffers) unless `IgnoreClipping` is true. `IgnoreClipping` uploads zero clip ranges (`range.y == 0` keeps every fragment). It is not a CPU-fallback opt-in.
- A container **without** `DrawOrderLayouts` that still reports `HasDrawOrderTimeline` or `HasClipping` is a stale bake: `ResolveEntry` returns null and the instance stays on the CPU path. Force-rebake.
- Deform timelines and slot color timelines (RGBA / RGB / Alpha) are supported. Dark color (RGBA2 / RGB2, tint black) and attachment Sequences remain hard failures.
- `GpuSpineInstanceData` is **96 bytes**: three affine rows + color + Custom0 + Custom1. It is not a 112-byte `Matrix4x4`.
- `GpuSpineSkinToWorld` takes **8** arguments: `positionOS, influence12, influence3, boneIndices, boneWeights, dynInfo, deformInfo, instanceID`.
- Enumerate draws with `GpuSkinningManager.GetBatches(camera)` or `GetBatches(camera, source)`. `GetBatches()` is the last prepared camera only. `SubmeshIndex` is 0; the real range is in args `IndexStart` / `IndexCount`. Do not cache the list across frames.
- `MaterialOverride` does **not** swap in an arbitrary shader family. The resolved shader is `MaterialOverride.shader` only when the atlas page material already uses that same shader; otherwise `GpuSpineBakedData.DefaultShader` (`GpuSpine/URP/Skeleton`). Atlas textures stay on the cloned page material. Custom GPU shaders must already live on the page materials, then assign a `MaterialOverride` that uses the same shader so keywords copy through.
- Source fingerprint is **v2**: dependency path + length + content hash. File-time-only VCS checkouts no longer force rebake. `Force Rebake Selected` is for missing meshes or a rebuild with no source change.
- `GpuSpineDiagnostics.EnableLogging` and `EnableAutomaticValidation` are `const bool` and default `false`. Logging off does not stop validation; validation off does not stop GPU skinning.
- `IncludeInRuntimeSwitch` (off by default) registers with `GpuSpineRuntimeSwitch` for host CPU/GPU toggles. Unregister is on destroy, not disable.
- `CopyPropertyBlockToCustomData` copies MeshRenderer MPB vectors into Custom0/Custom1 before `WriteInstanceData`.
- `CameraFilter` limits which cameras prepare GPU batches. Leave null for every camera.

## Verify after edits

1. Confirm `Library/ScriptAssemblies/GpuSpine.Runtime.dll` is newer than the sources you changed.
2. Play a real scene. Toggle the component: visuals must match, `IsGpuActive` must follow the switch.
3. CPU/GPU A-B the same pose. Frame Debugger: one `RenderMeshIndirect` per submitted slice, not one per skeleton.
