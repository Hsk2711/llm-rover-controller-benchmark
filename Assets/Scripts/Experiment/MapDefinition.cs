// MapDefinition: a data asset describing ONE test map: a list of box obstacles plus the centres of the start zone and
// the goal zone. MapLoader turns it into real objects in the arena. Coordinates are metres on the floor, in the same
// frame as the arena: x = east (right), z = north (up on the top-down view), arena centre = (0, 0), so the arena
// spans -3..+3 on both axes. Each Vector2 below is (x, z).
using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public struct BoxObstacle
{
    public Vector2 position;     // centre of the box (x, z)
    public float rotationDeg;    // turn about the vertical axis; positive = counter-clockwise seen from above (= LEFT)
    public Vector2 size;         // (length along the box's own x axis, length along its own z axis), before rotation
}

[CreateAssetMenu(fileName = "NewMap", menuName = "RoverNavStudy/MapDefinition")]
public class MapDefinition : ScriptableObject
{
    public Vector2 startZoneCentre = new Vector2(-2.5f, -2.5f);   // bottom-left
    public Vector2 goalZoneCentre = new Vector2(2.5f, 2.5f);      // top-right
    public List<BoxObstacle> obstacles = new List<BoxObstacle>();
}
