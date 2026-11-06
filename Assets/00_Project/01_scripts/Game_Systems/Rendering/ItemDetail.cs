using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary> Player-selectable item detail; each level maps to one mesh LOD. </summary>
public enum ItemDetailLevel { High = 0, Medium = 1, Low = 2 }

/// <summary> Saved graphics and item detail setting; swaps item renderers to LOD-capable meshes. </summary>
public static class ItemDetail
{
    private const string PrefKey = "Item_Detail";
    private const ItemDetailLevel DefaultLevel = ItemDetailLevel.Medium;
    private const string LodSetPath = "ItemLodSet"; // Resources path

    private static readonly Dictionary<Mesh, Mesh> LodMeshes = new Dictionary<Mesh, Mesh>();
    private static readonly Dictionary<Mesh, Mesh> Originals = new Dictionary<Mesh, Mesh>();
    private static ItemLodSet _lodSet;
    private static bool _lodSetLoaded;

    /// <summary> Fired after the level changes. </summary>
    public static event Action OnChanged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        LodMeshes.Clear();
        Originals.Clear();
        _lodSet = null;
        _lodSetLoaded = false;
        OnChanged = null;
    }

    /// <summary> Saved level, Medium when never chosen. </summary>
    public static ItemDetailLevel Current
    {
        get
        {
            int value = PlayerPrefs.GetInt(PrefKey, (int)DefaultLevel);
            return Enum.IsDefined(typeof(ItemDetailLevel), value) ? (ItemDetailLevel)value : DefaultLevel;
        }
    }

    /// <summary> Master LOD switch; false keeps every item mesh on LOD0. </summary>
    public const bool LodEnabled = false;

    /// <summary> Mesh LOD index for the current level; 0 without baked LODs or when disabled. </summary>
    public static int Lod => LodEnabled && LodSet != null && LodSet.Count > 0 ? (int)Current : 0;

    /// <summary> Baked item LODs from Resources, or null if not baked. </summary>
    public static ItemLodSet LodSet
    {
        get
        {
            if (!_lodSetLoaded)
            {
                _lodSet = Resources.Load<ItemLodSet>(LodSetPath);
                _lodSetLoaded = true;
            }
            return _lodSet;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ApplySavedQuality() => ApplyQuality(Current);

    private static void ApplyQuality(ItemDetailLevel level)
    {
        int index = Array.IndexOf(QualitySettings.names, level.ToString());
        if (index < 0)
        {
            Debug.LogWarning($"Graphics quality preset '{level}' is missing.");
            return;
        }

        QualitySettings.SetQualityLevel(index, true);
    }

    /// <summary> Saves the level and applies graphics quality and item detail immediately. </summary>
    public static void Set(ItemDetailLevel level)
    {
        if (!Enum.IsDefined(typeof(ItemDetailLevel), level)) level = DefaultLevel;
        PlayerPrefs.SetInt(PrefKey, (int)level);
        PlayerPrefs.Save();

        ApplyQuality(level);

        short lod = (short)Lod;
        foreach (ItemVisualGroup group in ItemVisualGroup.All) group.RefreshDetail(lod);
        foreach (WorldItemBase item in WorldItemBase.All)
            if (item.GetComponent<ItemVisualGroup>() == null) item.RefreshDetail(lod);
        foreach (PlacedOnShelfItemBase item in UnityEngine.Object.FindObjectsByType<PlacedOnShelfItemBase>(FindObjectsSortMode.None))
            if (item.GetComponent<ItemVisualGroup>() == null) item.RefreshDetail(lod);

        OnChanged?.Invoke();
    }

    /// <summary> Swaps the renderer to its LOD-capable mesh and forces the current level. </summary>
    public static void ApplyTo(MeshRenderer renderer)
        => ApplyTo(renderer, Lod);

    /// <summary>Applies a detail level already resolved for a group of renderers.</summary>
    public static void ApplyTo(MeshRenderer renderer, int lod)
    {
        if (renderer == null) return;

        MeshFilter filter = renderer.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return;

        Mesh lodMesh = LodSet != null ? GetLodMesh(filter.sharedMesh) : null;
        if (lodMesh != null) filter.sharedMesh = lodMesh;
        renderer.forceMeshLod = (short)Mathf.Clamp(lod, 0, filter.sharedMesh.lodCount - 1);
    }

    /// <summary> Source mesh behind a LOD copy; the mesh itself otherwise. </summary>
    public static Mesh Original(Mesh mesh)
    {
        if (mesh == null) return null;
        if (Originals.TryGetValue(mesh, out Mesh original)) return original;
        return LodSet != null && LodSet.TryGet(mesh, out var entry) ? entry.mesh : mesh;
    }

    /// <summary> Copy of the source mesh carrying LOD0-2 as Mesh LOD ranges; cached. </summary>
    private static Mesh GetLodMesh(Mesh mesh)
    {
        if (Originals.ContainsKey(mesh)) return mesh; // already a LOD copy
        if (LodMeshes.TryGetValue(mesh, out Mesh cached)) return cached;
        if (!LodSet.TryGet(mesh, out ItemLodSet.Entry entry)) return null;
        if (entry.bakedMesh != null)
        {
            LodMeshes[entry.mesh] = entry.bakedMesh;
            Originals[entry.bakedMesh] = entry.mesh;
            return entry.bakedMesh;
        }
        if (!mesh.isReadable || mesh.subMeshCount != 1) return null;

        int[] lod0 = mesh.GetIndices(0);
        int[] lod1 = entry.lod1 != null && entry.lod1.Length > 0 ? entry.lod1 : lod0;
        int[] lod2 = entry.lod2 != null && entry.lod2.Length > 0 ? entry.lod2 : lod1;

        int[] all = new int[lod0.Length + lod1.Length + lod2.Length];
        lod0.CopyTo(all, 0);
        lod1.CopyTo(all, lod0.Length);
        lod2.CopyTo(all, lod0.Length + lod1.Length);

        Mesh copy = UnityEngine.Object.Instantiate(mesh);
        copy.name = mesh.name + " (LOD)";
        copy.SetIndices(all, MeshTopology.Triangles, 0, false);
        copy.lodCount = 3;
        copy.SetLods(new[]
        {
            new MeshLodRange { indexStart = 0, indexCount = (uint)lod0.Length },
            new MeshLodRange { indexStart = (uint)lod0.Length, indexCount = (uint)lod1.Length },
            new MeshLodRange { indexStart = (uint)(lod0.Length + lod1.Length), indexCount = (uint)lod2.Length },
        }, 0, MeshUpdateFlags.Default);
        copy.bounds = mesh.bounds;

        LodMeshes.Add(mesh, copy);
        Originals.Add(copy, mesh);
        return copy;
    }
}
