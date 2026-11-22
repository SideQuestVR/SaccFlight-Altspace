// Ground tiles and the outer desert: texture x colour, fogged, no lighting (shadows are painted
// into the ground textures). Close to the viewer a tiling grain is multiplied over it at two sizes, so
// sand and tarmac have a surface instead of being flat colour; it fades out with distance, where the
// map texture's own detail takes over. Single-pass-instanced / multiview stereo safe.
Shader "FlightWorld/CityUnlit"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        _DetailMap ("Grain (grey, 0.5 = no change)", 2D) = "gray" {}
        _DetailParams ("Grain (fine tile m, broad tile m, strength, fade distance m)", Vector) = (7, 53, 0.5, 700)
        _FogColor ("Haze Colour", Color) = (0.80, 0.86, 0.91, 1)
        _FogParams ("Haze (density, 1/scale height, fade start, 1/fade length)", Vector) = (0.000076, 0.000833, 15000, 0.000154)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "CityFog.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_DetailMap);
        SAMPLER(sampler_DetailMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            float4 _DetailParams;
            half4 _FogColor;
            float4 _FogParams;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
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
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half3 colour = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;

                // grain: a fine layer near the viewer and a broader, turned one (so the two never line up) further out
                float2 ground = input.positionWS.xz;
                float2 turned = float2(dot(ground, float2(0.8, 0.6)), dot(ground, float2(-0.6, 0.8)));
                half fine = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, ground / _DetailParams.x).r - 0.5;
                half broad = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, turned / _DetailParams.y).r - 0.5;
                half away = distance(input.positionWS, GetCameraPositionWS());
                half near = saturate(1.0 - away / _DetailParams.w);
                half mid = saturate(1.0 - away / (_DetailParams.w * 4.0));
                colour *= 1.0 + (fine * near * near + broad * 0.7 * mid) * 2.0 * _DetailParams.z;
                colour = ApplyCityFog(colour, input.positionWS, _FogColor.rgb, _FogParams);
                return half4(colour, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vertDepth(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 fragDepth(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return 0;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
