using UnityEngine;

public enum SpellChannel
{
    Hand1 = 0,
    Hand2 = 1,
    Voice = 2,
    Mind = 3
}

public enum SpellSign
{
    Up = 0,
    Left = 1,
    Down = 2,
    Right = 3
}

public enum SpellSlotState
{
    Empty = 0,
    Suspended = 1,
    Drawn = 2,
    Ready = 3
}

[System.Serializable]
public struct SpellBinding
{
    public bool occupied;
    public SpellChannel channel;
    public SpellSign[] signs;
    public float manaInvested;

    public int SignCount => signs != null ? signs.Length : 0;

    public string SignsText
    {
        get
        {
            if (signs == null || signs.Length == 0) return "—";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < signs.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(SpellSlots.SignGlyph(signs[i]));
            }
            return sb.ToString();
        }
    }

    public string ShortName => SpellSlots.NameFromSigns(signs);
}

public class SpellSlots : MonoBehaviour
{
    public const int Count = 4;

    [SerializeField] SpellBinding[] _bindings = new SpellBinding[Count];
    [SerializeField] SpellSlotState[] _states = new SpellSlotState[Count];

    public SpellBinding Get(SpellChannel ch) => _bindings[(int)ch];
    public SpellSlotState State(SpellChannel ch) => _states[(int)ch];
    public bool IsEmpty(SpellChannel ch) => _states[(int)ch] == SpellSlotState.Empty;
    public bool IsDrawn(SpellChannel ch) => _states[(int)ch] == SpellSlotState.Drawn;
    public bool HasBinding(SpellChannel ch) => _bindings[(int)ch].occupied;

    public bool IsHandMagicDrawn =>
        _states[(int)SpellChannel.Hand1] == SpellSlotState.Drawn
        || _states[(int)SpellChannel.Hand2] == SpellSlotState.Drawn;

    public void Bind(SpellChannel ch, SpellSign[] signs, float mana)
    {
        int i = (int)ch;
        var copy = new SpellSign[signs != null ? signs.Length : 0];
        if (signs != null) System.Array.Copy(signs, copy, copy.Length);
        _bindings[i] = new SpellBinding
        {
            occupied = copy.Length > 0,
            channel = ch,
            signs = copy,
            manaInvested = Mathf.Max(0f, mana)
        };
        if (!_bindings[i].occupied)
        {
            _states[i] = SpellSlotState.Empty;
            return;
        }
        _states[i] = IsHand(ch) ? SpellSlotState.Drawn : SpellSlotState.Ready;
    }

    public bool TryRecall(SpellChannel ch)
    {
        int i = (int)ch;
        if (!_bindings[i].occupied) return false;
        _states[i] = IsHand(ch) ? SpellSlotState.Drawn : SpellSlotState.Ready;
        return true;
    }

    public bool TrySuspend(SpellChannel ch)
    {
        int i = (int)ch;
        if (!_bindings[i].occupied) return false;
        if (!IsHand(ch)) return false;
        if (_states[i] != SpellSlotState.Drawn) return false;
        _states[i] = SpellSlotState.Suspended;
        return true;
    }

    public void Clear(SpellChannel ch)
    {
        int i = (int)ch;
        _bindings[i] = default;
        _states[i] = SpellSlotState.Empty;
    }

    public static bool IsHand(SpellChannel ch) =>
        ch == SpellChannel.Hand1 || ch == SpellChannel.Hand2;

    public static int Tax(SpellChannel ch)
    {
        switch (ch)
        {
            case SpellChannel.Voice: return 3;
            case SpellChannel.Mind: return 4;
            default: return 0;
        }
    }

    public static int RequiredSigns(SpellChannel ch) => 3 + Tax(ch);

    public static SpellSign[] ExtraSigns(SpellChannel ch)
    {
        switch (ch)
        {
            case SpellChannel.Voice:
                return new[] { SpellSign.Down, SpellSign.Up, SpellSign.Down };
            case SpellChannel.Mind:
                return new[] { SpellSign.Left, SpellSign.Right, SpellSign.Left, SpellSign.Right };
            default:
                return new SpellSign[0];
        }
    }

    public static string ExtraText(SpellChannel ch)
    {
        var e = ExtraSigns(ch);
        if (e.Length == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < e.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(SignGlyph(e[i]));
        }
        return sb.ToString();
    }

    public static string ExtraRemaining(SpellChannel ch, int signed)
    {
        var e = ExtraSigns(ch);
        if (e.Length == 0) return "";
        int done = Mathf.Clamp(signed - 3, 0, e.Length);
        if (done >= e.Length) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = done; i < e.Length; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(SignGlyph(e[i]));
        }
        return sb.ToString();
    }

    public static string ChannelLabel(SpellChannel ch)
    {
        switch (ch)
        {
            case SpellChannel.Hand1: return "рука 1";
            case SpellChannel.Hand2: return "рука 2";
            case SpellChannel.Voice: return "голос";
            case SpellChannel.Mind: return "разум";
            default: return "?";
        }
    }

    public static string ChannelKey(SpellChannel ch)
    {
        switch (ch)
        {
            case SpellChannel.Hand1: return "1";
            case SpellChannel.Hand2: return "2";
            case SpellChannel.Voice: return "3";
            case SpellChannel.Mind: return "4";
            default: return "?";
        }
    }

    public static string SignGlyph(SpellSign s)
    {
        switch (s)
        {
            case SpellSign.Up: return "↑";
            case SpellSign.Left: return "←";
            case SpellSign.Down: return "↓";
            case SpellSign.Right: return "→";
            default: return "?";
        }
    }

    public static string LayerWord(int layer, SpellSign s)
    {
        if (layer == 0)
        {
            switch (s)
            {
                case SpellSign.Up: return "себе";
                case SpellSign.Left: return "цель";
                case SpellSign.Down: return "область";
                case SpellSign.Right: return "аура";
            }
        }
        else if (layer == 1)
        {
            switch (s)
            {
                case SpellSign.Up: return "контроль";
                case SpellSign.Left: return "сияние";
                case SpellSign.Down: return "оплот";
                case SpellSign.Right: return "благодать";
            }
        }
        else if (layer == 2)
        {
            switch (s)
            {
                case SpellSign.Up: return "вспышка";
                case SpellSign.Left: return "узко";
                case SpellSign.Down: return "у ног";
                case SpellSign.Right: return "широко";
            }
        }
        return SignGlyph(s);
    }

    public static string NameFromSigns(SpellSign[] signs)
    {
        if (signs == null || signs.Length == 0) return "—";
        int n = Mathf.Min(3, signs.Length);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(" / ");
            sb.Append(LayerWord(i, signs[i]));
        }
        return sb.ToString();
    }

    public static string StateLabel(SpellSlotState st)
    {
        switch (st)
        {
            case SpellSlotState.Empty: return "пусто";
            case SpellSlotState.Suspended: return "висит";
            case SpellSlotState.Drawn: return "в руке";
            case SpellSlotState.Ready: return "готово";
            default: return "?";
        }
    }
}
