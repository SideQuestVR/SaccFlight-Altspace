// Shared haze for the city shaders.
//
// Plain distance fog looks wrong from an aircraft: it whites out the ground directly below once you
// climb, and it draws a hard line where the far clip plane meets the sky. This models a haze layer
// that thins with height instead - thick along the ground, nearly clear looking down from altitude -
// and always reaches full haze before the far clip plane, so the edge of the world is never visible.
// The sky shader uses the same colour at and below its horizon.
//
// _FogColor   rgb = haze colour
// _FogParams  x = haze density at sea level (per metre), y = 1 / haze scale height (per metre),
//             z = distance where the forced fade-out starts, w = 1 / length of that fade
#ifndef FLIGHTWORLD_CITY_FOG_INCLUDED
#define FLIGHTWORLD_CITY_FOG_INCLUDED

half CityFogAmount(float3 positionWS, float4 fogParams)
{
    float3 cam = GetCameraPositionWS();
    float dist = distance(positionWS, cam);
    float h0 = max(cam.y, 0.0);
    float h1 = max(positionWS.y, 0.0);
    float e0 = exp(-h0 * fogParams.y);
    float e1 = exp(-h1 * fogParams.y);
    float dh = (h1 - h0) * fogParams.y;
    // mean haze density along the view ray through an exponentially thinning layer
    float mean = abs(dh) > 0.001 ? (e0 - e1) / dh : e0;
    half haze = 1.0 - exp(-fogParams.x * dist * mean);
    half edge = saturate((dist - fogParams.z) * fogParams.w);
    return max(haze, edge * edge * (3.0 - 2.0 * edge));
}

half3 ApplyCityFog(half3 colour, float3 positionWS, half3 fogColour, float4 fogParams)
{
    return lerp(colour, fogColour, CityFogAmount(positionWS, fogParams));
}

#endif
