using UnityEngine;

namespace GpuSpine.Example {
    /// <summary>FPS and crowd control HUD with no extra UI package.</summary>
    public sealed class GpuSpineExampleHud : MonoBehaviour {
        public GpuSpineExampleController Controller;
        GUIStyle m_title;
        GUIStyle m_metric;
        GUIStyle m_label;
        GUIStyle m_button;
        GUIStyle m_input;
        Texture2D m_panel;
        Texture2D m_normal;
        Texture2D m_hover;
        string m_count = "32";
        string m_fps = "-- FPS";
        string m_frame = "-- ms / frame";
        string m_mode = "GPU";
        string m_active = "GPU active: 0 / 0";
        string m_comparison = "CPU: --     GPU: --";
        string m_status = "";
        float m_nextRefresh;
        int m_lastTarget = -1;
        static readonly string[] SkinLabels = { "Mixed skins", "Ember / Mint", "Forest / Copper", "Midnight / Rose" };
        static readonly int[] Presets = { 32, 100, 300, 1000 };

        void Update() {
            if (Controller == null) return;
            if (m_lastTarget != Controller.TargetCount) {
                m_lastTarget = Controller.TargetCount;
                m_count = m_lastTarget.ToString();
            }
            if (Time.unscaledTime < m_nextRefresh) return;
            m_nextRefresh = Time.unscaledTime + 0.25f;
            bool ready = !Controller.IsSettling && Controller.Fps > 0;
            m_fps = ready ? Controller.Fps.ToString("F1") + " FPS" : "-- FPS";
            m_frame = ready ? Controller.FrameMilliseconds.ToString("F2") + " ms / frame" : "Measuring...";
            m_mode = Controller.GpuRequested ? "GPU SKINNING" : "CPU SKINNING";
            m_active = "GPU active: " + Controller.GpuActiveCount + " / " + Controller.Count;
            m_comparison = "Last CPU: " + FormatFps(Controller.CpuFps) + "     Last GPU: " + FormatFps(Controller.GpuFps);
            m_status = Controller.IsBuilding ? "Creating: " + Controller.Count + " / " + Controller.TargetCount
                : Controller.IsSettling ? "Warming up..." : Controller.GpuRequested && Controller.GpuActiveCount != Controller.Count
                ? "CPU fallback detected" : "Same crowd. Same camera. Live comparison.";
        }

        static string FormatFps(float fps) => fps > 0 ? fps.ToString("F1") + " FPS" : "--";

        void OnGUI() {
            if (Controller == null) return;
            if (m_title == null) CreateStyles();
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            float scale = Mathf.Max(0.25f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f));
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
            float width = Screen.width / scale;
            GUI.DrawTexture(new Rect(0, 0, width, 184), m_panel);
            GUI.Label(new Rect(28, 14, 450, 28), "GPU SPINE  /  CROWD EXAMPLE", m_title);
            GUI.color = Controller.ComplexRequested ? Color.white : new Color(0.55f, 1f, 0.83f);
            if (GUI.Button(new Rect(470, 12, 150, 32), "Simple duo", m_button)) Controller.SetComplexCase(false);
            GUI.color = Controller.ComplexRequested ? new Color(0.55f, 1f, 0.83f) : Color.white;
            if (GUI.Button(new Rect(630, 12, 185, 32), "Complex courier", m_button)) Controller.SetComplexCase(true);
            GUI.color = Controller.UltraRequested ? new Color(0.55f, 1f, 0.83f) : Color.white;
            if (GUI.Button(new Rect(825, 12, 180, 32), "Production 300 cats", m_button)) Controller.SetProductionCase();
            GUI.color = oldColor;
            GUI.Label(new Rect(575, 49, 380, 24), Controller.UltraRequested ? (Controller.ProductionRequested ? "Production load / 100 bones / ~2490 vertices" : "100 bones / ~2490 vertices") : Controller.ComplexRequested
                ? "48 bones / ~1170 vertices" : "20 bones / 159 vertices each", m_label);
            GUI.Label(new Rect(28, 49, 275, 24), m_mode, m_label);
            GUI.Label(new Rect(305, 49, 270, 24), m_active, m_label);
            GUI.Label(new Rect(width - 290, 12, 265, 42), m_fps, m_metric);
            GUI.Label(new Rect(width - 285, 56, 260, 24), m_frame, m_label);
            if (GUI.Button(new Rect(28, 89, 175, 36), Controller.GpuRequested ? "Switch to CPU" : "Switch to GPU", m_button))
                Controller.SetGpuEnabled(!Controller.GpuRequested);
            GUI.Label(new Rect(226, 96, 60, 26), "Count", m_label);
            m_count = GUI.TextField(new Rect(290, 89, 78, 36), m_count, 4, m_input);
            if (GUI.Button(new Rect(378, 89, 76, 36), "Apply", m_button)) {
                if (int.TryParse(m_count, out int count)) Controller.SetCount(count);
                m_count = Controller.TargetCount.ToString();
            }
            for (int i = 0; i < Presets.Length; i++) {
                if (GUI.Button(new Rect(480 + i * 78, 89, 68, 36), Presets[i].ToString(), m_button))
                    Controller.SetCount(Presets[i]);
            }
            GUI.Label(new Rect(28, 142, 70, 26), "Skins", m_label);
            for (int i = 0; i < SkinLabels.Length; i++) {
                GUI.color = Controller.SkinSelection == i - 1 ? new Color(0.55f, 1f, 0.83f) : Color.white;
                if (GUI.Button(new Rect(105 + i * 171, 135, 162, 32), SkinLabels[i], m_button))
                    Controller.SetSkinSelection(i - 1);
            }
            GUI.color = oldColor;
            GUI.Label(new Rect(28, Screen.height / scale - 40, 560, 28), m_status, m_label);
            GUI.Label(new Rect(width - 480, Screen.height / scale - 40, 460, 28), m_comparison, m_label);
            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
        }
        void CreateStyles() {
            m_panel = Texture(new Color(0.045f, 0.075f, 0.105f, 0.97f));
            m_normal = Texture(new Color(0.13f, 0.24f, 0.29f));
            m_hover = Texture(new Color(0.20f, 0.39f, 0.43f));
            m_title = new GUIStyle(GUI.skin.label) { fontSize = 21, fontStyle = FontStyle.Bold };
            m_title.normal.textColor = new Color(0.80f, 0.93f, 0.94f);
            m_label = new GUIStyle(GUI.skin.label) { fontSize = 17 };
            m_label.normal.textColor = new Color(0.62f, 0.74f, 0.79f);
            m_metric = new GUIStyle(m_title) { fontSize = 34, alignment = TextAnchor.MiddleRight };
            m_metric.normal.textColor = new Color(0.57f, 0.95f, 0.80f);
            m_button = new GUIStyle(GUI.skin.button) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            m_button.normal.background = m_normal;
            m_button.hover.background = m_hover;
            m_button.active.background = m_hover;
            m_button.normal.textColor = Color.white;
            m_input = new GUIStyle(GUI.skin.textField) { fontSize = 19, alignment = TextAnchor.MiddleCenter };
        }

        static Texture2D Texture(Color color) {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        void OnDestroy() {
            if (m_panel != null) Destroy(m_panel);
            if (m_normal != null) Destroy(m_normal);
            if (m_hover != null) Destroy(m_hover);
        }
    }
}
