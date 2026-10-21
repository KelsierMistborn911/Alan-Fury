using UnityEngine;

/// <summary>
/// В основной сцене: читает HexSpawnRequest и ставит игрока в выбранный гекс.
/// Сторона гекса = 100 м. Альфа/стая — сразу при входе в соседний с Воющей пущей.
/// Повесь на любой объект в игровой сцене (или на тот же GO, что LocationStream).
/// </summary>
public class HexRegionEntry : MonoBehaviour
{
    public const int HowlingQ = 2;
    public const int HowlingR = -1;

    [Header("Связи")]
    public Transform player;
    public LocationStream stream;

    [Header("Спавн")]
    public float spawnHeightOffset = 1.5f;

    public int CurrentQ { get; private set; }
    public int CurrentR { get; private set; }
    public bool FromRegionMap { get; private set; }

    void Start()
    {
        if (player == null)
        {
            var move = FindObjectOfType<PlayerMovement3D>();
            if (move != null) player = move.transform;
        }
        if (stream == null) stream = FindObjectOfType<LocationStream>();

        if (!HexSpawnRequest.HasRequest)
        {
            FromRegionMap = false;
            return;
        }

        FromRegionMap = true;
        CurrentQ = HexSpawnRequest.Q;
        CurrentR = HexSpawnRequest.R;

        // Позицию и origin ставит LocationStream.EnterFromHex.
        // Здесь только отмечаем зону волков.
        bool neighbor = IsNeighbor(CurrentQ, CurrentR, RegionHexMap.HowlingQ, RegionHexMap.HowlingR);
        if (neighbor || (CurrentQ == RegionHexMap.HowlingQ && CurrentR == RegionHexMap.HowlingR))
            OnEnterWolfTerritory();

        // Clear делает LocationStream после входа
    }

    void OnEnterWolfTerritory()
    {
        // Альфа и стая появляются сразу при входе в соседний (или сам) гекс Воющей пущи.
        // Точка привязки — объект «Воющая пуща». Дальше подключится WerewolfPackManager.
        Debug.Log($"[HexRegion] Вход в зону волков (гекс {CurrentQ},{CurrentR}). Спавн альфы/стаи у Воющей пущи.");
        // TODO: WerewolfPackManager.SpawnAtHowling() или аналог
    }

    public static bool IsNeighbor(int q1, int r1, int q2, int r2)
    {
        int dq = q1 - q2;
        int dr = r1 - r2;
        int ds = -dq - dr; // cube s
        int dist = (Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(ds)) / 2;
        return dist == 1;
    }

    public static Vector3 AxialToWorld(int q, int r, float size)
    {
        // size = сторона / расстояние между центрами по оси (≈100 м)
        float x = size * (Mathf.Sqrt(3f) * q + Mathf.Sqrt(3f) * 0.5f * r);
        float z = size * (1.5f * r);
        return new Vector3(x, 0f, z);
    }
}
