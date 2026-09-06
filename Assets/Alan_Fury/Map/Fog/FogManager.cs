using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Один на сцену. Две задачи:
///  1) Производительность — держит играющими только N ближайших к игроку очагов,
///     дальние гасит (не рисуются).
///  2) Запросы для AI — скрыт ли кто-то туманом, где ближайший туман.
///
/// Важно: запросы пряток работают по ВСЕМ очагам, не только активным —
/// скрытность это геймплейная правда, а не «рисуется ли частица».
/// </summary>
public class FogManager : MonoBehaviour
{
    public static FogManager Instance { get; private set; }

    [Header("Игрок (для кулинга)")]
    public Transform player;
    public string playerTag = "Player";

    [Header("Производительность")]
    [Tooltip("Сколько очагов держать активными (ближайших к игроку).")]
    public int maxActive = 5;
    [Tooltip("Как часто пересчитывать активные очаги (сек).")]
    public float cullInterval = 0.4f;

    private static readonly List<FogVolume> _volumes = new List<FogVolume>();
    private float _timer;

    public static void Register(FogVolume v)
    {
        if (v != null && !_volumes.Contains(v)) _volumes.Add(v);
    }

    public static void Unregister(FogVolume v) => _volumes.Remove(v);

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    void Start()
    {
        if (player == null)
            player = PlayerRegistry.ResolvePrimary();

        if (player == null && !string.IsNullOrEmpty(playerTag))
        {
            var go = GameObject.FindGameObjectWithTag(playerTag);
            if (go != null) player = go.transform;
        }
        Recull();
    }

    void Update()
    {
        if (player == null)
            player = PlayerRegistry.ResolvePrimary();

        _timer -= Time.deltaTime;
        if (_timer <= 0f)
        {
            _timer = cullInterval;
            Recull();
        }
    }

    private void Recull()
    {
        _volumes.RemoveAll(v => v == null);
        if (player == null || _volumes.Count == 0) return;

        // Очагов мало — включаем все, без сортировки.
        if (_volumes.Count <= maxActive)
        {
            for (int i = 0; i < _volumes.Count; i++)
                _volumes[i].SetActive(true);
            return;
        }

        Vector3 p = player.position;
        _volumes.Sort((a, b) => a.FlatSqrDistance(p).CompareTo(b.FlatSqrDistance(p)));

        for (int i = 0; i < _volumes.Count; i++)
            _volumes[i].SetActive(i < maxActive);
    }

    // =================== Запросы для AI ===================

    /// <summary>Точка внутри какого-нибудь очага тумана?</summary>
    public bool IsInsideFog(Vector3 worldPos)
    {
        for (int i = 0; i < _volumes.Count; i++)
            if (_volumes[i] != null && _volumes[i].Contains(worldPos)) return true;
        return false;
    }

    /// <summary>
    /// Скрыт ли hider от seeker туманом.
    /// Правило: hider внутри очага, в который seeker НЕ зашёл.
    /// </summary>
    public bool IsConcealed(Vector3 hiderPos, Vector3 seekerPos)
    {
        for (int i = 0; i < _volumes.Count; i++)
        {
            var v = _volumes[i];
            if (v == null) continue;
            if (v.Contains(hiderPos) && !v.Contains(seekerPos)) return true;
        }
        return false;
    }

    /// <summary>Ближайший очаг к точке (для cover-seeking). null если очагов нет.</summary>
    public FogVolume NearestFogTo(Vector3 worldPos)
    {
        FogVolume best = null;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < _volumes.Count; i++)
        {
            var v = _volumes[i];
            if (v == null) continue;
            float sq = v.FlatSqrDistance(worldPos);
            if (sq < bestSqr) { bestSqr = sq; best = v; }
        }
        return best;
    }
}
