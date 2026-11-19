// SensorValidation: checks the ultrasonic sensor model against a flat wall. For every noise level in SimConfig and every
// test distance it places the wall that far in front of one sensor, takes N readings and writes results/sensor_validation.csv
// (noise, true_m, mean_m, sd_m, dropout_rate). mean_m and sd_m use the NON-dropout readings (dropouts are counted
// separately in dropout_rate). All noise levels use the same seed, so dropouts fall on the same readings in each.
// A distance beyond maxRange (4.50 m) is out of range: the sensor sees no wall, so every reading is exactly maxRange
// (4.0 m) with no noise, and no dropouts are counted (there is no echo to lose).
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public class SensorValidation : MonoBehaviour
{
    public SimConfig config;
    public UltrasonicSensor sensor;          // points along +Z
    public Transform wall;                   // flat wall, thin along Z; its front face is placed at the test distance

    public float[] distances = { 0.05f, 0.10f, 0.20f, 0.50f, 1.00f, 2.00f, 3.00f, 4.50f };
    public int readingsPerPoint = 100;
    public int seed = 12345;
    public bool runOnStart = true;
    public string outputPath = "results/sensor_validation.csv";   // relative to the project folder

    void Start()
    {
        if (runOnStart) Run();
    }

    [ContextMenu("Run Validation")]
    public void Run()
    {
        if (config == null || sensor == null || wall == null) { Debug.LogError("SensorValidation: assign config, sensor and wall"); return; }

        var csv = new StringBuilder("noise,true_m,mean_m,sd_m,dropout_rate\n");
        var inv = CultureInfo.InvariantCulture;
        var log = new StringBuilder("SensorValidation (seed " + seed + ", " + readingsPerPoint + " readings per point)\n");

        foreach (float noise in config.noiseLevels)
        {
            var rng = new System.Random(seed);                     // same seed for every noise level
            sensor.Initialize(config, rng, noise);

            foreach (float d in distances)
            {
                PlaceWall(d);

                var valid = new List<double>();
                int dropouts = 0;
                for (int i = 0; i < readingsPerPoint; i++)
                {
                    sensor.MeasureNow();
                    if (sensor.LastWasDropout) dropouts++;
                    else valid.Add(sensor.LastReading);
                }

                double mean = 0, sd = 0;
                foreach (double v in valid) mean += v;
                if (valid.Count > 0) mean /= valid.Count;
                if (valid.Count > 1)
                {
                    foreach (double v in valid) sd += (v - mean) * (v - mean);
                    sd = System.Math.Sqrt(sd / (valid.Count - 1));  // sample standard deviation
                }
                double dropoutRate = (double)dropouts / readingsPerPoint;

                // sanity check of the test geometry: the sensor's ground truth must equal the placed distance (up to maxRange)
                float expectedTrue = Mathf.Min(d, config.maxRange);
                if (Mathf.Abs(sensor.LastTrueDistance - expectedTrue) > 0.001f)
                    Debug.LogWarning("SensorValidation: at " + d + " m the sensor's true distance is " + sensor.LastTrueDistance);

                csv.Append(noise.ToString("0.00", inv)).Append(',')
                   .Append(d.ToString("0.00", inv)).Append(',')
                   .Append(mean.ToString("0.0000", inv)).Append(',')
                   .Append(sd.ToString("0.0000", inv)).Append(',')
                   .Append(dropoutRate.ToString("0.00", inv)).Append('\n');
                log.Append("noise " + noise.ToString("0.00", inv) + "  true " + d.ToString("0.00", inv) + " m  mean " + mean.ToString("0.0000", inv) +
                           "  sd " + sd.ToString("0.0000", inv) + "  dropout " + dropoutRate.ToString("0.00", inv) + "\n");
            }
        }

        string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, csv.ToString());
        Debug.Log(log.ToString() + "Saved " + full);
    }

    // wall front face at z = d (sensor at the origin looking along +Z); wall is thin along Z
    void PlaceWall(float d)
    {
        var p = wall.position;
        wall.position = new Vector3(p.x, p.y, d + wall.localScale.z * 0.5f);
        Physics.SyncTransforms();                                   // make the physics engine see the moved wall now
    }
}
