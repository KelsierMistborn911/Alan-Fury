using UnityEngine;

/// <summary>
/// Точка появления игрока на сцене (этап 1).
/// Create Empty → поставь над землёй → повесь этот скрипт.
/// NetworkPlayer при спавне (IsOwner / сервер) переносит персонажа сюда.
/// Несколько точек: берётся ближайшая к (0,0,0) или первая найденная — см. GetSpawnPose.
/// </summary>
public class PlayerSpawnPoint : MonoBehaviour
{
    [Tooltip("Случайный разброс по XZ, чтобы двое не стояли в одной точке.")]
    public float randomRadius = 1.5f;

    public static bool TryGetSpawnPose(out Vector3 position, out Quaternion rotation)
    {
        var points = FindObjectsOfType<PlayerSpawnPoint>();
        if (points == null || points.Length == 0)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        // Пока одна логика на всех: первая в порядке Find. Позже — слоты по ClientId.
        var point = points[0];
        Vector3 pos = point.transform.position;
        if (point.randomRadius > 0.01f)
        {
            Vector2 xz = Random.insideUnitCircle * point.randomRadius;
            pos.x += xz.x;
            pos.z += xz.y;
        }

        position = pos;
        rotation = point.transform.rotation;
        return true;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 0.9f, 0.4f, 0.9f);
        Gizmos.DrawWireSphere(transform.position, Mathf.Max(0.35f, randomRadius));
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.5f);
    }
#endif
}
