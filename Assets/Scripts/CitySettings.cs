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
    [Range(0f, 1f), Tooltip("Chance a lot along a street is left empty (rubble, a dumpster).")]
    public float emptyLotChance = 0.1f;
    [Range(0f, 1f), Tooltip("Chance the gap after a building is wide enough to walk through.")]
    public float passageChance = 0.3f;
    public BuildingPalette palette;

    [Header("Street lamps")]
    [Tooltip("Made from a scene lamp by the City Preview tool (Assets/Prefabs/StreetLamp.prefab).")]
    public GameObject streetLampPrefab;
    public float lampSpacing = 22f;
    [Range(0f, 1f), Tooltip("Chance each lamp spot actually gets a lamp.")]
    public float lampChance = 0.85f;
    [Range(0f, 1f)] public float brokenLampChance = 0.15f;
    [Range(0f, 1f)] public float flickeringLampChance = 0.12f;

    [Header("Ground and props (empty = plain colour)")]
    public Material groundMaterial;
    public Material pavingMaterial;
    public Material grassMaterial;
    public Material dirtMaterial;
}
