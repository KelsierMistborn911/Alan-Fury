using UnityEngine;

/// <summary>
/// Точка призыва стаи. Ставишь на карту рядом с игроком (или спавнишь).
/// Подходишь — над объектом подсказка [E], жмёшь E → WerewolfPackManager.SpawnPack().
/// В Start (и в редакторе) встаёт в центр ближайшей проходимой клетки.
/// Если задан visualPrefab — спавнит его дочерним на этой клетке.
/// Одноразовая: после вызова отключается.
/// </summary>
public class WolfSummonPoint : MonoBehaviour
{
    public KeyCode interactKey = KeyCode.E;
    [Tooltip("Радиус триггера (если на объекте нет Collider — добавится SphereCollider).")]
    public float triggerRadius = 2.5f;
    [Tooltip("Высота подсказки над объектом (м).")]
    public float promptHeight = 2.2f;
    [Tooltip("Визуал (куб и т.п.). Если задан — Instantiate как дочерний после снапа на клетку. Если скрипт уже на самом визуале — оставь пустым.")]
    public GameObject visualPrefab;
    [Tooltip("Pathfinder для снапа в центр клетки. Пусто — найдётся на сцене.")]
    public Pathfinder pathfinder;

    private bool _playerInRange;
    private bool _used;
    private GameObject _visualInstance;

    void Reset()
    {
        EnsureTrigger();
    }

    void OnValidate()
    {
        EnsureTrigger();
        SnapToNearestCell();
    }

    void Start()
    {
        EnsureTrigger();
        SnapToNearestCell();
        SpawnVisualIfNeeded();
    }

    void EnsureTrigger()
    {
        var col = GetComponent<Collider>();
        if (col == null)
        {
            var sphere = gameObject.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = triggerRadius;
            col = sphere;
        }
        else
        {
            col.isTrigger = true;
            if (col is SphereCollider sc) sc.radius = triggerRadius;
        }
    }

    void SnapToNearestCell()
    {
        if (pathfinder == null)
            pathfinder = FindObjectOfType<Pathfinder>();

        if (pathfinder == null || !pathfinder.IsReady) return;

        bool found;
        Vector3 cell = pathfinder.NearestWalkableWorld(transform.position, out found);
        if (found)
            transform.position = cell;
    }

    void SpawnVisualIfNeeded()
    {
        if (visualPrefab == null || _visualInstance != null) return;

        _visualInstance = Instantiate(visualPrefab, transform.position, Quaternion.identity, transform);
        _visualInstance.transform.localPosition = Vector3.zero;
        _visualInstance.transform.localRotation = Quaternion.identity;
    }

    void OnTriggerEnter(Collider other)
    {
        if (_used) return;
        if (other.GetComponentInParent<Inventory>() != null || other.CompareTag("Player"))
            _playerInRange = true;
    }

    void OnTriggerExit(Collider other)
    {
        if (other.GetComponentInParent<Inventory>() != null || other.CompareTag("Player"))
            _playerInRange = false;
    }

    void Update()
    {
        if (_used || !_playerInRange) return;

        if (Input.GetKeyDown(interactKey))
        {
            if (WerewolfPackManager.Instance != null)
            {
                WerewolfPackManager.Instance.SpawnPack();
                _used = true;
                _playerInRange = false;
                enabled = false; // больше не реагирует
            }
            else
            {
                Debug.LogWarning("WolfSummonPoint: WerewolfPackManager.Instance отсутствует.");
            }
        }
    }

    void OnGUI()
    {
        if (_used || !_playerInRange) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 world = transform.position + Vector3.up * promptHeight;
        Vector3 screen = cam.WorldToScreenPoint(world);
        if (screen.z < 0f) return; // за камерой

        // Unity GUI: y от верха экрана
        float guiY = Screen.height - screen.y;

        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 22,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = Color.white;

        // тень
        var shadow = new GUIStyle(style);
        shadow.normal.textColor = new Color(0f, 0f, 0f, 0.75f);
        GUI.Label(new Rect(screen.x - 40 + 1, guiY - 14 + 1, 80, 28), $"[ {interactKey} ]", shadow);
        GUI.Label(new Rect(screen.x - 40, guiY - 14, 80, 28), $"[ {interactKey} ]", style);
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = _used ? new Color(0.3f, 0.3f, 0.3f, 0.4f) : new Color(0.9f, 0.5f, 0.1f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, triggerRadius);
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * promptHeight);
    }
#endif
}
