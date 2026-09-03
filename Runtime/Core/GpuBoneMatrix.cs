using System.Runtime.InteropServices;
using UnityEngine;

namespace GpuSpine.Core {
	/// <summary>
	/// GPU palette entry for one Spine bone: the 2D world affine matrix of the bone in skeleton space,
	/// exported from Bone.A/B/C/D/WorldX/WorldY after SkeletonAnimation.UpdateComplete. 24 bytes,
	/// sequential layout, mirrored 1:1 by the GpuBoneMatrix struct in SpineGpuSkinning.hlsl.
	/// Skinning: wx = vx * a + vy * b + worldX ; wy = vx * c + vy * d + worldY.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct GpuBoneMatrix {
		/// <summary>First matrix row: (a, b).</summary>
		public Vector2 Row0;
		/// <summary>Second matrix row: (c, d).</summary>
		public Vector2 Row1;
		/// <summary>Translation: (worldX, worldY).</summary>
		public Vector2 Row2;

		public GpuBoneMatrix (float a, float b, float c, float d, float worldX, float worldY) {
			Row0 = new Vector2(a, b);
			Row1 = new Vector2(c, d);
			Row2 = new Vector2(worldX, worldY);
		}
	}
}
