// SafeController: the safety wrapper EVERY controller runs through, so all controllers are treated the same way.
// Decide() calls the wrapped controller inside try/catch. An exception, or a NaN output, becomes (0, 0) and is counted.
// Outputs outside the valid range are clamped (speed 0..1, or -1..1 if reversing is allowed; turn -1..1) and counted.
// The counters are per run: ResetController() clears them. The first exception message of a run is kept for debugging.
using System;
using UnityEngine;

public class SafeController : IRoverController
{
    readonly IRoverController inner;
    readonly bool allowReversing;

    /// <summary>Exceptions thrown by the wrapped controller in this run (in Decide or ResetController).</summary>
    public int ExceptionCount { get; private set; }
    /// <summary>NaN outputs turned into (0, 0) in this run.</summary>
    public int NanOutputCount { get; private set; }
    /// <summary>Outputs outside the valid range that had to be clamped in this run.</summary>
    public int ClampedOutputCount { get; private set; }
    /// <summary>Text of the first exception of this run, or null.</summary>
    public string FirstExceptionMessage { get; private set; }

    public IRoverController Inner => inner;

    public SafeController(IRoverController controller, bool allowReversing = false)
    {
        inner = controller ?? throw new ArgumentNullException(nameof(controller));
        this.allowReversing = allowReversing;
    }

    public string Name
    {
        get
        {
            try { return inner.Name ?? inner.GetType().Name; }
            catch { return inner.GetType().Name; }
        }
    }

    public void ResetController()
    {
        ExceptionCount = 0;
        NanOutputCount = 0;
        ClampedOutputCount = 0;
        FirstExceptionMessage = null;
        try { inner.ResetController(); }
        catch (Exception e) { Record(e); }
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        float speed, turn;
        try
        {
            var answer = inner.Decide(obs);
            speed = answer.speed01;
            turn = answer.turn;
        }
        catch (Exception e)
        {
            Record(e);
            return (0f, 0f);
        }

        if (float.IsNaN(speed) || float.IsNaN(turn))
        {
            NanOutputCount++;
            return (0f, 0f);
        }

        float minSpeed = allowReversing ? -1f : 0f;
        float safeSpeed = Mathf.Clamp(speed, minSpeed, 1f);     // +-Infinity is clamped too
        float safeTurn = Mathf.Clamp(turn, -1f, 1f);
        if (safeSpeed != speed || safeTurn != turn) ClampedOutputCount++;
        return (safeSpeed, safeTurn);
    }

    void Record(Exception e)
    {
        ExceptionCount++;
        if (FirstExceptionMessage == null)
        {
            FirstExceptionMessage = e.GetType().Name + ": " + e.Message;
            Debug.LogWarning("SafeController: " + Name + " threw " + FirstExceptionMessage + " (further exceptions in this run are only counted)");
        }
    }
}
