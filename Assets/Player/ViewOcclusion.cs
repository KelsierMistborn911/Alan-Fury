using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Поле зрения (culling). Считает, какие деревья видны.
/// Работает с ортографической камерой, проецируя на плоскость земли.
/// </summary>
[DisallowMultipleComponent]
public class ViewOcclusion : MonoBehaviour
{
    public static ViewOcclusion Instance { get; private set; }

    [Tooltip("Источник данных леса")]
    public ForestPlacement forestSource;

    [Header("Цель")]
    public Transform player;
    public Camera targetCamera;

    [Header("Настройки отсечения")]
    [Tooltip("Буфер для крон деревьев у края экрана")]
    public float treeHeightBuffer = 15f;

    [Header("Отладка")]
    public bool logOnce = true;
    public bool drawDebug = false;

    private readonly List<(ForestPlacement.TreeType type, Matrix4x4 matrix)> _visibleTrees =
        new List<(ForestPlacement.TreeType, Matrix4x4)>(2048);
    private bool _logged;

    void Awake()
    {
        Instance = this;
        ResolveForest();
    }

    void OnEnable()
    {
        Instance = this;
        ResolveForest();
        _logged = false;
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void ResolveForest()
    {
        if (forestSource == null) forestSource = GetComponent<ForestPlacement>();
        if (forestSource == null) forestSource = FindObjectOfType<ForestPlacement>();
    }

    /// <summary>
    /// Возвращает список видимых деревьев для ForestRenderer.
    /// </summary>
    public IReadOnlyList<(ForestPlacement.TreeType type, Matrix4x4 matrix)> GetVisibleTrees()
        => _visibleTrees;

    void LateUpdate()
    {
        if (forestSource == null) ResolveForest();
        if (forestSource == null || !forestSource.IsGenerated) return;

        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera == null) return;

        if (player == null)
            player = forestSource.player != null ? forestSource.player : PlayerRegistry.ResolvePrimary();
        if (player == null) return;

        Vector3 playerPos = player.position;

        // --- Получаем границы видимой области ---
        Vector3 viewMin = Vector3.zero, viewMax = Vector3.zero;
        bool hasViewBounds = TryGetCameraGroundBounds(playerPos.y, out viewMin, out viewMax);

        Vector3 focus = playerPos;
        float effectiveRadius = Mathf.Max(forestSource.drawRadius, 80f);

        if (hasViewBounds)
        {
            // Расширяем для высоких деревьев
            float margin = treeHeightBuffer;
            viewMin.x -= margin; viewMin.z -= margin;
            viewMax.x += margin; viewMax.z += margin;

            focus = new Vector3(
                (viewMin.x + viewMax.x) * 0.5f,
                playerPos.y,
                (viewMin.z + viewMax.z) * 0.5f);

            float halfW = (viewMax.x - viewMin.x) * 0.5f;
            float halfD = (viewMax.z - viewMin.z) * 0.5f;
            effectiveRadius = Mathf.Sqrt(halfW * halfW + halfD * halfD) + 4f;
        }

        // Очищаем список видимых деревьев
        _visibleTrees.Clear();

        float iterRadius = effectiveRadius + 1f;
        forestSource.ForEachVisibleMatrix(focus, iterRadius, (type, rootMatrix) =>
        {
            Vector3 pos = rootMatrix.GetColumn(3);

            // Отсев по границам камеры
            if (hasViewBounds)
            {
                if (pos.x < viewMin.x || pos.x > viewMax.x ||
                    pos.z < viewMin.z || pos.z > viewMax.z)
                    return;
            }
            else
            {
                if (HorizDist(pos, focus) >= effectiveRadius)
                    return;
            }

            _visibleTrees.Add((type, rootMatrix));
        });

        if (logOnce && !_logged)
        {
            _logged = true;
            Debug.Log($"ViewOcclusion: visible trees = {_visibleTrees.Count}, bounds = {viewMin} to {viewMax}");
        }

        if (drawDebug)
        {
            if (hasViewBounds)
            {
                // Рисуем прямоугольник видимости
                Vector3 p1 = new Vector3(viewMin.x, playerPos.y + 0.5f, viewMin.z);
                Vector3 p2 = new Vector3(viewMax.x, playerPos.y + 0.5f, viewMin.z);
                Vector3 p3 = new Vector3(viewMax.x, playerPos.y + 0.5f, viewMax.z);
                Vector3 p4 = new Vector3(viewMin.x, playerPos.y + 0.5f, viewMax.z);

                Debug.DrawLine(p1, p2, Color.red);
                Debug.DrawLine(p2, p3, Color.red);
                Debug.DrawLine(p3, p4, Color.red);
                Debug.DrawLine(p4, p1, Color.red);

                Debug.DrawLine(playerPos, focus, Color.yellow);
            }
        }
    }

    /// <summary>
    /// Получает AABB видимой области на земле.
    /// Для ортографической камеры: проецируем углы экрана на плоскость земли.
    /// </summary>
    private bool TryGetCameraGroundBounds(float groundY, out Vector3 min, out Vector3 max)
    {
        min = max = Vector3.zero;
        if (targetCamera == null) return false;

        // Для ортографической камеры все лучи параллельны
        Vector2[] corners = {
            new Vector2(0f, 0f), // нижний левый
            new Vector2(1f, 0f), // нижний правый
            new Vector2(0f, 1f), // верхний левый
            new Vector2(1f, 1f)  // верхний правый
        };

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        int hit = 0;

        for (int i = 0; i < 4; i++)
        {
            Ray ray = targetCamera.ViewportPointToRay(new Vector3(corners[i].x, corners[i].y, 0f));

            // Находим пересечение с плоскостью земли
            var plane = new Plane(Vector3.up, new Vector3(0f, groundY, 0f));
            if (!plane.Raycast(ray, out float dist)) continue;

            // Для ортографической камеры dist может быть отрицательным (луч смотрит вверх)
            if (dist < 0) continue;

            Vector3 p = ray.GetPoint(dist);
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            hit++;
        }

        // Если не все 4 угла попали на плоскость, пробуем расширить
        if (hit < 4)
        {
            // Fallback: используем радиус вокруг игрока
            float radius = 100f;
            min = new Vector3(player.position.x - radius, groundY, player.position.z - radius);
            max = new Vector3(player.position.x + radius, groundY, player.position.z + radius);
            return true;
        }

        min = new Vector3(minX, groundY, minZ);
        max = new Vector3(maxX, groundY, maxZ);
        return true;
    }

    private static float HorizDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}