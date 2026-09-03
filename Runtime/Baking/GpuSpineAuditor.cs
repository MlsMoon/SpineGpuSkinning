using System;
using System.Collections.Generic;
using Spine;

namespace GpuSpine.Baking {
	/// <summary>
	/// Admittance audit for GPU baking. Checks a <see cref="SkeletonData"/> (shared by all instances of
	/// the same asset) for features that interact with a statically baked prototype mesh and sorts every
	/// hit into one of three grades:
	/// <list type="bullet">
	/// <item>Hard failures (<see cref="GpuSpineAuditReport.Failures"/>): deform timelines rewrite the
	/// bind-pose vertex stream at runtime; slot color timelines (RGBA, RGB, Alpha and the two dark-color
	/// variants) are not supported by the GPU path yet; attachment sequences change uvs per frame.</item>
	/// <item>Warnings (<see cref="GpuSpineAuditReport.Warnings"/>): draw order timelines and clipping
	/// attachments are tolerated deviations; influence truncation notices are appended by the baker.</item>
	/// <item>Dynamic slots (<see cref="GpuSpineAuditReport.DynamicSlots"/>): attachment timelines are
	/// supported by pre-baking every attachment variant of the driven slot (see <see cref="GpuSpineBaker"/>).</item>
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
			AuditTimelines(data, report, dynamicSlotIndices);
			AuditAttachments(data, report);
			CollectDynamicSlots(data, report, dynamicSlotIndices);
			return report;
		}

		static void AuditTimelines (SkeletonData data, GpuSpineAuditReport report, HashSet<int> dynamicSlotIndices) {
			ExposedList<SlotData> slotList = data.Slots;
			Animation[] animations = data.Animations.Items;
			for (int i = 0, n = data.Animations.Count; i < n; i++) {
				Animation animation = animations[i];
				Timeline[] timelines = animation.Timelines.Items;
				for (int t = 0, tn = animation.Timelines.Count; t < tn; t++) {
					Timeline timeline = timelines[t];
					DeformTimeline deform = timeline as DeformTimeline;
					if (deform != null) {
						string attachmentName = deform.Attachment != null ? deform.Attachment.Name : "?";
						Fail(report, string.Format(
							"Animation '{0}' contains a DeformTimeline (slot '{1}', attachment '{2}'): free-form deformation rewrites the bind-pose vertex stream at runtime.",
							animation.Name, SlotName(slotList, deform.SlotIndex), attachmentName));
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
							"Animation '{0}' contains a DrawOrderTimeline: tolerated, runtime draw order changes may reorder overlapping attachments against the baked setup order.",
							animation.Name));
						continue;
					}
					if (IsSlotColorTimeline(timeline)) {
						int slotIndex = ((ISlotTimeline)timeline).SlotIndex;
						Fail(report, string.Format(
							"Animation '{0}' contains a slot color timeline ({1}, slot '{2}'): slot color animation is not supported by the GPU path yet.",
							animation.Name, timeline.GetType().Name, SlotName(slotList, slotIndex)));
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
						"ClippingAttachment '{0}' (skin '{1}', slot '{2}'): tolerated, clipped regions render unclipped on the GPU path.",
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
				if (entry.SlotIndex == slotIndex && entry.Name != null) names.Add(entry.Name);
			}
		}

		static bool IsSlotColorTimeline (Timeline timeline) {
			// Spine 4.2 slot color timelines: RGBA, RGB, Alpha and the two dark-color variants.
			return timeline is RGBATimeline || timeline is RGBTimeline || timeline is AlphaTimeline
				|| timeline is RGBA2Timeline || timeline is RGB2Timeline;
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
