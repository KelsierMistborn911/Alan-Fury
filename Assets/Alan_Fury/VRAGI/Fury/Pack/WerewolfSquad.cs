using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Отряд стаи: один контроллер, остальные дешевле. Слияние при встрече до полной стаи.
/// </summary>
public class WerewolfSquad
{
    public int Id;
    public WerewolfHowl Controller;
    public readonly List<WerewolfHowl> Members = new List<WerewolfHowl>();

    public void Add(WerewolfHowl w)
    {
        if (w == null || Members.Contains(w)) return;
        Members.Add(w);
        w.SquadId = Id;
        PickController();
    }

    public void Remove(WerewolfHowl w)
    {
        Members.Remove(w);
        if (w != null && w.SquadId == Id) w.SquadId = 0;
        if (Controller == w) Controller = null;
        PickController();
    }

    public void MergeFrom(WerewolfSquad other)
    {
        if (other == null || other == this) return;
        for (int i = 0; i < other.Members.Count; i++)
            Add(other.Members[i]);
        other.Members.Clear();
        other.Controller = null;
    }

    public void PickController()
    {
        WerewolfHowl best = null;
        int bestRank = int.MinValue;
        int n = 0;
        for (int i = Members.Count - 1; i >= 0; i--)
        {
            var m = Members[i];
            if (m == null)
            {
                Members.RemoveAt(i);
                continue;
            }
            n++;
            if (m.packRank > bestRank)
            {
                bestRank = m.packRank;
                best = m;
            }
        }
        if (best == null && Members.Count > 0)
            best = Members[Random.Range(0, Members.Count)];
        Controller = best;
        for (int i = 0; i < Members.Count; i++)
            Members[i].IsSquadController = Members[i] == Controller;
    }

    public bool Alive => Members.Count > 0;
}
