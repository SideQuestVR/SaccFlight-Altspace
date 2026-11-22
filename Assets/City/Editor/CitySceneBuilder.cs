using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace FlightWorld.CityTools
{
    [Serializable] public class CityPad { public float x, y, z, radius; }
    [Serializable] public class CityRunway { public float ax, ay, bx, by, length, width; }
    [Serializable] public class CityHero { public float x, y, height; public string kind; }

    [Serializable]
    public class CityLayout
    {
        public float worldX0, worldY0, worldX1, worldY1, landHeight, buildingTile, groundTile, seaTile, outerExtent;
        public float sunTowardX, sunTowardUp, sunTowardY;
        public int facadeStyles;
        public CityPad[] helipads;
        public CityRunway[] runways;
        public CityHero[] heroes;
    }

    /// <summary>
    /// Assembles the generated city tiles into the open scene: LOD groups, colliders, materials,
    /// sea, sky, haze and the platform's scene settings. Safe to run repeatedly; it replaces the
    /// "City" and "CityEnvironment" roots it created earlier and nothing else.
    /// Pipeline axes are x = east, y = north; Unity is X = east, Z = north, Y = up.
    /// </summary>
    public static class CitySceneBuilder
    {
        const string MatDir = "Assets/City/Materials";
        const string LayoutPath = "Assets/City/Generated/city_layout.json";
        const string ReportPath = "Logs/city_build_report.txt";

        // LOD switch points as a fraction of screen height (a 2 km tile at a ~96 degree headset field
        // of view): detailed within ~3.5 km, simplified to ~9 km, skyline boxes beyond.
        const float Lod0Until = 0.26f, Lod1Until = 0.10f, Lod2Until = 0.004f;

        // View distance and haze. The haze thins with height (clear looking down from altitude) and is
        // forced to full strength between FadeStart and FadeEnd so the far clip plane never shows.
        const float NearClip = 0.1f, FarClip = 22000f, FadeStart = 15000f, FadeEnd = 21500f;
        const float HazeDensity = 0.000076f, HazeScaleHeight = 1200f;
        const float RefreshRate = 90f;          // headset display rate requested by the world (Quest default is 72)
        static readonly Color HazeColor = new Color(0.80f, 0.86f, 0.91f);
        static Vector4 FogParams => new Vector4(HazeDensity, 1f / HazeScaleHeight, FadeStart, 1f / (FadeEnd - FadeStart));

        static void ApplyHaze(Material mat)
        {
            mat.SetColor("_FogColor", HazeColor);
            mat.SetVector("_FogParams", FogParams);
        }

        [MenuItem("FlightWorld/City/Rebuild City In Scene")]
        public static void Rebuild()
        {
            var report = new StringBuilder();
            try
            {
                RebuildInternal(report);
            }
            catch (Exception e)
            {
                report.AppendLine("FAILED: " + e);
                Debug.LogException(e);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, report.ToString());
            Debug.Log("[City] " + report);
        }

        static void RebuildInternal(StringBuilder report)
        {
            int reimported = CityImportSettings.EnsureAll();
            var layout = JsonUtility.FromJson<CityLayout>(File.ReadAllText(LayoutPath));
            var scene = EditorSceneManager.GetActiveScene();
            report.AppendLine($"scene: {scene.path}; re-imported {reimported} assets with corrected settings");

            Directory.CreateDirectory(MatDir);
            var buildingMat = GetMaterial("Building", "FlightWorld/CityBuilding");
            var facades = AssetDatabase.LoadAssetAtPath<Texture2DArray>(CityImportSettings.FacadePath);
            if (facades == null) report.AppendLine("WARNING: facade array did not import as a Texture2DArray");
            buildingMat.SetTexture("_Facades", facades);

            var seaMat = GetMaterial("Sea", "FlightWorld/CitySea");
            seaMat.SetTexture("_TintMap", AssetDatabase.LoadAssetAtPath<Texture2D>(CityImportSettings.WaterTintPath));
            seaMat.SetVector("_WorldRect", new Vector4(layout.worldX0, layout.worldY0,
                1f / (layout.worldX1 - layout.worldX0), 1f / (layout.worldY1 - layout.worldY0)));
            var towardSun = new Vector3(layout.sunTowardX, layout.sunTowardUp, layout.sunTowardY).normalized;
            seaMat.SetVector("_SunDir", towardSun);

            var desertMat = GetMaterial("Desert", "FlightWorld/CityUnlit");
            desertMat.SetColor("_BaseColor", new Color(0.80f, 0.72f, 0.56f));
            ApplyHaze(buildingMat);
            ApplyHaze(seaMat);
            ApplyHaze(desertMat);
            GroundDetail.Apply(desertMat);
            EditorUtility.SetDirty(buildingMat);
            EditorUtility.SetDirty(seaMat);
            EditorUtility.SetDirty(desertMat);

            DestroyRoot(scene, "City");
            DestroyRoot(scene, "CityEnvironment");
            var root = new GameObject("City");
            var groundRoot = Child(root, "Ground");
            var seaRoot = Child(root, "Sea");
            var outerRoot = Child(root, "Outer");
            var buildingRoot = Child(root, "Buildings");

            int tiles = 0, triangles = 0, colliderTris = 0;
            Quaternion? detected = null;
            var outerPaths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { CityImportSettings.MeshDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = Path.GetFileNameWithoutExtension(path);
                var parts = name.Split('_');
                var meshes = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToDictionary(m => m.name, m => m);
                if (meshes.Count == 0) continue;
                if (parts[0] == "outer")
                {
                    outerPaths.Add(path);
                    continue;
                }
                int ix = int.Parse(parts[1]), iy = int.Parse(parts[2]);
                float size = parts[0] == "bld" ? layout.buildingTile : parts[0] == "gnd" ? layout.groundTile : layout.seaTile;
                var origin = new Vector3(layout.worldX0 + ix * size, 0f, layout.worldY0 + iy * size);
                var rotation = TileRotation(meshes.Values.First(), name, report);
                detected ??= rotation;

                if (parts[0] == "gnd")
                {
                    var mesh = meshes.Values.First();
                    var go = Tile(groundRoot, name, origin, rotation);
                    AddRenderer(go, mesh, GroundMaterial($"{ix}_{iy}"));
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                    triangles += Triangles(mesh);
                }
                else if (parts[0] == "sea")
                {
                    var mesh = meshes.Values.First();
                    AddRenderer(Tile(seaRoot, name, origin, rotation), mesh, seaMat);
                    triangles += Triangles(mesh);
                }
                else
                {
                    var go = Tile(buildingRoot, name, origin, rotation);
                    var lods = new List<LOD>();
                    float[] until = { Lod0Until, Lod1Until, Lod2Until };
                    for (int i = 0; i < 3; i++)
                    {
                        if (!meshes.TryGetValue($"{name}_LOD{i}", out var mesh)) continue;
                        var lodGo = Child(go, $"LOD{i}");
                        lods.Add(new LOD(until[i], new Renderer[] { AddRenderer(lodGo, mesh, buildingMat) }));
                        triangles += Triangles(mesh);
                    }
                    if (lods.Count > 0)
                    {
                        var group = go.AddComponent<LODGroup>();
                        group.fadeMode = LODFadeMode.None;
                        group.SetLODs(lods.ToArray());
                        group.RecalculateBounds();
                    }
                    if (meshes.TryGetValue($"{name}_COL", out var col))
                    {
                        Child(go, "Collider").AddComponent<MeshCollider>().sharedMesh = col;
                        colliderTris += Triangles(col);
                    }
                }
                tiles++;
            }

            foreach (var path in outerPaths)
            {
                foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>())
                {
                    var go = Tile(outerRoot, mesh.name, Vector3.zero, detected ?? Quaternion.Euler(0f, 180f, 0f));
                    bool land = mesh.name.Contains("land");
                    AddRenderer(go, mesh, land ? desertMat : seaMat);
                    if (land) go.AddComponent<MeshCollider>().sharedMesh = mesh;
                    triangles += Triangles(mesh);
                }
            }

            BuildEnvironment(layout, towardSun, report);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            report.AppendLine($"tiles: {tiles}; render triangles (all LODs): {triangles}; collider triangles: {colliderTris}");
            report.AppendLine("OK");
        }

        // ------------------------------------------------------------------------------------------
        static void BuildEnvironment(CityLayout layout, Vector3 towardSun, StringBuilder report)
        {
            var env = new GameObject("CityEnvironment");

            // Sun for lit objects (aircraft, avatars). The city itself is unlit with baked light from this direction.
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional && light.gameObject.name != "Sun")
                {
                    light.gameObject.SetActive(false);
                    report.AppendLine($"disabled existing directional light '{light.gameObject.name}'");
                }
            }
            var sunGo = Child(env, "Sun");
            sunGo.transform.rotation = Quaternion.LookRotation(-towardSun);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1.0f, 0.95f, 0.86f);
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;

            // Sky whose horizon is exactly the haze colour, so distant ground fades into it seamlessly.
            var sky = GetMaterial("Sky", "FlightWorld/CitySky");
            // Every property is set explicitly: a material that previously used another sky shader keeps
            // same-named values (an old "_SunSize" turned half the sky into sun disc).
            sky.SetColor("_ZenithColor", new Color(0.20f, 0.42f, 0.78f));
            sky.SetColor("_MidColor", new Color(0.50f, 0.70f, 0.92f));
            sky.SetColor("_HorizonColor", HazeColor);
            sky.SetColor("_SunColor", new Color(1.0f, 0.96f, 0.88f));
            sky.SetFloat("_SunSize", 0.9996f);
            sky.SetFloat("_SunGlow", 0.35f);
            sky.SetVector("_SunDir", towardSun);
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;
            RenderSettings.sun = sun;
            // Standard fog only affects other (lit) objects such as aircraft; keep it light and the same colour.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.00004f;
            RenderSettings.fogColor = HazeColor;

            // Editor preview cameras: without this the Game view clips the world at the template's 1 km.
            foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = FarClip;
                report.AppendLine($"set far clip of scene camera '{cam.gameObject.name}' to {FarClip} m (editor preview only)");
            }
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.62f, 0.68f, 0.78f);

            // One slab under the whole sea so anything hitting the water collides at sea level.
            var water = Child(env, "SeaCollider");
            water.layer = 4; // Water
            var box = water.AddComponent<BoxCollider>();
            float span = (layout.worldX1 - layout.worldX0) + 2f * layout.outerExtent;
            box.center = new Vector3((layout.worldX0 + layout.worldX1) * 0.5f, -25f, (layout.worldY0 + layout.worldY1) * 0.5f);
            box.size = new Vector3(span, 50f, span);

            // Platform scene settings: long view distance, and distant players updated often enough for flight.
            var settingsType = FindType("BS.BSSettings");
            if (settingsType != null)
            {
                var existing = UnityEngine.Object.FindObjectsByType(settingsType, FindObjectsInactive.Include, FindObjectsSortMode.None);
                var component = existing.Length > 0 ? (Component)existing[0] : Child(env, "SceneSettings").AddComponent(settingsType);
                var so = new SerializedObject(component);
                Set(so, "clippingPlane", p => p.vector2Value = new Vector2(NearClip, FarClip), report);
                Set(so, "avatarSyncMode", p => p.stringValue = "action", report);
                Set(so, "refreshRate", p => p.floatValue = RefreshRate, report);
                Set(so, "enableSpiderMan", p => p.boolValue = false, report);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                report.AppendLine("WARNING: BS.BSSettings type not found; clipping planes not set");
            }

            // Arrival point beside the longest runway.
            var spawnType = FindType("BS.BSSpawn");
            if (spawnType != null && layout.runways != null && layout.runways.Length > 0)
            {
                foreach (var old in UnityEngine.Object.FindObjectsByType(spawnType, FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (((Component)old).gameObject.name == "RunwaySpawn")
                        UnityEngine.Object.DestroyImmediate(((Component)old).gameObject);
                }
                var runway = layout.runways.OrderByDescending(r => r.length).First();
                var a = new Vector3(runway.ax, 0f, runway.ay);
                var b = new Vector3(runway.bx, 0f, runway.by);
                var along = (b - a).normalized;
                var side = Vector3.Cross(Vector3.up, along);
                var spawn = Child(env, "RunwaySpawn");
                spawn.transform.position = a + along * 400f + side * (runway.width * 0.5f + 60f) + Vector3.up * (layout.landHeight + 0.1f);
                spawn.transform.rotation = Quaternion.LookRotation(along);
                spawn.AddComponent(spawnType);
                report.AppendLine($"spawn at {spawn.transform.position} beside the {runway.length:F0} m runway");
            }
        }

        // ------------------------------------------------------------------------------------------
        static Material GetMaterial(string name, string shaderName)
        {
            var path = $"{MatDir}/{name}.mat";
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new Exception($"shader '{shaderName}' not found (did it fail to compile?)");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
            }
            return mat;
        }

        static Material GroundMaterial(string key)
        {
            var mat = GetMaterial($"Ground_{key}", "FlightWorld/CityUnlit");
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{CityImportSettings.GroundDir}/gnd_{key}.jpg"));
            mat.SetColor("_BaseColor", Color.white);
            ApplyHaze(mat);
            GroundDetail.Apply(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// Tile meshes are authored with every vertex in the positive x / y quadrant. Depending on the
        /// exporter's axis conversion they arrive either as-is or turned 180 degrees about the up axis;
        /// work out which from the bounds so the tile lands the right way round.
        /// </summary>
        static Quaternion TileRotation(Mesh mesh, string name, StringBuilder report)
        {
            var c = mesh.bounds.center;
            if (c.x >= 0f && c.z >= 0f) return Quaternion.identity;
            if (c.x <= 0f && c.z <= 0f) return Quaternion.Euler(0f, 180f, 0f);
            report.AppendLine($"WARNING: {name} looks mirrored (bounds centre {c}); check the export axes");
            return Quaternion.Euler(0f, 180f, 0f);
        }

        static GameObject Tile(GameObject parent, string name, Vector3 origin, Quaternion rotation)
        {
            var go = Child(parent, name);
            go.transform.SetPositionAndRotation(origin, rotation);
            return go;
        }

        static GameObject Child(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        static MeshRenderer AddRenderer(GameObject go, Mesh mesh, Material material)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            return r;
        }

        static int Triangles(Mesh mesh)
        {
            long count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) count += mesh.GetIndexCount(i) / 3;
            return (int)count;
        }

        static void DestroyRoot(UnityEngine.SceneManagement.Scene scene, string name)
        {
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.name == name) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        static void Set(SerializedObject so, string property, Action<SerializedProperty> assign, StringBuilder report)
        {
            var p = so.FindProperty(property);
            if (p == null)
            {
                report.AppendLine($"WARNING: setting '{property}' not found on {so.targetObject.GetType().Name}");
                return;
            }
            assign(p);
        }
    }
}
