using UnityEngine;

/// <summary>
/// Ввод игрока → HumanoidCombat.
/// На префабе игрока остаётся этот скрипт (наследует машину, поля сериализуются).
/// NPC / скелет вешают только HumanoidCombat и вызывают TryThrust / TryHoldAttack сами.
/// </summary>
public class CombatController3D : HumanoidCombat
{
    [Header("Игрок: захват цели")]
    public PlayerTargeting targeting;

    [Header("Игрок: клавиши")]
    public KeyCode blockKey = KeyCode.Mouse1;
    public KeyCode parryKey = KeyCode.F;
    public KeyCode thrustKey = KeyCode.Q;
    public KeyCode swordToggleKey = KeyCode.Alpha1;
    public KeyCode shieldToggleKey = KeyCode.Alpha2;
    public KeyCode sheathKey = KeyCode.R;

    private Camera _mainCamera;
    private SpellComposer _composer;
    private SpellSlots _spellSlots;
    private SpellController _spells;

    protected override void Awake()
    {
        if (targeting == null) targeting = GetComponent<PlayerTargeting>();
        _composer = GetComponent<SpellComposer>();
        _spellSlots = GetComponent<SpellSlots>();
        _spells = GetComponent<SpellController>();
        base.Awake();
        _mainCamera = Camera.main;
        if (targeting == null)
            Debug.LogError("[CombatController3D] Нет PlayerTargeting на объекте. Добавь компонент.");
        if (stance == null)
            Debug.LogError("[CombatController3D] Нет PlayerStance на объекте. Добавь компонент.");
        if (targeting != null && EnemyLayers.value == 0)
            EnemyLayers = targeting.enemyLayers;
    }

    public override void ClearTarget()
    {
        base.ClearTarget();
        if (targeting != null) targeting.ClearTarget();
    }

    protected override void Update()
    {
        SyncPlayerAim();
        TickWeaponToggleInput();
        TickPlayerCombatInput();
        base.Update();
        if (targeting != null)
            targeting.SetAutoTarget(AutoTarget);
    }

    void SyncPlayerAim()
    {
        if (targeting != null)
        {
            combatFaceRange = targeting.combatFaceRange;
            if (EnemyLayers.value == 0)
                EnemyLayers = targeting.enemyLayers;

            targeting.TickLock();
            if (IsCharging || IsBlocking)
                targeting.TryAcquireIfNeeded();

            if (Input.GetKeyDown(KeyCode.LeftShift))
                targeting.SaveAndClearForShift();
            if (Input.GetKeyUp(KeyCode.LeftShift))
                targeting.RestoreAfterShift();

            targeting.UpdateMarker();

            if (Input.GetKeyDown(KeyCode.Tab))
                targeting.ToggleOrAcquireByTab();

            CommandTarget = targeting.CurrentTarget;
            if (AutoTarget == null)
                AutoTarget = targeting.AutoTarget;
            else
                targeting.SetAutoTarget(AutoTarget);
        }

        AimDirection = ComputeMouseAim();
    }

    Vector3 ComputeMouseAim()
    {
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (_mainCamera == null) return transform.forward;
        Ray ray = _mainCamera.ScreenPointToRay(Input.mousePosition);
        if (!new Plane(Vector3.up, transform.position).Raycast(ray, out float dist))
            return transform.forward;
        Vector3 dir = ray.GetPoint(dist) - transform.position;
        dir.y = 0f;
        return dir.sqrMagnitude > 0.01f ? dir.normalized : transform.forward;
    }

    void TickPlayerCombatInput()
    {
        if (resources != null && resources.IsDead) return;
        if (IsComposing()) return;
        if (_spells != null && _spells.BlocksMelee) return;

        if (IsArmed && Input.GetKeyDown(parryKey))
            TryParry();

        if (IsCharging && Input.GetKeyDown(KeyCode.Space))
        {
            CancelCharge();
            return;
        }

        if (IsAttacking) return;

        if (IsCharging)
        {
            if (Input.GetMouseButtonUp(0) && isHoldingAttack)
                ReleaseAttack();
            return;
        }

        bool wantsBlock = IsShieldArmed && Input.GetKey(blockKey) && loadout != null && loadout.HasShield();
        SetBlocking(wantsBlock);

        if (wantsBlock)
        {
            if (Input.GetMouseButtonDown(0)) TryBlockAttack();
            return;
        }

        if (Input.GetMouseButtonDown(0) && movement != null &&
            (movement.IsDodging || movement.TimeSinceDodgeEnd <= dodgeAttackBufferAfter))
        {
            TryDodgeAttack();
            return;
        }

        if (Input.GetKeyDown(thrustKey))
        {
            TryThrust();
            return;
        }

        if (Input.GetMouseButtonDown(0))
            TryHoldAttack();
    }

    protected override void SampleAttackMoveMode()
    {
        _attackMoveMode = AttackMoveMode.None;
        if (!Input.GetKey(KeyCode.LeftShift)) return;
        float v = Input.GetAxisRaw("Vertical");
        if (v > 0.1f) _attackMoveMode = AttackMoveMode.Stop;
        else if (v < -0.1f) _attackMoveMode = AttackMoveMode.TurnStrike;
    }

    protected override float ExtraMomentumSpeed()
    {
        if (_dodgeAttackPerfectFlag && movement != null) return movement.DodgeSpeedValue;
        if (Input.GetKey(KeyCode.LeftShift) && movement != null) return movement.CurrentSpeed;
        return 0f;
    }

    protected override bool AllowTargetMagnet()
    {
        return !Input.GetKey(KeyCode.LeftShift);
    }

    protected override bool HasManualMoveInput()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        return Mathf.Abs(h) > 0.15f || Mathf.Abs(v) > 0.15f;
    }

    bool IsComposing()
    {
        return _composer != null && _composer.IsComposing;
    }

    void TickWeaponToggleInput()
    {
        if (IsAttacking) return;

        if (Input.GetKeyDown(swordToggleKey))
        {
            if (_composer != null && _composer.HoldsFocus(SpellChannel.Hand1))
            {
                _composer.ReleaseFocus();
                DrawSword();
                return;
            }
            if (IsComposing()) return;
            HandleHandToggle(SpellChannel.Hand1, true);
        }
        if (Input.GetKeyDown(shieldToggleKey))
        {
            if (_composer != null && _composer.HoldsFocus(SpellChannel.Hand2))
            {
                _composer.ReleaseFocus();
                DrawShield();
                return;
            }
            if (IsComposing()) return;
            HandleHandToggle(SpellChannel.Hand2, false);
        }
        if (Input.GetKeyDown(sheathKey))
            EnterForcePeace();
    }

    void HandleHandToggle(SpellChannel ch, bool sword)
    {
        if (_spellSlots != null && _spellSlots.IsDrawn(ch))
        {
            if (_spells != null && _spells.IsAiming)
                return;
            if (_spells != null) _spells.TryStow(ch);
            else _spellSlots.TrySuspend(ch);
            if (loadout != null)
            {
                if (ch == SpellChannel.Hand1) loadout.rightHandMagic = false;
                else loadout.leftHandMagic = false;
            }
            if (sword) DrawSword();
            else DrawShield();
            return;
        }

        if (sword) ToggleSword();
        else ToggleShield();
    }
}
