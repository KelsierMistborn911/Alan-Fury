using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Появление снизу вверх: тело «догружается» лучом от ступней к голове.
/// Вешается само из SpawnArrivalLight.Reveal. Шейдер VFX/ArrivalReveal.
/// </summary>
public class ArrivalMaterialize : MonoBehaviour
{
    public float duration = 0.4f;
    public Color edgeColor = new Color(1.6f, 1.5f, 1.2f, 1f);

    Renderer[] _renderers;
    Material[][] _saved;
    Material[] _temps;
    float _feet;
    float _head;
    float _age;
    bool _playing;

    public static void Begin(Transform actor, Color edge)
    {
        if (actor == null) return;
        var m = actor.GetComponent<ArrivalMaterialize>();
        if (m == null) m = actor.gameObject.AddComponent<ArrivalMaterialize>();
        m.edgeColor = edge;
        m.Play();
    }

    public void Play()
    {
        Restore();
        var list = new List<Renderer>();
        var rends = GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rends.Length; i++)
        {
            if (rends[i] == null) continue;
            if (rends[i] is ParticleSystemRenderer) continue;
            if (rends[i].gameObject.name == "Column" || rends[i].gameObject.name == "Pool") continue;
            list.Add(rends[i]);
        }
        _renderers = list.ToArray();
        if (_renderers.Length == 0) return;

        var sh = Shader.Find("VFX/ArrivalReveal");
        if (sh == null) return;

        Bounds b = _renderers[0].bounds;
        for (int i = 1; i < _renderers.Length; i++)
            b.Encapsulate(_renderers[i].bounds);
        _feet = b.min.y;
        _head = b.max.y + 0.15f;

        _saved = new Material[_renderers.Length][];
        _temps = new Material[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
        {
            _saved[i] = _renderers[i].sharedMaterials;
            var src = _saved[i].Length > 0 ? _saved[i][0] : null;
            var mat = new Material(sh);
            if (src != null)
            {
                if (src.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                else if (src.HasProperty("_MainTex")) mat.SetTexture("_BaseMap", src.GetTexture("_MainTex"));
                if (src.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", src.GetColor("_BaseColor"));
            }
            mat.SetColor("_EdgeColor", edgeColor);
            _temps[i] = mat;
            _renderers[i].sharedMaterial = mat;
        }

        _age = 0f;
        _playing = true;
        Push(0f);
    }

    void Update()
    {
        if (!_playing) return;
        _age += Time.deltaTime;
        float t = Mathf.Clamp01(_age / Mathf.Max(0.05f, duration));
        t = t * t * (3f - 2f * t);
        Push(t);
        if (_age >= duration)
        {
            _playing = false;
            Restore();
        }
    }

    void Push(float t)
    {
        float cut = Mathf.Lerp(_feet, _head, t);
        for (int i = 0; i < _temps.Length; i++)
        {
            if (_temps[i] == null) continue;
            _temps[i].SetFloat("_CutY", cut);
            _temps[i].SetFloat("_Edge", 0.18f);
        }
    }

    void Restore()
    {
        if (_renderers == null || _saved == null) return;
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] != null && _saved[i] != null)
                _renderers[i].sharedMaterials = _saved[i];
            if (_temps != null && i < _temps.Length && _temps[i] != null)
                Destroy(_temps[i]);
        }
        _renderers = null;
        _saved = null;
        _temps = null;
    }

    void OnDisable()
    {
        Restore();
    }
}
