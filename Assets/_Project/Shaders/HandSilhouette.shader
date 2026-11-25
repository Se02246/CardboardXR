// Lightweight 3-instruction unlit vector-stencil contour shader for tracked hands:
//   - Used across 4 ordered submeshes on HandJointsView (Inner Mask, White Core, Dark Halo, Pinch Ring)
//   - Eliminates full-quad per-pixel SDF evaluation while keeping razor-sharp border-only outlines
//     at Queue 3019..3022 (strictly below palm UI at Queue 3050).
Shader "CardboardXR/HandSilhouette"
{
    Properties
    {
        _Color ("Color", Color) = (0.98, 0.99, 1.0, 0.96)
        _Opacity ("Global Opacity", Range(0.0, 1.0)) = 1.0
        _ColorMask ("Color Mask", Float) = 15
        _StencilComp ("Stencil Comp", Float) = 8
        _StencilPass ("Stencil Pass", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent+20"
        }

        Pass
        {
            Name "HandVectorContour"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off
            ColorMask [_ColorMask]

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp [_StencilComp]
                Pass [_StencilPass]
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Opacity;
                float _ColorMask;
                float _StencilComp;
                float _StencilPass;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float fade : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.fade = saturate(input.uv.x);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return half4(_Color.rgb, _Color.a * (half)(input.fade * _Opacity));
            }
            ENDHLSL
        }
    }
}
