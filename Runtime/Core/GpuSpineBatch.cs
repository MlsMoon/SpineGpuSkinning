using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GpuSpine.Baking;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuSpine.Core {
	/// <summary>
	/// Public read-only view of one submitted batch, for custom RenderPasses that resubmit the same
	/// indirect draw into their own pass (the batch material already carries the bound buffers, so
	/// CommandBuffer.DrawMeshInstancedIndirect with an explicit pass index works as-is).
	/// </summary>
	public struct GpuSpineBatchInfo {
		/// <summary>The baked entry's mesh.</summary>
		public Mesh Mesh;
		/// <summary>Submesh index this batch draws.</summary>
		public int SubmeshIndex;
		/// <summary>The cloned batch material with the GPU skinning shader and the bound buffers.</summary>
		public Material Material;
		/// <summary>uint[5] indirect args buffer (indexCount, instanceCount, indexStart, baseVertex, 0).</summary>
		public ComputeBuffer ArgsBuffer;
		/// <summary>World-space union bounds of all submitted instances.</summary>
		public Bounds Bounds;
		/// <summary>Number of submitted instances this frame.</summary>
		public int InstanceCount;
	}

	/// <summary>
	/// One indirect draw batch: all GpuSkeletonRenderer instances sharing the same baked entry
	/// and submesh index (a submesh is an atlas page material boundary, so this equals the CPU path's
	/// per-page batching). Owns the bone palette buffer, the per-instance data buffer, the dynamic
	/// slot variant selection buffer (entries with dynamic slots only), the indirect args buffer and
	/// a cloned batch material.
	/// The batch material is always a clone, never the shared page material: the Spine runtime flips
	/// enableInstancing on shared materials (SkeletonRenderer.SetMaterialSettingsToFixDrawOrder).
	/// </summary>
	internal sealed class GpuSpineBatch : IDisposable {
		/// <summary>Instance capacity of a fresh batch (ES2DInstance convention: start at 32).</summary>
		const int InitialInstanceCapacity = 32;
		/// <summary>World-space margin added to the submission bounds; live poses deviate from the
		/// baked bind pose, and DrawMeshInstancedIndirect culls the whole batch against these bounds.</summary>
		const float BoundsMargin = 1f;

		static readonly int BoneMatrixStride = Marshal.SizeOf(typeof(GpuBoneMatrix));
		static readonly int InstanceDataStride = Marshal.SizeOf(typeof(GpuSpineInstanceData));

		/// <summary>The baked entry this batch draws.</summary>
		public readonly GpuSpineBakedEntry Entry;
		/// <summary>Submesh of <see cref="Entry"/> this batch draws.</summary>
		public readonly int SubmeshIndex;

		readonly List<GpuSkeletonRenderer> instances = new List<GpuSkeletonRenderer>();
		readonly List<GpuSkeletonRenderer> submission = new List<GpuSkeletonRenderer>();
		readonly uint[] argsArray = new uint[5];

		Material material;
		ComputeBuffer paletteBuffer;   // GpuBoneMatrix[instanceCapacity * boneCount]
		ComputeBuffer instanceBuffer;  // GpuSpineInstanceData[instanceCapacity]
		ComputeBuffer argsBuffer;      // uint[5] indirect args
		ComputeBuffer dynSlotBuffer;   // uint[instanceCapacity * dynamicSlotCount], null for static-only entries
		ComputeBuffer deformBuffer;    // float2[instanceCapacity * deformStride], null for deform-less entries
		ComputeBuffer slotColorBuffer; // float4[instanceCapacity * slotCount], null for slot-less entries
		GpuBoneMatrix[] paletteStaging;
		GpuSpineInstanceData[] instanceStaging;
		uint[] dynSlotStaging;
		Vector2[] deformStaging;
		Vector4[] slotColorStaging;
		int instanceCapacity;
		int membershipVersion;
		int uploadedMembershipVersion = -1;
		int uploadedInstanceCount = -1;
		Bounds bounds;

		public GpuSpineBatch (GpuSpineBakedEntry entry, int submeshIndex, Material batchMaterial) {
			Entry = entry;
			SubmeshIndex = submeshIndex;
			material = batchMaterial;
			argsBuffer = new ComputeBuffer(1, argsArray.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
			GpuSpineSubmesh submesh = entry.Submeshes[submeshIndex];
			argsArray[0] = (uint)submesh.IndexCount;   // index count per instance
			argsArray[1] = 0;                          // instance count, updated per frame
			argsArray[2] = (uint)submesh.IndexStart;   // start index location
			argsArray[3] = (uint)entry.Mesh.GetBaseVertex(submeshIndex); // 0 for SetTriangles-built meshes
			argsArray[4] = 0;                          // reserved
			EnsureCapacity(InitialInstanceCapacity);
		}

		/// <summary>Number of registered instances (submitted or not).</summary>
		public int RegisteredCount { get { return instances.Count; } }
		/// <summary>Number of instances submitted in the last <see cref="SubmitFrame"/>.</summary>
		public int SubmittedCount { get; private set; }
		/// <summary>The cloned batch material.</summary>
		public Material Material { get { return material; } }
		/// <summary>The uint[5] indirect args buffer.</summary>
		public ComputeBuffer ArgsBuffer { get { return argsBuffer; } }
		/// <summary>World-space union bounds of the last submitted frame.</summary>
		public Bounds Bounds { get { return bounds; } }

		public void Add (GpuSkeletonRenderer renderer) {
			instances.Add(renderer);
			membershipVersion++;
		}

		public void Remove (GpuSkeletonRenderer renderer) {
			if (instances.Remove(renderer)) membershipVersion++;
		}

		/// <summary>
		/// Builds the submission set (registered instances that are active, GPU-active and Visible),
		/// sorts it back-to-front, fills and uploads the palette and instance data buffers, updates the
		/// args buffer and submits Graphics.DrawMeshInstancedIndirect. Shadow casting is off (the
		/// indirect path cannot join the cullResults shadow passes); shadow receiving stays on.
		/// </summary>
		public void SubmitFrame (Camera camera) {
			submission.Clear();
			for (int i = 0; i < instances.Count; i++) {
				GpuSkeletonRenderer renderer = instances[i];
				if (renderer != null && renderer.ShouldSubmit) submission.Add(renderer);
			}
			int count = submission.Count;
			SubmittedCount = count;
			if (count == 0) return;
			EnsureCapacity(count);

			// The sort mode of a batch is decided by its first submitted instance; mixing sort modes
			// within one (entry, submesh) batch is not supported.
			bool orderChanged = SortSubmission(submission[0].SortMode, camera);

			int boneCount = Entry.BoneCount;
			int dynamicSlotCount = Entry.DynamicSlotCount;
			int deformStride = Entry.DeformStride;
			int slotCount = Entry.SlotCount;
			bool paletteDirty = orderChanged
				|| membershipVersion != uploadedMembershipVersion
				|| count != uploadedInstanceCount;
			// The variant selection shares the palette's upload timing and reorder/membership
			// triggers; the staging layout [instanceIndex * dynamicSlotCount + dynSlotId] is filled
			// in the same loop, so sorted instance moves stay in sync automatically.
			bool dynamicSlotsDirty = paletteDirty;
			// The deform segment shares the same upload timing and triggers; the staging layout
			// [instanceIndex * deformStride] is filled in the same loop.
			bool deformDirty = paletteDirty;
			bool slotColorsDirty = paletteDirty;
			for (int i = 0; i < count; i++) {
				GpuSkeletonRenderer renderer = submission[i];
				GpuBoneMatrix[] bones = renderer.BoneMatrices;
				if (bones != null) Array.Copy(bones, 0, paletteStaging, i * boneCount, boneCount);
				if (renderer.ConsumePaletteDirty()) paletteDirty = true;
					if (dynamicSlotCount > 0) {
						uint[] selection = renderer.DynamicSlotSelection;
						if (selection != null) Array.Copy(selection, 0, dynSlotStaging, i * dynamicSlotCount, dynamicSlotCount);
						if (renderer.ConsumeDynamicSlotsDirty()) dynamicSlotsDirty = true;
					}
					if (slotCount > 0) {
						Vector4[] colors = renderer.SlotColors;
						if (colors != null) Array.Copy(colors, 0, slotColorStaging, i * slotCount, slotCount);
						else for (int s = 0; s < slotCount; s++) slotColorStaging[i * slotCount + s] = Vector4.one;
						if (renderer.ConsumeSlotColorsDirty()) slotColorsDirty = true;
					}
					if (deformStride > 0) {
						Vector2[] segment = renderer.DeformSegment;
						if (segment != null) Array.Copy(segment, 0, deformStaging, i * deformStride, deformStride);
						else Array.Clear(deformStaging, i * deformStride, deformStride);
						if (renderer.ConsumeDeformDirty()) deformDirty = true;
					}
				renderer.FillInstanceData(ref instanceStaging[i]);
			}
			if (paletteDirty) {
				paletteBuffer.SetData(paletteStaging, 0, 0, count * boneCount);
				uploadedMembershipVersion = membershipVersion;
			}
			if (dynamicSlotCount > 0 && dynamicSlotsDirty)
				if (dynamicSlotCount > 0 && dynamicSlotsDirty)
					dynSlotBuffer.SetData(dynSlotStaging, 0, 0, count * dynamicSlotCount);
				if (deformStride > 0 && deformDirty)
					deformBuffer.SetData(deformStaging, 0, 0, count * deformStride);
				if (slotCount > 0 && slotColorsDirty)
					slotColorBuffer.SetData(slotColorStaging, 0, 0, count * slotCount);
			// Instance data (transform, skeleton color, custom slots) changes untracked: upload every frame.
			instanceBuffer.SetData(instanceStaging, 0, 0, count);
			uploadedInstanceCount = count;

			argsArray[1] = (uint)count;
			argsBuffer.SetData(argsArray);

			bounds = ComputeBounds();

			Graphics.DrawMeshInstancedIndirect(Entry.Mesh, SubmeshIndex, material, bounds, argsBuffer,
				0, null, ShadowCastingMode.Off, true, submission[0].gameObject.layer);
		}

		/// <summary>Stable insertion sort, back-to-front (larger depth first). Returns true when any
		/// element moved. Cheap at Spine batch sizes and keeps registration order for equal keys.</summary>
		bool SortSubmission (GpuSpineSortMode mode, Camera camera) {
			if (mode == GpuSpineSortMode.None || submission.Count < 2) return false;
			if (mode == GpuSpineSortMode.CameraDepth && camera == null) return false;
			Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
			Vector3 cameraForward = camera != null ? camera.transform.forward : Vector3.forward;
			bool moved = false;
			for (int i = 1; i < submission.Count; i++) {
				GpuSkeletonRenderer item = submission[i];
				float key = DepthKey(item, mode, cameraPosition, cameraForward);
				int j = i - 1;
				while (j >= 0 && DepthKey(submission[j], mode, cameraPosition, cameraForward) < key) {
					submission[j + 1] = submission[j];
					j--;
				}
				submission[j + 1] = item;
				moved |= j + 1 != i;
			}
			return moved;
		}

		static float DepthKey (GpuSkeletonRenderer renderer, GpuSpineSortMode mode, Vector3 cameraPosition, Vector3 cameraForward) {
			Vector3 position = renderer.transform.position;
			if (mode == GpuSpineSortMode.WorldZBackToFront) return position.z;
			Vector3 delta = position - cameraPosition;
			return delta.x * cameraForward.x + delta.y * cameraForward.y + delta.z * cameraForward.z;
		}

		/// <summary>Union of the per-instance world bounds (baked bind-pose mesh bounds transformed by
		/// each instance's localToWorld), plus a fixed margin for pose deviation.</summary>
		Bounds ComputeBounds () {
			Bounds localBounds = Entry.Mesh.bounds;
			Bounds result = TransformBounds(submission[0].transform.localToWorldMatrix, localBounds);
			for (int i = 1; i < submission.Count; i++) {
				Bounds instanceBounds = TransformBounds(submission[i].transform.localToWorldMatrix, localBounds);
				result.Encapsulate(instanceBounds.min);
				result.Encapsulate(instanceBounds.max);
			}
			result.Expand(BoundsMargin * 2f);
			return result;
		}

		static Bounds TransformBounds (Matrix4x4 matrix, Bounds localBounds) {
			Vector3 center = localBounds.center;
			Vector3 extents = localBounds.extents;
			Bounds result = new Bounds(matrix.MultiplyPoint3x4(center), Vector3.zero);
			for (int i = 0; i < 8; i++) {
				Vector3 corner = center + new Vector3(
					(i & 1) == 0 ? extents.x : -extents.x,
					(i & 2) == 0 ? extents.y : -extents.y,
					(i & 4) == 0 ? extents.z : -extents.z);
				result.Encapsulate(matrix.MultiplyPoint3x4(corner));
			}
			return result;
		}

		void EnsureCapacity (int requiredInstances) {
			if (requiredInstances <= instanceCapacity) return;
			int capacity = Mathf.Max(InitialInstanceCapacity, Mathf.NextPowerOfTwo(requiredInstances));
			if (paletteBuffer != null) paletteBuffer.Release();
			if (instanceBuffer != null) instanceBuffer.Release();
			if (dynSlotBuffer != null) dynSlotBuffer.Release();
			if (deformBuffer != null) deformBuffer.Release();
			if (slotColorBuffer != null) slotColorBuffer.Release();
			paletteBuffer = new ComputeBuffer(capacity * Entry.BoneCount, BoneMatrixStride, ComputeBufferType.Structured);
			instanceBuffer = new ComputeBuffer(capacity, InstanceDataStride, ComputeBufferType.Default);
			paletteStaging = new GpuBoneMatrix[capacity * Entry.BoneCount];
			instanceStaging = new GpuSpineInstanceData[capacity];
			if (Entry.DynamicSlotCount > 0) {
				dynSlotBuffer = new ComputeBuffer(capacity * Entry.DynamicSlotCount, sizeof(uint), ComputeBufferType.Structured);
				dynSlotStaging = new uint[capacity * Entry.DynamicSlotCount];
			} else {
				// Static-only entry: no buffer, no _GpuSpineDynSlotCount binding (stays 0, which
				// short-circuits every dynamic slot read in the shader).
				dynSlotBuffer = null;
				dynSlotStaging = null;
				}
				if (Entry.SlotCount > 0) {
					slotColorBuffer = new ComputeBuffer(capacity * Entry.SlotCount, sizeof(float) * 4, ComputeBufferType.Structured);
					slotColorStaging = new Vector4[capacity * Entry.SlotCount];
				} else {
					// Slot-less entry (never happens in practice): no buffer, no _GpuSpineSlotCount
					// binding (stays 0, which short-circuits every slot color read in the shader).
					slotColorBuffer = null;
					slotColorStaging = null;
				}
				if (Entry.DeformStride > 0) {
					deformBuffer = new ComputeBuffer(capacity * Entry.DeformStride, sizeof(float) * 2, ComputeBufferType.Structured);
					deformStaging = new Vector2[capacity * Entry.DeformStride];
				} else {
					// Deform-less entry: no buffer, no _GpuSpineDeformStride binding (stays 0, which
					// short-circuits every deform read in the shader).
					deformBuffer = null;
					deformStaging = null;
				}
				instanceCapacity = capacity;
				// Buffer (re)creation requires rebinding; material-level SetBuffer, following ES2DInstance.
				material.SetBuffer("_GpuSpineBones", paletteBuffer);
				material.SetBuffer("_GpuSpineInstances", instanceBuffer);
				material.SetInt("_GpuSpineBoneCount", Entry.BoneCount);
				if (dynSlotBuffer != null) {
					material.SetBuffer("_GpuSpineDynSlots", dynSlotBuffer);
					material.SetInt("_GpuSpineDynSlotCount", Entry.DynamicSlotCount);
				}
				if (deformBuffer != null) {
					material.SetBuffer("_GpuSpineDeform", deformBuffer);
					material.SetInt("_GpuSpineDeformStride", Entry.DeformStride);
				}
				if (slotColorBuffer != null) {
					material.SetBuffer("_GpuSpineSlotColors", slotColorBuffer);
					material.SetInt("_GpuSpineSlotCount", Entry.SlotCount);
				}
			}

		/// <summary>Releases all buffers and destroys the cloned material.</summary>
		public void Dispose () {
			if (paletteBuffer != null) { paletteBuffer.Release(); paletteBuffer = null; }
			if (instanceBuffer != null) { instanceBuffer.Release(); instanceBuffer = null; }
			if (argsBuffer != null) { argsBuffer.Release(); argsBuffer = null; }
			if (dynSlotBuffer != null) { dynSlotBuffer.Release(); dynSlotBuffer = null; }
			if (deformBuffer != null) { deformBuffer.Release(); deformBuffer = null; }
			if (dynSlotBuffer != null) { dynSlotBuffer.Release(); dynSlotBuffer = null; }
			if (deformBuffer != null) { deformBuffer.Release(); deformBuffer = null; }
			if (slotColorBuffer != null) { slotColorBuffer.Release(); slotColorBuffer = null; }
			paletteStaging = null;
			instanceStaging = null;
			dynSlotStaging = null;
			deformStaging = null;
			slotColorStaging = null;
			instanceCapacity = 0;
			SubmittedCount = 0;
			if (material != null) {
				// The manager only exists in play mode, so Destroy (not DestroyImmediate) is correct.
				UnityEngine.Object.Destroy(material);
				material = null;
			}
			instances.Clear();
			submission.Clear();
		}
	}
}
