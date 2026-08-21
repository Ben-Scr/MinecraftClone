using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace BenScr.MinecraftClone
{
    /// <summary>
    /// Depth-aware radial sun shafts. The camera depth texture turns terrain, blocks and
    /// alpha-tested foliage into occluders, producing rays that follow the directional sun.
    /// </summary>
    public sealed class SunShaftsRendererFeature : ScriptableRendererFeature
    {
        private static readonly int WorldNightFactorId = Shader.PropertyToID("_WorldNightFactor");

        [System.Serializable]
        public sealed class Settings
        {
            [Range(0f, 2f)] public float intensity = 0.72f;
            [Range(0.1f, 1f)] public float maxRadius = 0.72f;
            [Range(0.8f, 0.999f)] public float decay = 0.965f;
            [Range(0.1f, 2f)] public float density = 0.92f;
            [Range(0f, 1f)] public float exposure = 0.18f;
            [Range(4, 32)] public int sampleCount = 16;
            [Range(1, 4)] public int downsample = 2;
            public Color daylightColor = new Color(1f, 0.7f, 0.34f, 1f);
        }

        [SerializeField] private Settings settings = new Settings();
        [SerializeField, HideInInspector] private Shader sunShaftsShader;

        private SunShaftsPass pass;
        private Material material;

        public override void Create()
        {
            Shader shader = sunShaftsShader != null
                ? sunShaftsShader
                : Shader.Find("Hidden/VoxelBuilder/SunShafts");
            if (shader != null && (material == null || material.shader != shader))
                material = CoreUtils.CreateEngineMaterial(shader);

            pass = new SunShaftsPass(material)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
            };
        }

#if UNITY_EDITOR
        public void SetShaderForEditor(Shader shader)
        {
            sunShaftsShader = shader;
        }
#endif

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (material == null || renderingData.cameraData.cameraType != CameraType.Game)
                return;

            Camera camera = renderingData.cameraData.camera;
            Light sun = RenderSettings.sun;
            if (sun == null || !sun.isActiveAndEnabled || sun.type != LightType.Directional)
                return;

            Vector3 directionToSun = -sun.transform.forward;
            Vector3 viewport = camera.WorldToViewportPoint(camera.transform.position + directionToSun * camera.farClipPlane);
            float altitude = Mathf.Clamp01(Vector3.Dot(directionToSun, Vector3.up) * 4f);
            float daylight = 1f - Mathf.Clamp01(Shader.GetGlobalFloat(WorldNightFactorId));
            float visibility = viewport.z > 0f
                ? altitude * Mathf.Clamp01(sun.intensity) * daylight
                : 0f;
            float intensity = settings.intensity * visibility;

            // Do not allocate an intermediate color target or execute either fullscreen pass
            // when the result would be invisible. The expanded viewport test still permits
            // shafts whose radial falloff overlaps an edge of the screen.
            const float minimumEffectiveIntensity = 0.001f;
            float viewportMargin = settings.maxRadius;
            bool outsideEffectArea = viewport.x < -viewportMargin || viewport.x > 1f + viewportMargin ||
                                     viewport.y < -viewportMargin || viewport.y > 1f + viewportMargin;
            if (intensity * settings.exposure < minimumEffectiveIntensity || outsideEffectArea)
                return;

            pass.Setup(
                new Vector2(viewport.x, viewport.y),
                settings.daylightColor * sun.color,
                intensity,
                settings.maxRadius,
                settings.decay,
                settings.density,
                settings.exposure,
                settings.sampleCount,
                settings.downsample);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(material);
            material = null;
            pass = null;
        }

        private sealed class SunShaftsPass : ScriptableRenderPass
        {
            private static readonly int SunPositionId = Shader.PropertyToID("_SunPosition");
            private static readonly int ShaftColorId = Shader.PropertyToID("_ShaftColor");
            private static readonly int ShaftParamsId = Shader.PropertyToID("_ShaftParams");
            private static readonly int ShaftExposureId = Shader.PropertyToID("_ShaftExposure");
            private static readonly int ShaftSampleCountId = Shader.PropertyToID("_ShaftSampleCount");
            private static readonly int ShaftTextureId = Shader.PropertyToID("_ShaftTexture");
            private static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
            private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
            private static readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();

            private readonly Material material;
            private int downsample = 2;

            public SunShaftsPass(Material material)
            {
                this.material = material;
                requiresIntermediateTexture = true;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Setup(
                Vector2 sunPosition,
                Color color,
                float intensity,
                float maxRadius,
                float decay,
                float density,
                float exposure,
                int sampleCount,
                int downsample)
            {
                material.SetVector(SunPositionId, new Vector4(sunPosition.x, sunPosition.y, 0f, 0f));
                material.SetColor(ShaftColorId, color);
                material.SetVector(ShaftParamsId, new Vector4(intensity, maxRadius, decay, density));
                material.SetFloat(ShaftExposureId, exposure);
                material.SetInt(ShaftSampleCountId, Mathf.Clamp(sampleCount, 4, 32));
                this.downsample = Mathf.Clamp(downsample, 1, 4);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer)
                    return;

                TextureHandle source = resources.activeColorTexture;

                TextureDesc shaftDesc = renderGraph.GetTextureDesc(source);
                shaftDesc.name = "VoxelBuilder Sun Shafts Mask";
                shaftDesc.width = Mathf.Max(1, shaftDesc.width / downsample);
                shaftDesc.height = Mathf.Max(1, shaftDesc.height / downsample);
                shaftDesc.filterMode = FilterMode.Bilinear;
                shaftDesc.clearBuffer = false;
                shaftDesc.depthBufferBits = DepthBits.None;
                shaftDesc.msaaSamples = MSAASamples.None;
                TextureHandle shaftTexture = renderGraph.CreateTexture(shaftDesc);

                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<ShaftPassData>(
                    "VoxelBuilder Sun Shafts Mask", out ShaftPassData passData))
                {
                    passData.material = material;

                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(shaftTexture, 0, AccessFlags.Write);

                    builder.SetRenderFunc(static (ShaftPassData data, RasterGraphContext context) =>
                    {
                        Properties.Clear();
                        Properties.SetVector(BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
                        context.cmd.DrawProcedural(
                            Matrix4x4.identity,
                            data.material,
                            0,
                            MeshTopology.Triangles,
                            3,
                            1,
                            Properties);
                    });
                }

                TextureDesc destinationDesc = renderGraph.GetTextureDesc(source);
                destinationDesc.name = "VoxelBuilder Sun Shafts";
                destinationDesc.clearBuffer = false;
                destinationDesc.depthBufferBits = DepthBits.None;
                TextureHandle destination = renderGraph.CreateTexture(destinationDesc);

                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<PassData>(
                    "VoxelBuilder Sun Shafts", out PassData passData))
                {
                    passData.source = source;
                    passData.shaftTexture = shaftTexture;
                    passData.material = material;

                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseTexture(shaftTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    {
                        Properties.Clear();
                        Properties.SetTexture(BlitTextureId, data.source);
                        Properties.SetTexture(ShaftTextureId, data.shaftTexture);
                        Properties.SetVector(BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
                        context.cmd.DrawProcedural(
                            Matrix4x4.identity,
                            data.material,
                            1,
                            MeshTopology.Triangles,
                            3,
                            1,
                            Properties);
                    });
                }

                resources.cameraColor = destination;
            }

            private sealed class PassData
            {
                internal TextureHandle source;
                internal TextureHandle shaftTexture;
                internal Material material;
            }

            private sealed class ShaftPassData
            {
                internal Material material;
            }
        }
    }
}
