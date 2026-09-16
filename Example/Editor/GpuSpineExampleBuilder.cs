using System;
using System.IO;
using GpuSpine.Baking;
using GpuSpine.Editor;
using Spine.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.Universal;

namespace GpuSpine.Example.Editor {
    /// <summary>Rebuild portable example assets. Does not replace or save the user's open scene.</summary>
    public static class GpuSpineExampleBuilder {
        public static string ExampleRoot {
            get {
                foreach (string guid in AssetDatabase.FindAssets("GpuSpineExampleBuilder t:MonoScript")) {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith("/Example/Editor/GpuSpineExampleBuilder.cs", StringComparison.Ordinal))
                        return path.Substring(0, path.Length - "/Editor/GpuSpineExampleBuilder.cs".Length);
                }
                throw new InvalidOperationException("Cannot locate GpuSpine Example folder.");
            }
        }
        public static string ScenePath => ExampleRoot + "/GpuSpineComparison.unity";

        [MenuItem(GpuSpineEditorMenu.Root + "/Build Comparison Example", false, 40)]
        public static void Build() {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Build the example in Edit Mode.");
            for (int i = 0; i < SceneManager.sceneCount; i++) {
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                    throw new InvalidOperationException("Save or close untitled scenes before building the example.");
                if (SceneManager.GetSceneAt(i).path == ScenePath)
                    throw new InvalidOperationException("Close the existing comparison scene before rebuilding it.");
            }
            var dude = EnsureSkeletonData("SampleDude");
            var robot = EnsureSkeletonData("SampleRobot");
            var complex = EnsureSkeletonData("ComplexCourier");
            var ultra = EnsureSkeletonData("UltraCourier");
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try {
                SceneManager.SetActiveScene(scene);
                var dudePrefab = EnsurePrefab(dude, "SampleDude");
                var robotPrefab = EnsurePrefab(robot, "SampleRobot");
                var complexPrefab = EnsurePrefab(complex, "ComplexCourier");
                var ultraPrefab = EnsurePrefab(ultra, "UltraCourier");
                var camera = new GameObject("Example Camera").AddComponent<Camera>();
                camera.tag = "MainCamera";
                camera.orthographic = true;
                camera.orthographicSize = 5.6f;
                camera.transform.position = new Vector3(0, -1.2f, -20);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.065f, 0.10f, 0.14f);
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.transparencySortMode = TransparencySortMode.Orthographic;
                var urp = camera.GetUniversalAdditionalCameraData();
                urp.renderPostProcessing = false;
                urp.requiresColorOption = CameraOverrideOption.Off;
                urp.requiresDepthOption = CameraOverrideOption.Off;
                var controller = new GameObject("GPU Spine Example").AddComponent<GpuSpineExampleController>();
                controller.AdventurerPrefab = dudePrefab;
                controller.RobotPrefab = robotPrefab;
                controller.ComplexPrefab = complexPrefab;
                controller.UltraPrefab = ultraPrefab;
                controller.StartWithComplex = true;
                controller.GpuShaderVariants = EnsureShaderVariants();
                controller.ExampleCamera = camera;
                controller.LaneMaterial = EnsureLaneMaterial();
                controller.InitialCount = 32;
                controller.StartWithGpu = true;
                controller.gameObject.AddComponent<GpuSpineExampleHud>().Controller = controller;
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Could not save " + ScenePath);
            } finally {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("GpuSpine Example built: " + ScenePath);
        }

        static SkeletonDataAsset EnsureSkeletonData(string name) {
            string dir = ExampleRoot + "/Character/" + name;
            var atlasText = AssetDatabase.LoadAssetAtPath<TextAsset>(dir + "/" + name + ".atlas.txt");
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(dir + "/" + name + ".json");
            string texturePath = dir + "/" + name + ".png";
            var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (atlasText == null || json == null || importer == null) throw new InvalidOperationException("Missing character sources: " + dir);
            importer.sRGBTexture = true;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            Shader shader = Shader.Find("Spine/Skeleton");
            if (shader == null) throw new InvalidOperationException("Install spine-unity 4.2 before building this example.");
            var material = LoadOrCreate<Material>(dir + "/" + name + "_Material.mat", () => new Material(shader));
            material.shader = shader;
            material.SetTexture("_MainTex", texture);
            material.SetFloat("_StraightAlphaInput", 1);
            material.EnableKeyword("_STRAIGHT_ALPHA_INPUT");
            EditorUtility.SetDirty(material);
            var atlas = LoadOrCreate<SpineAtlasAsset>(dir + "/" + name + "_Atlas.asset", ScriptableObject.CreateInstance<SpineAtlasAsset>);
            atlas.atlasFile = atlasText;
            atlas.materials = new[] { material };
            atlas.Clear();
            EditorUtility.SetDirty(atlas);
            var skeleton = LoadOrCreate<SkeletonDataAsset>(dir + "/" + name + "_SkeletonData.asset", ScriptableObject.CreateInstance<SkeletonDataAsset>);
            skeleton.atlasAssets = new AtlasAssetBase[] { atlas };
            skeleton.skeletonJSON = json;
            skeleton.scale = 0.01f;
            skeleton.Clear();
            EditorUtility.SetDirty(skeleton);
            AssetDatabase.SaveAssets();
            var baked = GpuSpineBakerEditorUtility.Rebake(skeleton);
            if (baked == null || baked.Audit == null || !baked.Audit.Passed || baked.Entries.Count == 0)
                throw new InvalidOperationException("Character bake failed: " + name);
            return skeleton;
        }

        static T LoadOrCreate<T>(string path, Func<T> create) where T : UnityEngine.Object {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = create();
            asset.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        static GpuSpineExampleWalker EnsurePrefab(SkeletonDataAsset data, string name) {
            GameObject go = new GameObject(name);
            go.SetActive(false);
            try {
                var animation = SkeletonAnimation.AddToGameObject(go, data);
                animation.zSpacing = 0;
                animation.loop = true;
                animation.AnimationName = "walk";
                var mesh = go.GetComponent<MeshRenderer>();
                mesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mesh.receiveShadows = false;
                var gpu = go.AddComponent<GpuSkeletonRenderer>();
                gpu.BakedData = GpuSpineBakerEditorUtility.FindContainer(AssetDatabase.GetAssetPath(data));
                var walker = go.AddComponent<GpuSpineExampleWalker>();
                walker.Skeleton = animation;
                walker.Gpu = gpu;
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, ExampleRoot + "/Character/" + name + "/" + name + ".prefab");
                return prefab.GetComponent<GpuSpineExampleWalker>();
            } finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        /// <summary>Keep straight alpha and instancing variants so Player stripping does not dirty transparent edges.</summary>
        static ShaderVariantCollection EnsureShaderVariants() {
            var variants = LoadOrCreate<ShaderVariantCollection>(ExampleRoot + "/ExampleGpu.shadervariants",
                () => new ShaderVariantCollection());
            Shader shader = Shader.Find("GpuSpine/URP/Skeleton");
            variants.Add(new ShaderVariantCollection.ShaderVariant(shader,
                UnityEngine.Rendering.PassType.ScriptableRenderPipeline, "INSTANCING_ON", "_STRAIGHT_ALPHA_INPUT"));
            variants.Add(new ShaderVariantCollection.ShaderVariant(shader,
                UnityEngine.Rendering.PassType.ScriptableRenderPipeline, "_STRAIGHT_ALPHA_INPUT"));
            EditorUtility.SetDirty(variants);
            return variants;
        }

        static Material EnsureLaneMaterial() {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("Install URP before building this example.");
            Material material = LoadOrCreate<Material>(ExampleRoot + "/Lane.mat", () => new Material(shader));
            material.SetColor("_BaseColor", new Color(0.14f, 0.21f, 0.27f));
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
