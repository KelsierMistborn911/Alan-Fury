using UnityEngine;

/// <summary>
/// Зрение призрака. Только факт «видит игрока» + гизмо поля.
/// Носитель: halfAngle 180 = полный круг. Летун: узкий конус вперёд.
/// Не использует WerewolfPerception.
/// </summary>
public class GhostPerception : MonoBehaviour
{
    public enum Kind { Scout, Host }

    [Header("Роль")]
    public Kind kind = Kind.Scout;

    [Header("Поле")]
    [Tooltip("Дальность зрения (м).")]
    public float sightRange = 10f;
    [Tooltip("Полуугол конуса. 180 = обзор 360.")]
    [Range(0f, 180f)] public float viewHalfAngle = 55f;
    public Vector3 eyeOffset = new Vector3(0f, 1.1f, 0f);
    public Vector3 playerEyeOffset = new Vector3(0f, 1.2f, 0f);
    public LayerMask sightBlockers = ~0;

    [Header("Гизмо")]
    public bool drawGizmos = true;
    [Tooltip("Рисовать всегда, не только Selected.")]
    public bool drawAlways = true;
    [Range(16, 96)] public int gizmoSegments = 48;
    public float gizmoY = 0.12f;
    public Color fieldColor = new Color(0.45f, 0.85f, 1f, 0.22f);
    public Color fieldEdge = new Color(0.55f, 0.95f, 1f, 0.85f);
    public Color hostFieldColor = new Color(0.85f, 0.45f, 1f, 0.16f);
    public Color hostFieldEdge = new Color(0.95f, 0.55f, 1f, 0.9f);
    public Color seenColor = new Color(1f, 0.25f, 0.2f, 0.28f);

    public bool SeesPlayer { get; private set; }
    public Transform Seen { get; private set; }

    public bool IsOmni => viewHalfAngle >= 179.5f;
    public Vector3 Eye => transform.position + eyeOffset;

    public void ApplyHost()
    {
        kind = Kind.Host;
        viewHalfAngle = 180f;
        if (sightRange < 16f) sightRange = 18f;
        eyeOffset = new Vector3(0f, 1.2f, 0f);
    }

    public void ApplyScout()
    {
        kind = Kind.Scout;
        if (viewHalfAngle >= 179.5f) viewHalfAngle = 55f;
        if (sightRange > 14f) sightRange = 8f;
        eyeOffset = new Vector3(0f, 0.6f, 0f);
    }

    void Update()
    {
        TickVision();
    }

    public bool CanSee(Transform target)
    {
        if (target == null) return false;
        Vector3 eye = Eye;
        Vector3 to = target.position + playerEyeOffset - eye;
        Vector3 flat = to;
        flat.y = 0f;
        float dist = flat.magnitude;
        if (dist > sightRange || dist < 0.01f) return false;

        if (!IsOmni)
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            if (Vector3.Angle(fwd, flat) > viewHalfAngle) return false;
        }

        Vector3 dest = target.position + playerEyeOffset;
        Vector3 delta = dest - eye;
        float rayLen = delta.magnitude;
        if (rayLen < 0.05f) return true;
        if (Physics.Raycast(eye, delta / rayLen, out RaycastHit hit, rayLen, sightBlockers, QueryTriggerInteraction.Ignore))
        {
            Transform h = hit.transform;
            if (h != target && !h.IsChildOf(target) && h != transform && !h.IsChildOf(transform))
                return false;
        }
        return true;
    }

    void TickVision()
    {
        SeesPlayer = false;
        Seen = null;

        var reg = PlayerRegistry.Instance;
        if (reg != null && reg.Count > 0)
        {
            for (int i = 0; i < reg.Count; i++)
            {
                Transform p = reg.Players[i];
                if (!CanSee(p)) continue;
                SeesPlayer = true;
                Seen = p;
                return;
            }
            return;
        }

        var fallback = FindObjectOfType<PlayerMovement3D>();
        if (fallback != null && CanSee(fallback.transform))
        {
            SeesPlayer = true;
            Seen = fallback.transform;
        }
    }

    void OnDrawGizmos()
    {
        if (drawGizmos && drawAlways) DrawField();
    }

    void OnDrawGizmosSelected()
    {
        if (drawGizmos && !drawAlways) DrawField();
    }

    void DrawField()
    {
        Vector3 origin = transform.position;
        origin.y += gizmoY;
        Color fill = SeesPlayer ? seenColor : (kind == Kind.Host ? hostFieldColor : fieldColor);
        Color edge = SeesPlayer ? new Color(1f, 0.35f, 0.25f, 0.95f) : (kind == Kind.Host ? hostFieldEdge : fieldEdge);

        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        fwd.Normalize();

        int seg = Mathf.Max(12, gizmoSegments);
        if (IsOmni)
        {
            DrawFan(origin, Vector3.forward, 180f, sightRange, seg, fill, edge);
        }
        else
        {
            DrawFan(origin, fwd, viewHalfAngle, sightRange, seg, fill, edge);
            Gizmos.color = edge;
            Vector3 left = Quaternion.Euler(0f, -viewHalfAngle, 0f) * fwd;
            Vector3 right = Quaternion.Euler(0f, viewHalfAngle, 0f) * fwd;
            Gizmos.DrawLine(origin, origin + left * sightRange);
            Gizmos.DrawLine(origin, origin + right * sightRange);
        }

        Gizmos.color = edge;
        Gizmos.DrawLine(origin, origin + Vector3.up * eyeOffset.y);
        if (Seen != null)
            Gizmos.DrawLine(Eye, Seen.position + playerEyeOffset);
    }

    static void DrawFan(Vector3 origin, Vector3 fwd, float halfAngle, float range, int segments, Color fill, Color edge)
    {
        int n = Mathf.Max(8, segments);
        float span = halfAngle * 2f;
        if (span >= 359.5f)
        {
            fwd = Vector3.forward;
            halfAngle = 180f;
            span = 360f;
        }

        Vector3 prev = Vector3.zero;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float ang = -halfAngle + span * t;
            Vector3 dir = Quaternion.Euler(0f, ang, 0f) * fwd;
            Vector3 p = origin + dir.normalized * range;
            if (i > 0)
            {
                Gizmos.color = fill;
                Gizmos.DrawLine(origin, p);
                Gizmos.color = edge;
                Gizmos.DrawLine(prev, p);
            }
            prev = p;
        }
    }
}
