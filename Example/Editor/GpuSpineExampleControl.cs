using System;
using GpuSpine.Core;
using UnityEditor;
using UnityEngine;

namespace GpuSpine.Example.Editor {
    /// <summary>无 UI 自动化的示例控制入口，供编辑器工具与本地 CLI 复用。</summary>
    public static class GpuSpineExampleControl {
        public const string RequestKey = "GpuSpine.Example.Request";
        public const string ResultKey = "GpuSpine.Example.Result";

        [Serializable]
        public sealed class Request {
            public string action = "status";
            public int value;
        }

        [Serializable]
        public sealed class Result {
            public bool success;
            public string error;
            public string caseName;
            public int count;
            public int targetCount;
            public int gpuActive;
            public int skinSelection;
            public int drawSlices;
            public int submittedInstances;
            public float fps;
            public float frameMs;
            public float cpuFps;
            public float gpuFps;
        }

        [MenuItem("Tools/GPUSpineSkin/Apply Example Control Request", false, 42)]
        public static void ApplyRequest() {
            string input = SessionState.GetString(RequestKey, "{\"action\":\"status\"}");
            SessionState.EraseString(RequestKey);
            Result result;
            try { result = Apply(JsonUtility.FromJson<Request>(input)); }
            catch (Exception e) { result = new Result { error = e.Message }; }
            string json = JsonUtility.ToJson(result);
            SessionState.SetString(ResultKey, json);
            if (!result.success) Debug.LogError("GpuSpine Example control: " + json);
        }

        /// <summary>action: status / count / gpu / skin / case / freeze；value 为整数参数。</summary>
        public static Result Apply(Request request) {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Example controls require Play Mode.");
            var controller = UnityEngine.Object.FindObjectOfType<GpuSpineExampleController>();
            if (controller == null) throw new InvalidOperationException("The comparison example is not running.");
            switch (request.action) {
                case "count": controller.SetCount(request.value); break;
                case "gpu": controller.SetGpuEnabled(request.value != 0); break;
                case "skin": controller.SetSkinSelection(request.value); break;
                case "case": controller.SetComplexCase(request.value != 0); break;
                case "freeze": SetFrozen(request.value != 0); break;
                case "status": break;
                default: throw new ArgumentException("Unknown action: " + request.action);
            }
            var result = new Result {
                success = true, caseName = controller.ComplexRequested ? "complex" : "simple",
                count = controller.Count, targetCount = controller.TargetCount, gpuActive = controller.GpuActiveCount,
                skinSelection = controller.SkinSelection, fps = controller.Fps, frameMs = controller.FrameMilliseconds,
                cpuFps = controller.CpuFps, gpuFps = controller.GpuFps
            };
            var draws = GpuSkinningManager.GetBatches(controller.ExampleCamera);
            result.drawSlices = draws.Count;
            foreach (var draw in draws) result.submittedInstances += draw.InstanceCount;
            return result;
        }

        static void SetFrozen(bool frozen) {
            const string key = "GpuSpine.Example.PreviousTimeScale";
            if (frozen) {
                if (Time.timeScale != 0) SessionState.SetFloat(key, Time.timeScale);
                Time.timeScale = 0;
            } else {
                Time.timeScale = SessionState.GetFloat(key, 1);
                SessionState.EraseFloat(key);
            }
        }
    }
}
