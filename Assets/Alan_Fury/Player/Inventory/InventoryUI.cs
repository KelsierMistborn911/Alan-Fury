using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Инвентарь. I — открыть. ПКМ по клетке — контекстное меню (Надеть / Снять).
/// </summary>
public class InventoryUI : MonoBehaviour
{
    public Inventory inventory;

    [Header("Настройки")]
    public KeyCode toggleKey = KeyCode.I;
    public int columns = 5;
    public float cellSize = 64f;
    public float cellSpacing = 6f;

    enum SlotKind { Bag, Right, Left }

    Canvas _canvas;
    GameObject _panel;
    Image[] _slotIcons;
    Text[] _slotCounts;
    Image _rightHandIcon;
    Image _leftHandIcon;
    bool _built;

    RectTransform _menu;
    readonly System.Collections.Generic.List<GameObject> _menuRows =
        new System.Collections.Generic.List<GameObject>();

    void Start()
    {
        TryBindInventory();
    }

    void OnDestroy()
    {
        if (inventory != null) inventory.onChanged -= Refresh;
    }

    void Update()
    {
        if (inventory == null)
            TryBindInventory();

        if (inventory == null || _panel == null) return;

        if (Input.GetKeyDown(toggleKey))
        {
            bool open = !_panel.activeSelf;
            _panel.SetActive(open);
            if (!open) CloseMenu();
        }

        if (_menu != null && _menu.gameObject.activeSelf)
        {
            if (Input.GetMouseButtonDown(0) && !PointerOverMenu())
                CloseMenu();
            if (Input.GetMouseButtonDown(1) && !PointerOverSlotOrMenu())
                CloseMenu();
        }
    }

    void TryBindInventory()
    {
        if (inventory != null)
        {
            if (!_built)
            {
                BuildUI();
                inventory.onChanged += Refresh;
                Refresh();
                if (_panel != null) _panel.SetActive(false);
            }
            return;
        }

        var nets = FindObjectsOfType<NetworkPlayer>();
        for (int i = 0; i < nets.Length; i++)
        {
            if (nets[i] == null || !nets[i].IsLocalControlled) continue;
            inventory = nets[i].GetComponent<Inventory>();
            if (inventory != null) break;
        }

        if (inventory == null)
        {
            var primary = PlayerRegistry.ResolvePrimary();
            if (primary != null)
                inventory = primary.GetComponent<Inventory>();
        }

        if (inventory == null)
            return;

        BuildUI();
        inventory.onChanged += Refresh;
        Refresh();
        if (_panel != null) _panel.SetActive(false);
    }

    void BuildUI()
    {
        if (_built) return;
        _built = true;

        if (FindObjectOfType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }

        var canvasObj = new GameObject("InventoryCanvas");
        canvasObj.transform.SetParent(transform);
        _canvas = canvasObj.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 20;
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        _panel = CreateImage("Panel", canvasObj.transform, new Color(0.08f, 0.08f, 0.08f, 0.92f)).gameObject;
        var panelRt = _panel.GetComponent<RectTransform>();
        int rows = Mathf.CeilToInt(inventory.slotCount / (float)columns);
        float w = columns * (cellSize + cellSpacing) + cellSpacing;
        float h = (rows + 1) * (cellSize + cellSpacing) + cellSpacing + 30f;
        panelRt.sizeDelta = new Vector2(w, h);
        panelRt.anchoredPosition = Vector2.zero;

        var title = CreateText("Title", _panel.transform, "Инвентарь  ·  ПКМ — меню", 16);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.5f, 1f);
        titleRt.anchorMax = new Vector2(0.5f, 1f);
        titleRt.anchoredPosition = new Vector2(0f, -18f);
        titleRt.sizeDelta = new Vector2(w, 24f);

        _rightHandIcon = CreateSlot(_panel.transform, "RightHand", 0, 0, SlotKind.Right, -1);
        _leftHandIcon = CreateSlot(_panel.transform, "LeftHand", 1, 0, SlotKind.Left, -1);

        _slotIcons = new Image[inventory.slotCount];
        _slotCounts = new Text[inventory.slotCount];
        for (int i = 0; i < inventory.slotCount; i++)
        {
            int index = i;
            int col = i % columns;
            int row = i / columns + 1;
            _slotIcons[i] = CreateSlot(_panel.transform, $"Slot{i}", col, row, SlotKind.Bag, index);
            _slotCounts[i] = CreateText($"Count{i}", _slotIcons[i].transform, "", 12);
            var crt = _slotCounts[i].GetComponent<RectTransform>();
            crt.anchorMin = Vector2.zero;
            crt.anchorMax = Vector2.one;
            crt.offsetMin = Vector2.zero;
            crt.offsetMax = new Vector2(-4f, -2f);
            _slotCounts[i].alignment = TextAnchor.LowerRight;
        }

        BuildMenu(canvasObj.transform);
    }

    Image CreateSlot(Transform parent, string name, int col, int row, SlotKind kind, int index)
    {
        var bg = CreateImage(name, parent, kind == SlotKind.Bag
            ? new Color(0.18f, 0.18f, 0.18f, 1f)
            : new Color(0.28f, 0.24f, 0.12f, 1f));
        var rt = bg.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(cellSize, cellSize);
        rt.anchoredPosition = new Vector2(
            cellSpacing + col * (cellSize + cellSpacing),
            -(36f + cellSpacing + row * (cellSize + cellSpacing)));

        var click = bg.gameObject.AddComponent<InventorySlotClick>();
        click.onClick = ev => OnSlotClick(kind, index, ev);

        var icon = CreateImage("Icon", bg.transform, Color.white);
        var irt = icon.GetComponent<RectTransform>();
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.offsetMin = new Vector2(4f, 4f);
        irt.offsetMax = new Vector2(-4f, -4f);
        icon.enabled = false;
        icon.raycastTarget = false;
        return icon;
    }

    void BuildMenu(Transform parent)
    {
        var img = CreateImage("ContextMenu", parent, new Color(0.10f, 0.10f, 0.10f, 0.98f));
        _menu = img.GetComponent<RectTransform>();
        _menu.pivot = new Vector2(0f, 1f);
        _menu.sizeDelta = new Vector2(160f, 32f);
        img.gameObject.AddComponent<VerticalLayoutGroup>().padding = new RectOffset(1, 1, 1, 1);
        var fitter = img.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        img.gameObject.SetActive(false);
    }

    void OnSlotClick(SlotKind kind, int index, PointerEventData ev)
    {
        if (ev.button != PointerEventData.InputButton.Right) return;
        OpenMenu(kind, index, ev.position);
    }

    void OpenMenu(SlotKind kind, int index, Vector2 screenPos)
    {
        ClearMenuRows();

        if (kind == SlotKind.Bag)
        {
            if (index < 0 || index >= inventory.Slots.Count)
            {
                CloseMenu();
                return;
            }
            var slot = inventory.Slots[index];
            if (slot.IsEmpty || slot.item.type != ItemData.ItemType.Equipment || slot.item.weapon == null)
            {
                CloseMenu();
                return;
            }
            AddMenuRow("Надеть", () =>
            {
                inventory.Equip(index);
                CloseMenu();
            });
        }
        else
        {
            bool empty = kind == SlotKind.Right
                ? inventory.EquippedRight == null
                : inventory.EquippedLeft == null;
            if (empty)
            {
                CloseMenu();
                return;
            }
            AddMenuRow("Снять", () =>
            {
                if (kind == SlotKind.Right) inventory.UnequipRight();
                else inventory.UnequipLeft();
                CloseMenu();
            });
        }

        _menu.gameObject.SetActive(true);
        _menu.SetAsLastSibling();
        _menu.position = screenPos;
        ClampMenuToScreen();
    }

    void AddMenuRow(string label, UnityEngine.Events.UnityAction action)
    {
        var row = CreateImage("Row_" + label, _menu, new Color(0.14f, 0.14f, 0.14f, 1f));
        var rt = row.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(158f, 28f);

        var btn = row.gameObject.AddComponent<Button>();
        var colors = btn.colors;
        colors.normalColor = new Color(0.14f, 0.14f, 0.14f, 1f);
        colors.highlightedColor = new Color(0.22f, 0.20f, 0.14f, 1f);
        colors.pressedColor = new Color(0.32f, 0.28f, 0.16f, 1f);
        btn.colors = colors;
        btn.onClick.AddListener(action);

        var txt = CreateText("Label", row.transform, label, 14);
        txt.alignment = TextAnchor.MiddleLeft;
        var trt = txt.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(10f, 0f);
        trt.offsetMax = new Vector2(-6f, 0f);
        txt.raycastTarget = false;

        var le = row.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 28f;
        le.preferredHeight = 28f;

        _menuRows.Add(row.gameObject);
    }

    void ClearMenuRows()
    {
        for (int i = 0; i < _menuRows.Count; i++)
            if (_menuRows[i] != null) Destroy(_menuRows[i]);
        _menuRows.Clear();
    }

    void CloseMenu()
    {
        if (_menu != null) _menu.gameObject.SetActive(false);
        ClearMenuRows();
    }

    void ClampMenuToScreen()
    {
        if (_canvas == null) return;
        Vector3[] corners = new Vector3[4];
        _menu.GetWorldCorners(corners);
        float dx = 0f, dy = 0f;
        if (corners[2].x > Screen.width) dx = Screen.width - corners[2].x;
        if (corners[0].x < 0f) dx = -corners[0].x;
        if (corners[2].y > Screen.height) dy = Screen.height - corners[2].y;
        if (corners[0].y < 0f) dy = -corners[0].y;
        _menu.position += new Vector3(dx, dy, 0f);
    }

    bool PointerOverMenu()
    {
        if (_menu == null || !_menu.gameObject.activeSelf) return false;
        if (EventSystem.current == null) return false;
        var ped = new PointerEventData(EventSystem.current) { position = Input.mousePosition };
        var hits = new System.Collections.Generic.List<RaycastResult>();
        EventSystem.current.RaycastAll(ped, hits);
        for (int i = 0; i < hits.Count; i++)
            if (hits[i].gameObject.transform.IsChildOf(_menu) || hits[i].gameObject == _menu.gameObject)
                return true;
        return false;
    }

    bool PointerOverSlotOrMenu()
    {
        return PointerOverMenu();
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
        txt.color = new Color(0.85f, 0.85f, 0.82f, 1f);
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.alignment = TextAnchor.MiddleCenter;
        return txt;
    }

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
                string name = slot.item.itemName;
                if (string.IsNullOrEmpty(name)) name = slot.item.name;
                _slotCounts[i].text = slot.count > 1 ? name + " ×" + slot.count : name;
            }
        }

        SetEquipIcon(_rightHandIcon, inventory.EquippedRight);
        SetEquipIcon(_leftHandIcon, inventory.EquippedLeft);
    }

    void SetEquipIcon(Image icon, ItemData item)
    {
        if (item == null || item.icon == null)
            icon.enabled = false;
        else
        {
            icon.enabled = true;
            icon.sprite = item.icon;
        }
    }
}

public class InventorySlotClick : MonoBehaviour, IPointerClickHandler
{
    public System.Action<PointerEventData> onClick;

    public void OnPointerClick(PointerEventData eventData)
    {
        onClick?.Invoke(eventData);
    }
}
