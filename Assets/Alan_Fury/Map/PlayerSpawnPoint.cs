using UnityEngine;

/// <summary>
/// Спавн на дороге. Каждый вызов, включая повторный: спрятать, луч, потом тело, потом спад.
/// </summary>
[DefaultExecutionOrder(-50)]
public class PlayerSpawnPoint : MonoBehaviour
{
    public float randomRadius = 1.5f;
    public bool snapToRoad = true;
    public float groundProbe = 120f;
    [Tooltip("Корень дороги, если есть. Иначе ищется объект с Road / дорог в имени.")]
    public Transform roadRoot;
    [Tooltip("Игрок на сцене. Пусто — ищется по тегу Player или по имени.")]
    public Transform player;
    public bool playOnStart = true;

    public static bool TryGetSpawnPose(out Vector3 position, out Quaternion rotation)
    {
        return TryGetSpawnPose(out position, out rotation, null);
    }

    public static bool TryGetSpawnPose(out Vector3 position, out Quaternion rotation, Transform actor)
    {
        var point = First();
        if (point == null)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        point.Spawn(actor, out position, out rotation);
        return true;
    }

    public static void Respawn(Transform actor)
    {
        var point = First();
        if (point == null || actor == null) return;
        point.Spawn(actor, out _, out _);
    }

    void Awake()
    {
        if (player == null)
            player = FindPlayer();
        var arrival = GetComponent<SpawnArrivalLight>();
        if (arrival != null && player != null)
            arrival.Arm(player);
    }

    void Start()
    {
        if (!playOnStart) return;
        Spawn(player, out _, out _);
    }

    public void Spawn(Transform actor, out Vector3 position, out Quaternion rotation)
    {
        Vector3 pos = transform.position;
        if (randomRadius > 0.01f)
        {
            Vector2 xz = Random.insideUnitCircle * randomRadius;
            pos.x += xz.x;
            pos.z += xz.y;
        }

        pos = snapToRoad ? Snap(pos) : Ground(pos);
        position = pos;
        rotation = transform.rotation;
        transform.position = pos;

        if (actor != null)
            Plant(actor, pos, rotation);

        var arrival = GetComponent<SpawnArrivalLight>();
        if (arrival == null)
            arrival = gameObject.AddComponent<SpawnArrivalLight>();
        arrival.playOnStart = false;
        if (actor != null)
            arrival.Arm(actor);
        arrival.Play(pos);
    }

    static PlayerSpawnPoint First()
    {
        var points = FindObjectsOfType<PlayerSpawnPoint>();
        if (points == null || points.Length == 0) return null;
        return points[0];
    }

    static Transform FindPlayer()
    {
        var tagged = GameObject.FindGameObjectWithTag("Player");
        if (tagged != null) return tagged.transform;
        var all = FindObjectsOfType<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].name == "Player") return all[i];
        }
        return null;
    }

    Vector3 Snap(Vector3 from)
    {
        Renderer best = null;
        float bestSq = float.PositiveInfinity;
        var rends = roadRoot != null
            ? roadRoot.GetComponentsInChildren<Renderer>(true)
            : FindObjectsOfType<Renderer>();
        for (int i = 0; i < rends.Length; i++)
        {
            var r = rends[i];
            if (r == null || !IsRoad(r.transform)) continue;
            Vector3 c = r.bounds.ClosestPoint(from);
            float sq = (c - from).sqrMagnitude;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = r;
            }
        }

        if (best == null)
            return Ground(from);

        Vector3 onRoad = best.bounds.ClosestPoint(from);
        return Ground(new Vector3(onRoad.x, best.bounds.max.y + 2f, onRoad.z));
    }

    static bool IsRoad(Transform t)
    {
        while (t != null)
        {
            string n = t.name;
            if (n.IndexOf("road", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("дорог", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            t = t.parent;
        }
        return false;
    }

    Vector3 Ground(Vector3 xz)
    {
        Vector3 origin = new Vector3(xz.x, xz.y + groundProbe, xz.z);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, groundProbe * 2f, ~0, QueryTriggerInteraction.Ignore))
            return hit.point + Vector3.up * 0.05f;
        return xz;
    }

    void Plant(Transform actor, Vector3 pos, Quaternion rot)
    {
        var cc = actor.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        actor.SetPositionAndRotation(pos, rot);
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

