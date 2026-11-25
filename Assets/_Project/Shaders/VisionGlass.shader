// Apple visionOS frosted smoked-glass UI surface shader (analytical SDF, zero 9-slice seams).
// Expects VisionSurfaceEffect on the Graphic to pack:
//   TEXCOORD1 = (normU, normV, widthDp, heightDp)
//   TEXCOORD2 = (radiusDp, rimStrength, sheenStrength, styleMode)
// styleMode: 0 = Frosted, 1 = Recessed, 2 = Glossy, 3 = Pure, 4 = OutlineRing, 5 = CornerArc
Shader "CardboardXR/VisionGlass"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _RimColor ("Specular Rim Color", Color) = (1, 1, 1, 1)
        _RimWidth ("Specular Rim Width (dp)", Float) = 1.55

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
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "VisionGlass"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

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
                float4 normAndSize : TEXCOORD0;
                float4 glassParams : TEXCOORD1;
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

                bool hasData = (input.uv1.z > 0.5 && input.uv1.w > 0.5);
                output.normAndSize.xy = hasData ? input.uv1.xy : input.uv0.xy;
                output.normAndSize.zw = hasData ? input.uv1.zw : float2(100.0, 100.0);
                output.glassParams = hasData ? input.uv2 : float4(18.0, 0.85, 0.85, 0.0);
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
                int mode = (int)round(input.glassParams.w);

                float2 p = (normUV - 0.5) * rectSize;

                // Mode 5: Apple Vision Pro curved bottom-right corner resize handle arc (handled before rounded-box clip).
                if (mode == 5)
                {
                    float2 c = float2(-rectSize.x * 0.44, rectSize.y * 0.44);
                    float arcR = max(radiusDp, 12.0);
                    float2 d = p - c;
                    float ang = atan2(d.y, d.x);
                    float clampedAng = clamp(ang, -1.36, -0.21);
                    float2 closest = float2(cos(clampedAng), sin(clampedAng)) * arcR;
                    float halfStroke = max(rimStrength, 2.2);
                    float sdArc = length(d - closest) - halfStroke;
                    float aaArc = clamp(fwidth(sdArc), 0.25, 2.0);
                    float arcMask = 1.0 - smoothstep(-aaArc * 0.6, aaArc * 0.6, sdArc);
                    if (arcMask < 0.002)
                        discard;
                    float sheen = smoothstep(-halfStroke, halfStroke * 0.6, length(d) - arcR) * 0.16 * sheenStrength;
                    half3 arcRgb = saturate(input.color.rgb + _RimColor.rgb * (half)sheen);
                    return half4(arcRgb, baseA * (half)arcMask);
                }

                float2 halfSize = rectSize * 0.5;
                float r = clamp(radiusDp, 0.0, min(halfSize.x, halfSize.y));
                float2 q = abs(p) - (halfSize - r);
                float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;

                float aa = clamp(fwidth(sd), 0.25, 2.2);
                float shape = 1.0 - smoothstep(-aa * 0.6, aa * 0.6, sd);
                if (shape < 0.002)
                    discard;

                half3 baseRgb = input.color.rgb;

                // Mode 3: Pure anti-aliased pill/circle (state layers, active slider track, dots, dividers).
                if (mode == 3)
                    return half4(baseRgb, baseA * (half)shape);

                // Mode 4: Crisp hollow SDF outline ring (e.g., Outlined buttons, Swatch/Reorder selection rings).
                if (mode == 4)
                {
                    float ringW = max(rimStrength, 1.4);
                    float ring = smoothstep(-ringW - aa * 0.5, -ringW + aa * 0.5, sd) * shape;
                    return half4(baseRgb, baseA * (half)ring);
                }

                // Exact analytical outward 2D normal on the rounded rectangle perimeter.
                float2 sgn = float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
                float2 n2d = (q.x > 0.0 || q.y > 0.0)
                    ? normalize(max(q, 0.0) * sgn + float2(1e-6, 1e-6))
                    : (q.x > q.y ? float2(sgn.x, 0.0) : float2(0.0, sgn.y));

                half3 outRgb = baseRgb;
                half outA = baseA;

                if (mode == 1)
                {
                    // Mode 1: Recessed glass cavity (search/address pills, settings group boxes, slider tracks).
                    float topShadow = smoothstep(0.55, 1.0, normUV.y) * 0.08 * sheenStrength;
                    outRgb = baseRgb * (half)(1.0 - topShadow) + _RimColor.rgb * (half)(0.015 * normUV.y * sheenStrength);

                    float rimW = max(_RimWidth * 0.85, 1.0);
                    float rimMask = smoothstep(-rimW - aa * 0.5, -rimW + aa * 0.5, sd);
                    float catchLight = lerp(0.18, 0.72, saturate(dot(n2d, float2(0.32, -0.94)) * 0.5 + 0.5));
                    half rimBlend = (half)(rimMask * rimStrength * 0.28 * catchLight * saturate((float)baseA * 2.5));
                    outRgb = lerp(outRgb, _RimColor.rgb, rimBlend);
                    outA = saturate(outA + rimBlend * 0.35h);
                }
                else if (mode == 2)
                {
                    // Mode 2: Glossy luminous glass / app medallion.
                    float vertSheen = lerp(-0.04, 0.10, normUV.y);
                    float crescent = smoothstep(0.42, 0.98, normUV.y) * (1.0 - abs(normUV.x - 0.5) * 0.75) * 0.14;
                    float radialSoft = saturate(1.0 - length(normUV - 0.5) * 0.75) * 0.04;
                    outRgb = saturate(baseRgb * (half)(1.0 + vertSheen * sheenStrength) +
                        _RimColor.rgb * (half)((crescent + radialSoft) * sheenStrength));

                    float rimW = max(_RimWidth * 1.12, 1.35);
                    float rimMask = smoothstep(-rimW - aa * 0.5, -rimW + aa * 0.5, sd);
                    float light = lerp(0.28, 1.0, saturate(dot(n2d, float2(-0.42, 0.91)) * 0.5 + 0.5));
                    float bottomKiss = 0.24 * saturate(dot(n2d, float2(0.35, -0.94)));
                    half rimBlend = (half)(rimMask * rimStrength * 0.56 * max(light, bottomKiss) * saturate((float)baseA * 2.4));
                    outRgb = lerp(outRgb, _RimColor.rgb, rimBlend);
                    outA = saturate(outA + rimBlend * 0.48h);
                }
                else
                {
                    // Mode 0: Signature visionOS translucent smoked frosted glass with specular hairline edge.
                    float diag = saturate(normUV.y * 0.65 + (1.0 - normUV.x) * 0.35);
                    float topWash = smoothstep(0.35, 1.0, normUV.y);
                    float edgeFrost = smoothstep(-14.0, 0.0, sd);
                    half sheenAmt = (half)((0.028 * diag + 0.036 * topWash + 0.022 * edgeFrost) * sheenStrength * saturate((float)baseA * 2.2));
                    outRgb = saturate(baseRgb + _RimColor.rgb * sheenAmt);

                    float rimW = max(_RimWidth, 1.1);
                    float rimMask = smoothstep(-rimW - aa * 0.5, -rimW + aa * 0.5, sd);
                    float specTL = lerp(0.24, 1.0, saturate(dot(n2d, float2(-0.45, 0.89)) * 0.5 + 0.5));
                    float specBR = 0.26 * saturate(dot(n2d, float2(0.42, -0.91)));
                    half rimBlend = (half)(rimMask * rimStrength * 0.52 * max(specTL, specBR) * saturate((float)baseA * 2.4));
                    outRgb = lerp(outRgb, _RimColor.rgb, rimBlend);
                    outA = saturate(outA + rimBlend * 0.55h);
                }

                return half4(outRgb, outA * (half)shape);
            }
            ENDHLSL
        }
    }
}
