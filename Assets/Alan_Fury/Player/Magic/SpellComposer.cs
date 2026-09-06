using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 4 = предмет Ђфокусировкаї: 1-е нажатие в правую (меч в ножны),
/// 2-е Ч в левую (щит в ножны, меч обратно). 1/2 возвращают меч/щит.
/// ѕока фокус в руке Ч набор стрелками в эту руку. 3 = голос.
/// </summary>
[DefaultExecutionOrder(-50)]
public class SpellComposer : MonoBehaviour
{
    [Header("¬вод")]
    public KeyCode composeKey = KeyCode.Alpha4;
    public KeyCode cancelKey = KeyCode.Space;

    [Header("ћана")]
    public float baseMana = 15f;
    public float maxInvest = 60f;
    public float wheelStep = 5f;

    public SpellSlots slots;
    public PlayerResources resources;
    public HumanoidCombat combat;
    public PlayerLoadout loadout;
    public SpellController caster;

    public enum FocusHand { None, Right, Left }

    public FocusHand Focus { get; private set; }
    public bool IsComposing => Focus != FocusHand.None;
    public bool HasChannel { get; private set; }
    public SpellChannel Channel { get; private set; }
    public float Invested { get; private set; }
    public IReadOnlyList<SpellSign> Signs => _signs;

    public int Required => HasChannel ? SpellSlots.RequiredSigns(Channel) : 0;
    public bool FormulaComplete => HasChannel && _signs.Count >= Required;

    readonly List<SpellSign> _signs = new List<SpellSign>(8);
    int _openedFrame = -1;
    Transform _crystal;

    void Awake()
    {
        if (slots == null) slots = GetComponent<SpellSlots>();
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (combat == null) combat = GetComponent<HumanoidCombat>();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (caster == null) caster = GetComponent<SpellController>();
        BuildCrystal();
    }

    void LateUpdate()
    {
        if (_crystal == null) return;
        _crystal.gameObject.SetActive(IsComposing);
        if (!IsComposing) return;
        _crystal.position = transform.position + Vector3.up * 2.85f;
        var cam = Camera.main;
        if (cam != null) _crystal.rotation = Quaternion.LookRotation(cam.transform.forward, Vector3.up);
    }

    void Update()
    {
        if (resources != null && resources.IsDead)
        {
            ReleaseFocus();
            return;
        }

        bool tap4 = Input.GetKeyDown(composeKey)
            || Input.GetKeyDown(KeyCode.Alpha4)
            || Input.GetKeyDown(KeyCode.Keypad4);

        if (tap4)
        {
            CycleFocus();
            return;
        }

        if (Input.GetKeyDown(cancelKey) && IsComposing)
        {
            ClearSigns();
            return;
        }

        if (IsComposing)
            TickCompose();
    }

    public bool HoldsFocus(SpellChannel ch)
    {
        if (ch == SpellChannel.Hand1) return Focus == FocusHand.Right;
        if (ch == SpellChannel.Hand2) return Focus == FocusHand.Left;
        return false;
    }

    public void ReleaseFocus()
    {
        if (Focus == FocusHand.None) return;
        if (caster != null)
        {
            if (Focus == FocusHand.Right) caster.TryStow(SpellChannel.Hand1);
            if (Focus == FocusHand.Left) caster.TryStow(SpellChannel.Hand2);
        }
        SetLoadoutFlag("rightHandMagic", false);
        SetLoadoutFlag("leftHandMagic", false);
        Focus = FocusHand.None;
        HasChannel = false;
        ClearSigns();
    }

    void CycleFocus()
    {
        if (Focus == FocusHand.Right) EquipLeft();
        else EquipRight();
    }

    void EquipRight()
    {
        if (Focus == FocusHand.Left && combat != null && !combat.IsShieldArmed)
            combat.DrawShield();
        Focus = FocusHand.Right;
        BindHandChannel(SpellChannel.Hand1);
        SetLoadoutFlag("rightHandMagic", true);
        SetLoadoutFlag("leftHandMagic", false);
        if (combat != null && combat.IsArmed) combat.SheathSword();
    }

    void EquipLeft()
    {
        Focus = FocusHand.Left;
        BindHandChannel(SpellChannel.Hand2);
        SetLoadoutFlag("rightHandMagic", false);
        SetLoadoutFlag("leftHandMagic", true);
        if (combat != null)
        {
            if (!combat.IsArmed) combat.DrawSword();
            if (combat.IsShieldArmed) combat.SheathShield();
        }
    }

    void BindHandChannel(SpellChannel ch)
    {
        HasChannel = true;
        Channel = ch;
        ClearSigns();
        _openedFrame = Time.frameCount;
    }

    void ClearSigns()
    {
        _signs.Clear();
        Invested = baseMana;
        if (resources != null)
            Invested = Mathf.Min(Invested, resources.CurrentMana);
    }

    void Abort()
    {
        ClearSigns();
    }

    void TickCompose()
    {
        if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
        {
            Pick(SpellChannel.Voice);
            return;
        }

        if (!HasChannel)
            return;

        if (!FormulaComplete)
        {
            if (TryReadSign(out SpellSign sign))
            {
                if (!AcceptSign(sign)) return;
                _signs.Add(sign);
                if (FormulaComplete) Commit();
            }
        }
        else
        {
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                float cap = maxInvest;
                if (resources != null) cap = Mathf.Min(cap, resources.CurrentMana);
                Invested = Mathf.Clamp(Invested + Mathf.Sign(wheel) * wheelStep, 0f, cap);
            }
        }
    }

    void Pick(SpellChannel ch)
    {
        if (slots != null && slots.HasBinding(ch))
        {
            Channel = ch;
            HasChannel = true;
            _signs.Clear();
            Commit();
            return;
        }

        HasChannel = true;
        Channel = ch;
        _signs.Clear();
        Invested = baseMana;
        if (resources != null)
            Invested = Mathf.Min(Invested, resources.CurrentMana);
    }

    void Commit()
    {
        if (!HasChannel)
        {
            Abort();
            return;
        }

        if (_signs.Count == 0)
        {
            if (slots != null && slots.HasBinding(Channel))
                ApplyDraw(Channel);
            Abort();
            return;
        }

        if (!FormulaComplete)
        {
            Abort();
            return;
        }

        var arr = _signs.ToArray();
        if (slots != null) slots.Bind(Channel, arr, Invested);
        ApplyDraw(Channel);
        if (Focus == FocusHand.Right) BindHandChannel(SpellChannel.Hand1);
        else if (Focus == FocusHand.Left) BindHandChannel(SpellChannel.Hand2);
        else ClearSigns();
    }

    void ApplyDraw(SpellChannel ch)
    {
        if (slots == null) return;
        slots.TryRecall(ch);
        if (caster != null) caster.BeginUse(ch);
        if (loadout == null) return;

        if (ch == SpellChannel.Hand1)
        {
            SetLoadoutFlag("rightHandMagic", true);
            if (combat != null && combat.IsArmed) combat.SheathSword();
        }
        else if (ch == SpellChannel.Hand2)
        {
            SetLoadoutFlag("leftHandMagic", true);
            if (combat != null && combat.IsShieldArmed) combat.SheathShield();
        }
    }

    bool AcceptSign(SpellSign sign)
    {
        if (_signs.Count < 3) return true;
        var extra = SpellSlots.ExtraSigns(Channel);
        int i = _signs.Count - 3;
        if (i < 0 || i >= extra.Length) return false;
        return extra[i] == sign;
    }

    void BuildCrystal()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "SpellModeCrystal";
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 2.85f, 0f);
        go.transform.localScale = new Vector3(0.22f, 0.38f, 1f);
        var r = go.GetComponent<MeshRenderer>();
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh != null)
        {
            var mat = new Material(sh);
            mat.color = new Color(0.25f, 0.55f, 1f, 1f);
            r.material = mat;
        }
        else
            r.material.color = new Color(0.25f, 0.55f, 1f, 1f);
        go.SetActive(false);
        _crystal = go.transform;
    }

    void SetLoadoutFlag(string field, bool value)
    {
        if (loadout == null) return;
        var f = typeof(PlayerLoadout).GetField(field);
        if (f != null) f.SetValue(loadout, value);
    }

    static bool TryReadSign(out SpellSign sign)
    {
        if (Input.GetKeyDown(KeyCode.UpArrow)) { sign = SpellSign.Up; return true; }
        if (Input.GetKeyDown(KeyCode.LeftArrow)) { sign = SpellSign.Left; return true; }
        if (Input.GetKeyDown(KeyCode.DownArrow)) { sign = SpellSign.Down; return true; }
        if (Input.GetKeyDown(KeyCode.RightArrow)) { sign = SpellSign.Right; return true; }
        sign = SpellSign.Up;
        return false;
    }
}
