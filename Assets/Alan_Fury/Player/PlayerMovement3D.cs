using UnityEngine;

/// <summary>
/// Ввод игрока → HumanoidLocomotion.
/// На префабе игрока остаётся этот скрипт (наследует мотор, поля сериализуются).
/// NPC / скелет вешают только HumanoidLocomotion.
/// </summary>
public class PlayerMovement3D : HumanoidLocomotion
{
    [Header("Уворот (двойное нажатие направления)")]
    public float dodgeDoubleTapWindow = 0.28f;

    private Camera _mainCamera;
    private bool _isRunning = true;
    private float _lastMouseMoveTime = -999f;
    private Vector3 _lastMousePos;
    private KeyCode _lastDirKey;
    private float _lastDirKeyTime = -99f;
    private SpellComposer _composer;

    static readonly KeyCode[] DirKeys =
    {
        KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D,
        KeyCode.UpArrow, KeyCode.LeftArrow, KeyCode.DownArrow, KeyCode.RightArrow
    };

    protected override void Start()
    {
        base.Start();
        _mainCamera = Camera.main;
        _lastMousePos = Input.mousePosition;
        _lastMouseMoveTime = Time.time;
        _composer = GetComponent<SpellComposer>();
    }

    void Update()
    {
        if (IsDead) return;

        if (Input.GetKeyDown(KeyCode.CapsLock))
            _isRunning = !_isRunning;
        if (Input.GetKeyDown(KeyCode.C))
            IsSneakingToggle();

        Vector3 move = ComputeCameraMoveDir();
        bool shift = Input.GetKey(KeyCode.LeftShift);
        int gait = shift ? 3 : (_isRunning ? 2 : 1);

        if (shift && move.sqrMagnitude < 0.01f)
        {
            move = ComputeCursorDirection();
            if (move.sqrMagnitude < 0.01f)
                move = transform.forward;
        }

        SetMove(move, gait, IsSneaking);
        SetFace(ComputeFaceDir(move));

        TickDoubleTapDodge();
        TickAltDodge(move);

        if (Input.GetKeyDown(KeyCode.Space))
        {
            Vector3 dir = move.sqrMagnitude > 0.01f ? move : ComputeLookDirection();
            if (witchLight != null && witchLight.IsOn)
                Teleport(dir);
            else
                TryRoll(dir);
        }
    }

    bool IsComposing()
    {
        return _composer != null && _composer.IsComposing;
    }

    void TickDoubleTapDodge()
    {
        bool composing = IsComposing();
        for (int i = 0; i < DirKeys.Length; i++)
        {
            KeyCode key = DirKeys[i];
            if (composing && IsArrow(key)) continue;
            if (!Input.GetKeyDown(key)) continue;

            if (key == _lastDirKey && Time.time - _lastDirKeyTime <= dodgeDoubleTapWindow)
            {
                _lastDirKeyTime = -99f;
                Vector3 dir = DirFromKey(key);
                if (dir.sqrMagnitude < 0.01f)
                    dir = ComputeLookDirection();
                TryDodge(dir);
                return;
            }

            _lastDirKey = key;
            _lastDirKeyTime = Time.time;
        }
    }

    void TickAltDodge(Vector3 move)
    {
        if (!Input.GetKeyDown(KeyCode.LeftAlt) && !Input.GetKeyDown(KeyCode.RightAlt))
            return;
        Vector3 dir = move.sqrMagnitude > 0.01f ? move : ComputeLookDirection();
        TryDodge(dir);
    }

    Vector3 DirFromKey(KeyCode key)
    {
        GetCameraPlanar(out Vector3 forward, out Vector3 right);
        switch (key)
        {
            case KeyCode.W:
            case KeyCode.UpArrow: return forward;
            case KeyCode.S:
            case KeyCode.DownArrow: return -forward;
            case KeyCode.A:
            case KeyCode.LeftArrow: return -right;
            case KeyCode.D:
            case KeyCode.RightArrow: return right;
            default: return Vector3.zero;
        }
    }

    void GetCameraPlanar(out Vector3 forward, out Vector3 right)
    {
        if (_mainCamera == null)
        {
            forward = transform.forward;
            right = transform.right;
            forward.y = 0f; right.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            if (right.sqrMagnitude < 0.01f) right = Vector3.right;
            forward.Normalize(); right.Normalize();
            return;
        }
        forward = _mainCamera.transform.forward;
        right = _mainCamera.transform.right;
        forward.y = 0f; right.y = 0f;
        forward.Normalize(); right.Normalize();
    }

    void IsSneakingToggle()
    {
        SetMove(DesiredMoveDir, CurrentGaitLevel, !IsSneaking);
    }

    static bool IsArrow(KeyCode key)
    {
        return key == KeyCode.UpArrow || key == KeyCode.DownArrow
            || key == KeyCode.LeftArrow || key == KeyCode.RightArrow;
    }

    Vector3 ComputeCameraMoveDir()
    {
        float h;
        float v;
        if (IsComposing())
        {
            h = 0f;
            v = 0f;
            if (Input.GetKey(KeyCode.D)) h += 1f;
            if (Input.GetKey(KeyCode.A)) h -= 1f;
            if (Input.GetKey(KeyCode.W)) v += 1f;
            if (Input.GetKey(KeyCode.S)) v -= 1f;
        }
        else
        {
            h = Input.GetAxisRaw("Horizontal");
            v = Input.GetAxisRaw("Vertical");
        }
        Vector3 input = new Vector3(h, 0f, v);
        if (input.sqrMagnitude < 0.01f) return Vector3.zero;
        input.Normalize();
        if (_mainCamera == null) return input;
        Vector3 forward = _mainCamera.transform.forward;
        Vector3 right = _mainCamera.transform.right;
        forward.y = 0f; right.y = 0f;
        forward.Normalize(); right.Normalize();
        return (forward * input.z + right * input.x).normalized;
    }

    Vector3 ComputeCursorDirection()
    {
        if (_mainCamera == null) return Vector3.zero;
        Ray ray = _mainCamera.ScreenPointToRay(Input.mousePosition);
        if (!new Plane(Vector3.up, transform.position).Raycast(ray, out float dist))
            return Vector3.zero;
        Vector3 look = ray.GetPoint(dist) - transform.position;
        look.y = 0f;
        return look.sqrMagnitude > 0.01f ? look.normalized : Vector3.zero;
    }

    Vector3 ComputeLookDirection()
    {
        Transform aim = Combat != null ? Combat.ActiveAimTarget : null;
        if (aim != null)
        {
            Vector3 to = aim.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude > 0.01f) return to.normalized;
        }
        Vector3 cursor = ComputeCursorDirection();
        if (cursor.sqrMagnitude > 0.01f) return cursor;
        return transform.forward;
    }

    Vector3 ComputeFaceDir(Vector3 moveDir)
    {
        Vector3 mousePos = Input.mousePosition;
        if ((mousePos - _lastMousePos).sqrMagnitude > 0.01f)
            _lastMouseMoveTime = Time.time;
        _lastMousePos = mousePos;

        bool mouseRecentlyMoved = (Time.time - _lastMouseMoveTime) < mouseLookTimeout;
        bool blocking = Combat != null && Combat.IsBlocking;
        bool inCombat = Combat != null && Combat.IsInCombat;

        bool faceMoveDir = !blocking
            && !inCombat
            && moveDir.sqrMagnitude > 0.01f
            && !mouseRecentlyMoved;

        if (faceMoveDir)
            return moveDir;

        Vector3 cursor = ComputeCursorDirection();
        return cursor.sqrMagnitude > 0.01f ? cursor : transform.forward;
    }
}
