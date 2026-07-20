using UnityEngine;

/// <summary>
/// Параметры оборотня: здоровье, стамина, агрессия.
/// Агрессия — накопительная (0..100): старт 0, растёт со скоростью aggressionPerSecond,
/// пока волк в роли Attack (начисляет WerewolfPackBrain). Личного страха больше нет:
/// урон по волку уходит в страх СТАИ (WerewolfPackManager.ReportWound), который
/// разово срезает агрессию всем волкам.
/// </summary>
public class WerewolfStats : MonoBehaviour, IDamageable
{
    [Header("Здоровье")]
    public float maxHealth = 30f;

    [Header("Стамина")]
    public float maxStamina = 100f;
    public float staminaRegenPerSecond = 15f;
    [Tooltip("Задержка перед началом регена после траты (сек).")]
    public float staminaRegenDelay = 1f;

    [Header("Агрессия")]
    [Tooltip("Скорость накопления агрессии в роли Attack (ед/сек). Начисляет WerewolfPackBrain; вне атаки значение замирает.")]
    public float aggressionPerSecond = 5f;

    private float _stamina;
    private float _regenTimer;
    private float _health;
    private float _aggression; // накопленная агрессия 0..100, старт 0
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

    /// <summary>Срабатывает один раз при смерти (мозг гасит компоненты, тело остаётся).</summary>
    public System.Action OnDeath;

    /// <summary>Изменить агрессию (накопление, свои попадания, страх стаи). Зажимается 0..100.</summary>
    public void AddAggression(float delta)
    {
        _aggression = Mathf.Clamp(_aggression + delta, 0f, 100f);
    }

    void Awake()
    {
        _stamina = maxStamina;
        _health = maxHealth;
        _locomotion = GetComponent<WerewolfLocomotion>();
    }

    // ===================== IDamageable (урон от меча игрока через WeaponHitbox) =====================

    public void TakeDamage(float amount)
    {
        if (!IsAlive) return;
        _health = Mathf.Max(0f, _health - amount);

        // Рана пугает всю стаю: страх стаи += урон, агрессия ВСЕХ волков −= урон.
        if (WerewolfPackManager.Instance != null)
            WerewolfPackManager.Instance.ReportWound(amount);

        DamagePopup.Spawn(transform.position + Vector3.up * 2f, amount, Color.white);
        if (_health <= 0f) OnDeath?.Invoke();
    }

    public void ApplyKnockback(Vector3 force)
    {
        if (_locomotion != null) _locomotion.AddImpulse(force);
    }

    void Update()
    {
        if (_regenTimer > 0f) { _regenTimer -= Time.deltaTime; return; }
        if (_stamina < maxStamina)
            _stamina = Mathf.Min(maxStamina, _stamina + staminaRegenPerSecond * Time.deltaTime);
    }

    public bool HasEnough(float amount) => _stamina >= amount;

    public void Spend(float amount)
    {
        _stamina = Mathf.Max(0f, _stamina - amount);
        _regenTimer = staminaRegenDelay;
    }
}
