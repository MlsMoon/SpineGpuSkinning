using System.Collections.Generic;
using GpuSpine.Baking;
using GpuSpine.Core;
using Spine;
using Spine.Unity;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GpuSpine {
	/// <summary>
	/// One-component switch that moves a <see cref="SkeletonAnimation"/> from CPU skinning to GPU
	/// skinning: sets updateMode to EverythingExceptMesh (animation state, physics and bone world
	/// transforms keep running on the CPU untouched), suppresses CPU rendering, exports the 3x2 bone
	/// matrices on UpdateComplete and lets <see cref="GpuSkinningManager"/> draw the editor-baked entry
	/// mesh via RenderMeshIndirect. The component never bakes at runtime: when no usable baked
	/// data exists (no container, failed audit, missing entry, zSpacing mismatch), it logs a warning,
	/// leaves the skeleton on the original CPU path and does nothing. Does nothing at all in edit mode,
	/// so editor previews keep the original path. OnDisable restores the CPU path completely.
	/// </summary>
	[AddComponentMenu("Spine/Gpu Skeleton Renderer")]
	public sealed class GpuSkeletonRenderer : MonoBehaviour {
		/// <summary>Optional custom GPU material template. Its shader replaces the cloned page
		/// material's shader only for matching shader families; atlas textures come from the page. Null uses
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

		/// <summary>Legacy serialized compatibility only; draw order is now replayed exactly.</summary>
		[HideInInspector] public bool AllowDrawOrderTimeline;
		/// <summary>When true, this instance skips GPU fragment clipping (its clip ranges upload as
		/// zeros, so <c>GpuSpineClip</c> keeps every fragment). Serialized on shipped prefabs; kept
		/// functional as the per-instance bypass for rigs whose clipping is not GPU-validated.</summary>
		[HideInInspector] public bool IgnoreClipping;

		/// <summary>When true, this instance is included in <see cref="GpuSpineRuntimeSwitch"/> so host
		/// commands can toggle CPU/GPU without a project-side adapter. Off by default so examples stay out.</summary>
		[Tooltip("Include this instance in GpuSpineRuntimeSwitch (host CPU/GPU toggle). Leave off for examples.")]
		public bool IncludeInRuntimeSwitch;

		/// <summary>Honor URP Camera Rendering Layer Filter when deciding per-camera submission.
		/// Cameras that do not enable the filter are unaffected.</summary>
		[Tooltip("Honor URP Camera Rendering Layer Filter. Cameras without the filter are unaffected.")]
		public bool ApplyCameraRenderingLayerFilter = true;

		/// <summary>Copy MeshRenderer MaterialPropertyBlock values into instance Custom0/Custom1.</summary>
		[Tooltip("Copy MeshRenderer MaterialPropertyBlock vectors into instance Custom0/Custom1.")]
		public bool CopyPropertyBlockToCustomData;

		/// <summary>Vector property copied into Custom0.xyz (and .w unless Custom0WProperty is set).</summary>
		[Tooltip("Vector property copied into Custom0.xyz (and .w unless Custom0WProperty is set).")]
		public string Custom0Property;

		/// <summary>Optional float property packed into Custom0.w.</summary>
		[Tooltip("Optional float property packed into Custom0.w.")]
		public string Custom0WProperty;

		/// <summary>Vector property copied into Custom1.</summary>
		[Tooltip("Vector property copied into Custom1.")]
		public string Custom1Property;

		/// <summary>Per-frame callback to fill the Custom0/Custom1 slots of this instance's draw data.</summary>
		public event GpuSpineInstanceDataWriter WriteInstanceData;

		SkeletonAnimation skeletonAnimation;
		MeshRenderer meshRenderer;
		UpdateMode originalUpdateMode;
		UpdateMode originalInvisibleMode;
		bool originalForceRenderingOff;
		public System.Func<Camera, bool> CameraFilter;
		GpuBoneMatrix[] boneMatrices;
		GpuSpineBakedData currentBakedData;
		ulong lastKey;
		bool gpuActive;
		bool bakedLease;
		ulong paletteVersion;
		bool visible = true;
		bool visibilityAssigned;
		GpuSpineAuditReport lastAudit;
		Dictionary<string, int>[] dynamicVariantLookup; // [dynSlotId] -> attachment name -> variant id
		uint[] dynamicSlotSelection;                    // [dynSlotId] -> variant id or FoldAllVariants
		HashSet<string> warnedMissingVariants;
		ulong dynamicSlotsVersion;
		DeformSlotState[] deformSlots;   // null while the current entry has no deform slots
		Vector2[] deformSegment;         // [entry.DeformStride] per-instance deform data (same lifecycle as boneMatrices)
		ulong deformVersion;
		Vector4[] slotColors;            // [entry.SlotCount] per-instance slot colors (slot.R/G/B/A), same lifecycle
		ulong slotColorsVersion;
		ulong rejectedKey;
		ulong lastOrderKey;
		GpuSpineBakedEntry currentEntry;
		GpuSpineClippingState clipping;
		ulong clippingVersion;
		bool activationPending;
		bool eventsRegistered;
		int boundsFrame = -1;
		Bounds cachedBounds;
		MaterialPropertyBlock propertyBlock;
		int custom0Id;
		int custom0WId;
		int custom1Id;
		string cachedCustom0Property;
		string cachedCustom0WProperty;
		string cachedCustom1Property;

		/// <summary>True while this skeleton is submitted through the GPU instanced path.</summary>
		public bool IsGpuActive { get { return gpuActive; } }

		/// <summary>Controls inclusion in the submission set: false removes the instance from the
		/// submitted batches while bone matrix export keeps running (the skeleton stays animated).</summary>
		public bool Visible {
			get { return visible; }
			set {
				visible = value;
				visibilityAssigned = true;
				if (meshRenderer != null) meshRenderer.enabled = value;
			}
		}

		/// <summary>The admittance audit evaluated on enable (null while never audited).</summary>
		public GpuSpineAuditReport LastAudit { get { return lastAudit; } }

		void OnEnable () {
			activationPending = Application.isPlaying;
			if (Application.isPlaying && IncludeInRuntimeSwitch)
				GpuSpineRuntimeSwitch.Register(this);
		}

		void TryActivate () {
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
				if (GpuSpineDiagnostics.EnableLogging) Debug.Log("GpuSkeletonRenderer stays on the CPU path: it requires a SkeletonAnimation and a MeshRenderer on the same GameObject or a child.", this);
				return;
			}

			skeletonAnimation.Initialize(false); // Idempotent; covers enabling before SkeletonAnimation.Awake.
			RegisterEvents();
			if (skeletonAnimation.Skeleton != null)
				rejectedKey = GpuSpineBakedRuntime.ComputeRuntimeHash(skeletonAnimation.Skeleton);
			SkeletonDataAsset asset = skeletonAnimation.skeletonDataAsset;
			if (asset == null) {
				if (GpuSpineDiagnostics.EnableLogging) Debug.Log("GpuSkeletonRenderer stays on the CPU path: no SkeletonDataAsset assigned.", this);
				return;
			}

			// Editor-baked data only; the runtime never bakes. The component field wins over the registry.
			GpuSpineBakedData bakedData = BakedData;
			if (bakedData == null) GpuSpineBakedRuntime.TryGet(asset, out bakedData);
			if (bakedData == null) {
				if (GpuSpineDiagnostics.EnableLogging) Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path: no GpuSpineBakedData available for the SkeletonDataAsset (assign BakedData or register one via GpuSpineBakedRuntime).", this);
				return;
			}

			if (!bakedData.IsCompatible || bakedData.SourceAsset != asset) {
				if (GpuSpineDiagnostics.EnableLogging) Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path: incompatible baked format or source asset. Rebake the skeleton.", this);
				return;
			}
			lastAudit = bakedData.Audit;
			if (lastAudit == null || !lastAudit.Passed) {
				if (GpuSpineDiagnostics.EnableLogging) Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path, audit failed. First reason: "
					+ (lastAudit != null && lastAudit.Failures.Count > 0 ? lastAudit.Failures[0] : "unknown"), this);
				return;
			}
			if (skeletonAnimation.zSpacing != bakedData.BakedZSpacing) {
				if (GpuSpineDiagnostics.EnableLogging) Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path: zSpacing " + skeletonAnimation.zSpacing
					+ " differs from the baked zSpacing " + bakedData.BakedZSpacing + " (baking is fixed to 0 in v1).", this);
				return;
			}

			Skeleton skeleton = skeletonAnimation.Skeleton;
			if (skeleton == null) {
				if (GpuSpineDiagnostics.EnableLogging) Debug.Log("GpuSkeletonRenderer stays on the CPU path: the skeleton is not initialized.", this);
				return;
			}
			string key = GpuSpineBakedRuntime.ComputeRuntimeKey(skeleton);
			lastOrderKey = ComputeOrderHash(skeleton);
			GpuSpineBakedEntry entry = ResolveEntry(bakedData.FindEntry(key), lastOrderKey.ToString("X16"));
			if (entry == null || entry.Mesh == null) {
				if (GpuSpineDiagnostics.EnableLogging) Debug.LogWarning("GpuSkeletonRenderer stays on the CPU path: no baked entry for the current skin combination (key "
					+ key + "). Rebake the SkeletonDataAsset or declare the combination on its GpuSpineBakedData.", this);
				return;
			}

			originalUpdateMode = skeletonAnimation.UpdateMode;
			originalInvisibleMode = skeletonAnimation.updateWhenInvisible;
			originalForceRenderingOff = meshRenderer.forceRenderingOff;
			if (visibilityAssigned) meshRenderer.enabled = visible;
			boneMatrices = new GpuBoneMatrix[entry.BoneCount];
			currentBakedData = bakedData;
			lastKey = ComputeSkinHash(skeleton);
			currentEntry = entry;
			BuildClipping(entry, skeleton);

			ExportBoneMatrices(skeleton); // A valid palette from the very first frame.
			BuildDynamicSlotState(entry);
			RefreshDynamicSlots(skeleton); // A valid variant selection from the very first frame.
			BuildDeformState(entry);
			FillDeform(skeleton); // A valid deform segment from the very first frame.
			BuildSlotColorState(entry);
			FillSlotColors(skeleton); // Valid slot colors from the very first frame.
			if (!GpuSkinningManager.Register(this, entry)) {
				RestoreCpuPath("GpuSkeletonRenderer: batch registration failed; CPU rendering retained.");
				return;
			}
			GpuSpineBakedRuntime.Retain(bakedData);
			bakedLease = true;
			skeletonAnimation.UpdateMode = UpdateMode.EverythingExceptMesh;
			skeletonAnimation.updateWhenInvisible = UpdateMode.EverythingExceptMesh;
			meshRenderer.forceRenderingOff = true;
			gpuActive = true;
		}

		void OnDisable () {
			activationPending = false;
			UnregisterEvents();
			BatchInstanceIndex = -1;
			if (!gpuActive) return;
			RestoreCpuPath(null);
		}

		void OnDestroy () {
			GpuSpineRuntimeSwitch.Unregister(this);
		}

		void LateUpdate () {
			if (activationPending) {
				activationPending = false;
				TryActivate();
			}
			if (!gpuActive) return;
			// Preserve Renderer.enabled as visibility intent while suppressing automatic CPU draws.
			if (meshRenderer != null) meshRenderer.forceRenderingOff = true;
			// Defensive: the GPU path requires EverythingExceptMesh at all times; Spine callbacks such
			// as OnBecameVisible reset updateMode to FullUpdate, which would revive the CPU mesh chain.
			if (skeletonAnimation != null && skeletonAnimation.UpdateMode != UpdateMode.EverythingExceptMesh)
				skeletonAnimation.UpdateMode = UpdateMode.EverythingExceptMesh;
		}

        ulong ComputeSkinHash(Skeleton skeleton) {
            using var scope=GpuSpineCpuDiagnostics.Skin.Auto();
            GpuSpineCpuDiagnostics.SkinChecks++;GpuSpineCpuDiagnostics.SkinHashRecomputations++;
            return GpuSpineBakedRuntime.ComputeRuntimeHash(skeleton);
        }
        ulong ComputeOrderHash(Skeleton skeleton) {
            using var scope=GpuSpineCpuDiagnostics.Order.Auto();
            return GpuSpineDrawOrderKey.ComputeHash(skeleton);
        }
		void OnSkeletonUpdateComplete (ISkeletonAnimation animated) {
			boundsFrame = -1;
			// Fired after the bone world transforms are final (including constraints and UpdateLocal
			// writers such as carry-sway bone edits).
			Skeleton skeleton = skeletonAnimation.Skeleton;
			if (skeleton == null) return;
			if (!gpuActive) {
				if (ComputeSkinHash(skeleton) != rejectedKey) TryActivate();
				return;
			}
			ulong key = ComputeSkinHash(skeleton);
			ulong orderKey = ComputeOrderHash(skeleton);
			if (key != lastKey || orderKey != lastOrderKey) {
				GpuSpineBakedEntry entry = ResolveEntry(currentBakedData.FindEntry(key.ToString("X16")), orderKey.ToString("X16"));
				if (entry == null || entry.Mesh == null) {
					rejectedKey = key;
					RestoreCpuPath("GpuSkeletonRenderer: missing baked skin or draw-order layout.");
					return;
				}
				if (!GpuSkinningManager.ChangeEntry(this, entry)) {
					rejectedKey = key;
					RestoreCpuPath("GpuSkeletonRenderer: batch registration failed.");
					return;
				}
				if (key != lastKey) {
					boneMatrices = new GpuBoneMatrix[entry.BoneCount];
					BuildDynamicSlotState(entry);
					BuildDeformState(entry);
					BuildSlotColorState(entry);
					BuildClipping(entry, skeleton);
				}
				lastKey = key;
				lastOrderKey = orderKey;
				currentEntry = entry;
			}
			ExportBoneMatrices(skeleton);
			RefreshDynamicSlots(skeleton);
			FillDeform(skeleton);
			FillSlotColors(skeleton);
			if (clipping != null) { using var clipScope=GpuSpineCpuDiagnostics.Clip.Auto(); clipping.Update(skeleton); clippingVersion++; }
		}

		GpuSpineBakedEntry ResolveEntry (GpuSpineBakedEntry entry, string orderKey) {
			if (entry == null) return null;
			if (entry.DrawOrderLayouts != null && entry.DrawOrderLayouts.Length > 0)
				return entry.FindOrderedEntry(orderKey);
			return lastAudit.HasDrawOrderTimeline || lastAudit.HasClipping ? null : entry;
		}

		void BuildClipping (GpuSpineBakedEntry entry, Skeleton skeleton) {
			clipping = entry.ClipVertexCapacity > 0 && !IgnoreClipping
				? new GpuSpineClippingState(entry.ClipVertexCapacity, entry.SlotCount) : null;
			if (clipping != null) clipping.Update(skeleton);
			clippingVersion++;
		}

		void ExportBoneMatrices (Skeleton skeleton) {
            using var cpuScope=GpuSpineCpuDiagnostics.Bones.Auto();
			if (boneMatrices == null) return;
			ExposedList<Bone> bones = skeleton.Bones;
			Bone[] items = bones.Items;
			int count = bones.Count;
			if (boneMatrices.Length != count) return; // Defensive; both sides come from the same SkeletonData.
			for (int i = 0; i < count; i++) {
				Bone bone = items[i];
				boneMatrices[i] = new GpuBoneMatrix(bone.A, bone.B, bone.C, bone.D, bone.WorldX, bone.WorldY);
			}
			paletteVersion++;
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
			dynamicSlotsVersion++;
		}

		/// <summary>Per-deform-slot runtime state: the slot's segment window plus the fallback lookup
		/// by attachment name (DefaultValues/DeformLength from the baked entry).</summary>
		sealed class DeformSlotState {
			public int SlotIndex;
			public int Prefix;
			public int Capacity;
			public Dictionary<string, GpuSpineDeformAttachmentInfo> AttachmentByName;
		}

		/// <summary>(Re)builds the per-entry deform state: one segment window per baked deform slot and
		/// the flat per-instance deform segment of entry.DeformStride float2 entries. Called on enable
		/// and on every entry change; an entry without deform slots keeps both null.</summary>
		void BuildDeformState (GpuSpineBakedEntry entry) {
			GpuSpineDeformSlotEntry[] baked = entry.DeformSlots;
			if (baked == null || baked.Length == 0 || entry.DeformStride <= 0) {
				deformSlots = null;
				deformSegment = null;
				return;
			}
			deformSlots = new DeformSlotState[baked.Length];
			for (int i = 0; i < baked.Length; i++) {
				GpuSpineDeformSlotEntry slotEntry = baked[i];
				Dictionary<string, GpuSpineDeformAttachmentInfo> byName = new Dictionary<string, GpuSpineDeformAttachmentInfo>();
				GpuSpineDeformAttachmentInfo[] attachments = slotEntry.Attachments;
				if (attachments != null) {
					for (int a = 0; a < attachments.Length; a++)
						if (attachments[a] != null) byName[attachments[a].AttachmentName] = attachments[a];
				}
				deformSlots[i] = new DeformSlotState {
					SlotIndex = slotEntry.SlotIndex,
					Prefix = slotEntry.Prefix,
					Capacity = slotEntry.Capacity,
					AttachmentByName = byName
				};
			}
			deformSegment = new Vector2[entry.DeformStride];
			deformVersion++;
		}

		/// <summary>Refreshes the per-instance deform segment from the live skeleton state, once per
		/// UpdateComplete after the bone matrix export. Per deform slot: when slot.Deform is written
		/// (DeformTimeline.Apply wrote it for the current attachment this frame: the timeline only
		/// writes when slot.Attachment resolves to its target (Animation.cs:1773-1776) and the
		/// attachment setter clears deform on an attachment change (Slot.cs:139-156), so Count > 0
		/// always matches the current attachment), copy min(Count/2, Capacity) float2 verbatim and
		/// zero-fill the rest; otherwise write the current attachment's baked DefaultValues
		/// (unweighted: local positions; weighted: all zeros = undeformed), or all zeros when the
		/// attachment name is not a baked deform attachment.</summary>
		void FillDeform (Skeleton skeleton) {
            using var cpuScope=GpuSpineCpuDiagnostics.Deform.Auto();
			if (deformSegment == null || deformSlots == null) return;
			ExposedList<Slot> slots = skeleton.Slots;
			bool changed = false;
			for (int i = 0; i < deformSlots.Length; i++) {
				DeformSlotState state = deformSlots[i];
				int prefix = state.Prefix;
				int capacity = state.Capacity;
				float[] source = null;
				int sourcePairs = 0;
				Vector2[] defaults = null;
				if (state.SlotIndex >= 0 && state.SlotIndex < slots.Count) {
					Slot slot = slots.Items[state.SlotIndex];
					ExposedList<float> deform = slot.Deform;
					if (deform.Count > 0) {
						source = deform.Items;
						sourcePairs = deform.Count >> 1;
					} else {
						Attachment attachment = slot.Attachment;
						GpuSpineDeformAttachmentInfo info;
						if (attachment != null && state.AttachmentByName.TryGetValue(attachment.Name, out info))
							defaults = info.DefaultValues;
					}
				}
                int pairs = source != null ? System.Math.Min(sourcePairs, capacity) : defaults != null ? System.Math.Min(defaults.Length, capacity) : 0;
                for (int p = 0; p < capacity; p++) {
                    Vector2 value = p >= pairs ? Vector2.zero : source != null ? new Vector2(source[p * 2], source[p * 2 + 1]) : defaults[p];
                    int index = prefix + p;
                    changed = changed || !deformSegment[index].x.Equals(value.x) || !deformSegment[index].y.Equals(value.y);
                    deformSegment[index] = value;
                }
            }
			if (changed) deformVersion++;
		}

		/// <summary>Resolves every dynamic slot's selected variant from the live skeleton state
		/// (Slot.Attachment) and updates the selection array the batches upload: the variant id whose
		/// <see cref="GpuSpineDynamicSlotVariant.AttachmentName"/> matches, or
		/// <see cref="FoldAllVariants"/> when the slot has no attachment or the name is not a baked
		/// variant. Runs every UpdateComplete after the bone matrix export; the dirty flag gates the
		/// GPU upload.</summary>
		void RefreshDynamicSlots (Skeleton skeleton) {
            using var cpuScope=GpuSpineCpuDiagnostics.Dynamic.Auto();
			if (dynamicSlotSelection == null || lastAudit == null) return;
			List<GpuSpineDynamicSlotInfo> dynamicSlots = lastAudit.DynamicSlots;
			ExposedList<Slot> slots = skeleton.Slots;
			int count = dynamicSlotSelection.Length < dynamicSlots.Count ? dynamicSlotSelection.Length : dynamicSlots.Count;
			for (int dynSlotId = 0; dynSlotId < count; dynSlotId++) {
				uint selected = FoldAllVariants;
				int slotIndex = dynamicSlots[dynSlotId].SlotIndex;
				if (slotIndex >= 0 && slotIndex < slots.Count) {
					Attachment attachment = slots.Items[slotIndex].Attachment;
					if (attachment is RegionAttachment || attachment is MeshAttachment) {
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
					dynamicSlotsVersion++;
				}
			}
		}

		/// <summary>Logs the missing-variant warning once per (slot, attachment name) pair per entry:
		/// the attachment is live on the slot but was never baked as a variant (e.g. the baked data is
		/// stale), so the slot folds to hidden on the GPU path.</summary>
		void WarnMissingVariantOnce (GpuSpineDynamicSlotInfo slotInfo, string attachmentName) {
			if (!GpuSpineDiagnostics.EnableLogging) return;
			string key = slotInfo.SlotName + "/" + attachmentName;
			if (warnedMissingVariants == null || warnedMissingVariants.Add(key))
				if (GpuSpineDiagnostics.EnableLogging) Debug.LogWarning("GpuSkeletonRenderer: attachment '" + attachmentName + "' of dynamic slot '" + slotInfo.SlotName
					+ "' is not a baked variant; folding all variants of the slot. Rebake the SkeletonDataAsset.", this);
		}

		/// <summary>(Re)allocates the per-instance slot color segment of entry.SlotCount entries.
		/// Every slot's color rides the buffer (setup-static colors included), which keeps the
		/// shader path uniform and removes any static color baking. Called on enable and on every
		/// entry change.</summary>
		void BuildSlotColorState (GpuSpineBakedEntry entry) {
			slotColors = entry.SlotCount > 0 ? new Vector4[entry.SlotCount] : null;
			slotColorsVersion++;
		}

		/// <summary>Refreshes the per-instance slot color segment from the live skeleton state
		/// (slot.R/G/B/A of every slot), once per UpdateComplete. Slot color timelines
		/// (RGBATimeline/RGBTimeline/AlphaTimeline) write those same fields through the CPU
		/// AnimationState, so timeline-driven colors arrive here for free. Slots beyond the live
		/// skeleton's count stay at white.</summary>
		void FillSlotColors (Skeleton skeleton) {
            using var cpuScope=GpuSpineCpuDiagnostics.Colors.Auto();
			if (slotColors == null) return;
			ExposedList<Slot> slots = skeleton.Slots;
			int count = slotColors.Length < slots.Count ? slotColors.Length : slots.Count;
			bool changed = false;
            for (int i = 0; i < slotColors.Length; i++) {
                Slot slot = i < count ? slots.Items[i] : null;
                Vector4 value = slot != null ? new Vector4(slot.R, slot.G, slot.B, slot.A) : Vector4.one;
                Vector4 previous = slotColors[i];
                changed = changed || !previous.x.Equals(value.x) || !previous.y.Equals(value.y) || !previous.z.Equals(value.z) || !previous.w.Equals(value.w);
                slotColors[i] = value;
            }
			if (changed) slotColorsVersion++;
		}

		/// <summary>Full restore of the original CPU path: original updateMode, original MeshRenderer
		/// suppression state, batches left, references released.</summary>
		void RestoreCpuPath (string reason) {
			if (GpuSpineDiagnostics.EnableLogging && reason != null) Debug.LogWarning(reason, this);
			gpuActive = false;
			if (skeletonAnimation != null) {
				skeletonAnimation.UpdateMode = originalUpdateMode;
				skeletonAnimation.updateWhenInvisible = originalInvisibleMode;
			}
			if (meshRenderer != null) meshRenderer.forceRenderingOff = originalForceRenderingOff;
			GpuSkinningManager.Unregister(this);
			if (bakedLease) GpuSpineBakedRuntime.Release(currentBakedData);
			bakedLease = false;
			boneMatrices = null;
			currentBakedData = null;
			currentEntry = null;
			clipping = null;
			lastKey = 0;
			BatchInstanceIndex = -1;
			dynamicVariantLookup = null;
			dynamicSlotSelection = null;
			warnedMissingVariants = null;
			deformSlots = null;
			deformSegment = null;
			slotColors = null;
		}

		internal GpuBoneMatrix[] BoneMatrices { get { return boneMatrices; } }

		/// <summary>The per-dynamic-slot variant selection uploaded by the batches (null while the
		/// current entry has no dynamic slots). Same lifecycle as the bone matrix palette.</summary>
		internal uint[] DynamicSlotSelection { get { return dynamicSlotSelection; } }

		/// <summary>Version observed independently by every upload target.</summary>
		internal ulong DynamicSlotsVersion => dynamicSlotsVersion;

		/// <summary>The per-instance deform segment uploaded by the batches (null while the current
		/// entry has no deform slots). Same lifecycle as the bone matrix palette.</summary>
		internal Vector2[] DeformSegment { get { return deformSegment; } }

		/// <summary>Version observed independently by every upload target.</summary>
		internal ulong DeformVersion => deformVersion;

		/// <summary>The per-instance slot color segment uploaded by the batches (null while the
		/// current entry has no slots, which never happens in practice). Same lifecycle as the
		/// bone matrix palette.</summary>
		internal Vector4[] SlotColors { get { return slotColors; } }

		/// <summary>Version observed independently by every upload target.</summary>
		internal ulong SlotColorsVersion => slotColorsVersion;

		/// <summary>True when the instance belongs in this frame's submission set.</summary>
		internal bool ShouldSubmit { get { return gpuActive && visible && isActiveAndEnabled && meshRenderer != null && meshRenderer.enabled && !originalForceRenderingOff; } }
		public MeshRenderer SourceRenderer => meshRenderer;
		public Transform SkeletonTransform => skeletonAnimation != null ? skeletonAnimation.transform : transform;
		internal bool ShouldSubmitTo(Camera camera) => ShouldSubmit && camera != null &&
			(camera.cullingMask & (1 << SourceRenderer.gameObject.layer)) != 0 &&
			PassesRenderingLayerFilter(camera) &&
			(CameraFilter == null || CameraFilter(camera));

		bool PassesRenderingLayerFilter(Camera camera) {
			if (!ApplyCameraRenderingLayerFilter || SourceRenderer == null) return true;
			if (!camera.TryGetComponent(out UniversalAdditionalCameraData data) ||
				!data.enableCameraRenderingLayerFilter)
				return true;
			if (camera.cameraType == CameraType.SceneView && !data.applyRenderingLayerFilterInSceneView)
				return true;
			return (data.cameraRenderingLayerMask & SourceRenderer.renderingLayerMask) != 0;
		}

        /// <summary>Debug index of the last upload group. For multi-camera or multi-material redraws use GetBatches(camera, source);
        /// do not treat this as a universal instance index across groups.</summary>
		public int BatchInstanceIndex { get; internal set; } = -1;

		/// <summary>Version observed independently by every upload target.</summary>
		internal ulong PaletteVersion => paletteVersion;
		internal ulong ClippingVersion => clippingVersion;
		internal GpuSpineClippingState Clipping => clipping;
		internal GpuSpineBakedEntry CurrentEntry => currentEntry;
		internal Shader ResolveMaterialShader(Material page) => MaterialOverride != null && page != null && page.shader == MaterialOverride.shader
			? MaterialOverride.shader : currentBakedData != null ? currentBakedData.DefaultShader : null;

		public Bounds WorldBounds {
			get {
				if (boundsFrame != Time.frameCount) {
					cachedBounds = CalculateWorldBounds;
					boundsFrame = Time.frameCount;
				}
				return cachedBounds;
			}
		}

		Bounds CalculateWorldBounds {
			get {
				using var boundsScope=GpuSpineCpuDiagnostics.Bounds.Auto();
                float radius = 0f, deformMargin = 0f;
				if (deformSegment != null) {
					foreach (Vector2 value in deformSegment)
						deformMargin = Mathf.Max(deformMargin, Mathf.Max(Mathf.Abs(value.x), Mathf.Abs(value.y)));
				}
				if (currentEntry?.BoneBounds != null && boneMatrices != null) {
					for (int i = 0; i < boneMatrices.Length; i++) {
						if (!currentEntry.UsedBones[i]) continue;
						Bounds local = currentEntry.BoneBounds[i];
						// Unweighted deform values are absolute local positions, so include the origin.
						local.Encapsulate(Vector3.zero);
						Vector2 extent = (Vector2)local.extents + Vector2.one * deformMargin;
						GpuBoneMatrix bone = boneMatrices[i];
						for (int corner = 0; corner < 4; corner++) {
							Vector2 point = (Vector2)local.center + new Vector2((corner & 1) == 0 ? extent.x : -extent.x,
								(corner & 2) == 0 ? extent.y : -extent.y);
							Vector2 world = new Vector2(Vector2.Dot(bone.Row0, point), Vector2.Dot(bone.Row1, point)) + bone.Row2;
							radius = Mathf.Max(radius, world.magnitude);
						}
					}
				} else if (meshRenderer != null) {
					return meshRenderer.bounds;
				}
				Vector3 scale = SkeletonTransform.lossyScale;
				radius *= Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
				return new Bounds(SkeletonTransform.position, Vector3.one * Mathf.Max(radius * 2f, 0.001f));
			}
		}

		void RegisterEvents () {
			if (eventsRegistered) return;
			skeletonAnimation.UpdateComplete += OnSkeletonUpdateComplete;
			eventsRegistered = true;
		}

		void UnregisterEvents () {
			if (!eventsRegistered) return;
			if (skeletonAnimation != null) skeletonAnimation.UpdateComplete -= OnSkeletonUpdateComplete;
			eventsRegistered = false;
		}

		/// <summary>Fills one instance data record: transform, skeleton color, then the user callback
		/// for the Custom0/Custom1 extension slots.</summary>
		internal void FillInstanceData (ref GpuSpineInstanceData data) {
            using var instanceScope=GpuSpineCpuDiagnostics.InstanceData.Auto();
			Matrix4x4 matrix = skeletonAnimation.transform.localToWorldMatrix;
			data.LocalToWorldRow0 = matrix.GetRow(0);
			data.LocalToWorldRow1 = matrix.GetRow(1);
			data.LocalToWorldRow2 = matrix.GetRow(2);
			Skeleton skeleton = skeletonAnimation != null ? skeletonAnimation.Skeleton : null;
			data.Color = skeleton != null ? new Vector4(skeleton.R, skeleton.G, skeleton.B, skeleton.A) : Vector4.one;
			data.Custom0 = Vector4.zero;
			data.Custom1 = Vector4.zero;
			CopyPropertyBlockCustom(ref data);
			GpuSpineInstanceDataWriter writer = WriteInstanceData;
			if (writer != null) writer(this, ref data);
		}

		void CopyPropertyBlockCustom(ref GpuSpineInstanceData data) {
			if (!CopyPropertyBlockToCustomData || meshRenderer == null) return;
			CacheCustomPropertyIds();
			if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();
			meshRenderer.GetPropertyBlock(propertyBlock);
			if (custom0Id != 0) {
				Vector4 value = propertyBlock.GetVector(custom0Id);
				if (custom0WId != 0) value.w = propertyBlock.GetFloat(custom0WId);
				data.Custom0 = value;
			}
			if (custom1Id != 0) data.Custom1 = propertyBlock.GetVector(custom1Id);
		}

		void CacheCustomPropertyIds() {
			if (cachedCustom0Property == Custom0Property &&
				cachedCustom0WProperty == Custom0WProperty &&
				cachedCustom1Property == Custom1Property)
				return;
			cachedCustom0Property = Custom0Property;
			cachedCustom0WProperty = Custom0WProperty;
			cachedCustom1Property = Custom1Property;
			custom0Id = string.IsNullOrEmpty(Custom0Property) ? 0 : Shader.PropertyToID(Custom0Property);
			custom0WId = string.IsNullOrEmpty(Custom0WProperty) ? 0 : Shader.PropertyToID(Custom0WProperty);
			custom1Id = string.IsNullOrEmpty(Custom1Property) ? 0 : Shader.PropertyToID(Custom1Property);
		}
	}
}
