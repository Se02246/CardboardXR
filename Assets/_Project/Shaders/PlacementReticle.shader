// Modern 3D spatial placement reticle rendered flush on real surfaces (depth, planes, or estimated floor).
// Draws an anti-aliased ring, soft center dot, translucent disc fill, optional animated dashed contour for
// estimated/unverified surfaces, and an optional directional chevron toward +Y UV (used for Portal doors).
Shader "CardboardXR/PlacementReticle"
{
    Properties
    {
        _Color ("Color", Color) = (0.86, 0.96, 1, 0.95)
        _OutlineColor ("Outline Color", Color) = (0.04, 0.08, 0.14, 0.55)
        _FillColor ("Fill Color", Color) = (0.45, 0.86, 1, 0.16)
        _Alpha ("Alpha", Range(0, 1)) = 1
        _Dashed ("Dashed", Range(0, 1)) = 0
        _Directional ("Directional", Range(0, 1)) = 0
        _Pulse ("Pulse", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent-1"
        }

        Pass
        {
            Name "PlacementReticle"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _OutlineColor;
                half4 _FillColor;
                float _Alpha;
                float _Dashed;
                float _Directional;
                float _Pulse;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 offset : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.offset = input.uv * 2.0 - 1.0;
                return output;
            }

            float Band(float radius, float inner, float outer, float pixel)
            {
                return smoothstep(inner - pixel, inner, radius) * (1.0 - smoothstep(outer - pixel, outer, radius));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.offset;
                float radius = length(uv);
                float pixel = clamp(fwidth(radius), 0.002, 0.08);

                float ringMid = 0.82 + (_Pulse - 0.5) * 0.025;
                float ringHalf = 0.055;
                float ring = Band(radius, ringMid - ringHalf, ringMid + ringHalf, pixel);
                float ringOutline = Band(radius, ringMid - ringHalf - 0.035, ringMid + ringHalf + 0.035, pixel);

                float angle = atan2(uv.y, uv.x) + _Time.y * 1.2;
                float dashWave = sin(angle * 12.0);
                float dashFactor = lerp(1.0, smoothstep(-0.15, 0.25, dashWave), saturate(_Dashed));
                ring *= dashFactor;
                ringOutline *= dashFactor;

                // Soft translucent inner disc inside the ring.
                float fillMask = 1.0 - smoothstep(ringMid - ringHalf - pixel, ringMid - ringHalf, radius);

                // Center precision dot.
                float dot = 1.0 - smoothstep(0.105 - pixel, 0.105 + pixel, radius);
                float dotOutline = 1.0 - smoothstep(0.145 - pixel, 0.145 + pixel, radius);

                // Subtle directional chevron pointing toward +Y UV (user-facing side for Portal doors).
                float2 cp = float2(abs(uv.x), uv.y - 0.44);
                float legDist = abs(cp.y - (0.16 - cp.x * 0.9)) / 1.345;
                float chevronMask = step(cp.x, 0.20) * smoothstep(-0.02, 0.02, cp.y);
                float chevron = (1.0 - smoothstep(0.035 - pixel, 0.035 + pixel, legDist)) * chevronMask * saturate(_Directional);
                float chevronOutline = (1.0 - smoothstep(0.065 - pixel, 0.065 + pixel, legDist)) * chevronMask * saturate(_Directional);

                float fgShape = saturate(max(max(ring, dot), chevron));
                float bgOutlineShape = saturate(max(max(ringOutline, dotOutline), chevronOutline));

                float fillAlpha = fillMask * _FillColor.a;
                float outlineAlpha = bgOutlineShape * _OutlineColor.a;
                float fgAlpha = fgShape * _Color.a;

                // Composite fill -> dark outline -> bright foreground.
                half3 rgb = _FillColor.rgb * fillAlpha;
                float a = fillAlpha;

                rgb = _OutlineColor.rgb * outlineAlpha + rgb * (1.0 - outlineAlpha);
                a = outlineAlpha + a * (1.0 - outlineAlpha);

                rgb = _Color.rgb * fgAlpha + rgb * (1.0 - fgAlpha);
                a = fgAlpha + a * (1.0 - fgAlpha);

                float finalAlpha = a * saturate(_Alpha);
                if (finalAlpha <= 0.001)
                    discard;

                return half4(rgb / max(a, 1e-5), finalAlpha);
            }
            ENDHLSL
        }
    }
}
