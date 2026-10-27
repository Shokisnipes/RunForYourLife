using UnityEngine;

public enum GenomeSense { Sight, Hearing }
public enum GenomeAccess { Reacher, Climber }   // Reacher = the giant, Climber = the climber

// One monster "recipe": how it perceives and how it gets at players. GenomeManager picks one per run.
[CreateAssetMenu(menuName = "RunForYourLife/Monster Genome", fileName = "MonsterGenome")]
public class MonsterGenome : ScriptableObject
{
    public GenomeSense sense = GenomeSense.Sight;
    public GenomeAccess access = GenomeAccess.Reacher;

    [Tooltip("Multiplies all of MonsterAI's sight ranges. High for Sight, low for Hearing.")]
    public float sightRangeMultiplier = 1.5f;
    [Tooltip("Sets MonsterAI.hearingMultiplier. Low for Sight, high for Hearing.")]
    public float hearingMultiplier = 0.4f;

    public static MonsterGenome CreateDefault(GenomeSense sense, GenomeAccess access)
    {
        var g = CreateInstance<MonsterGenome>();
        g.name = $"{sense}+{access}";
        g.sense = sense;
        g.access = access;
        bool sight = sense == GenomeSense.Sight;
        g.sightRangeMultiplier = sight ? 1.5f : 0.5f;
        g.hearingMultiplier = sight ? 0.4f : 2.5f;
        return g;
    }
}
