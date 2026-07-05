using UnityEngine;
public class PlayerResources : MonoBehaviour, IDamageable
{
    [Header("Здоровье")]
    public float maxHealth = 100f;
    public float healthRegenPerSecond = 0f;
    public float healthRegenDelay = 3f;
    [Header("Стамина")]
    public float maxStamina = 100f;
    public float staminaRegenPerSecond = 25f;
    public float staminaRegenDelay = 0.5f;

    [Header("Мана")]
    public float maxMana = 100f;
    public float manaRegenPerSecond = 15f;
    public float manaRegenDelay = 0.3f;

    // Публичные свойства
    public float CurrentHealth { get; private set; }
    public float CurrentStamina { get; private set; }
    public float CurrentMana { get; private set; }

    public float HealthPercent => CurrentHealth / maxHealth;
    public float StaminaPercent => CurrentStamina / maxStamina;
    public float ManaPercent => CurrentMana / maxMana;

    public bool HasStamina(float amount) => CurrentStamina >= amount;
    public bool HasMana(float amount) => CurrentMana >= amount;
    public bool IsAlive => CurrentHealth > 0;

    // Таймеры регенерации
    private float healthRegenTimer;
    private float staminaRegenTimer;
    private float manaRegenTimer;

    // События
    public System.Action<float> onHealthChanged;
    public System.Action<float> onStaminaChanged;
    public System.Action<float> onManaChanged;
    public System.Action onDeath;

    void Awake()
    {
        CurrentHealth = maxHealth;
        CurrentStamina = maxStamina;
        CurrentMana = maxMana;
    }

    void Update()
    {
        RegenHealth();
        RegenStamina();
        RegenMana();
    }

    // ==================== ЗДОРОВЬЕ ====================

    void RegenHealth()
    {
        if (healthRegenTimer > 0f)
        {
            healthRegenTimer -= Time.deltaTime;
            return;
        }
        if (CurrentHealth < maxHealth && healthRegenPerSecond > 0)
        {
            float old = CurrentHealth;
            CurrentHealth = Mathf.Min(maxHealth, CurrentHealth + healthRegenPerSecond * Time.deltaTime);
            if (CurrentHealth != old) onHealthChanged?.Invoke(CurrentHealth);
        }
    }

    public void TakeDamage(float amount)
    {
        if (!IsAlive) return;

        CurrentHealth -= amount;
        healthRegenTimer = healthRegenDelay;
        onHealthChanged?.Invoke(CurrentHealth);

        if (CurrentHealth <= 0)
        {
            CurrentHealth = 0;
            onDeath?.Invoke();
        }
    }

    public void ApplyKnockback(Vector3 force)
    {
        // Заглушка: урон проходит через TakeDamage.
        // Отброс подключим к PlayerMovement3D позже — как договаривались.
    }

    public void Heal(float amount)
    {
        CurrentHealth = Mathf.Min(maxHealth, CurrentHealth + amount);
        onHealthChanged?.Invoke(CurrentHealth);
    }

    // ==================== СТАМИНА ====================

    void RegenStamina()
    {
        if (staminaRegenTimer > 0f)
        {
            staminaRegenTimer -= Time.deltaTime;
            return;
        }
        if (CurrentStamina < maxStamina)
        {
            float old = CurrentStamina;
            CurrentStamina = Mathf.Min(maxStamina, CurrentStamina + staminaRegenPerSecond * Time.deltaTime);
            if (CurrentStamina != old) onStaminaChanged?.Invoke(CurrentStamina);
        }
    }

    public bool SpendStamina(float amount)
    {
        if (CurrentStamina >= amount)
        {
            CurrentStamina -= amount;
            staminaRegenTimer = staminaRegenDelay;
            onStaminaChanged?.Invoke(CurrentStamina);
            return true;
        }
        return false;
    }

    // ==================== МАНА ====================

    void RegenMana()
    {
        if (manaRegenTimer > 0f)
        {
            manaRegenTimer -= Time.deltaTime;
            return;
        }
        if (CurrentMana < maxMana)
        {
            float old = CurrentMana;
            CurrentMana = Mathf.Min(maxMana, CurrentMana + manaRegenPerSecond * Time.deltaTime);
            if (CurrentMana != old) onManaChanged?.Invoke(CurrentMana);
        }
    }

    public bool SpendMana(float amount)
    {
        if (CurrentMana >= amount)
        {
            CurrentMana -= amount;
            manaRegenTimer = manaRegenDelay;
            onManaChanged?.Invoke(CurrentMana);
            return true;
        }
        return false;
    }
}
