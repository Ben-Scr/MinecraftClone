Shader "VoxelBuilder/URP/BlockAtlasTiledLit"
{
    Properties
    {
        _BaseMap("Atlas Texture", 2D) = "white" {}
        _BlockTextures("Block Texture Array", 2DArray) = "" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clipping", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
        _AmbientStrength("Ambient Strength", Range(0, 1)) = 1
        _Saturation("Color Saturation", Range(0, 2)) = 1.1
        _Contrast("Color Contrast", Range(0.5, 2)) = 1.05
        _VariationStrength("World Variation", Range(0, 0.25)) = 0.08
        _SpecularStrength("Specular Strength", Range(0, 1)) = 0.12
        _SpecularPower("Specular Power", Range(4, 64)) = 24
        _RimColor("Rim Color", Color) = (0.85,0.95,1,1)
        _RimStrength("Rim Strength", Range(0, 1)) = 0.05
        _RimPower("Rim Power", Range(0.5, 8)) = 3
        _BlockLightColor("Block Light Color", Color) = (1,0.58,0.3,1)
        _BlockLightStrength("Block Light Strength", Range(0, 2)) = 1
        _MinimumCaveVisibility("Minimum Cave Visibility", Range(0, 0.2)) = 0.03
        _SkylightTransitionExponent("Skylight Transition", Range(0.4, 2.5)) = 1.35
        _VoxelAoStrength("Voxel Corner AO", Range(0, 1)) = 0.68
        _FaceShadingStrength("Directional Face Shading", Range(0, 1)) = 0.72
        _ReliefNormalStrength("Texture Relief", Range(0, 1)) = 0.28
        [Toggle] _UseVoxelLighting("Use Voxel Lighting", Float) = 0
        [Toggle] _UseObjectLighting("Use Object Lighting", Float) = 0
        _ObjectLighting("Object Lighting", Vector) = (1,0,0,0)
        _UseMeshBlockUv("Use Mesh Block UV", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma require 2darray
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float2 textureData : TEXCOORD1;
                // Unity Mesh.uv3 / SetUVs(2).
                float2 lightingData : TEXCOORD2;
                // Unity Mesh.uv4 / SetUVs(3): classic voxel corner AO.
                float2 ambientData : TEXCOORD3;
                half4 color       : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
                float  textureLayer : TEXCOORD4;
                float  overlayTextureLayer : TEXCOORD5;
                float2 lightingData : TEXCOORD6;
                half ambientOcclusion : TEXCOORD7;
                half4  color      : COLOR;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D_ARRAY(_BlockTextures);
            SAMPLER(sampler_BlockTextures);

            // Zero is the safe editor/no-controller default: full daylight.
            float _WorldNightFactor;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _AlphaClip;
                float _Cutoff;
                half _AmbientStrength;
                half _Saturation;
                half _Contrast;
                half _VariationStrength;
                half _SpecularStrength;
                half _SpecularPower;
                half4 _RimColor;
                half _RimStrength;
                half _RimPower;
                half4 _BlockLightColor;
                half _BlockLightStrength;
                half _MinimumCaveVisibility;
                half _SkylightTransitionExponent;
                half _UseVoxelLighting;
                half _UseObjectLighting;
                half4 _ObjectLighting;
                half _UseMeshBlockUv;
                half _VoxelAoStrength;
                half _FaceShadingStrength;
                half _ReliefNormalStrength;
            CBUFFER_END

            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            half3 AdjustSaturationAndContrast(half3 color)
            {
                half luminance = dot(color, half3(0.2126h, 0.7152h, 0.0722h));
                half3 saturated = lerp(luminance.xxx, color, _Saturation);
                return (saturated - 0.5h) * _Contrast + 0.5h;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
                output.uv = input.uv;
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                output.textureLayer = input.textureData.x;
                output.overlayTextureLayer = input.textureData.y;
                output.lightingData = input.lightingData;
                output.ambientOcclusion = input.ambientData.x;
                output.color = input.color;

                return output;
            }

            float2 ComputeBlockUv(float3 positionWS, float3 normalWS)
            {
                // Tile in world space depending on dominant face axis.
                float3 absN = abs(normalWS);
                float2 planeUv;

                if (absN.y >= absN.x && absN.y >= absN.z)
                {
                    // Top/bottom faces
                    planeUv = positionWS.xz;
                }
                else if (absN.x >= absN.z)
                {
                    // X-facing faces
                    planeUv = positionWS.zy;
                }
                else
                {
                    // Z-facing faces
                    planeUv = positionWS.xy;
                }

                float2 local = frac(planeUv);
                return local;
            }

            float3 ApplyTextureReliefNormal(
                float2 blockUv,
                float textureLayer,
                float3 geometricNormalWS,
                half centerHeight)
            {
                const float texelSize = 1.0 / 32.0;
                half3 sampleU = SAMPLE_TEXTURE2D_ARRAY(
                    _BlockTextures, sampler_BlockTextures, blockUv + float2(texelSize, 0.0), textureLayer).rgb;
                half3 sampleV = SAMPLE_TEXTURE2D_ARRAY(
                    _BlockTextures, sampler_BlockTextures, blockUv + float2(0.0, texelSize), textureLayer).rgb;
                half heightU = dot(sampleU, half3(0.2126h, 0.7152h, 0.0722h));
                half heightV = dot(sampleV, half3(0.2126h, 0.7152h, 0.0722h));
                half gradientU = clamp(heightU - centerHeight, -0.2h, 0.2h);
                half gradientV = clamp(heightV - centerHeight, -0.2h, 0.2h);

                float3 absN = abs(geometricNormalWS);
                float3 tangentU;
                float3 tangentV;
                if (absN.y >= absN.x && absN.y >= absN.z)
                {
                    tangentU = float3(1.0, 0.0, 0.0);
                    tangentV = float3(0.0, 0.0, 1.0);
                }
                else if (absN.x >= absN.z)
                {
                    tangentU = float3(0.0, 0.0, 1.0);
                    tangentV = float3(0.0, 1.0, 0.0);
                }
                else
                {
                    tangentU = float3(1.0, 0.0, 0.0);
                    tangentV = float3(0.0, 1.0, 0.0);
                }

                return normalize(
                    geometricNormalWS -
                    tangentU * gradientU * (_ReliefNormalStrength * 5.0h) -
                    tangentV * gradientV * (_ReliefNormalStrength * 5.0h));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 geometricNormalWS = normalize(input.normalWS);
                float2 blockUv = _UseMeshBlockUv > 0.5h
                    ? input.uv
                    : ComputeBlockUv(input.positionWS, geometricNormalWS);

                half4 albedo = SAMPLE_TEXTURE2D_ARRAY(_BlockTextures, sampler_BlockTextures, blockUv, input.textureLayer) * _BaseColor;
                half centerHeight = dot(albedo.rgb, half3(0.2126h, 0.7152h, 0.0722h));
                float3 normalWS = ApplyTextureReliefNormal(
                    blockUv, input.textureLayer, geometricNormalWS, centerHeight);
                half3 tint = input.color.a > 0.5h ? input.color.rgb : half3(1.0h, 1.0h, 1.0h);

                if (input.overlayTextureLayer > 0.5)
                {
                    half4 overlay = SAMPLE_TEXTURE2D_ARRAY(_BlockTextures, sampler_BlockTextures, blockUv, input.overlayTextureLayer);
                    half3 tintedOverlay = overlay.rgb * tint * _BaseColor.rgb;
                    albedo.rgb = lerp(albedo.rgb, tintedOverlay, overlay.a);
                    albedo.a = max(albedo.a, overlay.a * _BaseColor.a);
                }
                else
                {
                    albedo.rgb *= tint;
                }

                #if defined(_ALPHATEST_ON)
                    clip(albedo.a - _Cutoff);
                #endif

                InputData lightingInput = (InputData)0;
                lightingInput.positionWS = input.positionWS;
                lightingInput.normalWS = normalWS;
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                lightingInput.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                lightingInput.fogCoord = input.fogFactor;
                lightingInput.vertexLighting = VertexLighting(input.positionWS, normalWS);
                lightingInput.bakedGI = SampleSH(normalWS);
                lightingInput.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                half3 viewDirWS = lightingInput.viewDirectionWS;

                // Mesh.uv3 stores normalized voxel lighting as (sky light, block light).
                // Sky light gates every environment-derived term so an enclosed voxel can
                // become fully dark. Local/additional lights remain independent so torches
                // and other real-time lights still work underground.
                half useVoxelLighting = saturate(_UseVoxelLighting);
                half2 sampledLighting = lerp(
                    saturate(input.lightingData),
                    saturate(_ObjectLighting.xy),
                    saturate(_UseObjectLighting));
                half rawSkyLight = lerp(1.0h, sampledLighting.x, useVoxelLighting);
                half curvedSkyLight = pow(saturate(rawSkyLight), _SkylightTransitionExponent);
                half skyTransition = curvedSkyLight * curvedSkyLight * (3.0h - 2.0h * curvedSkyLight);
                half blockLight = sampledLighting.y * useVoxelLighting;
                half environmentDaylight = 1.0h - saturate(_WorldNightFactor);
                half voxelAo = lerp(1.0h, saturate(input.ambientOcclusion), useVoxelLighting);
                voxelAo = lerp(1.0h, voxelAo, _VoxelAoStrength);

                // Stable face-direction contrast is a deliberate voxel rendering cue.
                // Direct light remains physically directional; this shapes the broad
                // environment fill so top, side and underside planes stay readable.
                half topFace = saturate(geometricNormalWS.y);
                half bottomFace = saturate(-geometricNormalWS.y);
                half sideFace = saturate(1.0h - topFace - bottomFace);
                half sideShade = lerp(0.76h, 0.86h, abs(geometricNormalWS.x));
                half faceShade = topFace + bottomFace * 0.56h + sideFace * sideShade;
                faceShade = lerp(1.0h, faceShade, _FaceShadingStrength);

                Light mainLight = GetMainLight(lightingInput.shadowCoord);
                half NdotL = saturate(dot(normalWS, mainLight.direction));
                half3 halfVector = normalize(mainLight.direction + viewDirWS);
                half spec = pow(saturate(dot(normalWS, halfVector)), _SpecularPower) * _SpecularStrength;

                half skyLerp = saturate(normalWS.y * 0.5h + 0.5h);
                half3 hemisphere = lerp(half3(0.14h, 0.13h, 0.12h), half3(0.30h, 0.35h, 0.42h), skyLerp);

                half worldVariation = (Hash31(floor(input.positionWS * 2.0)) * 2.0h - 1.0h) * _VariationStrength;
                half3 stylizedAlbedo = AdjustSaturationAndContrast(albedo.rgb * (1.0h + worldVariation));

                half rim = pow(1.0h - saturate(dot(viewDirWS, normalWS)), _RimPower) * _RimStrength * skyTransition * environmentDaylight;

                half directAo = lerp(1.0h, voxelAo, 0.2h);
                half3 directDiffuse = stylizedAlbedo * mainLight.color * (NdotL * mainLight.distanceAttenuation * mainLight.shadowAttenuation * rawSkyLight * directAo);
                half3 ambient = stylizedAlbedo * (lightingInput.bakedGI + hemisphere * 0.40h) * (_AmbientStrength * skyTransition * environmentDaylight * voxelAo * faceShade);
                half outdoorDay = skyTransition * smoothstep(0.25h, 0.85h, environmentDaylight);
                ambient += stylizedAlbedo * (0.12h * outdoorDay * voxelAo * faceShade);
                half caveMask = useVoxelLighting * (1.0h - skyTransition) * (1.0h - blockLight);
                half3 caveFill = stylizedAlbedo * (_MinimumCaveVisibility * caveMask * voxelAo);

                half3 diffuse = directDiffuse + ambient + caveFill;
                diffuse += spec * mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation * rawSkyLight;
                diffuse += rim * _RimColor.rgb;

                // This makes the second lighting channel useful immediately and leaves a
                // simple hook for propagated torch/lava light without faking global ambient.
                diffuse += stylizedAlbedo * _BlockLightColor.rgb * (blockLight * _BlockLightStrength * lerp(1.0h, voxelAo, 0.55h));

                #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                diffuse += stylizedAlbedo * lightingInput.vertexLighting;
                #endif

                #if defined(_ADDITIONAL_LIGHTS)
                uint additionalLightsCount = GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < additionalLightsCount; ++lightIndex)
                {
                    Light light = GetAdditionalLight(lightIndex, input.positionWS);
                    half nDotL = saturate(dot(normalWS, light.direction));
                    half3 addHalf = normalize(light.direction + viewDirWS);
                    half addSpec = pow(saturate(dot(normalWS, addHalf)), _SpecularPower) * (_SpecularStrength * 0.6h);

                    diffuse += stylizedAlbedo * light.color * (nDotL * light.distanceAttenuation * light.shadowAttenuation);
                    diffuse += addSpec * light.color * light.distanceAttenuation * light.shadowAttenuation;
                }
                #endif

                half4 color = half4(diffuse, albedo.a);
                // Open-air fog fades with the eased skylight response. Enclosed surfaces keep
                // their cave-visibility floor instead of being replaced by pure black fog.
                color.rgb = MixFogColor(color.rgb, unity_FogColor.rgb, input.fogFactor * skyTransition);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma require 2darray
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 textureData : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float textureLayer : TEXCOORD3;
            };

            TEXTURE2D_ARRAY(_BlockTextures);
            SAMPLER(sampler_BlockTextures);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _AlphaClip;
                float _Cutoff;
                half _AmbientStrength;
                half _Saturation;
                half _Contrast;
                half _VariationStrength;
                half _SpecularStrength;
                half _SpecularPower;
                half4 _RimColor;
                half _RimStrength;
                half _RimPower;
                half4 _BlockLightColor;
                half _BlockLightStrength;
                half _MinimumCaveVisibility;
                half _SkylightTransitionExponent;
                half _UseVoxelLighting;
                half _UseObjectLighting;
                half4 _ObjectLighting;
                half _UseMeshBlockUv;
                half _VoxelAoStrength;
                half _FaceShadingStrength;
                half _ReliefNormalStrength;
            CBUFFER_END

            float2 DepthNormalsBlockUv(float3 positionWS, float3 normalWS)
            {
                float3 absN = abs(normalWS);
                if (absN.y >= absN.x && absN.y >= absN.z)
                    return frac(positionWS.xz);
                if (absN.x >= absN.z)
                    return frac(positionWS.zy);
                return frac(positionWS.xy);
            }

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
                output.uv = input.uv;
                output.textureLayer = input.textureData.x;
                return output;
            }

            half4 DepthNormalsFragment(Varyings input) : SV_Target
            {
                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                #if defined(_ALPHATEST_ON)
                    float2 blockUv = _UseMeshBlockUv > 0.5h
                        ? input.uv
                        : DepthNormalsBlockUv(input.positionWS, normalWS);
                    half alpha = SAMPLE_TEXTURE2D_ARRAY(
                        _BlockTextures, sampler_BlockTextures, blockUv, input.textureLayer).a * _BaseColor.a;
                    clip(alpha - _Cutoff);
                #endif

                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                    return half4(PackFloat2To888(remappedOctNormalWS), 0.0h);
                #else
                    return half4(normalWS, 0.0h);
                #endif
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma require 2darray
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float2 textureData : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float  textureLayer : TEXCOORD3;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D_ARRAY(_BlockTextures);
            SAMPLER(sampler_BlockTextures);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _AlphaClip;
                float _Cutoff;
                half _AmbientStrength;
                half _Saturation;
                half _Contrast;
                half _VariationStrength;
                half _SpecularStrength;
                half _SpecularPower;
                half4 _RimColor;
                half _RimStrength;
                half _RimPower;
                half4 _BlockLightColor;
                half _BlockLightStrength;
                half _MinimumCaveVisibility;
                half _SkylightTransitionExponent;
                half _UseVoxelLighting;
                half _UseObjectLighting;
                half4 _ObjectLighting;
                half _UseMeshBlockUv;
                half _VoxelAoStrength;
                half _FaceShadingStrength;
                half _ReliefNormalStrength;
            CBUFFER_END

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

                float3 positionWS = ApplyShadowBias(vertexInput.positionWS, normalInput.normalWS, _MainLightPosition.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionWS = vertexInput.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normalInput.normalWS);
                output.uv = input.uv;
                output.textureLayer = input.textureData.x;

                return output;
            }

            float2 ComputeBlockUv(float3 positionWS, float3 normalWS)
            {
                float3 absN = abs(normalWS);
                float2 planeUv;

                if (absN.y >= absN.x && absN.y >= absN.z)
                {
                    planeUv = positionWS.xz;
                }
                else if (absN.x >= absN.z)
                {
                    planeUv = positionWS.zy;
                }
                else
                {
                    planeUv = positionWS.xy;
                }

                return frac(planeUv);
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                #if defined(_ALPHATEST_ON)
                    float3 normalWS = normalize(input.normalWS);
                    float2 blockUv = _UseMeshBlockUv > 0.5h
                        ? input.uv
                        : ComputeBlockUv(input.positionWS, normalWS);
                    half4 albedo = SAMPLE_TEXTURE2D_ARRAY(
                        _BlockTextures, sampler_BlockTextures, blockUv, input.textureLayer) * _BaseColor;
                    clip(albedo.a - _Cutoff);
                #endif

                return 0;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
