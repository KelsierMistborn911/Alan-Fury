using UnityEngine;

/// <summary>
/// Занятость волка вне боя: патруль / охотник / свита / свободный.
/// Куда идти говорит PackManager. Шаг — через WerewolfHuntPatrol или Locomotion.
/// Боевые роли не трогает.
/// </summary>
[RequireComponent(typeof(WerewolfLocomotion))]
public class WolfDuty : MonoBehaviour
{
    public WerewolfLocomotion locomotion;
    public WerewolfHuntPatrol patrol;
    public NpcPerception perception;

    [Tooltip("Картинка вместо модели, пока волк вне зоны. Повесь на префаб и укажи сюда.")]
    public GameObject farSprite;

    Renderer[] _body;

    WerewolfPackManager.Occupation _job = WerewolfPackManager.Occupation.Free;
    int _patrolHex;
    float _nextGoal;
    Vector3 _goal;
    float _restUntil;
    int _scenario;

    public bool live { get; private set; }

    public static bool BlocksMove(Component c)
    {
        if (c == null) return false;
        var d = c.GetComponent<WolfDuty>();
        return d != null && !d.live;
    }

    void Awake()
    {
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (patrol == null) patrol = GetComponent<WerewolfHuntPatrol>();
        if (perception == null) perception = GetComponent<NpcPerception>();
        CacheBody();
    }

    void CacheBody()
    {
        var all = GetComponentsInChildren<Renderer>(true);
        int n = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (farSprite != null && all[i].transform.IsChildOf(farSprite.transform)) continue;
            if (farSprite != null && all[i].gameObject == farSprite) continue;
            n++;
        }
        _body = new Renderer[n];
        int k = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (farSprite != null && all[i].transform.IsChildOf(farSprite.transform)) continue;
            if (farSprite != null && all[i].gameObject == farSprite) continue;
            _body[k++] = all[i];
        }
    }

    void ApplyLook(bool inZone)
    {
        if (farSprite != null) farSprite.SetActive(!inZone);
        if (_body == null) CacheBody();
        for (int i = 0; i < _body.Length; i++)
            if (_body[i] != null) _body[i].enabled = inZone;
    }

    void OnEnable()
    {
        var cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        live = false;
        SetSenses(false);
        ApplyLook(false);
    }

    public void SetLive(bool on)
    {
        if (live == on) return;
        live = on;
        var cc = GetComponent<CharacterController>();
        if (on)
        {
            var stream = FindObjectOfType<LocationStream>();
            if (stream != null)
            {
                var p = transform.position;
                p.y = stream.HeightAtWorld(p.x, p.z) + 1.2f;
                if (cc != null) cc.enabled = false;
                transform.position = p;
            }
        }
        if (cc != null) cc.enabled = on;
        SetSenses(on);
        ApplyLook(on);
    }

    void SetSenses(bool on)
    {
        if (perception != null) perception.enabled = on;
        var notice = GetComponent<WerewolfNoticeIndicator>();
        if (notice != null) notice.enabled = on;
    }

    int _patrolIndex;
    bool _onPatrol;

    public void BeginPatrol(int slot)
    {
        _patrolIndex = slot;
        _onPatrol = true;
        _goal = Vector3.zero;
    }

    /// <summary>Далеко: рывок координат. Патруль — по кольцу точек.</summary>
    public void AdvanceAbstract()
    {
        if (GetComponent<WerewolfPackManager>() != null) return;
        var pack = WerewolfPackManager.Instance;
        if (pack == null) return;
        pack.GetOccupation(transform, out _job, out _patrolHex);

        if (_job == WerewolfPackManager.Occupation.Patrol)
        {
            if (!_onPatrol)
            {
                _patrolIndex = _patrolHex;
                _onPatrol = true;
                var start = pack.PatrolPoint(_patrolIndex);
                start.y = transform.position.y;
                transform.position = start;
            }
            _goal = pack.PatrolPoint(_patrolIndex);
        }
        else if (_goal == Vector3.zero || Time.time >= _nextGoal)
            PickGoal(pack);

        Vector3 to = _goal - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist < 6f)
        {
            if (_job == WerewolfPackManager.Occupation.Patrol)
                _patrolIndex = pack.NextPatrol(_patrolIndex);
            else
                PickGoal(pack);
            return;
        }
        float step = Mathf.Min(dist, Random.Range(4f, 8f));
        var pos = transform.position + to.normalized * step;
        pos.y = transform.position.y;
        transform.position = pos;
    }

    void Update()
    {
        if (!live)
        {
            DrawFarCircle();
            return;
        }
        if (GetComponent<WerewolfPackManager>() != null) return;
        var pack = WerewolfPackManager.Instance;
        if (pack == null || locomotion == null) return;
        if (pack.Phase == WerewolfPackManager.PackPhase.Hunt) return;
        if (perception != null && perception.IsLocked) return;

        pack.GetOccupation(transform, out _job, out _patrolHex);

        if (Time.time < _restUntil)
            return;

        if (Time.time >= _nextGoal)
            PickGoal(pack);

        float speed = _job == WerewolfPackManager.Occupation.Patrol ? 2.2f : 3.1f;
        if (patrol != null && patrol.enabled && _job == WerewolfPackManager.Occupation.Patrol)
            return;
        locomotion.MoveTo(_goal, speed, Time.deltaTime);
    }

    void DrawFarCircle()
    {
        float r = perception != null && perception.sightRange > 1f ? perception.sightRange : 24f;
        Vector3 c = transform.position + Vector3.up * 0.35f;
        const int n = 28;
        var col = new Color(0.35f, 0.9f, 0.45f);
        Vector3 prev = c + new Vector3(r, 0f, 0f);
        for (int i = 1; i <= n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            Vector3 next = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            Debug.DrawLine(prev, next, col);
            prev = next;
        }
    }

    void PickGoal(WerewolfPackManager pack)
    {
        _nextGoal = Time.time + Random.Range(8f, 16f);
        switch (_job)
        {
            case WerewolfPackManager.Occupation.Patrol:
                if (!_onPatrol) _patrolIndex = _patrolHex;
                _goal = pack.PatrolPoint(_patrolIndex);
                break;
            case WerewolfPackManager.Occupation.Hunter:
                _goal = pack.HunterPoint(transform.position);
                break;
            case WerewolfPackManager.Occupation.Retinue:
                _goal = pack.RetinuePoint(transform);
                break;
            default:
                PickFree(pack);
                break;
        }
    }

    void PickFree(WerewolfPackManager pack)
    {
        _scenario = Random.Range(0, 3);
        if (_scenario == 0)
        {
            _goal = pack.DenPoint() + Random.insideUnitSphere * 18f;
            _goal.y = transform.position.y;
            _restUntil = Time.time + Random.Range(4f, 9f);
        }
        else if (_scenario == 1)
            _goal = pack.BorderPoint();
        else
            _goal = pack.TerritoryPoint();
    }
}
