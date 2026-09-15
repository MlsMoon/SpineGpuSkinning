namespace GpuSpine {
    /// <summary>插件诊断日志总开关；关闭时编译器移除调用和日志参数构造。</summary>
    public static class GpuSpineDiagnostics {
        public const bool EnableLogging = false;
        /// <summary>自动冒烟、截图和性能验证总控；优先于历史 EditorPrefs 与验证构建参数。</summary>
        public const bool EnableAutomaticValidation = false;
    }
}
