using UnityEngine;

/// <summary>
/// Удержание M: правый нижний угол.
/// Стрелка + дистанция до пущи / деревни / храма.
/// Позиции с локальной карты (RegionLayout + hexSize).
/// Игрок берётся с камеры каждый кадр.
/// </summary>
public class LocalMapOverlay : MonoBehaviour
{
    public KeyCode mapKey = KeyCode.M;

    LocationStream _stream;
    Camera _cam;
    GUIStyle _label;
    GUIStyle _debug;

    void Start()
    {
        _stream = FindObjectOfType<LocationStream>();
        _cam = Camera.main;
    }

    Transform GetPlayer()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam != null)
        {
            var follow = _cam.GetComponent<CameraFollow>();
            if (follow != null && follow.target != null)
                return follow.target;
        }
        if (_stream != null && _stream.player != null)
            return _stream.player;
        var move = FindObjectOfType<PlayerMovement3D>();
        return move != null ? move.transform : null;
    }

    void OnGUI()
    {
        if (!Input.GetKey(mapKey)) return;
        if (_stream == null) _stream = FindObjectOfType<LocationStream>();
        if (_stream == null) return;
        if (RegionLayout.Seed == 0) return;

        var player = GetPlayer();
        if (player == null || _cam == null) return;

        EnsureStyles();

        float x = Screen.width - 250f;
        float y = Screen.height - 140f;

        DrawLine(x, y, player.position, RegionLayout.Howling, "пуща");
        DrawLine(x, y + 26f, player.position, RegionLayout.Village, "деревня");
        DrawLine(x, y + 52f, player.position, RegionLayout.Temple, "храм");

        var p = player.position;
        GUI.Label(new Rect(x, y + 82f, 230f, 18f),
            player.name + "  " + p.x.ToString("0") + " " + p.z.ToString("0"), _debug);
    }

    void DrawLine(float x, float y, Vector3 origin, Vector2Int hex, string name)
    {
        Vector3 world = HexToWorld(hex.x, hex.y);
        float dist = Vector3.Distance(
            new Vector3(world.x, 0f, world.z),
            new Vector3(origin.x, 0f, origin.z));

        Vector3 dir = world - origin;
        dir.y = 0f;
        float angle = 0f;
        if (dir.sqrMagnitude > 4f)
        {
            Vector3 fwd = _cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.01f)
                angle = Vector3.SignedAngle(fwd.normalized, dir.normalized, Vector3.up);
        }

        var old = GUI.matrix;
        GUIUtility.RotateAroundPivot(angle, new Vector2(x + 10f, y + 10f));
        GUI.Label(new Rect(x, y, 20f, 20f), "▲", _label);
        GUI.matrix = old;

        GUI.Label(new Rect(x + 24f, y, 210f, 20f), name + "  " + FormatDist(dist), _label);
    }

    Vector3 HexToWorld(int q, int r)
    {
        float size = (_stream != null && _stream.hexSize > 1f) ? _stream.hexSize : 200f;
        float wx = size * (Mathf.Sqrt(3f) * q + Mathf.Sqrt(3f) * 0.5f * r);
        float wz = size * 1.5f * r;
        return new Vector3(wx, 0f, wz);
    }

    static string FormatDist(float m)
    {
        if (m >= 1000f) return (m / 1000f).ToString("0.0") + " км";
        return Mathf.RoundToInt(m).ToString() + " м";
    }

    void EnsureStyles()
    {
        if (_label != null) return;
        _label = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.95f, 0.95f, 0.85f) },
            alignment = TextAnchor.MiddleLeft
        };
        _debug = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal = { textColor = new Color(0.65f, 0.65f, 0.55f) }
        };
    }
}
