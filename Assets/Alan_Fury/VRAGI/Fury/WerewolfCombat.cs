using UnityEngine;

/// <summary>
/// Боевой исполнитель оборотня: три атаки от замаха + собственная стамина.
///
/// Разделение обязанностей:
///   • МОЗГ (WerewolfAttackBrain) решает, ЧТО ударить по дистанции, и зовёт
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
/// Особый — короткий укус за ногу в упоре (сзади приоритет). Hop/bounce как были.
/// </summary>
[RequireComponent(typeof(NpcPerception))]
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
    public NpcPerception perception;
    public WerewolfLocomotion locomotion;
    [Tooltip("Общий WeaponHitbox на волке (или в детях). targetLayers ниже — слой игрока.")]
    public WeaponHitbox hitbox;
    [Tooltip("Исполнение удара. Пусто — найдётся / добавится.")]
    public MeleeAction melee;
    [Tooltip("Параметры оборотня (стамина). Пусто — найдётся на волке.")]
    public WerewolfStats stats;

    [Header("Цель урона")]
    [Tooltip("Слой(и), по которым бьёт волк. Поставь слой игрока.")]
    public LayerMask targetLayers;
    [Tooltip("Тик урона внутри окна active (сек). Меньше — может задеть несколько раз за окно.")]
    public float hitTickInterval = 0.2f;

    [Header("Прыжковая атака")]
    [Tooltip("Высота дуги прыжка (передаётся в locomotion.Leap).")]
    public float jumpArc = 2.2f;
    [Tooltip("Сколько секунд хитбокс прыжка активен с момента отрыва (должно покрывать весь полёт).")]
    public float jumpHitDuration = 1.5f;
    [Tooltip("Максимальная дистанция наскока (м): с этого расстояния мозг решается прыгнуть. Не путать с range хитбокса ниже.")]
    public float jumpLeapDistance = 8f;
    public AttackDef jump = new AttackDef
    {
        windup = 0.45f,
        active = 0.20f,
        recover = 0.50f,
        cooldown = 2.5f,
        staminaCost = 28f,
        range = 2.0f,
        radius = 1.2f,
        height = 2f,
        offset = new Vector3(0f, 1f, 0f),
        damage = 20f,
        stagger = 6f
    };

    [Header("Обычный удар / серия")]
    [Tooltip("Импульс рывка в игрока в момент свипа (м/с). 0 = выключено. Волк «проваливается» в сторону удара.")]
    public float swipeLungeImpulse = 5f;
    [Tooltip("Макс. длина серии (сколько свипов подряд).")]
    public int maxCombo = 3;
    [Tooltip("Окно после свипа, в котором повторный TrySwipe продолжает серию (сек).")]
    public float comboWindow = 0.6f;
    public AttackDef swipe = new AttackDef
    {
        windup = 0.5f,
        active = 0.15f,
        recover = 0.35f,
        cooldown = 0.10f,
        staminaCost = 16f,
        range = 3.0f,
        radius = 1.0f,
        height = 2f,
        offset = new Vector3(0f, 1f, 0f),
        damage = 10f,
        stagger = 3f
    };

    [Header("Особый удар (подлый, сзади)")]
    [Tooltip("Импульс подскока вперёд в момент удара (м/с). 0 = без hop.")]
    public float specialHopImpulse = 6.5f;
    [Tooltip("Импульс отскока назад после удара (м/с). 0 = без bounce.")]
    public float specialBounceImpulse = 8f;
    public AttackDef special = new AttackDef
    {
        windup = 0.80f,
        active = 0.25f,
        recover = 0.70f,
        cooldown = 4f,
        staminaCost = 36f,
        range = 1.6f,
        radius = 0.7f,
        height = 1.4f,
        offset = new Vector3(0f, 0.6f, 0.35f),
        damage = 30f,
        stagger = 8f
    };

    // ===================== Состояние =====================
    private enum Phase { Idle, Windup, Active, Recover }
    private Phase _phase = Phase.Idle;
    private AttackKind _kind;
    private AttackDef _def;
    private float _phaseTimer;

    // урон в active один раз (EnterActive); отдельный флаг не нужен
    private bool _jumpAirborne;  // волк оторвался от земли в прыжке

    private readonly float[] _cooldownUntil = new float[3]; // по индексу AttackKind

    private int _combo;
    private float _comboExpire;

    // ===================== Публичное для мозга =====================
    public bool IsBusy => _phase != Phase.Idle;
    /// <summary>Идёт замах. Под индикатор замаха волка (микроспрайт у головы).</summary>
    public bool IsWindingUp => _phase == Phase.Windup;

    /// <summary>Срабатывает, когда удар этого волка нанёс урон (мозг цепляет для агрессии и т.п.).</summary>
    public System.Action OnHitLanded;

    /// <summary>Хватает ли стамины и вышел ли кулдаун на конкретную атаку (для решений мозга).</summary>
    public bool CanStart(AttackKind kind)
    {
        if (_phase != Phase.Idle) return false;
        // Волк встаёт на две ноги / опускается на четыре — руки заняты, ударить нечем.
        if (locomotion != null && locomotion.IsChangingStance) return false;
        if (Time.time < _cooldownUntil[(int)kind]) return false;
        return stats != null && stats.HasEnough(DefOf(kind).staminaCost);
    }

    public bool TryJump() => TryStart(AttackKind.Jump, jump);
    public bool TrySwipe() => TryStart(AttackKind.Swipe, swipe);
    public bool TrySpecial() => TryStart(AttackKind.Special, special);

    // ===================== Жизненный цикл =====================

    void Start()
    {
        if (perception == null) perception = GetComponent<NpcPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
        if (melee == null) melee = GetComponent<MeleeAction>();
        if (melee == null) melee = gameObject.AddComponent<MeleeAction>();
        if (melee.hitbox == null) melee.hitbox = hitbox;
        if (hitbox == null)
            Debug.LogWarning("WerewolfCombat: не назначен WeaponHitbox — удары не нанесут урон.");
        if (stats == null) stats = GetComponent<WerewolfStats>();
        if (stats == null)
            Debug.LogWarning("WerewolfCombat: не найден WerewolfStats — атаки не будут проверять стамину.");

        if (targetLayers.value == 0)
        {
            Transform p = perception != null ? perception.player : null;
            if (p == null && PlayerRegistry.Instance != null)
                p = PlayerRegistry.Instance.GetNearest(transform.position);
            if (p != null)
                targetLayers = 1 << p.gameObject.layer;
        }

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

        // Триггеры шлём через локомоцию — она проверяет, есть ли параметр в контроллере,
        // чтобы Unity не сыпал ошибками, пока анимации ещё не добавлены.
        // Jump сюда не попадает намеренно: анимацию наскока шлёт locomotion.Leap
        // в EnterActive. Иначе на один прыжок летели бы два триггера подряд.
        if (locomotion != null)
        {
            switch (_kind)
            {
                case AttackKind.Swipe: locomotion.PlayAttack("Swipe"); break;
                case AttackKind.Special: locomotion.PlayAttack("Special"); break;
            }
        }
    }

    private void EnterActive()
    {
        _phase = Phase.Active;
        _phaseTimer = _def.active;

        if (_kind == AttackKind.Jump)
        {
            // Прыгаем в игрока; хитбокс активен весь полёт и летит с волком.
            if (perception.HasPlayer && locomotion.IsGrounded)
            {
                locomotion.Leap(perception.PlayerPos, jumpArc);
                FireHitbox(_def, jumpHitDuration);
            }
            else
            {
                // Прыгнуть не смогли (не на земле / нет игрока) — бьём на месте.
                FireHitbox(_def);
            }
        }
        else
        {
            // Свип: небольшой рывок в игрока — удар «на ходу» (каждый свип серии — новый рывок).
            if (_kind == AttackKind.Swipe && swipeLungeImpulse > 0f)
            {
                Vector3 lungeDir = perception.HasPlayer
                    ? Flat(perception.PlayerPos - transform.position)
                    : transform.forward;
                locomotion.AddImpulse(lungeDir * swipeLungeImpulse);
            }
            // Special: подскок вперёд в момент удара.
            else if (_kind == AttackKind.Special && specialHopImpulse > 0f)
            {
                Vector3 hopDir = perception.HasPlayer
                    ? Flat(perception.PlayerPos - transform.position)
                    : transform.forward;
                locomotion.AddImpulse(hopDir * specialHopImpulse);
            }

            // Свип / особый — урон в начале окна active.
            FireHitbox(_def);
        }
    }

    // Прыжок: урон включён с отрыва (EnterActive). Здесь ждём приземления,
    // после него — короткое окно active и recover.
    private void TickJumpActive(float dt)
    {
        if (locomotion.IsLeaping) { _jumpAirborne = true; return; } // в полёте — ждём
        if (_jumpAirborne)
        {
            _jumpAirborne = false;
            _phaseTimer = _def.active; // приземлился — короткое окно перед recover
        }

        _phaseTimer -= dt;
        if (_phaseTimer <= 0f) EnterRecover();
    }

    private void EnterRecover()
    {
        _phase = Phase.Recover;
        _phaseTimer = _def.recover;

        // Special: отскок назад сразу после удара (подлый уход).
        if (_kind == AttackKind.Special && specialBounceImpulse > 0f && locomotion != null)
        {
            Vector3 away = perception != null && perception.HasPlayer
                ? Flat(transform.position - perception.PlayerPos)
                : -transform.forward;
            locomotion.AddImpulse(away * specialBounceImpulse);
        }
    }

    // ===================== Урон =====================

    private void FireHitbox(AttackDef def, float durationOverride = 0f)
    {
        if (hitbox == null) return;

        // Бьём туда, куда СМОТРИМ. Раньше удар летел в игрока независимо от разворота —
        // волк попадал спиной, и обойти его было нельзя. Теперь обход = промах.
        // Наскок — исключение: там направление задаёт сам прыжок.
        Vector3 dir = _kind == AttackKind.Jump && perception.HasPlayer
            ? Flat(perception.PlayerPos - transform.position)
            : transform.forward;

        CombatRange band = CombatRange.Mid;
        HitZoneShape shape = HitZoneShape.Sector;
        float cone = 52f;
        float inner = def.range * 0.28f;
        float yaw = 0f;
        if (_kind == AttackKind.Jump)
        {
            band = CombatRange.Close;
            shape = HitZoneShape.Ellipse;
            cone = -1f;
            inner = 0f;
        }
        else if (_kind == AttackKind.Special)
        {
            band = CombatRange.Close;
            shape = HitZoneShape.Ellipse;
            cone = -1f;
            inner = 0f;
        }

        HitInfo info = HitInfo.Basic(def.damage, transform.position);
        info.hitDirection = dir;
        info.stagger = def.stagger;
        if (_kind == AttackKind.Special)
            info.zone = BiteLegZone();

        var req = new MeleeAction.Request
        {
            band = band,
            range = def.range,
            radius = def.radius,
            height = def.height,
            offset = def.offset,
            direction = dir,
            damage = def.damage,
            stagger = def.stagger,
            layers = targetLayers,
            duration = durationOverride > 0f ? durationOverride : def.active,
            tick = hitTickInterval,
            cone = cone,
            shape = shape,
            innerRadius = inner,
            yawOffset = yaw,
            info = info,
            target = perception != null && perception.HasPlayer ? perception.player : null
        };

        if (melee != null) melee.Play(req);
        else if (hitbox != null)
        {
            hitbox.SetHitInfo(info);
            hitbox.Activate(req.range, req.radius, req.height, req.offset, dir,
                req.damage, req.stagger, targetLayers, req.duration, hitTickInterval,
                0f, 0, req.cone, req.shape, req.innerRadius, req.yawOffset);
        }
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

    private BodyZone BiteLegZone()
    {
        if (perception == null || !perception.HasPlayer)
            return BodyZone.LeftLeg;
        Vector3 toWolf = Flat(transform.position - perception.PlayerPos);
        Vector3 playerRight = Vector3.Cross(Vector3.up, perception.PlayerForwardFlat);
        return Vector3.Dot(toWolf, playerRight) >= 0f ? BodyZone.RightLeg : BodyZone.LeftLeg;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-4f ? Vector3.forward : v.normalized;
    }
}
