// IRoverController: the "plug" every rover controller must fit. A controller only has a Name, a reset for the start of a
// run, and Decide(): look at an Observation, answer with (speed01, turn). speed01 is a fraction of max speed (0..1, no
// reversing); turn is a fraction of max turn rate (-1..1), positive = turn LEFT.
public interface IRoverController
{
    string Name { get; }
    void ResetController();
    (float speed01, float turn) Decide(Observation obs);
}
