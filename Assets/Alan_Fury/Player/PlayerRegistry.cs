using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Список живых игроков для коопа (этап 3).
/// Вместо FindGameObjectWithTag("Player") / одного Transform player.
///
/// Регистрация: NetworkPlayer при спавне/деспавне.
/// Пока один Host — в списке один игрок; API уже под нескольких.
/// </summary>
public class PlayerRegistry : MonoBehaviour
{
    public static PlayerRegistry Instance { get; private set; }

    readonly List<Transform> _players = new List<Transform>(4);

    public IReadOnlyList<Transform> Players => _players;
    public int Count => _players.Count;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public void Register(Transform player)
    {
        if (player == null) return;
        if (_players.Contains(player)) return;
        _players.Add(player);
    }

    public void Unregister(Transform player)
    {
        if (player == null) return;
        _players.Remove(player);
    }

    public Transform GetNearest(Vector3 from)
    {
        Transform best = null;
        float bestSq = float.PositiveInfinity;
        for (int i = 0; i < _players.Count; i++)
        {
            var t = _players[i];
            if (t == null) continue;
            float sq = (t.position - from).sqrMagnitude;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = t;
            }
        }
        return best;
    }

    /// <summary>
    /// Ближайший в радиусе (горизонтально, Y игнорируется). Нет — null.
    /// </summary>
    public Transform GetNearestFlat(Vector3 from, float maxDistance)
    {
        float maxSq = maxDistance * maxDistance;
        Transform best = null;
        float bestSq = float.PositiveInfinity;
        for (int i = 0; i < _players.Count; i++)
        {
            var t = _players[i];
            if (t == null) continue;
            Vector3 d = t.position - from;
            d.y = 0f;
            float sq = d.sqrMagnitude;
            if (sq <= maxSq && sq < bestSq)
            {
                bestSq = sq;
                best = t;
            }
        }
        return best;
    }

    /// <summary>
    /// Удобный fallback: реестр → тег Player (пока одиночка/миграция).
    /// </summary>
    public static Transform ResolvePrimary()
    {
        if (Instance != null && Instance._players.Count > 0)
        {
            for (int i = 0; i < Instance._players.Count; i++)
            {
                if (Instance._players[i] != null)
                    return Instance._players[i];
            }
        }

        var go = GameObject.FindGameObjectWithTag("Player");
        return go != null ? go.transform : null;
    }
}
