// Sea: colour from a low-resolution tint map addressed by world position (turquoise shallows and
// lagoons, deep blue offshore), cheap animated ripples, sky reflection and a sun glint.
// No depth or opaque texture reads, so it stays cheap on Quest. Stereo safe.
Shader "FlightWorld/CitySea"
{
    Properties
    {
        _TintMap ("Water Tint Map", 2D) = "white" {}
        _WorldRect ("World Rect (x0, z0, 1/width, 1/depth)", Vector) = (-22400, -16000, 0.000025, 0.00003125)
        _DeepColor ("Deep Water", Color) = (0.03, 0.17, 0.32, 1)
        _SkyColor ("Sky Reflection", Color) = (0.50, 0.70, 0.95, 1)
        _HorizonColor ("Horizon Reflection", Color) = (0.82, 0.88, 0.93, 1)
        _SunDir ("Direction Towards Sun", Vector) = (-0.53, 0.77, -0.36, 0)
        _SunColor ("Sun Glint", Color) = (1.0, 0.95, 0.85, 1)
        _Gloss ("Glint Sharpness", Range(8, 512)) = 180
        _WaveScale ("Ripple Scale", Float) = 0.06
        _WaveSpeed ("Ripple Speed", Float) = 0.9
        _WaveHeight ("Ripple Strength", Range(0, 1)) = 0.22
        _WaveFade ("Ripple Fade Distance", Float) = 2500
        _FogColor ("Haze Colour", Color) = (0.80, 0.86, 0.91, 1)
        _FogParams ("Haze (density, 1/scale height, fade start, 1/fade length)", Vector) = (0.000076, 0.000833, 15000, 0.000154)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "CityFog.hlsl"

        TEXTURE2D(_TintMap);
        SAMPLER(sampler_TintMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _TintMap_ST;
            float4 _WorldRect;
            half4 _DeepColor;
            half4 _SkyColor;
            half4 _HorizonColor;
            float4 _SunDir;
            half4 _SunColor;
            half _Gloss;
            float _WaveScale;
            float _WaveSpeed;
            half _WaveHeight;
            float _WaveFade;
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 mapUV = (input.positionWS.xz - _WorldRect.xy) * _WorldRect.zw;
                half3 tint = SAMPLE_TEXTURE2D(_TintMap, sampler_TintMap, saturate(mapUV)).rgb;
                float2 outside = max(abs(mapUV - 0.5) - 0.5, 0.0) / _WorldRect.zw;      // metres beyond the map
                tint = lerp(tint, _DeepColor.rgb, saturate(length(outside) / 4000.0));

                float3 toEye = GetWorldSpaceViewDir(input.positionWS);
                float dist = length(toEye);
                half3 v = toEye / max(dist, 0.001);

                float t = _Time.y * _WaveSpeed;
                float2 p = input.positionWS.xz * _WaveScale;
                half2 slope;
                slope.x = cos(p.x + t) * 0.5 + cos(p.x * 2.3 - p.y * 1.7 + t * 1.3) * 0.3 + cos(p.y * 3.1 + p.x * 0.7 - t * 0.8) * 0.2;
                slope.y = cos(p.y * 1.1 - t * 0.9) * 0.5 + cos(p.y * 2.7 + p.x * 1.3 + t * 1.1) * 0.3 + cos(p.x * 3.3 - p.y * 0.6 + t * 0.7) * 0.2;
                half fade = saturate(1.0 - dist / _WaveFade);
                half3 n = normalize(half3(-slope.x * _WaveHeight * fade, 1.0, -slope.y * _WaveHeight * fade));

                half fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(n, v)), 5.0);
                half3 r = reflect(-v, n);
                half3 sky = lerp(_HorizonColor.rgb, _SkyColor.rgb, saturate(r.y * 1.4));
                half3 colour = lerp(tint, sky, fresnel * 0.8);
                half glint = pow(saturate(dot(r, normalize(_SunDir.xyz))), _Gloss);
                colour += _SunColor.rgb * glint * (0.25 + 0.75 * fade);

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
