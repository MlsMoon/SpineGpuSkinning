# GpuSpine: Host Integration (xx)

Fictional host **xx**. Replace names with the real project. Do not write a real
host's Prefab, shader, or Pass names back into this plugin.

Plugin contracts stay in the parent skill. This file is only the host wiring shape.

## Five host call sites

1. **Character Prefab** — On `Xx` (`Assets/Characters/Xx/Xx.prefab`) add only
   `GpuSkeletonRenderer`. Do not add a second host adapter component.
   Enable `IncludeInRuntimeSwitch` and `CopyPropertyBlockToCustomData`.
   Fill `Custom0` / `Custom1` property names the host MPB already writes
   (xx uses `_XxOriginWS`, `_XxOriginEnabled`, `_XxRenderIdentity`).
   Leave `ApplyCameraRenderingLayerFilter` on.
2. **Page shader** — `XxLit.shader` includes `SpineGpuSkinning.hlsl`, defines the
   GPU keyword, and calls `GpuSpineSkinToWorld` as the first line of every
   submitted vertex function. Passes the character never submits (for example
   unlit depth-only) stay out of the GPU path.
3. **Redraw Passes** — Host mask / post passes disable the current LightMode on
   GPU batch clones, then resubmit with `DrawMeshInstancedIndirect` using
   `GetBatches` materials and args. Keep per-skeleton segment order. Do not
   force every custom Pass onto a plain cull-first path.
4. **Per-renderer lookup** — A mask Pass that needs one skeleton uses
   `GpuSkinningManager.TryGetByRenderer`. Do not keep a second host registry.
5. **MPB driver** — `XxDepthAnchorDriver` still writes the MeshRenderer
   property block. GPU copies those vectors through
   `CopyPropertyBlockToCustomData`. The driver must not call
   `WriteInstanceData`.

## Host glue

- Keep pass-redraw and quality-toggle facades in the **host** assembly
  (`XxPassRedrawAdapter`, `XxSkinningSwitch` → `GpuSpineRuntimeSwitch`).
- Production characters self-register with `IncludeInRuntimeSwitch`. Leave
  plugin `Example/` instances off so host commands do not sweep them.
- Hide a GPU character with layer `0` plus `ApplyCameraRenderingLayerFilter`,
  or set `renderingLayerMask = 0` in the host view. Do not add a plugin-side
  suppression component.
- Billboard / host-only shader globals stay in the host shader. Do not add
  them to this plugin.

## Hard rules

- Do not edit any Spine runtime file.
- Do not add Unity Test scripts or test asmdefs.
- Plugin shaders stay generic. Host-modified lighting shaders stay in the host.
- Bake is manual (Inspector / Bake All / Rebake Selected). Import does not bake.
- Plugin git commits are English Conventional Commits (`AGENTS.md`).
