# GpuSpine: Batch Lifecycle (xx)

Measured host **xx** / character **Xx** numbers. Use them as a scale hint when
changing the idle pool or layout mesh. Do not treat them as current fixed costs.

Plugin invariants stay in the parent skill (`Shared-resource maintenance`).

## Idle groups

Empty batches park in the per-camera idle pool by `BatchKey`
(`MaxIdleBatches = 64`). Overflow evicts oldest-first and `Dispose`s (old
behavior). Reuse is safe because `membershipVersion` dirties uploads and
`EnsureCapacity` rebinds every slice material; a reused batch needs no extra
reset.

Cap 32 broke a churn window on xx (hundreds of parks / few reuses). 64 is the
current plugin default. The cap counts **resource groups**, not layout segments.

## Shared layout mesh

The first query of an `orderKey` on an entry builds one combined mesh
(instantiate shared vertex streams + concatenated layout indices,
`subMeshCount = 1`). Layout views are `MemberwiseClone` objects; Submesh
`IndexStart` is relocated by the layout offset. `BatchInfo.SubmeshIndex` is 0.
Indirect args carry the real `IndexStart` / `IndexCount`.

Do not bring back per-layout full-mesh instantiate or an `EvictUnusedLayout`
of 8. Cache size is the baked layout count.

## xx scale hint

`Xx` sample: tens of entries, tens of distinct `orderKey`s, about twenty
layouts per entry. A batch create was tens of KiB managed. Treat that as
historical capacity, not a live per-batch tax.

On a dozen GPU `Xx` over a short window, a churn pass dropped plugin-side GC
from tens-to-hundreds of KiB/frame to a few KiB/frame, and multi-MiB
per-layout clone spikes disappeared. Steady-state zero alloc is not the same
as zero spike on layout change: count new batches, materials, slices, and
growth separately.

## Diagnostics

- `ProfilerMarker` `GpuSpine.CameraPrepare` isolates `beginCameraRendering`.
- Per-frame sampling: `GetLifecycleSnapshot()` (value type).
- `GetLifecycleCounters()` still returns the 7-int array (ChangeEntry / create /
  destroy / park / reuse / slice / combined-mesh build) but it allocates.
- `EnableLogging` is `const bool = false`. Gate it before building log arguments.
- `Prepare` must enumerate the real `Dictionary`. `IReadOnlyDictionary` boxes.
- Instance buffers allocate `NextPowerOfTwo` on first `PrepareFrame`. Growth
  must invalidate upload versions and rebind every offset material.
- `EnableAutomaticValidation = false` outranks old EditorPrefs / CLI smoke
  flags. Host `Temp/` rigs are not plugin content.

## Cold layout switch

- Group key = ResourceOwner + actual Mesh + atlas material + render state.
- Layout changes only the index range. Compatible `ChangeLayout` keeps members.
- Multi-segment characters still draw character-then-segment (painter order).
- Args slots are unique per frame range. A single-source `GetBatches` query
  must not overwrite live draw args.
- Self-test: GPU bone buffer matches the source, args survive a query, CPU/GPU
  screenshots match. Profile and readback are separate passes.
