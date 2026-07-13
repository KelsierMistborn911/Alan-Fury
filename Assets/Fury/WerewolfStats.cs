using UnityEngine;

/// <summary>
/// Параметры оборотня. Пока только стамина — вынесена сюда из WerewolfCombat,
/// чтобы бой не держал ресурсы сам. Здоровье/смерть (IDamageable) добавим сюда же позже.
/// </summary>
public class WerewolfStats : MonoBehaviour
{
    [Header("Стамина")]
    public float maxStamina = 100f;
    public float staminaRegenPerSecond = 15f;
    [Tooltip("Задержка перед началом регена после траты (сек).")]
    public float staminaRegenDelay = 1f;

    [Header("Агрессия")]
    [Tooltip("0 = осторожный (контроль дистанции, фланг, чаще уворот). 1 = борзый (чаще и множественнее атакует). Меняется по ходу боя через AddAggression.")]
    [Range(0f, 1f)] public float aggression = 0.5f;

    private float _stamina;
    private float _regenTimer;

    public float Stamina => _stamina;
    public float StaminaPercent => maxStamina > 0f ? _stamina / maxStamina : 0f;

    /// <summary>Изменить агрессию (событиями боя: попал/получил урон/зашёл сбоку и т.п.). Зажимается 0..1.</summary>
    public void AddAggression(float delta)
    {
        aggression = Mathf.Clamp01(aggression + delta);
    }

    void Awake()
    {
        _stamina = maxStamina;
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
