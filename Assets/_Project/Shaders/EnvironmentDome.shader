// 360-degree equirectangular panorama sky-sphere for CardboardXR's Isolation / Environments mode,
// plus a sleek dark cinema gradient mode when _UseProcedural > 0.5. Supports Apple Vision Pro-style
// organic spherical mist transitions between Camera (Passthrough) <-> Environment and smooth
// sweeping crossfades between Environment A -> Environment B.
Shader "CardboardXR/EnvironmentDome"
{
    Properties
    {
        [MainTexture] _BaseMap ("Equirectangular Panorama", 2D) = "black" {}
        _FromMap ("Previous Equirectangular Panorama", 2D) = "black" {}
        _UseProcedural ("Use Dark Cinema Gradient", Float) = 0.0
        _FromProcedural ("Use Previous Dark Cinema Gradient", Float) = 0.0
        _Exposure ("Exposure", Float) = 1.0
        _Immersion ("Immersion Reveal", Range(0.0, 1.0)) = 1.0
        _RevealDirection ("Reveal Direction", Float) = 1.0
        _RevealOriginDir ("Reveal Origin Direction", Vector) = (0, 0, 1, 0)
        _Crossfade ("Environment Crossfade", Range(0.0, 1.0)) = 1.0
        _CrossfadeOriginDir ("Crossfade Origin Direction", Vector) = (0, 0, 1, 0)
        _TransitionGlow ("Transition Edge Glow", Range(0.0, 1.0)) = 0.42
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Background+1"
        }

        Pass
        {
            Name "EnvironmentDome"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Front
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_FromMap);
            SAMPLER(sampler_FromMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _FromMap_ST;
                float _UseProcedural;
                float _FromProcedural;
                float _Exposure;
                float _Immersion;
                float _RevealDirection;
                float4 _RevealOriginDir;
                float _Crossfade;
                float4 _CrossfadeOriginDir;
                float _TransitionGlow;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dirOS : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.dirOS = input.positionOS;
                return output;
            }

            float3 SafeDir(float3 v)
            {
                float lenSq = dot(v, v);
                return lenSq > 1e-6 ? v * rsqrt(lenSq) : float3(0.0, 0.0, 1.0);
            }

            half3 SampleProceduralCinema(float3 dir)
            {
                // Sleek dark minimalist architectural cinema dome (deep midnight indigo + subtle horizon band).
                half up = saturate(dir.y * 0.5 + 0.5);
                half horizonGlow = exp(-abs(dir.y) * 9.0) * 0.055h;
                half3 floorCol = half3(0.015h, 0.018h, 0.028h);
                half3 zenithCol = half3(0.022h, 0.030h, 0.055h);
                return lerp(floorCol, zenithCol, up) + half3(0.04h, 0.08h, 0.16h) * horizonGlow;
            }

            half3 SampleTargetEnv(float3 dir, float2 uv)
            {
                if (_UseProcedural > 0.5)
                    return SampleProceduralCinema(dir);
                return SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, uv, 0).rgb * (half)_Exposure;
            }

            half3 SampleFromEnv(float3 dir, float2 uv)
            {
                if (_FromProcedural > 0.5)
                    return SampleProceduralCinema(dir);
                return SAMPLE_TEXTURE2D_LOD(_FromMap, sampler_FromMap, uv, 0).rgb * (half)_Exposure;
            }

            float OrganicMist(float3 d)
            {
                float n1 = sin(d.x * 5.3 + d.y * 4.1) * cos(d.z * 4.7 - d.y * 3.3);
                float n2 = sin(d.z * 11.1 + d.x * 8.9 - d.y * 6.7) * 0.5;
                float n3 = cos(d.y * 17.3 - d.x * 13.7 + d.z * 9.3) * 0.25;
                return (n1 + n2 + n3) * (1.0 / 1.75);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                if (_Immersion <= 0.0005)
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);

                float3 dir = normalize(input.dirOS);

                // Equirectangular spherical projection (seamless 360 horizontal wrap, LOD 0 avoids atan2 seam).
                float u = atan2(dir.x, dir.z) / (2.0 * PI) + 0.5;
                float v = asin(clamp(dir.y, -1.0, 1.0)) / PI + 0.5;
                float2 uv = float2(u, v);

                half3 toCol = SampleTargetEnv(dir, uv);
                half3 envCol = toCol;

                if (_Crossfade < 0.9995)
                {
                    half3 fromCol = SampleFromEnv(dir, uv);
                    float3 originCf = SafeDir(_CrossfadeOriginDir.xyz);
                    float angCf = 0.5 - 0.5 * dot(dir, originCf);
                    float mistCf = OrganicMist(dir) * 0.08;
                    const float cfFeather = 0.34;
                    float frontCf = lerp(-cfFeather - 0.09, 1.0 + cfFeather + 0.09, _Crossfade);
                    float sweep = 1.0 - smoothstep(frontCf - cfFeather, frontCf + cfFeather, angCf + mistCf);
                    float mixT = lerp(_Crossfade, sweep, 0.72);
                    float cfRim = sweep * (1.0 - sweep) * 4.0 * sin(saturate(_Crossfade) * PI);
                    envCol = lerp(fromCol, toCol, (half)mixT) + half3(0.85h, 0.92h, 1.0h) * (half)(cfRim * _TransitionGlow * 0.16);
                }

                if (_Immersion >= 0.9995)
                    return half4(envCol, 1.0h);

                float3 originRev = SafeDir(_RevealOriginDir.xyz);
                float angRev = 0.5 - 0.5 * dot(dir, originRev);
                float mistRev = OrganicMist(dir) * 0.085;
                const float feather = 0.24;

                float alpha;
                if (_RevealDirection >= 0.0)
                {
                    float front = lerp(-feather - 0.10, 1.0 + feather + 0.10, _Immersion);
                    alpha = 1.0 - smoothstep(front - feather, front + feather, angRev + mistRev);
                }
                else
                {
                    float exitT = 1.0 - _Immersion;
                    float front = lerp(-feather - 0.10, 1.0 + feather + 0.10, exitT);
                    float clearHole = 1.0 - smoothstep(front - feather, front + feather, angRev + mistRev);
                    alpha = 1.0 - clearHole;
                }

                float rim = alpha * (1.0 - alpha) * 4.0 * sin(saturate(_Immersion) * PI);
                half3 glowCol = half3(0.82h, 0.91h, 1.00h);
                envCol = lerp(envCol, max(envCol, glowCol), (half)(rim * _TransitionGlow * 0.42));
                envCol += glowCol * (half)(rim * _TransitionGlow * 0.20);

                return half4(envCol, (half)saturate(alpha));
            }
            ENDHLSL
        }
    }
}
