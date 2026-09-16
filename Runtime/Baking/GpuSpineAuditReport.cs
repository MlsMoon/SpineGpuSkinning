using System;
using System.Collections.Generic;

namespace GpuSpine.Baking {
	/// <summary>
	/// Description of one dynamic slot: a slot whose visible attachment is switched at runtime by an
	/// AttachmentTimeline. The baker pre-bakes every attachment variant of the slot into the prototype
	/// (after the static vertex zone); the runtime folds the unselected variants per instance in the
	/// vertex shader. A slot with no attachment set (timeline keyframe null) is the hidden state and is
	/// expressed by folding all variants, so null never appears in <see cref="AttachmentNames"/>.
	/// </summary>
	[Serializable]
	public sealed class GpuSpineDynamicSlotInfo {
		/// <summary>Index of the slot within SkeletonData.Slots (setup draw order).</summary>
		public int SlotIndex;
		/// <summary>Name of the slot, for diagnostics and inspector display.</summary>
		public string SlotName;
		/// <summary>All attachment names registered for this slot across the default skin and every named
		/// skin (ordinal sorted, deduplicated). This is the data-wide superset; a baked entry keeps only
		/// the names resolvable through its effective skin (plus default skin fallback).</summary>
		public string[] AttachmentNames;
	}

	/// <summary>
	/// Description of one deform slot: a slot whose attachment vertices are driven by a
	/// DeformTimeline. Deform is supported by uploading the per-frame deform data with the
	/// instance buffers and applying it in the vertex shader before the bone weighting, so a
	/// deform hit no longer blocks baking. <see cref="AttachmentNames"/> is the union of deform
	/// target attachment names of the slot across all animations (ordinal sorted, deduplicated);
	/// the baker keeps the entries resolvable through each entry's effective skin.
	/// </summary>
	[Serializable]
	public sealed class GpuSpineDeformSlotInfo {
		/// <summary>Index of the slot within SkeletonData.Slots (setup draw order).</summary>
		public int SlotIndex;
		/// <summary>Name of the slot, for diagnostics and inspector display.</summary>
		public string SlotName;
		/// <summary>All deform target attachment names of the slot across every animation.</summary>
		public string[] AttachmentNames;
	}

	/// <summary>
	/// Serializable, graded result of auditing a <see cref="Spine.SkeletonData"/> for GPU baking
	/// eligibility, stored on the baked-data container asset. Hard failures (dark color timelines,
	/// attachment sequences, vertex count overflow) flip <see cref="Passed"/> to false and block
	/// baking entirely. Deform timelines and slot color timelines (RGBA/RGB/Alpha) are supported via
	/// per-frame uploads (see <see cref="DeformSlots"/>). Tolerable deviations (draw order timelines,
	/// clipping attachments,
	/// influence truncation) are demoted to <see cref="Warnings"/> and do not block baking.
	/// </summary>
	[Serializable]
	public sealed class GpuSpineAuditReport {
		/// <summary>True when no hard failure was found. Warnings never affect this flag.</summary>
		public bool Passed;
		/// <summary>Hard failure reasons (english), one entry per hit.</summary>
		public List<string> Failures = new List<string>();
		/// <summary>Tolerated deviations: draw order timelines, clipping attachments and influence
		/// truncation notices appended during baking.</summary>
		public List<string> Warnings = new List<string>();
		/// <summary>True when any animation contains a DrawOrderTimeline. Tolerated: GPU path
		/// replays baked draw-order layouts. A stale container without layouts falls back to CPU.</summary>
		public bool HasDrawOrderTimeline;
		/// <summary>True when any skin contains a ClippingAttachment. Tolerated: GPU path evaluates
		/// fragment clipping unless IgnoreClipping is set.</summary>
		public bool HasClipping;
		/// <summary>Number of vertices with more than 4 bone influences that were fixed by truncation
		/// (strongest 4 influences kept, weights renormalized) during baking. Filled by the baker, not
		/// the auditor; the editor keeps the maximum across all baked entries (the same attachment
		/// truncates identically in every combination containing it).</summary>
		public int TruncatedVertexCount;
		/// <summary>Slots driven by an AttachmentTimeline, ordered by slot index. Empty when the skeleton
		/// has no attachment timelines.</summary>
		public List<GpuSpineDynamicSlotInfo> DynamicSlots = new List<GpuSpineDynamicSlotInfo>();
		/// <summary>Slots driven by a DeformTimeline, ordered by slot index. Empty when the skeleton
		/// has no deform timelines. Supported: the deform data rides the instance buffers.</summary>
		public List<GpuSpineDeformSlotInfo> DeformSlots = new List<GpuSpineDeformSlotInfo>();
	}
}
