using UnityEngine;

/// <summary>
/// Всплывающая цифра урона: рождается в мире, летит вверх, гаснет и удаляется.
/// Ничего вешать на сцену не нужно — вызывается статикой из кода:
///     DamagePopup.Spawn(позиция, урон, цвет);
///     DamagePopup.Spawn(позиция, урон, цвет, " заблокировано");
/// Урон округляется до целых. Текст всегда повёрнут к камере.
/// </summary>
public class DamagePopup : MonoBehaviour
{
    private const float Lifetime = 0.9f;   // сколько живёт (сек)
    private const float RiseSpeed = 1.5f;  // скорость всплытия (м/с)

    private TextMesh _tm;
    private Color _baseColor;
    private float _timer;

    public static void Spawn(Vector3 worldPos, float amount, Color color, string suffix = "")
    {
        var go = new GameObject("DamagePopup");
        go.transform.position = worldPos;

        var tm = go.AddComponent<TextMesh>();
        tm.text = Mathf.RoundToInt(amount).ToString() + suffix;
        tm.color = color;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.fontSize = 48;
        tm.characterSize = 0.15f;

        var popup = go.AddComponent<DamagePopup>();
        popup._tm = tm;
        popup._baseColor = color;
        popup._timer = Lifetime;
    }

    void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer <= 0f) { Destroy(gameObject); return; }

        transform.position += Vector3.up * (RiseSpeed * Time.deltaTime);

        // Лицом к камере
        Camera cam = Camera.main;
        if (cam != null)
            transform.rotation = cam.transform.rotation;

        // Затухание к концу жизни
        Color c = _baseColor;
        c.a = _baseColor.a * Mathf.Clamp01(_timer / (Lifetime * 0.5f)); // вторая половина жизни — гаснет
        _tm.color = c;
    }
}
