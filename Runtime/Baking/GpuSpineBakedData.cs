using System;
using System.Collections.Generic;
using UnityEngine;
using Spine.Unity;

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
		public int FormatVersion;
		public SkeletonDataAsset SourceAsset;
		public Shader DefaultShader;
		public bool IsCompatible => FormatVersion == GpuSpineBaker.BakeFormatVersion;

		/// <summary>AssetDatabase GUID of the owning SkeletonDataAsset (editor-time bookkeeping).</summary>
		public string SkeletonDataAssetGuid;
		/// <summary>Display name of the owning SkeletonDataAsset, written at bake time. Runtime code
		/// uses it to resolve the container without AssetDatabase (e.g. via a cached
		/// Resources.FindObjectsOfTypeAll lookup).</summary>
		public string SkeletonDataAssetName;
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
	/// TEXCOORD0 = atlas uv, COLOR = attachment color with its raw alpha (additive slots no longer
	/// bake alpha 0; the additive flag is a vertex attribute instead),
	/// TEXCOORD1 = (vx1, vy1, vx2, vy2), TEXCOORD2 = (vx3, vy3),
	/// TEXCOORD3 = 4 bone indices (integers stored as floats), TEXCOORD4 = 4 normalized weights,
	/// TEXCOORD5 = (dynSlotId, variantId) for dynamic slot variant vertices, (-1, -1) for static ones,
	/// TEXCOORD6 = (deformOffset, deformMode, additiveFlag): float2 index into the slot's segment of
	/// the per-instance deform buffer, the deform application mode, and 1 for additive-blend slots;
	/// (-1, -1, additiveFlag) for vertices of non-deform slots. deformMode 0 = absolute replacement
	/// (unweighted attachment), 1 = offset add (weighted attachment; the vertex stores influence 0's
	/// index and influence i reads deformOffset + i, relying on the per-influence deform elements
	/// being consecutive in Vertices expansion order).
	/// TEXCOORD7 = slot index of the vertex (float, SkeletonData.Slots order; -1 never occurs).
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
		public int DynamicSlotCount;
		/// how many of them have variants in this combination.</summary>
		/// <summary>Deform segment layout: one entry per deform slot (audit DeformSlots order), empty
		/// when the skeleton has no deform timelines.</summary>
		public GpuSpineDeformSlotEntry[] DeformSlots;
		/// <summary>Total deform segment length per instance in float2 units (sum of slot capacities);
		/// 0 when the skeleton has no deform slots (no deform buffer is created then).</summary>
		public int DeformStride;
		/// <summary>Number of slots of the skeleton (SkeletonData.Slots.Count): the per-instance slot
		/// color segment length and the TEXCOORD7 domain.</summary>
		public int SlotCount;
		/// <summary>Distinct index layouts, including setup order. All layouts share baked vertices.</summary>
		public GpuSpineDrawOrderLayout[] DrawOrderLayouts;
		/// <summary>Maximum number of triangle vertices used by active clipping attachments.</summary>
		public int ClipVertexCapacity;
		public Bounds[] BoneBounds;
		public bool[] UsedBones;
		[NonSerialized] Dictionary<string, GpuSpineBakedEntry> orderedEntries;
		[NonSerialized] GpuSpineBakedEntry runtimeOwner;
		[NonSerialized] int runtimeUsers;
		[NonSerialized] int lastUsedFrame;
		[NonSerialized] Mesh runtimeLayoutMesh;

		/// <summary>Process-wide counter of combined layout meshes built at runtime (monotonic),
		/// for allocation-spike diagnostics. Read via GpuSkinningManager.GetLifecycleCounters().</summary>
		internal static int LayoutMeshesBuilt;

		/// <summary>Returns the layout view for a runtime draw-order key. All layouts share the baked
		/// vertex streams: the first lookup builds ONE runtime mesh concatenating every baked layout's
		/// index stream, and each layout view is a lightweight clone whose submeshes point at its own
		/// index range (indirect args carry IndexStart/IndexCount, so batches draw the right range from
		/// the shared mesh). This replaces the old per-layout full mesh clones, whose Instantiate churn
		/// produced multi-MiB allocation spikes on draw-order switches.</summary>
		public GpuSpineBakedEntry FindOrderedEntry (string orderKey) {
			if (DrawOrderLayouts == null || DrawOrderLayouts.Length == 0) return null;
			if (orderedEntries == null) orderedEntries = new Dictionary<string, GpuSpineBakedEntry>();
			if (orderedEntries.TryGetValue(orderKey, out GpuSpineBakedEntry cached)) {
				cached.lastUsedFrame = Time.frameCount;
				return cached;
			}
			int layoutOffset = 0;
			foreach (GpuSpineDrawOrderLayout layout in DrawOrderLayouts) {
				if (layout.Indices == null) continue;
				if (layout.Key == orderKey) {
					Mesh layoutMesh = GetOrCreateLayoutMesh();
					if (layoutMesh == null) return null;
					GpuSpineBakedEntry entry = (GpuSpineBakedEntry)MemberwiseClone();
					entry.Mesh = layoutMesh;
					entry.Submeshes = RebaseSubmeshes(layout, layoutOffset);
					entry.DrawOrderLayouts = null;
					entry.orderedEntries = null;
					entry.runtimeLayoutMesh = null;
					entry.runtimeOwner = this;
					entry.runtimeUsers = 0;
					entry.lastUsedFrame = Time.frameCount;
					orderedEntries.Add(orderKey, entry);
					return entry;
				}
				layoutOffset += layout.Indices.Length;
			}
			return null;
		}

		/// <summary>One runtime mesh per entry holding every layout's index stream back to back.
		/// Vertex data comes from the shared baked mesh via a single Instantiate per entry (not per
		/// layout); submesh 0 spans the concatenated indices.</summary>
		Mesh GetOrCreateLayoutMesh () {
			if (runtimeLayoutMesh != null) return runtimeLayoutMesh;
			int total = 0;
			foreach (GpuSpineDrawOrderLayout layout in DrawOrderLayouts)
				if (layout.Indices != null) total += layout.Indices.Length;
			int[] combined = new int[total];
			int offset = 0;
			foreach (GpuSpineDrawOrderLayout layout in DrawOrderLayouts) {
				if (layout.Indices == null) continue;
				Array.Copy(layout.Indices, 0, combined, offset, layout.Indices.Length);
				offset += layout.Indices.Length;
			}
			Mesh mesh = UnityEngine.Object.Instantiate(Mesh);
			mesh.hideFlags = HideFlags.DontSave;
			mesh.subMeshCount = 1;
			mesh.SetTriangles(combined, 0, false, 0);
			runtimeLayoutMesh = mesh;
			LayoutMeshesBuilt++;
			return mesh;
		}

		/// <summary>Copies a layout's submesh partition with IndexStart rebased into the
		/// concatenated index stream of <see cref="GetOrCreateLayoutMesh"/>.</summary>
		static GpuSpineSubmesh[] RebaseSubmeshes (GpuSpineDrawOrderLayout layout, int layoutOffset) {
			GpuSpineSubmesh[] parts = new GpuSpineSubmesh[layout.Submeshes.Length];
			for (int i = 0; i < parts.Length; i++) {
				parts[i] = layout.Submeshes[i];
				parts[i].IndexStart += layoutOffset;
			}
			return parts;
		}

		/// <summary>布局视图共享骨架数据和 GPU 缓冲所有者，索引范围由绘制切片持有。</summary>
		internal GpuSpineBakedEntry ResourceOwner => runtimeOwner ?? this;

		public void RetainRuntimeLayout () { if (runtimeOwner != null) runtimeUsers++; }
		public void ReleaseRuntimeLayout () { if (runtimeOwner != null) runtimeUsers--; }

		/// <summary>Releases the combined layout mesh and all layout views. Layout views own no mesh
		/// of their own, so there is exactly one runtime mesh to destroy per entry.</summary>
		public void ClearRuntimeLayouts () {
			if (runtimeLayoutMesh != null) {
				if (Application.isPlaying) UnityEngine.Object.Destroy(runtimeLayoutMesh);
				else UnityEngine.Object.DestroyImmediate(runtimeLayoutMesh);
				runtimeLayoutMesh = null;
			}
			if (orderedEntries == null) return;
			orderedEntries.Clear();
		}

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

    /// <summary>
    /// One deform slot's segment layout within a baked entry: the slot reserves Capacity float2
    /// entries at Prefix of the per-instance deform segment. The runtime copies slot.Deform verbatim
    /// into the segment (attachment-local float2 order: region corner-slot order, unweighted vertex
    /// order, weighted influence-expansion order; the baked vertex deformOffset values index this
    /// same order, so no runtime reordering is needed) or, when no deform is active, the current
    /// attachment's DefaultValues.
    /// </summary>
    [Serializable]
    public sealed class GpuSpineDeformSlotEntry {
        /// <summary>Index of the slot within SkeletonData.Slots (setup draw order).</summary>
        public int SlotIndex;
        /// <summary>Name of the slot, for diagnostics and inspector display.</summary>
        public string SlotName;
        /// <summary>Reserved segment length in float2 units: the maximum DeformLength over the
        /// slot's resolvable deform attachments.</summary>
        public int Capacity;
        /// <summary>Start of the slot's segment within the per-instance deform segment.</summary>
        public int Prefix;
        /// <summary>One entry per resolvable deform target attachment of the slot.</summary>
        public GpuSpineDeformAttachmentInfo[] Attachments;
    }

    /// <summary>
    /// Fallback deform content of one (slot, attachment) pair, used when no deform timeline has
    /// written slot.Deform for the current attachment. Unweighted attachments: the attachment's local
    /// vertex positions (region: the Offset corner-slot order; unweighted mesh: the Vertices order).
    /// Weighted attachments: all zeros (deform values are per-influence offsets, so zero means
    /// undeformed). Length == DeformLength float2 entries.
    /// </summary>
    [Serializable]
    public sealed class GpuSpineDeformAttachmentInfo {
        /// <summary>Attachment name (the runtime match key against Slot.Attachment.Name).</summary>
        public string AttachmentName;
        /// <summary>Deform data length in float2 units (unweighted: vertex count; weighted: total
        /// influence count).</summary>
        public int DeformLength;
        /// <summary>Fallback values (see class summary), length == DeformLength.</summary>
        public Vector2[] DefaultValues;
    }
}
