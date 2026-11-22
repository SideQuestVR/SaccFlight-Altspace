// Gradient sky whose horizon (and everything below it) is exactly the city haze colour, so distant
// ground fades into it without a visible line at any altitude. Includes a simple sun disc and glow.
Shader "FlightWorld/CitySky"
{
    Properties
    {
        _ZenithColor ("Zenith", Color) = (0.20, 0.42, 0.78, 1)
        _MidColor ("Mid Sky", Color) = (0.50, 0.70, 0.92, 1)
        _HorizonColor ("Horizon / Haze", Color) = (0.80, 0.86, 0.91, 1)
        _SunDir ("Direction Towards Sun", Vector) = (-0.53, 0.77, -0.36, 0)
        _SunColor ("Sun", Color) = (1.0, 0.96, 0.88, 1)
        _SunSize ("Sun Size", Range(0.9990, 0.99999)) = 0.9996
        _SunGlow ("Sun Glow", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ZenithColor;
                half4 _MidColor;
                half4 _HorizonColor;
                float4 _SunDir;
                half4 _SunColor;
                float _SunSize;
                half _SunGlow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.direction = input.positionOS.xyz;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 dir = normalize(input.direction);
                half up = saturate(dir.y);
                half3 colour = lerp(_HorizonColor.rgb, _MidColor.rgb, saturate(pow(up, 0.55) * 1.6));
                colour = lerp(colour, _ZenithColor.rgb, saturate((up - 0.25) * 1.4));
                float towardSun = dot(dir, normalize(_SunDir.xyz));
                half disc = smoothstep(_SunSize, _SunSize + 0.00015, towardSun);
                half glow = pow(saturate(towardSun), 24.0) * _SunGlow + pow(saturate(towardSun), 400.0) * 0.5;
                colour += _SunColor.rgb * (disc * 4.0 + glow) * step(0.0, dir.y + 0.02);
                return half4(colour, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
