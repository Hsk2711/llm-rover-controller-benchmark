// ObservationRestrictionTest: Editor menu "Rover > Test Observation Restriction". Checks ControllerHost.Restrict(): every
// controller except Bug2 gets roverPos/startPos/goalPos = (0,0) and keeps all sensor/goal readings unchanged; Bug2 gets the
// Observation untouched. Prints PASS/FAIL per case in the Console. (Pure data test, no Play mode needed.)
using System.Text;
using UnityEditor;
using UnityEngine;

public static class ObservationRestrictionTest
{
    class Stub : IRoverController
    {
        public string Name => "Stub";
        public void ResetController() { }
        public (float speed01, float turn) Decide(Observation obs) => (0f, 0f);
    }

    static Observation Sample() => new Observation
    {
        front = 1.1f, left = 2.2f, right = 3.3f, leftSide = 0.4f, rightSide = 0.5f, goalDistance = 6.6f, goalAngleDeg = -45f,
        roverPos = new Vector2(1f, 2f), startPos = new Vector2(-2.5f, -2.5f), goalPos = new Vector2(2.5f, 2.5f)
    };

    [MenuItem("Rover/Test Observation Restriction")]
    public static void Menu() { Debug.Log(Run()); }

    public static string Run()
    {
        var sb = new StringBuilder();
        int failures = 0;
        void Check(string what, bool ok) { if (!ok) failures++; sb.AppendLine((ok ? "PASS " : "FAIL ") + what); }

        var o = Sample();
        foreach (var c in new IRoverController[] { new Stub(), new RuleBasedController(), new ManualController(), new StopController() })
        {
            var r = ControllerHost.Restrict(o, c);
            Check(c.GetType().Name + ": positions are zero", r.roverPos == Vector2.zero && r.startPos == Vector2.zero && r.goalPos == Vector2.zero);
            Check(c.GetType().Name + ": readings and goal data unchanged",
                  r.front == o.front && r.left == o.left && r.right == o.right && r.leftSide == o.leftSide && r.rightSide == o.rightSide
                  && r.goalDistance == o.goalDistance && r.goalAngleDeg == o.goalAngleDeg);
        }
        var b = ControllerHost.Restrict(o, new Bug2Controller());
        Check("Bug2Controller: positions kept", b.roverPos == o.roverPos && b.startPos == o.startPos && b.goalPos == o.goalPos);
        Check("Bug2Controller: readings unchanged", b.front == o.front && b.rightSide == o.rightSide && b.goalAngleDeg == o.goalAngleDeg);
        Check("original Observation is not modified (struct copy)", o.roverPos == new Vector2(1f, 2f));
        // every class the registry knows, by its real kind: only the Bug2 class keeps positions
        foreach (string name in ControllerRegistry.Names())
        {
            var inst = ControllerRegistry.Create(name);
            var r = ControllerHost.Restrict(o, inst);
            bool keeps = r.roverPos != Vector2.zero || r.startPos != Vector2.zero || r.goalPos != Vector2.zero;
            Check("registry class " + name + (name == "Bug2Controller" ? " keeps" : " loses") + " positions", keeps == (name == "Bug2Controller"));
        }
        sb.Insert(0, failures == 0 ? "Observation restriction test: ALL PASS\n" : "Observation restriction test: " + failures + " FAILED\n");
        return sb.ToString();
    }
}
