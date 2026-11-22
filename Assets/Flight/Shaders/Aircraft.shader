// Vehicle paint: texture x tint, lit per vertex by the sun and the sky, with the city's haze.
//
// From the pilot's seat the cockpit fills half the view, so the cost of this shader is paid on a large
// share of the screen every frame. Measured on a Quest 3, the standard lit shader added about 2 ms of
// GPU time while seated; here lighting and haze are worked out per vertex and each pixel is one texture
// read. _EmissionColor adds self-lit parts (screens, lamps), tinted by the texture.
// Single-pass-instanced / multiview stereo safe.
Shader "FlightWorld/Aircraft"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 1)
        _Cutoff ("Alpha cutoff (0 = off)", Range(0, 1)) = 0
        _Unlit ("Self-lit: ignores sun and sky (screens, lamps)", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _FogColor ("Haze Colour", Color) = (0.80, 0.86, 0.91, 1)
        _FogParams ("Haze (density, 1/scale height, fade start, 1/fade length)", Vector) = (0.000076, 0.000833, 15000, 0.000154)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Assets/City/Shaders/CityFog.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            half4 _Color;
            half4 _EmissionColor;
            half _Cutoff;
            half _Unlit;
            half _Cull;
            half4 _FogColor;
            float4 _FogParams;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 lightAndHaze : TEXCOORD1;      // rgb = light reaching the surface, a = haze amount
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                half3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);

                Light sun = GetMainLight();
                half3 light = SampleSH(normalWS) + sun.color * saturate(dot(normalWS, sun.direction));
                output.lightAndHaze = half4(lerp(light, half3(1.0, 1.0, 1.0), _Unlit), CityFogAmount(positionWS, _FogParams));
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                clip(tex.a * _Color.a - _Cutoff);
                half3 colour = tex.rgb * (_Color.rgb * input.lightAndHaze.rgb + _EmissionColor.rgb);
                return half4(lerp(colour, _FogColor.rgb, input.lightAndHaze.a), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vertDepth(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            half4 fragDepth(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a * _Color.a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
