// Bug2Params: the settings (all the numbers) of the Bug2 controller, in one asset (Assets/Resources/Bug2Params.asset) so
// nothing is hard-coded in the controller. Standard values, NOT tuned. Distances in metres. The first block is exactly what
// the study brief specifies; the second block holds the few extra numbers the brief leaves open.
using UnityEngine;

[CreateAssetMenu(fileName = "Bug2Params", menuName = "RoverNavStudy/Bug2Params")]
public class Bug2Params : ScriptableObject
{
    [Header("As specified")]
    public float hitDistance = 0.30f;            // front < this: obstacle hit (go to goal -> follow wall); also: turn left while following
    public float wallDistance = 0.25f;           // keep the wall on the right at this distance (right SIDE sensor, 90 deg)
    public float mLineTolerance = 0.05f;         // "back on the M-line": within this distance of the start-goal line
    public float progressMargin = 0.10f;         // leave the wall only if at least this much closer to the goal than the hit point
    public float loopPathLength = 1.0f;          // back at the hit point after travelling more than this: goal unreachable
    public int medianWindow = 3;                 // median filter over the last readings of each sensor

    [Header("Left open by the brief (my standard choices)")]
    public float frontClearDistance = 0.45f;     // "front is clear" (same value as the rule-based controller's turn release)
    public float goSpeed = 1.0f;                 // go to goal: full speed
    public float wallSpeed = 0.5f;               // follow wall: half speed
    public float wallGain = 4.0f;                // turn per metre of wall-distance error (0.25 m error = full turn)
    public float goalAngleForFullTurnDeg = 45f;  // go to goal: turn = goalAngle / this, clamped to -1..1
    public float hitReturnRadius = 0.15f;        // "back at the hit point" = within this distance of it

    [Header("Wall-lost rule (pilot revision; the textbook algorithm assumes continuous wall contact)")]
    public float lostWallDistance = 0.60f;       // FOLLOW_WALL: right SIDE reading above this = the wall is lost (outside corner)
    public float lostWallSpeed = 0.5f;           // while the wall is lost (straight part and turning part): 50% speed
    public float lostWallAdvance = 0.20f;        // drive straight this far first (one body length), then turn
    public float lostWallTurn = 0.6f;            // then turn RIGHT at 60% of the max turn rate until the wall is found again

    public const string ResourceName = "Bug2Params";

    /// <summary>Load Assets/Resources/Bug2Params.asset; if it is missing, use the default values above.</summary>
    public static Bug2Params Load()
    {
        var p = Resources.Load<Bug2Params>(ResourceName);
        if (p == null)
        {
            Debug.LogWarning("Bug2Params asset not found in a Resources folder: using the default values");
            p = CreateInstance<Bug2Params>();
        }
        return p;
    }
}
