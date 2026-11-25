// Apple visionOS full-bleed borderless rounded screen shader for RawImage/Image media surfaces.
// Respects RawImage uvRect (TEXCOORD0) while computing analytical rounded corners and delicate
// optical glass specular edge rim from VisionSurfaceEffect (TEXCOORD1 & TEXCOORD2).
Shader "CardboardXR/RoundedScreen"
{
    Properties
    {
        [PerRendererData] _MainTex ("Screen Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _RimColor ("Specular Rim Color", Color) = (1, 1, 1, 1)
        _RimWidth ("Specular Rim Width (dp)", Float) = 1.45

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "RoundedScreen"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            int _UIVertexColorAlwaysGammaSpace;

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _RimColor;
                float _RimWidth;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                half4 color : COLOR;
                float4 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
                float4 uv2 : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 texUV : TEXCOORD0;
                float4 normAndSize : TEXCOORD1;
                float4 glassParams : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);

                half4 vertCol = input.color;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                    if (_UIVertexColorAlwaysGammaSpace)
                        vertCol.rgb = SRGBToLinear(vertCol.rgb);
                #endif
                output.color = vertCol * _Color;
                output.texUV = input.uv0.xy;

                bool hasData = (input.uv1.z > 0.5 && input.uv1.w > 0.5);
                output.normAndSize.xy = hasData ? input.uv1.xy : input.uv0.xy;
                output.normAndSize.zw = hasData ? input.uv1.zw : float2(100.0, 100.0);
                output.glassParams = hasData ? input.uv2 : float4(28.0, 0.55, 0.16, 0.0);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half baseA = input.color.a;
                if (baseA < 0.002h)
                    discard;

                float2 normUV = input.normAndSize.xy;
                float2 rectSize = max(input.normAndSize.zw, float2(1.0, 1.0));
                float radiusDp = input.glassParams.x;
                float rimStrength = input.glassParams.y;
                float sheenStrength = input.glassParams.z;

                float2 halfSize = rectSize * 0.5;
                float r = clamp(radiusDp, 0.0, min(halfSize.x, halfSize.y));
                float2 p = (normUV - 0.5) * rectSize;
                float2 q = abs(p) - (halfSize - r);
                float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;

                float aa = clamp(fwidth(sd), 0.25, 2.2);
                float shape = 1.0 - smoothstep(-aa * 0.6, aa * 0.6, sd);
                if (shape < 0.002)
                    discard;

                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.texUV) * input.color;
                half outA = tex.a * (half)shape;
                if (outA < 0.002h)
                    discard;

                half3 outRgb = tex.rgb;

                // Subtle upper glass reflection sheen (kept very delicate so media stays crystal clear).
                if (sheenStrength > 0.001)
                {
                    float upperSheen = smoothstep(0.68, 1.0, normUV.y) * (0.45 + 0.55 * (1.0 - normUV.x));
                    outRgb = saturate(outRgb + _RimColor.rgb * (half)(upperSheen * sheenStrength * 0.045));
                }

                // Hairline optical glass bezel rim tracing the outer rounded perimeter.
                if (rimStrength > 0.001)
                {
                    float2 sgn = float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
                    float2 n2d = (q.x > 0.0 || q.y > 0.0)
                        ? normalize(max(q, 0.0) * sgn + float2(1e-6, 1e-6))
                        : (q.x > q.y ? float2(sgn.x, 0.0) : float2(0.0, sgn.y));

                    float rimW = max(_RimWidth, 1.0);
                    float rimMask = smoothstep(-rimW - aa * 0.5, -rimW + aa * 0.5, sd);
                    float specTL = lerp(0.28, 1.0, saturate(dot(n2d, float2(-0.45, 0.89)) * 0.5 + 0.5));
                    float specBR = 0.30 * saturate(dot(n2d, float2(0.42, -0.91)));
                    // Modulate by sampled alpha so transparent-margin icons do not draw stray outer halos.
                    float rimVis = saturate((float)tex.a * 4.0);
                    half rimBlend = (half)(rimMask * rimStrength * 0.52 * max(specTL, specBR) * rimVis * saturate((float)baseA * 2.4));
                    outRgb = lerp(outRgb, _RimColor.rgb, rimBlend);
                }

                return half4(outRgb, outA);
            }
            ENDHLSL
        }
    }
}
