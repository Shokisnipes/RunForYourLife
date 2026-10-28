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
    [Tooltip("This monster can't see at all (its type wasn't born with sight; see GenomeManager.startingSenses). " +
             "It only notices you right next to it, and its scouts are its eyes.")]
    public bool blind;

    public static MonsterGenome CreateDefault(GenomeSense sense, GenomeAccess access, bool canSee = true)
    {
        var g = CreateInstance<MonsterGenome>();
        g.name = canSee ? $"{sense}+{access}" : $"{sense}+{access} (blind)";
        g.sense = sense;
        g.access = access;
        bool sight = sense == GenomeSense.Sight;
        g.sightRangeMultiplier = !canSee ? 0f : sight ? 1.5f : 0.5f;
        g.hearingMultiplier = sight ? 0.4f : 2.5f;
        g.blind = !canSee;
        return g;
    }
}
