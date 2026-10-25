using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Настройки леса. Чаща, лес, роща, граница леса.
/// Плотность и слоты размещения лежат здесь.
/// Файл создаёт Leshiy.
/// </summary>
[CreateAssetMenu(fileName = "Forest", menuName = "Alan Fury/Forest")]
public class Forest : ScriptableObject
{
    [System.Serializable]
    public class PlacementSlot
    {
        public string name = "Слот";
        public NaturePlacement.LayerKind kind = NaturePlacement.LayerKind.MainForest;
        [Range(0f, 1f)] public float density = 0.5f;
        public bool enabled = true;
    }

    [System.Serializable]
    public class Area
    {
        public string name = "Зона";
        public List<PlacementSlot> slots = new List<PlacementSlot>();
    }

    [Header("Чаща")]
    public Area thicket = MakeArea("Чаща", 0.85f, 0.35f, 0.5f, 0.15f, 0.12f);

    [Header("Лес")]
    public Area forest = MakeArea("Лес", 0.55f, 0.2f, 0.3f, 0.25f, 0.55f);

    [Header("Роща")]
    public Area grove = MakeArea("Роща", 0.12f, 0.08f, 0.15f, 0.2f, 0.7f);

    [Header("Граница леса")]
    public Area border = MakeArea("Граница леса", 0.28f, 0.12f, 0.22f, 0.18f, 0.4f);

    static readonly List<PlacementSlot> Empty = new List<PlacementSlot>();

    public Area GetArea(NaturePlacement.Zone zone)
    {
        switch (zone)
        {
            case NaturePlacement.Zone.Thicket: return thicket;
            case NaturePlacement.Zone.Forest: return forest;
            case NaturePlacement.Zone.Grove: return grove;
            case NaturePlacement.Zone.ForestBorder: return border;
            default: return null;
        }
    }

    /// <summary>Слоты, в которые плейсер ставит префабы для зоны.</summary>
    public List<PlacementSlot> GetSlots(NaturePlacement.Zone zone)
    {
        var a = GetArea(zone);
        return a != null && a.slots != null ? a.slots : Empty;
    }

    public float DensityFor(NaturePlacement.Zone zone, NaturePlacement.LayerKind kind)
    {
        var slots = GetSlots(zone);
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s == null || !s.enabled) continue;
            if (s.kind == kind) return Mathf.Clamp01(s.density);
        }
        return 0f;
    }

    public bool Grows(NaturePlacement.Zone zone, NaturePlacement.LayerKind kind)
    {
        return DensityFor(zone, kind) > 0.0001f;
    }

    static Area MakeArea(string name, float trees, float small, float bushes, float plants, float grass)
    {
        return new Area
        {
            name = name,
            slots = new List<PlacementSlot>
            {
                new PlacementSlot { name = "Основной лес", kind = NaturePlacement.LayerKind.MainForest, density = trees },
                new PlacementSlot { name = "Маленькие деревья", kind = NaturePlacement.LayerKind.SmallTree, density = small },
                new PlacementSlot { name = "Кусты", kind = NaturePlacement.LayerKind.Bush, density = bushes },
                new PlacementSlot { name = "Растения", kind = NaturePlacement.LayerKind.Plant, density = plants },
                new PlacementSlot { name = "Трава", kind = NaturePlacement.LayerKind.Grass, density = grass }
            }
        };
    }
}
