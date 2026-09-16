using System;
namespace GpuSpine.Core {
    /// <summary>Allocation-free per-channel totals since this Play session started.</summary>
    [Serializable]
    public struct GpuSpineTransferSnapshot {
        public long CopiedBytes, UploadedBytes, UploadCalls;
    }
}
