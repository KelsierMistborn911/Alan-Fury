using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Спавн 2D-спрайтов (кусты, трава) и 3D-мешей (грибы) на клетках суши через GPU Instancing.
/// - Зоны роста через Perlin noise (пятна зарослей)
/// - Несколько штук на клетку
/// - Рисуются только секторы в радиусе от игрока
/// - Собираемые типы (collectible) получают триггер-объекты для подбора в инвентарь
/// Спрайты стоят с фиксированным поворотом (spriteEuler), 3D-меши — случайный поворот по Y.
/// </summary>
public class SpriteVegetationPlacer : MonoBehaviour
{
    [System.Serializable]
    public class VegetationType
    {
        public string name;
        [Tooltip("Спрайт (для 2D). Игнорируется, если задан mesh3D.")]
        public Sprite sprite;
        [Tooltip("3D-меш (грибы и т.п.). Если задан — рисуется вместо спрайта,материал должен быть с текстурой меша.")]
        public Mesh mesh3D;
        [Tooltip("Материал с Enable GPU Instancing = true. Для спрайтов текстура подставится из спрайта.")]
        public Material material;

        [Header("Зоны роста (Perlin)")]
        [Tooltip("Масштаб пятен: меньше = крупнее заросли")]
        public float noiseScale = 0.08f;
        [Tooltip("Порог: выше = пятна реже и меньше")]
        [Range(0f, 1f)] public float noiseThreshold = 0.55f;

        [Header("Плотность")]
        [Tooltip("Шанс заполнить клетку внутри зоны (0..1)")]
        [Range(0f, 1f)] public float density = 0.6f;
        [Tooltip("Штук на клетку (мин/макс)")]
        public int minPerCell = 1;
        public int maxPerCell = 3;

        [Header("Внешний вид")]
        public float minScale = 0.8f;
        public float maxScale = 1.2f;
        public float heightOffset = 0f;
        [Tooltip("Отбрасывать тени")]
        public bool castShadows = false;

        [Header("Сбор (грибы и т.п.)")]
        [Tooltip("Можно собирать: у каждого экземпляра появится триггер подбора")]
        public bool collectible = false;
        [Tooltip("Что падает в инвентарь")]
        public ItemData itemData;
        [Tooltip("Сколько штук за сбор")]
        public int itemCount = 1;
        [Tooltip("Радиус триггера подбора")]
        public float pickupRadius = 1f;

        // Служебное
        [HideInInspector] public Mesh mesh;
        [HideInInspector] public MaterialPropertyBlock propertyBlock;
        [HideInInspector] public Dictionary<Vector2Int, Matrix4x4[][]> sectors;      // сектор -> батчи по <=1023
        [HideInInspector] public Dictionary<Vector2Int, List<Matrix4x4>> sectorLists; // только для collectible (пересборка при сборе)
        [HideInInspector] public Vector2 noiseOffset;
    }

    [Header("Источники")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder terrainBuilder;
    public Transform player;

    [Header("Типы растительности")]
    public List<VegetationType> types = new List<VegetationType>();

    [Header("Общие настройки")]
    [Tooltip("Клетка считается сушей, если высота выше этого значения")]
    public float waterLevel = 0f;
    [Tooltip("Поворот квада спрайта (подогнать под камеру)")]
    public Vector3 spriteEuler = new Vector3(0f, 0f, 0f);
    [Tooltip("Отступ от края карты в клетках")]
    public int borderCells = 2;

    [Header("Секторы и прорисовка")]
    [Tooltip("Размер сектора в клетках")]
    public int sectorSize = 16;
    [Tooltip("Радиус прорисовки вокруг игрока, мировые единицы")]
    public float drawRadius = 60f;

    [Header("Сид")]
    public bool randomSeed = true;
    public int seed = 42;

    private bool isGenerated = false;
    private Transform pickupsRoot; // контейнер триггеров сбора
    private const int BatchSize = 1023;

    // =================== Публичный API ===================

    [ContextMenu("Разместить растительность")]
    public void PlaceAll()
    {
        if (!Validate()) return;

        DestroyPickupsRoot();

        if (randomSeed) seed = Random.Range(0, 100000);
        Random.InitState(seed);

        float ts = terrainBuilder.TileSize;
        int w = heightSource.width;
        int d = heightSource.depth;
        Vector3 origin = new Vector3(-w * ts / 2f, 0, -d * ts / 2f);
        Quaternion spriteRot = Quaternion.Euler(spriteEuler);

        for (int ti = 0; ti < types.Count; ti++)
        {
            var t = types[ti];
            bool is3D = t.mesh3D != null;

            if (!is3D && t.sprite == null || t.material == null)
            {
                Debug.LogWarning($"SpriteVegetationPlacer: '{t.name}' — нет спрайта/меша или материала.");
                continue;
            }

            t.mesh = is3D ? t.mesh3D : BuildQuadFromSprite(t.sprite);
            t.propertyBlock = new MaterialPropertyBlock();
            if (!is3D)
            {
                t.propertyBlock.SetTexture("_BaseMap", t.sprite.texture);
                t.propertyBlock.SetTexture("_MainTex", t.sprite.texture);
            }
            t.noiseOffset = new Vector2(Random.Range(0f, 9999f), Random.Range(0f, 9999f));

            var temp = new Dictionary<Vector2Int, List<Matrix4x4>>();
            int total = PlaceType(t, ti, w, d, ts, origin, spriteRot, is3D, temp);

            // Списки -> готовые батчи, чтобы не копировать каждый кадр
            t.sectors = new Dictionary<Vector2Int, Matrix4x4[][]>();
            foreach (var kv in temp)
                t.sectors[kv.Key] = SplitToBatches(kv.Value);

            // Для собираемых храним и списки — из них удаляем при сборе
            t.sectorLists = t.collectible ? temp : null;

            Debug.Log($"SpriteVegetationPlacer: '{t.name}' — {total} шт, секторов: {t.sectors.Count}");
        }

        isGenerated = true;
    }

    [ContextMenu("Очистить")]
    public void ClearAll()
    {
        foreach (var t in types)
        {
            t.sectors = null;
            t.sectorLists = null;
        }
        DestroyPickupsRoot();
        isGenerated = false;
    }

    /// <summary>
    /// Убрать один экземпляр (сбор). Ищет по позиции в списке сектора и пересобирает его батчи.
    /// </summary>
    public bool RemoveInstance(int typeIndex, Vector2Int sector, Vector3 worldPos)
    {
        if (typeIndex < 0 || typeIndex >= types.Count) return false;
        var t = types[typeIndex];
        if (t.sectorLists == null || !t.sectorLists.TryGetValue(sector, out var list)) return false;

        for (int i = 0; i < list.Count; i++)
        {
            Vector3 p = list[i].GetColumn(3);
            if ((p - worldPos).sqrMagnitude < 0.0001f)
            {
                list.RemoveAt(i);
                t.sectors[sector] = SplitToBatches(list);
                return true;
            }
        }
        return false;
    }

    // =================== Генерация ===================

    private int PlaceType(VegetationType t, int typeIndex, int w, int d, float ts, Vector3 origin,
                          Quaternion spriteRot, bool is3D, Dictionary<Vector2Int, List<Matrix4x4>> temp)
    {
        int total = 0;

        for (int x = borderCells; x < w - borderCells; x++)
        {
            for (int z = borderCells; z < d - borderCells; z++)
            {
                float h = heightSource.GetHeight(x, z);
                if (h <= waterLevel) continue; // только суша

                // Зона роста
                float n = Mathf.PerlinNoise((x + t.noiseOffset.x) * t.noiseScale,
                                            (z + t.noiseOffset.y) * t.noiseScale);
                if (n < t.noiseThreshold) continue;

                if (Random.value > t.density) continue;

                int count = Random.Range(t.minPerCell, t.maxPerCell + 1);
                Vector2Int sector = new Vector2Int(x / sectorSize, z / sectorSize);
                if (!temp.TryGetValue(sector, out var list))
                {
                    list = new List<Matrix4x4>();
                    temp[sector] = list;
                }

                for (int i = 0; i < count; i++)
                {
                    // Случайная точка внутри клетки, с отступом чтобы не вылезать за край
                    float margin = ts * 0.15f;
                    float px = origin.x + x * ts + Random.Range(margin, ts - margin);
                    float pz = origin.z + z * ts + Random.Range(margin, ts - margin);
                    float scale = Random.Range(t.minScale, t.maxScale);
                    Vector3 pos = new Vector3(px, h + t.heightOffset, pz);
                    Quaternion rot = is3D ? Quaternion.Euler(0, Random.Range(0f, 360f), 0) : spriteRot;

                    list.Add(Matrix4x4.TRS(pos, rot, Vector3.one * scale));
                    total++;

                    if (t.collectible)
                        CreatePickup(t, typeIndex, sector, pos);
                }
            }
        }
        return total;
    }

    private void CreatePickup(VegetationType t, int typeIndex, Vector2Int sector, Vector3 pos)
    {
        if (pickupsRoot == null)
        {
            pickupsRoot = new GameObject("Pickups").transform;
            pickupsRoot.SetParent(transform, false);
        }

        var go = new GameObject($"Pickup_{t.name}");
        go.transform.SetParent(pickupsRoot, false);
        go.transform.position = pos;

        var col = go.AddComponent<SphereCollider>();
        col.isTrigger = true;
        col.radius = t.pickupRadius;

        var plant = go.AddComponent<CollectablePlant>();
        plant.Init(this, typeIndex, sector, pos, t.itemData, t.itemCount);
    }

    private void DestroyPickupsRoot()
    {
        if (pickupsRoot == null) return;
        if (Application.isPlaying) Destroy(pickupsRoot.gameObject);
        else DestroyImmediate(pickupsRoot.gameObject);
        pickupsRoot = null;
    }

    private Matrix4x4[][] SplitToBatches(List<Matrix4x4> list)
    {
        int batchCount = (list.Count + BatchSize - 1) / BatchSize;
        var result = new Matrix4x4[batchCount][];
        for (int i = 0; i < batchCount; i++)
        {
            int start = i * BatchSize;
            int len = Mathf.Min(BatchSize, list.Count - start);
            result[i] = new Matrix4x4[len];
            list.CopyTo(start, result[i], 0, len);
        }
        return result;
    }

    /// <summary>Квад по размеру спрайта, опора снизу (pivot по нижней грани).</summary>
    private Mesh BuildQuadFromSprite(Sprite s)
    {
        float wWorld = s.rect.width / s.pixelsPerUnit;
        float hWorld = s.rect.height / s.pixelsPerUnit;
        float hw = wWorld * 0.5f;

        Rect tr = s.textureRect;
        float tw = s.texture.width, th = s.texture.height;
        Vector2 uvMin = new Vector2(tr.xMin / tw, tr.yMin / th);
        Vector2 uvMax = new Vector2(tr.xMax / tw, tr.yMax / th);

        var mesh = new Mesh();
        mesh.vertices = new Vector3[]
        {
            new Vector3(-hw, 0, 0), new Vector3(hw, 0, 0),
            new Vector3(-hw, hWorld, 0), new Vector3(hw, hWorld, 0)
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(uvMin.x, uvMin.y), new Vector2(uvMax.x, uvMin.y),
            new Vector2(uvMin.x, uvMax.y), new Vector2(uvMax.x, uvMax.y)
        };
        mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
        mesh.RecalculateNormals();
        return mesh;
    }

    // =================== Отрисовка ===================

    void Update()
    {
        if (!isGenerated || player == null) return;

        float ts = terrainBuilder.TileSize;
        float sectorWorld = sectorSize * ts;
        int w = heightSource.width;
        int d = heightSource.depth;
        Vector3 origin = new Vector3(-w * ts / 2f, 0, -d * ts / 2f);

        // Сектор игрока и радиус в секторах
        int pcx = Mathf.FloorToInt((player.position.x - origin.x) / sectorWorld);
        int pcz = Mathf.FloorToInt((player.position.z - origin.z) / sectorWorld);
        int r = Mathf.CeilToInt(drawRadius / sectorWorld);

        foreach (var t in types)
        {
            if (t.sectors == null || t.mesh == null) continue;

            var shadows = t.castShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;

            for (int sx = pcx - r; sx <= pcx + r; sx++)
            {
                for (int sz = pcz - r; sz <= pcz + r; sz++)
                {
                    if (!t.sectors.TryGetValue(new Vector2Int(sx, sz), out var batches)) continue;

                    foreach (var batch in batches)
                    {
                        Graphics.DrawMeshInstanced(
                            t.mesh, 0, t.material,
                            batch, batch.Length, t.propertyBlock,
                            shadows, false);
                    }
                }
            }
        }
    }

    // =================== Проверка ===================

    private bool Validate()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("SpriteVegetationPlacer: HeightMapGenerator не готов!");
            return false;
        }
        if (terrainBuilder == null)
        {
            Debug.LogError("SpriteVegetationPlacer: нет ChunkedTerrainBuilder!");
            return false;
        }
        return true;
    }
}
