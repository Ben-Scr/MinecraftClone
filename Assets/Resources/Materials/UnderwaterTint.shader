Shader "Hidden/UnderwaterTint"
{
    Properties {
        _TintColor ("Tint", Color) = (0.10, 0.40, 0.80, 1.0)
        _Strength  ("Strength", Range(0,1)) = 0.0
    }
    SubShader {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always
        Pass {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _TintColor;
            float  _Strength;

            fixed4 frag (v2f_img i) : SV_Target {
                fixed4 c = tex2D(_MainTex, i.uv);
                c.rgb = lerp(c.rgb, _TintColor.rgb, _Strength);
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
