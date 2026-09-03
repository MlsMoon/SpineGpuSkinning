using System;
using System.Collections.Generic;
using UnityEngine;

namespace GpuSpine.Baking {
	/// <summary>
	/// Container of everything baked for one SkeletonDataAsset, stored as a sub-asset of that
	/// SkeletonDataAsset by the editor bake processor; the entry meshes are sub-assets of this
	/// container. One container shows the whole baked state of a skeleton in the Inspector: the graded
	/// audit report, one entry per baked skin combination (default + every single skin + every declared
	/// combination) and the user-editable combination declarations. The runtime never bakes; it only
	/// looks entries up by content key and falls back to the CPU path on a miss.
	/// </summary>
	public sealed class GpuSpineBakedData : ScriptableObject {
		/// <summary>AssetDatabase GUID of the owning SkeletonDataAsset (editor-time bookkeeping).</summary>
		public string SkeletonDataAssetGuid;
		/// <summary>zSpacing the entries were baked with. Baking is fixed to 0 (v1 simplification): every
		/// known skeleton uses 0, and a component whose SkeletonRenderer.zSpacing differs from this value
		/// falls back to the CPU path with a warning.</summary>
		public float BakedZSpacing;
		/// <summary>The graded audit report of the shared SkeletonData, including the dynamic slot table
		/// and the baker-merged truncation count.</summary>
		public GpuSpineAuditReport Audit;
		/// <summary>Editor-time fingerprint of the source dependencies (skeleton file, atlas files,
		/// scale), used by the bake processor to skip no-change rebakes and to terminate the import loop
		/// caused by saving this container.</summary>
		public string SourceFingerprint;
		/// <summary>One entry per baked skin combination: default first, then one per skin, then the
		/// declared combinations. Entries whose <see cref="GpuSpineBakedEntry.Mesh"/> is null record a
		/// bake-time hard failure (e.g. vertex count overflow) for that combination.</summary>
		public List<GpuSpineBakedEntry> Entries = new List<GpuSpineBakedEntry>();
		/// <summary>User-editable skin combination declarations (e.g. base skin + Dirty overlay). The
		/// bake processor bakes one entry per declaration; preserved across rebakes.</summary>
		public List<GpuSpineComboDeclaration> DeclaredCombos = new List<GpuSpineComboDeclaration>();

		/// <summary>Returns the entry with the given content key, or null. Keys are computed with
		/// <see cref="GpuSpineBakeKey"/>; the runtime key for a live skeleton comes from
		/// <see cref="GpuSpineBakedRuntime.ComputeRuntimeKey"/>.</summary>
		public GpuSpineBakedEntry FindEntry (string key) {
			if (key == null) return null;
			for (int i = 0, n = Entries.Count; i < n; i++) {
				GpuSpineBakedEntry entry = Entries[i];
				if (entry != null && entry.Key == key) return entry;
			}
			return null;
		}
	}

	/// <summary>A user-declared skin combination to bake (composite skin semantics: the named skins are
	/// stacked via Skin.AddSkin in array order, later skins overriding earlier ones per placeholder).</summary>
	[Serializable]
	public sealed class GpuSpineComboDeclaration {
		/// <summary>Display name of the baked entry (e.g. "CatS_01+Dirty"). Falls back to the joined
		/// skin names when empty.</summary>
		public string DisplayName;
		/// <summary>Skin names to stack, in AddSkin order. Every name must exist in SkeletonData.Skins.</summary>
		public string[] SkinNames;
	}

	/// <summary>
	/// Baked bind-pose prototype for one (SkeletonData, effective skin) combination — the persisted
	/// successor of the v1 runtime GpuSpinePrototype.
	/// Vertex layout contract (consumed by the GPU skinning shader):
	/// POSITION = local position of influence 0 (x, y) with z = zSpacing * setup draw order index,
	/// TEXCOORD0 = atlas uv, COLOR = attachment color (alpha 0 for additive slots),
	/// TEXCOORD1 = (vx1, vy1, vx2, vy2), TEXCOORD2 = (vx3, vy3),
	/// TEXCOORD3 = 4 bone indices (integers stored as floats), TEXCOORD4 = 4 normalized weights,
	/// TEXCOORD5 = (dynSlotId, variantId) for dynamic slot variant vertices, (-1, -1) for static ones.
	/// Static vertices come first in setup draw order; every dynamic slot variant (including the setup
	/// state attachment) is appended after the static zone and recorded in
	/// <see cref="DynamicVariants"/>. At runtime the instance selects one variant id per dynamic slot
	/// and the vertex shader folds (collapses) every variant vertex that is not selected; a slot with
	/// no attachment folds all variants of its dynamic slot.
	/// </summary>
	[Serializable]
	public sealed class GpuSpineBakedEntry {
		/// <summary>Content key of this (asset, attachment set) combination, stable across sessions.
		/// Computed by <see cref="GpuSpineBakeKey.Compute"/>.</summary>
		public string Key;
		/// <summary>Display name: "default", the single skin name, or the declared combination name.</summary>
		public string DisplayName;
		/// <summary>Effective skin names of this entry (empty array for the default entry).</summary>
		public string[] SkinNames;
		/// <summary>The baked prototype mesh (sub-asset of the container). Null when baking this
		/// combination hit a bake-time hard failure (see the container audit failures).</summary>
		public Mesh Mesh;
		/// <summary>One entry per submesh; submeshes are batch boundaries (one indirect batch each).
		/// Static-zone submeshes come first, dynamic-zone runs (grouped by page material) after them.</summary>
		public GpuSpineSubmesh[] Submeshes;
		/// <summary>Flattened dynamic slot variant table, ordered by (DynSlotId, VariantId). VertexStart
		/// and VertexCount address the appended dynamic vertex zone of <see cref="Mesh"/>.</summary>
		public GpuSpineDynamicSlotVariant[] DynamicVariants;
		/// <summary>Number of bones in the skeleton (must match the bone matrix palette size).</summary>
		public int BoneCount;
		/// <summary>Total vertex count of the baked mesh (static zone + all dynamic variants).</summary>
		public int VertexCount;
		/// <summary>Number of dynamic slots of the skeleton (the DynSlotId domain size), regardless of
		/// how many of them have variants in this combination.</summary>
		public int DynamicSlotCount;
	}

	/// <summary>One submesh of a <see cref="GpuSpineBakedEntry"/>; equals one indirect draw batch.</summary>
	[Serializable]
	public struct GpuSpineSubmesh {
		/// <summary>Atlas page material, i.e. ((AtlasRegion)region).page.rendererObject. May be null when
		/// the attachment region is not an atlas region. This reference is the batch key.</summary>
		public Material PageMaterial;
		/// <summary>First index of this submesh within the combined index buffer.</summary>
		public int IndexStart;
		/// <summary>Index count of this submesh.</summary>
		public int IndexCount;
		/// <summary>True when any slot contributing to this submesh uses BlendMode.Additive (PMA additive
		/// trick: vertex color alpha was baked as 0, matching the CPU path).</summary>
		public bool HasPmaAdditiveSlot;
	}

	/// <summary>
	/// One baked attachment variant of a dynamic slot. The variant's vertices occupy
	/// [VertexStart, VertexStart + VertexCount) of the entry mesh and carry (DynSlotId, VariantId) in
	/// TEXCOORD5. The runtime resolves VariantId by matching Slot.Attachment.Name against
	/// <see cref="AttachmentName"/>; a slot without an attachment folds every variant of its DynSlotId.
	/// </summary>
	[Serializable]
	public struct GpuSpineDynamicSlotVariant {
		/// <summary>Dynamic slot id: index of the slot in the audit's DynamicSlots table (slot index
		/// order), stable for a given SkeletonData.</summary>
		public int DynSlotId;
		/// <summary>Variant id within the dynamic slot (ascending in ordinal attachment name order, only
		/// renderable variants resolvable through the entry's effective skin are numbered).</summary>
		public int VariantId;
		/// <summary>Attachment name of this variant (the runtime match key).</summary>
		public string AttachmentName;
		/// <summary>First vertex of this variant within the entry mesh (dynamic zone, after the static
		/// vertices).</summary>
		public int VertexStart;
		/// <summary>Vertex count of this variant.</summary>
		public int VertexCount;
	}
}
