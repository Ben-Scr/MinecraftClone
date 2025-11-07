Shader "BenScr/Fluid/Water"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (0.2, 0.45, 0.85, 0.65)
        _WaveSpeed ("Wave Speed", Vector) = (0.05, 0.04, -0.03, 0.02)
        _WaveScale ("Wave Scale", Float) = 1
        _WaveAmplitude ("Wave Amplitude", Range(0, 0.5)) = 0.03
        _NormalBlend ("Normal Blend", Range(0, 1)) = 0.7
        _FoamColor ("Foam Color", Color) = (0.85, 0.95, 1, 1)
        _FoamStrength ("Foam Strength", Range(0,1)) = 0.35
        _FresnelPower ("Fresnel Power", Range(1, 8)) = 3
        _ReflectionStrength ("Reflection Strength", Range(0, 1)) = 0.5
        _ReflectionTint ("Reflection Tint", Color) = (0.6, 0.75, 0.9, 1)
        _ReflectionDistortion ("Reflection Distortion", Range(0, 1)) = 0.2
        _RefractionStrength ("Refraction Strength", Range(0, 1)) = 0.1
        _SpecularIntensity ("Specular Intensity", Range(0, 2)) = 0.5
        _Shininess ("Specular Shininess", Range(1, 128)) = 64
        _DepthColor ("Depth Color", Color) = (0.02, 0.12, 0.2, 1)
        _DepthFade ("Depth Fade", Range(0.1, 5)) = 1.5
        _DepthStrength ("Depth Strength", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 200
        Cull Back
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        GrabPass { "_WaterGrabTex" }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float4 _WaveSpeed;
            float _WaveScale;
            float _WaveAmplitude;
            float _NormalBlend;
            fixed4 _FoamColor;
            float _FoamStrength;
            float _FresnelPower;
            float _ReflectionStrength;
            fixed4 _ReflectionTint;
            float _ReflectionDistortion;
            float _RefractionStrength;
            float _SpecularIntensity;
            float _Shininess;
            fixed4 _DepthColor;
            float _DepthFade;
            float _DepthStrength;

            sampler2D _WaterGrabTex;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float2 uv1 : TEXCOORD2;
                float2 uv2 : TEXCOORD3;
                float4 grabPos : TEXCOORD4;
            };

            float Height(float2 xz, float t)
            {
                float amplitude = _WaveAmplitude;
                float h = 0.0;
                h += sin((xz.x + t * _WaveSpeed.x) * _WaveScale) * amplitude;
                h += sin((xz.y + t * _WaveSpeed.y) * _WaveScale) * amplitude;
                return h;
            }

            v2f vert(appdata v)
            {
                v2f o;

                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;

                float t = _Time.y;
                worldPos.y += Height(worldPos.xz, t);

                o.pos = UnityWorldToClipPos(float4(worldPos, 1.0));
                o.worldPos = worldPos;
                o.grabPos = ComputeGrabScreenPos(o.pos);

                float3 n = UnityObjectToWorldNormal(v.normal);
                n = normalize(n);

                const float eps = 0.05;
                float hL = Height(worldPos.xz + float2(-eps, 0), t);
                float hR = Height(worldPos.xz + float2( eps, 0), t);
                float hD = Height(worldPos.xz + float2(0, -eps), t);
                float hU = Height(worldPos.xz + float2(0,  eps), t);
                float3 dx = float3(2 * eps, hR - hL, 0);
                float3 dz = float3(0, hU - hD, 2 * eps);
                float3 nDerived = normalize(cross(dz, dx));

                o.worldNormal = normalize(lerp(n, nDerived, saturate(_NormalBlend)));

                float2 uv1 = v.uv * _WaveScale + t * _WaveSpeed.xy;
                float2 uv2 = v.uv * (_WaveScale * 0.5) + t * _WaveSpeed.zw;
                o.uv1 = TRANSFORM_TEX(uv1, _MainTex);
                o.uv2 = TRANSFORM_TEX(uv2, _MainTex);

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 baseSample   = tex2D(_MainTex, i.uv1);
                fixed4 detailSample = tex2D(_MainTex, i.uv2);
                fixed4 col = lerp(baseSample, detailSample, 0.5) * _Color;

                float3 N = normalize(i.worldNormal);
                float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
                float fresnel = pow(saturate(1.0 - dot(V, N)), _FresnelPower);

                float4 grabPos = i.grabPos;
                float2 noiseOffset = (detailSample.rg * 2.0 - 1.0) * _RefractionStrength;
                float4 refractPos = grabPos;
                refractPos.xy += noiseOffset * refractPos.w;
                fixed4 refracted = tex2Dproj(_WaterGrabTex, refractPos);

                float4 reflectPos = grabPos;
                float2 reflectOffset = float2(N.x, -N.z) * _ReflectionDistortion;
                reflectPos.xy -= reflectOffset * reflectPos.w;
                fixed4 reflectedSample = tex2Dproj(_WaterGrabTex, reflectPos);
                float3 reflectedColor = reflectedSample.rgb * _ReflectionTint.rgb;

                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                float lightAtten = _WorldSpaceLightPos0.w;
                if (lightAtten != 0.0)
                {
                    float3 lightVec = _WorldSpaceLightPos0.xyz - i.worldPos * _WorldSpaceLightPos0.w;
                    lightDir = normalize(lightVec);
                }

                float NdotL = saturate(dot(N, lightDir));
                float3 halfDir = normalize(lightDir + V);
                float specular = pow(saturate(dot(N, halfDir)), _Shininess) * _SpecularIntensity;
                float3 ambient = UNITY_LIGHTMODEL_AMBIENT.rgb;
                float3 litColor = col.rgb * (ambient + NdotL);
                litColor += specular * _LightColor0.rgb;

                float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(grabPos)));
                float surfaceDepth = LinearEyeDepth(UNITY_Z_0_FAR_FROM_CLIPSPACE(i.pos.z));
                float depthDiff = max(sceneDepth - surfaceDepth, 0.0);
                float depthLerp = saturate(depthDiff / max(_DepthFade, 1e-4)) * _DepthStrength;
                litColor = lerp(litColor, _DepthColor.rgb, depthLerp);

                float reflectionFactor = saturate(fresnel * _ReflectionStrength);
                float refractionFactor = saturate(_RefractionStrength * (1.0 - reflectionFactor));
                float3 combined = litColor;
                combined = lerp(combined, refracted.rgb, refractionFactor);
                combined = lerp(combined, reflectedColor, reflectionFactor);

                fixed4 foam = _FoamColor * pow(fresnel, 3.0) * _FoamStrength;
                combined += foam.rgb;

                fixed4 finalColor = fixed4(combined, saturate(col.a + foam.a));
                return finalColor;
            }
            ENDCG
        }
    }
    Fallback Off
}