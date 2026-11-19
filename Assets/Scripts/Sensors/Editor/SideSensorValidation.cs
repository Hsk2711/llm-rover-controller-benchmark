// SideSensorValidation: Editor-only. Re-runs the Phase 2 accuracy check for the two SIDE sensors (+90 / -90 deg) as they are
// mounted on the real rover. It builds the rover prefab in a throw-away scene far from the arena, puts a flat test wall in
// front of each side sensor at every test distance, takes 100 readings per point for every noise level in SimConfig (seed
// 12345, same as Phase 2) and writes results/sensor_validation_side.csv (sensor, noise, true_m, mean_m, sd_m, dropout_rate).
// mean_m and sd_m use the non-dropout readings; a wall beyond max range gives exactly max range and no dropouts (as in Phase 2).
// Menu: Rover > Validate Side Sensors.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class SideSensorValidation
{
    static readonly float[] Distances = { 0.05f, 0.10f, 0.20f, 0.50f, 1.00f, 2.00f, 3.00f, 4.50f };
    const int ReadingsPerPoint = 100;
    const int Seed = 12345;

    [MenuItem("Rover/Validate Side Sensors")]
    static void MenuRun() { Debug.Log(Run()); }

    public static string Run()
    {
        var cfg = AssetDatabase.LoadAssetAtPath<SimConfig>("Assets/SimConfig.asset");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Rover.prefab");
        if (cfg == null || prefab == null) return "SideSensorValidation: SimConfig or Rover prefab not found";

        Scene previous = SceneManager.GetActiveScene();
        Scene temp = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        var inv = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("sensor,noise,true_m,mean_m,sd_m,dropout_rate\n");
        var log = new StringBuilder();
        try
        {
            var rover = (GameObject)PrefabUtility.InstantiatePrefab(prefab, temp);
            rover.transform.SetPositionAndRotation(new Vector3(1000f, 0.045f, 1000f), Quaternion.identity);   // far from the arena
            var drive = rover.GetComponent<RoverDrive>();
            if (drive.config == null) drive.config = cfg;
            drive.ApplyConfig();                                   // sizes the body and mounts the five sensors
            var rig = rover.GetComponent<SensorRig>();
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(wall, temp);
            wall.transform.localScale = new Vector3(2.0f, 1.0f, 0.1f);

            var sensors = new List<(string name, UltrasonicSensor s)> { ("leftSide", rig.leftSide), ("rightSide", rig.rightSide) };
            foreach (var (name, sensor) in sensors)
            {
                Vector3 local = rover.transform.InverseTransformPoint(sensor.transform.position);
                Vector3 look = rover.transform.InverseTransformDirection(sensor.transform.forward);
                log.AppendLine(name + " mounted at rover-local (" + local.x.ToString("0.000", inv) + ", " + local.y.ToString("0.000", inv) + ", " +
                               local.z.ToString("0.000", inv) + "), looking along (" + look.x.ToString("0.0", inv) + ", " + look.y.ToString("0.0", inv) + ", " + look.z.ToString("0.0", inv) + ")");
                foreach (float noise in cfg.noiseLevels)
                {
                    sensor.Initialize(cfg, new System.Random(Seed), noise);        // same seed for every noise level, as in Phase 2
                    foreach (float d in Distances)
                    {
                        // wall square-on to the sensor: its front face is d metres in front of the sensor
                        wall.transform.rotation = sensor.transform.rotation;
                        wall.transform.position = sensor.transform.position + sensor.transform.forward * (d + 0.05f);
                        Physics.SyncTransforms();

                        var valid = new List<double>();
                        int dropouts = 0;
                        for (int i = 0; i < ReadingsPerPoint; i++)
                        {
                            sensor.MeasureNow();
                            if (sensor.LastWasDropout) dropouts++;
                            else valid.Add(sensor.LastReading);
                        }
                        float expectedTrue = Mathf.Min(d, cfg.maxRange);
                        if (Mathf.Abs(sensor.LastTrueDistance - expectedTrue) > 0.001f)
                            log.AppendLine("WARNING " + name + " at " + d + " m: true distance seen = " + sensor.LastTrueDistance);

                        double mean = 0, sd = 0;
                        foreach (double v in valid) mean += v;
                        if (valid.Count > 0) mean /= valid.Count;
                        if (valid.Count > 1)
                        {
                            foreach (double v in valid) sd += (v - mean) * (v - mean);
                            sd = System.Math.Sqrt(sd / (valid.Count - 1));
                        }
                        csv.Append(name).Append(',').Append(noise.ToString("0.00", inv)).Append(',').Append(d.ToString("0.00", inv)).Append(',')
                           .Append(mean.ToString("0.0000", inv)).Append(',').Append(sd.ToString("0.0000", inv)).Append(',')
                           .Append(((double)dropouts / ReadingsPerPoint).ToString("0.00", inv)).Append('\n');
                    }
                }
            }
            Object.DestroyImmediate(wall);
            Object.DestroyImmediate(rover);
        }
        finally
        {
            EditorSceneManager.CloseScene(temp, true);
            if (previous.IsValid()) SceneManager.SetActiveScene(previous);
        }

        string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "results", "sensor_validation_side.csv"));
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, csv.ToString());
        return log + "Saved " + full;
    }
}
