using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Применение записанных формул. Рука: инстант по ЛКМ; столб — 1 включает прицел по клеткам, клик ставит.
/// </summary>
public class SpellController : MonoBehaviour
{
    public SpellSlots slots;
    public SpellComposer composer;
    public PlayerResources resources;
    public PlayerLoadout loadout;
    public HumanoidCombat combat;
    public MapGrid mapGrid;

    [Header("Лечение ↑←↑")]
    public float healBase = 40f;
    public float healPerMana = 0.8f;

    [Header("Столб ↓←→")]
    public int pillarCellRadius = 1;
    public float pillarDuration = 4f;
    public float pillarRange = 18f;

    [Header("Вспышка ↑↑↑")]
    public float flashRadius = 8f;
    public float flashStun = 1.2f;
    public float flashBlind = 2f;
    public float flashFear = 25f;
    public float flashGhostDamage = 8f;

    public bool IsAiming { get; private set; }
    public SpellChannel AimChannel { get; private set; }

    public static Material CellMarkMaterial { get; private set; }

    Camera _cam;
    Animator _anim;
    Vector2Int[] _preview = new Vector2Int[0];
    static Mesh _quad;
    readonly List<Transform> _marks = new List<Transform>();
    Transform _aimGhost;
    LineRenderer _ring;
    Mesh _aimMesh;
    Material _aimMat;
    readonly List<Vector3> _aimVerts = new List<Vector3>(64);
    readonly List<int> _aimTris = new List<int>(96);
    readonly HashSet<Vector2Int> _aimSet = new HashSet<Vector2Int>();

    void Awake()
    {
        if (slots == null) slots = GetComponent<SpellSlots>();
        if (composer == null) composer = GetComponent<SpellComposer>();
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (combat == null) combat = GetComponent<HumanoidCombat>();
        if (mapGrid == null) mapGrid = FindObjectOfType<MapGrid>();
        _cam = Camera.main;
        _anim = GetComponent<Animator>();
        EnsureMarkMat();
        if (_quad == null) _quad = BuildQuad();
    }

    public bool BlocksMelee =>
        (slots != null && slots.IsHandMagicDrawn && (composer == null || !composer.IsComposing)) || IsAiming;

    public void BeginUse(SpellChannel ch)
    {
        if (slots == null || !slots.HasBinding(ch)) return;
        var id = SpellBook.Resolve(slots.Get(ch).signs);
        if (SpellBook.NeedsGroundAim(id))
        {
            IsAiming = true;
            AimChannel = ch;
        }
    }

    public bool TryStow(SpellChannel ch)
    {
        if (slots == null || !slots.HasBinding(ch)) return false;
        if (IsAiming && AimChannel == ch) CancelAim();
        if (!SpellSlots.IsHand(ch)) return true;
        if (!slots.IsDrawn(ch)) return false;
        slots.TrySuspend(ch);
        return true;
    }

    public bool TryToggleAim(SpellChannel ch)
    {
        return TryStow(ch);
    }

    public void CancelAim()
    {
        IsAiming = false;
        _preview = new Vector2Int[0];
        if (_aimGhost != null) _aimGhost.gameObject.SetActive(false);
    }

    void TryAutoAim(SpellChannel ch)
    {
        if (IsAiming || slots == null || !slots.IsDrawn(ch)) return;
        var id = SpellBook.Resolve(slots.Get(ch).signs);
        if (SpellBook.NeedsGroundAim(id))
            BeginUse(ch);
    }

    void Update()
    {
        if (resources != null && resources.IsDead)
        {
            CancelAim();
            return;
        }
        // Набор знаков — не кастуем. Фокус в руке после записи формулы — кастуем.
        if (composer != null && composer.IsComposing
            && composer.Signs != null && composer.Signs.Count > 0
            && !composer.FormulaComplete)
        {
            CancelAim();
            return;
        }

        if (IsAiming)
        {
            if (slots == null || !slots.IsDrawn(AimChannel))
            {
                CancelAim();
                return;
            }
            UpdatePreview();
            if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Space))
            {
                TryStow(AimChannel);
                return;
            }
            if (Input.GetMouseButtonDown(0))
                CastFromSlot(AimChannel);
            return;
        }

        if (slots == null) return;
        TryAutoAim(SpellChannel.Hand1);
        TryAutoAim(SpellChannel.Hand2);

        if (slots.IsDrawn(SpellChannel.Hand1) || slots.IsDrawn(SpellChannel.Hand2))
        {
            var ch = slots.IsDrawn(SpellChannel.Hand1) ? SpellChannel.Hand1 : SpellChannel.Hand2;
            var id = SpellBook.Resolve(slots.Get(ch).signs);
            if (!SpellBook.NeedsGroundAim(id) && Input.GetMouseButtonDown(0))
                CastFromSlot(ch);
        }

        if (slots.HasBinding(SpellChannel.Voice) &&
            (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)))
        {
            var id = SpellBook.Resolve(slots.Get(SpellChannel.Voice).signs);
            if (SpellBook.NeedsGroundAim(id))
                BeginUse(SpellChannel.Voice);
            else
                CastFromSlot(SpellChannel.Voice);
        }

    }

    void LateUpdate()
    {
        HideOldAimActors();
        DrawAimFrames();
    }

    void HideOldAimActors()
    {
        for (int i = 0; i < _marks.Count; i++)
            if (_marks[i] != null) _marks[i].gameObject.SetActive(false);
        if (_aimGhost != null) _aimGhost.gameObject.SetActive(false);
        if (_ring != null) _ring.enabled = false;
    }

    void DrawAimFrames()
    {
        if (!IsAiming || mapGrid == null || _preview == null || _preview.Length == 0)
            return;
        EnsureAimMat();
        if (_aimMesh == null || _aimMat == null) return;

        _aimSet.Clear();
        for (int i = 0; i < _preview.Length; i++)
            _aimSet.Add(_preview[i]);

        _aimVerts.Clear();
        _aimTris.Clear();
        float half = mapGrid.TileSize * 0.5f;
        float w = Mathf.Clamp(0.05f, 0.01f, half * 0.35f);
        var col = new Color(0.80f, 0.66f, 0.30f, 0.32f);
        _aimMat.SetColor("_Color", col);
        if (_aimMat.HasProperty("_BaseColor")) _aimMat.SetColor("_BaseColor", col);
        if (_aimMat.HasProperty("_Color")) _aimMat.SetColor("_Color", col);

        for (int i = 0; i < _preview.Length; i++)
        {
            var cell = _preview[i];
            Vector3 p = mapGrid.CellCenterWorld(cell.x, cell.y);
            float y = p.y + 0.06f;
            float x0 = p.x - half, x1 = p.x + half;
            float z0 = p.z - half, z1 = p.z + half;
            if (!_aimSet.Contains(new Vector2Int(cell.x, cell.y - 1)))
                AddAimQuad(x0, z0, x1, z0 + w, y);
            if (!_aimSet.Contains(new Vector2Int(cell.x, cell.y + 1)))
                AddAimQuad(x0, z1 - w, x1, z1, y);
            if (!_aimSet.Contains(new Vector2Int(cell.x - 1, cell.y)))
                AddAimQuad(x0, z0 + w, x0 + w, z1 - w, y);
            if (!_aimSet.Contains(new Vector2Int(cell.x + 1, cell.y)))
                AddAimQuad(x1 - w, z0 + w, x1, z1 - w, y);
        }

        _aimMesh.Clear();
        if (_aimVerts.Count < 3) return;
        _aimMesh.SetVertices(_aimVerts);
        _aimMesh.SetTriangles(_aimTris, 0, false);
        _aimMesh.RecalculateBounds();
        Graphics.DrawMesh(_aimMesh, Matrix4x4.identity, _aimMat, 0);
    }

    void AddAimQuad(float x0, float z0, float x1, float z1, float y)
    {
        int b = _aimVerts.Count;
        _aimVerts.Add(new Vector3(x0, y, z0));
        _aimVerts.Add(new Vector3(x1, y, z0));
        _aimVerts.Add(new Vector3(x1, y, z1));
        _aimVerts.Add(new Vector3(x0, y, z1));
        _aimTris.Add(b + 0); _aimTris.Add(b + 2); _aimTris.Add(b + 1);
        _aimTris.Add(b + 0); _aimTris.Add(b + 3); _aimTris.Add(b + 2);
    }

    void EnsureAimMat()
    {
        if (_aimMesh == null)
        {
            _aimMesh = new Mesh { name = "PillarAimOverlay" };
            _aimMesh.MarkDynamic();
        }
        if (_aimMat != null) return;
        var sh = Shader.Find("Hidden/VisionCellOverlay");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) return;
        _aimMat = new Material(sh);
        _aimMat.renderQueue = 3000;
    }

    void CastFromSlot(SpellChannel ch)
    {
        if (slots == null) return;
        var bind = slots.Get(ch);
        if (!bind.occupied) return;
        var id = SpellBook.Resolve(bind.signs);
        if (id == SpellId.None) return;

        float cost = bind.manaInvested > 0f ? bind.manaInvested : 15f;
        if (resources != null && !resources.HasMana(cost)) return;
        if (resources != null) resources.SpendMana(cost);

        switch (id)
        {
            case SpellId.SelfHealBurst:
                CastHeal(cost);
                break;
            case SpellId.LightPillar:
                CastPillar(cost);
                break;
            case SpellId.BlindFlash:
                CastFlash(cost);
                break;
        }
        FireAnim("SpellCast");
        if (id == SpellId.SelfHealBurst) FireAnim("Heal");
        CancelAim();
    }

    void CastHeal(float invested)
    {
        float amount = healBase + invested * healPerMana;
        if (resources != null) resources.Heal(amount);
        DamagePopup.Spawn(transform.position + Vector3.up * 2.1f, amount, new Color(0.4f, 1f, 0.45f), " хил");
        BurstLight(transform.position + Vector3.up * 1.2f, new Color(0.7f, 1f, 0.75f), 3.2f, 10f, 0.45f);
    }

    void CastPillar(float invested)
    {
        if (mapGrid == null) mapGrid = FindObjectOfType<MapGrid>();
        if (_preview == null || _preview.Length == 0)
            UpdatePreview();
        if (_preview != null && _preview.Length > 0 && mapGrid != null)
        {
            var copy = new Vector2Int[_preview.Length];
            System.Array.Copy(_preview, copy, copy.Length);
            LightPillar.Spawn(mapGrid, copy, pillarDuration, invested);
            return;
        }
        if (!MouseGround(out Vector3 hit)) return;
        var p = LightPillar.Spawn(mapGrid, new[] { Vector2Int.zero }, pillarDuration, invested);
        p.transform.position = hit;
    }

    void CastFlash(float invested)
    {
        BurstLight(transform.position + Vector3.up * 1.4f, new Color(1f, 0.95f, 0.7f), 8f, 16f, 0.25f);
        float r = flashRadius + invested * 0.04f;
        var cols = Physics.OverlapSphere(transform.position, r);
        for (int i = 0; i < cols.Length; i++)
        {
            var root = cols[i].GetComponentInParent<Transform>();
            if (root == null) continue;
            if (cols[i].GetComponentInParent<PlayerResources>() != null) continue;

            var wolf = cols[i].GetComponentInParent<WerewolfStats>();
            var ghost = cols[i].GetComponentInParent<GhostStats>();
            var skel = cols[i].GetComponentInParent<SkeletonBrain>();
            if (wolf == null && ghost == null && skel == null) continue;

            var cc = CrowdControl.On(wolf != null ? (Component)wolf : ghost != null ? ghost : skel);
            if (cc != null)
            {
                cc.Blind(flashBlind);
                cc.Stun(flashStun);
            }
            if (wolf != null) wolf.AddFear(flashFear);
            if (ghost != null && ghost.IsAlive)
                ghost.TakeDamage(flashGhostDamage + invested * 0.2f, transform.position);
        }
    }

    void UpdatePreview()
    {
        _preview = new Vector2Int[0];
        if (mapGrid == null) mapGrid = FindObjectOfType<MapGrid>();
        if (mapGrid == null) return;
        if (!MouseGround(out Vector3 hit)) return;
        if ((hit - transform.position).sqrMagnitude > pillarRange * pillarRange) return;
        mapGrid.WorldToCell(hit, out int cx, out int cz);
        int rad = Mathf.Max(0, pillarCellRadius);
        int n = (rad * 2 + 1) * (rad * 2 + 1);
        var buf = new Vector2Int[n];
        int w = 0;
        for (int z = cz - rad; z <= cz + rad; z++)
            for (int x = cx - rad; x <= cx + rad; x++)
            {
                if (!mapGrid.InBounds(x, z)) continue;
                buf[w++] = new Vector2Int(x, z);
            }
        _preview = new Vector2Int[w];
        System.Array.Copy(buf, _preview, w);
    }

    bool MouseGround(out Vector3 hit)
    {
        hit = transform.position;
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return false;
        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        var hits = Physics.RaycastAll(ray, 80f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].transform == transform || hits[i].transform.IsChildOf(transform))
                continue;
            if (hits[i].distance < best)
            {
                best = hits[i].distance;
                hit = hits[i].point;
                found = true;
            }
        }
        if (found) return true;
        var plane = new Plane(Vector3.up, new Vector3(0f, transform.position.y, 0f));
        if (!plane.Raycast(ray, out float dist)) return false;
        hit = ray.GetPoint(dist);
        return true;
    }

    void FireAnim(string name)
    {
        if (combat != null) combat.FireSpellTrigger(name);
        else if (_anim != null) _anim.SetTrigger(name);
    }

    static void BurstLight(Vector3 pos, Color color, float intensity, float range, float life)
    {
        var go = new GameObject("SpellBurst");
        go.transform.position = pos;
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = color;
        l.intensity = intensity;
        l.range = range;
        l.shadows = LightShadows.None;
        var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.Destroy(ball.GetComponent<Collider>());
        ball.transform.SetParent(go.transform, false);
        ball.transform.localScale = Vector3.one * Mathf.Max(1.2f, range * 0.12f);
        var r = ball.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        if (r.material != null)
        {
            r.material.color = color;
            if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", color);
        }
        Object.Destroy(go, life);
    }

    static void EnsureMarkMat()
    {
        if (CellMarkMaterial != null) return;
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) return;
        CellMarkMaterial = new Material(sh);
        var c = new Color(1f, 0.84f, 0.22f, 0.8f);
        CellMarkMaterial.color = c;
        if (CellMarkMaterial.HasProperty("_BaseColor"))
            CellMarkMaterial.SetColor("_BaseColor", c);
        if (CellMarkMaterial.HasProperty("_Surface"))
        {
            CellMarkMaterial.SetFloat("_Surface", 1f);
            CellMarkMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            CellMarkMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            CellMarkMaterial.SetInt("_ZWrite", 0);
            CellMarkMaterial.renderQueue = 3000;
        }
    }

    static Mesh BuildQuad()
    {
        var m = new Mesh();
        m.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f)
        };
        m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        m.RecalculateBounds();
        return m;
    }
}
