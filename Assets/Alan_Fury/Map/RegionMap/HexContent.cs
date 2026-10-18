using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Стоит на префабе гекса. Списки объектов (3D префаб или 2D спрайт).
/// Generate вызывается после дороги. Сид гекса — раскладка уникальна.
/// </summary>
public class HexContent : MonoBehaviour
{
    public string kind = "лес";
    public List<HexPlace> places = new List<HexPlace>();
    [Range(0f, 0.4f)]
    public float edgeMargin = 0.1f;
    public bool avoidRoad = true;
    [Tooltip("Половина ширины дороги в долях радиуса гекса.")]
    public float roadClear = 0.22f;

    public void Generate(int seed, float hexSize, float topY, bool hasRoad, Vector3 roadDir)
    {
        ClearGenerated();
        var baseT = transform.Find("Base");
        if (baseT != null)
            baseT.localScale = new Vector3(hexSize, Mathf.Max(0.05f, topY * 2f), hexSize);

        if (places == null || places.Count == 0) return;
        var rnd = new System.Random(seed);
        var dir = roadDir;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
        dir.Normalize();
        float clear = hexSize * roadClear;

        for (int i = 0; i < places.Count; i++)
        {
            var place = places[i];
            if (place == null) continue;
            if (place.prefab == null && place.sprite == null) continue;
            int lo = Mathf.Max(0, place.minCount);
            int hi = Mathf.Max(lo, place.maxCount);
            int count = rnd.Next(lo, hi + 1);
            for (int n = 0; n < count; n++)
            {
                var local = Sample(rnd, hexSize, place.minRadius, place.maxRadius);
                if (hasRoad && avoidRoad && HitsRoad(local, dir, clear))
                {
                    local = PushOffRoad(local, dir, clear);
                    if (HitsRoad(local, dir, clear * 0.85f)) continue;
                }
                local.y = topY + 0.02f;
                Spawn(place, rnd, local);
            }
        }
    }

    void Spawn(HexPlace place, System.Random rnd, Vector3 local)
    {
        float yaw = (float)rnd.NextDouble() * 360f;
        float s = Mathf.Lerp(place.minScale, place.maxScale, (float)rnd.NextDouble());
        if (s <= 0f) s = 1f;

        if (place.prefab != null)
        {
            var obj = Instantiate(place.prefab, transform);
            obj.name = string.IsNullOrEmpty(place.label) ? place.prefab.name : place.label;
            obj.transform.localPosition = local;
            obj.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            obj.transform.localScale = Vector3.one * s;
            var cols = obj.GetComponentsInChildren<Collider>();
            for (int i = 0; i < cols.Length; i++) cols[i].enabled = false;
            return;
        }

        var go = new GameObject(string.IsNullOrEmpty(place.label) ? "sprite" : place.label);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = local;
        go.transform.localRotation = place.flat
            ? Quaternion.Euler(90f, yaw, 0f)
            : Quaternion.Euler(0f, yaw, 0f);
        go.transform.localScale = Vector3.one * s;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = place.sprite;
        sr.color = place.tint;
    }

    void ClearGenerated()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            if (ch.name == "Base") continue;
            if (Application.isPlaying) Destroy(ch.gameObject);
            else DestroyImmediate(ch.gameObject);
        }
    }

    Vector3 Sample(System.Random rnd, float hexSize, float minR, float maxR)
    {
        float outer = hexSize * (1f - edgeMargin);
        float inner = hexSize * Mathf.Clamp01(minR);
        float far = hexSize * Mathf.Clamp(maxR <= 0f ? 1f : maxR, 0f, 1f);
        if (far < inner) far = inner;
        for (int attempt = 0; attempt < 18; attempt++)
        {
            float ang = (float)rnd.NextDouble() * Mathf.PI * 2f;
            float u = (float)rnd.NextDouble();
            float rad = Mathf.Sqrt(Mathf.Lerp(inner * inner, far * far, u));
            if (rad > outer) rad = outer;
            float x = Mathf.Cos(ang) * rad;
            float z = Mathf.Sin(ang) * rad;
            if (HexMesh.InHex(x, z, outer))
                return new Vector3(x, 0f, z);
        }
        return Vector3.zero;
    }

    static bool HitsRoad(Vector3 local, Vector3 dir, float halfWidth)
    {
        float dist = Mathf.Abs(local.x * dir.z - local.z * dir.x);
        return dist < halfWidth;
    }

    static Vector3 PushOffRoad(Vector3 local, Vector3 dir, float halfWidth)
    {
        var side = new Vector3(-dir.z, 0f, dir.x);
        float dist = local.x * dir.z - local.z * dir.x;
        float sign = dist >= 0f ? 1f : -1f;
        return local + side * sign * (halfWidth - Mathf.Abs(dist) + 0.05f);
    }
}

/// <summary>Один слот списка на гексе: 3D префаб или 2D спрайт.</summary>
[System.Serializable]
public class HexPlace
{
    public string label = "объект";
    public GameObject prefab;
    public Sprite sprite;
    [Tooltip("Спрайт плашмя. Иначе вертикальный.")]
    public bool flat = true;
    public Color tint = Color.white;
    public int minCount = 1;
    public int maxCount = 1;
    public float minScale = 0.85f;
    public float maxScale = 1.15f;
    [Tooltip("0..1 от радиуса гекса.")]
    public float minRadius = 0f;
    public float maxRadius = 1f;
}
