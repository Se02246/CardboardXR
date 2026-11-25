// The passthrough screen: the camera image on a quad in front of the AR camera. It's the background,
// so it draws first, ignores depth and writes none, and everything virtual draws over it wherever
// it is. Outside the camera's exact 1:1 field of view (when _PadScale > 1), it smoothly feathers
// into a true multi-pass separable Gaussian-blurred ambient map (_AmbientMap) with radial diffusion
// and gentle peripheral dimming — zero streaking, zero ghosting, preserving 1:1 sharpness inside.
Shader "CardboardXR/Passthrough"
{
    Properties
    {
        [MainTexture] _BaseMap ("Camera Image", 2D) = "black" {}
        [HideInInspector] _MainTex ("Blit Source", 2D) = "black" {}
        _MidBlurMap ("Medium Blurred", 2D) = "black" {}
        _AmbientMap ("Ambient Blurred", 2D) = "black" {}
        _PadScale ("Ambient Pad Scale", Float) = 1.0
        _HasAmbient ("Has Ambient Map", Float) = 0.0
        _VignetteStrength ("Peripheral Vignette Strength", Range(0.0, 1.0)) = 0.85
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Background"
        }

        // Pass 0: Main passthrough quad (sharp 1:1 center + true Gaussian Ambilight periphery).
        Pass
        {
            Name "Passthrough"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MidBlurMap);
            SAMPLER(sampler_MidBlurMap);
            TEXTURE2D(_AmbientMap);
            SAMPLER(sampler_AmbientMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _PadScale;
                float _HasAmbient;
                float _VignetteStrength;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            half3 SampleSharp(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, clamp(uv, 0.002, 0.998)).rgb;
            }

            half3 SampleMid(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_MidBlurMap, sampler_MidBlurMap, clamp(uv, 0.01, 0.99)).rgb;
            }

            half3 SampleAmbient(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_AmbientMap, sampler_AmbientMap, clamp(uv, 0.02, 0.98)).rgb;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float pad = max(1.0, _PadScale);
                float2 innerUv = (input.uv - 0.5) * pad + 0.5;

                // Crisp 1:1 center sample.
                half3 sharp = SampleSharp(innerUv);
                if (pad <= 1.001 || _HasAmbient < 0.5)
                    return half4(sharp, 1.0);

                // Rounded-rectangle signed distance around the exact [0..1] 1:1 camera frame.
                // Generous corner radius + 3-stage progressive optical defocus (sharp -> soft bokeh
                // -> deep ambient blur) eliminates any visible border seam.
                const float cornerRadius = 0.085;
                float2 boxHalf = float2(0.5 - cornerRadius, 0.5 - cornerRadius);
                float2 q = abs(innerUv - 0.5) - boxHalf;
                float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - cornerRadius;

                // Stage 1 -> Stage 2: razor-sharp 1:1 center dissolves gently into soft bokeh blur
                // starting slightly inside the frame edge.
                float midBlend = smoothstep(-0.085, 0.015, sd);
                if (midBlend <= 0.001)
                    return half4(sharp, 1.0);

                // Stage 2 -> Stage 3: soft bokeh dissolves smoothly into deep velvety Ambilight.
                float deepBlend = smoothstep(-0.025, 0.145, sd);

                float outDist = max(0.0, sd);
                float2 edgeUv = clamp(innerUv, 0.02, 0.98);
                half3 midColor = SampleMid(edgeUv);

                // Gently diffuse outward pixels inward toward the room's blurred field so
                // border colors spread smoothly instead of stretching in horizontal/vertical stripes.
                float pull = saturate(outDist * 0.55);
                float2 ambientUv = lerp(edgeUv, float2(0.5, 0.5), pull);
                float r = 0.035 + min(outDist, 0.95) * 0.14;

                half3 ambient = SampleAmbient(ambientUv) * 0.36;
                ambient += SampleAmbient(ambientUv + float2( r,  r * 0.5)) * 0.16;
                ambient += SampleAmbient(ambientUv + float2(-r, -r * 0.5)) * 0.16;
                ambient += SampleAmbient(ambientUv + float2(-r * 0.5,  r)) * 0.16;
                ambient += SampleAmbient(ambientUv + float2( r * 0.5, -r)) * 0.16;

                // Peripheral vignette over the blurred transition & outer border (strong on Main 6DoF, subtle on Ultrawide).
                float vigEdge = smoothstep(-0.04, 0.42, sd);
                float radial = smoothstep(0.50, 1.02, length((input.uv - 0.5) * 2.0)) * smoothstep(-0.085, 0.05, sd);
                float vigAmount = saturate(max(vigEdge, radial * 0.80));
                half vignette = lerp(1.0h, (half)(1.0 - saturate(_VignetteStrength)), (half)(vigAmount * vigAmount));

                half dim = lerp(1.0h, 0.54h, smoothstep(0.0, 0.95, outDist));
                half3 blurred = lerp(midColor, ambient, deepBlend) * dim;
                half3 color = lerp(sharp, blurred, midBlend) * vignette;
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // Pass 1: Horizontal 9-tap separable Gaussian blur (explicitly invoked via CommandBuffer.Blit).
        Pass
        {
            Name "BlurHorizontal"
            Tags { "LightMode" = "CardboardBlur" }

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex VertBlur
            #pragma fragment FragBlurH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _BlurStep;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings VertBlur(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            half3 Tap(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, clamp(uv, 0.005, 0.995)).rgb;
            }

            half4 FragBlurH(Varyings input) : SV_Target
            {
                float2 delta = float2(_BlurStep, 0.0);
                half3 c = Tap(input.uv) * 0.227027h;
                c += (Tap(input.uv + delta * 1.0) + Tap(input.uv - delta * 1.0)) * 0.1945946h;
                c += (Tap(input.uv + delta * 2.0) + Tap(input.uv - delta * 2.0)) * 0.1216216h;
                c += (Tap(input.uv + delta * 3.0) + Tap(input.uv - delta * 3.0)) * 0.054054h;
                c += (Tap(input.uv + delta * 4.0) + Tap(input.uv - delta * 4.0)) * 0.016216h;
                return half4(c, 1.0h);
            }
            ENDHLSL
        }

        // Pass 2: Vertical 9-tap separable Gaussian blur (explicitly invoked via CommandBuffer.Blit).
        Pass
        {
            Name "BlurVertical"
            Tags { "LightMode" = "CardboardBlur" }

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex VertBlur
            #pragma fragment FragBlurV

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _BlurStep;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings VertBlur(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            half3 Tap(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, clamp(uv, 0.005, 0.995)).rgb;
            }

            half4 FragBlurV(Varyings input) : SV_Target
            {
                float2 delta = float2(0.0, _BlurStep);
                half3 c = Tap(input.uv) * 0.227027h;
                c += (Tap(input.uv + delta * 1.0) + Tap(input.uv - delta * 1.0)) * 0.1945946h;
                c += (Tap(input.uv + delta * 2.0) + Tap(input.uv - delta * 2.0)) * 0.1216216h;
                c += (Tap(input.uv + delta * 3.0) + Tap(input.uv - delta * 3.0)) * 0.054054h;
                c += (Tap(input.uv + delta * 4.0) + Tap(input.uv - delta * 4.0)) * 0.016216h;
                return half4(c, 1.0h);
            }
            ENDHLSL
        }
    }
}
