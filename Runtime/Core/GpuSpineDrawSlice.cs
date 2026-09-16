using System;
using UnityEngine;
using GpuSpine.Baking;

namespace GpuSpine.Core {
    /// <summary>Independent args buffer, reusable across frames. The batch owns materials by instance offset.</summary>
    internal sealed class GpuSpineDrawSlice : IDisposable {
        public Material Material { get; private set; }
        public readonly GraphicsBuffer Args;
        readonly uint[] arguments = new uint[5];
        bool initialized;
        public GpuSpineDrawSlice() {
            Args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        }
        /// <summary>Each slice binds one geometry and instance range per frame; later redraws reuse it.</summary>
        public void Configure(Material material, GpuSpineSubmesh part, int count) {
            Material = material;
            uint indices = (uint)part.IndexCount, offset = (uint)part.IndexStart, instances = (uint)count;
            if (initialized && arguments[0] == indices && arguments[1] == instances && arguments[2] == offset) return;
            arguments[0] = indices; arguments[1] = instances; arguments[2] = offset;
            // Baked and combined layout meshes use a zero base vertex. Instance offset comes from the material.
            arguments[3] = 0; arguments[4] = 0;
            Args.SetData(arguments); initialized = true;
        }
        public void Dispose() { Args.Release(); Material = null; }
    }
}
