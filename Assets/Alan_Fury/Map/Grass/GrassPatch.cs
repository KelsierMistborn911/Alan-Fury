using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

/// <summary>
/// Простой участок травы для любой сцены.
/// Строит один меш, шейдер Nature/GrassField разворачивает его в травинки.
/// Не зависит от HeightMap / MapGrid / NaturePlacement.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class GrassPatch : MonoBehaviour
{
    [Header("Участок")]
    public float sizeX = 40f;
    public float sizeZ = 40f;
    [Tooltip("Размер клетки в метрах.")]
    public float cellSize = 4f;
    [Range(1, 4)] public int subdiv = 2;
    public float heightOffset = 0.03f;

    [Header("Материал")]
    [Tooltip("Материал с шейдером Nature/GrassField.")]
    public Material grassMaterial;

    [Header("Вид")]
    [Range(1f, 32f)] public float centerBlades = 12f;
    [Range(0f, 16f)] public float edgeBlades = 2f;
    public float bladeHeight = 0.85f;
    public float bladeWidth = 0.09f;
    public float windStrength = 0.14f;
    public float windSpeed = 0.35f;

    [Header("Вокруг деревьев (необязательно)")]
    public bool avoidTrees = true;
    [Tooltip("Имена объектов, которые считаем деревьями. Пусто = все дети с MeshRenderer.")]
    public string treeNameContains = "Tree";
    public float trunkRadius = 5f;
    public float fadeMeters = 18f;

    [Header("Рендер")]
    public ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
    public bool receiveShadows = true;

    private Mesh _mesh;
    private MeshRenderer _renderer;
    private MaterialPropertyBlock _block;

    private void Awake()
    {
        _renderer = GetComponent<MeshRenderer>();
        if (grassMaterial != null)
            _renderer.sharedMaterial = grassMaterial;
    }

    [ContextMenu("Build")]
    public void Build()
    {
        Clear();
        if (grassMaterial == null)
        {
            Debug.LogWarning("GrassPatch: нет grassMaterial.");
            return;
        }

        int cellsX = Mathf.Max(1, Mathf.CeilToInt(sizeX / cellSize));
        int cellsZ = Mathf.Max(1, Mathf.CeilToInt(sizeZ / cellSize));
        int div = Mathf.Max(1, subdiv);

        var verts = new List<Vector3>(cellsX * cellsZ * div * div * 4);
        var tris = new List<int>(cellsX * cellsZ * div * div * 6);
        var cols = new List<Color>(verts.Capacity);
        var uvs = new List<Vector2>(verts.Capacity);

        var trees = CollectTrees();

        float halfX = sizeX * 0.5f;
        float halfZ = sizeZ * 0.5f;

        for (int x = 0; x < cellsX; x++)
        {
            for (int z = 0; z < cellsZ; z++)
            {
                float ox = -halfX + x * cellSize;
                float oz = -halfZ + z * cellSize;
                float sx = Mathf.Min(cellSize, sizeX - x * cellSize);
                float sz = Mathf.Min(cellSize, sizeZ - z * cellSize);

                for (int ix = 0; ix < div; ix++)
                {
                    for (int iz = 0; iz < div; iz++)
                    {
                        float u0 = ix / (float)div;
                        float v0 = iz / (float)div;
                        float u1 = (ix + 1) / (float)div;
                        float v1 = (iz + 1) / (float)div;

                        Vector3 p00 = new Vector3(ox + u0 * sx, heightOffset, oz + v0 * sz);
                        Vector3 p10 = new Vector3(ox + u1 * sx, heightOffset, oz + v0 * sz);
                        Vector3 p11 = new Vector3(ox + u1 * sx, heightOffset, oz + v1 * sz);
                        Vector3 p01 = new Vector3(ox + u0 * sx, heightOffset, oz + v1 * sz);

                        float o00 = OpenAt(p00, trees);
                        float o10 = OpenAt(p10, trees);
                        float o11 = OpenAt(p11, trees);
                        float o01 = OpenAt(p01, trees);

                        int i = verts.Count;
                        verts.Add(p00);
                        verts.Add(p10);
                        verts.Add(p11);
                        verts.Add(p01);
                        uvs.Add(new Vector2(u0, v0));
                        uvs.Add(new Vector2(u1, v0));
                        uvs.Add(new Vector2(u1, v1));
                        uvs.Add(new Vector2(u0, v1));
                        cols.Add(new Color(o00, 0, 0, 1));
                        cols.Add(new Color(o10, 0, 0, 1));
                        cols.Add(new Color(o11, 0, 0, 1));
                        cols.Add(new Color(o01, 0, 0, 1));
                        tris.Add(i);
                        tris.Add(i + 3);
                        tris.Add(i + 2);
                        tris.Add(i);
                        tris.Add(i + 2);
                        tris.Add(i + 1);
                    }
                }
            }
        }

        if (verts.Count == 0) return;

        _mesh = new Mesh();
        _mesh.name = "GrassPatch";
        _mesh.indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _mesh.SetVertices(verts);
        _mesh.SetTriangles(tris, 0);
        _mesh.SetUVs(0, uvs);
        _mesh.SetColors(cols);
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        var mf = GetComponent<MeshFilter>();
        mf.sharedMesh = _mesh;

        _renderer = GetComponent<MeshRenderer>();
        _renderer.sharedMaterial = grassMaterial;
        _renderer.shadowCastingMode = shadowCasting;
        _renderer.receiveShadows = receiveShadows;

        ApplyBlock();
        Debug.Log($"GrassPatch: {verts.Count} вершин, деревьев учтено {trees.Count}");
    }

    [ContextMenu("Clear")]
    public void Clear()
    {
        var mf = GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null && mf.sharedMesh.name == "GrassPatch")
        {
            if (Application.isPlaying) Destroy(mf.sharedMesh);
            else DestroyImmediate(mf.sharedMesh);
            mf.sharedMesh = null;
        }
        _mesh = null;
    }

    private void ApplyBlock()
    {
        if (_block == null) _block = new MaterialPropertyBlock();
        _block.SetFloat("_CenterDensity", centerBlades);
        _block.SetFloat("_EdgeDensity", edgeBlades);
        _block.SetFloat("_BladeHeight", bladeHeight);
        _block.SetFloat("_BladeWidth", bladeWidth);
        _block.SetFloat("_WindStrength", windStrength);
        _block.SetFloat("_WindSpeed", windSpeed);
        _block.SetFloat("_DrawRadius", 999f);
        _block.SetVector("_PlayerPos", Vector3.zero);
        if (_renderer != null)
            _renderer.SetPropertyBlock(_block);
    }

    private List<Vector2> CollectTrees()
    {
        var list = new List<Vector2>();
        if (!avoidTrees) return list;

        var renderers = FindObjectsOfType<MeshRenderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null || r.gameObject == gameObject) continue;
            if (!string.IsNullOrEmpty(treeNameContains) &&
                r.gameObject.name.IndexOf(treeNameContains, System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            Vector3 p = r.transform.position;
            list.Add(new Vector2(p.x, p.z));
        }
        return list;
    }

    private float OpenAt(Vector3 localPos, List<Vector2> trees)
    {
        if (trees.Count == 0) return 1f;
        Vector3 w = transform.TransformPoint(localPos);
        float best = float.MaxValue;
        for (int i = 0; i < trees.Count; i++)
        {
            float dx = w.x - trees[i].x;
            float dz = w.z - trees[i].y;
            float d2 = dx * dx + dz * dz;
            if (d2 < best) best = d2;
        }
        float dist = Mathf.Sqrt(best);
        float inner = Mathf.Max(0.2f, trunkRadius);
        float fade = Mathf.Max(0.2f, fadeMeters);
        return Mathf.Clamp01((dist - inner) / fade);
    }

    private void OnValidate()
    {
        sizeX = Mathf.Max(1f, sizeX);
        sizeZ = Mathf.Max(1f, sizeZ);
        cellSize = Mathf.Max(0.5f, cellSize);
    }
}
