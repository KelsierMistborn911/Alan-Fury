using UnityEngine;

/// <summary>
/// Контроллер шага. Генерирует синусоидальный импульс-толчок поверх базовой скорости.
/// Внешний код вызывает TryStart() при наличии ввода, Tick() каждый кадр.
///
/// Каденция считается как ПОЛНЫЙ период: cooldown = max(0, 1/frequency - duration).
/// Поэтому stepFrequency теперь честно означает «шагов в секунду»,
/// а на высоких частотах cooldown уходит в 0 и шаги склеиваются в непрерывный галоп.
/// </summary>
public class StepController
{
    private bool _active;
    private float _timer;
    private float _cooldownTimer;

    private float _impulse;
    private float _duration = 0.0001f;
    private float _cooldown;

    public bool IsActive => _active;

    /// <summary>Прогресс текущего шага 0..1 (0 вне шага).</summary>
    public float Phase => _active ? Mathf.Clamp01(_timer / _duration) : 0f;

    /// <summary>Нормализованная кривая шага 0..1 — для боба, звука, наклона камеры.</summary>
    public float Curve => _active ? Mathf.Sin(Phase * Mathf.PI) : 0f;

    /// <summary>Вызывается в момент начала каждого шага (футстеп-звук и т.п.).</summary>
    public System.Action onStepStart;

    /// <summary>
    /// Пытается начать новый шаг. Параметры блендятся между двумя гейтами
    /// по фактической скорости currentSpeed.
    /// </summary>
    public void TryStart(float currentSpeed, GaitConfig a, GaitConfig b)
    {
        if (_active || _cooldownTimer > 0f) return;

        float t = Mathf.InverseLerp(a.speed, b.speed, currentSpeed);
        _impulse = Mathf.Lerp(a.stepDistance, b.stepDistance, t);
        _duration = Mathf.Max(0.0001f, Mathf.Lerp(a.stepDuration, b.stepDuration, t));

        float period = Mathf.Lerp(1f / a.stepFrequency, 1f / b.stepFrequency, t);
        _cooldown = Mathf.Max(0f, period - _duration);   // полный период = duration + cooldown

        _timer = 0f;
        _active = true;
        onStepStart?.Invoke();
    }

    /// <summary>Возвращает мгновенный импульс скорости вдоль шага (метры в секунду).</summary>
    public float Tick(float deltaTime)
    {
        if (_cooldownTimer > 0f)
            _cooldownTimer -= deltaTime;

        if (!_active) return 0f;

        _timer += deltaTime;

        if (_timer >= _duration)
        {
            _active = false;
            _cooldownTimer = _cooldown;
            return 0f;
        }

        return _impulse * Curve;
    }

    /// <summary>Принудительно отменяет шаг (при манёврах).</summary>
    public void Cancel()
    {
        _active = false;
        _cooldownTimer = 0f;
    }
}
