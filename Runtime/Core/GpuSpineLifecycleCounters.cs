namespace GpuSpine.Core {
    /// <summary>无需数组分配的累计生命周期快照。</summary>
    public struct GpuSpineLifecycleCounters {
        public int EntryChanges, BatchesCreated, BatchesDisposed, BatchesParked;
        public int BatchesReused, SlicesCreated, LayoutMeshesBuilt, CapacityGrowths;
    }
}
