using UnityEngine;

// Tuning for CityGenerator: how big the city is, how the streets are cut up and what fills the blocks.
// Tools > RunForYourLife > City Preview makes Assets/Settings/CitySettings.asset.
[CreateAssetMenu(menuName = "RunForYourLife/City Settings", fileName = "CitySettings")]
public class CitySettings : ScriptableObject
{
    [Header("Size (meters)")]
    [Tooltip("The whole city, ring road included. The old hand-built map was about 70 x 110.")]
    public Vector2 citySize = new Vector2(150f, 190f);
    [Tooltip("Asphalt beyond the boundary wall, so the edge of the world hides in the fog.")]
    public float outerMargin = 40f;
    [Tooltip("Height of the wall around the city that keeps players in.")]
    public float boundaryWallHeight = 4f;

    [Header("Streets")]
    [Tooltip("The wide road through the middle of the city.")]
    public float mainRoadWidth = 14f;
    [Tooltip("Normal streets. Must stay wide enough for the giant (NavMesh radius 2.5).")]
    public float streetWidth = 10f;
    [Tooltip("Alley width range. Under 5 m the giant (radius 2.5) can't follow you in.")]
    public Vector2 alleyWidth = new Vector2(2.6f, 3.6f);
    [Tooltip("Blocks are cut until both sides are shorter than this.")]
    public float maxBlockSize = 40f;
    [Tooltip("No block side gets shorter than this.")]
    public float minBlockSize = 22f;
    [Range(0f, 1f), Tooltip("Chance a block is cut in two by an alley.")]
    public float alleyChance = 0.45f;
    [Range(0f, 1f), Tooltip("Chance an alley is walled off at one end (dead end).")]
    public float deadEndChance = 0.4f;
    public float sidewalkWidth = 2f;

    [Header("Open areas (whole blocks)")]
    [Tooltip("The block nearest the centre becomes a paved plaza with a fountain.")]
    public bool plaza = true;
    public int parks = 2;
    public int parkingLots = 1;
    public int emptyBlocks = 1;

    [Header("Buildings")]
    [Tooltip("Relative chance of each kind for a lot (House, Shop, Warehouse).")]
    public Vector3 kindWeights = new Vector3(0.5f, 0.3f, 0.2f);
    [Tooltip("Relative chance of an Eastern European apartment block (3-4 storeys; needs a lot 14 m wide, 10 m deep).")]
    public float apartmentWeight = 0.3f;
    [Range(0f, 1f), Tooltip("Chance a lot along a street is left empty (rubble, a dumpster).")]
    public float emptyLotChance = 0.1f;
    [Range(0f, 1f), Tooltip("Chance the gap after a building is wide enough to walk through.")]
    public float passageChance = 0.3f;
    public BuildingPalette palette;

    [Header("Fences (the giant steps over them; they stop players and the Climber)")]
    [Tooltip("Iron railings round parks, with an open gate where each path comes in.")]
    public bool parkFences = true;
    [Tooltip("Wire fencing round parking lots, with two wide openings.")]
    public bool parkingFences = true;
    [Range(0f, 1f), Tooltip("Chance a walk-through gap next to a shop or warehouse gets a wire fence and an open gate.")]
    public float shopPassageFenceChance = 0.6f;
    [Range(0f, 1f), Tooltip("Same for gaps next to other buildings.")]
    public float passageFenceChance = 0.2f;

    [Header("Street lamps")]
    [Tooltip("Made from a scene lamp by the City Preview tool (Assets/Prefabs/StreetLamp.prefab).")]
    public GameObject streetLampPrefab;
    public float lampSpacing = 22f;
    [Range(0f, 1f), Tooltip("Chance each lamp spot actually gets a lamp.")]
    public float lampChance = 0.85f;
    [Range(0f, 1f)] public float brokenLampChance = 0.15f;
    [Range(0f, 1f)] public float flickeringLampChance = 0.12f;

    [Header("Roads (deserted city)")]
    [Tooltip("Faded lane lines and crosswalks.")]
    public bool roadMarkings = true;
    [Range(0f, 1f), Tooltip("Chance each street end gets a crosswalk.")]
    public float crosswalkChance = 0.6f;
    [Tooltip("Litter pieces (paper, bags, cans, tires, planks, scrap, drums) per 100 m2 of road. Alleys get double.")]
    public float litterDensity = 1.2f;
    [Tooltip("Cracks and puddles per 100 m2 of road.")]
    public float crackDensity = 0.5f;
    [Tooltip("Wrecked cars per 100 m of main road (only the main road is wide enough to leave the giant a way past).")]
    public float wrecksPer100m = 2f;

    [Header("Ground and props (empty = plain colour)")]
    public Material groundMaterial;
    public Material pavingMaterial;
    public Material grassMaterial;
    public Material dirtMaterial;
}
