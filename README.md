# SpineGpuSkinning

English | [简体中文](Docs/README.zh-Hans.md) | [日本語](Docs/README.ja.md)

GPU skinning for [spine-unity](http://esotericsoftware.com/spine-unity) `SkeletonAnimation` — moves the per-frame CPU mesh rebuild / vertex upload to the GPU vertex shader, without modifying any Spine source.

> CPU keeps bone evaluation (AnimationState, mixing, events, physics, BoneFollower, runtime skinning). GPU takes over vertex skinning, mesh assembly and vertex upload. Compatible instances share upload resources; draw ranges keep per-character transparent order.

**Agents:** start at [`AGENTS.md`](AGENTS.md). Skills (Unity-ignored) live in [`Skills~/`](Skills~/README.md). Machine-readable index: [`llms.txt`](llms.txt). Do not invent APIs from older README copies — `AGENTS.md` is the current-contract override.

## Requirements

- Unity 2022.3+
- Universal Render Pipeline (URP) 14
- spine-unity **4.2** (spine-csharp 4.2) installed in the project. Spine runtimes are **not** bundled; install them yourself and comply with the [Spine Runtimes License](http://esotericsoftware.com/spine-runtimes-license).

## Quick Start

1. Copy the `SpineGpuSkinning` folder anywhere under your project's `Assets/` (e.g. `Assets/Plugins/`).
2. Select the GameObject that has your `SkeletonAnimation`.
3. Add component → `Gpu Skeleton Renderer`.

Import does **not** bake. Bake from the Inspector (`Bake Skeleton Data` appears when the referenced `SkeletonDataAsset` has no container), `Tools/GPUSpineSkin/Bake All Skeleton Data`, or `Rebake Selected`. The container (audit report + one prototype mesh per skin combination, plus draw-order layouts) is a sub-asset of that asset. The component:

- looks up the baked entry for the current skin and draw-order layout (the runtime never bakes; a miss stays on the CPU path);
- switches to GPU when an entry exists — `updateMode` becomes `EverythingExceptMesh` and automatic `MeshRenderer` draws are suppressed;
- exports the 3x2 bone palette, deform, slot colors and clip ranges after `UpdateComplete`;
- submits through per-camera `Graphics.RenderMeshIndirect` groups.

Remove or disable the component to fall back to the stock CPU path — zero residue.

If the audit fails, the instance **stays on the CPU path**. No action required.

## Example

`Example/` ships a Play Mode crowd: **SampleDude**, **SampleRobot**, **ComplexCourier**, and **UltraCourier**. Open `Example/GpuSpineComparison.unity` and press Play.

- Live **FPS** and **ms / frame**, with the last CPU/GPU sample kept for comparison.
- Switch the same crowd between CPU and GPU without resetting animation.
- Default 32 characters; 0–1000 from the UI. **Production 300** is a one-click stress profile.
- Three Spine skins per character. Original SVG, Spine 4.2 exports, prefabs and baked data are included.
- Independent of game frameworks; requires Unity, URP and spine-unity only.

Rebuild with **Tools / GPUSpineSkin / Build Comparison Example**. See `Example/README.md`.

The example restricts GPU submits to its Game Camera via `GpuSkeletonRenderer.CameraFilter` so SceneView / preview cameras do not pay batch-prepare cost. The plugin itself stays multi-camera; production code should set a filter when a renderer belongs to one camera.

## When to use GPU skinning

This plugin is a workload-dependent rendering option, not a universal FPS upgrade.
**CPU skinning can be cheaper for small crowds and low-vertex characters.** Bone evaluation
and AnimationState still run on the CPU. GPU mode adds palette / deform / color / clip
uploads, visibility, sorting, buffers and draw submission.

| Case | What to expect |
| --- | --- |
| Few characters, mostly region attachments, lightweight meshes | Start with the stock CPU path; GPU overhead can exceed the saved mesh work. |
| Many visible instances sharing meshes, skins and atlas materials | A candidate; measure CPU mesh generation/upload savings against plugin overhead. |
| Dense weighted meshes and deforming cloth/accessories | More CPU vertex work can move to the GPU; deform evaluation and uploads remain costs. |
| Frequent skin / attachment / draw-order changes, many materials, interleaved transparency | Batches split; more characters do not imply one draw. |
| Clipping, shadows or custom multipass | Supported paths have extra cost. Verify visual parity first. |
| CPU animation / AI / fill-rate dominate | Moving mesh skinning alone may not help frame time. |

Workloads in the Example:

- **Simple duo**: adventurer + robot, each 20 bones, 16 slots, 159 visible vertices, three skins. Correctness sample, not a speedup proof.
- **Complex courier** (default): 48 bones, 24 slots, about 1170 baked vertices per skin, four deforming weighted cloth meshes, a 24-point non-convex visor clip, attachment / color / draw-order timelines, three skins.
- **Ultra courier**: 100 bones, 26 slots, about 2490 baked vertices per skin. Stress case for bone count and deform upload — not a recommendation to use 100 bones in production.

Compare CPU and GPU with the **same case, count, skin mix, resolution and camera**. Warm up. Check Profiler CPU mesh work, render thread, GPU time, GC and actual draw count. Do not infer lower CPU use from FPS alone. Prefer a Player build for performance decisions.

A same-machine Windows DX11 Player sample (1280×720, 32 UltraCourier) recorded GPU about 84.5–92.7 FPS (10.79–11.83 ms) and CPU about 151.8 FPS (6.59 ms). That count did not cover GPU prepare/upload cost. It is not a promise for other hardware.

## Editor menus

All generic tools hang under **`Tools/GPUSpineSkin`**. Selection items need one or more `SkeletonDataAsset`s; Bake All does not.

| Menu | What it does |
|---|---|
| `Tools/GPUSpineSkin/Bake All Skeleton Data` | Rebake every `SkeletonDataAsset` in the project. Current containers are skipped. |
| `Tools/GPUSpineSkin/Rebake Selected` | Rebake if the source fingerprint or entry keys changed. |
| `Tools/GPUSpineSkin/Force Rebake Selected` | Clear the fingerprint and rebuild even when the no-change check would skip. |
| `Tools/GPUSpineSkin/Log Audit Report` | Log the graded audit without baking. |
| `Tools/GPUSpineSkin/Dump Baked Data` | Log the container, entries and declared combos. |
| `Tools/GPUSpineSkin/Inspect Default Shader` | Log compile status of `GpuSpine/URP/Skeleton`. |

The same Rebake / Audit commands also appear under `Assets/GpuSpine/` when a `SkeletonDataAsset` is selected. Host-project smoke tools should use `Tools/GPUSpineSkin/Temp/...`.

Source fingerprint **v2** hashes dependency **content** (plus path and length), not file write time, so VCS checkouts that only rewrite timestamps do not rebake. Use Force Rebake when meshes were deleted by hand or the baked state must rebuild with no source change.

## Automatic CPU fallback

| Feature | Behavior |
|---|---|
| Deform timeline | Supported: CPU `slot.Deform` uploads every change and applies in the vertex shader before bone weighting |
| Slot color (RGBA / RGB / Alpha) | Supported: every slot `R/G/B/A` uploads and multiplies into vertex color |
| Dark color (RGBA2 / RGB2) | CPU fallback (tint black is not implemented) |
| Texture sequence | CPU fallback |
| No baked entry / incompatible `FormatVersion` (current bake format is 5) | CPU fallback |
| `zSpacing` ≠ baked 0 | CPU fallback |
| Attachment timeline | Supported (dynamic slots): variants pre-baked; unused variants fold in the vertex shader |
| Draw-order timeline | Supported: baked `DrawOrderLayouts` replay the live order. A stale container without layouts falls back to CPU. `AllowDrawOrderTimeline` is unused legacy. |
| Clipping | Supported: GPU fragment clip. `IgnoreClipping` skips clip for that instance (ranges upload as zeros). A stale container without layouts falls back to CPU. |
| Vertex influenced by > 4 bones | Weights truncated and renormalized; GPU path continues |

Full rule text: `Skills~/gpuspine-use-plugin/references/fallback-rules.md`.

## Custom shader / RenderPass

Include `Runtime/Shaders/SpineGpuSkinning.hlsl` and call `GpuSpineSkinToWorld` (8 arguments) at the top of the vertex function. Call `GpuSpineClip` in the fragment function if the material should honor Spine clipping.

Put a custom GPU shader on the atlas page materials, then assign a `MaterialOverride` that uses **that same shader**. An override with a different shader family does not replace the page shader; `DefaultShader` (`GpuSpine/URP/Skeleton`) wins.

Enumerate live draws with `GpuSkinningManager.GetBatches(camera)` or `GetBatches(camera, source)`. Keep each item's material/args/mesh pairing. `SubmeshIndex` is 0; the real range is in the args buffer. See `Skills~/gpuspine-use-plugin/references/integration-examples.md`.

## Runtime switch and instance data

- `IncludeInRuntimeSwitch` (off by default) registers the instance with `GpuSpineRuntimeSwitch` so a host command can toggle CPU/GPU.
- `CopyPropertyBlockToCustomData` copies MeshRenderer MPB vectors into Custom0/Custom1 before `WriteInstanceData`.

## Diagnostics

`Runtime/GpuSpineDiagnostics.cs` — two compile-time consts, both default `false`:

```csharp
public const bool EnableLogging = false;
public const bool EnableAutomaticValidation = false;
```

- `EnableLogging` gates opted-in log calls. Off does not stop host validation.
- `EnableAutomaticValidation` is the host smoke / screenshot / A-B gate. Check it before EditorPrefs or command-line flags. Production GPU skinning is not behind this gate.
- Flip a const and wait for recompile. Do not run automatic screenshots during a performance capture.

## Agent skills

`Skills~/` (Unity-ignored `~` folder):

- `gpuspine-use-plugin` — integrate, fallback, troubleshoot
- `gpuspine-develop-plugin` — architecture, bake semantics, buffers

Copy a skill folder into the host `.agents/skills/` or `.cursor/skills/` if the agent only auto-loads those paths. In this repository, read `AGENTS.md` first.

## Shared upload groups

- Each camera owns resource groups keyed by skeleton ResourceOwner, actual Mesh, atlas page material, override and render state. Layout changes only update index ranges.
- Compatible draw-order changes keep members. Page set / mesh / state changes take the full register path.
- Multi-segment characters always draw character-then-segment. Merge only single-segment, same geometry, contiguous instances.
- Args slots are unique per (geometry, instance range) in one frame. Do not rewrite a slot later in the same frame.
- `GetBatches(camera, source)` returns a redraw-ready view. Callers must keep the returned material/args pair.
- First load of a new skeleton, atlas or camera still allocates. This does not promise game-wide zero GC.

## License

MIT (see `LICENSE`). Spine runtimes remain under the Spine Runtimes License and are not part of this repository.
