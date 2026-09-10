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
		public GraphicsBuffer ArgsBuffer;
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
		readonly Dictionary<ulong, GpuSpineDrawSlice> slices = new Dictionary<ulong, GpuSpineDrawSlice>();

		Material material;
		ComputeBuffer paletteBuffer;   // GpuBoneMatrix[instanceCapacity * boneCount]
		ComputeBuffer instanceBuffer;  // GpuSpineInstanceData[instanceCapacity]
		GraphicsBuffer argsBuffer;      // uint[5] indirect args
		ComputeBuffer dynSlotBuffer;   // uint[instanceCapacity * dynamicSlotCount], null for static-only entries
		ComputeBuffer deformBuffer;    // float2[instanceCapacity * deformStride], null for deform-less entries
		ComputeBuffer slotColorBuffer; // float4[instanceCapacity * slotCount], null for slot-less entries
		GpuBoneMatrix[] paletteStaging;
		GpuSpineInstanceData[] instanceStaging;
		uint[] dynSlotStaging;
		Vector2[] deformStaging;
		Vector4[] slotColorStaging;
		ComputeBuffer clipVertexBuffer, clipRangeBuffer;
		Vector2[] clipVertexStaging, clipRangeStaging;
		ulong[] clipVersions;
		GpuSkeletonRenderer[] uploadedSources;
		ulong[] paletteVersions, dynamicVersions, deformVersions, colorVersions;
		int instanceCapacity;
		int membershipVersion;
		int uploadedMembershipVersion = -1;
		int uploadedInstanceCount = -1;
		Bounds bounds;

		public GpuSpineBatch (GpuSpineBakedEntry entry, int submeshIndex, Material batchMaterial) {
			Entry = entry;
			SubmeshIndex = submeshIndex;
			material = batchMaterial;
			argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
			GpuSpineSubmesh submesh = entry.Submeshes[submeshIndex];
			argsArray[0] = (uint)submesh.IndexCount;   // index count per instance
			argsArray[1] = 0;                          // instance count, updated per frame
			argsArray[2] = (uint)submesh.IndexStart;   // start index location
			// 0 for SetTriangles-built meshes. Layout-view entries share one combined layout mesh
			// (subMeshCount 1) whose batches address index ranges through the args buffer instead of
			// real submeshes, so the base vertex lookup must not index past the mesh's submesh count.
			argsArray[3] = submeshIndex < entry.Mesh.subMeshCount ? (uint)entry.Mesh.GetBaseVertex(submeshIndex) : 0u;
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
		public GraphicsBuffer ArgsBuffer { get { return argsBuffer; } }
		/// <summary>World-space union bounds of the last submitted frame.</summary>
		public Bounds Bounds { get { return bounds; } }

		public int FindSourceIndex (GpuSkeletonRenderer source) => submission.IndexOf(source);

		/// <summary>Process-wide slice creation counter (monotonic), for allocation-churn diagnostics.</summary>
		internal static int SlicesCreated;

		public GpuSpineBatchInfo GetSlice (int start, int count) {
			ulong key = ((ulong)(uint)start << 32) | (uint)count;
			if (!slices.TryGetValue(key, out GpuSpineDrawSlice slice)) {
				slice = new GpuSpineDrawSlice(material, argsArray, start);
				BindBuffers(slice.Material);
				slices.Add(key, slice);
				SlicesCreated++;
			}
			slice.SetCount(count);
			// The reported submesh index feeds CommandBuffer.DrawMeshInstancedIndirect, which
			// validates it against mesh.subMeshCount; batches on a combined layout mesh report
			// submesh 0 and let the args buffer select the index range (same triangle topology).
			return new GpuSpineBatchInfo {
				Mesh = Entry.Mesh, SubmeshIndex = Entry.Mesh.subMeshCount > SubmeshIndex ? SubmeshIndex : 0,
				Material = slice.Material, ArgsBuffer = slice.Args,
				Bounds = ComputeBounds(start, count), InstanceCount = count
			};
		}

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
		public void PrepareFrame (Camera camera) {
			submission.Clear();
			for (int i = 0; i < instances.Count; i++) {
				GpuSkeletonRenderer renderer = instances[i];
				if (renderer != null) {
					renderer.BatchInstanceIndex = -1;
					if (renderer.ShouldSubmit) submission.Add(renderer);
				}
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
			bool clippingDirty = paletteDirty;
			for (int i = 0; i < count; i++) {
				GpuSkeletonRenderer renderer = submission[i];
				GpuBoneMatrix[] bones = renderer.BoneMatrices;
				if (bones != null) Array.Copy(bones, 0, paletteStaging, i * boneCount, boneCount);
				bool sourceChanged = uploadedSources[i] != renderer;
				if (Entry.ClipVertexCapacity > 0) {
					clippingDirty |= sourceChanged || clipVersions[i] != renderer.ClippingVersion;
					clipVersions[i] = renderer.ClippingVersion;
					Array.Copy(renderer.Clipping.TriangleVertices, 0, clipVertexStaging, i * Entry.ClipVertexCapacity, Entry.ClipVertexCapacity);
					Array.Copy(renderer.Clipping.SlotRanges, 0, clipRangeStaging, i * slotCount, slotCount);
				}
				paletteDirty |= sourceChanged || paletteVersions[i] != renderer.PaletteVersion;
				dynamicSlotsDirty |= sourceChanged || dynamicVersions[i] != renderer.DynamicSlotsVersion;
				deformDirty |= sourceChanged || deformVersions[i] != renderer.DeformVersion;
				slotColorsDirty |= sourceChanged || colorVersions[i] != renderer.SlotColorsVersion;
				uploadedSources[i] = renderer;
				paletteVersions[i] = renderer.PaletteVersion;
				dynamicVersions[i] = renderer.DynamicSlotsVersion;
				deformVersions[i] = renderer.DeformVersion;
				colorVersions[i] = renderer.SlotColorsVersion;
					if (dynamicSlotCount > 0) {
						uint[] selection = renderer.DynamicSlotSelection;
						if (selection != null) Array.Copy(selection, 0, dynSlotStaging, i * dynamicSlotCount, dynamicSlotCount);
					}
					if (slotCount > 0) {
						Vector4[] colors = renderer.SlotColors;
						if (colors != null) Array.Copy(colors, 0, slotColorStaging, i * slotCount, slotCount);
						else for (int s = 0; s < slotCount; s++) slotColorStaging[i * slotCount + s] = Vector4.one;
					}
					if (deformStride > 0) {
						Vector2[] segment = renderer.DeformSegment;
						if (segment != null) Array.Copy(segment, 0, deformStaging, i * deformStride, deformStride);
						else Array.Clear(deformStaging, i * deformStride, deformStride);
					}
				renderer.BatchInstanceIndex = i; // Submission index == SV_InstanceID of this draw.
				renderer.FillInstanceData(ref instanceStaging[i]);
			}
			if (paletteDirty) {
				paletteBuffer.SetData(paletteStaging, 0, 0, count * boneCount);
				uploadedMembershipVersion = membershipVersion;
			}
				if (dynamicSlotCount > 0 && dynamicSlotsDirty)
					dynSlotBuffer.SetData(dynSlotStaging, 0, 0, count * dynamicSlotCount);
				if (deformStride > 0 && deformDirty)
					deformBuffer.SetData(deformStaging, 0, 0, count * deformStride);
				if (slotCount > 0 && slotColorsDirty)
					slotColorBuffer.SetData(slotColorStaging, 0, 0, count * slotCount);
			if (Entry.ClipVertexCapacity > 0 && clippingDirty) {
				clipVertexBuffer.SetData(clipVertexStaging, 0, 0, count * Entry.ClipVertexCapacity);
				clipRangeBuffer.SetData(clipRangeStaging, 0, 0, count * slotCount);
			}
			// Instance data (transform, skeleton color, custom slots) changes untracked: upload every frame.
			instanceBuffer.SetData(instanceStaging, 0, 0, count);
			uploadedInstanceCount = count;

			argsArray[1] = (uint)count;
			argsBuffer.SetData(argsArray);

			bounds = ComputeBounds(0, count);

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
		Bounds ComputeBounds (int start, int count) {
			Bounds result = submission[start].WorldBounds;
			for (int i = start + 1; i < start + count; i++) result.Encapsulate(submission[i].WorldBounds);
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
			uploadedSources = new GpuSkeletonRenderer[capacity];
			paletteVersions = new ulong[capacity];
			dynamicVersions = new ulong[capacity];
			deformVersions = new ulong[capacity];
			colorVersions = new ulong[capacity];
			material.SetInt("_GpuSpineInstanceFilter", -1);
			if (clipVertexBuffer != null) clipVertexBuffer.Release();
			if (clipRangeBuffer != null) clipRangeBuffer.Release();
			if (Entry.ClipVertexCapacity > 0) {
				clipVertexBuffer = new ComputeBuffer(capacity * Entry.ClipVertexCapacity, 8);
				clipRangeBuffer = new ComputeBuffer(capacity * Entry.SlotCount, 8);
				clipVertexStaging = new Vector2[capacity * Entry.ClipVertexCapacity];
				clipRangeStaging = new Vector2[capacity * Entry.SlotCount];
				clipVersions = new ulong[capacity];

			}

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
			BindBuffers(material);
			foreach (GpuSpineDrawSlice slice in slices.Values) BindBuffers(slice.Material);
		}

        void BindBuffers (Material target) {
            // Material copies do not retain runtime uniforms absent from ShaderLab Properties.
            target.SetInt("_GpuSpineInstanceFilter", -1);
            target.SetInt("_GpuSpineRenderingLayerMask", material.GetInt("_GpuSpineRenderingLayerMask"));
			target.SetBuffer("_GpuSpineBones", paletteBuffer);
			target.SetBuffer("_GpuSpineInstances", instanceBuffer);
			target.SetInt("_GpuSpineBoneCount", Entry.BoneCount);
			target.SetInt("_GpuSpineDynSlotCount", Entry.DynamicSlotCount);
			target.SetInt("_GpuSpineDeformStride", Entry.DeformStride);
			target.SetInt("_GpuSpineSlotCount", Entry.SlotCount);
			target.SetInt("_GpuSpineClipStride", Entry.ClipVertexCapacity);
			if (dynSlotBuffer != null) target.SetBuffer("_GpuSpineDynSlots", dynSlotBuffer);
			if (deformBuffer != null) target.SetBuffer("_GpuSpineDeform", deformBuffer);
			if (slotColorBuffer != null) target.SetBuffer("_GpuSpineSlotColors", slotColorBuffer);
			if (clipVertexBuffer != null) {
				target.SetBuffer("_GpuSpineClipVertices", clipVertexBuffer);
				target.SetBuffer("_GpuSpineClipRanges", clipRangeBuffer);
			}
		}

		/// <summary>Releases all buffers and destroys the cloned material.</summary>
		public void Dispose () {
			foreach (GpuSpineDrawSlice slice in slices.Values) slice.Dispose();
			slices.Clear();
			if (clipVertexBuffer != null) { clipVertexBuffer.Release(); clipVertexBuffer = null; }
			if (clipRangeBuffer != null) { clipRangeBuffer.Release(); clipRangeBuffer = null; }
			if (paletteBuffer != null) { paletteBuffer.Release(); paletteBuffer = null; }
			if (instanceBuffer != null) { instanceBuffer.Release(); instanceBuffer = null; }
			if (argsBuffer != null) { argsBuffer.Release(); argsBuffer = null; }
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
