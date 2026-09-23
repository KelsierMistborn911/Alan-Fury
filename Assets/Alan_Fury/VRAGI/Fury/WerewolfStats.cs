using UnityEngine;

/// <summary>
/// Параметры оборотня: здоровье, стамина, агрессия, страх.
///
/// Агрессия — накопительная (0..100): старт 0, растёт со скоростью aggressionPerSecond,
/// пока волк в роли Attack (начисляет WerewolfPackBrain). Раны её больше НЕ режут.
///
/// Страх — личный (0..100), четыре ступени по 25: спокоен / насторожен / напуган / ужас.
/// Своя рана поднимает страх на величину урона и столько же уходит в страх СТАИ
/// (WerewolfPackManager.ReportWound). Когда страх стаи упирается в максимум — вся стая
/// разом получает +2 ступени, а страх стаи падает вдвое (вторая волна дешевле первой).
/// Вне драки страх тянется к 0, в драке — к 25 (нижняя боевая ступень).
///
/// Цель (target) — опорная точка для кольца. Роли/слоты атаки выдаёт WerewolfPackManager.
/// </summary>
public class WerewolfStats : MonoBehaviour, IDamageable
{
    /// <summary>Ступени страха. Шаг — 25 единиц.</summary>
    public enum FearTier { Calm, Wary, Afraid, Terror }

    /// <summary>Ступени агрессии, тот же шаг 25: осторожен / средний / злой / ярость.</summary>
    public enum AggressionTier { Cautious, Mid, Fierce, Rage }

    /// <summary>Боевое настроение из Fear+Aggression (не отдельная шкала).
    /// Rage…Skittish — как дерётся; Fleeish — не бьёт, AttackBrain сдаёт слот в Surround.</summary>
    public enum CombatMood { Rage, Aggressive, Tense, Skittish, Fleeish }

    /// <summary>Кто первым занял четвёртую ступень — второй упирается в третью.</summary>
    public enum ApexHolder { None, Fear, Aggression }

    public const float FearTierStep = 25f;
    /// <summary>Порог четвёртой ступени (75). Общий для обеих шкал.</summary>
    public const float ApexThreshold = FearTierStep * 3f;

    [Header("Здоровье")]
    public float maxHealth = 30f;
    [Tooltip("Реген HP сразу, даже под ударом и кровотечением. 0 = выкл.")]
    public float healthRegenPerSecond = 1.5f;
    [Tooltip("Устарело. Паузы регена больше нет.")]
    public float healthRegenDelay = 0f;

    [Header("Стамина")]
    public float maxStamina = 60f;
    public float staminaRegenPerSecond = 12f;
    [Tooltip("Задержка перед началом регена после траты (сек).")]
    public float staminaRegenDelay = 1f;

    [Header("Агрессия")]
    [Tooltip("Скорость накопления агрессии в роли Attack (ед/сек). Начисляет WerewolfPackBrain; вне атаки значение замирает.")]
    public float aggressionPerSecond = 1.5f;

    [Header("Страх")]
    [Tooltip("К какому уровню страх тянется вне драки (цели нет).")]
    public float baseFearIdle = 0f;
    [Tooltip("К какому уровню страх тянется в драке (есть цель). 25 = нижняя боевая ступень «насторожен».")]
    public float baseFearCombat = 25f;
    [Tooltip("Скорость возврата страха к базовому уровню (ед/сек).")]
    public float fearRegenPerSecond = 3f;

    [Header("Редкий пересчёт")]
    [Tooltip("Как часто считается дрейф страха (сек). Урон действует мгновенно, мимо этого тика.")]
    public float slowTickInterval = 1f;

    [Header("Масса")]
    [Tooltip("Тяжелее — меньше отброс. Игрок = 80.")]
    public float mass = 100f;

    [Header("Прерывание")]
    [Tooltip("Срыв атаки волка, если урон не меньше.")]
    public float interruptMinDamage = 8f;
    [Tooltip("Срыв атаки волка, если stagger не меньше.")]
    public float interruptMinStagger = 3.5f;

    [Header("Цель")]
    [Tooltip("Своя цель. Задаёт кольцо с мин. и макс. дистанцией. Пусто — волк вне драки.")]
    public Transform target;

    private float _stamina;
    private float _regenTimer;
    private float _health;
    private float _aggression;
    private float _fear;
    private float _slowTimer;
    private ApexHolder _apex = ApexHolder.None;
    private WerewolfLocomotion _locomotion;
    private WerewolfCombat _combat;
    private WoundTracker _wounds;

    public float Stamina => _stamina;
    public float StaminaPercent => maxStamina > 0f ? _stamina / maxStamina : 0f;
    public float Health => _health;
    public float HealthPercent => maxHealth > 0f ? _health / maxHealth : 0f;
    public bool IsAlive => _health > 0f;
    public float Aggression => _aggression;
    public float Aggression01 => Mathf.Clamp01(_aggression / 100f);
    public float Fear01 => Mathf.Clamp01(_fear / 100f);

    public FearTier Tier
    {
        get
        {
            if (_fear >= ApexThreshold) return FearTier.Terror;
            if (_fear >= FearTierStep * 2f) return FearTier.Afraid;
            if (_fear >= FearTierStep) return FearTier.Wary;
            return FearTier.Calm;
        }
    }

    public AggressionTier AggroTier
    {
        get
        {
            if (_aggression >= ApexThreshold) return AggressionTier.Rage;
            if (_aggression >= FearTierStep * 2f) return AggressionTier.Fierce;
            if (_aggression >= FearTierStep) return AggressionTier.Mid;
            return AggressionTier.Cautious;
        }
    }

    public CombatMood Mood
    {
        get
        {
            if (Tier == FearTier.Terror && _apex == ApexHolder.Fear)
                return CombatMood.Fleeish;
            if (AggroTier == AggressionTier.Rage && _apex == ApexHolder.Aggression)
                return CombatMood.Rage;
            if (Tier >= FearTier.Afraid && AggroTier <= AggressionTier.Mid)
                return CombatMood.Skittish;
            if (Tier >= FearTier.Wary && AggroTier <= AggressionTier.Fierce)
                return CombatMood.Tense;
            if (AggroTier >= AggressionTier.Fierce)
                return CombatMood.Aggressive;
            return CombatMood.Aggressive;
        }
    }

    public System.Action OnDeath;

    public bool HasEnough(float cost) => _stamina >= cost;

    public void Spend(float cost)
    {
        if (cost <= 0f) return;
        _stamina = Mathf.Max(0f, _stamina - cost);
        _regenTimer = staminaRegenDelay;
    }

    public void AddFear(float amount)
    {
        if (amount == 0f) return;
        _fear = Mathf.Clamp(_fear + amount, 0f, 100f);
        RefreshApex();
        if (_apex == ApexHolder.Aggression)
            _fear = Mathf.Min(_fear, ApexThreshold - 0.01f);
    }

    public void AddFearTiers(int tiers) => AddFear(FearTierStep * tiers);

    public void AddAggression(float delta)
    {
        if (delta == 0f) return;
        _aggression = Mathf.Clamp(_aggression + delta, 0f, 100f);
        RefreshApex();
        if (_apex == ApexHolder.Fear)
            _aggression = Mathf.Min(_aggression, ApexThreshold - 0.01f);
    }

    private void RefreshApex()
    {
        bool fearApex = _fear >= ApexThreshold;
        bool aggroApex = _aggression >= ApexThreshold;

        if (_apex == ApexHolder.Fear && !fearApex) _apex = ApexHolder.None;
        else if (_apex == ApexHolder.Aggression && !aggroApex) _apex = ApexHolder.None;
        if (_apex != ApexHolder.None) return;

        if (fearApex && aggroApex) _apex = _aggression > _fear ? ApexHolder.Aggression : ApexHolder.Fear;
        else if (fearApex) _apex = ApexHolder.Fear;
        else if (aggroApex) _apex = ApexHolder.Aggression;
    }

    void Awake()
    {
        _stamina = maxStamina;
        _health = maxHealth;
        _fear = baseFearIdle;
        _locomotion = GetComponent<WerewolfLocomotion>();
        _combat = GetComponent<WerewolfCombat>();
        _slowTimer = Random.value * slowTickInterval;

        _wounds = GetComponent<WoundTracker>();
        if (_wounds == null)
            _wounds = gameObject.AddComponent<WoundTracker>();
    }

    public void TakeDamage(float amount)
    {
        ApplyHealthDamage(amount, default, reportFear: true, showPopup: true);
    }

    public void TakeDamage(float amount, Vector3 sourcePosition)
    {
        TakeDamage(amount);
    }

    bool ShouldInterrupt(HitInfo hit)
    {
        float dmg = hit.finalDamage > 0f ? hit.finalDamage : hit.rawDamage;
        if (dmg >= 0.2f || hit.stagger >= 0.5f) return true;
        return dmg >= interruptMinDamage || hit.stagger >= interruptMinStagger;
    }

    public void TakeHit(HitInfo hit)
    {
        if (!IsAlive) return;
        if (_combat != null && ShouldInterrupt(hit))
            _combat.InterruptFromHit(hit.isHeavy || hit.stagger >= 5.5f || hit.kind == DamageKind.Blunt, hit);
        if (_wounds == null) _wounds = GetComponent<WoundTracker>();
        if (_wounds != null)
        {
            _wounds.ApplyHit(hit);
            return;
        }
        ApplyHealthDamage(hit.rawDamage > 0f ? hit.rawDamage : hit.finalDamage, hit, reportFear: true, showPopup: true);
    }

    public void ApplyHealthDamage(float amount, HitInfo hit = default, bool reportFear = true, bool showPopup = true)
    {
        if (!IsAlive || amount <= 0f) return;
        _health = Mathf.Max(0f, _health - amount);

        if (reportFear)
        {
            AddFear(amount);
            if (WerewolfPackManager.Instance != null)
                WerewolfPackManager.Instance.ReportWound(amount, transform.position, transform);
        }

        if (showPopup)
            DamagePopup.Spawn(transform.position + Vector3.up * 2f, amount, Color.white);

        if (_health <= 0f) OnDeath?.Invoke();
    }

    public void ApplyKnockback(Vector3 force)
    {
        if (_locomotion == null) return;
        _locomotion.ApplyHitRecoil(force, mass);
    }

    void Update()
    {
        float dt = Time.deltaTime;

        if (_regenTimer > 0f) _regenTimer -= dt;
        else if (_stamina < maxStamina)
            _stamina = Mathf.Min(maxStamina, _stamina + staminaRegenPerSecond * dt);

        if (IsAlive && healthRegenPerSecond > 0f && _health < maxHealth)
            _health = Mathf.Min(maxHealth, _health + healthRegenPerSecond * dt);

        _slowTimer -= dt;
        if (_slowTimer <= 0f)
        {
            _slowTimer = Mathf.Max(0.2f, slowTickInterval);
            float targetFear = target != null ? baseFearCombat : baseFearIdle;
            if (!Mathf.Approximately(_fear, targetFear))
            {
                _fear = Mathf.MoveTowards(_fear, targetFear, fearRegenPerSecond * Mathf.Max(0.2f, slowTickInterval));
                RefreshApex();
            }
        }
    }
}
