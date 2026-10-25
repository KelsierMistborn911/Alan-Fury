using UnityEngine;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Леший. Создаёт файл настроек леса (Forest) и держит настройки области.
/// Чаща, лес, роща, граница леса — плотности и слоты.
/// </summary>
public class Leshiy : MonoBehaviour
{
    [Header("Файл настроек леса")]
    public Forest forest;

    [Header("Отрисовка (свой объект, не террейн)")]
    public NaturePlacement placement;
    public NatureRenderer renderer;
    public GrassField grass;
    public Transform drawRoot;

    [Header("Слоты (префабы по слоям)")]
    public List<NaturePlacement.NatureLayer> layers = new List<NaturePlacement.NatureLayer>();

    [Header("Зоны")]
    public MapLayout layout;

    [Header("Карта")]
    public float waterLevel = 0f;
    public int borderCells = 2;
    public int sectorSize = 16;

    [Header("Стриминг")]
    public float streamRadius = 50f;
    public float unloadExtra = 30f;

    [Header("Трава")]
    [Range(0f, 1f)] public float grassUnderTreeMult = 0.28f;
    public float grassClusterNoiseScale = 0.09f;
    public Vector2 grassClusterOffset = new Vector2(41.2f, 8.7f);

    [Header("Чаща")]
    [Range(0f, 1f)] public float thicketTrees = 0.85f;
    [Range(0f, 1f)] public float thicketSmall = 0.35f;
    [Range(0f, 1f)] public float thicketBushes = 0.5f;
    [Range(0f, 1f)] public float thicketPlants = 0.15f;
    [Range(0f, 1f)] public float thicketGrass = 0.12f;

    [Header("Лес")]
    [Range(0f, 1f)] public float forestTrees = 0.55f;
    [Range(0f, 1f)] public float forestSmall = 0.2f;
    [Range(0f, 1f)] public float forestBushes = 0.3f;
    [Range(0f, 1f)] public float forestPlants = 0.25f;
    [Range(0f, 1f)] public float forestGrass = 0.55f;

    [Header("Роща")]
    [Range(0f, 1f)] public float groveTrees = 0.12f;
    [Range(0f, 1f)] public float groveSmall = 0.08f;
    [Range(0f, 1f)] public float groveBushes = 0.15f;
    [Range(0f, 1f)] public float grovePlants = 0.2f;
    [Range(0f, 1f)] public float groveGrass = 0.7f;

    [Header("Граница леса")]
    [Range(0f, 1f)] public float borderTrees = 0.28f;
    [Range(0f, 1f)] public float borderSmall = 0.12f;
    [Range(0f, 1f)] public float borderBushes = 0.22f;
    [Range(0f, 1f)] public float borderPlants = 0.18f;
    [Range(0f, 1f)] public float borderGrass = 0.4f;

    void Awake()
    {
        EnsureDrawObject();
    }

    /// <summary>
    /// Создаёт дочерний объект Leshiy и вешает на него NaturePlacement + NatureRenderer.
    /// С террейна отрисовку не берём.
    /// </summary>
    public void EnsureDrawObject()
    {
        if (drawRoot == null)
        {
            var existing = transform.Find("Leshiy");
            if (existing != null) drawRoot = existing;
            else
            {
                var go = new GameObject("Leshiy");
                go.transform.SetParent(transform, false);
                drawRoot = go.transform;
            }
        }

        if (placement == null) placement = drawRoot.GetComponent<NaturePlacement>();
        if (placement == null) placement = drawRoot.gameObject.AddComponent<NaturePlacement>();

        if (renderer == null) renderer = drawRoot.GetComponent<NatureRenderer>();
        if (renderer == null) renderer = drawRoot.gameObject.AddComponent<NatureRenderer>();

        renderer.placement = placement;
        placement.leshiy = this;
        if (forest != null) placement.forest = forest;
        AdoptLayersFrom(placement);
        var local = GetComponent<NaturePlacement>();
        if (local != null && local != placement) AdoptLayersFrom(local);
        EnsureDefaultSlots();
        PushToPlacement(placement);

        if (grass == null) grass = drawRoot.GetComponent<GrassField>();
        if (grass == null) grass = drawRoot.gameObject.AddComponent<GrassField>();
        grass.naturePlacement = placement;
        grass.natureRenderer = renderer;
        if (grass.grassMaterial == null || grass.grassMaterial.shader == null
            || grass.grassMaterial.shader.name != "Nature/GrassStrip")
        {
            var sh = Shader.Find("Nature/GrassStrip");
            if (sh != null) grass.grassMaterial = new Material(sh) { name = "GrassStrip" };
        }
    }

    void Reset()
    {
        EnsureDefaultSlots();
    }

    void OnValidate()
    {
        EnsureDefaultSlots();
    }

    /// <summary>Два слота: основной лес и кусты. Префаб пустой — вставить руками.</summary>
    public void EnsureDefaultSlots()
    {
        if (!SlotsAreBlank()) return;
        layers = new List<NaturePlacement.NatureLayer>
        {
            MakeTreeSlot(),
            MakeBushSlot()
        };
    }

    bool SlotsAreBlank()
    {
        if (layers == null || layers.Count == 0) return true;
        for (int i = 0; i < layers.Count; i++)
        {
            var L = layers[i];
            if (L == null) continue;
            if (!string.IsNullOrEmpty(L.name) && L.name != "Layer") return false;
            if (L.variants == null) continue;
            for (int v = 0; v < L.variants.Count; v++)
            {
                if (L.variants[v] != null && L.variants[v].prefab != null)
                    return false;
            }
        }
        return true;
    }

    static NaturePlacement.NatureLayer MakeTreeSlot()
    {
        return new NaturePlacement.NatureLayer
        {
            name = "Основной лес",
            kind = NaturePlacement.LayerKind.MainForest,
            enabled = true,
            footprint = 3,
            sightCover = MapGrid.SightCoverMode.Full,
            maxCoverHeight = 0f,
            variants = new List<NaturePlacement.NatureVariant>
            {
                new NaturePlacement.NatureVariant
                {
                    name = "Дерево",
                    weight = 100f,
                    minScale = 2.8f,
                    maxScale = 4.2f,
                    randomYRotation = true,
                    castShadows = true
                }
            }
        };
    }

    static NaturePlacement.NatureLayer MakeBushSlot()
    {
        return new NaturePlacement.NatureLayer
        {
            name = "Кусты",
            kind = NaturePlacement.LayerKind.Bush,
            enabled = true,
            footprint = 1,
            sightCover = MapGrid.SightCoverMode.MaxHeight,
            maxCoverHeight = 1.2f,
            variants = new List<NaturePlacement.NatureVariant>
            {
                new NaturePlacement.NatureVariant
                {
                    name = "Куст",
                    weight = 100f,
                    minScale = 0.7f,
                    maxScale = 1.1f,
                    randomYRotation = true,
                    castShadows = false
                }
            }
        };
    }

    /// <summary>Забирает слои с плейсера, если на Лешем ещё пусто (старая сцена).</summary>
    public void AdoptLayersFrom(NaturePlacement src)
    {
        if (src == null || src.layers == null || src.layers.Count == 0) return;
        if (layers != null && layers.Count > 0) return;
        layers = src.layers;
    }

    /// <summary>Отдаёт слои и настройки области плейсеру.</summary>
    public void PushToPlacement(NaturePlacement dst)
    {
        if (dst == null) return;
        dst.leshiy = this;
        if (forest != null) dst.forest = forest;
        if (layers != null && layers.Count > 0) dst.layers = layers;
        if (layout != null) dst.layout = layout;
        dst.waterLevel = waterLevel;
        dst.borderCells = borderCells;
        dst.sectorSize = sectorSize;
        dst.streamRadius = streamRadius;
        dst.unloadExtra = unloadExtra;
        dst.grassUnderTreeMult = grassUnderTreeMult;
        dst.grassClusterNoiseScale = grassClusterNoiseScale;
        dst.grassClusterOffset = grassClusterOffset;
    }

    [ContextMenu("Создать файл Forest")]
    public void CreateForestFile()
    {
        if (forest == null)
        {
            forest = ScriptableObject.CreateInstance<Forest>();
            forest.name = "Forest";
        }

        WriteAreas();

#if UNITY_EDITOR
        if (Application.isPlaying)
        {
            Debug.Log("Leshiy: Forest в памяти (в плеймоде файл не пишем).");
            return;
        }

        const string path = "Assets/Alan_Fury/Map/Forest/Forest.asset";
        EnsureFolder("Assets/Alan_Fury");
        EnsureFolder("Assets/Alan_Fury/Map");
        EnsureFolder("Assets/Alan_Fury/Map/Forest");
        var existing = AssetDatabase.LoadAssetAtPath<Forest>(path);
        if (existing == null)
        {
            forest.name = "Forest";
            AssetDatabase.CreateAsset(forest, path);
            AssetDatabase.SaveAssets();
            Debug.Log("Leshiy: создан " + path);
        }
        else
        {
            string keep = existing.name;
            EditorUtility.CopySerialized(forest, existing);
            existing.name = string.IsNullOrEmpty(keep) ? "Forest" : keep;
            forest = existing;
            EditorUtility.SetDirty(forest);
            AssetDatabase.SaveAssets();
            Debug.Log("Leshiy: обновлён " + path);
        }
#else
        Debug.Log("Leshiy: Forest создан в памяти (редактор нужен для файла на диске).");
#endif
    }

    [ContextMenu("Записать настройки в Forest")]
    public void WriteAreas()
    {
        if (forest == null)
        {
            Debug.LogWarning("Leshiy: нет Forest. Сначала «Создать файл Forest».");
            return;
        }
        forest.thicket = Make("Чаща", thicketTrees, thicketSmall, thicketBushes, thicketPlants, thicketGrass);
        forest.forest = Make("Лес", forestTrees, forestSmall, forestBushes, forestPlants, forestGrass);
        forest.grove = Make("Роща", groveTrees, groveSmall, groveBushes, grovePlants, groveGrass);
        forest.border = Make("Граница леса", borderTrees, borderSmall, borderBushes, borderPlants, borderGrass);
#if UNITY_EDITOR
        EditorUtility.SetDirty(forest);
#endif
    }

    static Forest.Area Make(string name, float trees, float small, float bushes, float plants, float grass)
    {
        return new Forest.Area
        {
            name = name,
            slots = new List<Forest.PlacementSlot>
            {
                new Forest.PlacementSlot { name = "Основной лес", kind = NaturePlacement.LayerKind.MainForest, density = trees },
                new Forest.PlacementSlot { name = "Маленькие деревья", kind = NaturePlacement.LayerKind.SmallTree, density = small },
                new Forest.PlacementSlot { name = "Кусты", kind = NaturePlacement.LayerKind.Bush, density = bushes },
                new Forest.PlacementSlot { name = "Растения", kind = NaturePlacement.LayerKind.Plant, density = plants },
                new Forest.PlacementSlot { name = "Трава", kind = NaturePlacement.LayerKind.Grass, density = grass }
            }
        };
    }

#if UNITY_EDITOR
    [CustomPropertyDrawer(typeof(NaturePlacement.NatureLayer))]
    public class NatureLayerDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var name = property.FindPropertyRelative("name");
            string title = name != null && !string.IsNullOrEmpty(name.stringValue) ? name.stringValue : label.text;
            EditorGUI.PropertyField(position, property, new GUIContent(title), true);
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, true);
        }
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        string parent = path.Substring(0, slash);
        string leaf = path.Substring(slash + 1);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
#endif
}
