Shader "Hidden/VoxelBuilder/SunShafts"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        float2 _SunPosition;
        half4 _ShaftColor;
        // x: intensity, y: maximum radius, z: decay, w: density
        float4 _ShaftParams;
        float _ShaftExposure;
        int _ShaftSampleCount;
        TEXTURE2D_X(_ShaftTexture);

        half4 FragShaftMask(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            float2 toSun = _SunPosition - uv;
            float distanceToSun = length(toSun * float2(_ScreenParams.x / _ScreenParams.y, 1.0));
            float radialFade = saturate(1.0 - distanceToSun / max(_ShaftParams.y, 0.001));
            radialFade *= radialFade;

            int sampleCount = clamp(_ShaftSampleCount, 4, 32);
            float2 stepUv = toSun * (_ShaftParams.w / sampleCount);
            float2 sampleUv = uv;
            half illumination = 1.0h;
            half accumulatedSky = 0.0h;

            UNITY_LOOP
            for (int i = 0; i < sampleCount; i++)
            {
                sampleUv += stepUv;
                float inBounds = step(0.0, sampleUv.x) * step(sampleUv.x, 1.0) *
                                 step(0.0, sampleUv.y) * step(sampleUv.y, 1.0);
                float rawDepth = SampleSceneDepth(saturate(sampleUv));
                float linearDepth = Linear01Depth(rawDepth, _ZBufferParams);
                half sky = step(0.999h, (half)linearDepth) * inBounds;
                accumulatedSky += sky * illumination;
                illumination *= (half)_ShaftParams.z;
            }

            half shafts = accumulatedSky / sampleCount;
            shafts *= (half)(_ShaftParams.x * _ShaftExposure * radialFade);
            return half4(shafts, shafts, shafts, 1.0h);
        }

        half4 FragComposite(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            half3 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            half shafts = SAMPLE_TEXTURE2D_X(_ShaftTexture, sampler_LinearClamp, uv).r;

            // Screen blend preserves highlights and avoids washing out the voxel palette.
            half3 rayColor = _ShaftColor.rgb * shafts;
            half3 result = 1.0h - (1.0h - source) * (1.0h - saturate(rayColor));
            return half4(result, 1.0h);
        }
        ENDHLSL

        Pass
        {
            Name "SunShaftMask"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragShaftMask
            #pragma target 3.5
            ENDHLSL
        }

        Pass
        {
            Name "SunShaftComposite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 3.5
            ENDHLSL
        }
    }
    Fallback Off
}
