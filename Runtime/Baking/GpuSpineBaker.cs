using System;
using System.Collections.Generic;
using Spine;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuSpine.Baking {
	/// <summary>
	/// Bakes a <see cref="SkeletonData"/> plus one skin combination into a <see cref="GpuSpineBakedEntry"/>,
	/// 1:1 aligned with the CPU path (MeshGenerator.BuildMeshWithArrays +
	/// VertexAttachment.ComputeWorldVertices), except that the weighted sum over bone matrices is
	/// deferred to the GPU: every influence contributes its own per-influence local coordinate, bone
	/// index and weight as vertex streams. This is a pure function: the returned entry and its mesh are
	/// not persisted; the caller decides where to store them (the editor bake processor adds them as
	/// sub-assets of the SkeletonDataAsset's container).
	/// <para/>
	/// Vertex data is already scaled at load time (SkeletonJson/SkeletonBinary applied the asset scale),
	/// so no additional scale is applied here.
	/// <para/>
	/// Mesh vertex layout: POSITION = local (x, y) of influence 0 with z = zSpacing * setup draw order
	/// index; TEXCOORD0 = atlas uv (copied verbatim, no atlas conversion); COLOR = attachment color with
	/// alpha forced to 0 for additive slots (PMA additive trick); TEXCOORD1 = (vx1, vy1, vx2, vy2);
	/// TEXCOORD2 = (vx3, vy3); TEXCOORD3 = 4 bone indices (integers as floats); TEXCOORD4 = 4 weights;
	/// TEXCOORD5 = (dynSlotId, variantId) for dynamic slot variant vertices, (-1, -1) for static ones.
	/// <para/>
	/// Dynamic slots (from the audit's <see cref="GpuSpineAuditReport.DynamicSlots"/>) emit no static
	/// vertices: their setup attachment is just one variant. Every renderable variant resolvable through
	/// the entry's effective skin (default skin fallback) is appended after the static zone in
	/// (dynSlotId, variantId) order and recorded in <see cref="GpuSpineBakedEntry.DynamicVariants"/>.
	/// </summary>
	public static class GpuSpineBaker {
		const int MaxInfluences = 4;
		/// <summary>Defensive cap on the total vertex count of one entry (static zone plus all dynamic
		/// variants). Exceeding it is a bake-time hard failure: the entry is recorded with a null mesh
		/// and the audit gains a failure.</summary>
		const int MaxVertexCount = 1 << 20;

		/// <summary>Bakes one entry. Does not consult <paramref name="audit"/>.Passed; gating on the
		/// audit result is the caller's decision. Baker-side findings (influence truncation, vertex
		/// count overflow) are appended to <paramref name="audit"/>.</summary>
		/// <param name="data">The skeleton data (shared by all instances of the same asset).</param>
		/// <param name="skinNames">Skin names of the combination in AddSkin order; null or empty bakes
		/// the default entry. Every name must exist in SkeletonData.Skins (validated by the caller).</param>
		/// <param name="zSpacing">zSpacing baked into vertex z (z = zSpacing * setup draw order index).
		/// The editor bakes with 0 (v1 simplification, recorded on the container).</param>
		/// <param name="audit">The audit of <paramref name="data"/>; drives the dynamic slot expansion
		/// and receives baker warnings and failures. May be null (treated as an empty passed audit).</param>
		public static GpuSpineBakedEntry Bake (SkeletonData data, string[] skinNames, float zSpacing, GpuSpineAuditReport audit) {
			if (data == null) throw new ArgumentNullException("data");
			if (audit == null) audit = new GpuSpineAuditReport { Passed = true };

			string displayName;
			Skin effectiveSkin = BuildEffectiveSkin(data, skinNames, out displayName);
			string key = GpuSpineBakeKey.Compute(data, effectiveSkin);

			ExposedList<BoneData> boneList = data.Bones;
			int boneCount = boneList.Count;
			bool[] boneActive = ComputeActiveBones(boneList, effectiveSkin);

			Skin defaultSkin = data.DefaultSkin;
			SlotData[] slots = data.Slots.Items;
			int slotCount = data.Slots.Count;
			List<GpuSpineDynamicSlotInfo> dynamicSlots = audit.DynamicSlots;
			bool[] slotIsDynamic = BuildDynamicSlotMap(dynamicSlots, slotCount);

			MeshBuffers buffers = new MeshBuffers();
			List<GpuSpineSubmesh> submeshes = new List<GpuSpineSubmesh>();
			List<GpuSpineDynamicSlotVariant> variants = new List<GpuSpineDynamicSlotVariant>();

			Material currentPageMaterial = null;
			int submeshIndexStart = 0;
			bool submeshHasPmaAdditiveSlot = false;
			Vector2 staticDynUv = new Vector2(-1f, -1f);

			// Static zone. Slots are stored in setup pose draw order (SkeletonData.cs:38), so this loop
			// replicates the CPU path's per-slot expansion in setup draw order, including skipped slots
			// consuming a z index. Dynamic slots emit no static vertices.
			for (int slotIndex = 0; slotIndex < slotCount; slotIndex++) {
				SlotData slotData = slots[slotIndex];
				// Same culling as the CPU path: slots on inactive bones emit no vertices.
				if (!boneActive[slotData.BoneData.Index]) continue;
				if (slotIsDynamic[slotIndex]) continue;
				// Setup pose attachment: effective skin first, then the default skin (Skeleton.GetAttachment semantics).
				string attachmentName = slotData.AttachmentName;
				if (attachmentName == null) continue;
				Attachment attachment = null;
				if (effectiveSkin != null) attachment = effectiveSkin.GetAttachment(slotIndex, attachmentName);
				if (attachment == null && defaultSkin != null) attachment = defaultSkin.GetAttachment(slotIndex, attachmentName);
				if (attachment == null) continue;

				TextureRegion textureRegion;
				RegionAttachment region = attachment as RegionAttachment;
				MeshAttachment meshAttachment = attachment as MeshAttachment;
				if (region != null) textureRegion = region.Region;
				else if (meshAttachment != null) textureRegion = meshAttachment.Region;
				else continue; // Point/BoundingBox/Path/Clipping attachments emit no vertices.

				// Submesh boundary = page material change; the page material reference is the batch key.
				Material pageMaterial = GetPageMaterial(textureRegion);
				if (!ReferenceEquals(pageMaterial, currentPageMaterial)) {
					CloseSubmesh(submeshes, currentPageMaterial, submeshIndexStart, buffers.triangles.Count, submeshHasPmaAdditiveSlot);
					currentPageMaterial = pageMaterial;
					submeshIndexStart = buffers.triangles.Count;
					submeshHasPmaAdditiveSlot = false;
				}
				bool additive = slotData.BlendMode == Spine.BlendMode.Additive;
				submeshHasPmaAdditiveSlot |= additive;

				float z = zSpacing * slotIndex;
				int vertexBase = buffers.positions.Count;
				if (region != null) {
					Color32 color = ToColor32(region.R, region.G, region.B, additive ? 0f : region.A);
					EmitRegion(region, slotData.BoneData.Index, color, z, vertexBase, staticDynUv, buffers);
				} else {
					Color32 color = ToColor32(meshAttachment.R, meshAttachment.G, meshAttachment.B, additive ? 0f : meshAttachment.A);
					EmitMesh(meshAttachment, slotData, color, z, vertexBase, staticDynUv, audit, buffers);
				}
			}

			// Dynamic zone: every renderable variant of every dynamic slot, appended after the static
			// zone. The submesh run continues seamlessly from the static zone (indices stay contiguous).
			for (int dynSlotId = 0, dynCount = dynamicSlots.Count; dynSlotId < dynCount; dynSlotId++) {
				GpuSpineDynamicSlotInfo info = dynamicSlots[dynSlotId];
				int slotIndex = info.SlotIndex;
				if (slotIndex < 0 || slotIndex >= slotCount) continue;
				SlotData slotData = slots[slotIndex];
				if (!boneActive[slotData.BoneData.Index]) continue;
				bool additive = slotData.BlendMode == Spine.BlendMode.Additive;
				float z = zSpacing * slotIndex;
				string[] names = info.AttachmentNames;
				int variantId = 0;
				for (int a = 0, an = names.Length; a < an; a++) {
					Attachment attachment = null;
					if (effectiveSkin != null) attachment = effectiveSkin.GetAttachment(slotIndex, names[a]);
					if (attachment == null && defaultSkin != null) attachment = defaultSkin.GetAttachment(slotIndex, names[a]);
					RegionAttachment region = attachment as RegionAttachment;
					MeshAttachment meshAttachment = attachment as MeshAttachment;
					TextureRegion textureRegion;
					if (region != null) textureRegion = region.Region;
					else if (meshAttachment != null) textureRegion = meshAttachment.Region;
					else continue; // Unresolved in this combination or non-renderable: no variant record.

					Material pageMaterial = GetPageMaterial(textureRegion);
					if (!ReferenceEquals(pageMaterial, currentPageMaterial)) {
						CloseSubmesh(submeshes, currentPageMaterial, submeshIndexStart, buffers.triangles.Count, submeshHasPmaAdditiveSlot);
						currentPageMaterial = pageMaterial;
						submeshIndexStart = buffers.triangles.Count;
						submeshHasPmaAdditiveSlot = false;
					}
					submeshHasPmaAdditiveSlot |= additive;

					Vector2 dynUv = new Vector2(dynSlotId, variantId);
					int vertexStart = buffers.positions.Count;
					if (region != null) {
						Color32 color = ToColor32(region.R, region.G, region.B, additive ? 0f : region.A);
						EmitRegion(region, slotData.BoneData.Index, color, z, vertexStart, dynUv, buffers);
					} else {
						Color32 color = ToColor32(meshAttachment.R, meshAttachment.G, meshAttachment.B, additive ? 0f : meshAttachment.A);
						EmitMesh(meshAttachment, slotData, color, z, vertexStart, dynUv, audit, buffers);
					}
					variants.Add(new GpuSpineDynamicSlotVariant {
						DynSlotId = dynSlotId,
						VariantId = variantId,
						AttachmentName = names[a],
						VertexStart = vertexStart,
						VertexCount = buffers.positions.Count - vertexStart
					});
					variantId++;
				}
			}
			CloseSubmesh(submeshes, currentPageMaterial, submeshIndexStart, buffers.triangles.Count, submeshHasPmaAdditiveSlot);

			string[] entrySkinNames = skinNames != null ? (string[])skinNames.Clone() : new string[0];
			if (buffers.positions.Count > MaxVertexCount) {
				Fail(audit, string.Format(
					"Entry '{0}' would bake {1} vertices (limit {2}): vertex count overflow.",
					displayName, buffers.positions.Count, MaxVertexCount));
				return new GpuSpineBakedEntry {
					Key = key,
					DisplayName = displayName,
					SkinNames = entrySkinNames,
					Mesh = null,
					Submeshes = new GpuSpineSubmesh[0],
					DynamicVariants = new GpuSpineDynamicSlotVariant[0],
					BoneCount = boneCount,
					VertexCount = 0,
					DynamicSlotCount = dynamicSlots.Count
				};
			}

			Mesh mesh = new Mesh();
			mesh.name = "GpuSpine_" + displayName + "_" + key;
			if (buffers.positions.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
			mesh.SetVertices(buffers.positions);
			mesh.SetUVs(0, buffers.uvs);
			mesh.SetUVs(1, buffers.uv2);
			mesh.SetUVs(2, buffers.uv3);
			mesh.SetUVs(3, buffers.uv4);
			mesh.SetUVs(4, buffers.uv5);
			mesh.SetUVs(5, buffers.uv6);
			// Mesh has no SetColors32; the colors32 property is the Color32 equivalent of SetColors.
			mesh.colors32 = buffers.colors.ToArray();
			if (submeshes.Count > 0) {
				mesh.subMeshCount = submeshes.Count;
				for (int s = 0; s < submeshes.Count; s++) {
					GpuSpineSubmesh submesh = submeshes[s];
					mesh.SetTriangles(buffers.triangles, submesh.IndexStart, submesh.IndexCount, s, false);
				}
			}
			mesh.RecalculateBounds();

			return new GpuSpineBakedEntry {
				Key = key,
				DisplayName = displayName,
				SkinNames = entrySkinNames,
				Mesh = mesh,
				Submeshes = submeshes.ToArray(),
				DynamicVariants = variants.ToArray(),
				BoneCount = boneCount,
				VertexCount = buffers.positions.Count,
				DynamicSlotCount = dynamicSlots.Count
			};
		}

		/// <summary>
		/// Builds the effective skin <see cref="Bake"/> resolves attachments through: null or empty
		/// <paramref name="skinNames"/> selects the default entry (null skin, default skin fallback
		/// only); one name selects that single skin; several names build a composite skin via
		/// Skin.AddSkin in array order (later skins override earlier ones per placeholder), matching
		/// the runtime composite semantics. Exposed so editor tooling can precompute entry keys with
		/// <see cref="GpuSpineBakeKey.Compute"/> without duplicating this construction. A name not found
		/// in SkeletonData.Skins contributes nothing (validated by the caller).
		/// </summary>
		public static Skin BuildEffectiveSkin (SkeletonData data, string[] skinNames, out string displayName) {
			if (data == null) throw new ArgumentNullException("data");
			if (skinNames == null || skinNames.Length == 0) {
				displayName = "default";
				return null;
			}
			if (skinNames.Length == 1) {
				displayName = skinNames[0];
				return data.FindSkin(skinNames[0]);
			}
			displayName = string.Join("+", skinNames);
			Skin composite = new Skin(displayName);
			for (int i = 0; i < skinNames.Length; i++) {
				Skin skin = data.FindSkin(skinNames[i]);
				if (skin != null) composite.AddSkin(skin);
			}
			return composite;
		}

		static bool[] BuildDynamicSlotMap (List<GpuSpineDynamicSlotInfo> dynamicSlots, int slotCount) {
			bool[] map = new bool[slotCount];
			for (int i = 0, n = dynamicSlots.Count; i < n; i++) {
				int slotIndex = dynamicSlots[i].SlotIndex;
				if (slotIndex >= 0 && slotIndex < slotCount) map[slotIndex] = true;
			}
			return map;
		}

		/// <summary>
		/// Data-side equivalent of Skeleton.UpdateCache (Skeleton.cs:227-242): a bone is active unless it is
		/// skin-required; bones contained in the effective skin, plus their ancestors, are active.
		/// </summary>
		static bool[] ComputeActiveBones (ExposedList<BoneData> boneList, Skin skin) {
			BoneData[] bones = boneList.Items;
			int boneCount = boneList.Count;
			bool[] active = new bool[boneCount];
			for (int i = 0; i < boneCount; i++) active[i] = !bones[i].SkinRequired;
			if (skin != null) {
				BoneData[] skinBones = skin.Bones.Items;
				for (int i = 0, n = skin.Bones.Count; i < n; i++) {
					for (BoneData bone = skinBones[i]; bone != null; bone = bone.Parent) {
						int index = bone.Index;
						if (index >= 0 && index < boneCount) active[index] = true;
					}
				}
			}
			return active;
		}

		static void EmitRegion (RegionAttachment region, int boneIndex, Color32 color, float z, int vertexBase, Vector2 dynUv, MeshBuffers buffers) {
			float[] offset = region.Offset;
			float[] uvs = region.UVs;
			// Vertex order replicates the CPU fast path: BL, BR, UL, UR (MeshGenerator.cs:944-951), paired
			// with triangles {0,2,1, 2,3,1} relative to this attachment's vertex base (MeshGenerator.cs:1115-1121).
			// Offset/UVs layout: BLX/BLY=0/1, ULX/ULY=2/3, URX/URY=4/5, BRX/BRY=6/7 (RegionAttachment.cs:35-38).
			EmitSingleInfluenceVertex(buffers, offset[RegionAttachment.BLX], offset[RegionAttachment.BLY],
				uvs[RegionAttachment.BLX], uvs[RegionAttachment.BLY], z, color, boneIndex, dynUv); // BL
			EmitSingleInfluenceVertex(buffers, offset[RegionAttachment.BRX], offset[RegionAttachment.BRY],
				uvs[RegionAttachment.BRX], uvs[RegionAttachment.BRY], z, color, boneIndex, dynUv); // BR
			EmitSingleInfluenceVertex(buffers, offset[RegionAttachment.ULX], offset[RegionAttachment.ULY],
				uvs[RegionAttachment.ULX], uvs[RegionAttachment.ULY], z, color, boneIndex, dynUv); // UL
			EmitSingleInfluenceVertex(buffers, offset[RegionAttachment.URX], offset[RegionAttachment.URY],
				uvs[RegionAttachment.URX], uvs[RegionAttachment.URY], z, color, boneIndex, dynUv); // UR
			buffers.triangles.Add(vertexBase + 0);
			buffers.triangles.Add(vertexBase + 2);
			buffers.triangles.Add(vertexBase + 1);
			buffers.triangles.Add(vertexBase + 2);
			buffers.triangles.Add(vertexBase + 3);
			buffers.triangles.Add(vertexBase + 1);
		}

		static void EmitSingleInfluenceVertex (MeshBuffers buffers, float x, float y, float u, float v, float z, Color32 color, int boneIndex, Vector2 dynUv) {
			// Unweighted attachments: influence 0 is the slot's bone with weight 1 (VertexAttachment.cs:107-118).
			buffers.positions.Add(new Vector3(x, y, z));
			buffers.uvs.Add(new Vector2(u, v));
			buffers.colors.Add(color);
			buffers.uv2.Add(Vector4.zero);
			buffers.uv3.Add(Vector2.zero);
			buffers.uv4.Add(new Vector4(boneIndex, 0f, 0f, 0f));
			buffers.uv5.Add(new Vector4(1f, 0f, 0f, 0f));
			buffers.uv6.Add(dynUv);
		}

		static void EmitMesh (MeshAttachment meshAttachment, SlotData slotData, Color32 color, float z, int vertexBase,
				Vector2 dynUv, GpuSpineAuditReport audit, MeshBuffers buffers) {
			// Linked meshes share their parent mesh's deform data (MeshAttachment.cs:66-82); dereference to
			// the actual data source. UVs and color stay with the (possibly linked) attachment itself.
			MeshAttachment source = meshAttachment;
			while (source.ParentMesh != null) source = source.ParentMesh;
			int[] bones = source.Bones;
			float[] vertices = source.Vertices;
			int vertexCount = source.WorldVerticesLength >> 1;
			float[] uvs = meshAttachment.UVs;
			int slotBoneIndex = slotData.BoneData.Index;

			if (bones == null) {
				// Unweighted mesh: Vertices are x/y interleaved local coordinates, single influence = slot bone.
				for (int v = 0; v < vertexCount; v++) {
					EmitSingleInfluenceVertex(buffers, vertices[v * 2], vertices[v * 2 + 1],
						uvs[v * 2], uvs[v * 2 + 1], z, color, slotBoneIndex, dynUv);
				}
			} else {

				// Weighted mesh (VertexAttachment.cs:119-136). Bones layout per vertex: influence count n,
				// then n bone indices. Vertices layout: 3 floats (vx, vy, weight) per influence, sequential.
				// (vx, vy) is the vertex position in the local space of that influence's bone, so every
				// influence bakes its own coordinate along with its bone index and weight.
				int boneCursor = 0;
				int vertexCursor = 0;
				int truncatedInAttachment = 0;
				for (int v = 0; v < vertexCount; v++) {
					int n = bones[boneCursor++];
					float x0 = 0f, y0 = 0f, w0 = 0f, x1 = 0f, y1 = 0f, w1 = 0f;
					float x2 = 0f, y2 = 0f, w2 = 0f, x3 = 0f, y3 = 0f, w3 = 0f;
					int i0 = 0, i1 = 0, i2 = 0, i3 = 0;
					if (n <= MaxInfluences) {
						for (int k = 0; k < n; k++) {
							int boneIndex = bones[boneCursor++];
							float vx = vertices[vertexCursor++], vy = vertices[vertexCursor++], weight = vertices[vertexCursor++];
							switch (k) {
							case 0: x0 = vx; y0 = vy; w0 = weight; i0 = boneIndex; break;
							case 1: x1 = vx; y1 = vy; w1 = weight; i1 = boneIndex; break;
							case 2: x2 = vx; y2 = vy; w2 = weight; i2 = boneIndex; break;
							default: x3 = vx; y3 = vy; w3 = weight; i3 = boneIndex; break;
							}
						}
					} else {
						// More than 4 influences: keep the strongest 4 (descending by weight), renormalize.
						for (int k = 0; k < n; k++) {
							int boneIndex = bones[boneCursor++];
							float vx = vertices[vertexCursor++], vy = vertices[vertexCursor++], weight = vertices[vertexCursor++];
							if (weight > w0) {
								x3 = x2; y3 = y2; w3 = w2; i3 = i2; x2 = x1; y2 = y1; w2 = w1; i2 = i1; x1 = x0; y1 = y0; w1 = w0; i1 = i0;
								x0 = vx; y0 = vy; w0 = weight; i0 = boneIndex;
							} else if (weight > w1) {
								x3 = x2; y3 = y2; w3 = w2; i3 = i2; x2 = x1; y2 = y1; w2 = w1; i2 = i1;
								x1 = vx; y1 = vy; w1 = weight; i1 = boneIndex;
							} else if (weight > w2) {
								x3 = x2; y3 = y2; w3 = w2; i3 = i2;
								x2 = vx; y2 = vy; w2 = weight; i2 = boneIndex;
							} else if (weight > w3) {
								x3 = vx; y3 = vy; w3 = weight; i3 = boneIndex;
							}
						}
						float sum = w0 + w1 + w2 + w3;
						if (sum > 0f) {
							float inv = 1f / sum;
							w0 *= inv; w1 *= inv; w2 *= inv; w3 *= inv;
						}
						truncatedInAttachment++;
					}
					buffers.positions.Add(new Vector3(x0, y0, z));
					buffers.uvs.Add(new Vector2(uvs[v * 2], uvs[v * 2 + 1]));
					buffers.colors.Add(color);
					buffers.uv2.Add(new Vector4(x1, y1, x2, y2));
					buffers.uv3.Add(new Vector2(x3, y3));
					buffers.uv4.Add(new Vector4(i0, i1, i2, i3));
					buffers.uv5.Add(new Vector4(w0, w1, w2, w3));
					buffers.uv6.Add(dynUv);
				}
				if (truncatedInAttachment > 0) {
					audit.TruncatedVertexCount += truncatedInAttachment;
					audit.Warnings.Add(string.Format(
						"MeshAttachment '{0}' (slot '{1}') has {2} vertex(es) with more than 4 bone influences; kept the strongest 4 and renormalized.",
						meshAttachment.Name, slotData.Name, truncatedInAttachment));
				}
			}

			int[] srcTriangles = source.Triangles;
			for (int t = 0; t < srcTriangles.Length; t++) buffers.triangles.Add(vertexBase + srcTriangles[t]);
		}

		static void CloseSubmesh (List<GpuSpineSubmesh> submeshes, Material pageMaterial, int indexStart, int indexEnd, bool hasPmaAdditiveSlot) {
			int indexCount = indexEnd - indexStart;
			if (indexCount <= 0) return;
			submeshes.Add(new GpuSpineSubmesh {
				PageMaterial = pageMaterial,
				IndexStart = indexStart,
				IndexCount = indexCount,
				HasPmaAdditiveSlot = hasPmaAdditiveSlot
			});
		}

		static Material GetPageMaterial (TextureRegion textureRegion) {
			AtlasRegion atlasRegion = textureRegion as AtlasRegion;
			if (atlasRegion == null || atlasRegion.page == null) return null;
			return atlasRegion.page.rendererObject as Material;
		}

		static Color32 ToColor32 (float r, float g, float b, float a) {
			return new Color32(ToByte(r), ToByte(g), ToByte(b), ToByte(a));
		}

		static byte ToByte (float value) {
			return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
		}

		static void Fail (GpuSpineAuditReport audit, string reason) {
			audit.Passed = false;
			audit.Failures.Add(reason);
		}

		/// <summary>All vertex streams and the combined index buffer; allocated once per bake.</summary>
		sealed class MeshBuffers {
			public readonly List<Vector3> positions = new List<Vector3>();
			public readonly List<Vector2> uvs = new List<Vector2>();      // TEXCOORD0
			public readonly List<Color32> colors = new List<Color32>();   // COLOR
			public readonly List<Vector4> uv2 = new List<Vector4>();      // TEXCOORD1: (vx1, vy1, vx2, vy2)
			public readonly List<Vector2> uv3 = new List<Vector2>();      // TEXCOORD2: (vx3, vy3)
			public readonly List<Vector4> uv4 = new List<Vector4>();      // TEXCOORD3: 4 bone indices
			public readonly List<Vector4> uv5 = new List<Vector4>();      // TEXCOORD4: 4 weights
			public readonly List<Vector2> uv6 = new List<Vector2>();      // TEXCOORD5: (dynSlotId, variantId), (-1, -1) static
			public readonly List<int> triangles = new List<int>();
		}
	}
}
