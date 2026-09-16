namespace GpuSpine.Core {
    /// <summary>Allocation-free cumulative lifecycle snapshot.</summary>
    public struct GpuSpineLifecycleCounters {
        public int EntryChanges, BatchesCreated, BatchesDisposed, BatchesParked;
        public int BatchesReused, SlicesCreated, LayoutMeshesBuilt, CapacityGrowths;
    }
}
