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
    }

    void Update()
    {
        if (resources != null && resources.IsDead)
        {
            CancelAim();
            return;
        }
        if (composer != null && composer.IsComposing)
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
            {
                if (_preview == null || _preview.Length == 0)
                    UpdatePreview();
                if (_preview != null && _preview.Length > 0)
                    CastFromSlot(AimChannel);
            }
            return;
        }

        if (slots == null) return;

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
        SyncMarks();
    }

    void SyncMarks()
    {
        int need = (IsAiming && _preview != null) ? _preview.Length : 0;
        while (_marks.Count < need) _marks.Add(MakeMark());
        for (int i = 0; i < _marks.Count; i++)
        {
            bool on = i < need && mapGrid != null;
            _marks[i].gameObject.SetActive(on);
            if (!on) continue;
            Vector3 p = mapGrid.CellCenterWorld(_preview[i].x, _preview[i].y);
            p.y += 0.07f;
            float ts = mapGrid.TileSize * 0.9f;
            _marks[i].position = p;
            _marks[i].rotation = Quaternion.Euler(90f, 0f, 0f);
            _marks[i].localScale = new Vector3(ts, ts, 1f);
        }
    }

    Transform MakeMark()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "SpellCellMark";
        Object.Destroy(go.GetComponent<Collider>());
        var r = go.GetComponent<MeshRenderer>();
        EnsureMarkMat();
        if (CellMarkMaterial != null) r.sharedMaterial = CellMarkMaterial;
        go.SetActive(false);
        return go.transform;
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
        if (mapGrid == null || _preview == null || _preview.Length == 0) return;
        var copy = new Vector2Int[_preview.Length];
        System.Array.Copy(_preview, copy, copy.Length);
        LightPillar.Spawn(mapGrid, copy, pillarDuration, invested);
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
        if (mapGrid == null || !mapGrid.IsReady) return;
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
        Object.Destroy(go, life);
    }

    static void EnsureMarkMat()
    {
        if (CellMarkMaterial != null) return;
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) return;
        CellMarkMaterial = new Material(sh);
        CellMarkMaterial.color = new Color(1f, 0.92f, 0.45f, 0.45f);
        if (CellMarkMaterial.HasProperty("_Surface"))
        {
            CellMarkMaterial.SetFloat("_Surface", 1f);
            CellMarkMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            CellMarkMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
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
