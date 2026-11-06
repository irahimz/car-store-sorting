using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary> Pooled outlines drawn on demand; replaces one QuickOutline per item. </summary>
public static class SharedOutline
{
    /// <summary> Outline width, matching the old QuickOutline setting. </summary>
    public const float Width = 7f;

    /// <summary> One pooled outline drawer. </summary>
    private sealed class Entry
    {
        public GameObject Go;
        public MeshFilter Filter;
        public MeshRenderer Renderer;
        public MeshFilter FillFilter;
        public MeshRenderer FillRenderer;
        public Material Mask;
        public Material Fill;
        public Transform Target;
        public MeshRenderer SourceRenderer;
    }

    private static readonly Dictionary<object, Entry> Active = new Dictionary<object, Entry>();
    private static readonly Stack<Entry> Pool = new Stack<Entry>();
    private static readonly Dictionary<Mesh, Mesh> OutlineMeshes = new Dictionary<Mesh, Mesh>();
    private static readonly HashSet<Mesh> Smoothed = new HashSet<Mesh>();
    private static readonly List<object> Stale = new List<object>();
    private static Material _maskTemplate;
    private static Material _fillTemplate;
    private static Driver _driver;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        foreach (Entry entry in Active.Values)
            if (entry.Go != null) Object.Destroy(entry.Go);
        foreach (Entry entry in Pool)
            if (entry.Go != null) Object.Destroy(entry.Go);
        if (_driver != null) Object.Destroy(_driver.gameObject);
        foreach (Mesh outlineMesh in OutlineMeshes.Values)
            if (outlineMesh != null) Object.Destroy(outlineMesh);
        OutlineMeshes.Clear();
        Active.Clear();
        Pool.Clear();
        Smoothed.Clear();
        _driver = null;
    }

    /// <summary> Outlines the target renderer's mesh for this owner, in a color. </summary>
    public static void Show(object owner, MeshRenderer target, Color color, float width = Width)
    {
        if (owner == null || target == null || !EnsureDriver()) return;

        MeshFilter source = target.GetComponent<MeshFilter>();
        if (source == null || source.sharedMesh == null) return;

        ShowMesh(owner, source.sharedMesh, target.transform, color, target.forceMeshLod, width, false,
            target.GetComponent<CarBatchGeometry>() != null);
    }

    /// <summary> Outlines a given mesh at the target's pose; optional width and through-walls mode. </summary>
    public static void Show(object owner, Mesh mesh, Transform target, Color color, short meshLod = -1,
        float width = Width, bool throughWalls = false)
        => ShowMesh(owner, mesh, target, color, meshLod, width, throughWalls, false);

    private static void ShowMesh(object owner, Mesh mesh, Transform target, Color color, short meshLod,
        float width, bool throughWalls, bool bakedNormals)
    {
        if (owner == null || mesh == null || target == null || !EnsureDriver()) return;

        if (!Active.TryGetValue(owner, out Entry entry))
        {
            entry = Pool.Count > 0 ? Pool.Pop() : CreateEntry();
            Active.Add(owner, entry);
        }

        Mesh outlineMesh = GetOutlineMesh(mesh);
        if (!bakedNormals) SmoothNormals(outlineMesh);
        entry.Filter.sharedMesh = outlineMesh;
        entry.FillFilter.sharedMesh = outlineMesh;
        entry.Renderer.forceMeshLod = meshLod; // outline the detail level shown
        entry.FillRenderer.forceMeshLod = meshLod;
        entry.Fill.SetColor("_OutlineColor", color);
        entry.Fill.SetFloat("_OutlineWidth", width); // pooled entries reset on every show
        entry.Fill.SetFloat("_ZTest", (float)(throughWalls ? CompareFunction.Always : CompareFunction.LessEqual));
        entry.Target = target;
        entry.SourceRenderer = target.GetComponent<MeshRenderer>();
        entry.Go.layer = target.gameObject.layer;
        entry.FillRenderer.gameObject.layer = target.gameObject.layer;
        Follow(entry);
        entry.Go.SetActive(true);
    }

    /// <summary> Removes this owner's outline, if any. </summary>
    public static void Hide(object owner)
    {
        if (owner == null || !Active.TryGetValue(owner, out Entry entry)) return;

        Active.Remove(owner);
        Release(entry);
    }

    private static void Release(Entry entry)
    {
        entry.Target = null;
        entry.SourceRenderer = null;
        if (entry.Go == null) return; // destroyed with the scene
        entry.Go.SetActive(false);
        if (_driver == null)
        {
            Object.Destroy(entry.Go);
            return;
        }
        entry.Go.transform.SetParent(_driver.transform, false);
        Pool.Push(entry);
    }

    /// <summary>Both outline passes must render every submesh, not separate material slots.</summary>
    private static Mesh GetOutlineMesh(Mesh source)
    {
        if (source.subMeshCount <= 1 || !source.isReadable) return source;
        if (OutlineMeshes.TryGetValue(source, out Mesh cached) && cached != null) return cached;

        Mesh combined = Object.Instantiate(source);
        combined.name = source.name + " (Outline)";
        combined.hideFlags = HideFlags.HideAndDontSave;
        // Flatten material slots at EVERY native LOD, rather than replacing all levels with LOD0.
        var indices = new List<int>();
        var ranges = new List<MeshLodRange>();
        for (int level = 0; level < source.lodCount; level++)
        {
            uint start = (uint)indices.Count;
            for (int sub = 0; sub < source.subMeshCount; sub++)
                indices.AddRange(source.GetIndices(sub, level, true));
            ranges.Add(new MeshLodRange { indexStart = start, indexCount = (uint)indices.Count - start });
        }
        combined.lodCount = 1;
        combined.subMeshCount = 1;
        combined.SetIndices(indices, MeshTopology.Triangles, 0, false);
        if (source.lodCount > 1) // Unity refuses SetLods on a single-LOD mesh
        {
            combined.lodCount = source.lodCount;
            combined.SetLods(ranges.ToArray(), 0, MeshUpdateFlags.Default);
        }
        combined.bounds = source.bounds;
        OutlineMeshes[source] = combined;
        return combined;
    }

    /// <summary>Uses the exact parent matrix, including nested scale and rotation.</summary>
    private static void Follow(Entry entry)
    {
        Transform t = entry.Go.transform;
        if (t.parent != entry.Target) t.SetParent(entry.Target, false);
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
    }

    private static bool EnsureDriver()
    {
        if (_driver != null) return true;

        // Scene reload destroyed the old drawers; forget them.
        Active.Clear();
        Pool.Clear();

        if (_maskTemplate == null) _maskTemplate = Resources.Load<Material>("Materials/OutlineMask");
        if (_fillTemplate == null) _fillTemplate = Resources.Load<Material>("Materials/OutlineFill");
        if (_maskTemplate == null || _fillTemplate == null)
        {
            Debug.LogWarning("SharedOutline: QuickOutline materials missing from Resources/Materials.");
            return false;
        }

        _driver = new GameObject("SharedOutline").AddComponent<Driver>();
        return true;
    }

    /// <summary> Builds a drawer in 'visible only' mode: hidden where other objects cover it. </summary>
    private static Entry CreateEntry()
    {
        Entry entry = new Entry { Go = new GameObject("Outline") };
        entry.Go.transform.SetParent(_driver.transform, false);
        entry.Filter = entry.Go.AddComponent<MeshFilter>();
        entry.Renderer = entry.Go.AddComponent<MeshRenderer>();

        GameObject fillObject = new GameObject("Fill");
        fillObject.transform.SetParent(entry.Go.transform, false);
        entry.FillFilter = fillObject.AddComponent<MeshFilter>();
        entry.FillRenderer = fillObject.AddComponent<MeshRenderer>();

        entry.Mask = new Material(_maskTemplate) { name = "OutlineMask (Shared)" };
        entry.Fill = new Material(_fillTemplate) { name = "OutlineFill (Shared)" };
        entry.Mask.SetFloat("_ZTest", (float)CompareFunction.Always);
        entry.Fill.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
        entry.Fill.SetFloat("_OutlineWidth", Width);

        // Separate renderers let Unity draw every mask before any fill, so car parts
        // share one silhouette instead of painting over one another's interiors.
        entry.Renderer.sharedMaterial = entry.Mask;
        entry.FillRenderer.sharedMaterial = entry.Fill;
        entry.Renderer.shadowCastingMode = ShadowCastingMode.Off;
        entry.Renderer.receiveShadows = false;
        entry.Renderer.lightProbeUsage = LightProbeUsage.Off;
        entry.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        entry.FillRenderer.shadowCastingMode = ShadowCastingMode.Off;
        entry.FillRenderer.receiveShadows = false;
        entry.FillRenderer.lightProbeUsage = LightProbeUsage.Off;
        entry.FillRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        entry.Go.SetActive(false);
        return entry;
    }

    /// <summary> Stores averaged normals in UV3 once per mesh, as QuickOutline did. </summary>
    private static void SmoothNormals(Mesh mesh)
    {
        if (!Smoothed.Add(mesh) || !mesh.isReadable) return;

        Vector3[] vertices = mesh.vertices;
        List<Vector3> smooth = new List<Vector3>(mesh.normals);
        if (smooth.Count != vertices.Length) return;

        foreach (var group in vertices.Select((v, i) => (v, i)).GroupBy(p => p.v))
        {
            if (group.Count() == 1) continue;

            Vector3 sum = Vector3.zero;
            foreach (var p in group) sum += smooth[p.i];
            sum.Normalize();
            foreach (var p in group) smooth[p.i] = sum;
        }

        mesh.SetUVs(3, smooth);
    }

    /// <summary>Releases outlines whose targets have been destroyed.</summary>
    private sealed class Driver : MonoBehaviour
    {
        private void LateUpdate()
        {
            foreach (KeyValuePair<object, Entry> pair in Active)
            {
                Entry entry = pair.Value;
                if (entry.Target == null || !entry.Target.gameObject.activeInHierarchy ||
                    entry.SourceRenderer != null && !entry.SourceRenderer.enabled &&
                    !ItemChunkRenderer.IsMerged(entry.SourceRenderer)) // a chunk cell still draws it
                    Stale.Add(pair.Key);
            }

            foreach (object owner in Stale) Hide(owner);
            Stale.Clear();
        }

        private void OnDestroy()
        {
            if (_driver == this) _driver = null;
        }
    }
}
