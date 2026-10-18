using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class SceneSwitcher : EditorWindow
{
    const string ScenesFolder = "Assets/Alan_Fury/Scenes";

    [MenuItem("Scenes/Open Switcher %#0")] // Ctrl+Shift+0
    static void OpenWindow()
    {
        GetWindow<SceneSwitcher>("Scenes");
    }

    void OnGUI()
    {
        GUILayout.Label("Scenes", EditorStyles.boldLabel);

        if (!AssetDatabase.IsValidFolder(ScenesFolder))
        {
            EditorGUILayout.HelpBox($"Папка не найдена:\n{ScenesFolder}", MessageType.Warning);
            return;
        }

        var guids = AssetDatabase.FindAssets("t:Scene", new[] { ScenesFolder });
        if (guids.Length == 0)
        {
            EditorGUILayout.HelpBox("В папке нет сцен.", MessageType.Info);
            return;
        }

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var name = Path.GetFileNameWithoutExtension(path);

            if (GUILayout.Button(name, GUILayout.Height(24)))
            {
                if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    EditorSceneManager.OpenScene(path);
            }
        }
    }
}
