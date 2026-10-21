using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Один файл со всеми типами гексов. На карте только ссылка.
/// У каждого типа — префаб гекса (меш + HexContent со списками объектов).
/// </summary>
[CreateAssetMenu(menuName = "Alan Fury/Hex Types", fileName = "HexTypes")]
public class HexTypes : ScriptableObject
{
    public List<HexTypeEntry> types = new List<HexTypeEntry>();

    public HexTypeEntry Find(string kind)
    {
        if (string.IsNullOrEmpty(kind) || types == null) return null;
        for (int i = 0; i < types.Count; i++)
        {
            var t = types[i];
            if (t != null && t.kind == kind) return t;
        }
        return null;
    }
}

[System.Serializable]
public class HexTypeEntry
{
    [Tooltip("лес / роща / населенный пункт / чаща / поляна / ...")]
    public string kind = "лес";
    [Tooltip("Префаб гекса со скриптом HexContent.")]
    public GameObject hexPrefab;
    [Tooltip("Спрятать призму карты, когда стоит префаб.")]
    public bool hideBase = true;
}
