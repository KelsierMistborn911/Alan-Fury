using UnityEngine;

/// <summary>
/// Один шаг/толчок. TryStart() когда готов новый шаг, Tick() каждый кадр.
///
/// Период = duration + cooldown, cooldown = max(0, 1/frequency - duration).
/// Impulse = длина шага (м). Hop = высота подскока (м), 0 = по земле.
/// Tick() по-прежнему возвращает impulse * sin — волки используют как добавку к скорости.
/// Игрок читает Impulse/Duration/Phase/Hop и сам считает перемещение за шаг.
/// </summary>
public class StepController
{
    private bool _active;
    private float _timer;
    private float _cooldownTimer;

    private float _impulse;
    private float _duration = 0.0001f;
    private float _cooldown;
    private float _hop;

    public bool IsActive => _active;
    public float Phase => _active ? Mathf.Clamp01(_timer / _duration) : 0f;
    public float Curve => _active ? Mathf.Sin(Phase * Mathf.PI) : 0f;
    public float Duration => _duration;
    public float Impulse => _impulse;
    public float Hop => _hop;

    public System.Action onStepStart;

    public void TryStart(float currentSpeed, GaitConfig a, GaitConfig b)
    {
        if (_active || _cooldownTimer > 0f) return;

        float t = Mathf.InverseLerp(a.speed, b.speed, currentSpeed);
        _impulse = Mathf.Lerp(a.stepDistance, b.stepDistance, t);
        _duration = Mathf.Max(0.0001f, Mathf.Lerp(a.stepDuration, b.stepDuration, t));
        _hop = Mathf.Lerp(a.stepHop, b.stepHop, t);

        float freqA = Mathf.Max(0.01f, a.stepFrequency);
        float freqB = Mathf.Max(0.01f, b.stepFrequency);
        float period = Mathf.Lerp(1f / freqA, 1f / freqB, t);
        _cooldown = Mathf.Max(0f, period - _duration);

        _timer = 0f;
        _active = true;
        onStepStart?.Invoke();
    }

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

    public void OverrideTiming(float duration, float impulse)
    {
        if (!_active) return;
        _duration = Mathf.Max(0.05f, duration);
        _impulse = Mathf.Max(0f, impulse);
    }

    public void Cancel()
    {
        _active = false;
        _cooldownTimer = 0f;
    }
}
