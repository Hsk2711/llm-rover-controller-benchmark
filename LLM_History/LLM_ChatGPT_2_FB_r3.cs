
using System;
using UnityEngine;

public class LLM_ChatGPT_2_FB : IRoverController
{
    public string Name => "Adaptive Navigation V4";

    // Filtered sensor readings
    private float fFront, fLeft, fRight, fLeftSide, fRightSide;
    private bool sensorsInitialized;

    private int frontClearTicks;
    private int leftClearTicks;
    private int rightClearTicks;
    private int leftSideClearTicks;
    private int rightSideClearTicks;

    // Obstacle avoidance state
    private bool avoiding;
    private int avoidDirection;
    private int avoidTicks;
    private int avoidClearTicks;

    // Steering stability
    private float previousTurn;
    private int lastTurnDirection;
    private int turnCommitTicks;

    // Progress monitoring
    private float progressReference;
    private int progressTimer;
    private int noProgressWindows;

    // Recovery state
    private bool recovering;
    private int recoveryDirection;
    private int recoveryTicks;
    private int recoveryAttempts;

    private const float SENSOR_MAX = 4.0f;

    public void ResetController()
    {
        fFront = 4f;
        fLeft = 4f;
        fRight = 4f;
        fLeftSide = 4f;
        fRightSide = 4f;

        sensorsInitialized = false;

        frontClearTicks = 0;
        leftClearTicks = 0;
        rightClearTicks = 0;
        leftSideClearTicks = 0;
        rightSideClearTicks = 0;

        avoiding = false;
        avoidDirection = 1;
        avoidTicks = 0;
        avoidClearTicks = 0;

        previousTurn = 0f;
        lastTurnDirection = 1;
        turnCommitTicks = 0;

        progressReference = 0f;
        progressTimer = 0;
        noProgressWindows = 0;

        recovering = false;
        recoveryDirection = 1;
        recoveryTicks = 0;
        recoveryAttempts = 0;
    }

    private float Clean(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return SENSOR_MAX;

        return Mathf.Clamp(value, 0.02f, SENSOR_MAX);
    }

    // Closer readings are accepted quickly.
    // Sudden large increases are accepted more cautiously
    // to reduce the effect of occasional false-clear readings.
    private float FilterSensor(
        float raw,
        ref float filtered,
        ref int clearTicks)
    {
        raw = Clean(raw);

        if (!sensorsInitialized)
        {
            filtered = raw;
            clearTicks = 0;
            return filtered;
        }

        if (raw < filtered)
        {
            filtered = Mathf.Lerp(filtered, raw, 0.72f);
            clearTicks = 0;
        }
        else if (raw > filtered + 1.1f)
        {
            clearTicks++;

            if (clearTicks >= 2)
            {
                filtered = Mathf.Lerp(filtered, raw, 0.30f);
                clearTicks = 0;
            }
        }
        else
        {
            filtered = Mathf.Lerp(filtered, raw, 0.24f);
            clearTicks = 0;
        }

        return filtered;
    }

    private float Danger(float distance, float threshold)
    {
        return Mathf.Clamp01(
            (threshold - distance) / threshold
        );
    }

    private float LeftClearance()
    {
        return 0.55f * fLeft + 0.45f * fLeftSide;
    }

    private float RightClearance()
    {
        return 0.55f * fRight + 0.45f * fRightSide;
    }

    // Positive direction means left.
    private int ChooseDirection(float goalAngle)
    {
        float leftScore = LeftClearance();
        float rightScore = RightClearance();

        // Slight preference for the direction that moves
        // the rover closer to the goal heading.
        if (goalAngle > 12f)
            leftScore += 0.20f;
        else if (goalAngle < -12f)
            rightScore += 0.20f;

        // Prefer the previous direction when the choices
        // are almost equally clear.
        if (Mathf.Abs(leftScore - rightScore) < 0.30f)
            return lastTurnDirection;

        return leftScore > rightScore ? 1 : -1;
    }

    private void StartAvoidance(float goalAngle)
    {
        avoiding = true;
        avoidDirection = ChooseDirection(goalAngle);
        avoidTicks = 0;
        avoidClearTicks = 0;

        lastTurnDirection = avoidDirection;
        turnCommitTicks = 15;
    }

    private void UpdateAvoidance(float goalAngle)
    {
        if (!avoiding)
            return;

        avoidTicks++;

        float selectedClearance =
            avoidDirection > 0
                ? LeftClearance()
                : RightClearance();

        float otherClearance =
            avoidDirection > 0
                ? RightClearance()
                : LeftClearance();

        // Only change direction when the current direction
        // is substantially worse and the commitment period
        // has elapsed.
        if (avoidTicks > 15 &&
            otherClearance > selectedClearance + 0.65f)
        {
            avoidDirection = -avoidDirection;
            lastTurnDirection = avoidDirection;
            avoidTicks = 0;
        }

        // Exit when the forward route has opened.
        // This does not require both side sensors to be clear,
        // which helps in narrow corridors.
        if (fFront > 1.55f &&
            (fLeft > 0.85f || fRight > 0.85f))
        {
            avoidClearTicks++;
        }
        else
        {
            avoidClearTicks = 0;
        }

        if (avoidTicks >= 12 && avoidClearTicks >= 5)
        {
            avoiding = false;
            avoidTicks = 0;
            avoidClearTicks = 0;
        }
    }

    private void UpdateProgress(float goalDistance)
    {
        progressTimer++;

        if (progressTimer == 1)
        {
            progressReference = goalDistance;
        }

        // Check progress approximately every 1.5 seconds.
        if (progressTimer >= 30)
        {
            float improvement =
                progressReference - goalDistance;

            if (improvement > 0.07f)
            {
                noProgressWindows = 0;
                recoveryAttempts = 0;
            }
            else
            {
                noProgressWindows++;
            }

            progressReference = goalDistance;
            progressTimer = 0;
        }
    }

    private void StartRecovery(float goalAngle)
    {
        recovering = true;

        recoveryDirection = ChooseDirection(goalAngle);
        recoveryTicks = 22;

        recoveryAttempts++;

        lastTurnDirection = recoveryDirection;
        turnCommitTicks = 15;

        // Reset progress monitoring so the controller
        // can evaluate the result of this manoeuvre.
        noProgressWindows = 0;
        progressTimer = 0;
    }

    private (float speed, float turn) RecoveryCommand()
    {
        recoveryTicks--;

        float speed = 0.20f;
        float turn = recoveryDirection;

        // Give the rover a stronger turn if the front is blocked.
        if (fFront < 0.55f)
        {
            speed = 0.12f;
            turn = recoveryDirection;
        }

        if (fFront < 0.22f)
        {
            speed = 0.07f;
        }

        if (recoveryTicks <= 0)
        {
            recovering = false;

            // If the front is still blocked, allow another
            // short recovery attempt and reconsider direction.
            if (fFront < 0.55f && recoveryAttempts < 4)
            {
                if (recoveryAttempts >= 2)
                {
                    float left = LeftClearance();
                    float right = RightClearance();

                    if (recoveryDirection > 0 &&
                        right > left + 0.20f)
                    {
                        recoveryDirection = -1;
                    }
                    else if (recoveryDirection < 0 &&
                             left > right + 0.20f)
                    {
                        recoveryDirection = 1;
                    }
                }

                recovering = true;
                recoveryTicks = 18;
                turn = recoveryDirection;
            }
            else
            {
                recoveryAttempts = 0;
            }
        }

        previousTurn = turn;

        return (speed, turn);
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        float rawFront = Clean(obs.front);
        float rawLeft = Clean(obs.left);
        float rawRight = Clean(obs.right);
        float rawLeftSide = Clean(obs.leftSide);
        float rawRightSide = Clean(obs.rightSide);

        if (!sensorsInitialized)
        {
            fFront = rawFront;
            fLeft = rawLeft;
            fRight = rawRight;
            fLeftSide = rawLeftSide;
            fRightSide = rawRightSide;

            sensorsInitialized = true;
        }
        else
        {
            FilterSensor(rawFront, ref fFront, ref frontClearTicks);
            FilterSensor(rawLeft, ref fLeft, ref leftClearTicks);
            FilterSensor(rawRight, ref fRight, ref rightClearTicks);
            FilterSensor(rawLeftSide, ref fLeftSide, ref leftSideClearTicks);
            FilterSensor(rawRightSide, ref fRightSide, ref rightSideClearTicks);
        }

        float goalDistance = Mathf.Max(0f, obs.goalDistance);
        float goalAngle = Mathf.Clamp(
            obs.goalAngleDeg, -180f, 180f
        );

        UpdateProgress(goalDistance);

        // Recovery takes priority over normal navigation.
        if (recovering)
            return RecoveryCommand();

        // Detect a persistent lack of progress.
        // Two consecutive windows means approximately 3 seconds
        // without meaningful improvement.
        if (noProgressWindows >= 2)
        {
            StartRecovery(goalAngle);
            return RecoveryCommand();
        }

        // Start avoidance only when the forward route or
        // diagonal approaches indicate a genuine obstacle.
        bool frontBlocked = fFront < 1.15f;

        bool leftDiagonalBlocked = fLeft < 0.62f;
        bool rightDiagonalBlocked = fRight < 0.62f;

        bool bothDiagonalsBlocked =
            leftDiagonalBlocked && rightDiagonalBlocked;

        if (!avoiding &&
            (frontBlocked || bothDiagonalsBlocked))
        {
            StartAvoidance(goalAngle);
        }

        UpdateAvoidance(goalAngle);

        // Goal steering.
        float goalTurn = Mathf.Clamp(
            goalAngle / 65f, -1f, 1f
        );

        // Reduce goal steering when an obstacle is close.
        float frontDanger = Danger(fFront, 1.25f);

        float goalWeight =
            1f - 0.70f * frontDanger;

        float turn = goalTurn * goalWeight;

        // Smooth obstacle avoidance.
        if (avoiding)
        {
            float avoidanceStrength =
                0.48f + 0.42f * frontDanger;

            turn =
                turn * (1f - avoidanceStrength) +
                avoidDirection * avoidanceStrength;
        }
        else
        {
            // Gentle diagonal and side obstacle avoidance.
            float leftDanger = Danger(fLeft, 0.85f);
            float rightDanger = Danger(fRight, 0.85f);

            float leftSideDanger = Danger(fLeftSide, 0.45f);
            float rightSideDanger = Danger(fRightSide, 0.45f);

            float sideCorrection =
                (rightDanger - leftDanger) * 0.42f +
                (rightSideDanger - leftSideDanger) * 0.25f;

            turn += sideCorrection;
        }

        // Emergency response for a very close front obstacle.
        if (fFront < 0.38f)
        {
            int emergencyDirection =
                ChooseDirection(goalAngle);

            turn = emergencyDirection;
        }

        turn = Mathf.Clamp(turn, -1f, 1f);

        // Steering hysteresis:
        // prevent rapid left-right changes caused by small
        // sensor fluctuations.
        if (turnCommitTicks > 0)
        {
            if (Mathf.Abs(turn) > 0.18f &&
                Mathf.Sign(turn) != Mathf.Sign(previousTurn) &&
                Mathf.Abs(previousTurn) > 0.35f)
            {
                turn = previousTurn * 0.65f +
                       turn * 0.35f;
            }

            turnCommitTicks--;
        }

        // Smooth steering without excessive delay.
        turn = previousTurn * 0.72f + turn * 0.28f;

        // Remember meaningful steering direction.
        if (Mathf.Abs(turn) > 0.18f)
        {
            lastTurnDirection = turn > 0f ? 1 : -1;
        }

        previousTurn = turn;

        // Adaptive speed.
        float speed = 0.82f;

        if (fFront < 1.60f)
        {
            speed = Mathf.Min(
                speed,
                0.30f + 0.42f * Mathf.Clamp01(fFront / 1.60f)
            );
        }

        if (fFront < 0.75f)
        {
            speed = Mathf.Min(speed, 0.28f);
        }

        if (fFront < 0.38f)
        {
            speed = Mathf.Min(speed, 0.13f);
        }

        if (Mathf.Abs(turn) > 0.70f)
        {
            speed = Mathf.Min(speed, 0.38f);
        }
        else if (Mathf.Abs(turn) > 0.45f)
        {
            speed = Mathf.Min(speed, 0.55f);
        }

        // Avoid scraping nearby side obstacles.
        if (fLeftSide < 0.30f ||
            fRightSide < 0.30f)
        {
            speed = Mathf.Min(speed, 0.25f);
        }

        // Slow down near the goal.
        if (goalDistance < 0.45f)
        {
            speed = Mathf.Min(speed, 0.30f);
        }

        if (goalDistance < 0.18f)
        {
            speed = Mathf.Min(speed, 0.15f);
        }

        speed = Mathf.Clamp01(speed);
        turn = Mathf.Clamp(turn, -1f, 1f);

        return (speed, turn);
    }
}
