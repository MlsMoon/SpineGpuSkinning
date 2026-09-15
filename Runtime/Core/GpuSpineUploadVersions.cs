namespace GpuSpine.Core {
    // Versions belong to the data copied before the instance callback, not a later source state.
    internal struct GpuSpineUploadVersions {
        internal ulong Palette, Dynamic, Deform, Colors, Clipping;
        internal GpuSpineUploadVersions(GpuSkeletonRenderer source) {
            Palette = source.PaletteVersion;
            Dynamic = source.DynamicSlotsVersion;
            Deform = source.DeformVersion;
            Colors = source.SlotColorsVersion;
            Clipping = source.ClippingVersion;
        }
    }
}
