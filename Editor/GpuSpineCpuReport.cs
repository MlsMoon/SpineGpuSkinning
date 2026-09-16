using System;
using System.IO;
using GpuSpine.Core;
using UnityEditor;
using UnityEngine;
namespace GpuSpine.Editor {
    /// <summary>Explicit CPU transfer counters for CLI or manual before/after diffs. Does not run by itself.</summary>
    public static class GpuSpineCpuReport {
        [Serializable] sealed class Channel {
            public string name;
            public GpuSpineTransferSnapshot counters;
        }
        [Serializable] sealed class Report {
            public int frame, gpuActive, cameraCount, targetFrameRate, vSyncCount, renderFrameInterval;
            public string utc;
            public long skinChecks, skinHashRecomputations;
            public GpuSpineLifecycleCounters lifecycle;
            public Channel[] channels;
        }
        [MenuItem("Tools/GPUSpineSkin/Diagnostics/Export CPU Counters")]
        public static void Export() {
            var report=new Report { frame=Time.frameCount, utc=DateTime.UtcNow.ToString("O"),
                cameraCount=Camera.allCamerasCount,targetFrameRate=Application.targetFrameRate,vSyncCount=QualitySettings.vSyncCount,
                renderFrameInterval=UnityEngine.Rendering.OnDemandRendering.renderFrameInterval,
                skinChecks=GpuSpineCpuDiagnostics.SkinChecks,skinHashRecomputations=GpuSpineCpuDiagnostics.SkinHashRecomputations,
                lifecycle=GpuSkinningManager.GetLifecycleSnapshot(),channels=new Channel[6] };
            foreach(var renderer in UnityEngine.Object.FindObjectsOfType<GpuSkeletonRenderer>())if(renderer.IsGpuActive)report.gpuActive++;
            for(int i=0;i<report.channels.Length;i++)report.channels[i]=new Channel { name=((GpuSpineDataChannel)i).ToString(),counters=GpuSpineCpuDiagnostics.GetTransfers((GpuSpineDataChannel)i) };
            Directory.CreateDirectory("Library/GpuSpineCpu");
            File.WriteAllText("Library/GpuSpineCpu/snapshot.json",JsonUtility.ToJson(report,true),new System.Text.UTF8Encoding(false));
        }
    }
}
