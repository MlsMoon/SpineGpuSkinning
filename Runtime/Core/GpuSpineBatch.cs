using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GpuSpine.Baking;
using UnityEngine;
using Unity.Profiling;
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
    /// 同一骨架数据、图集材质和相机状态的共享上传组。
    /// 布局视图共用缓冲，独立切片持有本帧几何范围；材质按实例偏移共享。
    /// </summary>
	internal sealed class GpuSpineBatch : IDisposable {
		/// <summary>记录实际容量增长，首次提交才分配实例缓冲。</summary>
		static readonly ProfilerMarker CapacityMarker = new ProfilerMarker("GpuSpine.Batch.EnsureCapacity");
		internal static int CapacityGrowths;
		/// <summary>World-space margin added to the submission bounds; live poses deviate from the
		/// baked bind pose, and DrawMeshInstancedIndirect culls the whole batch against these bounds.</summary>
		const float BoundsMargin = 1f;

		static readonly int BoneMatrixStride = Marshal.SizeOf(typeof(GpuBoneMatrix));
		static readonly int InstanceDataStride = Marshal.SizeOf(typeof(GpuSpineInstanceData));

		/// <summary>The baked entry this batch draws.</summary>
		public readonly GpuSpineBakedEntry Entry;

		readonly List<GpuSkeletonRenderer> instances = new List<GpuSkeletonRenderer>();
		readonly List<GpuSkeletonRenderer> submission = new List<GpuSkeletonRenderer>();
        readonly Dictionary<GpuSpineSliceKey, GpuSpineDrawSlice> frameSlices = new();
        readonly List<GpuSpineDrawSlice> slicePool = new();
        readonly Dictionary<int, Material> offsetMaterials = new();
        int usedSlices;

		Material material;
		ComputeBuffer paletteBuffer;   // GpuBoneMatrix[instanceCapacity * boneCount]
		ComputeBuffer instanceBuffer;  // GpuSpineInstanceData[instanceCapacity]
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
		GpuSpineUploadVersions[] uploadedVersions, pendingVersions;
		GpuSkeletonRenderer[] pendingSources;
		GpuSkeletonRenderer[] uploadedSources;

		int instanceCapacity;
		int membershipVersion;
		int uploadedMembershipVersion = -1;
		int uploadedInstanceCount = -1;
		Bounds bounds;

        public GpuSpineBatch(GpuSpineBakedEntry entry, Material batchMaterial) {
            Entry = entry; material = batchMaterial;
            material.SetInt("_GpuSpineInstanceOffset", 0);
        }

		/// <summary>Number of registered instances (submitted or not).</summary>
		public int RegisteredCount { get { return instances.Count; } }
		/// <summary>Number of instances submitted in the last <see cref="SubmitFrame"/>.</summary>
		public int SubmittedCount { get; private set; }
		/// <summary>The cloned batch material.</summary>
		public Material Material { get { return material; } }
		/// <summary>World-space union bounds of the last submitted frame.</summary>
		public Bounds Bounds { get { return bounds; } }

		public int FindSourceIndex (GpuSkeletonRenderer source) => submission.IndexOf(source);

		/// <summary>Process-wide slice creation counter (monotonic), for allocation-churn diagnostics.</summary>
		internal static int SlicesCreated;

        /// <summary>只按当前帧的实际绘制数复用 args，布局历史不再累积材质和 args 对象。</summary>
        public GpuSpineBatchInfo GetSlice(GpuSpineSubmesh part, int start, int count) {
            using var sliceScope=GpuSpineCpuDiagnostics.Slice.Auto();
            var key = new GpuSpineSliceKey(part.IndexStart, part.IndexCount, start, count);
            if (!frameSlices.TryGetValue(key, out GpuSpineDrawSlice slice)) {
                if (usedSlices == slicePool.Count) { slicePool.Add(new GpuSpineDrawSlice()); SlicesCreated++; }
                slice = slicePool[usedSlices++];
                slice.Configure(GetOffsetMaterial(start), part, count);
                frameSlices.Add(key, slice);
            }
            return new GpuSpineBatchInfo {
                Mesh = Entry.Mesh, SubmeshIndex = 0, Material = slice.Material,
                ArgsBuffer = slice.Args, Bounds = ComputeBounds(start, count), InstanceCount = count
            };
        }
        /// <summary>同一实例偏移共享材质；不同几何切片只拥有独立 args。</summary>
        Material GetOffsetMaterial(int start) {
            if (start == 0) return material;
            if (!offsetMaterials.TryGetValue(start, out Material result)) {
                result = new Material(material);
                result.SetInt("_GpuSpineInstanceOffset", start);
                BindBuffers(result);
                offsetMaterials.Add(start, result);
            }
            return result;
        }

		public void Add (GpuSkeletonRenderer renderer) {
			instances.Add(renderer);
			membershipVersion++;
		}

		public void Remove (GpuSkeletonRenderer renderer) {
			if (instances.Remove(renderer)) membershipVersion++;
		}

        /// <summary>按相机排序共享实例，重置本帧切片查询，并上传变化的骨骼与实例数据。</summary>
		public void PrepareFrame (Camera camera) {
            frameSlices.Clear(); usedSlices = 0;
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
            int frameMembership = membershipVersion;
            bool force = orderChanged || frameMembership != uploadedMembershipVersion || count != uploadedInstanceCount;
            bool paletteDirty = force, dynamicSlotsDirty = force, deformDirty = force, slotColorsDirty = force, clippingDirty = force;
            using (GpuSpineCpuDiagnostics.Staging.Auto()) {
                for (int i = 0; i < count; i++) {
                    GpuSkeletonRenderer renderer = submission[i];
                    var versions = new GpuSpineUploadVersions(renderer);
                    var uploaded = uploadedVersions[i];
                    pendingSources[i] = renderer;
                    pendingVersions[i] = versions;
                    bool moved = force || uploadedSources[i] != renderer;
                    if (moved || uploaded.Palette != versions.Palette) {
                        paletteDirty = true;
                        if (renderer.BoneMatrices != null)
                            GpuSpineCpuDiagnostics.Copy(GpuSpineDataChannel.Palette, renderer.BoneMatrices, 0, paletteStaging, i * boneCount, boneCount, BoneMatrixStride);
                        else { Array.Clear(paletteStaging, i * boneCount, boneCount); GpuSpineCpuDiagnostics.RecordCopy(GpuSpineDataChannel.Palette, boneCount, BoneMatrixStride); }
                    }
                    if (dynamicSlotCount > 0 && (moved || uploaded.Dynamic != versions.Dynamic)) {
                        dynamicSlotsDirty = true;
                        if (renderer.DynamicSlotSelection != null)
                            GpuSpineCpuDiagnostics.Copy(GpuSpineDataChannel.DynamicSlots, renderer.DynamicSlotSelection, 0, dynSlotStaging, i * dynamicSlotCount, dynamicSlotCount, 4);
                        else { Array.Clear(dynSlotStaging, i * dynamicSlotCount, dynamicSlotCount); GpuSpineCpuDiagnostics.RecordCopy(GpuSpineDataChannel.DynamicSlots, dynamicSlotCount, 4); }
                    }
                    if (deformStride > 0 && (moved || uploaded.Deform != versions.Deform)) {
                        deformDirty = true;
                        if (renderer.DeformSegment != null)
                            GpuSpineCpuDiagnostics.Copy(GpuSpineDataChannel.Deform, renderer.DeformSegment, 0, deformStaging, i * deformStride, deformStride, 8);
                        else { Array.Clear(deformStaging, i * deformStride, deformStride); GpuSpineCpuDiagnostics.RecordCopy(GpuSpineDataChannel.Deform, deformStride, 8); }
                    }
                    if (slotCount > 0 && (moved || uploaded.Colors != versions.Colors)) {
                        slotColorsDirty = true;
                        if (renderer.SlotColors != null)
                            GpuSpineCpuDiagnostics.Copy(GpuSpineDataChannel.SlotColors, renderer.SlotColors, 0, slotColorStaging, i * slotCount, slotCount, 16);
                        else {
                            for (int slot = 0; slot < slotCount; slot++) slotColorStaging[i * slotCount + slot] = Vector4.one;
                            GpuSpineCpuDiagnostics.RecordCopy(GpuSpineDataChannel.SlotColors, slotCount, 16);
                        }
                    }
                    if (Entry.ClipVertexCapacity > 0 && (moved || uploaded.Clipping != versions.Clipping)) {
                        clippingDirty = true;
                        if (renderer.Clipping != null) {
                            GpuSpineCpuDiagnostics.Copy(GpuSpineDataChannel.Clipping, renderer.Clipping.TriangleVertices, 0, clipVertexStaging, i * Entry.ClipVertexCapacity, Entry.ClipVertexCapacity, 8);
                            GpuSpineCpuDiagnostics.Copy(GpuSpineDataChannel.Clipping, renderer.Clipping.SlotRanges, 0, clipRangeStaging, i * slotCount, slotCount, 8);
                        } else {
                            // IgnoreClipping 实例：范围段清零，shader 侧 range.y == 0 直接保留全部片元。
                            // 顶点段无需处理（范围为零时不会被读取），布局与容量保持不变。
                            Array.Clear(clipRangeStaging, i * slotCount, slotCount);
                            GpuSpineCpuDiagnostics.RecordCopy(GpuSpineDataChannel.Clipping, slotCount, 8);
                        }
                    }
                    renderer.BatchInstanceIndex = i;
                    renderer.FillInstanceData(ref instanceStaging[i]);
                    GpuSpineCpuDiagnostics.RecordCopy(GpuSpineDataChannel.Instances, 1, InstanceDataStride);
                }
            }
			if (paletteDirty) {
				GpuSpineCpuDiagnostics.SetData(GpuSpineDataChannel.Palette, paletteBuffer, paletteStaging, count * boneCount, BoneMatrixStride);

			}
				if (dynamicSlotCount > 0 && dynamicSlotsDirty)
					GpuSpineCpuDiagnostics.SetData(GpuSpineDataChannel.DynamicSlots, dynSlotBuffer, dynSlotStaging, count * dynamicSlotCount, 4);
				if (deformStride > 0 && deformDirty)
					GpuSpineCpuDiagnostics.SetData(GpuSpineDataChannel.Deform, deformBuffer, deformStaging, count * deformStride, 8);
				if (slotCount > 0 && slotColorsDirty)
					GpuSpineCpuDiagnostics.SetData(GpuSpineDataChannel.SlotColors, slotColorBuffer, slotColorStaging, count * slotCount, 16);
			if (Entry.ClipVertexCapacity > 0 && clippingDirty) {
				GpuSpineCpuDiagnostics.SetData(GpuSpineDataChannel.Clipping, clipVertexBuffer, clipVertexStaging, count * Entry.ClipVertexCapacity, 8);
				GpuSpineCpuDiagnostics.SetData(GpuSpineDataChannel.Clipping, clipRangeBuffer, clipRangeStaging, count * slotCount, 8);
			}
			// Instance data (transform, skeleton color, custom slots) changes untracked: upload every frame.
			GpuSpineCpuDiagnostics.SetData(GpuSpineDataChannel.Instances, instanceBuffer, instanceStaging, count, InstanceDataStride);
            // Commit after every upload succeeds; failures leave the previous versions for retry.
            for (int i = 0; i < count; i++) {
                uploadedSources[i] = pendingSources[i];
                uploadedVersions[i] = pendingVersions[i];
            }
            uploadedMembershipVersion = frameMembership;
            uploadedInstanceCount = count;


			bounds = ComputeBounds(0, count);

		}

		/// <summary>Stable insertion sort, back-to-front (larger depth first). Returns true when any
		/// element moved. Cheap at Spine batch sizes and keeps registration order for equal keys.</summary>
		bool SortSubmission (GpuSpineSortMode mode, Camera camera) {
            using var sortScope=GpuSpineCpuDiagnostics.BatchSort.Auto();
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
			using var capacityScope = CapacityMarker.Auto();
			int capacity = Mathf.Max(1, Mathf.NextPowerOfTwo(requiredInstances));
			CapacityGrowths++;
			uploadedMembershipVersion = -1;
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
			uploadedVersions = new GpuSpineUploadVersions[capacity];
			pendingVersions = new GpuSpineUploadVersions[capacity];
			pendingSources = new GpuSkeletonRenderer[capacity];
			material.SetInt("_GpuSpineInstanceFilter", -1);
			if (clipVertexBuffer != null) clipVertexBuffer.Release();
			if (clipRangeBuffer != null) clipRangeBuffer.Release();
			if (Entry.ClipVertexCapacity > 0) {
				clipVertexBuffer = new ComputeBuffer(capacity * Entry.ClipVertexCapacity, 8);
				clipRangeBuffer = new ComputeBuffer(capacity * Entry.SlotCount, 8);
				clipVertexStaging = new Vector2[capacity * Entry.ClipVertexCapacity];
				clipRangeStaging = new Vector2[capacity * Entry.SlotCount];


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
			foreach (Material offsetMaterial in offsetMaterials.Values) BindBuffers(offsetMaterial);
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
			foreach (GpuSpineDrawSlice slice in slicePool) slice.Dispose();
			slicePool.Clear(); frameSlices.Clear();
            foreach (Material offsetMaterial in offsetMaterials.Values) UnityEngine.Object.Destroy(offsetMaterial);
            offsetMaterials.Clear();
			if (clipVertexBuffer != null) { clipVertexBuffer.Release(); clipVertexBuffer = null; }
			if (clipRangeBuffer != null) { clipRangeBuffer.Release(); clipRangeBuffer = null; }
			if (paletteBuffer != null) { paletteBuffer.Release(); paletteBuffer = null; }
			if (instanceBuffer != null) { instanceBuffer.Release(); instanceBuffer = null; }
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
