// MapLoader: builds a MapDefinition in the arena. Load() first removes every obstacle made by an earlier Load(), then
// creates one grey box (height SimConfig.obstacleHeight, tagged "Obstacle", with a collider) for each obstacle in the map
// and moves the StartZone / GoalZone squares to the map's zone centres. The boxes are children of an "Obstacles" object.
// Works in Edit mode (to look at a map) and in Play mode. Keep this object at the world origin (0,0,0).
using UnityEngine;

public class MapLoader : MonoBehaviour
{
    public SimConfig config;
    public Material obstacleMaterial;        // grey; if empty a plain grey material is made at run time
    public Transform startZone;              // found by the name "StartZone" if left empty
    public Transform goalZone;               // found by the name "GoalZone" if left empty
    public MapDefinition map;                // the map to show; also loaded automatically when Play starts
    public bool loadOnStart = true;

    public const string RootName = "Obstacles";

    /// <summary>null (default) = tell every RoverStatus in the scene that the map changed. The experiment runner (several arenas, each
    /// with its own MapLoader placed at the arena's offset) sets this to the one rover of its arena.</summary>
    public RoverStatus onlyStatus;

    void Start()
    {
        if (loadOnStart && map != null) Load(map);
    }

    /// <summary>Remove the old obstacles, build the new map and move the zones.</summary>
    public void Load(MapDefinition definition)
    {
        if (definition == null) { Debug.LogError("MapLoader: no map given", this); return; }
        if (config == null) { Debug.LogError("MapLoader: SimConfig is not assigned", this); return; }

        map = definition;
        Clear();

        Transform root = new GameObject(RootName).transform;
        root.SetParent(transform, false);
        root.localPosition = Vector3.zero;       // the loader may sit at an arena offset (experiment runner); at the origin this is the same as before
        root.localRotation = Quaternion.identity;

        float h = config.obstacleHeight;
        for (int i = 0; i < definition.obstacles.Count; i++)
        {
            BoxObstacle b = definition.obstacles[i];
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);   // comes with a BoxCollider
            box.name = "Obstacle_" + i.ToString("00");
            box.tag = "Obstacle";
            box.transform.SetParent(root, false);
            box.transform.localScale = new Vector3(b.size.x, h, b.size.y);
            box.transform.localPosition = new Vector3(b.position.x, h * 0.5f, b.position.y);
            // Unity's +Y rotation turns RIGHT, and the map's positive angle is LEFT (counter-clockwise from above)
            box.transform.localRotation = Quaternion.Euler(0f, -b.rotationDeg, 0f);
            box.GetComponent<Renderer>().sharedMaterial = GetMaterial();
        }

        PlaceZone(ref startZone, "StartZone", definition.startZoneCentre);
        PlaceZone(ref goalZone, "GoalZone", definition.goalZoneCentre);

        if (Application.isPlaying)
        {
            Physics.SyncTransforms();
            // the rover's status keeps lists of obstacle and goal colliders: tell it the map changed
            if (onlyStatus != null) onlyStatus.RefreshTargets();
            else foreach (var status in FindObjectsByType<RoverStatus>(FindObjectsSortMode.None)) status.RefreshTargets();
        }
    }

    /// <summary>Remove every obstacle made by Load().</summary>
    public void Clear()
    {
        // collect first, then destroy (the child list changes while destroying)
        var old = new System.Collections.Generic.List<Transform>();
        foreach (Transform child in transform) if (child.name == RootName) old.Add(child);
        foreach (var t in old)
        {
            if (Application.isPlaying)
            {
                t.gameObject.SetActive(false);   // gone at once for tag searches; Destroy happens at the end of the frame
                Destroy(t.gameObject);
            }
            else DestroyImmediate(t.gameObject);
        }
    }

    static void Resolve(ref Transform zone, string zoneName)
    {
        if (zone != null) return;
        var found = GameObject.Find(zoneName);
        if (found != null) zone = found.transform;
    }

    void PlaceZone(ref Transform zone, string zoneName, Vector2 centre)
    {
        Resolve(ref zone, zoneName);
        if (zone == null) { Debug.LogWarning("MapLoader: no object named " + zoneName + " found", this); return; }
        Vector3 p = zone.position;
        zone.position = new Vector3(transform.position.x + centre.x, p.y, transform.position.z + centre.y);   // loader at the origin: (centre.x, y, centre.y)
        Vector3 s = zone.localScale;
        zone.localScale = new Vector3(config.zoneSize, s.y, config.zoneSize);
    }

    Material GetMaterial()
    {
        if (obstacleMaterial != null) return obstacleMaterial;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        obstacleMaterial = new Material(shader) { color = new Color(0.55f, 0.55f, 0.55f) };
        return obstacleMaterial;
    }
}
