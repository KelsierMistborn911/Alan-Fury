using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Лучник отряда. Слот и залп — только от SkeletonSquad.
/// После Disband — сам, медленно и криво.
/// </summary>
[RequireComponent(typeof(HumanoidLocomotion))]
[RequireComponent(typeof(NpcPerception))]
public class SkeletonArcherBrain : MonoBehaviour
{
    public enum Mode { Slot, Volley, Broken }

    public HumanoidLocomotion locomotion;
    public HumanoidCombat combat;
    public PlayerResources resources;
    public NpcPerception perception;
    public NpcRanged ranged;
    public Pathfinder pathfinder;
    public SkeletonSquad squad;

    [Header("Слот")]
    public float arriveDistance = 0.7f;
    public int marchGait = 1;

    [Header("Слом строя")]
    public float brokenAggro = 18f;
    public float brokenLose = 28f;
    public float brokenIdeal = 11f;
    public float brokenSlack = 2.2f;
    public float brokenDrawMin = 1.15f;
    public float brokenDrawMax = 1.9f;
    public float brokenReloadMin = 3.2f;
    public float brokenReloadMax = 5.4f;
    public float brokenYaw = 16f;
    public float brokenWander = 4.5f;

    public int SlotIndex { get; set; } = -1;
    public Mode CurrentMode { get; private set; } = Mode.Slot;
    public bool InSlot { get; private set; }
    public Transform CurrentTarget { get; private set; }

    public static readonly List<SkeletonArcherBrain> Alive = new List<SkeletonArcherBrain>(16);

    Vector3 _slotPos;
    Vector3 _slotFace = Vector3.forward;
    int _slotGait = 1;
    bool _holdFire;
    float _brokenDrawUntil;
    float _wanderUntil;
    Vector3 _wanderOff;

    void Awake()
    {
        if (locomotion == null) locomotion = GetComponent<HumanoidLocomotion>();
        if (combat == null) combat = GetComponent<HumanoidCombat>();
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (perception == null) perception = GetComponent<NpcPerception>();
        if (perception == null) perception = gameObject.AddComponent<NpcPerception>();
        perception.ApplyHumanoid();
        if (ranged == null) ranged = GetComponent<NpcRanged>();
        if (ranged == null) ranged = gameObject.AddComponent<NpcRanged>();
        if (pathfinder == null) pathfinder = FindObjectOfType<Pathfinder>();
        ranged.EnsureBow();
    }

    void Start()
    {
        if (resources != null) resources.onDeath += HandleDeath;
        if (combat != null) combat.DrawSword();
        RegisterTarget();
    }

    void OnDestroy()
    {
        if (resources != null) resources.onDeath -= HandleDeath;
        if (squad != null) squad.Forget(this);
        UnregisterTarget();
    }

    void HandleDeath()
    {
        if (ranged != null) ranged.Cancel();
        if (locomotion != null) locomotion.SetMove(Vector3.zero, 1, false);
        if (squad != null) squad.Forget(this);
        UnregisterTarget();
        enabled = false;
    }

    void RegisterTarget()
    {
        if (!Alive.Contains(this)) Alive.Add(this);
        SkeletonSquad.ApplyEnemyLayer(gameObject);
    }

    void UnregisterTarget()
    {
        Alive.Remove(this);
    }

    public void BindSquad(SkeletonSquad owner, int slot)
    {
        squad = owner;
        SlotIndex = slot;
        CurrentMode = Mode.Slot;
    }

    public void SetSlot(Vector3 worldPos, Vector3 face, int gait)
    {
        if (CurrentMode == Mode.Broken) return;
        _slotPos = worldPos;
        face.y = 0f;
        _slotFace = face.sqrMagnitude > 0.01f ? face.normalized : Vector3.forward;
        _slotGait = gait < 1 ? 1 : gait;
        if (CurrentMode != Mode.Volley)
            CurrentMode = Mode.Slot;
    }

    public void OrderDraw()
    {
        if (CurrentMode == Mode.Broken) return;
        CurrentMode = Mode.Volley;
        _holdFire = true;
        if (ranged != null && !ranged.IsDrawing)
            ranged.BeginDraw();
    }

    public void OrderFire(Transform target)
    {
        if (CurrentMode == Mode.Broken) return;
        CurrentTarget = target;
        _holdFire = false;
        if (ranged == null) return;
        ranged.TickDraw();
        ranged.FireAt(target, SlotIndex * 0.35f);
        CurrentMode = Mode.Slot;
    }

    public void OrderCancelVolley()
    {
        if (CurrentMode == Mode.Broken) return;
        if (ranged != null) ranged.Cancel();
        CurrentMode = Mode.Slot;
        _holdFire = false;
    }

    public void BreakRanks()
    {
        squad = null;
        CurrentMode = Mode.Broken;
        _holdFire = false;
        if (ranged != null) ranged.Cancel();
        CurrentTarget = null;
        _wanderUntil = 0f;
    }

    void Update()
    {
        if (resources != null && resources.IsDead) return;
        var cc = GetComponent<CrowdControl>();
        if (cc != null && cc.IsStunned)
        {
            if (locomotion != null) locomotion.SetMove(Vector3.zero, 1, false);
            return;
        }

        if (CurrentMode == Mode.Broken)
        {
            TickBroken();
            return;
        }

        if (CurrentMode == Mode.Volley)
        {
            TickVolley();
            return;
        }

        TickSlot();
    }

    void TickSlot()
    {
        Vector3 dest = _slotPos;
        if (pathfinder != null && pathfinder.IsReady)
            dest = pathfinder.NearestWalkableWorld(_slotPos, out _);

        float dist = FlatDist(transform.position, dest);
        InSlot = dist <= arriveDistance;
        if (InSlot)
        {
            if (locomotion != null)
            {
                locomotion.SetMove(Vector3.zero, 1, false);
                locomotion.SetFace(_slotFace);
            }
            return;
        }

        Vector3 dir = dest - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f)
        {
            if (locomotion != null) locomotion.SetMove(Vector3.zero, 1, false);
            return;
        }
        dir.Normalize();
        if (locomotion != null)
        {
            locomotion.SetMove(dir, _slotGait, false);
            locomotion.SetFace(dir);
        }
    }

    void TickVolley()
    {
        if (locomotion != null)
        {
            locomotion.SetMove(Vector3.zero, 1, false);
            if (_slotFace.sqrMagnitude > 0.01f)
                locomotion.SetFace(_slotFace);
        }
        if (_holdFire && ranged != null)
            ranged.TickDraw();
        InSlot = true;
    }

    void TickBroken()
    {
        if (CurrentTarget != null && !IsTargetAlive(CurrentTarget))
            CurrentTarget = null;
        if (CurrentTarget != null && FlatDist(transform.position, CurrentTarget.position) > brokenLose)
            CurrentTarget = null;

        if (CurrentTarget == null)
        {
            Transform nearest = null;
            if (PlayerRegistry.Instance != null)
                nearest = PlayerRegistry.Instance.GetNearestFlat(transform.position, brokenAggro);
            if (nearest != null && IsTargetAlive(nearest) && CanSee(nearest))
                CurrentTarget = nearest;
        }

        if (CurrentTarget == null)
        {
            Idle();
            return;
        }

        Vector3 to = CurrentTarget.position - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        Vector3 face = dist > 0.01f ? to / dist : transform.forward;
        if (locomotion != null) locomotion.SetFace(face);

        if (Time.time >= _wanderUntil)
        {
            _wanderUntil = Time.time + Random.Range(1.1f, 2.4f);
            Vector2 r = Random.insideUnitCircle * brokenWander;
            _wanderOff = new Vector3(r.x, 0f, r.y);
        }

        float error = dist - brokenIdeal;
        if (Mathf.Abs(error) <= brokenSlack)
        {
            Vector3 side = Vector3.Cross(Vector3.up, face);
            if (locomotion != null)
                locomotion.SetMove((side * _wanderOff.x * 0.15f), 1, false);
        }
        else
        {
            Vector3 dest = CurrentTarget.position - face * brokenIdeal + _wanderOff;
            Vector3 dir = dest - transform.position;
            dir.y = 0f;
            if (locomotion != null)
                locomotion.SetMove(dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.zero, 1, false);
        }

        if (ranged == null) return;
        ranged.TickDraw();
        if (ranged.IsReloading) return;

        if (!ranged.IsDrawing)
        {
            if (ranged.BeginDraw())
                _brokenDrawUntil = Time.time + Random.Range(brokenDrawMin, brokenDrawMax);
            return;
        }

        if (Time.time < _brokenDrawUntil) return;

        float yaw = Random.Range(-brokenYaw, brokenYaw);
        ranged.FireAt(CurrentTarget, yaw);
        ranged.SetReload(Random.Range(brokenReloadMin, brokenReloadMax));
    }

    void Idle()
    {
        if (locomotion != null) locomotion.SetMove(Vector3.zero, 1, false);
        if (ranged != null) ranged.Cancel();
    }

    bool CanSee(Transform t)
    {
        if (perception != null) return perception.CanSee(t);
        return t != null && FlatDist(transform.position, t.position) <= brokenAggro;
    }

    static bool IsTargetAlive(Transform t)
    {
        if (t == null || !t.gameObject.activeInHierarchy) return false;
        var dmg = t.GetComponentInParent<IDamageable>();
        return dmg == null || dmg.IsAlive;
    }

    static float FlatDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
