using UnityEngine;

/// <summary>
/// Восприятие оборотнем игрока. Считает дистанцию, направление,
/// смотрит ли игрок на оборотня и есть ли между ними прямая видимость.
/// Только данные — решения принимают мозг/сталкер.
/// Учитывает укрытия (туман): данные об укрытиях живут в AlphaStalker.
/// </summary>
public class WerewolfPerception : MonoBehaviour
{
    [Header("Игрок")]
    public Transform player;
    public string playerTag = "Player";

    [Header("Угол обзора игрока")]
    [Tooltip("Половина конуса обзора (град). В пределах него игрок считается смотрящим на цель.")]
    [Range(0f, 180f)] public float viewAngleThreshold = 35f;

    [Header("Линия видимости")]
    [Tooltip("Слои, перекрывающие обзор: рельеф, объекты-укрытия.")]
    public LayerMask sightBlockers = ~0;
    [Tooltip("Точка «глаз» оборотня относительно его origin.")]
    public Vector3 selfEyeOffset = new Vector3(0f, 1.0f, 0f);
    [Tooltip("Точка «глаз» игрока относительно его origin.")]
    public Vector3 playerEyeOffset = new Vector3(0f, 1.2f, 0f);
    [Tooltip("Дальше этого игрок не замечает оборотня даже в прямой видимости (м).")]
    public float playerSightRange = 35f;

    public bool HasPlayer => player != null;
    public Vector3 PlayerPos => player.position;

    /// <summary>Игрок заряжает удар (замах удержанием).</summary>
    public bool PlayerIsCharging => _playerCombat != null && _playerCombat.IsCharging;
    /// <summary>Игрок в активной фазе удара.</summary>
    public bool PlayerIsAttacking => _playerCombat != null && _playerCombat.IsAttacking;
    /// <summary>Игрок в windup обычного/любого удара (телеграф до active-фазы).</summary>
    public bool PlayerIsWindingUp => _playerCombat != null && _playerCombat.IsWindingUp;
    /// <summary>Игрок замахивается или уже бьёт — угроза для уворота.</summary>
    public bool PlayerThreatActive =>
        PlayerIsWindingUp || PlayerIsCharging || PlayerIsAttacking;

    /// <summary>Дальность удара текущего оружия игрока (м); 2, если оружие не найдено.</summary>
    public float PlayerWeaponRange
    {
        get
        {
            var w = _playerLoadout != null ? _playerLoadout.GetMainWeapon() : null;
            return w != null ? w.attackRange : 2f;
        }
    }

    /// <summary>Полуконус удара оружия игрока (град, из его WeaponHitbox); 60, если хитбокс не найден.</summary>
    public float PlayerWeaponConeHalfAngle => _playerHitbox != null ? _playerHitbox.coneHalfAngle : 60f;

    /// <summary>Угол (град) между взглядом игрока и направлением на этого волка. 0 = игрок смотрит прямо на волка.</summary>
    public float AngleFromPlayerGaze
    {
        get
        {
            if (!HasPlayer) return 180f;
            Vector3 toSelf = transform.position - player.position; toSelf.y = 0f;
            Vector3 fwd = player.forward; fwd.y = 0f;
            if (toSelf.sqrMagnitude < 0.0001f || fwd.sqrMagnitude < 0.0001f) return 180f;
            return Vector3.Angle(fwd, toSelf);
        }
    }

    private WerewolfAlphaStalker _stalker; // источник данных об укрытиях (может отсутствовать)
    private CombatController3D _playerCombat;
    private PlayerLoadout _playerLoadout;
    private WeaponHitbox _playerHitbox;

    void Awake()
    {
        if (player == null && !string.IsNullOrEmpty(playerTag))
        {
            var go = GameObject.FindGameObjectWithTag(playerTag);
            if (go != null) player = go.transform;
        }
        if (player != null) _playerCombat = player.GetComponent<CombatController3D>();
        if (player != null) _playerLoadout = player.GetComponent<PlayerLoadout>();
        if (player != null) _playerHitbox = player.GetComponentInChildren<WeaponHitbox>();
        _stalker = GetComponent<WerewolfAlphaStalker>();
    }

    /// <summary>Горизонтальная дистанция до игрока (Y игнорируется).</summary>
    public float DistanceToPlayer
    {
        get
        {
            if (player == null) return Mathf.Infinity;
            Vector3 d = player.position - transform.position;
            d.y = 0f;
            return d.magnitude;
        }
    }

    /// <summary>Горизонтальное направление ОТ игрока К оборотню (нормализованное).</summary>
    public Vector3 DirFromPlayerFlat
    {
        get
        {
            if (player == null) return Vector3.forward;
            Vector3 d = transform.position - player.position;
            d.y = 0f;
            return d.sqrMagnitude < 0.0001f ? Vector3.forward : d.normalized;
        }
    }

    /// <summary>Плоское направление взгляда игрока (нормализованное).</summary>
    public Vector3 PlayerForwardFlat
    {
        get
        {
            if (player == null) return Vector3.forward;
            Vector3 f = player.forward;
            f.y = 0f;
            return f.sqrMagnitude < 0.0001f ? Vector3.forward : f.normalized;
        }
    }

    /// <summary>Повёрнут ли игрок в сторону оборотня (только угол, без укрытий).</summary>
    public bool PlayerLookingAtMe
    {
        get
        {
            if (player == null) return false;
            Vector3 fwd = player.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return false;
            fwd.Normalize();
            float angle = Vector3.Angle(fwd, DirFromPlayerFlat);
            return angle < viewAngleThreshold;
        }
    }

    /// <summary>Чистая ли прямая видимость между оборотнем и игроком (нет укрытий).</summary>
    public bool HasLineOfSightToPlayer()
    {
        if (player == null) return false;
        Vector3 a = transform.position + selfEyeOffset;
        Vector3 b = player.position + playerEyeOffset;
        Vector3 to = b - a;
        float d = to.magnitude;
        if (d < 0.001f) return true;
        // Луч до игрока ничего не задел → видимость чистая.
        return !Physics.Raycast(a, to / d, d - 0.05f, sightBlockers, QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    /// Видит ли игрок оборотня прямо сейчас:
    /// в пределах дальности + в конусе обзора + не в укрытии + не перекрыт рельефом.
    /// </summary>
    public bool IsSeenByPlayer()
    {
        if (player == null) return false;
        if (DistanceToPlayer > playerSightRange) return false;
        if (!PlayerLookingAtMe) return false;

        // Оборотень в тумане, а игрок снаружи → не виден.
        if (_stalker != null && _stalker.IsConcealedAt(transform.position, player.position))
            return false;

        return HasLineOfSightToPlayer();
    }
}
