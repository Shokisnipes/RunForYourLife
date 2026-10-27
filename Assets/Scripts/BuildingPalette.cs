using UnityEngine;

// The materials BuildingBuilder puts on a building. The game can't load materials by path at runtime, so the city
// generator holds one of these. Building Texturer > Use Texture Set fills Assets/Materials/BuildingPalette.asset.
[CreateAssetMenu(menuName = "RunForYourLife/Building Palette", fileName = "BuildingPalette")]
public class BuildingPalette : ScriptableObject
{
    [Tooltip("Outside walls. Each building gets one at random.")]
    public Material[] wallMaterials;
    public Material roofMaterial;
    public Material floorMaterial;
    [Tooltip("Inside walls. Empty = use the building's outside wall material.")]
    public Material interiorWallMaterial;
    public Material sidewalkMaterial;

    [Header("Windows and props")]
    public Material windowFrame;
    public Material windowDark;
    public Material windowLit;
    public Material propMetal;

    [Tooltip("How many meters one copy of a wall/roof/floor texture covers (BoxUV).")]
    public float metersPerTile = 2.5f;
}
