using UnityEngine;

/// <summary>
/// Боевой исполнитель оборотня: три атаки от замаха + собственная стамина.
///
/// Разделение обязанностей:
///   • МОЗГ (WerewolfPackBrain) решает, ЧТО ударить по дистанции, и зовёт
///     TryJump()/TrySwipe()/TrySpecial(). Пока IsBusy — мозг не двигает волка (MoveTo).
///   • ЭТОТ скрипт сам не выбирает атаку — только гоняет фазы и наносит урон.
///
/// Урон идёт через ОБЩИЙ WeaponHitbox (тот же, что у игрока): цель обязана иметь
/// IDamageable (у игрока это теперь PlayerResources). Слой цели задаётся полем targetLayers.
/// Визуал меча (SwordAttackVisual) волку не нужен — в WeaponHitbox он опционален.
///
/// Стамина — в отдельном WerewolfStats (параметры оборотня). Не хватает на удар —
/// атака не стартует, поэтому волк не долбит без остановки.
///
/// Прыжок использует locomotion.Leap (баллистический наскок в игрока); урон — на приземлении.
/// Обычный — свип спереди, сцепляется в серию повторным TrySwipe в окне (maxCombo/comboWindow).
/// Особый — долгий замах на месте + удлинённый хитбокс укуса ("дотянуться").
/// </summary>
[RequireComponent(typeof(WerewolfPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
[RequireComponent(typeof(WerewolfStats))]
public class WerewolfCombat : MonoBehaviour
{
    public enum AttackKind { Jump = 0, Swipe = 1, Special = 2 }

    [System.Serializable]
    public struct AttackDef
    {
        [Header("Тайминги (сек)")]
        [Tooltip("Замах/подготовка перед ударом.")]
        public float windup;
        [Tooltip("Длительность окна урона — уходит в WeaponHitbox как duration.")]
        public float active;
        [Tooltip("Восстановление после удара: новую атаку начать нельзя.")]
        public float recover;
        [Tooltip("Пауза до следующей атаки ЭТОГО типа (сверх фаз выше).")]
        public float cooldown;

        [Header("Стоимость")]
        [Tooltip("Сколько стамины стоит удар. Нет стамины — атака не начнётся.")]
        public float staminaCost;

        [Header("Хитбокс (как у меча)")]
        [Tooltip("Дальность зоны удара вперёд (м).")]
        public float range;
        [Tooltip("Полуширина зоны удара (м).")]
        public float radius;
        [Tooltip("Высота зоны удара (м).")]
        public float height;
        [Tooltip("Смещение центра зоны относительно волка.")]
        public Vector3 offset;
        [Tooltip("Урон за попадание.")]
        public float damage;
        [Tooltip("Сила отброса (stagger).")]
        public float stagger;
    }

    [Header("Ссылки")]
    public WerewolfPerception perception;
    public WerewolfLocomotion locomotion;
    [Tooltip("Общий WeaponHitbox на волке (или в детях). targetLayers ниже — слой игрока.")]
    public WeaponHitbox hitbox;
    [Tooltip("Параметры оборотня (стамина). Пусто — найдётся на волке.")]
    public WerewolfStats stats;
    [Tooltip("Аниматор волка (опционально — пока нет, оставь пустым). Триггеры: Jump/Swipe/Special в начале замаха.")]
    public Animator animator;

    [Header("Цель урона")]
    [Tooltip("Слой(и), по которым бьёт волк. Поставь слой игрока.")]
    public LayerMask targetLayers;
    [Tooltip("Тик урона внутри окна active (сек). Меньше — может задеть несколько раз за окно.")]
    public float hitTickInterval = 0.2f;

    [Header("Прыжковая атака")]
    [Tooltip("Высота дуги прыжка (передаётся в locomotion.Leap).")]
    public float jumpArc = 2.2f;
    public AttackDef jump = new AttackDef
    {
        windup = 0.45f,
        active = 0.20f,
        recover = 0.50f,
        cooldown = 2.5f,
        staminaCost = 35f,
        range = 2.0f,
        radius = 1.2f,
        height = 2f,
        offset = new Vector3(0f, 1f, 0f),
        damage = 20f,
        stagger = 6f
    };

    [Header("Обычный удар / серия")]
    [Tooltip("Макс. длина серии (сколько свипов подряд).")]
    public int maxCombo = 3;
    [Tooltip("Окно после свипа, в котором повторный TrySwipe продолжает серию (сек).")]
    public float comboWindow = 0.6f;
    public AttackDef swipe = new AttackDef
    {
        windup = 0.25f,
        active = 0.15f,
        recover = 0.35f,
        cooldown = 0.10f,
        staminaCost = 12f,
        range = 2.0f,
        radius = 1.0f,
        height = 2f,
        offset = new Vector3(0f, 1f, 0f),
        damage = 10f,
        stagger = 3f
    };

    [Header("Особый удар с места (дотянуться)")]
    public AttackDef special = new AttackDef
    {
        windup = 0.80f,
        active = 0.25f,
        recover = 0.70f,
        cooldown = 4f,
        staminaCost = 45f,
        range = 3.5f,
        radius = 1.0f,
        height = 2f,
        offset = new Vector3(0f, 1f, 0f),
        damage = 30f,
        stagger = 8f
    };

    // ===================== Состояние =====================
    private enum Phase { Idle, Windup, Active, Recover }
    private Phase _phase = Phase.Idle;
    private AttackKind _kind;
    private AttackDef _def;
    private float _phaseTimer;

    private bool _hitFired;      // урон уже нанесён в этой атаке (для прыжка — один раз)
    private bool _jumpAirborne;  // волк оторвался от земли в прыжке

    private readonly float[] _cooldownUntil = new float[3]; // по индексу AttackKind

    private int _combo;
    private float _comboExpire;

    // ===================== Публичное для мозга =====================
    public bool IsBusy => _phase != Phase.Idle;
    public bool IsWindingUp => _phase == Phase.Windup;
    public int ComboCount => _combo;
    public float Stamina => stats != null ? stats.Stamina : 0f;
    public float StaminaPercent => stats != null ? stats.StaminaPercent : 0f;

    /// <summary>Срабатывает, когда удар этого волка нанёс урон (мозг цепляет для агрессии и т.п.).</summary>
    public System.Action OnHitLanded;

    /// <summary>Хватает ли стамины и вышел ли кулдаун на конкретную атаку (для решений мозга).</summary>
    public bool CanStart(AttackKind kind)
    {
        if (_phase != Phase.Idle) return false;
        if (Time.time < _cooldownUntil[(int)kind]) return false;
        return stats != null && stats.HasEnough(DefOf(kind).staminaCost);
    }

    public bool TryJump() => TryStart(AttackKind.Jump, jump);
    public bool TrySwipe() => TryStart(AttackKind.Swipe, swipe);
    public bool TrySpecial() => TryStart(AttackKind.Special, special);

    // ===================== Жизненный цикл =====================

    void Start()
    {
        if (perception == null) perception = GetComponent<WerewolfPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
        if (hitbox == null)
            Debug.LogWarning("WerewolfCombat: не назначен WeaponHitbox — удары не нанесут урон.");
        if (stats == null) stats = GetComponent<WerewolfStats>();
        if (stats == null)
            Debug.LogWarning("WerewolfCombat: не найден WerewolfStats — атаки не будут проверять стамину.");

        if (hitbox != null) hitbox.onHit += HandleHitboxHit;

        for (int i = 0; i < _cooldownUntil.Length; i++) _cooldownUntil[i] = -999f;
    }

    void OnDestroy()
    {
        if (hitbox != null) hitbox.onHit -= HandleHitboxHit;
    }

    // Хитбокс сообщил о попадании → прокидываем наружу (мозгу).
    private void HandleHitboxHit() => OnHitLanded?.Invoke();

    void Update()
    {
        float dt = Time.deltaTime;
        if (_combo > 0 && Time.time > _comboExpire) _combo = 0;

        switch (_phase)
        {
            case Phase.Idle:
                return;

            case Phase.Windup:
                if (perception.HasPlayer) locomotion.FaceTowards(perception.PlayerPos, dt);
                _phaseTimer -= dt;
                if (_phaseTimer <= 0f) EnterActive();
                break;

            case Phase.Active:
                if (_kind == AttackKind.Jump) TickJumpActive(dt);
                else
                {
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f) EnterRecover();
                }
                break;

            case Phase.Recover:
                _phaseTimer -= dt;
                if (_phaseTimer <= 0f) _phase = Phase.Idle;
                break;
        }
    }

    // ===================== Старт атаки =====================

    private bool TryStart(AttackKind kind, AttackDef def)
    {
        if (!CanStart(kind)) return false;

        if (stats != null) stats.Spend(def.staminaCost);
        _kind = kind;
        _def = def;
        _hitFired = false;
        _jumpAirborne = false;
        // Кулдаун считаем от конца атаки (фазы + пауза), чтобы типы не спамились.
        _cooldownUntil[(int)kind] = Time.time + def.windup + def.active + def.recover + def.cooldown;

        // Серия: свип в окне наращивает combo, иначе — сброс.
        if (kind == AttackKind.Swipe)
        {
            _combo = (_combo > 0 && _combo < maxCombo) ? _combo + 1 : 1;
            _comboExpire = Time.time + def.windup + def.active + def.recover + comboWindow;
        }
        else _combo = 0;

        EnterWindup();
        return true;
    }

    // ===================== Фазы =====================

    private void EnterWindup()
    {
        _phase = Phase.Windup;
        _phaseTimer = _def.windup;

        if (animator != null)
        {
            switch (_kind)
            {
                case AttackKind.Jump: animator.SetTrigger("Jump"); break;
                case AttackKind.Swipe: animator.SetTrigger("Swipe"); break;
                case AttackKind.Special: animator.SetTrigger("Special"); break;
            }
        }
    }

    private void EnterActive()
    {
        _phase = Phase.Active;
        _phaseTimer = _def.active;

        if (_kind == AttackKind.Jump)
        {
            // Прыгаем в игрока; урон нанесём на приземлении (TickJumpActive).
            if (perception.HasPlayer && locomotion.IsGrounded)
            {
                locomotion.Leap(perception.PlayerPos, jumpArc);
            }
            else
            {
                // Прыгнуть не смогли (не на земле / нет игрока) — бьём на месте.
                FireHitbox(_def);
                _hitFired = true;
            }
        }
        else
        {
            // Свип / особый — урон в начале окна active.
            FireHitbox(_def);
            _hitFired = true;
        }
    }

    // Прыжок: ждём приземления, наносим урон один раз, затем короткое окно active → recover.
    private void TickJumpActive(float dt)
    {
        if (_hitFired)
        {
            _phaseTimer -= dt;
            if (_phaseTimer <= 0f) EnterRecover();
            return;
        }

        if (locomotion.IsLeaping) { _jumpAirborne = true; return; } // в полёте — ждём
        if (!_jumpAirborne) return;                                 // кадр до взлёта

        // Приземлился.
        FireHitbox(_def);
        _hitFired = true;
        _phaseTimer = _def.active; // короткое окно урона после удара о землю
    }

    private void EnterRecover()
    {
        _phase = Phase.Recover;
        _phaseTimer = _def.recover;
    }

    // ===================== Урон =====================

    private void FireHitbox(AttackDef def)
    {
        if (hitbox == null) return;

        Vector3 dir = perception.HasPlayer
            ? Flat(perception.PlayerPos - transform.position)
            : transform.forward;

        hitbox.Activate(def.range, def.radius, def.height, def.offset, dir,
                        def.damage, def.stagger, targetLayers, def.active, hitTickInterval);
    }

    // ===================== Утилиты =====================

    private AttackDef DefOf(AttackKind kind)
    {
        switch (kind)
        {
            case AttackKind.Jump: return jump;
            case AttackKind.Special: return special;
            default: return swipe;
        }
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-4f ? Vector3.forward : v.normalized;
    }
}
