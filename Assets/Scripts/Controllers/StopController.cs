// StopController: does nothing, the rover stays still. Used to check the controller plug and as a "no controller" choice.
public class StopController : IRoverController
{
    public string Name => "Stop";

    public void ResetController() { }

    public (float speed01, float turn) Decide(Observation obs) => (0f, 0f);
}
