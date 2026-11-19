// IControllerGizmos: optional. A controller that implements it can draw debugging lines in the Scene view (for example
// Bug2 draws its M-line and hit point). ControllerHost calls DrawGizmos() from OnDrawGizmos, only while the viewing aids
// are on, so nothing is drawn during experiments. It never changes what the controller decides.
public interface IControllerGizmos
{
    void DrawGizmos();
}
