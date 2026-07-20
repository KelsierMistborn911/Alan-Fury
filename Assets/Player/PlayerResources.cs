using UnityEngine;
public class PlayerResources : MonoBehaviour, IDamageable
{
    [Header("Здоровье")]
    public bool invincible = false;
    public float maxHealth = 100f;
    public float healthRegenPerSecond = 0f;
    public float healthRegenDelay = 3f;
    [Header("Выносливость")]
    public float maxStamina = 100f;
    public float staminaRegenPerSecond = 25f;
    public float staminaRegenDelay = 0.5f;

    [Header("Мана")]
    public float maxMana = 100f;
    public float manaRegenPerSecond = 15f;
    public float manaRegenDelay = 0.3f;

    [Header("Оборона (блок)")]
    public float maxGuard = 50f;
    public float guardRegenPerSecond = 10f;
    public float guardRegenDelay = 1.5f;
    [Tooltip("Доля входящего урона, проходящая при блоке. 0.5 = половина.")]
    [Range(0f, 1f)] public float blockDamageMultiplier = 0.5f;
    [Tooltip("Дуга перед игроком (град.), в которой блок гасит урон. Сзади — полный урон.")]
    [Range(0f, 360f)] public float blockArcAngle = 200f;

    [Header("Импульс атаки")]
    [Tooltip("Масса персонажа для бонуса урона от скорости движения (удар на Shift / комбо с уворотом).")]
    public float mass = 80f;

    // Текущие значения
    public float CurrentHealth { get; private set; }
    public float CurrentStamina { get; private set; }
    public float CurrentMana { get; private set; }
    public float CurrentGuard { get; private set; }

    public float HealthPercent => CurrentHealth / maxHealth;
    public float StaminaPercent => CurrentStamina / maxStamina;
    public float ManaPercent => CurrentMana / maxMana;
    public float GuardPercent => CurrentGuard / maxGuard;

    public bool HasStamina(float amount) => CurrentStamina >= amount;
    public bool HasMana(float amount) => CurrentMana >= amount;
    public bool IsAlive => CurrentHealth > 0;

    // Таймеры восстановления
    private float healthRegenTimer;
    private float staminaRegenTimer;
    private float manaRegenTimer;
    private float guardRegenTimer;

    private CombatController3D _combat;

    // События
    public System.Action<float> onHealthChanged;
    public System.Action<float> onStaminaChanged;
    public System.Action<float> onManaChanged;
    public System.Action<float> onGuardChanged;
    public System.Action onDeath;

    void Awake()
    {
        CurrentHealth = maxHealth;
        CurrentStamina = maxStamina;
        CurrentMana = maxMana;
        CurrentGuard = maxGuard;
        _combat = GetComponent<CombatController3D>();
    }

    void Update()
    {
        RegenHealth();
        RegenStamina();
        RegenMana();
        RegenGuard();
    }

    // ==================== Здоровье ====================

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

    // Урон без позиции источника (снаряды и пр.) — блок здесь не работает,
    // направленный блок идёт через перегрузку ниже.
    public void TakeDamage(float amount)
    {
        if (!IsAlive || invincible) return;

        CurrentHealth -= amount;
        DamagePopup.Spawn(transform.position + Vector3.up * 2f, amount, Color.red);
        healthRegenTimer = healthRegenDelay;
        onHealthChanged?.Invoke(CurrentHealth);

        if (CurrentHealth <= 0)
        {
            CurrentHealth = 0;
            onDeath?.Invoke();
        }
    }

    // Урон с известной позицией атакующего: блок работает только в дуге спереди.
    public void TakeDamage(float amount, Vector3 sourcePosition)
    {
        if (!IsAlive || invincible) return;

        if (_combat != null && _combat.IsBlocking && IsInBlockArc(sourcePosition))
        {
            TakeBlockedDamage(amount);
            return;
        }
        TakeDamage(amount);
    }

    bool IsInBlockArc(Vector3 sourcePosition)
    {
        Vector3 to = sourcePosition - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f) return true; // атакующий вплотную — считаем спереди
        return Vector3.Angle(transform.forward, to) <= blockArcAngle * 0.5f;
    }

    // Требуется интерфейсом IDamageable. Отталкивания игрока нет — метод пуст.
    public void ApplyKnockback(Vector3 force) { }

    public void Heal(float amount)
    {
        CurrentHealth = Mathf.Min(maxHealth, CurrentHealth + amount);
        onHealthChanged?.Invoke(CurrentHealth);
    }

    // ==================== Выносливость ====================

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

    // ==================== Мана ====================

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

    // ==================== Оборона (блок) ====================

    void RegenGuard()
    {
        if (guardRegenTimer > 0f)
        {
            guardRegenTimer -= Time.deltaTime;
            return;
        }
        if (CurrentGuard < maxGuard && guardRegenPerSecond > 0)
        {
            CurrentGuard = Mathf.Min(maxGuard, CurrentGuard + guardRegenPerSecond * Time.deltaTime);
            onGuardChanged?.Invoke(CurrentGuard);
        }
    }

    // Блок: попап входящего урона голубым, затем половина уходит
    // в оборону → выносливость → здоровье по остатку.
    void TakeBlockedDamage(float incoming)
    {
        DamagePopup.Spawn(transform.position + Vector3.up * 2f, incoming,
                          new Color(0.55f, 0.8f, 1f), " заблокировано");

        float remaining = incoming * blockDamageMultiplier;

        float fromGuard = Mathf.Min(CurrentGuard, remaining);
        CurrentGuard -= fromGuard;
        remaining -= fromGuard;
        guardRegenTimer = guardRegenDelay;
        onGuardChanged?.Invoke(CurrentGuard);

        if (remaining > 0f)
        {
            float fromStamina = Mathf.Min(CurrentStamina, remaining);
            CurrentStamina -= fromStamina;
            remaining -= fromStamina;
            staminaRegenTimer = staminaRegenDelay;
            onStaminaChanged?.Invoke(CurrentStamina);
        }

        if (remaining > 0f)
        {
            CurrentHealth -= remaining;
            healthRegenTimer = healthRegenDelay;
            onHealthChanged?.Invoke(CurrentHealth);
            if (CurrentHealth <= 0f)
            {
                CurrentHealth = 0f;
                onDeath?.Invoke();
            }
        }
    }
}