using System.Collections.Generic;
using UnityEngine;

// On each car prefab made by Tools > RunForYourLife > Vehicle Setup. CityGenerator parks them around the city as cover:
// it gives the paint materials a random colour and rusts the whole car when it's a wreck.
public class Vehicle : MonoBehaviour
{
    [Tooltip("Width, height and length in metres (same as the box collider). Used to keep it inside a parking stall or by the curb.")]
    public Vector3 size;
    [Tooltip("Materials that get a random paint colour when the car is parked. Empty = the car keeps its own colours.")]
    public Material[] paint;
    [Tooltip("Too big for a parking stall (pickups, trucks, vans): only parked by the main road and on empty lots.")]
    public bool big;

    // Every parked car in the current city (CarAlarms picks which ones have a working alarm)
    public static readonly List<Vehicle> All = new List<Vehicle>();
    // Set by CityGenerator: a rusted wreck (dead battery, never has an alarm)
    [HideInInspector] public bool wreck;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);
}
