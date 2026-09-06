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
    public float maxZoom = 150f;
    [Tooltip("Чувствительность колеса.")]
    public float zoomSpeed = 4f;
    [Tooltip("Сглаживание зума (сек). 0 = мгновенно.")]
    public float zoomSmoothTime = 0.12f;
    public bool invertZoom = false;

    [Header("Границы карты (опционально)")]
    public Bounds mapBounds;
    public bool clampToMap = false;

    [Header("Peek (заглянуть дальше)")]
    [Tooltip("Удержание клавиши — камера смещается к точке под мышью (ограничено maxPeekDistance)")]
    public bool enablePeek = true;
    public KeyCode peekKey = KeyCode.LeftControl;
    [Tooltip("Макс. смещение камеры от игрока (м)")]
    public float maxPeekDistance = 14f;
    [Tooltip("Скорость сглаживания смещения")]
    public float peekSmooth = 8f;

    [Tooltip("Как быстро камера догоняет высоту цели. Ниже — меньше укачивания.")]
    public float heightFollow = 2.2f;

    [Header("Gizmo кадра")]
    [Tooltip("Прямоугольник Size×aspect на плоскости камеры и на земле. Не near/far clip.")]
    public bool drawFrameGizmo = true;
    public Color frameGizmoColor = new Color(1f, 1f, 1f, 0.95f);

    private Camera cam;
    private float _targetSize;
    private float _zoomVel;
    private Vector3 _peekOffset;
    private float _followY;
    private bool _followYInit;

    /// <summary>Текущее смещение peek (XZ). Используется ViewOcclusion для сдвига focus прорисовки.</summary>
    public Vector3 CurrentPeekOffset => _peekOffset;

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

        // 2) Peek: удержание Ctrl → смещение к точке под мышью (кламп по maxPeekDistance)
        Vector3 peekTarget = Vector3.zero;
        if (enablePeek && Input.GetKey(peekKey) && cam != null)
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(Vector3.up, target.position);
            if (plane.Raycast(ray, out float dist))
            {
                Vector3 mouseWorld = ray.GetPoint(dist);
                Vector3 delta = mouseWorld - target.position;
                delta.y = 0f;
                float len = delta.magnitude;
                if (len > 0.05f)
                {
                    if (len > maxPeekDistance)
                        delta *= maxPeekDistance / len;
                    peekTarget = delta;
                }
            }
        }
        _peekOffset = Vector3.Lerp(_peekOffset, peekTarget, 1f - Mathf.Exp(-peekSmooth * Time.deltaTime));

        if (!_followYInit)
        {
            _followY = target.position.y;
            _followYInit = true;
        }
        else
            _followY = Mathf.Lerp(_followY, target.position.y, 1f - Mathf.Exp(-heightFollow * Time.deltaTime));

        Vector3 followPos = target.position;
        followPos.y = _followY;
        Vector3 desiredPosition = followPos + offset + _peekOffset;

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

    void OnDrawGizmos()
    {
        Camera c = cam != null ? cam : GetComponent<Camera>();
        if (drawFrameGizmo)
        {
            float groundY = target != null ? target.position.y : transform.position.y;
            DrawFrameGizmo(c, groundY, frameGizmoColor);
        }

        if (clampToMap && mapBounds.size != Vector3.zero)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(mapBounds.center, mapBounds.size);
        }
    }

    /// <summary>
    /// Прямоугольник кадра на плоскости камеры (не Near clip) и его проекция на землю вдоль взгляда.
    /// </summary>
    public static void DrawFrameGizmo(Camera c, float groundY, Color col)
    {
        if (!TryOrthoFrame(c, groundY, out Vector3[] film, out Vector3[] ground))
            return;
        Gizmos.color = col;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            Gizmos.DrawLine(film[i], film[j]);
            Gizmos.DrawLine(ground[i], ground[j]);
            Gizmos.DrawLine(film[i], ground[i]);
        }
    }

    public static void DrawFrameDebug(Camera c, float groundY, Color col)
    {
        if (!TryOrthoFrame(c, groundY, out Vector3[] film, out Vector3[] ground))
            return;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            Debug.DrawLine(film[i], film[j], col, 0f, false);
            Debug.DrawLine(ground[i], ground[j], col, 0f, false);
            Debug.DrawLine(film[i], ground[i], col, 0f, false);
        }
    }

    /// <summary>
    /// Кадр ортокамеры: film[4] на плоскости камеры (z=0), ground[4] на Y=groundY вдоль forward.
    /// Near clip не используется — отрицательный near не уводит рамку за камеру.
    /// </summary>
    public static bool TryOrthoFrame(Camera c, float groundY, out Vector3[] film, out Vector3[] ground)
    {
        film = null;
        ground = null;
        if (c == null || !c.orthographic) return false;

        float hh = c.orthographicSize;
        float hw = hh * Mathf.Max(0.05f, c.aspect);
        Transform t = c.transform;
        Vector3 o = t.position;
        Vector3 r = t.right;
        Vector3 u = t.up;
        Vector3 f = t.forward;

        film = new Vector3[4];
        film[0] = o + r * -hw + u * -hh;
        film[1] = o + r * hw + u * -hh;
        film[2] = o + r * hw + u * hh;
        film[3] = o + r * -hw + u * hh;

        ground = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            if (Mathf.Abs(f.y) < 1e-5f)
            {
                ground[i] = film[i] + f * 80f;
                continue;
            }
            float ty = (groundY - film[i].y) / f.y;
            if (ty < 1f) ty = 80f;
            ground[i] = film[i] + f * ty;
        }
        return true;
    }

    /// <summary>
    /// Точка на плоскости камеры, зажатая в прямоугольник Size×aspect. Не near clip.
    /// </summary>
    public static Vector3 ProjectToFrame(Camera c, Vector3 worldPt)
    {
        if (c == null) return worldPt;
        Transform t = c.transform;
        Vector3 nrm = t.forward;
        Vector3 dir = c.orthographic ? -nrm : (t.position - worldPt);
        if (dir.sqrMagnitude < 0.0001f) dir = -nrm;
        float denom = Vector3.Dot(nrm, dir);
        Vector3 hit = t.position;
        if (Mathf.Abs(denom) >= 1e-5f)
        {
            float rayT = Vector3.Dot(t.position - worldPt, nrm) / denom;
            if (rayT < 0f) rayT = 0f;
            hit = worldPt + dir * rayT;
        }

        Vector3 local = t.InverseTransformPoint(hit);
        float hh = c.orthographic ? c.orthographicSize : 15f;
        float hw = hh * Mathf.Max(0.05f, c.aspect);
        local.x = Mathf.Clamp(local.x, -hw, hw);
        local.y = Mathf.Clamp(local.y, -hh, hh);
        local.z = 0f;
        return t.TransformPoint(local);
    }
}
