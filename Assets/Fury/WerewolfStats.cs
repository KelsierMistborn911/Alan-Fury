using UnityEngine;

/// <summary>
/// Параметры оборотня. Пока только стамина — вынесена сюда из WerewolfCombat,
/// чтобы бой не держал ресурсы сам. Здоровье/смерть (IDamageable) добавим сюда же позже.
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
    [Tooltip("Норма агрессии (0..100). Рана временно снижает текущую (страх), затем возвращается к норме.")]
    [Range(0f, 100f)] public float aggression = 50f;
    [Tooltip("Насколько одна рана снижает агрессию.")]
    public float woundFear = 40f;
    [Tooltip("За сколько секунд страх от одной раны полностью уходит.")]
    public float fearDecayTime = 5f;

    private float _stamina;
    private float _regenTimer;
    private float _health;
    private float _fear; // накопленный страх от ран, 0..100
    private WerewolfLocomotion _locomotion;

    public float Stamina => _stamina;
    public float StaminaPercent => maxStamina > 0f ? _stamina / maxStamina : 0f;
    public float Health => _health;
    public bool IsAlive => _health > 0f;

    /// <summary>Текущая агрессия с учётом страха, нормированная 0..1 (для мозга).</summary>
    public float Aggression01 => Mathf.Clamp01((aggression - _fear) / 100f);

    /// <summary>Срабатывает один раз при смерти (мозг гасит компоненты, тело остаётся).</summary>
    public System.Action OnDeath;

    /// <summary>Изменить норму агрессии (событиями боя: попал и т.п.). Зажимается 0..100.</summary>
    public void AddAggression(float delta)
    {
        aggression = Mathf.Clamp(aggression + delta, 0f, 100f);
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
        _fear = Mathf.Min(100f, _fear + woundFear); // рана пугает → осторожное поведение
        DamagePopup.Spawn(transform.position + Vector3.up * 2f, amount, Color.white);
        if (_health <= 0f) OnDeath?.Invoke();
    }

    public void ApplyKnockback(Vector3 force)
    {
        if (_locomotion != null) _locomotion.AddImpulse(force);
    }

    void Update()
    {
        // Страх от ран уходит со временем — агрессия возвращается к норме.
        if (_fear > 0f && fearDecayTime > 0f)
            _fear = Mathf.MoveTowards(_fear, 0f, (woundFear / fearDecayTime) * Time.deltaTime);

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
