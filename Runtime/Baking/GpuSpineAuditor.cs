using System;
using System.Collections.Generic;
using Spine;

namespace GpuSpine.Baking {
	/// <summary>
	/// Admittance audit for GPU baking. Checks a <see cref="SkeletonData"/> (shared by all instances of
	/// the same asset) for features that interact with a statically baked prototype mesh and sorts every
	/// hit into one of three grades:
	/// <list type="bullet">
	/// <item>Hard failures (<see cref="GpuSpineAuditReport.Failures"/>): dark color timelines (RGBA2,
	/// RGB2; tint black is not implemented) and attachment sequences (uvs change per frame).</item>
    /// <item>Warnings (<see cref="GpuSpineAuditReport.Warnings"/>): draw order timelines and clipping
	/// attachments are tolerated; GPU path replays layouts and evaluates clip unless IgnoreClipping.</item>
	/// <item>Dynamic slots (<see cref="GpuSpineAuditReport.DynamicSlots"/>): attachment timelines are
	/// supported by pre-baking every attachment variant of the driven slot (see <see cref="GpuSpineBaker"/>).</item>
	/// <item>Deform slots (<see cref="GpuSpineAuditReport.DeformSlots"/>): deform timelines are supported
	/// by uploading the per-frame deform data with the instance buffers; the vertex shader applies it
	/// before the bone weighting. Slot color timelines (RGBA/RGB/Alpha) are supported the same way via
	/// the per-instance slot color buffer.</item>
	/// </list>
	/// Never throws on audit hits; every hit is recorded in the report.
	/// </summary>
	public static class GpuSpineAuditor {
		/// <summary>Audits one <see cref="SkeletonData"/> and returns the graded report.</summary>
		/// <param name="data">The skeleton data to audit. A null value fails the audit.</param>
		public static GpuSpineAuditReport Audit (SkeletonData data) {
			GpuSpineAuditReport report = new GpuSpineAuditReport { Passed = true };
			if (data == null) {
				Fail(report, "SkeletonData is null.");
				return report;
			}

            HashSet<int> dynamicSlotIndices = new HashSet<int>();
            Dictionary<int, SortedSet<string>> deformSlotNames = new Dictionary<int, SortedSet<string>>();
            AuditTimelines(data, report, dynamicSlotIndices, deformSlotNames);
            AuditAttachments(data, report);
            CollectDynamicSlots(data, report, dynamicSlotIndices);
            CollectDeformSlots(data, report, deformSlotNames);
			return report;
		}

		static void AuditTimelines (SkeletonData data, GpuSpineAuditReport report, HashSet<int> dynamicSlotIndices, Dictionary<int, SortedSet<string>> deformSlotNames) {
			ExposedList<SlotData> slotList = data.Slots;
			Animation[] animations = data.Animations.Items;
			for (int i = 0, n = data.Animations.Count; i < n; i++) {
				Animation animation = animations[i];
				Timeline[] timelines = animation.Timelines.Items;
				for (int t = 0, tn = animation.Timelines.Count; t < tn; t++) {
					Timeline timeline = timelines[t];
						DeformTimeline deform = timeline as DeformTimeline;
						if (deform != null) {
							// Not a failure: deform is supported by uploading the per-frame deform data
							// with the instance buffers and applying it in the vertex shader before the
							// bone weighting. Collect the target attachment names per slot.
							if (deform.Attachment != null && deform.Attachment.Name != null) {
								SortedSet<string> names;
								if (!deformSlotNames.TryGetValue(deform.SlotIndex, out names)) {
									names = new SortedSet<string>(StringComparer.Ordinal);
									deformSlotNames.Add(deform.SlotIndex, names);
								}
								names.Add(deform.Attachment.Name);
							}
							continue;
						}
						AttachmentTimeline attachment = timeline as AttachmentTimeline;
						if (attachment != null) {
							// Not a failure: the driven slot becomes a dynamic slot whose attachment variants
							// are all pre-baked into the prototype.
							dynamicSlotIndices.Add(attachment.SlotIndex);
							continue;
						}
						if (timeline is DrawOrderTimeline) {
							report.HasDrawOrderTimeline = true;
							Warn(report, string.Format(
								"Animation '{0}' contains a DrawOrderTimeline: tolerated, GPU path replays baked draw-order layouts.",
								animation.Name));
							continue;
						}
						if (timeline is RGBA2Timeline || timeline is RGB2Timeline) {
							// Dark-color (tint black) timelines stay a hard failure: tint black is not
							// implemented by the GPU path.
							int slotIndex = ((ISlotTimeline)timeline).SlotIndex;
							Fail(report, string.Format(
								"Animation '{0}' contains a dark color timeline ({1}, slot '{2}'): tint black is not supported by the GPU path.",
								animation.Name, timeline.GetType().Name, SlotName(slotList, slotIndex)));
							continue;
						}
						if (timeline is RGBATimeline || timeline is RGBTimeline || timeline is AlphaTimeline) {
							// Supported: slot.R/G/B/A rides the per-instance slot color buffer every
							// frame (the CPU AnimationState computes it anyway), so a slot color
							// timeline no longer blocks baking.
							continue;
						}
				}
			}
		}

		static void AuditAttachments (SkeletonData data, GpuSpineAuditReport report) {
			HashSet<Attachment> checkedAttachments = new HashSet<Attachment>();
			HashSet<Skin> checkedSkins = new HashSet<Skin>();
			Skin defaultSkin = data.DefaultSkin;
			if (defaultSkin != null && checkedSkins.Add(defaultSkin))
				AuditSkin(data, defaultSkin, checkedAttachments, report);
			Skin[] skins = data.Skins.Items;
			for (int i = 0, n = data.Skins.Count; i < n; i++) {
				Skin skin = skins[i];
				if (skin != null && checkedSkins.Add(skin))
					AuditSkin(data, skin, checkedAttachments, report);
			}
		}

		static void AuditSkin (SkeletonData data, Skin skin, HashSet<Attachment> checkedAttachments, GpuSpineAuditReport report) {
			foreach (Skin.SkinEntry entry in skin.Attachments) {
				Attachment attachment = entry.Attachment;
				if (attachment == null || !checkedAttachments.Add(attachment)) continue;

				if (attachment is ClippingAttachment) {
					report.HasClipping = true;
					Warn(report, string.Format(
						"ClippingAttachment '{0}' (skin '{1}', slot '{2}'): tolerated, GPU path evaluates fragment clipping unless IgnoreClipping is set.",
						attachment.Name, skin.Name, SlotName(data.Slots, entry.SlotIndex)));
					continue;
				}
				RegionAttachment region = attachment as RegionAttachment;
				if (region != null) {
					if (region.Sequence != null)
						Fail(report, string.Format(
							"RegionAttachment '{0}' (skin '{1}', slot '{2}') has a Sequence: uv changes per frame, baked uvs would be invalid.",
							attachment.Name, skin.Name, SlotName(data.Slots, entry.SlotIndex)));
					continue;
				}
				MeshAttachment mesh = attachment as MeshAttachment;
				if (mesh != null && mesh.Sequence != null)
					Fail(report, string.Format(
						"MeshAttachment '{0}' (skin '{1}', slot '{2}') has a Sequence: uv changes per frame, baked uvs would be invalid.",
						attachment.Name, skin.Name, SlotName(data.Slots, entry.SlotIndex)));
			}
		}

		/// <summary>
		/// Builds the dynamic slot table: for every slot driven by an AttachmentTimeline, the union of
		/// attachment names registered for that slot across the default skin and every named skin,
		/// ordinal sorted. Slots are emitted in slot index order so the table position doubles as the
		/// stable dynamic slot id used by the baker's uv6 vertex stream.
		/// </summary>
		static void CollectDynamicSlots (SkeletonData data, GpuSpineAuditReport report, HashSet<int> dynamicSlotIndices) {
			if (dynamicSlotIndices.Count == 0) return;
			List<int> sorted = new List<int>(dynamicSlotIndices);
			sorted.Sort();
			for (int i = 0; i < sorted.Count; i++) {
				int slotIndex = sorted[i];
				SortedSet<string> names = new SortedSet<string>(StringComparer.Ordinal);
				CollectSlotAttachmentNames(data.DefaultSkin, slotIndex, names);
				Skin[] skins = data.Skins.Items;
				for (int s = 0, n = data.Skins.Count; s < n; s++)
					CollectSlotAttachmentNames(skins[s], slotIndex, names);
				string[] attachmentNames = new string[names.Count];
				names.CopyTo(attachmentNames);
				report.DynamicSlots.Add(new GpuSpineDynamicSlotInfo {
					SlotIndex = slotIndex,
					SlotName = SlotName(data.Slots, slotIndex),
					AttachmentNames = attachmentNames
				});
			}
		}

		static void CollectSlotAttachmentNames (Skin skin, int slotIndex, SortedSet<string> names) {
			if (skin == null) return;
			foreach (Skin.SkinEntry entry in skin.Attachments) {
				// Collect the attachment Name (e.g. "CatS_01/body"), not the placeholder key
				// ("body"): the runtime matches variants against Slot.Attachment.Name, which may
				// differ from the key (SkeletonJson loads the JSON name field).
				if (entry.SlotIndex == slotIndex && entry.Attachment != null && entry.Attachment.Name != null)
					names.Add(entry.Attachment.Name);
			}
		}

		/// <summary>
		/// Builds the deform slot table from the per-slot target attachment names collected during the
		/// timeline audit, in slot index order. The table feeds the baker's deform segment layout
		/// (per-slot prefix/capacity) and the inspector.
		/// </summary>
		static void CollectDeformSlots (SkeletonData data, GpuSpineAuditReport report, Dictionary<int, SortedSet<string>> deformSlotNames) {
			if (deformSlotNames.Count == 0) return;
			List<int> sorted = new List<int>(deformSlotNames.Keys);
			sorted.Sort();
			for (int i = 0; i < sorted.Count; i++) {
				int slotIndex = sorted[i];
				string[] attachmentNames = new string[deformSlotNames[slotIndex].Count];
				deformSlotNames[slotIndex].CopyTo(attachmentNames);
				report.DeformSlots.Add(new GpuSpineDeformSlotInfo {
					SlotIndex = slotIndex,
					SlotName = SlotName(data.Slots, slotIndex),
					AttachmentNames = attachmentNames
				});
			}
		}

		static string SlotName (ExposedList<SlotData> slots, int slotIndex) {
			SlotData[] items = slots.Items;
			return slotIndex >= 0 && slotIndex < slots.Count ? items[slotIndex].Name : "#" + slotIndex;
		}

		static void Fail (GpuSpineAuditReport report, string reason) {
			report.Passed = false;
			report.Failures.Add(reason);
		}

		static void Warn (GpuSpineAuditReport report, string warning) {
			report.Warnings.Add(warning);
		}
	}
}
