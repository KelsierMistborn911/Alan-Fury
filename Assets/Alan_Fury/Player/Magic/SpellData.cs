using UnityEngine;

[CreateAssetMenu(fileName = "NewSpell", menuName = "Combat/Spell Data")]
public class SpellData : ScriptableObject
{
    public string spellName = "Spell";
    public Sprite icon;
    public KeyCode hotkey = KeyCode.Q;

    public float damagePerSecond = 30f;
    public float tickInterval = 0.2f;
    public float staggerForce = 3f;

    public float maxRange = 20f;
    public float beamWidth = 0.3f;

    public float manaCostPerSecond = 25f;
    public float maxDuration = 5f;

    public Color beamColor = Color.cyan;
    public Material beamMaterial;

    public LayerMask targetLayers;
    public LayerMask obstacleLayers;
}
