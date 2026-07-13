using UnityEngine;

/// <summary>
/// ���� �����-�������� (���� ������).
/// ������ ������ ������ �� ���������� �� ���������, ����� ������,
/// � ��������� ������, ����� ����� ������� �� ���� ��� �������� ������.
/// �� ������ ������� ������ � �����; � �������� � �� ����.
///
/// ������ ���� �������: ���������� � WerewolfPerception,
/// ��������/������� � WerewolfLocomotion.
/// </summary>
[RequireComponent(typeof(WerewolfPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
public class WerewolfBrain : MonoBehaviour
{
    [Header("���������� (�����������, ���� �����)")]
    public WerewolfPerception perception;
    public WerewolfLocomotion locomotion;

    [Header("��������� (�)")]
    public float preferredDistance = 18f;
    public float minDistance = 10f;
    public float noticeRange = 30f;

    [Header("�������� (�/�)")]
    [Tooltip("��������� �������� � ����� ���� boundSpeedThreshold, ����� ��� ������.")]
    public float circleSpeed = 4f;
    [Tooltip("����������� � ����� ���� boundSpeedThreshold, ����� ��������� �� ������.")]
    public float fleeSpeed = 10f;

    [Header("��������")]
    [Tooltip("�� ������� ��������������� ������ ������ �� ���� ��������� (����).")]
    public float orbitStepAngle = 60f;
    [Tooltip("���� ������� ����������� ������ �� ��������� (0..1).")]
    [Range(0f, 1f)] public float reverseChance = 0.25f;

    [Header("�����������")]
    [Tooltip("�� ������� ��� ������ preferredDistance �������� ��� ������.")]
    public float retreatDistanceFactor = 1.3f;

    [Header("��������")]
    public float holdInterval = 2f;
    public float holdJitter = 1f;

    private enum State { Hold, Circle, Retreat }
    private State _state = State.Hold;
    private Vector3 _target;
    private float _holdTimer;
    private int _orbitDir = 1;

    void Start()
    {
        if (perception == null) perception = GetComponent<WerewolfPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();

        _orbitDir = Random.value < 0.5f ? -1 : 1;
        _target = transform.position;
        ResetHoldTimer();
    }

    void Update()
    {
        if (perception == null || !perception.HasPlayer) return;

        float dt = Time.deltaTime;
        float dist = perception.DistanceToPlayer;

        // Открытое окружение: на взгляд игрока не реагируем, отступаем только от сближения.
        bool threatened = dist < minDistance;

        switch (_state)
        {
            case State.Hold:
                locomotion.FaceTowards(perception.PlayerPos, dt); // � ����� ������� �� ������
                if (threatened) { EnterRetreat(); break; }

                _holdTimer -= dt;
                if (_holdTimer <= 0f) EnterCircle();
                break;

            case State.Circle:
                if (threatened) { EnterRetreat(); break; }
                bool arrived = locomotion.MoveTo(_target, circleSpeed, dt);
                locomotion.FaceTowards(_target, dt);              // ������� �� ����
                if (arrived) EnterHold();
                break;

            case State.Retreat:
                bool reached = locomotion.MoveTo(_target, fleeSpeed, dt);
                locomotion.FaceTowards(_target, dt);              // ������� ���� �����
                if (!threatened) { EnterHold(); break; }
                if (reached) _target = ComputeRetreatPoint();
                break;
        }
    }

    // ============ �������� ============

    private void EnterHold()
    {
        _state = State.Hold;
        if (Random.value < reverseChance) _orbitDir = -_orbitDir;
        ResetHoldTimer();
    }

    private void EnterCircle() { _state = State.Circle; _target = ComputeOrbitPoint(); }
    private void EnterRetreat() { _state = State.Retreat; _target = ComputeRetreatPoint(); }

    private void ResetHoldTimer()
        => _holdTimer = holdInterval + Random.Range(-holdJitter, holdJitter);

    // ============ ����� ����� ============

    // ��������� ����� �� ����������: �������� ������ ������ �� orbitStepAngle.
    // ������ �� ������� preferredDistance � ���� �����������, ���� ������ �� ���,
    // � ������ ��� �������������, ���� ������.
    private Vector3 ComputeOrbitPoint()
    {
        Vector3 p = perception.PlayerPos;
        Vector3 cur = perception.DirFromPlayerFlat;
        float ang = Mathf.Atan2(cur.z, cur.x) + orbitStepAngle * Mathf.Deg2Rad * _orbitDir;
        Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
        return p + dir * preferredDistance;
    }

    // ����������� ������ �� ������ ������ (�� ��������� ���), � ����� ���������.
    private Vector3 ComputeRetreatPoint()
    {
        Vector3 p = perception.PlayerPos;
        Vector3 away = perception.DirFromPlayerFlat;
        float j = Random.Range(-25f, 25f) * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(
            away.x * Mathf.Cos(j) - away.z * Mathf.Sin(j),
            0f,
            away.x * Mathf.Sin(j) + away.z * Mathf.Cos(j));
        return p + dir * (preferredDistance * retreatDistanceFactor);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (perception == null || !perception.HasPlayer) return;
        Vector3 p = perception.PlayerPos;

        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(p, minDistance);
        Gizmos.color = new Color(0.9f, 0.8f, 0.2f, 0.6f);
        Gizmos.DrawWireSphere(p, preferredDistance);
        Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.4f);
        Gizmos.DrawWireSphere(p, noticeRange);

        if (Application.isPlaying)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(transform.position, _target);
            Gizmos.DrawWireCube(_target, Vector3.one * 0.6f);
        }
    }
#endif
}
