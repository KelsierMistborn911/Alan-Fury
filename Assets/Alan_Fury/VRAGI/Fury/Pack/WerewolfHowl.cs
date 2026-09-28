using UnityEngine;

/// <summary>
/// Вой. На объекте Pack Manager — банк клипов и настройки на всю стаю.
/// На волке — 3D-источник, клипы читает с банка.
/// </summary>
public class WerewolfHowl : MonoBehaviour
{
    public enum Type
    {
        Contact,
        Rally,
        Lost,
        LinkRequest,
        BeaterPing,
        Assemble,
        OrderAttack,
        OrderPursue,
        OrderRetreat,
        OrderRejoin,
        Morale,
        HeraldSpeak
    }

    [HideInInspector] public int packRank;

    [Header("Вой (на объекте Pack Manager — на всю стаю)")]
    public AudioClip[] clips;
    [Tooltip("Дальность воя (м). Contact/пересказ и приказы альфы. Крутить в процессе.")]
    public float howlRange = 600f;
    public float answerDelay = 0.35f;
    public float howlCooldown = 8f;
    public float beaterEveryMeters = 100f;
    [Tooltip("Глашатай орёт не ближе этого к альфе и к игроку.")]
    public float heraldClearMeters = 70f;

    public int SquadId { get; set; }
    public bool IsSquadController { get; set; } = true;
    public bool ChaseAlive { get; private set; }

    public bool IsBank => GetComponent<WerewolfPackManager>() != null;
    public bool IsHerald => packRank >= 5 && packRank < 10;
    public bool IsAlpha => packRank >= 10;

    WerewolfHowl Bank
    {
        get
        {
            var pack = WerewolfPackManager.Instance;
            if (pack == null) return this;
            var bank = pack.GetComponent<WerewolfHowl>();
            return bank != null ? bank : this;
        }
    }

    NpcPerception _perc;
    WerewolfBrain _brain;
    AudioSource _src;
    float _cd;
    float _answerAt = -1f;
    Type _answerType;
    Vector3 _answerPos;
    bool _wasLocked;
    float _chaseTravel;
    Vector3 _lastPos;
    float _heraldUntil;
    Type _heraldType;
    bool _heraldPending;
    float _expectReplyUntil;
    bool _gotReply;

    void Awake()
    {
        if (IsBank) return;
        _perc = GetComponent<NpcPerception>();
        _brain = GetComponent<WerewolfBrain>();
        _src = GetComponent<AudioSource>();
        if (_src == null) _src = gameObject.AddComponent<AudioSource>();
        _src.spatialBlend = 1f;
        _src.playOnAwake = false;
        _src.rolloffMode = AudioRolloffMode.Logarithmic;
        _src.minDistance = 8f;
        _src.maxDistance = Bank.howlRange;
        _lastPos = transform.position;
    }

    void OnEnable()
    {
        if (IsBank) return;
        var pack = WerewolfPackManager.Instance;
        if (pack != null) pack.RegisterHowler(this);
    }

    void OnDisable()
    {
        if (IsBank) return;
        var pack = WerewolfPackManager.Instance;
        if (pack != null) pack.UnregisterHowler(this);
    }

    void Update()
    {
        if (IsBank) return;

        float dt = Time.deltaTime;
        var cfg = Bank;
        if (_cd > 0f) _cd -= dt;

        bool locked = _perc != null && _perc.IsLocked;
        if (locked && !_wasLocked)
            TryEmit(Type.Contact, transform.position, force: true);
        _wasLocked = locked;

        ChaseAlive = locked || (_perc != null && _perc.HasCue);
        TickBeater(cfg);
        TickAnswer();
        TickHerald(dt, cfg);
        if (_expectReplyUntil > 0f && Time.time >= _expectReplyUntil)
        {
            _expectReplyUntil = 0f;
            if (!_gotReply)
                TryEmit(Type.LinkRequest, transform.position, force: true);
        }
    }

    void TickBeater(WerewolfHowl cfg)
    {
        if (!ChaseAlive || (_perc != null && _perc.SeesPlayer))
        {
            _chaseTravel = 0f;
            _lastPos = transform.position;
            return;
        }

        _chaseTravel += Flat(transform.position, _lastPos);
        _lastPos = transform.position;
        if (_chaseTravel < cfg.beaterEveryMeters) return;
        _chaseTravel = 0f;
        TryEmit(Type.BeaterPing, transform.position, force: false);
    }

    void TickAnswer()
    {
        if (_answerAt < 0f) return;
        if (Time.time < _answerAt) return;
        _answerAt = -1f;
        if (IsMute()) return;
        TryEmit(_answerType, _answerPos, force: false);
    }

    void TickHerald(float dt, WerewolfHowl cfg)
    {
        if (!_heraldPending || _brain == null) return;
        var pack = WerewolfPackManager.Instance;
        if (pack == null) { _heraldPending = false; return; }

        Transform alpha = pack.alphaTransform;
        Transform player = pack.player;
        float da = alpha != null ? Flat(transform.position, alpha.position) : 999f;
        float dp = player != null ? Flat(transform.position, player.position) : 999f;
        if (da >= cfg.heraldClearMeters && dp >= cfg.heraldClearMeters)
        {
            TryEmit(_heraldType, transform.position, force: true);
            _heraldPending = false;
            _brain.EndHerald();
            return;
        }

        _heraldUntil -= dt;
        if (_heraldUntil <= 0f)
        {
            TryEmit(_heraldType, transform.position, force: true);
            _heraldPending = false;
            _brain.EndHerald();
        }
    }

    public void BeginHerald(Type type, float giveUpSeconds = 20f)
    {
        _heraldType = type == Type.HeraldSpeak ? Type.Assemble : type;
        _heraldPending = true;
        _heraldUntil = giveUpSeconds;
        if (_brain != null) _brain.BeginHerald();
    }

    public bool TryEmit(Type type, Vector3 at, bool force)
    {
        if (IsBank) return false;
        var cfg = Bank;
        if (!force && _cd > 0f) return false;
        if (!force && IsMute() && type != Type.Contact && type != Type.BeaterPing)
            return false;

        _cd = cfg.howlCooldown;
        if (type == Type.Contact || type == Type.LinkRequest)
        {
            _expectReplyUntil = Time.time + 6f;
            _gotReply = false;
        }
        PlayClip(cfg);
        EnemySoundBus.Emit(at, EnemySoundBus.Kind.Howl, 1f);

        var pack = WerewolfPackManager.Instance;
        if (pack != null) pack.OnHowl(this, type, at);
        return true;
    }

    public void Hear(Type type, Vector3 at, WerewolfHowl source)
    {
        if (IsBank || source == this) return;
        _gotReply = true;
        if (IsMute()) return;
        if (_answerAt > 0f) return;

        float delay = Bank.answerDelay;
        if (type == Type.LinkRequest)
        {
            _answerType = Type.Rally;
            _answerPos = transform.position;
            _answerAt = Time.time + delay;
            return;
        }

        if (type == Type.Contact || type == Type.Rally || type == Type.Lost || type == Type.BeaterPing)
        {
            _answerType = Type.Rally;
            _answerPos = transform.position;
            _answerAt = Time.time + delay;
        }
    }

    public bool IsMute()
    {
        return _brain != null && _brain.CurrentMode == WerewolfBrain.Mode.Stalk;
    }

    void PlayClip(WerewolfHowl cfg)
    {
        if (_src == null) return;
        AudioClip[] bank = cfg != null ? cfg.clips : clips;
        float range = cfg != null ? cfg.howlRange : howlRange;
        if (bank != null && bank.Length > 0)
        {
            var c = bank[Random.Range(0, bank.Length)];
            if (c != null)
            {
                _src.clip = c;
                _src.pitch = Random.Range(0.92f, 1.08f);
                _src.maxDistance = range;
                _src.Play();
                return;
            }
        }
        _src.pitch = Random.Range(0.85f, 1.1f);
        _src.Play();
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
