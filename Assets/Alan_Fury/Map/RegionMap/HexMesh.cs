using System.Collections.Generic;
using UnityEngine;

/// <summary>Общая призма гекса. Size — от центра до вершины.</summary>
public static class HexMesh
{
    public static Mesh Prism(float size, float height)
    {
        var mesh = new Mesh();
        mesh.name = "HexPrism";
        const int sides = 6;
        var verts = new Vector3[sides * 2 + 2];
        var tris = new List<int>(sides * 12);

        verts[0] = new Vector3(0f, height * 0.5f, 0f);
        verts[1] = new Vector3(0f, -height * 0.5f, 0f);

        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides + Mathf.PI / 6f;
            float x = Mathf.Cos(a) * size;
            float z = Mathf.Sin(a) * size;
            verts[2 + i] = new Vector3(x, height * 0.5f, z);
            verts[2 + sides + i] = new Vector3(x, -height * 0.5f, z);
        }

        for (int i = 0; i < sides; i++)
        {
            int n = (i + 1) % sides;
            tris.Add(0); tris.Add(2 + i); tris.Add(2 + n);
            tris.Add(1); tris.Add(2 + sides + n); tris.Add(2 + sides + i);
            int t0 = 2 + i, t1 = 2 + n, b0 = 2 + sides + i, b1 = 2 + sides + n;
            tris.Add(t0); tris.Add(b0); tris.Add(t1);
            tris.Add(t1); tris.Add(b0); tris.Add(b1);
        }

        mesh.vertices = verts;
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    public static bool InHex(float x, float z, float size)
    {
        const float k = 0.57735026f;
        float q = (k * x - (1f / 3f) * z) / size;
        float r = ((2f / 3f) * z) / size;
        return Mathf.Abs(q) <= 1f && Mathf.Abs(r) <= 1f && Mathf.Abs(q + r) <= 1f;
    }
}
