namespace GpuSpine {
    /// <summary>Master switch for plugin diagnostic logs. When false the compiler strips calls and argument construction.</summary>
    public static class GpuSpineDiagnostics {
        public const bool EnableLogging = false;
        /// <summary>Master gate for automatic smoke, screenshots, and performance validation. Overrides older EditorPrefs and validation build flags.</summary>
        public const bool EnableAutomaticValidation = false;
    }
}
