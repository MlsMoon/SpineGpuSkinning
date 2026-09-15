using System;
namespace GpuSpine.Core {
    /// <summary>无需分配的通道累计计数，自本次 Play 初始化起统计。</summary>
    [Serializable]
    public struct GpuSpineTransferSnapshot {
        public long CopiedBytes, UploadedBytes, UploadCalls;
    }
}
