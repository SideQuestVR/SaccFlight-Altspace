// Head-up display symbology: glowing lines drawn over everything, but only where the display's glass
// (FlightWorld/HudGlass) has marked the stencil buffer. Set Stencil Comparison to Always (8) for symbols
// that should show anywhere. Stands in for SaccFlight/HUDStencil and SaccFlight/HUD_NoStencil from
// SaccFlightAndVehicles (MIT, (c) 2021 Sacchan-VRC). Single-pass-instanced / multiview stereo safe.
Shader "FlightWorld/HudSymbol"
{
    Properties
    {
        [HDR] _Color ("Colour", Color) = (0, 1, 0, 0.6)
        _Brightness ("Brightness", Range(0, 1)) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil Comparison", Float) = 3
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+2000" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha One
            ZWrite Off
            ZTest Always

            Stencil
            {
                Ref 1
                Comp [_StencilComp]
                Pass Keep
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Brightness;
                half _StencilComp;
            CBUFFER_END

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

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return _Color * _Brightness;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
