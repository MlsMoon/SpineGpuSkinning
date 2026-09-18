# GpuSpine: Custom Pass Redraw (xx)

How host **xx** keeps GPU batches and URP `DrawRenderers` from painting twice,
and how it A-B an overlay skin. Plugin batch APIs stay in the parent skill.

## Double submit

`Graphics.RenderMeshIndirect` batches can still be hit by `context.DrawRenderers`.
Do not assume GPU characters are absent from `cullResults`. Auto draw plus an
explicit redraw stacks attachments and can hide an overlay skin.

Before the automatic `DrawRenderers`, the host adapter turns off the current
`DrawingSettings` LightMode on **cloned** GPU batch materials
(`SetShaderPassEnabled` uses the LightMode name). Explicit
`DrawMeshInstancedIndirect` still picks a `passIndex`.

Keep:

- per-skeleton segment order
- shared atlas page materials untouched
- overlay skins in authored order (do not hoist them)
- no slot z-bias used to hide a duplicate submit

After GPU `DrawRenderers`, a host outline / mask Pass must set its validity
flag. Otherwise later composite Passes treat the mask as missing and the
outline disappears.

## Overlay skin A-B (`XxOverlay`)

`XxOverlay` may add stains or decals, and may `linkedmesh` face parts
from the base skin. A dark eye region can be authored on the base skin; it is
not proof that overlay residue remains.

Compare one `Xx`, one pose, one SceneCamera RT:

`base → overlay GPU → overlay CPU → base`

Use the attachment list and the CPU path as ground truth. A face-edge
difference from `IgnoreClipping` is not a draw-order bug.

Temporary overlay: one character, record and restore, no save, no diagnostic
forced overlay in normal play.

## Other host checks

- Character flickers: check slice `_GpuSpineInstanceFilter`, offset, indirect
  args, and multi-frame zero draws before bones or bounds.
- Cross-workspace A-B: record weather / time, camera context, main light, and
  Volume. Matching static assets does not mean matching runtime.
- Footprints left on a ground mask: check the host mask and color-depth source,
  not the baker.
