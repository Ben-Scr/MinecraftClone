#if UNITY_EDITOR
using BenScr.MinecraftClone;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BenScr.MinecraftClone.Editor
{
    /// <summary>Installs the project renderer feature once after scripts compile.</summary>
    [InitializeOnLoad]
    internal static class VoxelRenderPipelineSetup
    {
        private static readonly string[] RendererPaths =
        {
            "Assets/Settings/PC_Renderer.asset",
            "Assets/Settings/Mobile_Renderer.asset"
        };

        static VoxelRenderPipelineSetup()
        {
            EditorApplication.delayCall += EnsureSunShaftsFeature;
        }

        [MenuItem("Voxel Builder/Rendering/Install Sun Shafts")]
        private static void EnsureSunShaftsFeature()
        {
            bool changed = false;
            foreach (string path in RendererPaths)
            {
                UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (rendererData == null || HasFeature(rendererData))
                    continue;

                SunShaftsRendererFeature feature = ScriptableObject.CreateInstance<SunShaftsRendererFeature>();
                feature.name = "Depth-Aware Sun Shafts";
                feature.SetShaderForEditor(AssetDatabase.LoadAssetAtPath<Shader>(
                    "Assets/Materials/Mats/SunShafts.shader"));
                rendererData.rendererFeatures.Add(feature);
                AssetDatabase.AddObjectToAsset(feature, rendererData);
                feature.Create();
                EditorUtility.SetDirty(feature);
                EditorUtility.SetDirty(rendererData);
                changed = true;
            }

            if (changed)
                AssetDatabase.SaveAssets();
        }

        private static bool HasFeature(UniversalRendererData rendererData)
        {
            foreach (ScriptableRendererFeature feature in rendererData.rendererFeatures)
            {
                if (feature is SunShaftsRendererFeature)
                    return true;
            }

            return false;
        }
    }
}
#endif
