using UnityEngine;

/// <summary>
/// Триггер сбора растения (гриба). Создаётся SpriteVegetationPlacer'ом,
/// вручную на сцену не вешается. Игрок в радиусе жмёт E — предмет уходит
/// в Inventory, экземпляр убирается из инстансной отрисовки.
/// </summary>
public class CollectablePlant : MonoBehaviour
{
    public KeyCode collectKey = KeyCode.E;

    private SpriteVegetationPlacer placer;
    private int typeIndex;
    private Vector2Int sector;
    private Vector3 worldPos;
    private ItemData itemData;
    private int itemCount;

    private Inventory inventoryInRange; // инвентарь игрока, пока он в триггере
    private bool inventoryFull;

    public void Init(SpriteVegetationPlacer placer, int typeIndex, Vector2Int sector,
                     Vector3 worldPos, ItemData itemData, int itemCount)
    {
        this.placer = placer;
        this.typeIndex = typeIndex;
        this.sector = sector;
        this.worldPos = worldPos;
        this.itemData = itemData;
        this.itemCount = itemCount;
    }

    void OnTriggerEnter(Collider other)
    {
        var inv = other.GetComponentInParent<Inventory>();
        if (inv != null) inventoryInRange = inv;
    }

    void OnTriggerExit(Collider other)
    {
        if (other.GetComponentInParent<Inventory>() == inventoryInRange)
        {
            inventoryInRange = null;
            inventoryFull = false;
        }
    }

    void Update()
    {
        if (inventoryInRange == null || itemData == null) return;

        if (Input.GetKeyDown(collectKey))
        {
            int left = inventoryInRange.Add(itemData, itemCount);
            if (left == 0)
            {
                placer.RemoveInstance(typeIndex, sector, worldPos);
                Destroy(gameObject);
            }
            else
            {
                // Не влезло целиком — откатываем то, что успело добавиться, и сообщаем
                if (left < itemCount) inventoryInRange.Remove(itemData, itemCount - left);
                inventoryFull = true;
            }
        }
    }

    void OnGUI()
    {
        if (inventoryInRange == null) return;

        string text = inventoryFull ? "Инвентарь полон" : $"Собрать [{collectKey}]";
        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 20
        };
        GUI.Label(new Rect(0, Screen.height - 80, Screen.width, 30), text, style);
    }
}
