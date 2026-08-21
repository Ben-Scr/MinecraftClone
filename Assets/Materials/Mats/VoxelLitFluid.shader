Shader "VoxelBuilder/URP/VoxelLitFluid"
{
    Properties
    {
        _BlockTextures("Block Texture Array", 2DArray) = "" {}
        _BaseColor("Base Color", Color) = (0.05,0.42,0.9,0.62)
        _AmbientStrength("Ambient Strength", Range(0, 1.5)) = 1
        _Smoothness("Smoothness", Range(0, 1)) = 0.8
        _SpecularStrength("Specular Strength", Range(0, 1)) = 0.32
        _EnvironmentLightingStrength("Environment Lighting", Range(0, 1)) = 1
        _SideBrightness("Voxel Side Brightness", Range(0.4, 1)) = 0.78
        _SideReflectionStrength("Voxel Side Reflection", Range(0, 1)) = 0.025
        _BlockLightColor("Block Light Color", Color) = (1,0.58,0.3,1)
        _BlockLightStrength("Block Light Strength", Range(0, 2)) = 1
        _MinimumCaveVisibility("Minimum Cave Visibility", Range(0, 0.2)) = 0.05
        _SkylightTransitionExponent("Skylight Transition", Range(0.4, 2.5)) = 1.35
        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0,1)
        _EmissionStrength("Emission Strength", Range(0, 4)) = 0
        _FlowSpeed("Flow Speed", Vector) = (0.012,0.008,0,0)
        [Toggle] _UseVoxelLighting("Use Voxel Lighting", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma require 2darray
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 textureData : TEXCOORD1;
                // Unity Mesh.uv3 / SetUVs(2): normalized (sky light, block light).
                float2 lightingData : TEXCOORD2;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float textureLayer : TEXCOORD2;
                half2 lightingData : TEXCOORD3;
                half4 color : COLOR;
                half fogFactor : TEXCOORD4;
            };

            TEXTURE2D_ARRAY(_BlockTextures);
            SAMPLER(sampler_BlockTextures);

            // Zero is the safe editor/no-controller default: full daylight.
            float _WorldNightFactor;

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _AmbientStrength;
                half _Smoothness;
                half _SpecularStrength;
                half _EnvironmentLightingStrength;
                half _SideBrightness;
                half _SideReflectionStrength;
                half4 _BlockLightColor;
                half _BlockLightStrength;
                half _MinimumCaveVisibility;
                half _SkylightTransitionExponent;
                half4 _EmissionColor;
                half _EmissionStrength;
                float4 _FlowSpeed;
                half _UseVoxelLighting;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
                output.textureLayer = input.textureData.x;
                output.lightingData = input.lightingData;
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return output;
            }

            float2 ComputeFluidUv(float3 positionWS, float3 normalWS)
            {
                float3 absNormal = abs(normalWS);
                float2 planeUv;

                if (absNormal.y >= absNormal.x && absNormal.y >= absNormal.z)
                    planeUv = positionWS.xz;
                else if (absNormal.x >= absNormal.z)
                    planeUv = positionWS.zy;
                else
                    planeUv = positionWS.xy;

                return frac(planeUv + _Time.y * _FlowSpeed.xy);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);
                float2 fluidUv = ComputeFluidUv(input.positionWS, normalWS);
                half4 texel = SAMPLE_TEXTURE2D_ARRAY(
                    _BlockTextures,
                    sampler_BlockTextures,
                    fluidUv,
                    input.textureLayer);

                half3 vertexTint = input.color.a > 0.5h
                    ? input.color.rgb
                    : half3(1.0h, 1.0h, 1.0h);
                half3 albedo = texel.rgb * _BaseColor.rgb * vertexTint;
                half alpha = saturate(texel.a * _BaseColor.a);

                half useVoxelLighting = saturate(_UseVoxelLighting);
                half rawSkyLight = lerp(1.0h, saturate(input.lightingData.x), useVoxelLighting);
                half curvedSkyLight = pow(saturate(rawSkyLight), _SkylightTransitionExponent);
                half skyTransition = curvedSkyLight * curvedSkyLight * (3.0h - 2.0h * curvedSkyLight);
                half blockLight = saturate(input.lightingData.y) * useVoxelLighting;
                half environmentDaylight = 1.0h - saturate(_WorldNightFactor);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

                // A voxel fluid is a connected volume, but its step walls are rendered
                // as two-sided transparent quads. One-sided lighting made backfaces hit
                // maximum Fresnel and turned individual water/lava faces into bright
                // sheets. Stable top/side shading keeps opposite faces consistent and
                // matches the restrained directional shading used by Minecraft.
                half3 absNormalWS = abs(normalWS);
                half topFace = saturate(normalWS.y);
                half bottomFace = saturate(-normalWS.y);
                half sideFace = saturate(1.0h - topFace - bottomFace);
                half sideAxisShade = lerp(
                    _SideBrightness,
                    min(1.0h, _SideBrightness + 0.06h),
                    absNormalWS.z);
                half faceShade = topFace + bottomFace * 0.56h + sideFace * sideAxisShade;
                half nDotL = saturate(mainLight.direction.y) * faceShade;
                half reflectionWeight = lerp(_SideReflectionStrength, 1.0h, topFace);
                half environmentLighting = saturate(_EnvironmentLightingStrength);

                half3 halfVector = normalize(mainLight.direction + viewDirectionWS);
                half specularPower = lerp(16.0h, 96.0h, _Smoothness);
                half specular = pow(saturate(abs(dot(normalWS, halfVector))), specularPower) *
                    _SpecularStrength * reflectionWeight;

                half skyLerp = saturate(normalWS.y * 0.5h + 0.5h);
                half3 hemisphere = lerp(
                    half3(0.12h, 0.13h, 0.15h),
                    half3(0.34h, 0.39h, 0.48h),
                    skyLerp);
                // Fluids receive their ambient environment mainly from the sky rather
                // than from the arbitrary outward normal of a generated voxel wall.
                half3 bakedAmbient = SampleSH(half3(0.0h, 1.0h, 0.0h));

                // All environment-derived terms are gated by skylight. This keeps cave
                // water dark while preserving a bright, reflective outdoor surface.
                half mainAttenuation = mainLight.distanceAttenuation * mainLight.shadowAttenuation;
                half3 direct = albedo * mainLight.color * (nDotL * mainAttenuation * rawSkyLight * environmentLighting);
                half3 ambient = albedo * (bakedAmbient + hemisphere * 0.32h) *
                    (_AmbientStrength * skyTransition * environmentDaylight * environmentLighting * faceShade);
                half outdoorDay = skyTransition * smoothstep(0.25h, 0.85h, environmentDaylight);
                ambient += albedo * (0.12h * outdoorDay * environmentLighting * faceShade);
                half3 highlight = mainLight.color * (specular * mainAttenuation * rawSkyLight * environmentLighting);
                half nDotV = saturate(abs(dot(viewDirectionWS, normalWS)));
                half fresnel = pow(1.0h - nDotV, 4.0h) * reflectionWeight;
                half3 skyReflection = half3(0.12h, 0.23h, 0.38h) *
                    (fresnel * skyTransition * environmentDaylight * environmentLighting);
                half caveMask = useVoxelLighting * (1.0h - skyTransition) * (1.0h - blockLight);
                half3 caveFill = albedo * (_MinimumCaveVisibility * caveMask);

                // Propagated block light is deliberately warm, matching solid blocks.
                half3 localLight = albedo * _BlockLightColor.rgb * (blockLight * _BlockLightStrength);

                // Material emission is independent of skylight so lava remains visible at
                // its source even in a completely enclosed cave. Water leaves this at zero.
                half3 emission = albedo * _EmissionColor.rgb * _EmissionStrength;

                half3 color = direct + ambient + highlight + skyReflection + caveFill + localLight + emission;

                // Real-time local lights are independent of skylight, matching the solid
                // block shader so point/spot lights still illuminate fluids underground.
                #if defined(_ADDITIONAL_LIGHTS)
                uint additionalLightsCount = GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < additionalLightsCount; ++lightIndex)
                {
                    Light light = GetAdditionalLight(lightIndex, input.positionWS);
                    half lightNdotL = saturate(abs(dot(normalWS, light.direction))) * faceShade;
                    half3 lightHalfVector = normalize(light.direction + viewDirectionWS);
                    half lightSpecular = pow(
                        saturate(abs(dot(normalWS, lightHalfVector))),
                        specularPower) * (_SpecularStrength * 0.6h) * reflectionWeight;
                    half attenuation = light.distanceAttenuation * light.shadowAttenuation;

                    color += albedo * light.color * (lightNdotL * attenuation);
                    color += lightSpecular * light.color * attenuation;
                }
                #endif

                color = MixFogColor(color, unity_FogColor.rgb, input.fogFactor * skyTransition);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
