using System.Collections.Generic;
using UnityEngine;

namespace GpuSpine.Example {
    /// <summary>独立示例入口：人群、蒙皮切换、相机与帧率采样。</summary>
    public sealed class GpuSpineExampleController : MonoBehaviour {
        public GpuSpineExampleWalker AdventurerPrefab;
        public GpuSpineExampleWalker RobotPrefab;
        public GpuSpineExampleWalker ComplexPrefab;
        public GpuSpineExampleWalker UltraPrefab;
        public ShaderVariantCollection GpuShaderVariants;
        public bool StartWithComplex = true;
        public Camera ExampleCamera;
        public Material LaneMaterial;
        [Range(0, 1000)] public int InitialCount = 32;
        public bool StartWithGpu = true;
        readonly List<GpuSpineExampleWalker> m_people = new List<GpuSpineExampleWalker>();
        readonly List<GpuSpineExampleWalker> m_robots = new List<GpuSpineExampleWalker>();
        readonly List<LineRenderer> m_lines = new List<LineRenderer>();
        Material m_lineMaterial;
        int m_target;
        int m_previousVSync;
        int m_previousFrameRate;
        int m_sampleFrames;
        float m_sampleTime;
        float m_settleUntil;
        float m_left;
        float m_right;
        float m_aspect;
        int m_skinSelection = -1;
        public int SkinSelection => m_skinSelection;
        bool m_complex;
        bool m_requestedComplex;
        public bool ComplexRequested => m_requestedComplex;
        public bool UltraRequested { get; private set; }
        bool m_gpu;
        bool m_layoutDirty;
        bool m_initialized;
        public int Count => m_people.Count + m_robots.Count;
        public int TargetCount => m_target;
        public bool GpuRequested => m_gpu;
        public int GpuActiveCount { get; private set; }
        public bool IsBuilding => Count != m_target || m_complex != m_requestedComplex;
        public bool IsSettling => IsBuilding || Time.unscaledTime < m_settleUntil;
        public float Fps { get; private set; }
        public float FrameMilliseconds { get; private set; }
        public float CpuFps { get; private set; }
        public float GpuFps { get; private set; }

        void Start() {
            if (AdventurerPrefab == null || RobotPrefab == null || ComplexPrefab == null || UltraPrefab == null || ExampleCamera == null) {
                Debug.LogError("GpuSpine Example: missing character prefab or camera.", this);
                enabled = false;
                return;
            }
            m_previousVSync = QualitySettings.vSyncCount;
            m_previousFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            m_initialized = true;
            m_gpu = StartWithGpu;
            m_complex = m_requestedComplex = StartWithComplex;
            if (GpuShaderVariants != null) GpuShaderVariants.WarmUp();
            SetCount(InitialCount);
        }

        /// <summary>数量只在提交后生效；保留已有实例，统计只比较相同数量。</summary>
        public void SetCount(int count) {
            count = Mathf.Clamp(count, 0, 1000);
            if (m_target == count && m_initialized && !IsBuilding && m_aspect > 0) return;
            m_target = count;
            CpuFps = GpuFps = 0f;
            m_layoutDirty = true;
            ResetMeasurement();
        }

        /// <summary>只启停插件组件，不重新创建骨架或重播动画。</summary>
        public void SetGpuEnabled(bool enabledGpu) {
            m_gpu = enabledGpu;
            ApplyMode(m_people);
            ApplyMode(m_robots);
            ResetMeasurement();
        }

        /// <summary>-1 混合皮肤；0/1/2 为两种角色对应的三套皮肤。</summary>
        public void SetSkinSelection(int selection) {
            m_skinSelection = Mathf.Clamp(selection, -1, 2);
            foreach (var walker in m_people) walker.ApplySkin(m_skinSelection);
            foreach (var walker in m_robots) walker.ApplySkin(m_skinSelection);
            CpuFps = GpuFps = 0;
            ResetMeasurement();
        }

        /// <summary>案例切换分帧回收旧角色，再按原数量创建新角色。</summary>
        public void SetComplexCase(bool complex) {
            m_requestedComplex = complex;
            UltraRequested = false;
            CpuFps = GpuFps = 0;
            m_layoutDirty = true;
            ResetMeasurement();
        }

        /// <summary>切换 100 骨骼超复杂案例，角色数不变。</summary>
        public void SetUltraCase(bool ultra) {
            UltraRequested = ultra;
            m_requestedComplex = ultra;
            CpuFps = GpuFps = 0;
            m_layoutDirty = true;
            ResetMeasurement();
        }

        void ApplyMode(List<GpuSpineExampleWalker> walkers) {
            foreach (var walker in walkers) {
                walker.Gpu.CameraFilter = camera => camera == ExampleCamera;
                walker.Gpu.enabled = m_gpu;
            }
        }

        void ResetMeasurement() {
            m_sampleFrames = 0;
            m_sampleTime = 0;
            Fps = FrameMilliseconds = 0;
            m_settleUntil = Time.unscaledTime + 1f;
        }

        void Update() {
            int budget = 20;
            if (UltraRequested) {
                Resize(m_people, m_target, UltraPrefab, 0, ref budget);
                Resize(m_robots, 0, RobotPrefab, 1, ref budget);
            } else if (m_complex != m_requestedComplex) {
                Resize(m_people, 0, AdventurerPrefab, 0, ref budget);
                Resize(m_robots, 0, RobotPrefab, 1, ref budget);
                if (Count == 0) m_complex = m_requestedComplex;
            } else {
                Resize(m_people, m_complex ? m_target : (m_target + 1) / 2,
                    m_complex ? ComplexPrefab : AdventurerPrefab, 0, ref budget);
                Resize(m_robots, m_complex ? 0 : m_target / 2, RobotPrefab, 1, ref budget);
            }
            if (m_layoutDirty || !Mathf.Approximately(m_aspect, ExampleCamera.aspect)) Arrange();
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            foreach (var walker in m_people) walker.Tick(dt, m_left, m_right);
            foreach (var walker in m_robots) walker.Tick(dt, m_left, m_right);
            Measure();
        }
        void Resize(List<GpuSpineExampleWalker> list, int desired, GpuSpineExampleWalker prefab, int kind, ref int budget) {
            while (list.Count > desired && budget-- > 0) {
                var last = list[list.Count - 1];
                list.RemoveAt(list.Count - 1);
                last.gameObject.SetActive(false);
                Destroy(last.gameObject);
                m_layoutDirty = true;
                ResetMeasurement();
            }
            while (list.Count < desired && budget-- > 0) {
                var walker = Instantiate(prefab, transform);
                walker.name = (kind == 0 ? "Adventurer " : "Robot ") + list.Count;
                walker.Gpu.CameraFilter = camera => camera == ExampleCamera;
                walker.Gpu.enabled = m_gpu;
                walker.Configure(kind, list.Count);
                walker.ApplySkin(m_skinSelection);
                list.Add(walker);
                walker.gameObject.SetActive(true);
                m_layoutDirty = true;
                ResetMeasurement();
            }
        }

        void Arrange() {
            m_layoutDirty = false;
            m_aspect = ExampleCamera.aspect;
            CpuFps = GpuFps = 0;
            int perKind = Mathf.Max(1, UltraRequested ? m_target : (m_complex ? m_target : (m_target + 1) / 2));
            int columns = Mathf.Max(4, Mathf.CeilToInt(Mathf.Sqrt(perKind * m_aspect * 2.05f / 1.65f * (m_complex ? 1f : 2f))));
            int rows = Mathf.CeilToInt(perKind / (float)columns);
            float width = columns * 1.65f;
            m_left = -width * 0.5f + 0.35f;
            m_right = width * 0.5f - 0.35f;
            foreach (var walker in m_people) walker.Arrange(columns, 0, -width * 0.5f);
            foreach (var walker in m_robots) walker.Arrange(columns, rows, -width * 0.5f);
            int totalRows = UltraRequested || m_complex ? rows : rows * 2;
            float bottom = -(totalRows - 1) * 2.05f - 0.7f;
            float top = 1.8f;
            // 顶部保留 30% 画面给 UI，世界取景不依赖 IMGUI 的像素缩放。
            float worldHeight = top - bottom;
            float half = Mathf.Max(worldHeight / 1.4f, (width + 1f) / (2f * m_aspect));
            ExampleCamera.orthographicSize = half;
            ExampleCamera.transform.position = new Vector3(0, (top + bottom) * 0.5f + half * 0.30f, -20f);
            EnsureLines(totalRows, width);
            ResetMeasurement();
        }

        void EnsureLines(int count, float width) {
            if (m_lineMaterial == null) {
                if (LaneMaterial == null) return;
                m_lineMaterial = new Material(LaneMaterial);
            }
            while (m_lines.Count < count) {
                var line = new GameObject("Walking lane").AddComponent<LineRenderer>();
                line.transform.SetParent(transform, false);
                line.sharedMaterial = m_lineMaterial;
                line.useWorldSpace = false;
                line.positionCount = 2;
                line.startWidth = line.endWidth = 0.012f;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_lines.Add(line);
            }
            for (int i = 0; i < m_lines.Count; i++) {
                var line = m_lines[i];
                line.gameObject.SetActive(i < count);
                line.SetPosition(0, new Vector3(-width * 0.5f, -i * 2.05f - 0.15f, 1));
                line.SetPosition(1, new Vector3(width * 0.5f, -i * 2.05f - 0.15f, 1));
            }
        }

        void Measure() {
            GpuActiveCount = 0;
            foreach (var walker in m_people) if (walker.Gpu.IsGpuActive) GpuActiveCount++;
            foreach (var walker in m_robots) if (walker.Gpu.IsGpuActive) GpuActiveCount++;
            if (IsSettling) return;
            m_sampleTime += Time.unscaledDeltaTime;
            m_sampleFrames++;
            if (m_sampleTime < 0.5f) return;
            Fps = m_sampleFrames / m_sampleTime;
            FrameMilliseconds = 1000f * m_sampleTime / m_sampleFrames;
            if (m_gpu && GpuActiveCount == Count) GpuFps = Fps;
            else if (!m_gpu && GpuActiveCount == 0) CpuFps = Fps;
            m_sampleFrames = 0;
            m_sampleTime = 0;
        }

        void OnDestroy() {
            if (m_initialized) {
                QualitySettings.vSyncCount = m_previousVSync;
                Application.targetFrameRate = m_previousFrameRate;
            }
            if (m_lineMaterial != null) Destroy(m_lineMaterial);
        }
    }
}
