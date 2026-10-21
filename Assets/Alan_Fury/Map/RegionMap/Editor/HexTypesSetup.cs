#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Alan Fury → Create Hex Types.
/// Создаёт HexTypes.asset и папку Prefabs с гексом каждого типа.
/// На гексе HexContent со списками. Туда вешаются деревья, дома, спрайты.
/// </summary>
public static class HexTypesSetup
{
    const string Root = "Assets/Alan_Fury/Map/HexTypes";

    [MenuItem("Alan Fury/Create Hex Types")]
    public static void Create()
    {
        Ensure("Assets/Alan_Fury");
        Ensure("Assets/Alan_Fury/Map");
        Ensure(Root);
        Ensure(Root + "/Prefabs");

        var forestTree = MakeTree(Root + "/Prefabs/Forest/Tree.prefab", 1.1f, new Color(0.22f, 0.42f, 0.2f));
        var groveTree = MakeTree(Root + "/Prefabs/Grove/Tree.prefab", 0.75f, new Color(0.45f, 0.62f, 0.28f));
        var house = MakeHouse(Root + "/Prefabs/Settlement/House.prefab");

        var forest = MakeHex("Forest", "лес", new Color(0.28f, 0.48f, 0.26f), new[]
        {
            Place("деревья", forestTree, null, 8, 14, 0.85f, 1.2f, 0.05f, 0.92f),
            Place("трава", null, null, 6, 12, 0.7f, 1.1f, 0f, 1f, flat: true)
        });
        var grove = MakeHex("Grove", "роща", new Color(0.55f, 0.72f, 0.38f), new[]
        {
            Place("деревья", groveTree, null, 3, 6, 0.8f, 1.1f, 0.08f, 0.9f),
            Place("трава", null, null, 4, 8, 0.7f, 1f, 0f, 1f, flat: true)
        });
        var town = MakeHex("Settlement", "населенный пункт", new Color(0.75f, 0.62f, 0.35f), new[]
        {
            Place("дома", house, null, 5, 7, 0.9f, 1.15f, 0.12f, 0.72f),
            Place("лужа", null, null, 1, 2, 0.8f, 1.2f, 0.15f, 0.8f, flat: true)
        });

        var assetPath = Root + "/HexTypes.asset";
        var asset = AssetDatabase.LoadAssetAtPath<HexTypes>(assetPath);
        if (asset == null)
        {
            asset = ScriptableObject.CreateInstance<HexTypes>();
            AssetDatabase.CreateAsset(asset, assetPath);
        }
        asset.types = new List<HexTypeEntry>
        {
            Entry("лес", forest),
            Entry("роща", grove),
            Entry("населенный пункт", town)
        };
        EditorUtility.SetDirty(asset);

        var map = Object.FindObjectOfType<RegionHexMap>();
        if (map != null)
        {
            map.hexTypesAsset = asset;
            EditorUtility.SetDirty(map);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorUtility.FocusProjectWindow();
        Selection.activeObject = asset;
        Debug.Log("HexTypes: " + assetPath + " + Prefabs/Forest, Grove, Settlement");
    }

    static HexTypeEntry Entry(string kind, GameObject prefab)
    {
        return new HexTypeEntry { kind = kind, hexPrefab = prefab, hideBase = true };
    }

    static HexPlace Place(string label, GameObject prefab, Sprite sprite,
        int min, int max, float minS, float maxS, float minR, float maxR, bool flat = true)
    {
        return new HexPlace
        {
            label = label,
            prefab = prefab,
            sprite = sprite,
            flat = flat,
            minCount = min,
            maxCount = max,
            minScale = minS,
            maxScale = maxS,
            minRadius = minR,
            maxRadius = maxR
        };
    }

    static GameObject MakeHex(string folder, string kind, Color color, HexPlace[] places)
    {
        string dir = Root + "/Prefabs/" + folder;
        Ensure(dir);
        var root = new GameObject("Hex_" + folder);
        var content = root.AddComponent<HexContent>();
        content.kind = kind;
        content.places = new List<HexPlace>(places);

        var baseGo = new GameObject("Base");
        baseGo.transform.SetParent(root.transform, false);
        var mf = baseGo.AddComponent<MeshFilter>();
        mf.sharedMesh = HexMesh.Prism(1f, 1f);
        var mr = baseGo.AddComponent<MeshRenderer>();
        mr.sharedMaterial = MakeMat(color);

        string path = dir + "/Hex_" + folder + ".prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab;
    }

    static GameObject MakeTree(string path, float scale, Color crown)
    {
        Ensure(Path.GetDirectoryName(path).Replace('\\', '/'));
        var root = new GameObject("Tree");
        var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        trunk.name = "Trunk";
        trunk.transform.SetParent(root.transform, false);
        trunk.transform.localScale = new Vector3(0.08f, 0.22f, 0.08f) * scale;
        trunk.transform.localPosition = new Vector3(0f, 0.22f * scale, 0f);
        trunk.GetComponent<MeshRenderer>().sharedMaterial = MakeMat(new Color(0.35f, 0.24f, 0.14f));
        Object.DestroyImmediate(trunk.GetComponent<Collider>());

        var top = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        top.name = "Crown";
        top.transform.SetParent(root.transform, false);
        top.transform.localScale = new Vector3(0.38f, 0.28f, 0.38f) * scale;
        top.transform.localPosition = new Vector3(0f, 0.48f * scale, 0f);
        top.GetComponent<MeshRenderer>().sharedMaterial = MakeMat(crown);
        Object.DestroyImmediate(top.GetComponent<Collider>());

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab;
    }

    static GameObject MakeHouse(string path)
    {
        Ensure(Path.GetDirectoryName(path).Replace('\\', '/'));
        var root = new GameObject("House");
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = new Vector3(0.28f, 0.22f, 0.28f);
        body.transform.localPosition = new Vector3(0f, 0.11f, 0f);
        body.GetComponent<MeshRenderer>().sharedMaterial = MakeMat(new Color(0.72f, 0.58f, 0.4f));
        Object.DestroyImmediate(body.GetComponent<Collider>());

        var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
        roof.name = "Roof";
        roof.transform.SetParent(root.transform, false);
        roof.transform.localScale = new Vector3(0.34f, 0.1f, 0.34f);
        roof.transform.localPosition = new Vector3(0f, 0.27f, 0f);
        roof.GetComponent<MeshRenderer>().sharedMaterial = MakeMat(new Color(0.45f, 0.22f, 0.16f));
        Object.DestroyImmediate(roof.GetComponent<Collider>());

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab;
    }

    static Material MakeMat(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var mat = new Material(shader);
        mat.color = color;
        return mat;
    }

    static void Ensure(string path)
    {
        if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        string name = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent)) Ensure(parent);
        AssetDatabase.CreateFolder(parent, name);
    }
}
#endif
