using Unity.Netcode;
using UnityEngine;

// Lives on the NetworkManager object. When the host starts, picks a random genome and applies it to the monster.
public class GenomeManager : MonoBehaviour
{
    [Tooltip("Genomes to pick from. Leave empty to use the built-in Sight/Hearing x Reacher set.")]
    public MonsterGenome[] genomes;
    [Tooltip("Climber is not built yet (task 4); keep off so the giant never rolls it.")]
    public bool allowClimber = false;

    public static MonsterGenome Current { get; private set; }

    bool applied;

    // Polls instead of using an event, so it can't miss the moment the host starts.
    void Update()
    {
        var nm = NetworkManager.Singleton;
        bool hosting = nm != null && nm.IsServer && nm.IsListening;
        if (hosting && !applied) { applied = true; PickAndApply(); }
        else if (!hosting) applied = false;   // new genome next time we host
    }

    void PickAndApply()
    {
        if (genomes == null || genomes.Length == 0)
        {
            genomes = new[]
            {
                MonsterGenome.CreateDefault(GenomeSense.Sight, GenomeAccess.Reacher),
                MonsterGenome.CreateDefault(GenomeSense.Hearing, GenomeAccess.Reacher),
            };
        }

        var pool = new System.Collections.Generic.List<MonsterGenome>();
        foreach (var g in genomes)
            if (g != null && (allowClimber || g.access != GenomeAccess.Climber)) pool.Add(g);
        if (pool.Count == 0) { Debug.LogWarning("GenomeManager: no usable genomes."); return; }

        Current = pool[Random.Range(0, pool.Count)];
        var monster = FindAnyObjectByType<MonsterAI>();
        if (monster == null) { Debug.LogWarning("GenomeManager: no MonsterAI in scene."); return; }

        monster.ApplyGenome(Current);
        Debug.Log($"Genome picked: {Current.name} (sense {Current.sense}, access {Current.access}, " +
                  $"sight x{Current.sightRangeMultiplier}, hearing x{Current.hearingMultiplier})");
    }
}
