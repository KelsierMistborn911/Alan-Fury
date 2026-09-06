using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Отрисовка леса: получает видимые матрицы от ViewOcclusion и рисует их instanced.
/// Только РЕНДЕРИНГ. Никакой логики размещения или определения видимости.
/// </summary>
[DisallowMultipleComponent]
public class ForestRenderer : MonoBehaviour
{
    public static ForestRenderer Instance { get; private set; }

    [Tooltip("Источник данных леса")]
    public ForestPlacement forestSource;

    [Header("Отладка")]
    public bool logDrawCalls = false;

    private class RenderBatch
    {
        public ForestPlacement.TreeType type;
        public readonly List<Matrix4x4> matrices = new List<Matrix4x4>(1024);
    }

    private readonly List<RenderBatch> _batches = new List<RenderBatch>(4);
    private readonly Dictionary<ForestPlacement.TreeType, RenderBatch> _batchMap =
        new Dictionary<ForestPlacement.TreeType, RenderBatch>();

    private bool _loggedThisFrame = false;

    void Awake()
    {
        Instance = this;
        ResolveForest();
    }

    void OnEnable()
    {
        Instance = this;
        ResolveForest();
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

    void LateUpdate()
    {
        if (forestSource == null) ResolveForest();
        if (forestSource == null || !forestSource.IsGenerated) return;

        // Получаем видимые деревья от ViewOcclusion
        var occlusion = ViewOcclusion.Instance;
        if (occlusion == null) occlusion = FindObjectOfType<ViewOcclusion>();
        if (occlusion == null) return;

        // Очищаем батчи
        foreach (var batch in _batches)
            batch.matrices.Clear();

        _loggedThisFrame = false;

        // Заполняем батчи видимыми деревьями
        foreach (var visible in occlusion.GetVisibleTrees())
        {
            if (forestSource.IsLiveAt(visible.matrix.GetColumn(3))) continue;

            if (!_batchMap.TryGetValue(visible.type, out var batch))
            {
                batch = new RenderBatch { type = visible.type };
                _batchMap[visible.type] = batch;
                _batches.Add(batch);
            }
            batch.matrices.Add(visible.matrix);
        }

        // Отрисовываем все батчи
        RenderAllBatches();
    }

    private void RenderAllBatches()
    {
        int totalDrawCalls = 0;

        foreach (var batch in _batches)
        {
            if (batch.matrices.Count == 0) continue;

            foreach (var part in batch.type.parts)
            {
                // Разбиваем на подбатчи по 1023 (ограничение Unity)
                for (int i = 0; i < batch.matrices.Count; i += 1023)
                {
                    int count = Mathf.Min(1023, batch.matrices.Count - i);
                    var matrices = batch.matrices.GetRange(i, count);

                    Graphics.DrawMeshInstanced(
                        part.mesh,
                        part.subMeshIndex,
                        part.material,
                        matrices,
                        batch.type.propertyBlock,
                        batch.type.shadowCasting,
                        batch.type.receiveShadows);

                    totalDrawCalls++;
                }
            }
        }

        if (logDrawCalls && !_loggedThisFrame)
        {
            _loggedThisFrame = true;
            Debug.Log($"ForestRenderer: draw calls = {totalDrawCalls}");
        }
    }

    /// <summary>
    /// Статический метод для отрисовки без MonoBehaviour (fallback).
    /// </summary>
    public static void DrawInstanced(ForestPlacement.TreeType type, List<Matrix4x4> matrices)
    {
        foreach (var part in type.parts)
        {
            for (int i = 0; i < matrices.Count; i += 1023)
            {
                int count = Mathf.Min(1023, matrices.Count - i);
                var batch = matrices.GetRange(i, count);

                Graphics.DrawMeshInstanced(
                    part.mesh,
                    part.subMeshIndex,
                    part.material,
                    batch,
                    type.propertyBlock,
                    type.shadowCasting,
                    type.receiveShadows);
            }
        }
    }
}
