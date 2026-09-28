using System;
using UnityEngine;

/// <summary>
/// Шина звука врага. Визуал (волна / рябь) подписывается позже — сейчас только событие.
/// </summary>
public static class EnemySoundBus
{
    public enum Kind { Step, Howl, Combat, Unknown }

    public static event Action<Vector3, Kind, float> OnSound;

    public static void Emit(Vector3 worldPos, Kind kind, float strength = 1f)
    {
        OnSound?.Invoke(worldPos, kind, Mathf.Max(0f, strength));
    }
}
