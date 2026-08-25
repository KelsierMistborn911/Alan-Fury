#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TerrainManager))]
public class TerrainManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(5);

        TerrainManager manager = (TerrainManager)target;
        HeightMapGenerator heightGen = manager.heightGenerator;

        if (heightGen != null && heightGen.isGenerated)
        {
            EditorGUILayout.HelpBox(
                $"Карта высот готова: {heightGen.width}×{heightGen.depth}\n" +
                $"Seed: {heightGen.seed}\n" +
                $"Размер тайла: {(manager.chunkedTerrainBuilder != null ? manager.chunkedTerrainBuilder.TileSize : 0)}",
                MessageType.Info
            );
        }
        else
        {
            EditorGUILayout.HelpBox(
                "Карта высот не сгенерирована",
                MessageType.Warning
            );
        }
    }
}
#endif
