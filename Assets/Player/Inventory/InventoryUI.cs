using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Простой UI инвентаря: сетка слотов, строится кодом при старте — префабы не нужны.
/// Повесь на любой объект в сцене, укажи inventory (или найдётся по тегу Player).
/// Открытие/закрытие — клавиша toggleKey (по умолчанию I).
/// Клик по слоту с экипировкой — надеть. Клик по слоту "Правая/Левая рука" — снять.
/// </summary>
public class InventoryUI : MonoBehaviour
{
    [Header("Ссылки")]
    [Tooltip("Пусто — ищется на объекте с тегом Player.")]
    public Inventory inventory;

    [Header("Настройки")]
    public KeyCode toggleKey = KeyCode.I;
    [Tooltip("Слотов в ряду сетки.")]
    public int columns = 5;
    [Tooltip("Размер ячейки (px).")]
    public float cellSize = 64f;
    public float cellSpacing = 6f;

    private Canvas _canvas;
    private GameObject _panel;
    private Image[] _slotIcons;
    private Text[] _slotCounts;
    private Image _rightHandIcon;
    private Image _leftHandIcon;
    private bool _built;

    void Start()
    {
        if (inventory == null)
        {
            var player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) inventory = player.GetComponent<Inventory>();
        }
        if (inventory == null)
        {
            Debug.LogWarning("InventoryUI: инвентарь не найден.");
            enabled = false;
            return;
        }

        BuildUI();
        inventory.onChanged += Refresh;
        Refresh();
        _panel.SetActive(false);
    }

    void OnDestroy()
    {
        if (inventory != null) inventory.onChanged -= Refresh;
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            _panel.SetActive(!_panel.activeSelf);
    }

    // ==================== Построение ====================

    void BuildUI()
    {
        if (_built) return;
        _built = true;

        // Canvas
        var canvasObj = new GameObject("InventoryCanvas");
        canvasObj.transform.SetParent(transform);
        _canvas = canvasObj.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        // Панель-фон
        _panel = CreateImage("Panel", canvasObj.transform, new Color(0f, 0f, 0f, 0.85f)).gameObject;
        var panelRt = _panel.GetComponent<RectTransform>();
        int rows = Mathf.CeilToInt(inventory.slotCount / (float)columns);
        float w = columns * (cellSize + cellSpacing) + cellSpacing;
        float h = (rows + 1) * (cellSize + cellSpacing) + cellSpacing + 30f; // +ряд экипировки +заголовок
        panelRt.sizeDelta = new Vector2(w, h);
        panelRt.anchoredPosition = Vector2.zero;

        // Заголовок
        var title = CreateText("Title", _panel.transform, "Инвентарь (клик — надеть/снять)", 16);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.5f, 1f);
        titleRt.anchorMax = new Vector2(0.5f, 1f);
        titleRt.anchoredPosition = new Vector2(0f, -18f);
        titleRt.sizeDelta = new Vector2(w, 24f);

        // Ряд экипировки: правая и левая рука
        _rightHandIcon = CreateSlot(_panel.transform, "RightHand", 0, 0, () => inventory.UnequipRight(), true);
        _leftHandIcon = CreateSlot(_panel.transform, "LeftHand", 1, 0, () => inventory.UnequipLeft(), true);

        // Сетка сумки
        _slotIcons = new Image[inventory.slotCount];
        _slotCounts = new Text[inventory.slotCount];
        for (int i = 0; i < inventory.slotCount; i++)
        {
            int index = i; // замыкание
            int col = i % columns;
            int row = i / columns + 1; // +1 — под рядом экипировки
            _slotIcons[i] = CreateSlot(_panel.transform, $"Slot{i}", col, row, () => inventory.Equip(index), false);
            _slotCounts[i] = CreateText($"Count{i}", _slotIcons[i].transform, "", 12);
            var crt = _slotCounts[i].GetComponent<RectTransform>();
            crt.anchorMin = Vector2.zero;
            crt.anchorMax = Vector2.one;
            crt.offsetMin = Vector2.zero;
            crt.offsetMax = new Vector2(-4f, -2f);
            _slotCounts[i].alignment = TextAnchor.LowerRight;
        }
    }

    Image CreateSlot(Transform parent, string name, int col, int row, System.Action onClick, bool isEquipSlot)
    {
        var bg = CreateImage(name, parent, isEquipSlot ? new Color(0.35f, 0.3f, 0.1f, 1f) : new Color(0.2f, 0.2f, 0.2f, 1f));
        var rt = bg.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(cellSize, cellSize);
        rt.anchoredPosition = new Vector2(
            cellSpacing + col * (cellSize + cellSpacing),
            -(36f + cellSpacing + row * (cellSize + cellSpacing)));

        var btn = bg.gameObject.AddComponent<Button>();
        btn.onClick.AddListener(() => onClick());

        // Иконка поверх фона
        var icon = CreateImage("Icon", bg.transform, Color.white);
        var irt = icon.GetComponent<RectTransform>();
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.offsetMin = new Vector2(4f, 4f);
        irt.offsetMax = new Vector2(-4f, -4f);
        icon.enabled = false;
        return icon;
    }

    Image CreateImage(string name, Transform parent, Color color)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        var img = obj.AddComponent<Image>();
        img.color = color;
        return img;
    }

    Text CreateText(string name, Transform parent, string content, int size)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        var txt = obj.AddComponent<Text>();
        txt.text = content;
        txt.fontSize = size;
        txt.color = Color.white;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.alignment = TextAnchor.MiddleCenter;
        return txt;
    }

    // ==================== Отрисовка ====================

    void Refresh()
    {
        if (!_built) return;

        for (int i = 0; i < inventory.Slots.Count && i < _slotIcons.Length; i++)
        {
            var slot = inventory.Slots[i];
            if (slot.IsEmpty)
            {
                _slotIcons[i].enabled = false;
                _slotCounts[i].text = "";
            }
            else
            {
                _slotIcons[i].enabled = slot.item.icon != null;
                _slotIcons[i].sprite = slot.item.icon;
                _slotCounts[i].text = slot.count > 1 ? slot.count.ToString() : "";
            }
        }

        SetEquipIcon(_rightHandIcon, inventory.EquippedRight);
        SetEquipIcon(_leftHandIcon, inventory.EquippedLeft);
    }

    void SetEquipIcon(Image icon, ItemData item)
    {
        if (item == null || item.icon == null)
        {
            icon.enabled = false;
        }
        else
        {
            icon.enabled = true;
            icon.sprite = item.icon;
        }
    }
}
