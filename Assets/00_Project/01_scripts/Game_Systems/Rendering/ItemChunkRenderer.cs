using System.Collections.Generic;
using System.Runtime.InteropServices;
using DG.Tweening;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary> Merges resting item meshes into one mesh per grid cell; one draw per cell material. </summary>
[DefaultExecutionOrder(100)] // runs after items register and move this frame
public class ItemChunkRenderer : MonoBehaviour
{
    [Tooltip("Merged with the chunk material below as-is.")]
    [SerializeField] private Material sourceMaterial;

    [Tooltip("Chunk material for the source material; template for generated chunk materials.")]
    [SerializeField] private Material chunkMaterial;

    [Tooltip("Also merge any opaque URP Lit or Baked Lit material through a generated chunk material.")]
    [SerializeField] private bool mergeLitMaterials = true;

    [Tooltip("Grid cell size in meters; one mesh per cell, one draw per material.")]
    [SerializeField, Min(0.5f)] private float cellSize = 4f;

    [Tooltip("Seconds a detached item must stay still before it merges back.")]
    [SerializeField, Min(0f)] private float restSeconds = 0.5f;

    [Tooltip("Max milliseconds per frame spent rebuilding cells during play.")]
    [SerializeField, Min(0.1f)] private float rebuildBudgetMs = 3f;

    [Tooltip("Seconds after load where every dirty cell rebuilds at once.")]
    [SerializeField, Min(0f)] private float startupSeconds = 1.5f;

    [Header("Distance LOD")]
    [Tooltip("Distance origin; Camera.main when empty.")]
    [SerializeField] private Transform lodCamera;

    [Tooltip("Meters from camera to cell bounds where cells switch to LOD1.")]
    [SerializeField, Min(0f)] private float lod1Distance = 10f;

    [Tooltip("Meters from camera to cell bounds where cells switch to LOD2.")]
    [SerializeField, Min(0f)] private float lod2Distance = 20f;

    [Tooltip("Meters from camera to cell bounds where cells switch to LOD3 (far cars).")]
    [SerializeField, Min(0f)] private float lod3Distance = 30f;

    [Tooltip("Meters past a threshold before switching back, to avoid flicker.")]
    [SerializeField, Min(0f)] private float lodHysteresis = 1f;


    private const float LightScale = 2f;     // must match the shader's _LightScale
    private const float MoveEpsilonSqr = 1e-6f;
    private const float TurnEpsilon = 0.1f;  // degrees
    private const int LodCount = 4; // LOD3 is the far car LOD; other items repeat LOD2
    private const string LitShader = "Universal Render Pipeline/Lit";
    private const string BakedLitShader = "Universal Render Pipeline/Baked Lit";

    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    /// <summary> A tracked item: one or more renderers merged and shown together. </summary>
    public sealed class Member
    {
        internal Part[] Parts;
        internal MeshRenderer[] Renderers; // the parts' renderers, for the visual group
        internal ItemVisualGroup Group;    // owns part visibility, or null
        internal Transform Root;    // tween and pose target
        internal Rigidbody Body;
        internal Cell Cell;         // cell holding it, or null
        internal bool Waiting;      // detached, checking for rest
        internal bool Queued;       // attach requested, cell not chosen yet
        internal float StillTime;
        internal Vector3 LastPosition;
        internal Quaternion LastRotation;
        internal Vector3 LastScale;
    }

    /// <summary> One mergeable renderer and the chunk material of each submesh. </summary>
    internal sealed class Part
    {
        public MeshRenderer Renderer;
        public Mesh Mesh;
        public Material[] Chunks;
    }

    /// <summary> One grid cell's merged mesh. </summary>
    internal sealed class Cell
    {
        public Vector3Int Key;
        public readonly List<Member> Members = new List<Member>();
        public MeshFilter Filter;
        public MeshRenderer Renderer;
        public bool Dirty;
        public int Levels = 1; // LOD ranges in the current mesh
        public short Lod = -1; // forced LOD, -1 until first pick
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position; // Unity's fixed attribute order: position, color, uv
        public Color32 Light;
        public ushort U, V;      // half floats
    }

    /// <summary> Cached source mesh data read once per mesh. </summary>
    private sealed class SourceMesh
    {
        public Vector3[] Positions;
        public ushort[] U, V;           // half-float UVs
        public int[][][] Lods;          // [submesh][level] triangles; lower levels may repeat higher ones
        public int[] NormalIndex;       // per vertex, into UniqueNormals
        public Vector3[] UniqueNormals; // faceted items share few normals
        public Vector3[] WorldNormals;
        public Color[] Lights;
        public Color32[] Encoded;
    }

    private static ItemChunkRenderer _instance;
    private static readonly HashSet<MeshRenderer> Hidden = new HashSet<MeshRenderer>();

    private readonly Dictionary<Vector3Int, Cell> _cells = new Dictionary<Vector3Int, Cell>();
    private readonly List<Cell> _dirtyCells = new List<Cell>();
    private readonly List<Member> _waiting = new List<Member>();
    private readonly List<Member> _queued = new List<Member>();
    private readonly Dictionary<Mesh, SourceMesh> _sources = new Dictionary<Mesh, SourceMesh>();
    private readonly Dictionary<Material, Material> _chunkMaterials = new Dictionary<Material, Material>();
    private readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
    private VertexAttributeDescriptor[] _layout;
    private Vertex[] _vertexBuffer = new Vertex[0];
    private ushort[] _shortIndices = new ushort[0];
    private int[] _intIndices = new int[0];
    private readonly List<int> _bases = new List<int>(); // first vertex of each part in the cell
    private readonly List<Material> _cellMaterials = new List<Material>();
    private int[] _lodCounts = new int[0]; // [material * LodCount + level]
    private float _startTime;

    /// <summary> Number of merged cells with geometry. </summary>
    public int CellCount => _cells.Count;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Hidden.Clear();

    private void Awake()
    {
        _instance = this;
        _startTime = Time.time;
        _layout = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, 2),
        };
    }

    private void OnEnable()
    {
        ItemDetail.OnChanged += ApplyDetail;
    }

    private void OnDisable()
    {
        ItemDetail.OnChanged -= ApplyDetail;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
        Hidden.Clear();
        foreach (Cell cell in _cells.Values)
            if (cell.Filter != null) Destroy(cell.Filter.sharedMesh);
        foreach (Material material in _chunkMaterials.Values)
            if (material != chunkMaterial) Destroy(material);
    }

    // ------------------------------------------------------------------ public API

    /// <summary> Starts tracking a single-renderer item; null if it can't be merged. </summary>
    public static Member Register(MeshRenderer renderer, Transform root, bool atRest)
    {
        if (renderer == null) return null;
        return Register(new[] { renderer }, null, root != null ? root : renderer.transform, atRest);
    }

    /// <summary> Starts tracking every mergeable part of a nested car; null if none qualify. </summary>
    public static Member Register(ItemVisualGroup group, Transform root, bool atRest)
    {
        if (group == null) return null;
        return Register(group.MergeCandidates(), group, root != null ? root : group.transform, atRest);
    }

    /// <summary> Stops tracking; drops it from its cell and shows its own renderers. </summary>
    public static void Unregister(Member member)
    {
        if (_instance == null || member == null) return;
        _instance.RemoveFromCell(member);
        _instance.StopWaiting(member);
        _instance.Dequeue(member);
        SetHidden(member, false);
    }

    /// <summary> Shows the item on its own renderers until it rests again. </summary>
    public static void Detach(Member member)
    {
        if (_instance == null || member == null || member.Waiting) return;
        _instance.Dequeue(member);
        _instance.RemoveFromCell(member);
        _instance.StartWaiting(member);
    }

    /// <summary> Refreshes the cached Rigidbody after one is added or removed. </summary>
    public static void RefreshBody(Member member, Rigidbody body)
    {
        if (member != null) member.Body = body;
    }

    /// <summary> True while the renderer is hidden because a cell draws it. </summary>
    public static bool IsMerged(MeshRenderer renderer) => renderer != null && Hidden.Contains(renderer);

    private static Member Register(MeshRenderer[] renderers, ItemVisualGroup group, Transform root, bool atRest)
    {
        if (_instance == null || renderers == null) return null;

        var parts = new List<Part>(renderers.Length);
        foreach (MeshRenderer renderer in renderers)
            if (_instance.TryMakePart(renderer, out Part part)) parts.Add(part);
        if (parts.Count == 0) return null;

        Member member = new Member { Parts = parts.ToArray(), Group = group, Root = root };
        member.Renderers = new MeshRenderer[member.Parts.Length];
        for (int i = 0; i < member.Parts.Length; i++) member.Renderers[i] = member.Parts[i].Renderer;
        member.Body = root.GetComponentInParent<Rigidbody>();

        if (atRest) _instance.Queue(member);
        else _instance.StartWaiting(member);
        return member;
    }

    // ------------------------------------------------------------------ loop

    private void LateUpdate()
    {
        CheckWaiting();
        AssignQueued();
        RebuildDirty();
        UpdateLods();
    }

    /// <summary> Re-picks every cell's LOD after the player's detail setting changes. </summary>
    private void ApplyDetail()
    {
        foreach (Cell cell in _cells.Values) cell.Lod = -1;
        UpdateLods();
    }

    /// <summary> Picks each cell's LOD from camera distance, never finer than the player setting. </summary>
    private void UpdateLods()
    {
        Transform eye = lodCamera != null ? lodCamera : Camera.main != null ? Camera.main.transform : null;
        if (eye == null) return;

        Vector3 eyePosition = eye.position;
        int minLod = ItemDetail.Lod;
        foreach (Cell cell in _cells.Values)
        {
            if (!cell.Renderer.enabled || cell.Levels <= 1) continue;
            float distance = Mathf.Sqrt(cell.Renderer.bounds.SqrDistance(eyePosition));
            int lod = ItemDetail.LodEnabled ? Mathf.Min(Mathf.Max(PickLod(distance, cell.Lod), minLod), cell.Levels - 1) : 0;
            if (lod == cell.Lod) continue;
            cell.Lod = (short)lod;
            cell.Renderer.forceMeshLod = (short)lod;
        }
    }

    /// <summary> Distance LOD with hysteresis around each threshold. </summary>
    private int PickLod(float distance, int current)
    {
        int lod = 0;
        if (distance > lod1Distance + (current >= 1 ? -lodHysteresis : lodHysteresis)) lod = 1;
        if (distance > lod2Distance + (current >= 2 ? -lodHysteresis : lodHysteresis)) lod = 2;
        if (distance > lod3Distance + (current >= 3 ? -lodHysteresis : lodHysteresis)) lod = 3;
        return lod;
    }

    /// <summary> Queues detached items that stayed still long enough. </summary>
    private void CheckWaiting()
    {
        float dt = Time.deltaTime;
        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            Member m = _waiting[i];
            if (m.Root == null)
            {
                SwapRemove(_waiting, i);
                m.Waiting = false;
                continue;
            }

            Transform t = m.Root;
            t.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Vector3 scale = t.lossyScale;

            bool still = (position - m.LastPosition).sqrMagnitude < MoveEpsilonSqr
                && Quaternion.Angle(rotation, m.LastRotation) < TurnEpsilon
                && (scale - m.LastScale).sqrMagnitude < MoveEpsilonSqr
                && !DOTween.IsTweening(m.Root)
                && (m.Body == null || m.Body.isKinematic || m.Body.IsSleeping());

            m.LastPosition = position;
            m.LastRotation = rotation;
            m.LastScale = scale;
            m.StillTime = still ? m.StillTime + dt : 0f;
            if (m.StillTime < restSeconds) continue;

            SwapRemove(_waiting, i);
            m.Waiting = false;
            Queue(m);
        }
    }

    /// <summary> Puts queued items into the cell under their current bounds. </summary>
    private void AssignQueued()
    {
        for (int i = 0; i < _queued.Count; i++)
        {
            Member m = _queued[i];
            m.Queued = false;
            if (m.Root == null) continue;

            Cell cell = GetCell(Center(m));
            m.Cell = cell;
            cell.Members.Add(m);
            MarkDirty(cell);
        }
        _queued.Clear();
    }

    /// <summary> Rebuilds dirty cells; unlimited at startup, time-budgeted after. </summary>
    private void RebuildDirty()
    {
        if (_dirtyCells.Count == 0) return;

        bool startup = Time.time - _startTime < startupSeconds;
        _clock.Restart();

        int done = 0;
        while (done < _dirtyCells.Count)
        {
            Rebuild(_dirtyCells[done]);
            done++;
            if (!startup && _clock.Elapsed.TotalMilliseconds >= rebuildBudgetMs) break;
        }
        _dirtyCells.RemoveRange(0, done);
    }

    // ------------------------------------------------------------------ state changes

    private void Queue(Member m)
    {
        if (m.Queued) return;
        m.Queued = true;
        _queued.Add(m);
    }

    private void Dequeue(Member m)
    {
        if (!m.Queued) return;
        m.Queued = false;
        _queued.Remove(m);
    }

    private void StartWaiting(Member m)
    {
        SetHidden(m, false);
        if (m.Waiting) return;

        m.Waiting = true;
        m.StillTime = 0f;
        m.LastPosition = new Vector3(float.NaN, 0f, 0f); // first check never counts as still
        _waiting.Add(m);
    }

    private void StopWaiting(Member m)
    {
        if (!m.Waiting) return;
        m.Waiting = false;
        _waiting.Remove(m);
    }

    private void RemoveFromCell(Member m)
    {
        if (m.Cell == null) return;
        m.Cell.Members.Remove(m);
        MarkDirty(m.Cell);
        m.Cell = null;
    }

    private void MarkDirty(Cell cell)
    {
        if (cell.Dirty) return;
        cell.Dirty = true;
        _dirtyCells.Add(cell);
    }

    /// <summary> Rebuilds every cell, e.g. after the light probes changed. </summary>
    private void MarkAllDirty()
    {
        foreach (Cell cell in _cells.Values) MarkDirty(cell);
    }

    /// <summary> Hides or shows the parts; a visual group applies it with its own state. </summary>
    private static void SetHidden(Member m, bool hidden)
    {
        foreach (MeshRenderer renderer in m.Renderers)
        {
            if (renderer == null) continue;
            if (hidden) Hidden.Add(renderer);
            else Hidden.Remove(renderer);
            if (m.Group == null) renderer.enabled = !hidden;
        }
        if (m.Group != null) m.Group.SetMerged(m.Renderers, hidden);
    }

    // ------------------------------------------------------------------ building

    /// <summary> True for readable renderers whose every submesh has a chunk material. </summary>
    private bool TryMakePart(MeshRenderer renderer, out Part part)
    {
        part = null;
        if (renderer == null || chunkMaterial == null) return false;

        MeshFilter filter = renderer.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return false;

        Mesh mesh = ItemDetail.Original(filter.sharedMesh); // cells build from the source mesh
        if (!mesh.isReadable || !Geometry(mesh).isReadable) return false;

        Material[] materials = renderer.sharedMaterials;
        if (materials.Length != mesh.subMeshCount) return false;

        var chunks = new Material[materials.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            chunks[i] = GetChunkMaterial(materials[i]);
            if (chunks[i] == null) return false;
        }

        part = new Part { Renderer = renderer, Mesh = mesh, Chunks = chunks };
        return true;
    }

    /// <summary> Chunk material for a source; generated once per opaque Lit material. </summary>
    private Material GetChunkMaterial(Material source)
    {
        if (source == null) return null;
        if (source == sourceMaterial) return chunkMaterial;
        if (_chunkMaterials.TryGetValue(source, out Material cached)) return cached;
        if (!mergeLitMaterials || !IsOpaqueLit(source)) return null;

        Material chunk = new Material(chunkMaterial) { name = source.name + " (Chunk)" };
        chunk.SetTexture(BaseMapId, source.GetTexture(BaseMapId));
        chunk.SetTextureScale(BaseMapId, source.GetTextureScale(BaseMapId));
        chunk.SetTextureOffset(BaseMapId, source.GetTextureOffset(BaseMapId));
        chunk.SetColor(BaseColorId, source.GetColor(BaseColorId));

        bool glow = source.shader.name == LitShader && source.IsKeywordEnabled("_EMISSION"); // Baked Lit has no glow
        chunk.SetTexture(EmissionMapId, glow ? source.GetTexture(EmissionMapId) : null);
        chunk.SetColor(EmissionColorId, glow ? source.GetColor(EmissionColorId) : Color.black);

        _chunkMaterials.Add(source, chunk);
        return chunk;
    }

    /// <summary> Opaque, non-cutout URP Lit or Baked Lit; cells drop metallic and reflections. </summary>
    private static bool IsOpaqueLit(Material material)
    {
        return material.shader != null && (material.shader.name == LitShader || material.shader.name == BakedLitShader)
            && material.renderQueue < 2500
            && (!material.HasProperty("_Surface") || material.GetFloat("_Surface") == 0f)
            && (!material.HasProperty("_AlphaClip") || material.GetFloat("_AlphaClip") == 0f);
    }

    /// <summary> Mesh holding the vertices; the baked LOD mesh when one exists. </summary>
    private static Mesh Geometry(Mesh mesh)
    {
        ItemLodSet.Entry entry = null;
        if (ItemDetail.LodSet != null) ItemDetail.LodSet.TryGet(mesh, out entry);
        return entry != null && entry.bakedMesh != null ? entry.bakedMesh : mesh;
    }

    /// <summary> Center of all parts' bounds; picks the item's cell. </summary>
    private static Vector3 Center(Member m)
    {
        Bounds bounds = default;
        bool any = false;
        foreach (Part part in m.Parts)
        {
            if (part.Renderer == null) continue;
            if (!any) bounds = part.Renderer.bounds;
            else bounds.Encapsulate(part.Renderer.bounds);
            any = true;
        }
        return any ? bounds.center : m.Root.position;
    }

    private Cell GetCell(Vector3 position)
    {
        Vector3Int key = new Vector3Int(Mathf.FloorToInt(position.x / cellSize), 0, Mathf.FloorToInt(position.z / cellSize));
        if (_cells.TryGetValue(key, out Cell cell)) return cell;

        GameObject go = new GameObject($"ItemCell {key.x},{key.z}");
        go.transform.SetParent(transform, false);
        go.transform.position = new Vector3((key.x + 0.5f) * cellSize, 0f, (key.z + 0.5f) * cellSize);

        cell = new Cell { Key = key, Filter = go.AddComponent<MeshFilter>(), Renderer = go.AddComponent<MeshRenderer>() };
        cell.Renderer.sharedMaterial = chunkMaterial;
        cell.Renderer.shadowCastingMode = ShadowCastingMode.Off;
        cell.Renderer.receiveShadows = false;
        cell.Renderer.lightProbeUsage = LightProbeUsage.Off;
        cell.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        cell.Renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
        cell.Renderer.enabled = false;

        _cells.Add(key, cell);
        return cell;
    }

    private SourceMesh GetSource(Mesh mesh)
    {
        if (_sources.TryGetValue(mesh, out SourceMesh source)) return source;

        ItemLodSet.Entry entry = null;
        if (ItemDetail.LodSet != null) ItemDetail.LodSet.TryGet(mesh, out entry);
        Mesh geometry = entry != null && entry.bakedMesh != null ? entry.bakedMesh : mesh;
        Vector3[] positions = geometry.vertices;
        Vector3[] normals = geometry.normals;
        Vector2[] uvs = geometry.uv;
        int count = positions.Length;

        source = new SourceMesh
        {
            Positions = positions,
            U = new ushort[count],
            V = new ushort[count],
            Lods = new int[mesh.subMeshCount][][],
            NormalIndex = new int[count],
        };

        // LOD0 is the mesh itself; empty baked levels reuse the level above.
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            source.Lods[sub] = new int[LodCount][];
            for (int level = 0; level < LodCount; level++)
                source.Lods[sub][level] = entry != null ? entry.GetIndices(level, sub)
                    : mesh.GetIndices(sub, Mathf.Min(level, mesh.lodCount - 1), true); // native LODs, or LOD0 repeated
        }

        // Group identical normals so lighting is evaluated once per facet direction.
        var unique = new List<Vector3>();
        var lookup = new Dictionary<Vector3Int, int>();
        for (int i = 0; i < count; i++)
        {
            Vector3 n = i < normals.Length ? normals[i] : Vector3.up;
            Vector3Int key = Vector3Int.RoundToInt(n * 1000f);
            if (!lookup.TryGetValue(key, out int index))
            {
                index = unique.Count;
                unique.Add(n);
                lookup.Add(key, index);
            }
            source.NormalIndex[i] = index;

            Vector2 uv = i < uvs.Length ? uvs[i] : Vector2.zero;
            source.U[i] = Mathf.FloatToHalf(uv.x);
            source.V[i] = Mathf.FloatToHalf(uv.y);
        }

        source.UniqueNormals = unique.ToArray();
        source.WorldNormals = new Vector3[unique.Count];
        source.Lights = new Color[unique.Count];
        source.Encoded = new Color32[unique.Count];

        _sources.Add(mesh, source);
        return source;
    }

    /// <summary> Rebuilds a cell mesh with baked probe lighting; one submesh per chunk material. </summary>
    private void Rebuild(Cell cell)
    {
        cell.Dirty = false;
        List<Member> members = cell.Members;
        members.RemoveAll(m => m.Root == null);

        int levels = ItemDetail.LodSet != null ? LodCount : 1;

        // Collect the cell's materials and each one's per-LOD index counts.
        _cellMaterials.Clear();
        int vertexCount = 0;
        foreach (Member m in members)
            foreach (Part p in m.Parts)
            {
                if (p.Renderer == null) continue;
                vertexCount += GetSource(p.Mesh).Positions.Length;
                foreach (Material chunk in p.Chunks)
                    if (!_cellMaterials.Contains(chunk)) _cellMaterials.Add(chunk);
            }

        int materialCount = _cellMaterials.Count;
        if (_lodCounts.Length < materialCount * LodCount) _lodCounts = new int[materialCount * LodCount];
        System.Array.Clear(_lodCounts, 0, materialCount * LodCount);
        int indexCount = 0;
        foreach (Member m in members)
            foreach (Part p in m.Parts)
            {
                if (p.Renderer == null) continue;
                SourceMesh s = GetSource(p.Mesh);
                for (int sub = 0; sub < p.Chunks.Length; sub++)
                {
                    int mi = _cellMaterials.IndexOf(p.Chunks[sub]);
                    for (int l = 0; l < levels; l++)
                    {
                        _lodCounts[mi * LodCount + l] += s.Lods[sub][l].Length;
                        indexCount += s.Lods[sub][l].Length;
                    }
                }
            }

        Mesh old = cell.Filter.sharedMesh;
        if (vertexCount == 0)
        {
            cell.Filter.sharedMesh = null;
            cell.Renderer.enabled = false;
            if (old != null) Destroy(old);
            return;
        }

        bool wide = vertexCount > 65535;
        if (_vertexBuffer.Length < vertexCount) _vertexBuffer = new Vertex[vertexCount];
        if (wide && _intIndices.Length < indexCount) _intIndices = new int[indexCount];
        if (!wide && _shortIndices.Length < indexCount) _shortIndices = new ushort[indexCount];

        Vector3 origin = cell.Filter.transform.position;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        int v = 0;
        _bases.Clear();
        foreach (Member m in members)
            foreach (Part p in m.Parts)
            {
                if (p.Renderer == null) continue;
                SourceMesh s = GetSource(p.Mesh);
                Matrix4x4 mx = p.Renderer.localToWorldMatrix;
                _bases.Add(v);

                // Light once per facet normal.
                for (int k = 0; k < s.UniqueNormals.Length; k++) s.WorldNormals[k] = mx.MultiplyVector(s.UniqueNormals[k]).normalized;
                SampleLight(p.Renderer).Evaluate(s.WorldNormals, s.Lights);
                for (int k = 0; k < s.Lights.Length; k++)
                {
                    Color c = s.Lights[k];
                    s.Encoded[k] = new Color32(Encode(c.r), Encode(c.g), Encode(c.b), 255);
                }

                float ox = mx.m03 - origin.x, oy = mx.m13 - origin.y, oz = mx.m23 - origin.z;
                Vector3[] positions = s.Positions;
                int count = positions.Length;
                for (int i = 0; i < count; i++)
                {
                    Vector3 pos = positions[i];
                    float x = mx.m00 * pos.x + mx.m01 * pos.y + mx.m02 * pos.z + ox;
                    float y = mx.m10 * pos.x + mx.m11 * pos.y + mx.m12 * pos.z + oy;
                    float z = mx.m20 * pos.x + mx.m21 * pos.y + mx.m22 * pos.z + oz;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;

                    ref Vertex vert = ref _vertexBuffer[v + i];
                    vert.Position = new Vector3(x, y, z);
                    vert.Light = s.Encoded[s.NormalIndex[i]];
                    vert.U = s.U[i];
                    vert.V = s.V[i];
                }

                v += count;
            }

        // Each material's submesh holds its LODs back to back: [LOD0][LOD1][LOD2].
        var starts = new int[materialCount];
        int idx = 0;
        for (int mi = 0; mi < materialCount; mi++)
        {
            starts[mi] = idx;
            Material material = _cellMaterials[mi];
            for (int l = 0; l < levels; l++)
            {
                int partIndex = 0;
                foreach (Member m in members)
                    foreach (Part p in m.Parts)
                    {
                        if (p.Renderer == null) continue;
                        int b = _bases[partIndex++];
                        SourceMesh s = GetSource(p.Mesh);
                        for (int sub = 0; sub < p.Chunks.Length; sub++)
                        {
                            if (p.Chunks[sub] != material) continue;
                            int[] src = s.Lods[sub][l];
                            if (wide) for (int i = 0; i < src.Length; i++) _intIndices[idx + i] = src[i] + b;
                            else for (int i = 0; i < src.Length; i++) _shortIndices[idx + i] = (ushort)(src[i] + b);
                            idx += src.Length;
                        }
                    }
            }
        }

        const MeshUpdateFlags Flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds
            | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;

        Mesh mesh = new Mesh { name = $"ItemCell {cell.Key.x},{cell.Key.z}" };
        mesh.SetVertexBufferParams(vertexCount, _layout);
        mesh.SetVertexBufferData(_vertexBuffer, 0, 0, vertexCount, 0, Flags);
        mesh.SetIndexBufferParams(indexCount, wide ? IndexFormat.UInt32 : IndexFormat.UInt16);
        if (wide) mesh.SetIndexBufferData(_intIndices, 0, 0, indexCount, Flags);
        else mesh.SetIndexBufferData(_shortIndices, 0, 0, indexCount, Flags);

        Vector3 min = new Vector3(minX, minY, minZ), max = new Vector3(maxX, maxY, maxZ);
        Bounds bounds = new Bounds((min + max) * 0.5f, max - min);
        mesh.subMeshCount = materialCount;
        for (int mi = 0; mi < materialCount; mi++)
        {
            int length = 0;
            for (int l = 0; l < levels; l++) length += _lodCounts[mi * LodCount + l];
            mesh.SetSubMesh(mi, new SubMeshDescriptor(starts[mi], length) { bounds = bounds, vertexCount = vertexCount }, Flags);
        }
        if (levels > 1)
        {
            mesh.lodCount = levels;
            var ranges = new MeshLodRange[levels];
            for (int mi = 0; mi < materialCount; mi++)
            {
                uint start = 0; // relative to the submesh start
                for (int l = 0; l < levels; l++)
                {
                    uint lodLength = (uint)_lodCounts[mi * LodCount + l];
                    ranges[l] = new MeshLodRange { indexStart = start, indexCount = lodLength };
                    start += lodLength;
                }
                mesh.SetLods(ranges, mi, Flags);
            }
        }
        mesh.bounds = bounds;
        mesh.UploadMeshData(true); // frees the CPU copy

        cell.Filter.sharedMesh = mesh;
        cell.Renderer.sharedMaterials = _cellMaterials.ToArray();
        cell.Levels = levels;
        cell.Lod = -1; // UpdateLods picks it this frame
        cell.Renderer.forceMeshLod = (short)(levels > 1 ? Mathf.Min(ItemDetail.Lod, levels - 1) : 0);
        cell.Renderer.enabled = true;
        if (old != null) Destroy(old);

        // Hide merged items only once the cell draws them.
        foreach (Member m in members) SetHidden(m, true);
    }

    /// <summary> Probe lighting at the renderer, as Baked Lit samples it. </summary>
    private static SphericalHarmonicsL2 SampleLight(Renderer renderer)
    {
        LightProbes.GetInterpolatedProbe(renderer.bounds.center, renderer, out SphericalHarmonicsL2 sh);
        return sh;
    }

    private static byte Encode(float light) => (byte)Mathf.Clamp(Mathf.RoundToInt(light / LightScale * 255f), 0, 255);

    private static void SwapRemove(List<Member> list, int index)
    {
        int last = list.Count - 1;
        list[index] = list[last];
        list.RemoveAt(last);
    }
}
