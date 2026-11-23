// QuickTestRunner: runs ONE controller on 10 PRACTICE maps (PracticeMapGenerator seeds 2000-2009, noise 0.05, never the
// four test maps) so a chatbot-written controller can be checked quickly and the result sent back to the chatbot as
// feedback. Map i (0..9) is run once with trial seed i+1 (start pose from TrialSpawner, sensor noise seed = trial seed).
// It needs the Arena scene in Play mode (the editor window "Rover > Quick Test Controller" starts it). For each run it
// loads the map, places the rover, plugs in a fresh instance of the controller, and watches the run at every physics step.
// The one-line report has exactly the fixed wording of docs/feedback_template.txt. The failure labels come from simple,
// documented rules (see Classify*) applied to what the rover actually did; they are hints for the chatbot, not proofs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public enum RunOutcome { Goal, Collision, Timeout }

public static class QuickFailure
{
    // The only allowed failure labels, in tie-break order (earlier wins a tie).
    public const string HeadOn = "hits obstacles head-on";
    public const string WhileTurning = "hits obstacles while turning";
    public const string Oscillating = "gets stuck oscillating";
    public const string DeadEnd = "gets stuck in a dead end";
    public const string AwayFromGoal = "drives away from the goal";
    public const string None = "none";
    public static readonly string[] All = { HeadOn, WhileTurning, Oscillating, DeadEnd, AwayFromGoal, None };
}

/// <summary>What was observed in one run (the input of the failure classification).</summary>
public struct QuickRunRecord
{
    public int mapSeed, trialSeed;
    public RunOutcome outcome;
    public float time;                 // simulated seconds
    public int exceptions;             // exceptions thrown by the controller in this run (SafeController count)
    public string firstException;
    public float lastSpeed, lastTurn;  // the command that was active when a collision happened
    public float startGoalDistance, endGoalDistance;
    public float windowDisplacement;   // straight-line distance moved during the last 20 s (or the whole run if shorter)
    public int windowTurnFlips;        // sign changes of the turn command (|turn| >= 0.1) during that window
    public string failure;             // filled by QuickTestRunner.Classify
}

public class QuickTestResult
{
    public string controller;
    public float noise = 0.05f;
    public List<QuickRunRecord> runs = new List<QuickRunRecord>();
    public int Count(RunOutcome o) { int n = 0; foreach (var r in runs) if (r.outcome == o) n++; return n; }
    public int RuntimeErrors { get { int n = 0; foreach (var r in runs) if (r.exceptions > 0) n++; return n; } }
    public string MostCommonFailure => QuickTestRunner.MostCommonFailure(runs);

    /// <summary>The exact report line (wording fixed by docs/feedback_template.txt).</summary>
    public string Report =>
        "Test report: " + Count(RunOutcome.Goal) + "/" + runs.Count + " reached the goal, " + Count(RunOutcome.Collision)
        + " collisions, " + Count(RunOutcome.Timeout) + " timeouts (got stuck), " + RuntimeErrors
        + " runtime errors. Most common failure: " + MostCommonFailure + ".";

    /// <summary>Report plus one line per run (for the log file; NOT part of the feedback sent to the chatbot).</summary>
    public string Details()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Report);
        sb.AppendLine();
        sb.AppendLine("controller: " + controller + "   (practice map seeds 2000-2009, noise " + noise.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ", trial seeds 1-10)");
        sb.AppendLine("map_seed trial_seed outcome time_s exceptions failure_label");
        foreach (var r in runs)
            sb.AppendLine(r.mapSeed + " " + r.trialSeed + " " + r.outcome + " " + r.time.ToString("0.0") + " " + r.exceptions + " "
                          + (r.failure ?? QuickFailure.None) + (r.exceptions > 0 ? "   [first exception: " + r.firstException + "]" : ""));
        return sb.ToString();
    }
}

public class QuickTestRunner : MonoBehaviour
{
    public const int FirstMapSeed = 2000;
    public const int MapCount = 10;
    public const float WindowSeconds = 20f;

    public string controllerName;
    public static float DefaultNoise = 0.05f;   // tools/run_quicktest.py --noise X sets it (used for the Bug2_Corrected validation at noise 0)
    public float noise = DefaultNoise;          // one of SimConfig.noiseLevels
    public float timeScale = 16f;        // game speed while testing; does not change any result

    /// <summary>Raised once, when all runs are done.</summary>
    public static event Action<QuickTestResult> Completed;
    public static bool IsRunning { get; private set; }
    public int FinishedRuns { get; private set; }

    void Start() { StartCoroutine(RunAll()); }

    IEnumerator RunAll()
    {
        IsRunning = true;
        float oldScale = Time.timeScale;
        var result = new QuickTestResult { controller = controllerName, noise = noise };
        yield return null; yield return null;             // let every Start() of the scene run first

        var loader = FindFirstObjectByType<MapLoader>();
        var spawner = FindFirstObjectByType<TrialSpawner>();
        var host = FindFirstObjectByType<ControllerHost>();
        if (loader == null || spawner == null || host == null || !ControllerRegistry.Exists(controllerName))
        {
            Debug.LogError("QuickTest: needs the Arena scene (MapLoader, TrialSpawner, ControllerHost) and an existing controller class '" + controllerName + "'");
            IsRunning = false; Time.timeScale = oldScale;
            Completed?.Invoke(null);
            Destroy(gameObject);
            yield break;
        }
        var rig = host.GetComponent<SensorRig>();
        var drive = host.GetComponent<RoverDrive>();
        var status = host.GetComponent<RoverStatus>();
        float maxTime = loader.config.maxRunTime;
        Time.timeScale = timeScale;

        for (int i = 0; i < MapCount; i++)
        {
            int mapSeed = FirstMapSeed + i, trialSeed = loader.config.firstTrialSeed + i;
            host.enabled = false;                                   // hold the old controller while the map is swapped
            drive.SetCommand(0f, 0f);
            loader.Load(PracticeMapGenerator.Generate(mapSeed, loader.config));
            yield return null; yield return null;                   // old obstacles are really destroyed at the end of a frame

            spawner.Spawn(trialSeed);
            rig.NoiseLevel = noise;
            rig.SetSeed(trialSeed);
            host.SetController(controllerName);                     // a fresh controller object for every run
            host.enabled = true;

            var rec = new QuickRunRecord { mapSeed = mapSeed, trialSeed = trialSeed };
            var samples = new List<(float t, Vector2 pos)>();
            var turns = new List<(float t, float turn)>();
            int lastDecisions = -1;
            bool haveGoal = host.TryBuildObservation(out var first);
            rec.startGoalDistance = haveGoal ? first.goalDistance : 0f;

            while (true)
            {
                yield return new WaitForFixedUpdate();              // after this physics step (collision callbacks have run)
                if (host.DecisionCount != lastDecisions)
                {
                    lastDecisions = host.DecisionCount;
                    if (!status.Finished) turns.Add((status.ElapsedTime, host.LastTurn));
                }
                var rb = status.GetComponent<Rigidbody>();
                samples.Add((status.ElapsedTime, new Vector2(rb.position.x, rb.position.z)));
                if (status.Finished || status.ElapsedTime >= maxTime) break;
            }

            rec.time = status.ElapsedTime;
            rec.outcome = status.ReachedGoal ? RunOutcome.Goal : status.Collided ? RunOutcome.Collision : RunOutcome.Timeout;
            rec.exceptions = host.ExceptionCount;
            rec.firstException = host.Safe != null ? host.Safe.FirstExceptionMessage : null;
            rec.lastSpeed = host.LastSpeed;       // still the command of the last step: the host zeroes it one step LATER
            rec.lastTurn = host.LastTurn;
            if (host.TryBuildObservation(out var last)) rec.endGoalDistance = last.goalDistance;
            FillWindow(ref rec, samples, turns);
            rec.failure = Classify(rec);
            result.runs.Add(rec);
            FinishedRuns = i + 1;
        }

        host.enabled = false;
        drive.SetCommand(0f, 0f);
        Time.timeScale = oldScale;
        IsRunning = false;
        Completed?.Invoke(result);
        Destroy(gameObject);
    }

    static void FillWindow(ref QuickRunRecord rec, List<(float t, Vector2 pos)> samples, List<(float t, float turn)> turns)
    {
        if (samples.Count == 0) return;
        float tEnd = samples[samples.Count - 1].t;
        float tStart = Mathf.Max(0f, tEnd - WindowSeconds);
        Vector2 firstPos = samples[0].pos;
        foreach (var s in samples) { if (s.t >= tStart) { firstPos = s.pos; break; } }
        rec.windowDisplacement = Vector2.Distance(firstPos, samples[samples.Count - 1].pos);
        int flips = 0, lastSign = 0;
        foreach (var tr in turns)
        {
            if (tr.t < tStart || Mathf.Abs(tr.turn) < 0.1f) continue;
            int sign = tr.turn > 0f ? 1 : -1;
            if (lastSign != 0 && sign != lastSign) flips++;
            lastSign = sign;
        }
        rec.windowTurnFlips = flips;
    }

    // ---- failure classification (pure functions, unit-tested) ----

    public const float HeadOnTurnLimit = 0.3f;        // collision with |last turn command| below this = "head-on"
    public const int OscillationFlips = 10;           // turn direction flips in the last 20 s ...
    public const float OscillationMaxMove = 1.0f;     // ... while moving less than this (m) in a straight line
    public const float StuckMaxMove = 0.5f;           // moved less than this (m) in the last 20 s = stuck (dead end)

    /// <summary>The failure label of one run (QuickFailure.None for a run that reached the goal).</summary>
    public static string Classify(QuickRunRecord r)
    {
        switch (r.outcome)
        {
            case RunOutcome.Goal: return QuickFailure.None;
            case RunOutcome.Collision:
                // the rover was turning noticeably when it hit, or going (nearly) straight
                return Mathf.Abs(r.lastTurn) >= HeadOnTurnLimit ? QuickFailure.WhileTurning : QuickFailure.HeadOn;
            default:
                if (r.windowTurnFlips >= OscillationFlips && r.windowDisplacement < OscillationMaxMove) return QuickFailure.Oscillating;
                if (r.windowDisplacement < StuckMaxMove) return QuickFailure.DeadEnd;
                if (r.endGoalDistance > r.startGoalDistance) return QuickFailure.AwayFromGoal;
                return QuickFailure.Oscillating;     // keeps moving to and fro / round and round without getting closer
        }
    }

    /// <summary>The most common label among the failed runs (collision or timeout); ties go to the label listed first; "none" if no run failed.</summary>
    public static string MostCommonFailure(List<QuickRunRecord> runs)
    {
        string best = QuickFailure.None; int bestCount = 0;
        foreach (string label in QuickFailure.All)
        {
            if (label == QuickFailure.None) continue;
            int n = 0;
            foreach (var r in runs) if (r.outcome != RunOutcome.Goal && (r.failure ?? Classify(r)) == label) n++;
            if (n > bestCount) { best = label; bestCount = n; }
        }
        return best;
    }
}
