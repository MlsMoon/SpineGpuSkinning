using System.Collections.Generic;
using GpuSpine.Baking;
using GpuSpine.Core;
using Spine;
using Spine.Unity;
using UnityEngine;

namespace GpuSpine {
	/// <summary>
	/// One-component switch that moves a <see cref="SkeletonAnimation"/> from CPU skinning to GPU
	/// skinning: sets updateMode to EverythingExceptMesh (animation state, physics and bone world
	/// transforms keep running on the CPU untouched), disables the MeshRenderer, exports the 3x2 bone
	/// matrices on UpdateComplete and lets <see cref="GpuSkinningManager"/> draw the editor-baked entry
	/// mesh via DrawMeshInstancedIndirect. The component never bakes at runtime: when no usable baked
	/// data exists (no container, failed audit, missing entry, zSpacing mismatch), it logs a warning,
	/// leaves the skeleton on the original CPU path and does nothing. Does nothing at all in edit mode,
	/// so editor previews keep the original path. OnDisable restores the CPU path completely.
	/// </summary>
	[AddComponentMenu("Spine/Gpu Skeleton Renderer")]
	public sealed class GpuSkeletonRenderer : MonoBehaviour {
		/// <summary>Optional custom GPU material template. Its shader replaces the cloned page
		/// material's shader; per-page atlas textures still come from the page material. Null uses
		/// the plugin default shader.</summary>
		[Tooltip("Optional custom GPU material template; its shader is used instead of the plugin default.")]
		public Material MaterialOverride;

		/// <summary>The baked data container of this skeleton (a sub-asset of the SkeletonDataAsset,
		/// produced by the editor bake processor). When null, the component falls back to the
		/// <see cref="GpuSpineBakedRuntime"/> registry (game side registers containers there). When no
		/// container is available at all, the component stays on the CPU path with a warning.</summary>
		[Tooltip("Baked GPU data of this skeleton (sub-asset of the SkeletonDataAsset). Leave empty to use the GpuSpineBakedRuntime registry.")]
		public GpuSpineBakedData BakedData;

		/// <summary>Draw-order strategy of the batches this instance joins. The first submitted
		/// instance of a batch decides the batch's sort mode.</summary>
		[Tooltip("Draw-order strategy of the batches this instance joins.")]
		public GpuSpineSortMode SortMode = GpuSpineSortMode.CameraDepth;

		/// <summary>Allows the GPU path when the audit found a draw order timeline. Runtime draw
		/// order changes may reorder overlapping attachments against the baked setup order; when off
		/// (default) a hit falls back to the CPU path with a warning.</summary>
		[Tooltip("Allow the GPU path despite a draw order timeline (possible overlap-order artifacts). Off: CPU fallback with a warning.")]
		public bool AllowDrawOrderTimeline;

		/// <summary>Allows the GPU path when the audit found a clipping attachment. Clipped regions
		/// render unclipped on the GPU path; when off (default) a hit falls back to the CPU path with
		/// a warning.</summary>
		[Tooltip("Allow the GPU path despite clipping attachments (rendered unclipped). Off: CPU fallback with a warning.")]
		public bool IgnoreClipping;

		/// <summary>Per-frame callback to fill the Custom0/Custom1 slots of this instance's draw data.</summary>
		public event GpuSpineInstanceDataWriter WriteInstanceData;

		SkeletonAnimation skeletonAnimation;
		MeshRenderer meshRenderer;
		UpdateMode originalUpdateMode;
		bool originalRendererEnabled;
		GpuBoneMatrix[] boneMatrices;
		GpuSpineBakedData currentBakedData;
		string lastKey;
		bool gpuActive;
		bool paletteDirty;
		bool visible = true;
		GpuSpineAuditReport lastAudit;
		Dictionary<string, int>[] dynamicVariantLookup; // [dynSlotId] -> attachment name -> variant id
		uint[] dynamicSlotSelection;                    // [dynSlotId] -> variant id or FoldAllVariants
		HashSet<string> warnedMissingVariants;
		bool dynamicSlotsDirty;

		/// <summary>True while this skeleton is submitted through the GPU instanced path.</summary>
		public bool IsGpuActive { get { return gpuActive; } }

		/// <summary>Controls inclusion in the submission set: false removes the instance from the
		/// submitted batches while bone matrix export keeps running (the skeleton stays animated).</summary>
		public bool Visible { get { return visible; } set { visible = value; } }

		/// <summary>The admittance audit evaluated on enable (null while never audited).</summary>
		public GpuSpineAuditReport LastAudit { get { return lastAudit; } }

		void OnEnable () {
			if (!Application.isPlaying) return; // Edit-mode previews keep the original CPU path.
			if (skeletonAnimation == null) {
				skeletonAnimation = GetComponent<SkeletonAnimation>();
				if (skeletonAnimation == null) skeletonAnimation = GetComponentInChildren<SkeletonAnimation>();
			}
			if (meshRenderer == null) {
				meshRenderer = GetComponent<MeshRenderer>();
				if (meshRenderer == null && skeletonAnimation != null) meshRenderer = skeletonAnimation.GetComponent<MeshRenderer>();
				if (meshRenderer == null) meshRenderer = GetComponentInChildren<MeshRenderer>();
			}
			if (skeletonAnimation == null || meshRenderer == null) {
				Debug.Log("GpuSkeletonRenderer stays on the CPU path: it requires a SkeletonAnimation and a MeshRenderer on the same GameObject or a child.", this);
				return;
			}

			skeletonAnimation.Initialize(false); // Idempotent; covers enabling before SkeletonAnimation.Awake.
			SkeletonDataAsset asset = skeletonAnimation.skeletonDataAsset;
			if (asset == null) {
				Debug.Log("GpuSkeletonRenderer stays on the CPU path: no SkeletonDataAsset assigned.", this);
				return;
			}

			// Editor-baked data only; the runtime never bakes. The component field wins over the registry.
			GpuSpineBakedData bakedData = BakedData;
			if (bakedData != null) GpuSpineBakedRuntime.Register(asset, bakedData);
			else GpuSpineBakedRuntime.TryGet(asset, out bakedData);
			if (bakedData == null) {
				Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path: no GpuSpineBakedData available for the SkeletonDataAsset (assign BakedData or register one via GpuSpineBakedRuntime).", this);
				return;
			}

			lastAudit = bakedData.Audit;
			if (lastAudit == null || !lastAudit.Passed) {
				Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path, audit failed. First reason: "
					+ (lastAudit != null && lastAudit.Failures.Count > 0 ? lastAudit.Failures[0] : "unknown"), this);
				return;
			}
			if (lastAudit.HasDrawOrderTimeline && !AllowDrawOrderTimeline) {
				Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path: the skeleton uses a draw order timeline, which can reorder overlapping attachments against the baked setup order. Set AllowDrawOrderTimeline to accept the artifacts.", this);
				return;
			}
			if (lastAudit.HasClipping && !IgnoreClipping) {
				Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path: the skeleton uses a clipping attachment, which the GPU path renders unclipped. Set IgnoreClipping to accept unclipped rendering.", this);
				return;
			}
			if (skeletonAnimation.zSpacing != bakedData.BakedZSpacing) {
				Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path: zSpacing " + skeletonAnimation.zSpacing
					+ " differs from the baked zSpacing " + bakedData.BakedZSpacing + " (baking is fixed to 0 in v1).", this);
				return;
			}

			Skeleton skeleton = skeletonAnimation.Skeleton;
			if (skeleton == null) {
				Debug.Log("GpuSkeletonRenderer stays on the CPU path: the skeleton is not initialized.", this);
				return;
			}
			string key = GpuSpineBakedRuntime.ComputeRuntimeKey(skeleton);
			GpuSpineBakedEntry entry = bakedData.FindEntry(key);
			if (entry == null || entry.Mesh == null) {
				Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path: no baked entry for the current skin combination (key "
					+ key + "). Rebake the SkeletonDataAsset or declare the combination on its GpuSpineBakedData.", this);
				return;
			}

			originalUpdateMode = skeletonAnimation.UpdateMode;
			originalRendererEnabled = meshRenderer.enabled;
			boneMatrices = new GpuBoneMatrix[entry.BoneCount];
			currentBakedData = bakedData;
			lastKey = key;

			// Double cut of the CPU mesh chain: EverythingExceptMesh skips LateUpdateMesh, and the
			// disabled MeshRenderer additionally blocks the OnBecameVisible updateMode reset.
			skeletonAnimation.UpdateMode = UpdateMode.EverythingExceptMesh;
			meshRenderer.enabled = false;
			skeletonAnimation.UpdateComplete += OnSkeletonUpdateComplete;
			GpuSkinningManager.Register(this, entry);
			gpuActive = true;
			ExportBoneMatrices(skeleton); // A valid palette from the very first frame.
			BuildDynamicSlotState(entry);
			RefreshDynamicSlots(skeleton); // A valid variant selection from the very first frame.
		}

		void OnDisable () {
			if (!gpuActive) return;
			RestoreCpuPath(null);
		}

		void LateUpdate () {
			if (!gpuActive) return;
			// Guard against external code re-enabling the MeshRenderer (e.g. visibility toggles driving
			// Renderer.enabled): that intent maps to the submission set, while the renderer itself must
			// stay disabled to prevent CPU/GPU double rendering.
			if (meshRenderer != null && meshRenderer.enabled) {
				visible = true;
				meshRenderer.enabled = false;
			}
			// Defensive: the GPU path requires EverythingExceptMesh at all times; Spine callbacks such
			// as OnBecameVisible reset updateMode to FullUpdate, which would revive the CPU mesh chain.
			if (skeletonAnimation != null && skeletonAnimation.UpdateMode != UpdateMode.EverythingExceptMesh)
				skeletonAnimation.UpdateMode = UpdateMode.EverythingExceptMesh;
		}

		void OnSkeletonUpdateComplete (ISkeletonAnimation animated) {
			// Fired after the bone world transforms are final (including constraints and UpdateLocal
			// writers such as carry-sway bone edits).
			Skeleton skeleton = skeletonAnimation.Skeleton;
			if (skeleton == null) return;
			ExportBoneMatrices(skeleton);
			RefreshDynamicSlots(skeleton);

			string key = GpuSpineBakedRuntime.ComputeRuntimeKey(skeleton);
			if (key == lastKey) return;
			lastKey = key;
			// The effective skin combination changed: look up the pre-baked entry and move to its batches.
			GpuSpineBakedEntry entry = currentBakedData != null ? currentBakedData.FindEntry(key) : null;
			if (entry != null && entry.Mesh != null) {
				if (entry.BoneCount != boneMatrices.Length) boneMatrices = new GpuBoneMatrix[entry.BoneCount];
				GpuSkinningManager.ChangeEntry(this, entry);
				BuildDynamicSlotState(entry); // The new entry has its own variant table and slot count.
				RefreshDynamicSlots(skeleton);
			} else {
				RestoreCpuPath("GpuSkeletonRenderer fell back to the CPU path: no baked entry for the new skin combination (key " + key + ").");
			}
		}

		void ExportBoneMatrices (Skeleton skeleton) {
			if (boneMatrices == null) return;
			ExposedList<Bone> bones = skeleton.Bones;
			Bone[] items = bones.Items;
			int count = bones.Count;
			if (boneMatrices.Length != count) return; // Defensive; both sides come from the same SkeletonData.
			for (int i = 0; i < count; i++) {
				Bone bone = items[i];
				boneMatrices[i] = new GpuBoneMatrix(bone.A, bone.B, bone.C, bone.D, bone.WorldX, bone.WorldY);
			}
			paletteDirty = true;
		}

		/// <summary>Selection value written for a dynamic slot whose current attachment is null or not
		/// a baked variant: the vertex shader folds every variant of the slot (hidden state).</summary>
		const uint FoldAllVariants = 0xFFFFFFFF;

		/// <summary>(Re)builds the per-entry dynamic slot state: one attachment-name -> variant-id
		/// lookup per dynamic slot, flattened from <see cref="GpuSpineBakedEntry.DynamicVariants"/>,
		/// plus the selection array the batches upload. Called on enable and on every entry change
		/// (skin combination switch); an entry without dynamic slots keeps both arrays null.</summary>
		void BuildDynamicSlotState (GpuSpineBakedEntry entry) {
			int dynamicSlotCount = entry.DynamicSlotCount;
			if (dynamicSlotCount <= 0) {
				dynamicVariantLookup = null;
				dynamicSlotSelection = null;
				return;
			}
			dynamicVariantLookup = new Dictionary<string, int>[dynamicSlotCount];
			GpuSpineDynamicSlotVariant[] variants = entry.DynamicVariants;
			if (variants != null) {
				for (int i = 0; i < variants.Length; i++) {
					GpuSpineDynamicSlotVariant variant = variants[i];
					if ((uint)variant.DynSlotId >= (uint)dynamicSlotCount) continue; // Defensive: malformed baked data.
					Dictionary<string, int> lookup = dynamicVariantLookup[variant.DynSlotId];
					if (lookup == null) {
						lookup = new Dictionary<string, int>();
						dynamicVariantLookup[variant.DynSlotId] = lookup;
					}
					lookup[variant.AttachmentName] = variant.VariantId;
				}
			}
			dynamicSlotSelection = new uint[dynamicSlotCount];
			for (int i = 0; i < dynamicSlotCount; i++) dynamicSlotSelection[i] = FoldAllVariants; // Safe default: hidden until refreshed.
			warnedMissingVariants = new HashSet<string>();
			dynamicSlotsDirty = true; // The fold-all selection must be refreshed before upload.
		}

		/// <summary>Resolves every dynamic slot's selected variant from the live skeleton state
		/// (Slot.Attachment) and updates the selection array the batches upload: the variant id whose
		/// <see cref="GpuSpineDynamicSlotVariant.AttachmentName"/> matches, or
		/// <see cref="FoldAllVariants"/> when the slot has no attachment or the name is not a baked
		/// variant. Runs every UpdateComplete after the bone matrix export; the dirty flag gates the
		/// GPU upload.</summary>
		void RefreshDynamicSlots (Skeleton skeleton) {
			if (dynamicSlotSelection == null || lastAudit == null) return;
			List<GpuSpineDynamicSlotInfo> dynamicSlots = lastAudit.DynamicSlots;
			ExposedList<Slot> slots = skeleton.Slots;
			int count = dynamicSlotSelection.Length < dynamicSlots.Count ? dynamicSlotSelection.Length : dynamicSlots.Count;
			for (int dynSlotId = 0; dynSlotId < count; dynSlotId++) {
				uint selected = FoldAllVariants;
				int slotIndex = dynamicSlots[dynSlotId].SlotIndex;
				if (slotIndex >= 0 && slotIndex < slots.Count) {
					Attachment attachment = slots.Items[slotIndex].Attachment;
					if (attachment != null) {
						Dictionary<string, int> lookup = dynamicVariantLookup[dynSlotId];
						int variantId;
						if (lookup != null && lookup.TryGetValue(attachment.Name, out variantId))
							selected = (uint)variantId;
						else
							WarnMissingVariantOnce(dynamicSlots[dynSlotId], attachment.Name);
					}
				}
				if (dynamicSlotSelection[dynSlotId] != selected) {
					dynamicSlotSelection[dynSlotId] = selected;
					dynamicSlotsDirty = true;
				}
			}
		}

		/// <summary>Logs the missing-variant warning once per (slot, attachment name) pair per entry:
		/// the attachment is live on the slot but was never baked as a variant (e.g. the baked data is
		/// stale), so the slot folds to hidden on the GPU path.</summary>
		void WarnMissingVariantOnce (GpuSpineDynamicSlotInfo slotInfo, string attachmentName) {
			string key = slotInfo.SlotName + "/" + attachmentName;
			if (warnedMissingVariants == null || warnedMissingVariants.Add(key))
				Debug.LogWarning("GpuSkeletonRenderer: attachment '" + attachmentName + "' of dynamic slot '" + slotInfo.SlotName
					+ "' is not a baked variant; folding all variants of the slot. Rebake the SkeletonDataAsset.", this);
		}

		/// <summary>Full restore of the original CPU path: original updateMode, original MeshRenderer
		/// enabled state, event unsubscribed, batches left, references released.</summary>
		void RestoreCpuPath (string reason) {
			if (reason != null) Debug.LogWarning(reason, this);
			gpuActive = false;
			if (skeletonAnimation != null) {
				skeletonAnimation.UpdateComplete -= OnSkeletonUpdateComplete;
				skeletonAnimation.UpdateMode = originalUpdateMode;
			}
			if (meshRenderer != null) meshRenderer.enabled = originalRendererEnabled;
			GpuSkinningManager.Unregister(this);
			boneMatrices = null;
			currentBakedData = null;
			lastKey = null;
			paletteDirty = false;
			dynamicVariantLookup = null;
			dynamicSlotSelection = null;
			warnedMissingVariants = null;
			dynamicSlotsDirty = false;
		}

		internal GpuBoneMatrix[] BoneMatrices { get { return boneMatrices; } }

		/// <summary>The per-dynamic-slot variant selection uploaded by the batches (null while the
		/// current entry has no dynamic slots). Same lifecycle as the bone matrix palette.</summary>
		internal uint[] DynamicSlotSelection { get { return dynamicSlotSelection; } }

		/// <summary>Reads and clears the dynamic slot selection dirty flag set by RefreshDynamicSlots.</summary>
		internal bool ConsumeDynamicSlotsDirty () {
			bool dirty = dynamicSlotsDirty;
			dynamicSlotsDirty = false;
			return dirty;
		}

		/// <summary>True when the instance belongs in this frame's submission set.</summary>
		internal bool ShouldSubmit { get { return gpuActive && visible && isActiveAndEnabled; } }

		/// <summary>Reads and clears the palette dirty flag set by the bone matrix export.</summary>
		internal bool ConsumePaletteDirty () {
			bool dirty = paletteDirty;
			paletteDirty = false;
			return dirty;
		}

		/// <summary>Fills one instance data record: transform, skeleton color, then the user callback
		/// for the Custom0/Custom1 extension slots.</summary>
		internal void FillInstanceData (ref GpuSpineInstanceData data) {
			data.LocalToWorld = transform.localToWorldMatrix;
			Skeleton skeleton = skeletonAnimation != null ? skeletonAnimation.Skeleton : null;
			data.Color = skeleton != null ? new Vector4(skeleton.R, skeleton.G, skeleton.B, skeleton.A) : Vector4.one;
			data.Custom0 = Vector4.zero;
			data.Custom1 = Vector4.zero;
			GpuSpineInstanceDataWriter writer = WriteInstanceData;
			if (writer != null) writer(this, ref data);
		}
	}
}
