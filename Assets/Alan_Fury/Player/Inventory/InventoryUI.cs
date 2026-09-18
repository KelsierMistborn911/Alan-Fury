using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// Инвентарь-сетка. I — открыть. ПКМ по предмету — Надеть / Снять / Использовать.
/// </summary>
public class InventoryUI : MonoBehaviour
{
    public Inventory inventory;

    [Header("Настройки")]
    public KeyCode toggleKey = KeyCode.I;
    public float cellSize = 44f;
    public float cellSpacing = 3f;

    enum SlotKind { Bag, Right, Left }

    Canvas _canvas;
    GameObject _panel;
    RectTransform _gridRoot;
    Image _rightHandIcon;
    Image _leftHandIcon;
    readonly List<GameObject> _itemViews = new List<GameObject>();
    bool _built;

    RectTransform _menu;
    readonly List<GameObject> _menuRows = new List<GameObject>();

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
            if (Input.GetMouseButtonDown(1) && !PointerOverMenu())
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

    float Step => cellSize + cellSpacing;
    int Cols => inventory != null ? inventory.gridWidth : 10;
    int Rows => inventory != null ? inventory.gridHeight : 8;

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

        float gridW = Cols * Step + cellSpacing;
        float gridH = Rows * Step + cellSpacing;
        float handH = cellSize * 2f + cellSpacing;
        float w = gridW + 16f;
        float h = gridH + handH + 52f;

        _panel = CreateImage("Panel", canvasObj.transform, new Color(0.08f, 0.08f, 0.08f, 0.94f)).gameObject;
        var panelRt = _panel.GetComponent<RectTransform>();
        panelRt.sizeDelta = new Vector2(w, h);
        panelRt.anchoredPosition = Vector2.zero;

        var title = CreateText("Title", _panel.transform, "Инвентарь  ·  ПКМ — меню", 15);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.5f, 1f);
        titleRt.anchorMax = new Vector2(0.5f, 1f);
        titleRt.anchoredPosition = new Vector2(0f, -16f);
        titleRt.sizeDelta = new Vector2(w, 22f);

        _rightHandIcon = CreateHand(_panel.transform, "RightHand", SlotKind.Right, 8f, -(36f));
        _leftHandIcon = CreateHand(_panel.transform, "LeftHand", SlotKind.Left, 8f + Step * 3f, -(36f));

        _gridRoot = CreateImage("Grid", _panel.transform, new Color(0.06f, 0.06f, 0.06f, 1f)).rectTransform;
        _gridRoot.anchorMin = new Vector2(0f, 1f);
        _gridRoot.anchorMax = new Vector2(0f, 1f);
        _gridRoot.pivot = new Vector2(0f, 1f);
        _gridRoot.anchoredPosition = new Vector2(8f, -(36f + handH + 4f));
        _gridRoot.sizeDelta = new Vector2(gridW, gridH);

        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                var cell = CreateImage($"c{x}_{y}", _gridRoot, new Color(0.17f, 0.17f, 0.17f, 1f));
                var rt = cell.rectTransform;
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(cellSize, cellSize);
                rt.anchoredPosition = new Vector2(cellSpacing + x * Step, -(cellSpacing + y * Step));
                cell.raycastTarget = false;
            }
        }

        BuildMenu(canvasObj.transform);
    }

    Image CreateHand(Transform parent, string name, SlotKind kind, float x, float y)
    {
        var bg = CreateImage(name, parent, new Color(0.28f, 0.24f, 0.12f, 1f));
        var rt = bg.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(cellSize * 2.4f, cellSize * 2f);
        rt.anchoredPosition = new Vector2(x, y);

        var click = bg.gameObject.AddComponent<InventorySlotClick>();
        click.onClick = ev => OnSlotClick(kind, -1, ev);

        var label = CreateText("HandLabel", bg.transform, kind == SlotKind.Right ? "ПК" : "ЛК", 11);
        var lrt = label.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0f, 1f);
        lrt.anchorMax = new Vector2(1f, 1f);
        lrt.pivot = new Vector2(0.5f, 1f);
        lrt.sizeDelta = new Vector2(0f, 14f);
        lrt.anchoredPosition = new Vector2(0f, -2f);
        label.raycastTarget = false;

        var icon = CreateImage("Icon", bg.transform, Color.white);
        var irt = icon.GetComponent<RectTransform>();
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.offsetMin = new Vector2(6f, 4f);
        irt.offsetMax = new Vector2(-6f, -16f);
        icon.enabled = false;
        icon.raycastTarget = false;
        icon.preserveAspect = true;
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
            if (slot.IsEmpty)
            {
                CloseMenu();
                return;
            }

            bool any = false;
            if (slot.item.type == ItemData.ItemType.Equipment && slot.item.weapon != null)
            {
                AddMenuRow("Надеть", () =>
                {
                    inventory.Equip(index);
                    CloseMenu();
                });
                any = true;
            }
            if (slot.item.CanUse)
            {
                AddMenuRow("Использовать", () =>
                {
                    inventory.Use(index);
                    CloseMenu();
                });
                any = true;
            }
            if (!any)
            {
                CloseMenu();
                return;
            }
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
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(ped, hits);
        for (int i = 0; i < hits.Count; i++)
            if (hits[i].gameObject.transform.IsChildOf(_menu) || hits[i].gameObject == _menu.gameObject)
                return true;
        return false;
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
        if (!_built || inventory == null || _gridRoot == null) return;

        for (int i = 0; i < _itemViews.Count; i++)
            if (_itemViews[i] != null) Destroy(_itemViews[i]);
        _itemViews.Clear();

        for (int i = 0; i < inventory.Slots.Count; i++)
        {
            var slot = inventory.Slots[i];
            if (slot.IsEmpty) continue;
            _itemViews.Add(CreateItemView(slot, i));
        }

        SetEquipIcon(_rightHandIcon, inventory.EquippedRight);
        SetEquipIcon(_leftHandIcon, inventory.EquippedLeft);
    }

    GameObject CreateItemView(Inventory.Slot slot, int index)
    {
        float w = slot.W * cellSize + (slot.W - 1) * cellSpacing;
        float h = slot.H * cellSize + (slot.H - 1) * cellSpacing;

        var bg = CreateImage("Item_" + index, _gridRoot, new Color(0.22f, 0.20f, 0.14f, 0.95f));
        var rt = bg.rectTransform;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(
            cellSpacing + slot.x * Step,
            -(cellSpacing + slot.y * Step));

        var click = bg.gameObject.AddComponent<InventorySlotClick>();
        click.onClick = ev => OnSlotClick(SlotKind.Bag, index, ev);

        var icon = CreateImage("Icon", bg.transform, Color.white);
        var irt = icon.rectTransform;
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.offsetMin = new Vector2(3f, 3f);
        irt.offsetMax = new Vector2(-3f, -3f);
        var sprite = slot.item.ResolvedIcon;
        icon.enabled = sprite != null;
        icon.sprite = sprite;
        icon.preserveAspect = true;
        icon.raycastTarget = false;

        string name = slot.item.itemName;
        if (string.IsNullOrEmpty(name)) name = slot.item.name;
        string label = slot.count > 1 ? name + " ×" + slot.count : name;
        var txt = CreateText("Name", bg.transform, label, 11);
        txt.alignment = TextAnchor.LowerLeft;
        var trt = txt.rectTransform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(4f, 2f);
        trt.offsetMax = new Vector2(-4f, -2f);
        txt.raycastTarget = false;

        return bg.gameObject;
    }

    void SetEquipIcon(Image icon, ItemData item)
    {
        var sprite = item != null ? item.ResolvedIcon : null;
        if (sprite == null)
            icon.enabled = false;
        else
        {
            icon.enabled = true;
            icon.sprite = sprite;
            icon.preserveAspect = true;
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
