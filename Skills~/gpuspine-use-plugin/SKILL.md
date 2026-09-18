---
name: gpuspine-use-plugin
description: Integrate SpineGpuSkinning so spine-unity SkeletonAnimation characters render through GPU vertex skinning and RenderMeshIndirect batches. Covers GpuSkeletonRenderer, editor baking, DeclaredCombos, CPU fallback, custom shader and RenderPass, GpuSpineRuntimeSwitch, GPU clipping, draw-order layouts, MPB instance data, and troubleshooting. Use when adding GpuSkeletonRenderer, diagnosing a CPU-path stay, writing a GPU skinning shader, resubmitting batches, toggling CPU/GPU, or wiring Custom0/Custom1.
---

# GpuSpine: Use the Plugin

Integration guide for the consuming project. Paths are relative to the plugin root
(`SpineGpuSkinning/`). Before coding, also read [`../../AGENTS.md`](../../AGENTS.md) —
that file is the current-contract override if any older copy of this skill disagrees.

## What the plugin does

- Moves per-frame CPU mesh rebuild / vertex upload of a spine-unity `SkeletonAnimation`
  to the GPU vertex shader. Spine source is not modified.
- CPU keeps AnimationState, mixing, events, physics, BoneFollower, and bone evaluation.
- Compatible instances share upload resources per camera. Draw ranges keep per-character
  transparent order. Primary submit is `Graphics.RenderMeshIndirect`.
- Baking is editor-only. A miss or failed audit stays on the stock CPU path
  (`references/fallback-rules.md`).

## Requirements

- Unity 2022.3+
- URP 14
- spine-unity 4.2 (spine-csharp 4.2) already in the project. Spine runtimes are not bundled.

## 1. One-click usage

1. Select the GameObject that has `SkeletonAnimation`.
2. Add component `Gpu Skeleton Renderer` (`Spine/Gpu Skeleton Renderer`).
3. Enter Play Mode.

Source: `Runtime/GpuSkeletonRenderer.cs`. On enable (play mode only):

- look up the editor-baked entry for the current skin combination and draw-order layout;
- on hit: `updateMode = EverythingExceptMesh`, `MeshRenderer.forceRenderingOff = true`;
- after `UpdateComplete`, export the 3x2 bone palette, dyn-slot variants, deform, slot
  colors, and clip ranges;
- submit into per-camera shared upload groups automatically.

Correct:

- `SkeletonAnimation` and `MeshRenderer` on the same GameObject or a child.
- `SkeletonRenderer.zSpacing` left at `0`.
- The referenced `SkeletonDataAsset` has a `GpuSpineBakedData` container. Import does not bake; use Inspector `Bake Skeleton Data`, `Tools/GPUSpineSkin/Bake All Skeleton Data`, or `Rebake Selected`.

Common mistakes:

- Expecting edit-mode GPU preview. The component is idle in edit mode.
- Driving `MeshRenderer.enabled` to hide the skeleton. Use `Visible` (or keep
  `enabled` as visibility; the GPU path maps renderer enabled to `Visible`).
- Re-enabling `updateMode` to `FullUpdate`. `LateUpdate` re-forces `EverythingExceptMesh`.

## 2. Editor baking

Sources: `Editor/GpuSpineBakerEditorUtility.cs`, `Editor/GpuSpineEditorMenu.cs`,
`Editor/GpuSkeletonRendererEditor.cs`.

- Import/update of a `SkeletonDataAsset` does **not** bake. GPU data is created only by
  a manual bake: Inspector `Bake Skeleton Data` (shown when the referenced asset has no
  container), `Tools/GPUSpineSkin/Bake All Skeleton Data`, or `Rebake Selected`.
- A bake audits and writes one entry per skin combination (default + every single skin +
  `DeclaredCombos`).
- Container `GpuSpineBakedData` is a sub-asset of the `.asset`. Failed audits still write
  the container (report only, no meshes).
- No-change check: source fingerprint **v2** (dependency path + length + content hash +
  scale + `BakeFormatVersion`) plus entry keys. File-time-only VCS checkouts do not rebake.
- `DeclaredCombos` survive rebakes. Baking is fixed to `zSpacing = 0`.
- Each entry also stores `DrawOrderLayouts` (setup order plus every unique
  `DrawOrderTimeline` permutation) and `ClipVertexCapacity`.

Menus:

- `Tools/GPUSpineSkin/Bake All Skeleton Data` — Rebake every `SkeletonDataAsset` in the
  project. Already-current containers are skipped. Does not use the Selection.
- `Tools/GPUSpineSkin/Rebake Selected` — skip when fingerprint and keys match.
- `Tools/GPUSpineSkin/Force Rebake Selected` — clear fingerprint first. Use when meshes
  were deleted by hand, or the baked state must rebuild with no source change.
- `Tools/GPUSpineSkin/Log Audit Report` / `Dump Baked Data` / `Inspect Default Shader`.

Host smoke tools hang under `Tools/GPUSpineSkin/Temp`.

Visible MIT example: `Example/GpuSpineComparison.unity`. Rebuild with
`Tools/GPUSpineSkin/Build Comparison Example`.

### DeclaredCombos

Composite skins built with `Skin.AddSkin` must be declared. `SkinNames` order must match
the runtime `AddSkin` order exactly. A miss logs a warning and stays on the CPU path.

## 3. Runtime registration

Resolve order (`GpuSkeletonRenderer`, `GpuSpineBakedRuntime`):

1. Serialized `BakedData` on the component.
2. `GpuSpineBakedRuntime` registry (cleared every play-mode start).
3. Neither → CPU path.

```csharp
using GpuSpine.Baking;
using Spine.Unity;
using UnityEngine;

public sealed class GpuSpineSpawner : MonoBehaviour {
    [SerializeField] SkeletonDataAsset skeletonDataAsset;
    [SerializeField] GpuSpineBakedData bakedData;
    [SerializeField] GameObject skeletonPrefab;

    void Awake () {
        GpuSpineBakedRuntime.Register(skeletonDataAsset, bakedData);
    }
}
```

## 4. Component options

All on `GpuSkeletonRenderer`:

| Member | Effect |
|---|---|
| `MaterialOverride` | Keyword/template for a shader **already on the atlas page materials**. Does not swap an arbitrary shader family onto a different page shader. Unmatched override → `DefaultShader` (`GpuSpine/URP/Skeleton`). Atlas textures stay on the page clone. |
| `BakedData` | Container sub-asset. Null uses the registry. |
| `SortMode` | `CameraDepth` (default), `WorldZBackToFront`, `None`. First submitted instance of a batch decides the mode. |
| `AllowDrawOrderTimeline` | Legacy serialized field. Unused. Draw order is replayed from baked layouts. |
| `IgnoreClipping` | Per-instance GPU clip bypass. Clip ranges upload as zeros; `GpuSpineClip` keeps every fragment. Not a CPU-fallback opt-in. |
| `Visible` | False removes the instance from draws; bone export keeps running. |
| `IsGpuActive` | True while submitted on the GPU path. |
| `WriteInstanceData` | Fill Custom0/Custom1 after transform/color (and after optional MPB copy). |
| `IncludeInRuntimeSwitch` | Self-register with `GpuSpineRuntimeSwitch`. Off by default. Unregister on destroy, not disable. |
| `ApplyCameraRenderingLayerFilter` | Honor URP Camera Rendering Layer Filter. On by default. |
| `CopyPropertyBlockToCustomData` | Copy MeshRenderer MPB into Custom0/Custom1 before `WriteInstanceData`. |
| `Custom0Property` / `Custom0WProperty` / `Custom1Property` | MPB property names for that copy. |
| `CameraFilter` | `Func<Camera,bool>`. Null = every camera. Example uses this to skip SceneView cost. |

Disable or remove the component to restore the CPU path (`updateMode` and
`forceRenderingOff`) with zero residue.

Host CPU/GPU toggle:

```csharp
GpuSpineRuntimeSwitch.Apply(useGpu: true);
var snap = GpuSpineRuntimeSwitch.CaptureState();
GpuSpineRuntimeSwitch.Restore(snap);
```

Only instances with `IncludeInRuntimeSwitch` are in that set.

## 5. CPU fallback (summary)

Full table: `references/fallback-rules.md`.

| Finding | Behavior |
|---|---|
| Dark color timeline (RGBA2 / RGB2) | Hard failure → CPU |
| Texture sequence | Hard failure → CPU |
| Bake-time vertex overflow (> 1,048,576) | That combination has a null mesh |
| Deform timeline | GPU: upload `slot.Deform` |
| Slot color (RGBA / RGB / Alpha) | GPU: upload `slot.R/G/B/A` |
| Attachment timeline | GPU: dynamic slots, unused variants fold |
| Draw-order timeline | GPU: baked layouts. Stale container without layouts → CPU |
| Clipping attachment | GPU: fragment clip. `IgnoreClipping` skips clip. Stale container without layouts → CPU |
| `zSpacing` ≠ baked 0 | CPU |
| No entry / incompatible `FormatVersion` | CPU |
| Vertex with > 4 influences | Truncate + warn; GPU continues |

## 6. Skin switching

The component recomputes the content key every `UpdateComplete` and moves batches.
Single skins need no declaration; composites must be declared.

```csharp
Skin combo = new Skin(baseSkin + "+" + overlaySkin);
combo.AddSkin(skeleton.Data.FindSkin(baseSkin));
combo.AddSkin(skeleton.Data.FindSkin(overlaySkin));
skeleton.SetSkin(combo);
skeleton.SetSlotsToSetupPose();
```

## 7. Custom shader (summary)

Complete sample: `references/integration-examples.md`.

- Include `Runtime/Shaders/SpineGpuSkinning.hlsl`.
- Vertex struct must match the baked layout (`POSITION`, `TEXCOORD0`–`TEXCOORD7`, `COLOR`,
  `SV_InstanceID`).
- First line of the vertex function:

  ```hlsl
  float3 GpuSpineSkinToWorld(float3 positionOS, float4 influence12, float2 influence3,
      float4 boneIndices, float4 boneWeights, float2 dynInfo, float3 deformInfo, uint instanceID);
  ```

  Eight arguments. Do not call a 7-argument form.
- Color: `GpuSpineGetVertexColor` then your tint, then PMA. Additive slots:
  `deformInfo.z > 0.5` → `LinearToSRGB` on alpha, output alpha 0.
- Fragment clip: call `GpuSpineClip(positionSS, slotIndex, instanceID)` in the fragment
  function if the material should honor Spine clipping.
- Put the custom shader on the atlas page materials. Set `MaterialOverride` to a material
  that uses **that same shader** so keywords copy. Never declare or bind
  `_GpuSpineBones` / `_GpuSpineInstances` yourself.
- Instance data is 96 bytes (three affine rows). Read Custom0/Custom1 with
  `GpuSpineGetInstanceCustom0` / `Custom1`.

## 8. Custom RenderPass (summary)

- `GpuSkinningManager.GetBatches(camera)` — all slices this camera submitted this frame.
- `GetBatches(camera, source)` — one character. Must not overwrite the merged-instance args
  already used by the main draw.
- `GetBatches()` — last prepared camera only. Prefer the camera overload.
- Each `GpuSpineBatchInfo`: `Mesh`, `SubmeshIndex` (0 = topology), `Material` (buffers
  already bound), `ArgsBuffer` (real `IndexStart`/`IndexCount`), `Bounds`, `InstanceCount`.
- Resubmit with `CommandBuffer.DrawMeshInstancedIndirect` using the returned pair.
  Primary plugin submit is `Graphics.RenderMeshIndirect`; do not assume GPU instances are
  absent from URP `cullResults`.
- Every `GpuSpineDrawSlice` must rebind `_GpuSpineInstanceFilter = -1` after a material
  clone; otherwise only instance 0 draws.
- Do not cache the returned list across frames.

## 9. Troubleshooting

| Symptom | Fix |
|---|---|
| Invisible / looks like stock Spine | CPU path. Console warnings start with `GpuSkeletonRenderer stays on the CPU path:` (only when `EnableLogging`). Check baked `FormatVersion`, entry key, zSpacing, stale layouts. |
| "no GpuSpineBakedData" | Assign `BakedData` or `GpuSpineBakedRuntime.Register` before enable. |
| "audit failed" | Dark color or Sequence. `Log Audit Report`. |
| "no baked entry" / "missing baked skin or draw-order layout" | Undeclared combo, `AddSkin` order mismatch, or stale bake without layouts. `Force Rebake Selected`. |
| "zSpacing ... differs" | Set `SkeletonRenderer.zSpacing` to 0. |
| "incompatible baked format" | `FormatVersion` ≠ 5. Rebake. |
| Wrong overlap between instances | One `SortMode` per skeleton family. |
| Disappears at distance | Check `Visible` first. Bounds are bind-pose union + deform margin. |
| Dynamic slot empty | Attachment name not a baked variant — rebake. Match `Attachment.Name`, never the placeholder key. |
| Custom shader never appears | Page material shader ≠ `MaterialOverride.shader`, so `DefaultShader` won. |
| Only instance 0 visible after a slice clone | Rebind `_GpuSpineInstanceFilter = -1` and verify `SV_InstanceID` + `_GpuSpineInstanceOffset`. |
| Standalone build blank | Include `GpuSpine/URP/Skeleton` (or the page shader) in the build. |

## 10. Diagnostics gates

`Runtime/GpuSpineDiagnostics.cs` — two `const bool`, both default `false`:

- `EnableLogging` — opted-in runtime/bake log calls. Off does not stop validation.
- `EnableAutomaticValidation` — host smoke / screenshot / A-B gate. Host Bootstrap must
  return before creating objects. Scene validators disable themselves in `Start`.
- Production GPU registration, baking, and menus are not behind the validation gate.
- After flipping a const, wait for the host assembly to recompile.

## Host cases (xx)

Fictional host **xx**. Pattern only; do not paste a real project's paths here.

- Wiring shape: `references/host-integration-xx.md`
- Custom Pass redraw + overlay A-B: `references/custom-pass-redraw-xx.md`
- Character bake options: `references/character-setup-xx.md`

## References

- `references/fallback-rules.md`
- `references/integration-examples.md`
- `references/host-integration-xx.md`
- `references/custom-pass-redraw-xx.md`
- `references/character-setup-xx.md`
- Sibling develop skill for bake/buffer contracts
