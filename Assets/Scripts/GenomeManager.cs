using Unity.Netcode;
using UnityEngine;

// Lives on the NetworkManager object. When the host starts, picks a random genome and applies it to the monster.
public class GenomeManager : MonoBehaviour
{
    [Tooltip("Genomes to pick from. Leave empty to use the built-in Sight/Hearing x Reacher set.")]
    public MonsterGenome[] genomes;
    [Tooltip("Off = only Reacher genomes (the giant) are picked.")]
    public bool allowClimber = true;
    [Tooltip("Testing: always pick a Climber genome.")]
    public bool alwaysClimber = false;

    public static MonsterGenome Current { get; private set; }

    bool applied;

    // Polls instead of using an event, so it can't miss the moment the host starts.
    void Update()
    {
        var nm = NetworkManager.Singleton;
        bool hosting = nm != null && nm.IsServer && nm.IsListening;
        if (hosting && !applied) { applied = true; PickAndApply(); }
        else if (!hosting) { applied = false; Current = null; }   // new genome next time we host; never a stale one
    }

    void PickAndApply()
    {
        if (genomes == null || genomes.Length == 0)
        {
            genomes = new[]
            {
                MonsterGenome.CreateDefault(GenomeSense.Sight, GenomeAccess.Reacher),
                MonsterGenome.CreateDefault(GenomeSense.Hearing, GenomeAccess.Reacher),
                MonsterGenome.CreateDefault(GenomeSense.Sight, GenomeAccess.Climber),
                MonsterGenome.CreateDefault(GenomeSense.Hearing, GenomeAccess.Climber),
            };
        }

        var pool = new System.Collections.Generic.List<MonsterGenome>();
        foreach (var g in genomes)
            if (g != null && (allowClimber || g.access != GenomeAccess.Climber) &&
                (!alwaysClimber || g.access == GenomeAccess.Climber)) pool.Add(g);
        if (pool.Count == 0) { Debug.LogWarning("GenomeManager: no usable genomes."); return; }

        Current = pool[Random.Range(0, pool.Count)];
        // Every monster gets the genome; only the one matching its Access shows up (Reacher = giant, Climber = climber)
        var giant = FindAnyObjectByType<MonsterAI>();
        var climber = FindAnyObjectByType<ClimberAI>();
        if (giant != null) giant.ApplyGenome(Current);
        if (climber != null) climber.ApplyGenome(Current);
        if (Current.access == GenomeAccess.Climber && climber == null)
            Debug.LogWarning("GenomeManager: Climber genome but no ClimberAI in scene (Tools > RunForYourLife > Climber Setup).");
        Debug.Log($"Genome picked: {Current.name} (sense {Current.sense}, access {Current.access}, " +
                  $"sight x{Current.sightRangeMultiplier}, hearing x{Current.hearingMultiplier})");
    }
}
