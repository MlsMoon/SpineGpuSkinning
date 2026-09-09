using System.Runtime.InteropServices;
using UnityEngine;

namespace GpuSpine.Core {
	/// <summary>
	/// Per-instance draw data, indexed by SV_InstanceID in the GPU skinning shader. Sequential layout,
	/// 96 bytes, mirrored by explicit affine rows in SpineGpuSkinning.hlsl.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct GpuSpineInstanceData {
		/// <summary>GameObject transform (SkeletonAnimation.transform.localToWorldMatrix), mapping
		/// skeleton space to world space; replaces the Transform step the CPU path gets for free.</summary>
		public Vector4 LocalToWorldRow0;
		public Vector4 LocalToWorldRow1;
		public Vector4 LocalToWorldRow2;
		/// <summary>Skeleton color: skeleton.R/G/B/A.</summary>
		public Vector4 Color;
		/// <summary>Per-instance extension slot, written via <see cref="GpuSpine.GpuSpineInstanceDataWriter"/>.</summary>
		public Vector4 Custom0;
		/// <summary>Per-instance extension slot, written via <see cref="GpuSpine.GpuSpineInstanceDataWriter"/>.</summary>
		public Vector4 Custom1;
	}
}

namespace GpuSpine {
	/// <summary>
	/// Callback through which a GpuSkeletonRenderer delegates per-instance extension data writes.
	/// Invoked once per submitted instance per frame, after LocalToWorld and Color have been filled.
	/// </summary>
	/// <param name="sender">The component whose instance data is being built.</param>
	/// <param name="data">The instance data record to complete (Custom0/Custom1).</param>
	public delegate void GpuSpineInstanceDataWriter (GpuSkeletonRenderer sender, ref GpuSpine.Core.GpuSpineInstanceData data);
}
