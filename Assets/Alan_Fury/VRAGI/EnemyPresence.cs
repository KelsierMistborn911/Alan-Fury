using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Тонкий субъект присутствия. Регистрируется в менеджере. AI не трогает.
/// </summary>
public class EnemyPresence : MonoBehaviour
{
    public enum View { Hidden, Fringe, Visible }

    [Tooltip("Пусто — собрать все Mesh/Skinned на объекте и детях.")]
    public Renderer[] renderers;

    public View Current { get; private set; } = View.Hidden;
    public bool ChaseAlive { get; set; }

    private MaterialPropertyBlock _block;
    private static Material _darkMat;
    private Material[][] _original;
    private bool _cached;
    private View _lastView = (View)(-1);
    private bool _lastOutline;

    void Awake()
    {
        Cache();
    }

    void OnEnable()
    {
        Cache();
        EnemyPresenceManager.Register(this);
    }

    void OnDisable()
    {
        EnemyPresenceManager.Unregister(this);
        Apply(View.Visible, outline: false);
    }

    public void Cache()
    {
        if (_cached) return;
        if (renderers == null || renderers.Length == 0)
            renderers = GetComponentsInChildren<Renderer>(true);
        _original = new Material[renderers.Length][];
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null) continue;
            _original[i] = renderers[i].sharedMaterials;
        }
        _block = new MaterialPropertyBlock();
        _cached = true;
    }

    public void Apply(View view, bool outline)
    {
        Cache();
        if (_lastView == view && _lastOutline == outline) return;
        _lastView = view;
        _lastOutline = outline;
        Current = view;
        bool show = view != View.Hidden;
        bool dark = view == View.Fringe || outline;

        EnsureDarkMat();
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null) continue;
            r.enabled = show;
            if (!show) continue;
            if (dark && _darkMat != null)
            {
                var one = r.sharedMaterials;
                if (one == null || one.Length == 0) r.sharedMaterial = _darkMat;
                else
                {
                    for (int m = 0; m < one.Length; m++) one[m] = _darkMat;
                    r.sharedMaterials = one;
                }
            }
            else if (_original != null && _original[i] != null)
                r.sharedMaterials = _original[i];
        }
    }

    static void EnsureDarkMat()
    {
        if (_darkMat != null) return;
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return;
        _darkMat = new Material(sh);
        _darkMat.name = "EnemyPresenceDark";
        _darkMat.SetFloat("_Surface", 1f);
        _darkMat.SetFloat("_ZWrite", 0f);
        _darkMat.SetOverrideTag("RenderType", "Transparent");
        _darkMat.renderQueue = 3000;
        _darkMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        _darkMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        var c = new Color(0.05f, 0.05f, 0.06f, 0.34f);
        if (_darkMat.HasProperty("_BaseColor")) _darkMat.SetColor("_BaseColor", c);
        if (_darkMat.HasProperty("_Color")) _darkMat.SetColor("_Color", c);
    }
}
