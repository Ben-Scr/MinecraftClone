Shader "BenScr/Fluid/Water"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (0.2, 0.45, 0.85, 0.65)
        _WaveSpeed ("Wave Speed", Vector) = (0.05, 0.04, -0.03, 0.02)
        _WaveScale ("Wave Scale", Float) = 1
        _FoamColor ("Foam Color", Color) = (0.85, 0.95, 1, 1)
        _FoamStrength ("Foam Strength", Range(0,1)) = 0.35
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 200
        Cull Back
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float4 _WaveSpeed;
            float _WaveScale;
            fixed4 _FoamColor;
            float _FoamStrength;

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
            };

            // einfache Wellenhöhe (für Position und Normalableitung)
            float Height(float2 xz, float t)
            {
                float h = 0.0;
                h += sin((xz.x + t * _WaveSpeed.x) * _WaveScale) * 0.03;
                h += sin((xz.y + t * _WaveSpeed.y) * _WaveScale) * 0.03;
                return h;
            }

            v2f vert(appdata v)
            {
                v2f o;

                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;

                float t = _Time.y;
                // vertex verschieben
                worldPos.y += Height(worldPos.xz, t);

                // KORREKT: Welt -> Clip
                o.pos = UnityWorldToClipPos(float4(worldPos, 1.0));
                o.worldPos = worldPos;

                // Basis-Normal (Objekt->Welt) normalisieren
                float3 n = UnityObjectToWorldNormal(v.normal);
                n = normalize(n);

                // grobe Normalableitung aus dem Höhenfeld (bessere Beleuchtung)
                // zentrale Differenzen
                const float eps = 0.05;
                float hL = Height(worldPos.xz + float2(-eps, 0), t);
                float hR = Height(worldPos.xz + float2( eps, 0), t);
                float hD = Height(worldPos.xz + float2(0, -eps), t);
                float hU = Height(worldPos.xz + float2(0,  eps), t);
                float3 dx = float3(2*eps, hR - hL, 0);
                float3 dz = float3(0, hU - hD, 2*eps);
                float3 nDerived = normalize(cross(dz, dx)); // y-Up

                // mische abgeleitete Normal leicht ein (stabiler)
                o.worldNormal = normalize(lerp(n, nDerived, 0.7));

                // UV-Scroll in Objekt-UV, dann _ST anwenden
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
                float fresnel = saturate(1.0 - dot(V, N));
                fixed4 foam = _FoamColor * pow(fresnel, 3.0) * _FoamStrength;

                col.rgb += foam.rgb;
                col.a   = saturate(col.a + foam.a);
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
