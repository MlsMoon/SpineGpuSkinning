namespace GpuSpine.Core {
	/// <summary>
	/// Draw-order strategy for the instances of one indirect batch. Transparent Spine rendering relies
	/// on the painter's algorithm within a batch: the instance buffer write order is the draw order,
	/// so batches are sorted on the CPU before upload.
	/// </summary>
	public enum GpuSpineSortMode {
		/// <summary>Keep registration order.</summary>
		None,
		/// <summary>Sort back-to-front by the axial distance to the active camera.</summary>
		CameraDepth,
		/// <summary>Sort back-to-front by world position z.</summary>
		WorldZBackToFront
	}
}
