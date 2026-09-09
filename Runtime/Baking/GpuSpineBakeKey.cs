using System;
using System.Globalization;
using Spine;

namespace GpuSpine.Baking {
	/// <summary>
	/// Computes the content key identifying one (SkeletonData, effective skin) combination: a 64-bit
	/// FNV-1a hash over the per-slot resolved setup attachment names, stable across sessions and
	/// machines (unlike reference or instanceID based hashes). Editor baking and runtime lookup MUST use
	/// this single implementation so a key baked into an entry matches the key of a live skeleton whose
	/// effective skin resolves to the same attachments.
	/// <para/>
	/// Hash stream: for each slot in SkeletonData.Slots order (setup draw order):
	/// slotIndex + ':' + (resolvedAttachment?.Name ?? "~null") + ';', where resolvedAttachment is looked
	/// up in the effective skin first with fallback to the default skin — exactly
	/// Skeleton.GetAttachment(slotIndex, attachmentName) semantics. Hashing the UTF-16 code units of
	/// that stream is deterministic within one runtime and equals the UTF-8 byte hash for ASCII names.
	/// </summary>
	public static class GpuSpineBakeKey {
		const ulong OffsetBasis = 14695981039346656037UL;
		const ulong Prime = 1099511628211UL;

		/// <summary>Computes the content key for the given skeleton data and effective skin.</summary>
		/// <param name="data">The skeleton data (shared by all instances of the same asset).</param>
		/// <param name="effectiveSkin">The effective skin (may be a composite Skin built via AddSkin),
		/// or null to resolve through the default skin only.</param>
		public static string Compute (SkeletonData data, Skin effectiveSkin) => ComputeHash(data, effectiveSkin).ToString("X16", CultureInfo.InvariantCulture);

		public static ulong ComputeHash (SkeletonData data, Skin effectiveSkin) {
			if (data == null) throw new ArgumentNullException("data");
			Skin defaultSkin = data.DefaultSkin;
			SlotData[] slots = data.Slots.Items;
			int count = data.Slots.Count;
			ulong hash = OffsetBasis;
			for (int i = 0; i < count; i++) {
				Attachment attachment = null;
				string attachmentName = slots[i].AttachmentName;
				if (attachmentName != null) {
					if (effectiveSkin != null) attachment = effectiveSkin.GetAttachment(i, attachmentName);
					if (attachment == null && defaultSkin != null) attachment = defaultSkin.GetAttachment(i, attachmentName);
				}
				AppendIndex(ref hash, i);
				Append(ref hash, ':');
				string name = attachment != null ? attachment.Name : "~null";
				foreach (char character in name) Append(ref hash, character);
				Append(ref hash, ';');
			}
			return hash;
		}
		static void Append(ref ulong hash, char value) => hash = unchecked((hash ^ value) * Prime);

		static void AppendIndex(ref ulong hash, int value) {
			if (value >= 10) AppendIndex(ref hash, value / 10);
			Append(ref hash, (char)('0' + value % 10));
		}
	}
}
