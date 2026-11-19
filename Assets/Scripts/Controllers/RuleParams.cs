// RuleParams: the settings (all the numbers) of the rule-based baseline controller, kept in one asset so nothing is hard-coded
// in the controller. They are "standard values", NOT tuned: round numbers a student would pick. The asset lives in
// Assets/Resources/RuleParams.asset so the controller can load it by name. Distances in metres, turn/speed as fractions.
using UnityEngine;

[CreateAssetMenu(fileName = "RuleParams", menuName = "RoverNavStudy/RuleParams")]
public class RuleParams : ScriptableObject
{
    [Header("Front obstacle: stop and turn on the spot")]
    public float frontStopDistance = 0.30f;        // front reading below this: stop and turn away
    public float frontClearDistance = 0.45f;       // keep turning until the front reading is above this (avoids wobbling)
    public float escapeTurn = 1.0f;                // full turn rate

    [Header("Side obstacle: slow down and steer away")]
    public float sideDistance = 0.20f;             // left or right reading below this
    public float sideSpeed = 0.5f;                 // 50% speed
    public float sideTurn = 1.0f;                  // steer away at full turn rate

    [Header("Nothing close: go to the goal")]
    public float cruiseSpeed = 1.0f;               // full speed
    public float goalAngleForFullTurnDeg = 45f;    // turn = goalAngle / this, clamped to -1..1

    public const string ResourceName = "RuleParams";

    /// <summary>Load Assets/Resources/RuleParams.asset; if it is missing, use the default values above.</summary>
    public static RuleParams Load()
    {
        var p = Resources.Load<RuleParams>(ResourceName);
        if (p == null)
        {
            Debug.LogWarning("RuleParams asset not found in a Resources folder: using the default values");
            p = CreateInstance<RuleParams>();
        }
        return p;
    }
}
