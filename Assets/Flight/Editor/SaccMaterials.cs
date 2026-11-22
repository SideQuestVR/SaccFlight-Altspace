using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>
    /// The Sacc vehicles' materials were authored for VRChat's built-in render pipeline (Standard, the old
    /// particle shaders, a few custom ones). None of those draw here, so each material is rebuilt on one of
    /// this world's lightweight shaders, keeping its texture, tint, transparency and glow. The rebuilt
    /// materials are kept as assets under Assets/Flight/SaccMaterials and reused between builds.
    ///
    /// The stand-in shaders keep the original property names (_Color, _MainTex), because the vehicles'
    /// animations drive material._Color (engine flames, lamps).
    /// </summary>
    public static class SaccMaterials
    {
        const string Dir = "Assets/Flight/SaccMaterials";

        /// <summary>Shaders met that have no stand-in yet (their renderers are switched off by the builder).</summary>
        public static readonly SortedSet<string> Unconverted = new SortedSet<string>();

        public static bool Usable(Material material) =>
            material != null && material.shader != null &&
            (material.shader.name.StartsWith("FlightWorld/") || material.shader.name.StartsWith("TextMeshPro/"));

        public static Material Convert(Material source, bool forParticles)
        {
            if (source == null) return null;
            string shader = source.shader != null ? source.shader.name : "";
            if (shader.StartsWith("FlightWorld/") || shader.StartsWith("TextMeshPro/")) return source;

            string hud = shader == "SaccFlight/HUDStencil" || shader == "SaccFlight/HUD_NoStencil" ? "FlightWorld/HudSymbol"
                : shader == "SaccFlight/HUDScreen" ? "FlightWorld/HudGlass"
                : shader == "SaccFlight/HUDText" ? "FlightWorld/HudText" : null;
            bool particle = hud == null && (forParticles || shader.Contains("Particle"));
            bool selfLit = shader == "SaccFlight/MFD" || shader == "SaccFlight/AGMScreen" || shader == "Unlit/Color" || shader == "Unlit/Texture";
            bool lit = shader == "Standard" || shader == "Autodesk Interactive" || shader == "Universal Render Pipeline/Lit" ||
                       shader.StartsWith("SaccFlight/PBRVCol") || shader == "Standard (Specular setup)";
            if (hud == null && !particle && !selfLit && !lit)
            {
                Unconverted.Add(shader);
                return source;
            }

            Directory.CreateDirectory(Dir);
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            string file = string.Concat(source.name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
            string path = $"{Dir}/{file}_{(guid.Length >= 6 ? guid.Substring(0, 6) : "local")}{(particle ? "_p" : "")}.mat";

            Texture texture = Texture(source, "_MainTex") ?? Texture(source, "_BaseMap");
            // A face that is drawn only by its glow (instrument dials: black, with the markings in the emission
            // map): the glow map becomes the picture, shown self-lit.
            Texture glowMap = Texture(source, "_EmissionMap");
            bool glowOnly = lit && texture == null && glowMap != null && source.IsKeywordEnabled("_EMISSION");
            if (glowOnly) texture = glowMap;
            Color colour = source.HasProperty("_Color") ? source.GetColor("_Color")
                : source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor")
                : source.HasProperty("_TintColor") ? source.GetColor("_TintColor") * 2f : Color.white;
            float mode = source.HasProperty("_Mode") ? source.GetFloat("_Mode") : source.HasProperty("_Surface") ? source.GetFloat("_Surface") * 3f : 0f;
            string property = source.HasProperty("_MainTex") ? "_MainTex" : "_BaseMap";
            // Windscreens that come embedded in a model file arrive as plain opaque colour (the original draws
            // them as dark panes): here they are made glass you can see through.
            bool namedGlass = lit && mode < 2f && texture == null && source.name.ToLowerInvariant().Contains("glass");
            if (namedGlass) colour.a = 0.3f;

            Shader target;
            if (hud != null) target = Shader.Find(hud);
            else if (particle) target = Shader.Find("FlightWorld/VehicleParticle");
            else if (lit && (mode >= 2f || namedGlass)) target = Shader.Find("FlightWorld/AircraftGlass");
            else target = Shader.Find("FlightWorld/Aircraft");
            if (target == null) throw new System.Exception($"stand-in shader for '{shader}' not found (did it fail to compile?)");

            var result = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (result == null)
            {
                result = new Material(target);
                AssetDatabase.CreateAsset(result, path);
            }
            result.shader = target;
            if (result.HasProperty("_MainTex"))
            {
                result.SetTexture("_MainTex", texture);
                if (texture != null && source.HasProperty(property))
                {
                    result.SetTextureScale("_MainTex", source.GetTextureScale(property));
                    result.SetTextureOffset("_MainTex", source.GetTextureOffset(property));
                }
            }

            if (hud != null)
            {
                result.SetColor("_Color", colour);
                if (result.HasProperty("_Brightness")) result.SetFloat("_Brightness", source.HasProperty("_Brightness") ? source.GetFloat("_Brightness") : 1f);
                if (result.HasProperty("_StencilComp"))
                    result.SetFloat("_StencilComp", (float)(shader == "SaccFlight/HUD_NoStencil"
                        ? UnityEngine.Rendering.CompareFunction.Always : UnityEngine.Rendering.CompareFunction.Equal));
                result.renderQueue = -1;
            }
            else if (particle)
            {
                // Standard particle modes: 2 fade, 3 transparent (premultiplied), 4 additive, 6 modulate
                bool additive = mode == 4f || shader.Contains("Additive");
                bool premultiplied = mode == 3f || shader.Contains("Premultiply");
                result.SetFloat("_SrcBlend", (float)(premultiplied ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.SrcAlpha));
                result.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
                result.SetColor("_Color", colour);
                result.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else if (target.name == "FlightWorld/AircraftGlass")
            {
                // A see-through surface with a picture that is not a window (a propeller's blur disc): drawn as the
                // original Fade material is, its opacity the tint's times the texture's and no sky in it. The tint's
                // opacity is often animated (the Wasp's blur disc is invisible at rest and fades in with the
                // propeller's speed), so it is left as it is. Windows keep a floor and a ceiling on their opacity.
                bool fade = texture != null && !source.name.ToLowerInvariant().Contains("glass");
                if (!fade) colour.a = Mathf.Clamp(colour.a, 0.12f, 0.85f);
                result.SetColor("_Color", colour);
                result.SetFloat("_Glance", fade ? 0f : 1f);
                result.SetFloat("_Cull", fade ? SourceCull(source) : 2f);
                result.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else
            {
                Color emission = Color.black;
                if (source.IsKeywordEnabled("_EMISSION") && source.HasProperty("_EmissionColor"))
                {
                    emission = source.GetColor("_EmissionColor");
                    float peak = Mathf.Max(emission.r, emission.g, emission.b);
                    if (peak > 1.5f) emission *= 1.5f / peak;          // no bloom here: keep glow within the display's range
                }
                if (glowOnly)
                {
                    colour = emission.maxColorComponent > 1f ? emission / emission.maxColorComponent : emission;
                    emission = Color.black;
                }
                // A glow that the original paints through a map (cabin signs, panel lighting, running lights) on a
                // surface that also has a picture: the shader here has no glow map, and the glow colour alone would
                // light the whole surface (an airliner's cabin came out pure white). Such a surface is simply lit.
                else if (glowMap != null) emission = Color.black;
                result.SetColor("_Color", new Color(colour.r, colour.g, colour.b, 1f));
                result.SetFloat("_Unlit", selfLit || glowOnly ? 1f : 0f);
                result.SetColor("_EmissionColor", emission);
                result.SetFloat("_Cutoff", mode == 1f && source.HasProperty("_Cutoff") ? source.GetFloat("_Cutoff") : 0f);
                result.SetFloat("_Cull", SourceCull(source));
                result.renderQueue = -1;
            }
            EditorUtility.SetDirty(result);
            return result;
        }

        /// <summary>
        /// Which faces the original draws: Sacc's PBRVCol shaders call it CullMode (the balloon's envelope is 0, both
        /// sides, so it can be seen from inside the basket), URP's _Cull; back faces only if neither says.
        /// </summary>
        static float SourceCull(Material source) =>
            source.HasProperty("_Cull") ? source.GetFloat("_Cull")
            : source.HasProperty("CullMode") ? source.GetFloat("CullMode")
            : source.HasProperty("_CullMode") ? source.GetFloat("_CullMode") : 2f;

        static Texture Texture(Material material, string property) =>
            material.HasProperty(property) ? material.GetTexture(property) : null;
    }
}
