using System;
using UnityEngine;

public class LLM_ChatGPT_5_FB : IRoverController
{
    public string Name => "Persistent Bug Navigation V2";

    private enum Mode
    {
        Seek,
        FollowLeft,
        FollowRight,
        Escape
    }

    private Mode mode;

    private const float DT = 0.05f;
    private const float MAX_RANGE = 4f;

    private const float FOLLOW_DISTANCE = 0.38f;
    private const float FRONT_LIMIT = 0.52f;
    private const float CRITICAL_LIMIT = 0.23f;

    private float front, left, right;
    private float leftSide, rightSide;

    private bool initialized;

    // Persistent navigation direction
    private int followDirection;
    private int escapeDirection;

    // Timers
    private float modeTime;
    private float clearTime;
    private float stuckTime;
    private float escapeTime;

    // Progress monitoring
    private float bestDistance;
    private float previousDistance;

    // Steering memory
    private float previousTurn;

    // Sensor filtering
    private int frontClearCount;
    private int leftClearCount;
    private int rightClearCount;
    private int leftSideClearCount;
    private int rightSideClearCount;

    public void ResetController()
    {
        mode = Mode.Seek;

        front = MAX_RANGE;
        left = MAX_RANGE;
        right = MAX_RANGE;
        leftSide = MAX_RANGE;
        rightSide = MAX_RANGE;

        initialized = false;

        followDirection = 1;
        escapeDirection = 1;

        modeTime = 0f;
        clearTime = 0f;
        stuckTime = 0f;
        escapeTime = 0f;

        bestDistance = 999f;
        previousDistance = 999f;

        previousTurn = 0f;

        frontClearCount = 0;
        leftClearCount = 0;
        rightClearCount = 0;
        leftSideClearCount = 0;
        rightSideClearCount = 0;
    }

    private float Clean(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v))
            return MAX_RANGE;

        return Mathf.Clamp(v, 0.02f, MAX_RANGE);
    }

    // Reject isolated false maximum-range readings.
    private float Filter(
        float oldValue,
        float newValue,
        ref int clearCount)
    {
        newValue = Clean(newValue);

        if (newValue >= 3.95f)
        {
            clearCount++;

            if (clearCount < 3 && oldValue < 3.0f)
                return oldValue;
        }
        else
        {
            clearCount = 0;
        }

        return Mathf.Lerp(oldValue, newValue, 0.40f);
    }

    private void UpdateSensors(Observation o)
    {
        if (!initialized)
        {
            front = Clean(o.front);
            left = Clean(o.left);
            right = Clean(o.right);

            leftSide = Clean(o.leftSide);
            rightSide = Clean(o.rightSide);

            initialized = true;
            return;
        }

        front = Filter(front, o.front, ref frontClearCount);
        left = Filter(left, o.left, ref leftClearCount);
        right = Filter(right, o.right, ref rightClearCount);

        leftSide = Filter(
            leftSide, o.leftSide, ref leftSideClearCount);

        rightSide = Filter(
            rightSide, o.rightSide, ref rightSideClearCount);
    }

    private float ClampTurn(float v)
    {
        return Mathf.Clamp(v, -1f, 1f);
    }

    private float NormalizeAngle(float a)
    {
        while (a > 180f) a -= 360f;
        while (a < -180f) a += 360f;

        return a;
    }

    private float Danger(float distance)
    {
        return Mathf.Clamp01((0.62f - distance) / 0.48f);
    }

    // Select direction only when entering obstacle avoidance.
    private int SelectDirection()
    {
        float leftClearance =
            0.65f * left +
            0.35f * leftSide;

        float rightClearance =
            0.65f * right +
            0.35f * rightSide;

        if (leftClearance > rightClearance + 0.12f)
            return 1;

        if (rightClearance > leftClearance + 0.12f)
            return -1;

        // Break ties consistently.
        return leftClearance >= rightClearance ? 1 : -1;
    }

    private void EnterFollow(int direction)
    {
        followDirection = direction;

        mode = direction > 0
            ? Mode.FollowLeft
            : Mode.FollowRight;

        modeTime = 0f;
        clearTime = 0f;
        stuckTime = 0f;
    }

    private float SeekSteering(float goalAngle)
    {
        float turn = Mathf.Clamp(goalAngle / 65f, -1f, 1f);

        // Forward obstacle repulsion.
        turn += 0.85f * Danger(left);
        turn -= 0.85f * Danger(right);

        // Side obstacle repulsion.
        turn -= 0.45f * Danger(leftSide);
        turn += 0.45f * Danger(rightSide);

        return ClampTurn(turn);
    }

    // Persistent wall following.
    private float FollowSteering()
    {
        float turn;

        if (followDirection > 0)
        {
            // Follow the obstacle on the left.
            float error = leftSide - FOLLOW_DISTANCE;

            turn = -1.45f * error;

            // Front-left obstacle.
            turn -= 0.85f * Danger(left);

            // Directly ahead.
            turn -= 1.15f * Danger(front);

            // Obstacle on the opposite side.
            turn += 0.35f * Danger(rightSide);

            // Lost the left wall: gently search for it.
            if (leftSide > 1.1f)
                turn += 0.25f;
        }
        else
        {
            // Follow the obstacle on the right.
            float error = rightSide - FOLLOW_DISTANCE;

            turn = 1.45f * error;

            turn += 0.85f * Danger(right);
            turn += 1.15f * Danger(front);

            turn -= 0.35f * Danger(leftSide);

            if (rightSide > 1.1f)
                turn -= 0.25f;
        }

        return ClampTurn(turn);
    }

    // Check whether the path toward the goal is clear.
    private bool GoalPathClear(float angle)
    {
        if (front < 0.72f)
            return false;

        if (Mathf.Abs(angle) < 30f)
            return front > 0.90f;

        if (angle > 0f)
            return left > 0.85f;

        return right > 0.85f;
    }

    private void UpdateProgress(float distance)
    {
        if (distance < bestDistance - 0.06f)
        {
            bestDistance = distance;
            stuckTime = 0f;
        }
        else
        {
            stuckTime += DT;
        }

        if (distance < previousDistance - 0.025f)
            stuckTime = Mathf.Max(0f, stuckTime - 0.5f * DT);

        previousDistance = distance;
    }

    private void StartEscape()
    {
        // Change direction once after sustained lack of progress.
        escapeDirection = -followDirection;

        if (mode == Mode.Seek)
            escapeDirection = -SelectDirection();

        mode = Mode.Escape;

        escapeTime = 0f;
        modeTime = 0f;
        clearTime = 0f;
        stuckTime = 0f;
    }

    private float EscapeSteering()
    {
        // Turn toward the selected open side.
        float turn = escapeDirection;

        // Avoid turning into a close obstacle.
        if (escapeDirection > 0 && left < 0.32f)
            turn = -0.5f;

        if (escapeDirection < 0 && right < 0.32f)
            turn = 0.5f;

        return ClampTurn(turn);
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateSensors(obs);

        float goalDistance = Mathf.Max(0f, obs.goalDistance);
        float goalAngle = NormalizeAngle(obs.goalAngleDeg);

        modeTime += DT;

        UpdateProgress(goalDistance);

        // Goal reached.
        if (goalDistance < 0.16f)
            return (0f, 0f);

        bool obstacleAhead =
            front < FRONT_LIMIT ||
            left < 0.34f ||
            right < 0.34f;

        // Enter obstacle avoidance.
        if (mode == Mode.Seek && obstacleAhead)
        {
            EnterFollow(SelectDirection());
        }

        // Escape mode: hold the selected direction.
        if (mode == Mode.Escape)
        {
            escapeTime += DT;

            if (escapeTime > 1.1f)
            {
                EnterFollow(escapeDirection);
            }
        }

        // Check for a clear route back to the goal.
        if (mode == Mode.FollowLeft ||
            mode == Mode.FollowRight)
        {
            if (GoalPathClear(goalAngle))
                clearTime += DT;
            else
                clearTime = 0f;

            // Require sustained clearance to prevent oscillation.
            if (clearTime > 0.65f && modeTime > 1.5f)
            {
                mode = Mode.Seek;

                modeTime = 0f;
                clearTime = 0f;
                stuckTime = 0f;

                bestDistance = goalDistance;
            }
        }

        // Persistent lack-of-progress recovery.
        if (mode != Mode.Escape && stuckTime > 5.0f)
        {
            StartEscape();
        }

        float turn;

        if (mode == Mode.Seek)
        {
            turn = SeekSteering(goalAngle);
        }
        else if (mode == Mode.Escape)
        {
            turn = EscapeSteering();
        }
        else
        {
            turn = FollowSteering();
        }

        // Emergency collision avoidance.
        if (front < CRITICAL_LIMIT)
        {
            if (left > right)
                turn = 1f;
            else
                turn = -1f;
        }

        // Prevent sudden steering reversals.
        float smoothing = mode == Mode.Escape ? 0.85f : 0.40f;

        turn = Mathf.Lerp(previousTurn, turn, smoothing);

        previousTurn = ClampTurn(turn);

        turn = previousTurn;

        // Adaptive speed.
        float nearest = Mathf.Min(
            front,
            left,
            right,
            leftSide,
            rightSide
        );

        float speed = 0.85f;

        if (nearest < 0.85f)
        {
            speed = Mathf.Lerp(
                0.12f,
                0.65f,
                Mathf.Clamp01((nearest - 0.18f) / 0.67f)
            );
        }

        // Reduce speed for sharp turns.
        speed *= 1f - 0.65f * Mathf.Abs(turn);

        // Wall following requires slower movement.
        if (mode == Mode.FollowLeft ||
            mode == Mode.FollowRight)
        {
            speed = Mathf.Min(speed, 0.48f);
        }

        // Escape at a controlled speed.
        if (mode == Mode.Escape)
            speed = 0.20f;

        // Critical proximity.
        if (front < 0.15f)
            speed = 0f;

        return (
            Mathf.Clamp(speed, 0f, 1f),
            ClampTurn(turn)
        );
    }
}
