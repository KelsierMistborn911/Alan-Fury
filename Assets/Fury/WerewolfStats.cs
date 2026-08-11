using UnityEngine;

/// <summary>
/// Параметры оборотня: здоровье, стамина, агрессия, страх + текущие приказы стаи.
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
/// Приказы (токены и цель) пишет менеджер стаи, читает мозг. Лежат здесь, чтобы их
/// было видно в инспекторе и чтобы до них дотягивались аниматор, лог и HUD.
/// </summary>
public class WerewolfStats : MonoBehaviour, IDamageable
{
    /// <summary>Приказы от стаи. Набор — волк может держать несколько разом.</summary>
    [System.Flags]
    public enum Token
    {
        None = 0,
        Escort = 1,        // сопровождать альфу
        Surround = 2,      // точка на дуге окружения
        KeepDistance = 4,  // радиус кольца вокруг своей цели
        Attack = 8,        // право на инициативу: сближаться ради удара
        Hurry = 16,        // спешит — гейт на ступень выше
        Flee = 32          // бегство
    }

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

    [Header("Приказы стаи (пишет менеджер)")]
    [Tooltip("Своя цель. Задаёт кольцо с мин. и макс. дистанцией. Пусто — волк вне драки.")]
    public Transform target;
    [Tooltip("Набор активных токенов. Приоритет между ними разбирает мозг.")]
    public Token tokens = Token.None;

    private float _stamina;
    private float _regenTimer;
    private float _health;
    private float _aggression; // накопленная агрессия 0..100, старт 0
    private float _fear;       // личный страх 0..100, старт 0
    private float _slowTimer;
    private ApexHolder _apex = ApexHolder.None;
    private WerewolfLocomotion _locomotion;

    public float Stamina => _stamina;
    public float StaminaPercent => maxStamina > 0f ? _stamina / maxStamina : 0f;
    public float Health => _health;
    public float HealthPercent => maxHealth > 0f ? _health / maxHealth : 0f;
    public bool IsAlive => _health > 0f;

    /// <summary>Текущая агрессия 0..100 (для HUD).</summary>
    public float Aggression => _aggression;
    /// <summary>Текущая агрессия, нормированная 0..1 (для мозга).</summary>
    public float Aggression01 => _aggression / 100f;

    /// <summary>Текущий страх 0..100 (для HUD).</summary>
    public float Fear => _fear;
    /// <summary>Текущий страх, нормированный 0..1 (для мозга).</summary>
    public float Fear01 => _fear / 100f;
    /// <summary>Ступень страха: 0..24 спокоен, 25..49 насторожен, 50..74 напуган, 75+ ужас.
    /// Если вершину уже держит агрессия — упирается в «напуган».</summary>
    public FearTier Tier => (FearTier)TierIndex(_fear, _apex == ApexHolder.Aggression);

    /// <summary>Ступень агрессии. Если вершину держит страх — упирается в «злой».</summary>
    public AggressionTier AggroTier => (AggressionTier)TierIndex(_aggression, _apex == ApexHolder.Fear);

    /// <summary>Кто сейчас держит вершину (лог, HUD).</summary>
    public ApexHolder Apex => _apex;

    /// <summary>
    /// Настроение 1–5 из двух шкал. Afraid+ (Tier ≥ Afraid) → Fleeish.
    /// Иначе по разнице Aggro01−Fear01 и уровню.
    /// </summary>
    public CombatMood Mood
    {
        get
        {
            if (Tier >= FearTier.Afraid) return CombatMood.Fleeish;
            float d = Aggression01 - Fear01; // −1..+1
            float peak = Mathf.Max(Aggression01, Fear01);
            if (d > 0.35f && Aggression01 >= 0.55f) return CombatMood.Rage;
            if (d > 0.12f) return CombatMood.Aggressive;
            if (d < -0.12f || Fear01 > 0.4f) return CombatMood.Skittish;
            if (peak < 0.2f) return CombatMood.Aggressive; // оба низкие — спокойный пресс
            return CombatMood.Tense;
        }
    }

    private static int TierIndex(float value, bool capped)
        => Mathf.Clamp(Mathf.FloorToInt(value / FearTierStep), 0, capped ? 2 : 3);

    /// <summary>Есть ли своя цель — по этому же признаку страх тянется к боевому уровню.</summary>
    public bool InCombat => target != null;
    /// <summary>К какому уровню страх сейчас возвращается.</summary>
    public float BaseFear => InCombat ? baseFearCombat : baseFearIdle;

    /// <summary>Срабатывает один раз при смерти (мозг гасит компоненты, тело остаётся).</summary>
    public System.Action OnDeath;

    // ===================== Токены =====================

    /// <summary>Держит ли волк этот токен (можно проверять несколько разом).</summary>
    public bool Has(Token t) => (tokens & t) != 0;

    /// <summary>Выдать токен(ы). Зовёт менеджер стаи.</summary>
    public void GrantToken(Token t) => tokens |= t;

    /// <summary>Забрать токен(ы). Зовёт менеджер стаи или сам волк, когда отказывается.</summary>
    public void RevokeToken(Token t) => tokens &= ~t;

    /// <summary>Заменить весь набор разом.</summary>
    public void SetTokens(Token t) => tokens = t;

    // ===================== Агрессия и страх =====================

    /// <summary>Изменить агрессию (накопление, свои попадания, чужая рана рядом). Зажимается 0..100.</summary>
    public void AddAggression(float delta)
    {
        _aggression = Mathf.Clamp(_aggression + delta, 0f, 100f);
        RefreshApex();
    }

    /// <summary>Изменить страх напрямую. Зажимается 0..100.</summary>
    public void AddFear(float delta)
    {
        _fear = Mathf.Clamp(_fear + delta, 0f, 100f);
        RefreshApex();
    }

    /// <summary>Кто первым перевалил 75, тот держит вершину, пока сам не упадёт ниже.
    /// Если обе шкалы перескочили порог в один кадр — вершину берёт большая, при равенстве страх.</summary>
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

    /// <summary>
    /// Поднять страх на N ступеней (по 25 единиц). Позиция внутри ступени сохраняется:
    /// волк с 20 после +2 окажется на 70. Этим бьёт срыв стаи.
    /// </summary>
    public void AddFearTiers(int tiers) => AddFear(FearTierStep * tiers);

    void Awake()
    {
        _stamina = maxStamina;
        _health = maxHealth;
        _fear = baseFearIdle;
        _locomotion = GetComponent<WerewolfLocomotion>();
        // Фаза тика разъезжается по волкам, чтобы вся стая не пересчитывалась в один кадр.
        _slowTimer = Random.value * slowTickInterval;
    }

    // ===================== IDamageable (урон от меча игрока через WeaponHitbox) =====================

    public void TakeDamage(float amount)
    {
        if (!IsAlive) return;
        _health = Mathf.Max(0f, _health - amount);

        // Своя рана пугает лично (мгновенно, мимо редкого тика) и копится в страх стаи.
        AddFear(amount);
        if (WerewolfPackManager.Instance != null)
            WerewolfPackManager.Instance.ReportWound(amount, transform.position, transform);

        DamagePopup.Spawn(transform.position + Vector3.up * 2f, amount, Color.white);
        if (_health <= 0f) OnDeath?.Invoke();
    }

    public void ApplyKnockback(Vector3 force)
    {
        if (_locomotion != null) _locomotion.AddImpulse(force);
    }

    void Update()
    {
        float dt = Time.deltaTime;

        // --- Стамина: как было, каждый кадр ---
        if (_regenTimer > 0f) _regenTimer -= dt;
        else if (_stamina < maxStamina)
            _stamina = Mathf.Min(maxStamina, _stamina + staminaRegenPerSecond * dt);

        // --- Дрейф страха: редким тиком ---
        _slowTimer -= dt;
        if (_slowTimer <= 0f)
        {
            float step = slowTickInterval;
            _slowTimer += slowTickInterval;
            SlowTick(step);
        }
    }

    /// <summary>Медленные изменения. Зовётся раз в slowTickInterval, а не каждый кадр.</summary>
    private void SlowTick(float step)
    {
        _fear = Mathf.MoveTowards(_fear, BaseFear, fearRegenPerSecond * step);
    }

    public bool HasEnough(float amount) => _stamina >= amount;

    public void Spend(float amount)
    {
        _stamina = Mathf.Max(0f, _stamina - amount);
        _regenTimer = staminaRegenDelay;
    }
}
