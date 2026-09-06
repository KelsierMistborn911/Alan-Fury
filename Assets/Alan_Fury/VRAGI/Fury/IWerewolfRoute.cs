using UnityEngine;

/// <summary>
/// Сменный источник точек патруля. HuntPatrol / Investigate не строят маршрут сами.
/// Обстоятельства меняют провайдер, не шаг.
/// </summary>
public interface IWerewolfRoute
{
    bool HasPoints { get; }

    /// <summary>Текущая цель следования.</summary>
    Vector3 CurrentPoint { get; }

    /// <summary>Ближайшая точка маршрута (отход после потери следа).</summary>
    Vector3 ResumePoint(Vector3 from);

    /// <summary>Куда коситься на этом участке (не обязательно в точку ног).</summary>
    Vector3 LookHint(Vector3 from);

    /// <summary>Дошли до текущей — следующая (цикл).</summary>
    void Advance();

    /// <summary>Сбросить индекс на ближайшую к from.</summary>
    void ResetToNearest(Vector3 from);
}
