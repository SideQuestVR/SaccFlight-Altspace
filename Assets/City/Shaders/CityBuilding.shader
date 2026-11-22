// City buildings: one material for every tile.
//  - Vertex colour RGB = facade tint x baked light (authored in sRGB).
//  - Vertex colour A   = 0 for roofs / ledges (no facade texture), otherwise (style + 1) / 16.
//  - UV                = facade coordinates, one unit = one 8 x 8 window tile of the facade array.
//  - Facade alpha      = glass mask; glass picks up a cheap sky reflection.
// Unlit (all lighting is baked), fogged, single-pass-instanced / multiview stereo safe.
Shader "FlightWorld/CityBuilding"
{
    Properties
    {
        _Facades ("Facade Array", 2DArray) = "white" {}
        _SkyColor ("Glass: Sky Reflection", Color) = (0.56, 0.74, 0.95, 1)
        _HorizonColor ("Glass: Horizon Reflection", Color) = (0.86, 0.89, 0.92, 1)
        _GlassReflect ("Glass Reflectivity", Range(0, 1)) = 0.5
        _Exposure ("Brightness", Range(0.5, 2)) = 1.0
        _FogColor ("Haze Colour", Color) = (0.80, 0.86, 0.91, 1)
        _FogParams ("Haze (density, 1/scale height, fade start, 1/fade length)", Vector) = (0.000076, 0.000833, 15000, 0.000154)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "CityFog.hlsl"

        TEXTURE2D_ARRAY(_Facades);
        SAMPLER(sampler_Facades);

        CBUFFER_START(UnityPerMaterial)
            float4 _Facades_ST;
            half4 _SkyColor;
            half4 _HorizonColor;
            half _GlassReflect;
            half _Exposure;
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
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                half3 normalWS : TEXCOORD1;
                float3 viewWS : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;
                output.color = input.color;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewWS = GetWorldSpaceViewDir(positionWS);
                output.positionWS = positionWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half3 baked = SRGBToLinear(input.color.rgb);
                half code = round(input.color.a * 16.0);
                half3 colour = baked;
                if (code > 0.5)
                {
                    half4 facade = SAMPLE_TEXTURE2D_ARRAY(_Facades, sampler_Facades, input.uv, code - 1.0);
                    colour = baked * facade.rgb;
                    half3 n = normalize(input.normalWS);
                    half3 v = normalize(input.viewWS);
                    half fresnel = pow(1.0 - saturate(dot(n, v)), 4.0);
                    half3 r = reflect(-v, n);
                    half3 sky = lerp(_HorizonColor.rgb, _SkyColor.rgb, saturate(r.y * 1.5 + 0.2));
                    half light = dot(baked, half3(0.3333, 0.3333, 0.3333));
                    half amount = facade.a * _GlassReflect * (0.35 + 0.65 * fresnel);
                    colour = lerp(colour, sky * (0.35 + 0.65 * light), amount);
                }
                colour *= _Exposure;
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

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vertNormals
            #pragma fragment fragNormals
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 normalWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vertNormals(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 fragNormals(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return half4(normalize(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
