using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    [Header("Цель")]
    public Transform target;

    [Header("Смещение")]
    public Vector3 offset = new Vector3(0, 35, -35);

    [Header("Сглаживание следования")]
    public float smoothSpeed = 5f;

    [Header("Поворот")]
    public bool lockRotation = true;
    public Vector3 fixedRotation = new Vector3(40f, 45f, 0f);

    [Header("Ортографическая камера")]
    public bool isOrthographic = true;
    public float orthographicSize = 15f;

    [Header("Зум колесом мыши")]
    public bool enableZoom = true;
    [Tooltip("Минимальный размер (приближено).")]
    public float minZoom = 10f;
    [Tooltip("Максимальный размер (отдалено).")]
    public float maxZoom = 22f;
    [Tooltip("Чувствительность колеса.")]
    public float zoomSpeed = 4f;
    [Tooltip("Сглаживание зума (сек). 0 = мгновенно.")]
    public float zoomSmoothTime = 0.12f;
    public bool invertZoom = false;

    [Header("Границы карты (опционально)")]
    public Bounds mapBounds;
    public bool clampToMap = false;

    private Camera cam;
    private float _targetSize;
    private float _zoomVel;

    void Start()
    {
        cam = GetComponent<Camera>();

        if (isOrthographic && cam != null)
        {
            cam.orthographic = true;
            cam.orthographicSize = orthographicSize;
        }

        _targetSize = cam != null ? cam.orthographicSize : orthographicSize;

        // Цель не ищем по тегу Player: её выставляет NetworkPlayer (локальный владелец)
        // или ResolveTarget(), если играешь без сети / target ещё пуст.

        // Автоопределение границ карты
        if (clampToMap && mapBounds.size == Vector3.zero)
        {
            GameObject map = GameObject.FindGameObjectWithTag("Map");
            if (map != null)
            {
                Renderer renderer = map.GetComponent<Renderer>();
                if (renderer != null)
                    mapBounds = renderer.bounds;
            }
        }
    }

    /// <summary>
    /// Подхватить цель: предпочтительно локальный NetworkPlayer, иначе primary из реестра.
    /// Не использует тег "Player".
    /// </summary>
    public void ResolveTarget()
    {
        // Локальный владелец (кооп / Host)
        var nets = FindObjectsOfType<NetworkPlayer>();
        for (int i = 0; i < nets.Length; i++)
        {
            if (nets[i] != null && nets[i].IsLocalControlled)
            {
                target = nets[i].transform;
                return;
            }
        }

        var primary = PlayerRegistry.ResolvePrimary();
        if (primary != null)
            target = primary;
    }

    void Update()
    {
        if (target == null)
            ResolveTarget();

        if (!enableZoom || cam == null || !cam.orthographic) return;

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.0001f)
        {
            if (invertZoom) scroll = -scroll;
            // колесо вверх (+) приближает → уменьшает размер
            _targetSize = Mathf.Clamp(_targetSize - scroll * zoomSpeed, minZoom, maxZoom);
        }
    }

    void LateUpdate()
    {
        if (target == null) return;

        // 1) Применяем зум ДО клампа, чтобы кламп считал актуальный размер.
        if (enableZoom && cam != null && cam.orthographic)
        {
            cam.orthographicSize = zoomSmoothTime > 0f
                ? Mathf.SmoothDamp(cam.orthographicSize, _targetSize, ref _zoomVel, zoomSmoothTime)
                : _targetSize;
        }

        Vector3 desiredPosition = target.position + offset;

        if (clampToMap && mapBounds.size != Vector3.zero)
        {
            float verticalHalf = cam.orthographicSize;
            float horizontalHalf = verticalHalf * Screen.width / Screen.height;

            // Защита: если карта меньше кадра (сильный зум-аут) — центрируемся,
            // иначе Clamp с min > max даёт дёрганье.
            float minX = mapBounds.min.x + horizontalHalf;
            float maxX = mapBounds.max.x - horizontalHalf;
            float minY = mapBounds.min.y + verticalHalf;
            float maxY = mapBounds.max.y - verticalHalf;

            desiredPosition.x = minX <= maxX ? Mathf.Clamp(desiredPosition.x, minX, maxX) : mapBounds.center.x;
            desiredPosition.y = minY <= maxY ? Mathf.Clamp(desiredPosition.y, minY, maxY) : mapBounds.center.y;
        }

        transform.position = Vector3.Lerp(transform.position, desiredPosition, smoothSpeed * Time.deltaTime);

        if (lockRotation)
            transform.rotation = Quaternion.Euler(fixedRotation);
    }

    void OnDrawGizmosSelected()
    {
        if (clampToMap && mapBounds.size != Vector3.zero)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(mapBounds.center, mapBounds.size);
        }
    }
}
