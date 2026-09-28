using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Зрение игрока → скрыть / тёмная кромка / полный меш.
/// Видимость как у золотого контура: клетка, не луч в ступни.
/// </summary>
public class EnemyPresenceManager : MonoBehaviour
{
    public static EnemyPresenceManager Instance { get; private set; }

    public PlayerVision vision;
    public int fringeCells = 3;

    private static readonly List<EnemyPresence> Subjects = new List<EnemyPresence>(32);
    float _ensureIn;

    public static void Register(EnemyPresence p)
    {
        if (p != null && !Subjects.Contains(p)) Subjects.Add(p);
    }

    public static void Unregister(EnemyPresence p)
    {
        Subjects.Remove(p);
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            enabled = false;
            return;
        }
        Instance = this;
        if (vision == null) vision = FindObjectOfType<PlayerVision>();
    }

    void Start()
    {
        if (vision == null) vision = FindObjectOfType<PlayerVision>();
        EnsureOnAll();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void LateUpdate()
    {
        if (vision == null) vision = FindObjectOfType<PlayerVision>();
        if (vision == null) return;

        _ensureIn -= Time.deltaTime;
        if (_ensureIn <= 0f)
        {
            _ensureIn = 1.5f;
            EnsureOnAll();
        }

        for (int i = Subjects.Count - 1; i >= 0; i--)
        {
            var s = Subjects[i];
            if (s == null)
            {
                Subjects.RemoveAt(i);
                continue;
            }
            TickOne(s);
        }
    }

    void TickOne(EnemyPresence s)
    {
        Vector3 feet = s.transform.position;
        Vector3 chest = feet + Vector3.up * 1.2f;
        bool visible = CellVisible(feet) || vision.IsPointVisible(chest) || vision.IsPointVisible(feet);
        bool fringe = !visible && (vision.InFringe(chest, fringeCells) || vision.InFringe(feet, fringeCells));
        bool chase = s.ChaseAlive || HasChaseOn(s);

        if (visible)
            s.Apply(EnemyPresence.View.Visible, outline: false);
        else if (fringe && chase)
            s.Apply(EnemyPresence.View.Fringe, outline: true);
        else
            s.Apply(EnemyPresence.View.Hidden, outline: false);
    }

    bool CellVisible(Vector3 world)
    {
        if (vision.mapGrid == null || !vision.mapGrid.IsReady) return false;
        vision.mapGrid.WorldToCell(world, out int cx, out int cz);
        return vision.IsCellVisible(cx, cz);
    }

    static bool HasChaseOn(EnemyPresence s)
    {
        var perc = s.GetComponent<NpcPerception>();
        if (perc != null && (perc.IsLocked || perc.Notice01 > 0.05f || perc.HasCue))
            return true;
        var howl = s.GetComponent<WerewolfHowl>();
        return howl != null && howl.ChaseAlive;
    }

    void EnsureOnAll()
    {
        AttachAll<WerewolfStats>();
        AttachAll<GhostStats>();
        AttachAll<TrainingDummyStats>();
        AttachAll<SkeletonBrain>();
        AttachAll<SkeletonArcherBrain>();
    }

    static void AttachAll<T>() where T : Component
    {
        var list = FindObjectsOfType<T>();
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i].GetComponent<EnemyPresence>() == null)
                list[i].gameObject.AddComponent<EnemyPresence>();
        }
    }
}
