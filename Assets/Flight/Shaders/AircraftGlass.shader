// Canopy and window glass: a tinted, see-through layer that brightens towards the sky colour at glancing
// angles. Everything is worked out per vertex. Back faces are culled, so from inside a cockpit the glass
// costs nothing: a pilot's view is not drawn twice.
// Also the vehicles' other see-through surfaces (propeller blur discs, textured canopies): those set the
// glancing sky to 0, and their opacity is the tint's times the texture's, as the original Fade materials draw.
// Single-pass-instanced / multiview stereo safe.
Shader "FlightWorld/AircraftGlass"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint and opacity", Color) = (0.55, 0.70, 0.80, 0.25)
        _SkyColor ("Reflected sky", Color) = (0.75, 0.86, 0.98, 1)
        _Glance ("Sky reflection at glancing angles", Range(0, 1)) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _FogColor ("Haze Colour", Color) = (0.80, 0.86, 0.91, 1)
        _FogParams ("Haze (density, 1/scale height, fade start, 1/fade length)", Vector) = (0.000076, 0.000833, 15000, 0.000154)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/City/Shaders/CityFog.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                half4 _SkyColor;
                half _Glance;
                half _Cull;
                half4 _FogColor;
                float4 _FogParams;
            CBUFFER_END

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
                half4 colour : TEXCOORD1;
                half haze : TEXCOORD2;
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

                half3 view = normalize(GetCameraPositionWS() - positionWS);
                half glance = 1.0 - saturate(dot(normalWS, view));
                glance = glance * glance * glance * _Glance;
                Light sun = GetMainLight();
                half3 light = SampleSH(normalWS) + sun.color * saturate(dot(normalWS, sun.direction));
                output.colour = half4(lerp(_Color.rgb * light, _SkyColor.rgb, glance), saturate(_Color.a + glance * 0.6));
                output.haze = CityFogAmount(positionWS, _FogParams);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half3 colour = lerp(input.colour.rgb * tex.rgb, _FogColor.rgb, input.haze);
                return half4(colour, input.colour.a * tex.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
