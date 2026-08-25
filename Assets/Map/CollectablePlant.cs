using UnityEngine;

/// <summary>
/// Триггер сбора растения (гриба). Создаётся NaturePlacement / SpriteVegetationPlacer,
/// вручную на сцену не вешается. Игрок в радиусе жмёт E — предмет уходит
/// в Inventory, экземпляр убирается из инстансной отрисовки.
/// </summary>
public class CollectablePlant : MonoBehaviour
{
    public KeyCode collectKey = KeyCode.E;

    public interface IInstanceRemover
    {
        bool RemoveInstance(int typeIndex, Vector2Int sector, Vector3 worldPos);
    }

    private IInstanceRemover remover;
    private int typeIndex;
    private Vector2Int sector;
    private Vector3 worldPos;
    private ItemData itemData;
    private int itemCount;

    private Inventory inventoryInRange;
    private bool inventoryFull;

    public void Init(IInstanceRemover remover, int typeIndex, Vector2Int sector,
                     Vector3 worldPos, ItemData itemData, int itemCount)
    {
        this.remover = remover;
        this.typeIndex = typeIndex;
        this.sector = sector;
        this.worldPos = worldPos;
        this.itemData = itemData;
        this.itemCount = itemCount;
    }

    // Совместимость со старым SpriteVegetationPlacer
    public void Init(SpriteVegetationPlacer placer, int typeIndex, Vector2Int sector,
                     Vector3 worldPos, ItemData itemData, int itemCount)
    {
        this.remover = new SpriteRemoverAdapter(placer);
        this.typeIndex = typeIndex;
        this.sector = sector;
        this.worldPos = worldPos;
        this.itemData = itemData;
        this.itemCount = itemCount;
    }

    private class SpriteRemoverAdapter : IInstanceRemover
    {
        private readonly SpriteVegetationPlacer _p;
        public SpriteRemoverAdapter(SpriteVegetationPlacer p) { _p = p; }
        public bool RemoveInstance(int typeIndex, Vector2Int sector, Vector3 worldPos)
            => _p != null && _p.RemoveInstance(typeIndex, sector, worldPos);
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
                if (remover != null)
                    remover.RemoveInstance(typeIndex, sector, worldPos);
                Destroy(gameObject);
            }
            else
            {
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
