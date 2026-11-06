using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using TinyRiders;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary> Bakes every car into one atlas-mapped mesh: body submesh plus accessory submesh. </summary>
public static class CarVisualBatchBuilder
{
    public const string Root = "Assets/TinyRiders/Gameplay/Rendering/CarAtlas";
    const string LegacyRoot = "Assets/TinyRiders/Gameplay/Rendering/CarBatches";
    public const string Evidence = "IntegrationEvidence/CarBatching";
    const int Version = 7;
    const int SourceLevels = 3; // LODs read from the source parts
    const int LodLevels = 4;    // plus a generated far LOD3
    const int Lod3Target = 1300; // non-wheel LOD3 triangles per car
    const int WheelSides = 12;
    const float RimRatio = 0.6f; // rim disc radius vs tire radius on LOD3 wheels
    const int SwatchGrid = 8; // flat-color cells per swatch tile side
    public static bool SkipForBenchmark;
    static ItemLodSet lods;

    /// <summary> One atlas: grid layout, generated texture/material and its tile lookup. </summary>
    sealed class Atlas
    {
        public string Name;
        public int Size, Tile, Pad, Grid;
        public Material Material;
        public readonly Dictionary<string, int> Tiles = new Dictionary<string, int>();   // textured key -> tile
        public readonly Dictionary<string, int> Swatches = new Dictionary<string, int>(); // color key -> cell
        public int SwatchTile = -1;
        public int Content => Tile - 2 * Pad;
        public string TexturePath => Root + "/" + Name + ".png";
        public string MaterialPath => Root + "/" + Name + "_Mat.mat";
    }

    static Atlas NewBody() => new Atlas { Name = "CarAtlas", Size = 4096, Tile = 240, Pad = 4, Grid = 17 };
    static Atlas NewAccessory() => new Atlas { Name = "CarAccessoryAtlas", Size = 2048, Tile = 256, Pad = 4, Grid = 8 };

    // ------------------------------------------------------------------ classification

    /// <summary> Decals are dropped from the bake. </summary>
    static bool IsDecal(Material m) => m != null && m.name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0;

    static bool IsAccessory(Renderer r) => r.GetComponentsInParent<Transform>(true).Any(t => t.name == "ThemeAccessories");

    /// <summary> Only visible renderers merge; hidden parts stay untouched. </summary>
    static bool IsVisible(MeshRenderer r) => r.enabled && r.GetComponent<MeshFilter>()?.sharedMesh != null
        && r.GetComponentsInParent<Transform>(true).All(t => t.gameObject.activeSelf); // activeInHierarchy is false on prefab assets

    /// <summary> Driver parts sit behind opaque glass; the bake drops them. </summary>
    static bool IsDriver(Renderer r) => r.GetComponentsInParent<Transform>(true).Any(t => t.name.StartsWith("Racer_", StringComparison.Ordinal));

    /// <summary> Visible renderers the bake merges: everything except the hidden driver. </summary>
    static bool IsBaked(MeshRenderer r) => IsVisible(r) && !IsDriver(r);

    static Texture BaseMap(Material m) => m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
    static Color Tint(Material m) => m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : Color.white;
    static string ColorKey(Color c) => ColorUtility.ToHtmlStringRGB(c);
    static string TileKey(Material m) => AssetDatabase.GetAssetPath(BaseMap(m)) + "|" + ColorKey(Tint(m));

    static Mesh SourceMesh(Mesh mesh) => mesh != null && lods != null && lods.TryGet(mesh, out var entry) && entry.bakedMesh != null ? entry.bakedMesh : mesh;
    static int[] Indices(Mesh mesh, int sub, int level) => lods != null && lods.TryGet(mesh, out var entry)
        ? entry.GetIndices(level, sub) : mesh.GetIndices(sub, Mathf.Min(level, mesh.lodCount - 1), true);

    // ------------------------------------------------------------------ atlas building

    /// <summary> Collects every (texture, tint) pair across enabled cars and renders both atlases. </summary>
    static (Atlas body, Atlas accessory) BuildAtlases(IEnumerable<GameObject> sources)
    {
        var body = NewBody(); var accessory = NewAccessory();
        var textured = new[] { new SortedDictionary<string, Material>(StringComparer.Ordinal), new SortedDictionary<string, Material>(StringComparer.Ordinal) };
        var flat = new[] { new SortedDictionary<string, Color>(StringComparer.Ordinal), new SortedDictionary<string, Color>(StringComparer.Ordinal) };
        foreach (var source in sources)
        foreach (var r in source.GetComponentsInChildren<MeshRenderer>(true).Where(IsBaked))
        {
            int a = IsAccessory(r) ? 1 : 0;
            foreach (var m in r.sharedMaterials.Take(r.GetComponent<MeshFilter>().sharedMesh.subMeshCount))
            {
                if (m == null || IsDecal(m)) continue;
                if (BaseMap(m) == null) flat[a][ColorKey(Tint(m))] = Tint(m);
                else if (!textured[a].ContainsKey(TileKey(m))) textured[a][TileKey(m)] = m;
            }
        }
        Render(body, textured[0], flat[0]);
        Render(accessory, textured[1], flat[1]);
        return (body, accessory);
    }

    /// <summary> Assigns tiles, paints tinted downscaled copies with edge gutters, saves texture and material. </summary>
    static void Render(Atlas atlas, SortedDictionary<string, Material> textured, SortedDictionary<string, Color> flat)
    {
        int needed = textured.Count + (flat.Count > 0 ? 1 : 0);
        if (needed > atlas.Grid * atlas.Grid) throw new InvalidOperationException($"{atlas.Name}: {needed} tiles exceed {atlas.Grid * atlas.Grid} slots");
        if (flat.Count > SwatchGrid * SwatchGrid) throw new InvalidOperationException($"{atlas.Name}: {flat.Count} flat colors exceed one swatch tile");

        var pixels = new Color32[atlas.Size * atlas.Size];
        int tile = 0;
        foreach (var pair in textured)
        {
            atlas.Tiles[pair.Key] = tile;
            var tint = Tint(pair.Value);
            var content = Downsample(ReadSource(BaseMap(pair.Value)), atlas.Content);
            for (int i = 0; i < content.Length; i++) content[i] = new Color(content[i].r * tint.r, content[i].g * tint.g, content[i].b * tint.b, 1f);
            Blit(atlas, pixels, tile++, content);
        }
        if (flat.Count > 0)
        {
            atlas.SwatchTile = tile;
            var content = new Color[atlas.Content * atlas.Content];
            int cell = 0;
            foreach (var pair in flat)
            {
                atlas.Swatches[pair.Key] = cell;
                for (int y = 0; y < atlas.Content; y++)
                for (int x = 0; x < atlas.Content; x++)
                    if (x * SwatchGrid / atlas.Content == cell % SwatchGrid && y * SwatchGrid / atlas.Content == cell / SwatchGrid)
                        content[y * atlas.Content + x] = new Color(pair.Value.r, pair.Value.g, pair.Value.b, 1f);
                cell++;
            }
            Blit(atlas, pixels, tile, content);
        }

        var texture = new Texture2D(atlas.Size, atlas.Size, TextureFormat.RGBA32, false);
        texture.SetPixels32(pixels);
        File.WriteAllBytes(atlas.TexturePath, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);
        AssetDatabase.ImportAsset(atlas.TexturePath, ImportAssetOptions.ForceSynchronousImport);

        var importer = (TextureImporter)AssetImporter.GetAtPath(atlas.TexturePath);
        importer.textureType = TextureImporterType.Default; importer.sRGBTexture = true;
        importer.alphaSource = TextureImporterAlphaSource.None; importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Clamp; importer.filterMode = FilterMode.Bilinear;
        importer.maxTextureSize = atlas.Size; importer.isReadable = false;
        importer.SaveAndReimport();

        var material = AssetDatabase.LoadAssetAtPath<Material>(atlas.MaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Baked Lit")) { name = atlas.Name + "_Mat" };
            AssetDatabase.CreateAsset(material, atlas.MaterialPath);
        }
        material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(atlas.TexturePath));
        material.SetColor("_BaseColor", Color.white);
        material.enableInstancing = false;
        EditorUtility.SetDirty(material);
        atlas.Material = material;
    }

    /// <summary> Copies tile content and extrudes its edge pixels into the gutter. </summary>
    static void Blit(Atlas atlas, Color32[] pixels, int tile, Color[] content)
    {
        int ox = tile % atlas.Grid * atlas.Tile, oy = tile / atlas.Grid * atlas.Tile, c = atlas.Content;
        for (int y = 0; y < atlas.Tile; y++)
        for (int x = 0; x < atlas.Tile; x++)
        {
            int sx = Mathf.Clamp(x - atlas.Pad, 0, c - 1), sy = Mathf.Clamp(y - atlas.Pad, 0, c - 1);
            pixels[(oy + y) * atlas.Size + ox + x] = content[sy * c + sx];
        }
    }

    /// <summary> Full-resolution source pixels; reads the file when possible, else GPU readback. </summary>
    static (Color[] pixels, int width, int height) ReadSource(Texture source)
    {
        string path = AssetDatabase.GetAssetPath(source);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
        {
            var file = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (file.LoadImage(File.ReadAllBytes(path)))
            {
                var result = (file.GetPixels(), file.width, file.height);
                Object.DestroyImmediate(file); return result;
            }
            Object.DestroyImmediate(file);
        }
        var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        Graphics.Blit(source, rt); RenderTexture.active = rt;
        var read = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        read.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); read.Apply();
        RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt);
        var pixels = (read.GetPixels(), read.width, read.height);
        Object.DestroyImmediate(read); return pixels;
    }

    /// <summary> Box-filters a source image down to a square of the given size. </summary>
    static Color[] Downsample((Color[] pixels, int width, int height) source, int size)
    {
        var result = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            int y0 = y * source.height / size, y1 = Mathf.Max(y0 + 1, (y + 1) * source.height / size);
            for (int x = 0; x < size; x++)
            {
                int x0 = x * source.width / size, x1 = Mathf.Max(x0 + 1, (x + 1) * source.width / size);
                Color sum = Color.clear;
                for (int sy = y0; sy < y1; sy++)
                for (int sx = x0; sx < x1; sx++) sum += source.pixels[sy * source.width + sx];
                result[y * size + x] = sum / ((y1 - y0) * (x1 - x0));
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ mesh baking

    /// <summary> Growable vertex stream; one entry per (source vertex, UV wrap, tile). </summary>
    sealed class Builder
    {
        public readonly List<Vector3> Positions = new List<Vector3>(), Normals = new List<Vector3>(), Smooth = new List<Vector3>();
        public readonly List<Color> Colors = new List<Color>();
        public readonly List<Vector2> Uv0 = new List<Vector2>();
        public readonly List<Vector4>[] Extra = Enumerable.Range(0, 8).Select(_ => new List<Vector4>()).ToArray();
        public readonly int[] Dimensions = new int[8];
        public readonly List<int>[,] Triangles = new List<int>[2, LodLevels];
        public readonly List<int>[] Simplify = { new List<int>(), new List<int>() }; // non-wheel LOD0, LOD3 input
        public Builder() { for (int s = 0; s < 2; s++) for (int l = 0; l < LodLevels; l++) Triangles[s, l] = new List<int>(); }
    }

    /// <summary> One wheel's root-space bounds and its material UVs, for the LOD3 cylinder. </summary>
    sealed class Wheel
    {
        public Bounds Bounds;
        public bool HasBounds;
        public readonly List<(Vector2 uv, long tris, bool tire)> Slots = new List<(Vector2, long, bool)>();
    }

    /// <summary> Nearest wheel transform at or above the renderer; null when not a wheel. </summary>
    static Transform WheelOf(Renderer r)
    {
        for (var t = r.transform; t != null; t = t.parent) if (t.name.Contains("Wheel_")) return t;
        return null;
    }

    /// <summary> Source part data read once per renderer. </summary>
    sealed class PartData
    {
        public Vector3[] Vertices, Normals, Smooth;
        public Color[] Colors;
        public Vector2[] Uv;
        public List<Vector4>[] Channels = new List<Vector4>[8];
    }

    static PartData Read(Mesh source, Matrix4x4 matrix)
    {
        var data = new PartData { Vertices = source.vertices, Normals = source.normals, Colors = source.colors, Uv = source.uv };
        var normalMatrix = matrix.inverse.transpose;
        data.Smooth = new Vector3[data.Vertices.Length];
        // Matches SharedOutline's per-renderer smoothing before the part transform.
        foreach (var group in data.Vertices.Select((v, i) => (v, i)).GroupBy(p => p.v))
        {
            Vector3 sum = Vector3.zero;
            foreach (var p in group) if (data.Normals.Length > p.i) sum += data.Normals[p.i];
            sum.Normalize();
            foreach (var p in group) data.Smooth[p.i] = normalMatrix.MultiplyVector(sum).normalized;
        }
        for (int c = 1; c < 8; c++)
        {
            if (c == 3 || !source.HasVertexAttribute(VertexAttribute.TexCoord0 + c)) continue;
            data.Channels[c] = new List<Vector4>(); source.GetUVs(c, data.Channels[c]);
        }
        return data;
    }

    /// <summary> Merges all visible renderers into one mesh with atlas UVs; drops decals. </summary>
    static Mesh Combine(GameObject root, MeshRenderer[] renderers, Atlas body, Atlas accessory, string name)
    {
        var b = new Builder();
        var remap = new Dictionary<(int part, int vertex, int ox, int oy, int sub, int slot), int>();
        var wheels = new Dictionary<Transform, Wheel>();
        bool hasNormals = false, hasColors = false; // tangents dropped: Baked Lit has no normal map
        for (int part = 0; part < renderers.Length; part++)
        {
            var r = renderers[part];
            var mesh = r.GetComponent<MeshFilter>().sharedMesh;
            var source = SourceMesh(mesh);
            var matrix = root.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
            var normalMatrix = matrix.inverse.transpose;
            bool flip = matrix.determinant < 0;
            var data = Read(source, matrix);
            hasNormals |= data.Normals.Length > 0; hasColors |= data.Colors.Length > 0;
            for (int c = 1; c < 8; c++)
                if (data.Channels[c] != null) b.Dimensions[c] = Math.Max(b.Dimensions[c], source.GetVertexAttributeDimension(VertexAttribute.TexCoord0 + c));

            int sub = IsAccessory(r) ? 1 : 0;
            var atlas = sub == 1 ? accessory : body;
            var materials = r.sharedMaterials;
            Wheel wheel = null;
            var wheelRoot = sub == 0 ? WheelOf(r) : null;
            if (wheelRoot != null)
            {
                if (!wheels.TryGetValue(wheelRoot, out wheel)) wheels[wheelRoot] = wheel = new Wheel();
                foreach (var v in data.Vertices)
                {
                    var p = matrix.MultiplyPoint3x4(v);
                    if (wheel.HasBounds) wheel.Bounds.Encapsulate(p); else { wheel.Bounds = new Bounds(p, Vector3.zero); wheel.HasBounds = true; }
                }
            }
            for (int slot = 0; slot < Mathf.Min(materials.Length, mesh.subMeshCount); slot++)
            {
                var material = materials[slot];
                if (material == null || IsDecal(material)) continue;
                bool swatch = BaseMap(material) == null;
                int tile = swatch ? atlas.SwatchTile : atlas.Tiles[TileKey(material)];
                int cell = swatch ? atlas.Swatches[ColorKey(Tint(material))] : 0;
                if (wheel != null)
                    wheel.Slots.Add((swatch ? SwatchUV(atlas, tile, cell) : TileUV(atlas, tile, new Vector2(0.5f, 0.5f)),
                        Indices(mesh, slot, 0).Length / 3, material.name.IndexOf("Tire", StringComparison.OrdinalIgnoreCase) >= 0));
                for (int level = 0; level < SourceLevels; level++)
                {
                    int[] indices = Indices(mesh, slot, level);
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        // Wrapped UVs shift per triangle into 0..1; spanning triangles clamp.
                        int ox = 0, oy = 0;
                        if (!swatch)
                        {
                            Vector2 centroid = (UV(data, indices[i]) + UV(data, indices[i + 1]) + UV(data, indices[i + 2])) / 3f;
                            ox = Mathf.FloorToInt(centroid.x); oy = Mathf.FloorToInt(centroid.y);
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            int src = indices[i + (flip && k > 0 ? 3 - k : k)];
                            var key = (part, src, ox, oy, sub, slot);
                            if (!remap.TryGetValue(key, out int index))
                            {
                                index = b.Positions.Count; remap[key] = index;
                                b.Positions.Add(matrix.MultiplyPoint3x4(data.Vertices[src]));
                                b.Normals.Add(data.Normals.Length > src ? normalMatrix.MultiplyVector(data.Normals[src]).normalized : Vector3.up);
                                b.Colors.Add(data.Colors.Length > src ? data.Colors[src] : Color.white);
                                b.Smooth.Add(data.Smooth[src]);
                                b.Uv0.Add(swatch ? SwatchUV(atlas, tile, cell) : TileUV(atlas, tile, UV(data, src) - new Vector2(ox, oy)));
                                for (int c = 1; c < 8; c++)
                                    if (c != 3) b.Extra[c].Add(data.Channels[c] != null && data.Channels[c].Count > src ? data.Channels[c][src] : Vector4.zero);
                            }
                            b.Triangles[sub, level].Add(index);
                            if (level == 0 && wheel == null) b.Simplify[sub].Add(index);
                        }
                    }
                }
            }
        }

        Weld(b);
        BuildLod3(b, wheels.Values);
        var result = new Mesh { name = name, indexFormat = b.Positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        result.SetVertices(b.Positions);
        if (hasNormals) result.SetNormals(b.Normals);
        if (hasColors) result.SetColors(b.Colors);
        result.SetUVs(0, b.Uv0);
        for (int c = 1; c < 8; c++)
        {
            if (c == 3 || b.Dimensions[c] == 0) continue;
            if (b.Dimensions[c] == 2) result.SetUVs(c, b.Extra[c].Select(v => new Vector2(v.x, v.y)).ToList());
            else if (b.Dimensions[c] == 3) result.SetUVs(c, b.Extra[c].Select(v => new Vector3(v.x, v.y, v.z)).ToList());
            else result.SetUVs(c, b.Extra[c]);
        }
        result.SetUVs(3, b.Smooth); // QuickOutline smooth normals; CarBatchGeometry marks them baked

        int subCount = Enumerable.Range(0, LodLevels).Any(l => b.Triangles[1, l].Count > 0) ? 2 : 1;
        result.subMeshCount = subCount;
        for (int s = 0; s < subCount; s++)
            result.SetIndices(Enumerable.Range(0, LodLevels).SelectMany(l => b.Triangles[s, l]).ToArray(), MeshTopology.Triangles, s, false);
        result.lodCount = LodLevels;
        for (int s = 0; s < subCount; s++)
        {
            uint start = 0;
            result.SetLods(Enumerable.Range(0, LodLevels).Select(l =>
            {
                var range = new MeshLodRange { indexStart = start, indexCount = (uint)b.Triangles[s, l].Count };
                start += (uint)b.Triangles[s, l].Count; return range;
            }).ToArray(), s, MeshUpdateFlags.Default);
        }
        result.RecalculateBounds();
        return result;
    }

    /// <summary> Merges vertices identical in every written attribute; remaps all LOD triangles. </summary>
    static void Weld(Builder b)
    {
        if (b.Dimensions.Any(d => d > 0)) return; // extra UV channels aren't keyed; keep exact data
        var unique = new Dictionary<(Vector3, Vector3, Vector2, Vector3, Color), int>();
        var map = new int[b.Positions.Count];
        var keep = new List<int>(b.Positions.Count);
        for (int i = 0; i < map.Length; i++)
        {
            var key = (b.Positions[i], b.Normals[i], b.Uv0[i], b.Smooth[i], b.Colors[i]);
            if (!unique.TryGetValue(key, out int index)) { index = keep.Count; unique[key] = index; keep.Add(i); }
            map[i] = index;
        }
        if (keep.Count == map.Length) return;

        void Compact<T>(List<T> list) { var copy = keep.Select(i => list[i]).ToList(); list.Clear(); list.AddRange(copy); }
        Compact(b.Positions); Compact(b.Normals); Compact(b.Uv0); Compact(b.Smooth); Compact(b.Colors);
        foreach (var triangles in b.Triangles.Cast<List<int>>().Concat(b.Simplify))
            for (int i = 0; i < triangles.Count; i++) triangles[i] = map[triangles[i]];
    }

    /// <summary> LOD3: simplified non-wheel geometry plus one low-poly cylinder per wheel. </summary>
    static void BuildLod3(Builder b, IEnumerable<Wheel> wheels)
    {
        long total = (b.Simplify[0].Count + b.Simplify[1].Count) / 3;
        // Already low-poly: LOD3 reuses LOD2 instead of adding heavier wheel cylinders.
        if (total <= Lod3Target) { UseLod2AsLod3(b); return; }
        float ratio = total > 0 ? Mathf.Min(1f, (float)Lod3Target / total) : 1f;
        var temp = new Mesh { indexFormat = IndexFormat.UInt32 };
        try
        {
            temp.SetVertices(b.Positions); temp.SetNormals(b.Normals); temp.SetUVs(0, b.Uv0);
            temp.subMeshCount = 2;
            for (int s = 0; s < 2; s++) temp.SetIndices(b.Simplify[s], MeshTopology.Triangles, s, false);
            if (total > 64) MeshLodUtility.GenerateMeshLods(temp, 8);
            for (int s = 0; s < 2; s++)
            {
                // Shallowest level at or under the target ratio; small parts keep LOD0.
                int[] pick = b.Simplify[s].ToArray();
                for (int level = 1; level < temp.lodCount && pick.Length > ratio * b.Simplify[s].Count; level++)
                {
                    int[] reduced = temp.GetIndices(s, level, true);
                    if (reduced.Length >= 24 && reduced.Length < pick.Length) pick = reduced;
                }
                b.Triangles[s, 3].AddRange(pick);
            }
        }
        finally { Object.DestroyImmediate(temp); }

        foreach (var wheel in wheels) if (wheel.HasBounds && wheel.Slots.Count > 0) AddCylinder(b, wheel);
        int lod3 = b.Triangles[0, 3].Count + b.Triangles[1, 3].Count;
        int lod2 = b.Triangles[0, SourceLevels - 1].Count + b.Triangles[1, SourceLevels - 1].Count;
        if (lod3 >= lod2) UseLod2AsLod3(b);
    }

    /// <summary> Copies LOD2 triangles into LOD3 for both submeshes. </summary>
    static void UseLod2AsLod3(Builder b)
    {
        for (int s = 0; s < 2; s++) { b.Triangles[s, 3].Clear(); b.Triangles[s, 3].AddRange(b.Triangles[s, SourceLevels - 1]); }
    }

    /// <summary> Appends a capped cylinder along the wheel's thinnest axis; tire sides, tire ring and rim disc caps. </summary>
    static void AddCylinder(Builder b, Wheel wheel)
    {
        Vector3 size = wheel.Bounds.size, center = wheel.Bounds.center;
        int axis = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
        Vector3 along = Vector3.zero; along[axis] = 1f;
        Vector3 u = Vector3.zero; u[(axis + 1) % 3] = 1f;
        Vector3 w = Vector3.zero; w[(axis + 2) % 3] = 1f;
        float radius = Mathf.Max(size[(axis + 1) % 3], size[(axis + 2) % 3]) * 0.5f, half = size[axis] * 0.5f;

        var tire = wheel.Slots.Where(s => s.tire).OrderByDescending(s => s.tris).Select(s => s.uv).DefaultIfEmpty(wheel.Slots.OrderByDescending(s => s.tris).First().uv).First();
        var rim = wheel.Slots.Where(s => !s.tire).OrderByDescending(s => s.tris).Select(s => s.uv).DefaultIfEmpty(tire).First();

        int Add(Vector3 position, Vector3 normal, Vector2 uv)
        {
            b.Positions.Add(position); b.Normals.Add(normal); b.Smooth.Add(normal); b.Colors.Add(Color.white); b.Uv0.Add(uv);
            for (int c = 1; c < 8; c++) if (c != 3) b.Extra[c].Add(Vector4.zero);
            return b.Positions.Count - 1;
        }
        void Triangle(int i0, int i1, int i2, Vector3 outward)
        {
            var normal = Vector3.Cross(b.Positions[i1] - b.Positions[i0], b.Positions[i2] - b.Positions[i0]);
            if (Vector3.Dot(normal, outward) < 0) (i1, i2) = (i2, i1); // Unity front faces: cross points outward
            b.Triangles[0, 3].Add(i0); b.Triangles[0, 3].Add(i1); b.Triangles[0, 3].Add(i2);
        }

        Vector3 Radial(int i) { float a = i * Mathf.PI * 2f / WheelSides; return u * Mathf.Cos(a) + w * Mathf.Sin(a); }
        int sides = b.Positions.Count;
        for (int i = 0; i < WheelSides; i++)
        {
            Add(center + Radial(i) * radius - along * half, Radial(i), tire);
            Add(center + Radial(i) * radius + along * half, Radial(i), tire);
        }
        for (int i = 0; i < WheelSides; i++)
        {
            int j = (i + 1) % WheelSides, a0 = sides + i * 2, a1 = a0 + 1, b0 = sides + j * 2, b1 = b0 + 1;
            var outward = (Radial(i) + Radial(j)).normalized;
            Triangle(a0, b0, a1, outward); Triangle(a1, b0, b1, outward);
        }
        foreach (float sign in new[] { -1f, 1f })
        {
            // Tire-colored ring around a smaller rim disc.
            Vector3 face = center + along * (half * sign), normal = along * sign;
            int outer = b.Positions.Count;
            for (int i = 0; i < WheelSides; i++)
            {
                Add(face + Radial(i) * radius, normal, tire);
                Add(face + Radial(i) * radius * RimRatio, normal, tire);
            }
            for (int i = 0; i < WheelSides; i++)
            {
                int j = (i + 1) % WheelSides, o0 = outer + i * 2, i0 = o0 + 1, o1 = outer + j * 2, i1 = o1 + 1;
                Triangle(o0, o1, i0, normal); Triangle(i0, o1, i1, normal);
            }
            int disc = b.Positions.Count;
            for (int i = 0; i < WheelSides; i++) Add(face + Radial(i) * radius * RimRatio, normal, rim);
            for (int i = 1; i < WheelSides - 1; i++) Triangle(disc, disc + i, disc + i + 1, normal);
        }
    }

    static Vector2 UV(PartData data, int i) => data.Uv.Length > i ? data.Uv[i] : Vector2.zero;

    /// <summary> Maps a 0..1 source UV into a tile's content area. </summary>
    static Vector2 TileUV(Atlas atlas, int tile, Vector2 uv)
    {
        float x = tile % atlas.Grid * atlas.Tile + atlas.Pad + Mathf.Clamp01(uv.x) * atlas.Content;
        float y = tile / atlas.Grid * atlas.Tile + atlas.Pad + Mathf.Clamp01(uv.y) * atlas.Content;
        return new Vector2(x / atlas.Size, y / atlas.Size);
    }

    /// <summary> Center of a flat-color cell inside the swatch tile. </summary>
    static Vector2 SwatchUV(Atlas atlas, int tile, int cell)
        => TileUV(atlas, tile, new Vector2((cell % SwatchGrid + 0.5f) / SwatchGrid, (cell / SwatchGrid + 0.5f) / SwatchGrid));

    /// <summary> Writes the mesh in place so its GUID survives rebakes. </summary>
    static Mesh SaveMesh(Mesh mesh, string path)
    {
        var saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (saved == null) { AssetDatabase.CreateAsset(mesh, path); return mesh; }
        EditorUtility.CopySerialized(mesh, saved); saved.name = mesh.name;
        Object.DestroyImmediate(mesh); return saved;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = path.Substring(0, path.LastIndexOf('/'));
        EnsureFolder(parent); AssetDatabase.CreateFolder(parent, path.Substring(path.LastIndexOf('/') + 1));
    }

    /// <summary> Bakes one source car into folder/Visual.prefab with a single atlas renderer. </summary>
    static GameObject BakeVisual(GameObject source, string folder, Atlas body, Atlas accessory)
    {
        EnsureFolder(folder);
        var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(source));
        try
        {
            // Gameplay wrappers own physics.
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(rb);
            foreach (var collider in root.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            var renderers = root.GetComponentsInChildren<MeshRenderer>().Where(IsBaked).ToArray();
            if (renderers.Length == 0) throw new InvalidOperationException("No visible renderers: " + source.name);
            var drivers = root.GetComponentsInChildren<MeshRenderer>(true).Where(IsDriver).ToArray();

            var mesh = SaveMesh(Combine(root, renderers, body, accessory, "CarAtlas " + source.name), folder + "/Mesh.asset");
            var target = new GameObject("Atlas Mesh", typeof(MeshFilter), typeof(MeshRenderer), typeof(CarBatchGeometry));
            target.transform.SetParent(root.transform, false); target.layer = renderers[0].gameObject.layer;
            var renderer = target.GetComponent<MeshRenderer>();
            EditorUtility.CopySerialized(renderers[0], renderer);
            renderer.sharedMaterials = mesh.subMeshCount > 1 ? new[] { body.Material, accessory.Material } : new[] { body.Material };
            target.GetComponent<MeshFilter>().sharedMesh = mesh;
            renderer.ResetLocalBounds(); renderer.ResetBounds();

            // Transforms stay for any references; only the merged renderers go.
            foreach (var r in renderers.Concat(drivers)) { var filter = r.GetComponent<MeshFilter>(); Object.DestroyImmediate(r); Object.DestroyImmediate(filter); }
            root.name = source.name;
            var saved = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Visual.prefab");
            ValidateCounts(source, saved);
            return saved;
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    [MenuItem("Tools/Toy Car Sort/Rendering/Bake Car Atlas")]
    public static void BakeAll()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode before baking");
        EnsureFolder(Root); Directory.CreateDirectory(Evidence);
        lods = AssetDatabase.LoadAssetAtPath<ItemLodSet>(ItemDetailBuilder.SetPath);
        var catalog = AssetDatabase.LoadAssetAtPath<ThemedCarCatalog>(ItemDetailBuilder.CatalogPath);
        var entries = catalog.entries.Where(e => e.enabled).OrderBy(e => e.collectibleId, StringComparer.Ordinal).ToList();
        var (body, accessory) = BuildAtlases(entries.Select(e => e.prefab));
        string layout = LayoutHash(body, accessory);

        var report = new JArray();
        foreach (var entry in entries)
        {
            string sourcePath = AssetDatabase.GetAssetPath(entry.prefab);
            string folder = Root + "/" + AssetDatabase.AssetPathToGUID(sourcePath);
            string fingerprint = Fingerprint(entry.prefab) + ":" + layout;
            var visual = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Visual.prefab");
            if (visual == null || AssetImporter.GetAtPath(folder + "/Visual.prefab").userData != fingerprint)
            {
                visual = BakeVisual(entry.prefab, folder, body, accessory);
                var importer = AssetImporter.GetAtPath(folder + "/Visual.prefab");
                importer.userData = fingerprint; AssetDatabase.WriteImportSettingsIfDirty(importer.assetPath);
            }
            foreach (var wrapper in new[] { entry.sortingItem.WorldItem.gameObject, entry.sortingItem.HandItem.gameObject, entry.sortingItem.PlacedOnShelfPrefab.gameObject })
                ReplaceVisual(wrapper, visual);
            report.Add(new JObject { ["id"] = entry.collectibleId, ["source"] = sourcePath, ["generated"] = AssetDatabase.GetAssetPath(visual),
                ["drawsBefore"] = entry.prefab.GetComponentsInChildren<MeshRenderer>().Where(IsVisible).Sum(r => r.sharedMaterials.Length),
                ["drawsAfter"] = visual.GetComponentsInChildren<MeshRenderer>().Where(IsVisible).Sum(r => r.sharedMaterials.Length) });
            if (report.Count % 50 == 0) Debug.Log("CAR_ATLAS " + report.Count + "/" + entries.Count);
        }
        RegisterLods(); AssetDatabase.SaveAssets();
        File.WriteAllText(Evidence + "/atlas-bake.json", new JObject { ["version"] = Version, ["cars"] = report,
            ["bodyTiles"] = body.Tiles.Count, ["bodySwatches"] = body.Swatches.Count, ["accessoryTiles"] = accessory.Tiles.Count,
            ["drawsBefore"] = report.Sum(r => (int)r["drawsBefore"]), ["drawsAfter"] = report.Sum(r => (int)r["drawsAfter"]) }.ToString());
    }

    /// <summary> Changes when any tile assignment changes, forcing a full rebake. </summary>
    static string LayoutHash(Atlas body, Atlas accessory)
    {
        var text = new StringBuilder();
        foreach (var atlas in new[] { body, accessory })
        {
            text.Append(atlas.Name).Append(atlas.Size).Append(atlas.Tile).Append(atlas.Pad).Append(atlas.SwatchTile);
            foreach (var t in atlas.Tiles) text.Append(t.Key).Append(t.Value);
            foreach (var s in atlas.Swatches) text.Append(s.Key).Append(s.Value);
        }
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").Substring(0, 16);
    }

    static void ReplaceVisual(GameObject wrapper, GameObject visual)
    {
        var current = new SerializedObject(wrapper.GetComponent<ItemVisualGroup>());
        var currentRoot = (Transform)current.FindProperty("visualRoot").objectReferenceValue;
        if (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(currentRoot.gameObject) == AssetDatabase.GetAssetPath(visual)) return;
        var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(wrapper));
        try
        {
            var serialized = new SerializedObject(root.GetComponent<ItemVisualGroup>());
            var old = (Transform)serialized.FindProperty("visualRoot").objectReferenceValue;
            var replacement = (GameObject)PrefabUtility.InstantiatePrefab(visual, root.scene);
            foreach (var rb in replacement.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(rb);
            foreach (var collider in replacement.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            replacement.name = old.name; replacement.transform.SetParent(old.parent, false);
            replacement.transform.SetSiblingIndex(old.GetSiblingIndex());
            replacement.SetActive(old.gameObject.activeSelf);
            replacement.transform.localPosition = old.localPosition; replacement.transform.localRotation = old.localRotation;
            replacement.transform.localScale = old.localScale;
            foreach (var t in replacement.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = old.gameObject.layer;
            serialized.FindProperty("visualRoot").objectReferenceValue = replacement.transform; serialized.ApplyModifiedPropertiesWithoutUndo();
            Object.DestroyImmediate(old.gameObject);
            PrefabUtility.SaveAsPrefabAsset(root, AssetDatabase.GetAssetPath(wrapper));
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    [MenuItem("Tools/Toy Car Sort/Rendering/Restore Authored Car Visuals")]
    public static void RestoreOriginalVisuals()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode first");
        var catalog = AssetDatabase.LoadAssetAtPath<ThemedCarCatalog>(ItemDetailBuilder.CatalogPath);
        foreach (var entry in catalog.entries.Where(e => e.enabled))
            foreach (var wrapper in new[] { entry.sortingItem.WorldItem.gameObject, entry.sortingItem.HandItem.gameObject, entry.sortingItem.PlacedOnShelfPrefab.gameObject })
                ReplaceVisual(wrapper, entry.prefab);
        RegisterLods(); AssetDatabase.SaveAssets();
    }

    static bool Generated(string path) => path.StartsWith(Root + "/", StringComparison.Ordinal) || path.StartsWith(LegacyRoot + "/", StringComparison.Ordinal);

    /// <summary> Keeps source LOD entries, drops stale generated ones, registers current atlas meshes. </summary>
    public static void RegisterLods()
    {
        var set = AssetDatabase.LoadAssetAtPath<ItemLodSet>(ItemDetailBuilder.SetPath);
        var so = new SerializedObject(set); var field = so.FindProperty("entries");
        var entries = new List<ItemLodSet.Entry>();
        for (int i = 0; i < field.arraySize; i++)
        {
            var e = field.GetArrayElementAtIndex(i);
            var mesh = (Mesh)e.FindPropertyRelative("mesh").objectReferenceValue;
            if (mesh != null && !Generated(AssetDatabase.GetAssetPath(mesh)))
                entries.Add(new ItemLodSet.Entry { mesh = mesh, bakedMesh = (Mesh)e.FindPropertyRelative("bakedMesh").objectReferenceValue,
                    lod1 = ReadArray(e.FindPropertyRelative("lod1")), lod2 = ReadArray(e.FindPropertyRelative("lod2")) });
        }
        // Only meshes still referenced by wrapper prefabs; never root unused versions in Resources.
        foreach (var mesh in ItemDetailBuilder.CatalogPrefabs().SelectMany(p => p.GetComponentsInChildren<MeshFilter>(true))
            .Select(f => f.sharedMesh).Where(m => m != null && Generated(AssetDatabase.GetAssetPath(m)))
            .Distinct().OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal))
            entries.Add(new ItemLodSet.Entry { mesh = mesh, bakedMesh = mesh });
        set.SetEntries(entries); EditorUtility.SetDirty(set);
    }
    static int[] ReadArray(SerializedProperty p) => Enumerable.Range(0, p.arraySize).Select(i => p.GetArrayElementAtIndex(i).intValue).ToArray();

    /// <summary> Mesh buffers used by generated visuals, including their registered LOD sources. </summary>
    public static string[] PrecisionMeshPaths()
    {
        if (!AssetDatabase.IsValidFolder(Root)) return Array.Empty<string>();
        var set = AssetDatabase.LoadAssetAtPath<ItemLodSet>(ItemDetailBuilder.SetPath);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        void Add(Mesh mesh) { if (mesh != null) { string path = AssetDatabase.GetAssetPath(mesh); if (!string.IsNullOrEmpty(path)) paths.Add(path); } }
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { Root }))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                Add(filter.sharedMesh);
                if (filter.sharedMesh != null && set != null && set.TryGet(filter.sharedMesh, out var entry)) { Add(entry.mesh); Add(entry.bakedMesh); }
            }
        }
        return paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    /// <summary> Throws unless every LOD keeps the source's non-decal triangle count. </summary>
    public static void ValidateCounts(GameObject original, GameObject generated)
    {
        if (lods == null) lods = AssetDatabase.LoadAssetAtPath<ItemLodSet>(ItemDetailBuilder.SetPath);
        long Count(GameObject root, int level, bool skipDecals) => root.GetComponentsInChildren<MeshRenderer>().Where(IsBaked).Sum(r =>
        {
            var mesh = r.GetComponent<MeshFilter>().sharedMesh; var materials = r.sharedMaterials; long total = 0;
            for (int slot = 0; slot < Mathf.Min(materials.Length, mesh.subMeshCount); slot++)
                if (!skipDecals || !IsDecal(materials[slot])) total += Indices(mesh, slot, level).LongLength;
            return total;
        });
        for (int level = 0; level < SourceLevels; level++)
            if (Count(original, level, true) != Count(generated, level, false))
                throw new InvalidOperationException("Car atlas bake changed geometry at LOD" + level + ": " + original.name);
        long far = Count(generated, LodLevels - 1, false);
        if (far == 0 || far > Count(generated, SourceLevels - 1, false))
            throw new InvalidOperationException("Car atlas LOD3 is empty or above LOD2: " + original.name);
        var materials = generated.GetComponentsInChildren<MeshRenderer>().Where(IsVisible).SelectMany(r => r.sharedMaterials).Distinct().ToArray();
        if (materials.Any(m => m == null || !AssetDatabase.GetAssetPath(m).StartsWith(Root + "/", StringComparison.Ordinal)))
            throw new InvalidOperationException("Car atlas bake left a non-atlas material: " + original.name);
    }

    static string Fingerprint(GameObject source)
    {
        var paths = source.GetComponentsInChildren<MeshFilter>(true).Select(f => SourceMesh(f.sharedMesh))
            .Where(m => m != null).Select(AssetDatabase.GetAssetPath).Distinct().OrderBy(p => p, StringComparer.Ordinal);
        return Version + ":" + AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(source)) + ":" +
            string.Join(";", paths.Select(p => AssetDatabase.GetAssetDependencyHash(p).ToString()));
    }
}

/// <summary> Web builds run the same incremental atlas bake as the menu command. </summary>
public sealed class CarVisualBatchBuildProcessor : IPreprocessBuildWithReport
{
    public int callbackOrder => -1000;
    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform == BuildTarget.WebGL && !CarVisualBatchBuilder.SkipForBenchmark)
            CarVisualBatchBuilder.BakeAll();
    }
}
