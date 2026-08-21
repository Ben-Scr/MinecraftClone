Shader "VoxelBuilder/URP/BlockAtlasTiledLit_2"
{
    Properties
    {
        _BaseMap("Atlas Texture", 2D) = "white" {}
        _BlockTextures("Block Texture Array", 2DArray) = "" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _CLUSTERED_RENDERING

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 textureData : TEXCOORD1;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                float textureLayer : TEXCOORD4;
                float overlayTextureLayer : TEXCOORD5;
                half4 color : COLOR;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D_ARRAY(_BlockTextures);
            SAMPLER(sampler_BlockTextures);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _Cutoff;
                half _UseMeshBlockUv;
            CBUFFER_END

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
                output.color = input.color;
                return output;
            }

            float2 ComputeBlockUv(float3 positionWS, float3 normalWS)
            {
                // Tile once per world unit on the dominant face plane.
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

                float2 local = frac(planeUv);
                return local;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float2 blockUv = _UseMeshBlockUv > 0.5h
                    ? input.uv
                    : ComputeBlockUv(input.positionWS, normalWS);

                half4 albedo = SAMPLE_TEXTURE2D_ARRAY(_BlockTextures, sampler_BlockTextures, blockUv, input.textureLayer) * _BaseColor;
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

                clip(albedo.a - _Cutoff);

                InputData lightingInput = (InputData)0;
                lightingInput.positionWS = input.positionWS;
                lightingInput.normalWS = normalWS;
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                lightingInput.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                lightingInput.fogCoord = input.fogFactor;
                lightingInput.vertexLighting = VertexLighting(input.positionWS, normalWS);
                lightingInput.bakedGI = SampleSH(normalWS);
                lightingInput.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                Light mainLight = GetMainLight(lightingInput.shadowCoord);
                half NdotL = saturate(dot(normalWS, mainLight.direction));

                half3 diffuse = albedo.rgb * (lightingInput.bakedGI + mainLight.color * (NdotL * mainLight.shadowAttenuation));

                #if defined(_ADDITIONAL_LIGHTS)
                uint additionalLightsCount = GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < additionalLightsCount; ++lightIndex)
                {
                    Light light = GetAdditionalLight(lightIndex, input.positionWS);
                    half nDotL = saturate(dot(normalWS, light.direction));
                    diffuse += albedo.rgb * light.color * (nDotL * light.distanceAttenuation * light.shadowAttenuation);
                }
                #endif

                half4 color = half4(diffuse, albedo.a);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                return color;
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

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
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float textureLayer : TEXCOORD3;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D_ARRAY(_BlockTextures);
            SAMPLER(sampler_BlockTextures);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _Cutoff;
                half _UseMeshBlockUv;
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

            half4 ShadowPassFragment(Varyings input) : SV_TARGET
            {
                float3 normalWS = normalize(input.normalWS);
                float2 blockUv = _UseMeshBlockUv > 0.5h
                    ? input.uv
                    : ComputeBlockUv(input.positionWS, normalWS);
                half4 albedo = SAMPLE_TEXTURE2D_ARRAY(_BlockTextures, sampler_BlockTextures, blockUv, input.textureLayer) * _BaseColor;
                clip(albedo.a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
