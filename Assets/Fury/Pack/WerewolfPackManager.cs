using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Авторитет стаи. Один на уровень (WerewolfPackManager.Instance).
///
/// Обязанности (только это, без заделов на будущее):
///   • Спавнит волков — метод-кнопка SpawnPack() (для теста; вой альфы прикрутим позже).
///   • Держит игрока и список волков (волки регистрируются сами в Awake через Register).
///   • Периодически назначает роли: ближайшие к игроку maxAttackers → Attack, остальные → Surround.
///   • Второму атакующему ставит признак avoidFront ("заходи не спереди").
///   • Держит ОДИН токен фронт-атаки: фронтовики (1 и 3) бьют по очереди, чтобы не попасть друг в друга.
///
/// Чего менеджер НЕ делает (важно для будущей сети):
///   • Не двигает волков и не лезет в их компоненты — только выдаёт роль/признак/токен.
///   • Точку подхода волк считает сам в WerewolfPackBrain.
///
/// Мозг волка обязан предоставить интерфейс IPackAgent (см. ниже).
/// </summary>
public class WerewolfPackManager : MonoBehaviour
{
    public static WerewolfPackManager Instance { get; private set; }

    public enum PackRole { Surround, Attack }

    /// <summary>Что менеджер требует от мозга волка. Реализует WerewolfPackBrain.</summary>
    public interface IPackAgent
    {
        Transform Transform { get; }
        bool IsAlive { get; }
        void SetRole(PackRole role, bool avoidFront);
        /// <summary>Разрешение бить в этот момент (токен фронта). Второй (сзади) всегда true.</summary>
        void SetAttackToken(bool hasToken);
        /// <summary>Страх стаи вырос на amount (рана у сородича) — волк срезает свою агрессию.</summary>
        void OnPackFear(float amount);
    }

    [Header("Игрок")]
    [Tooltip("Если пусто — найдётся по тегу в Awake.")]
    public Transform player;
    public string playerTag = "Player";

    [Header("Спавн")]
    [Tooltip("Префаб волка (на нём WerewolfPackBrain + WerewolfBrain + WerewolfCombat + локомоция/восприятие).")]
    public GameObject wolfPrefab;
    [Tooltip("Сколько волков рождает один SpawnPack().")]
    public int spawnCount = 5;
    [Tooltip("Радиус кольца спавна вокруг точки спавна (м).")]
    public float spawnRadius = 12f;
    [Tooltip("Точка, вокруг которой спавнятся волки. Пусто — вокруг самого менеджера.")]
    public Transform spawnCenter;

    [Header("Вой (авто-спавн)")]
    [Tooltip("Через сколько секунд после старта альфа 'воет' и поднимает стаю (разово).")]
    public float howlDelay = 2f;
    [Tooltip("Альфа в сцене. Если задана — волки выходят веером с дальней от игрока стороны альфы.")]
    public Transform alphaTransform;
    [Tooltip("Полуугол веера выхода из-за альфы (град). 40 = узкий веер 'выбегают из-за неё'.")]
    public float spawnArcHalfAngle = 40f;

    [Header("Роли")]
    [Tooltip("Сколько волков одновременно в роли Attack. Остальные — Surround.")]
    public int maxAttackers = 3;
    [Tooltip("Как часто пересчитываются роли (сек). Не каждый кадр, чтобы роли не дёргались.")]
    public float roleUpdateInterval = 0.5f;

    [Header("Токен фронт-атаки")]
    [Tooltip("Как долго один фронтовик держит право удара, прежде чем токен уйдёт другому (сек).")]
    public float frontTokenHold = 1.25f;

    [Header("Прыжки стаи")]
    [Tooltip("Минимальный интервал между атакующими прыжками всей стаи (сек).")]
    public float packJumpInterval = 1.2f;

    [Header("Страх стаи")]
    [Tooltip("Потолок страха стаи. Урон по любому волку добавляет столько же страха; пока не затухает.")]
    public float maxPackFear = 500f;

    private float _nextJumpAllowed;
    private float _packFear;

    /// <summary>Текущий страх стаи (0..maxPackFear). Общий для всех волков, только растёт.</summary>
    public float PackFear => _packFear;
    public float PackFearPercent => maxPackFear > 0f ? _packFear / maxPackFear : 0f;

    /// <summary>Рана у волка (зовёт WerewolfStats.TakeDamage): страх стаи += урон,
    /// и все живые волки срезают агрессию на ту же величину.</summary>
    public void ReportWound(float damage)
    {
        _packFear = Mathf.Min(maxPackFear, _packFear + damage);
        for (int i = 0; i < _wolves.Count; i++)
            if (_wolves[i] != null && _wolves[i].IsAlive)
                _wolves[i].OnPackFear(damage);
    }

    // ---- состояние ----
    private readonly List<IPackAgent> _wolves = new List<IPackAgent>();
    private float _roleTimer;

    // Токен фронта: кто из фронтовиков сейчас бьёт.
    private IPackAgent _frontTokenHolder;
    private float _frontTokenUntil;

    // Вой: одноразовый авто-спавн через howlDelay после старта.
    private float _howlTimer;
    private bool _howled;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("WerewolfPackManager: на сцене уже есть Instance — этот отключаю.");
            enabled = false;
            return;
        }
        Instance = this;

        if (player == null && !string.IsNullOrEmpty(playerTag))
        {
            var go = GameObject.FindGameObjectWithTag(playerTag);
            if (go != null) player = go.transform;
        }

        _howlTimer = howlDelay; // отсчёт до воя стартует со сцены
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ===================== Регистрация (зовёт волк) =====================

    public void Register(IPackAgent wolf)
    {
        if (wolf != null && !_wolves.Contains(wolf)) _wolves.Add(wolf);
    }

    public void Unregister(IPackAgent wolf)
    {
        _wolves.Remove(wolf);
        if (_frontTokenHolder == wolf) _frontTokenHolder = null;
    }

    // ===================== Спавн (кнопка теста) =====================

    /// <summary>Родить стаю вокруг точки спавна и раздать роли. Для теста — зови из кнопки/клавиши.</summary>
    [ContextMenu("Spawn Pack")]
    public void SpawnPack()
    {
        if (wolfPrefab == null)
        {
            Debug.LogWarning("WerewolfPackManager: не назначен wolfPrefab — спавнить нечего.");
            return;
        }

        bool useAlpha = alphaTransform != null;

        // Центр спавна: у альфы (если есть) или как раньше — spawnCenter/менеджер.
        Vector3 center = useAlpha
            ? alphaTransform.position
            : (spawnCenter != null ? spawnCenter.position : transform.position);

        // Базовое направление веера: от игрока к альфе (дальняя от игрока сторона).
        Vector3 baseDir = Vector3.forward;
        if (useAlpha)
        {
            Vector3 fromPlayer = player != null ? (center - player.position) : alphaTransform.forward;
            fromPlayer.y = 0f;
            baseDir = fromPlayer.sqrMagnitude > 1e-4f ? fromPlayer.normalized : Vector3.forward;
        }

        for (int i = 0; i < spawnCount; i++)
        {
            Vector3 dir;
            if (useAlpha)
            {
                // Веер ±spawnArcHalfAngle вокруг baseDir — волки выходят из-за альфы.
                float t = spawnCount > 1 ? i / (float)(spawnCount - 1) : 0.5f;
                float deg = Mathf.Lerp(-spawnArcHalfAngle, spawnArcHalfAngle, t);
                dir = Quaternion.AngleAxis(deg, Vector3.up) * baseDir;
            }
            else
            {
                // Прежнее поведение: полное кольцо вокруг центра.
                float ang = (360f / Mathf.Max(1, spawnCount)) * i * Mathf.Deg2Rad;
                dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
            }

            Instantiate(wolfPrefab, center + dir * spawnRadius, Quaternion.identity);
            // Волк сам зарегистрируется в Awake.
        }

        // Сразу раздать роли, не дожидаясь таймера.
        UpdateRoles();
    }

    // ===================== Роли + токен =====================

    void Update()
    {
        // Вой: разово через howlDelay поднимаем стаю (переиспользуем SpawnPack).
        if (!_howled)
        {
            _howlTimer -= Time.deltaTime;
            if (_howlTimer <= 0f)
            {
                _howled = true;
                SpawnPack();
            }
        }

        _roleTimer -= Time.deltaTime;
        if (_roleTimer <= 0f)
        {
            _roleTimer = roleUpdateInterval;
            UpdateRoles();
        }

        UpdateFrontToken();
    }

    private void UpdateRoles()
    {
        PruneDead();
        if (player == null || _wolves.Count == 0) return;

        // Сортируем по дистанции до игрока — ближайшие идут в атаку.
        _wolves.Sort((a, b) =>
        {
            float da = (a.Transform.position - player.position).sqrMagnitude;
            float db = (b.Transform.position - player.position).sqrMagnitude;
            return da.CompareTo(db);
        });

        int attackers = Mathf.Min(maxAttackers, _wolves.Count);
        for (int i = 0; i < _wolves.Count; i++)
        {
            if (i < attackers)
            {
                // Второй атакующий (индекс 1) заходит не спереди.
                bool avoidFront = (i == 1);
                _wolves[i].SetRole(PackRole.Attack, avoidFront);
            }
            else
            {
                _wolves[i].SetRole(PackRole.Surround, false);
            }
        }
    }

    // Токен фронта передаётся между фронтовиками (Attack + !avoidFront здесь не знаем —
    // трактуем "фронт" как всех атакующих, кроме заднего; задний бьёт всегда).
    // Проще: токен держит один атакующий за раз; кому не досталось — ждут своей очереди.
    // Задний (второй) в токене не нуждается — ему всегда разрешено (см. SetAttackToken).
    private void UpdateFrontToken()
    {
        PruneDead();

        // Список тех, кому нужен токен (фронтовики). Мы не храним avoidFront на стороне менеджера,
        // поэтому раздаём токен по кругу всем атакующим — задний игнорирует его у себя.
        // Собираем текущих атакующих (первые maxAttackers ближайших после сортировки в UpdateRoles).
        // Чтобы не сортировать снова, просто крутим токен по всему списку атакующих кандидатов.

        if (_frontTokenHolder == null || Time.time >= _frontTokenUntil || !_frontTokenHolder.IsAlive)
        {
            IPackAgent next = PickNextFrontHolder();
            // Снять токен с прошлого, выдать новому.
            if (_frontTokenHolder != null && _frontTokenHolder.IsAlive)
                _frontTokenHolder.SetAttackToken(false);

            _frontTokenHolder = next;
            _frontTokenUntil = Time.time + frontTokenHold;

            if (_frontTokenHolder != null)
                _frontTokenHolder.SetAttackToken(true);
        }
    }

    // Берём следующего атакующего после текущего держателя (грубая карусель).
    private IPackAgent PickNextFrontHolder()
    {
        if (_wolves.Count == 0 || player == null) return null;

        int attackers = Mathf.Min(maxAttackers, _wolves.Count);
        if (attackers <= 0) return null;

        int startIdx = _frontTokenHolder != null ? _wolves.IndexOf(_frontTokenHolder) : -1;
        for (int step = 1; step <= attackers; step++)
        {
            int idx = ((startIdx + step) % attackers + attackers) % attackers;
            var cand = _wolves[idx];
            if (cand != null && cand.IsAlive) return cand;
        }
        return null;
    }

    private void PruneDead()
    {
        for (int i = _wolves.Count - 1; i >= 0; i--)
            if (_wolves[i] == null || !_wolves[i].IsAlive)
            {
                if (_frontTokenHolder == _wolves[i]) _frontTokenHolder = null;
                _wolves.RemoveAt(i);
            }
    }

    /// <summary>Разрешить атакующий прыжок: не чаще packJumpInterval на стаю,
    /// и только волку с максимальным углом от взгляда игрока (лучшая позиция).</summary>
    public bool RequestJump(WerewolfPerception asker)
    {
        if (Time.time < _nextJumpAllowed) return false;
        if (asker == null) { _nextJumpAllowed = Time.time + packJumpInterval; return true; }

        float myAngle = asker.AngleFromPlayerGaze;
        for (int i = 0; i < _wolves.Count; i++)
        {
            var w = _wolves[i];
            if (w == null || !w.IsAlive || w.Transform == asker.transform) continue;
            var p = w.Transform.GetComponent<WerewolfPerception>();
            if (p != null && p.AngleFromPlayerGaze > myAngle + 1f) return false; // есть волк в позиции лучше
        }

        _nextJumpAllowed = Time.time + packJumpInterval;
        return true;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Vector3 c = spawnCenter != null ? spawnCenter.position : transform.position;
        Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.6f);
        Gizmos.DrawWireSphere(c, spawnRadius);
    }
#endif
}
