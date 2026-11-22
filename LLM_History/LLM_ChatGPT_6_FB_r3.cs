
using System;
using UnityEngine;

public class LLM_ChatGPT_6_FB : IRoverController
{
    public string Name => "Rover Explorer V4";

    private const float MAX_RANGE = 4f;
    private const float CRITICAL = 0.24f;
    private const float FRONT_BLOCKED = 0.65f;

    private const int SEEK = 0;
    private const int FOLLOW = 1;
    private const int ESCAPE = 2;

    private int state;
    private int direction;

    private int stateTimer;
    private int clearTimer;
    private int lockTimer;
    private int escapeTimer;
    private int stuckTimer;
    private int recoveryCooldown;

    private int escapeDirection;

    private float front;
    private float left;
    private float right;
    private float leftSide;
    private float rightSide;

    private float previousTurn;
    private float bestGoalDistance;

    private bool initialized;

    public void ResetController()
    {
        state = SEEK;
        direction = 1;
        escapeDirection = 1;

        stateTimer = 0;
        clearTimer = 0;
        lockTimer = 0;
        escapeTimer = 0;
        stuckTimer = 0;
        recoveryCooldown = 0;

        front = MAX_RANGE;
        left = MAX_RANGE;
        right = MAX_RANGE;
        leftSide = MAX_RANGE;
        rightSide = MAX_RANGE;

        previousTurn = 0f;
        bestGoalDistance = float.MaxValue;

        initialized = false;
    }

    // -----------------------------------------
    // Sensor filtering
    // -----------------------------------------

    private float Filter(float raw, float old)
    {
        raw = Mathf.Clamp(raw, 0.02f, MAX_RANGE);

        if (raw < old)
            return Mathf.Lerp(old, raw, 0.85f);

        return Mathf.Min(raw, old + 0.12f);
    }

    private void UpdateSensors(Observation obs)
    {
        if (!initialized)
        {
            front = Mathf.Clamp(obs.front, 0.02f, MAX_RANGE);
            left = Mathf.Clamp(obs.left, 0.02f, MAX_RANGE);
            right = Mathf.Clamp(obs.right, 0.02f, MAX_RANGE);
            leftSide = Mathf.Clamp(obs.leftSide, 0.02f, MAX_RANGE);
            rightSide = Mathf.Clamp(obs.rightSide, 0.02f, MAX_RANGE);

            initialized = true;
            return;
        }

        front = Filter(obs.front, front);
        left = Filter(obs.left, left);
        right = Filter(obs.right, right);
        leftSide = Filter(obs.leftSide, leftSide);
        rightSide = Filter(obs.rightSide, rightSide);
    }

    private float Clamp(float v)
    {
        return Mathf.Clamp(v, -1f, 1f);
    }

    // -----------------------------------------
    // Opening assessment
    // -----------------------------------------

    private float LeftOpening()
    {
        return 0.65f * left + 0.35f * leftSide;
    }

    private float RightOpening()
    {
        return 0.65f * right + 0.35f * rightSide;
    }

    private int BestDirection(float goalAngle)
    {
        float l = LeftOpening();
        float r = RightOpening();

        // Prefer a direction that also points toward
        // the goal when the openings are similar.
        float leftScore =
            l + 0.20f * Mathf.Max(0f, goalAngle / 180f);

        float rightScore =
            r + 0.20f * Mathf.Max(0f, -goalAngle / 180f);

        return leftScore >= rightScore ? 1 : -1;
    }

    // -----------------------------------------
    // Dead-end detection
    // -----------------------------------------

    private bool IsDeadEnd()
    {
        bool frontClosed = front < 0.48f;

        bool leftClosed =
            left < 0.42f && leftSide < 0.48f;

        bool rightClosed =
            right < 0.42f && rightSide < 0.48f;

        return frontClosed && leftClosed && rightClosed;
    }

    private bool IsSeverelyBlocked()
    {
        return front < CRITICAL &&
               left < 0.32f &&
               right < 0.32f;
    }

    private bool FrontBlocked()
    {
        return front < FRONT_BLOCKED;
    }

    // -----------------------------------------
    // Navigation state transitions
    // -----------------------------------------

    private void EnterFollow(float goalAngle)
    {
        state = FOLLOW;
        direction = BestDirection(goalAngle);

        stateTimer = 0;
        clearTimer = 0;
        lockTimer = 50;
    }

    private void EnterEscape(float goalAngle)
    {
        state = ESCAPE;

        // Select the more open escape direction.
        escapeDirection = BestDirection(goalAngle);

        escapeTimer = 65;
        stateTimer = 0;
        clearTimer = 0;

        previousTurn = escapeDirection;
        stuckTimer = 0;
        recoveryCooldown = 100;
    }

    // -----------------------------------------
    // Dead-end escape steering
    // -----------------------------------------

    private float EscapeTurn()
    {
        // Strong committed rotation.
        // The controller does not continuously reverse
        // direction based on noisy sensor changes.
        float turn = escapeDirection * 0.95f;

        // If the selected side becomes very close,
        // reduce the turning command slightly.
        float selectedSide =
            escapeDirection > 0 ? left : right;

        if (selectedSide < 0.20f)
            turn *= 0.75f;

        return Clamp(turn);
    }

    // -----------------------------------------
    // Wall following
    // -----------------------------------------

    private float FollowTurn(float goalAngle)
    {
        float selectedSide =
            direction > 0 ? leftSide : rightSide;

        float selectedDiagonal =
            direction > 0 ? left : right;

        // Keep a reasonable clearance from the wall.
        float error = 0.55f - selectedSide;

        float wallTurn =
            -direction * Mathf.Clamp(
                error * 1.7f,
                -0.7f,
                0.7f
            );

        // Turn toward the open side around obstacles.
        float diagonalTurn = 0f;

        if (selectedDiagonal < 0.85f)
        {
            diagonalTurn = direction *
                Mathf.Clamp(
                    (0.85f - selectedDiagonal) * 1.5f,
                    0f,
                    0.8f
                );
        }

        float frontTurn = 0f;

        if (front < 0.90f)
        {
            frontTurn = direction *
                Mathf.Clamp(
                    (0.90f - front) * 1.8f,
                    0.3f,
                    1f
                );
        }

        float goalCorrection =
            Mathf.Clamp(goalAngle / 180f, -1f, 1f) * 0.12f;

        float result =
            wallTurn + diagonalTurn +
            frontTurn + goalCorrection;

        if (front < 0.70f)
        {
            result = direction *
                Mathf.Max(direction * result, 0.35f);
        }

        return Clamp(result);
    }

    // -----------------------------------------
    // Progress monitoring
    // -----------------------------------------

    private void MonitorProgress(float distance)
    {
        if (distance < bestGoalDistance - 0.04f)
        {
            bestGoalDistance = distance;
            stuckTimer = 0;
        }
        else
        {
            stuckTimer++;
        }
    }

    // -----------------------------------------
    // Main controller
    // -----------------------------------------

    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateSensors(obs);

        float goalDistance = Mathf.Max(0f, obs.goalDistance);
        float goalAngle = Mathf.Clamp(
            obs.goalAngleDeg,
            -180f,
            180f
        );

        if (goalDistance < 0.13f)
            return (0f, 0f);

        if (recoveryCooldown > 0)
            recoveryCooldown--;

        if (lockTimer > 0)
            lockTimer--;

        stateTimer++;

        MonitorProgress(goalDistance);

        // -------------------------------------
        // Detect dead ends
        // -------------------------------------

        if (IsDeadEnd() && state != ESCAPE)
        {
            EnterEscape(goalAngle);
        }

        // If no meaningful goal progress for 4 seconds,
        // initiate an escape rather than continuing
        // the same wall-following behaviour.
        if (stuckTimer >= 80 &&
            recoveryCooldown <= 0 &&
            state != ESCAPE)
        {
            EnterEscape(goalAngle);
        }

        float desiredTurn = 0f;
        float speed = 0.60f;

        // -------------------------------------
        // ESCAPE MODE
        // -------------------------------------

        if (state == ESCAPE)
        {
            escapeTimer--;

            desiredTurn = EscapeTurn();

            // Rotate in place to avoid scraping obstacles.
            speed = 0f;

            // Once the rover has turned away from the
            // original blocked direction, resume exploration.
            bool exitAvailable =
                front > 0.85f &&
                (left > 0.55f || right > 0.55f);

            if ((escapeTimer <= 0 && front > 0.65f) ||
                (escapeTimer < 35 && exitAvailable &&
                 Mathf.Abs(goalAngle) < 100f))
            {
                state = SEEK;
                stateTimer = 0;
                clearTimer = 0;

                bestGoalDistance = goalDistance;
                stuckTimer = 0;
            }
        }

        // -------------------------------------
        // GOAL SEEK MODE
        // -------------------------------------

        else if (state == SEEK)
        {
            desiredTurn = Clamp(goalAngle / 65f);

            // Gentle obstacle correction.
            if (left < 0.70f)
            {
                desiredTurn -= 0.30f *
                    Mathf.Clamp01((0.70f - left) / 0.70f);
            }

            if (right < 0.70f)
            {
                desiredTurn += 0.30f *
                    Mathf.Clamp01((0.70f - right) / 0.70f);
            }

            // Begin bypassing an obstacle.
            if (FrontBlocked())
            {
                EnterFollow(goalAngle);
                desiredTurn = direction;
            }
        }

        // -------------------------------------
        // WALL FOLLOW MODE
        // -------------------------------------

        else if (state == FOLLOW)
        {
            desiredTurn = FollowTurn(goalAngle);

            // Exit wall following only after sustained
            // clearance and a goal direction toward the front.
            bool clear =
                front > 1.10f &&
                left > 0.45f &&
                right > 0.45f &&
                Mathf.Abs(goalAngle) < 75f;

            if (clear)
                clearTimer++;
            else
                clearTimer = 0;

            if (clearTimer >= 18 && stateTimer >= 30)
            {
                state = SEEK;
                stateTimer = 0;
                clearTimer = 0;
                stuckTimer = 0;
                bestGoalDistance = goalDistance;

                desiredTurn = Clamp(goalAngle / 65f);
            }
        }

        // -------------------------------------
        // EMERGENCY
        // -------------------------------------

        if (IsSeverelyBlocked() && state != ESCAPE)
        {
            EnterEscape(goalAngle);
            desiredTurn = EscapeTurn();
            speed = 0f;
        }

        // -------------------------------------
        // Steering
        // -------------------------------------

        float smoothing = state == ESCAPE ? 0.85f : 0.60f;

        float turn = Mathf.Lerp(
            previousTurn,
            desiredTurn,
            smoothing
        );

        turn = Clamp(turn);

        previousTurn = turn;

        // -------------------------------------
        // Speed
        // -------------------------------------

        if (state == ESCAPE)
        {
            speed = 0f;
        }
        else
        {
            float safeDistance = Mathf.Min(front, 1.5f);

            speed = Mathf.Lerp(
                0.12f,
                0.75f,
                Mathf.Clamp01((safeDistance - 0.20f) / 1.10f)
            );

            speed *= 1f - 0.40f * Mathf.Abs(turn);

            if (state == FOLLOW)
                speed = Mathf.Min(speed, 0.35f);

            if (front < 0.30f)
                speed = Mathf.Min(speed, 0.05f);

            if (Mathf.Abs(goalAngle) > 120f &&
                state == SEEK)
            {
                speed = Mathf.Min(speed, 0.12f);
            }
        }

        return (
            Mathf.Clamp(speed, 0f, 0.80f),
            turn
        );
    }
}
