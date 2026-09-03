using System.Runtime.InteropServices;
using UnityEngine;

namespace GpuSpine.Core {
	/// <summary>
	/// Per-instance draw data, indexed by SV_InstanceID in the GPU skinning shader. Sequential layout,
	/// 112 bytes, mirrored 1:1 by the GpuSpineInstanceData struct in SpineGpuSkinning.hlsl (the
	/// row_major float4x4 there matches the byte order of UnityEngine.Matrix4x4 here).
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct GpuSpineInstanceData {
		/// <summary>GameObject transform (SkeletonAnimation.transform.localToWorldMatrix), mapping
		/// skeleton space to world space; replaces the Transform step the CPU path gets for free.</summary>
		public Matrix4x4 LocalToWorld;
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
