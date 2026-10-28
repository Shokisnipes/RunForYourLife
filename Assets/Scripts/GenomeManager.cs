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

    // Which senses a monster type has. A sense it lacks is never picked for it; one that can't see is blind.
    // Evolution (later) can grant a missing sense to a lineage as a mutation by ticking it here for that run.
    [System.Serializable]
    public class StartingSenses
    {
        public GenomeAccess access;
        public bool canSee = true;
        public bool canHear = true;
    }

    [Header("Senses each monster is born with")]
    [Tooltip("The giant sees and hears (the genome makes one strong, one weak). The Climber is blind: it hunts by " +
             "sound, only feels you right next to it, and its scouts are its eyes.")]
    public StartingSenses[] startingSenses =
    {
        new StartingSenses { access = GenomeAccess.Reacher, canSee = true, canHear = true },
        new StartingSenses { access = GenomeAccess.Climber, canSee = false, canHear = true },
    };

    [Header("Scouts")]
    [Tooltip("On = tiny scouts spawn at dusk, share the monster's genome and relay where players are (task 6).")]
    public bool allowScouts = false;
    [Tooltip("How many scouts spawn each run (random in this range, inclusive).")]
    public Vector2Int scoutCount = new Vector2Int(5, 6);

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

    StartingSenses SensesOf(GenomeAccess access)
    {
        if (startingSenses != null)
            foreach (var s in startingSenses) if (s != null && s.access == access) return s;
        return new StartingSenses { access = access };   // not listed: sees and hears
    }

    bool HasSense(MonsterGenome g)
    {
        var s = SensesOf(g.access);
        return g.sense == GenomeSense.Sight ? s.canSee : s.canHear;
    }

    void PickAndApply()
    {
        // Built-in set: every sense each monster type has (a blind type gets no Sight genome)
        bool builtIn = genomes == null || genomes.Length == 0;
        var list = builtIn ? new System.Collections.Generic.List<MonsterGenome>() : new System.Collections.Generic.List<MonsterGenome>(genomes);
        if (builtIn)
            foreach (GenomeAccess access in System.Enum.GetValues(typeof(GenomeAccess)))
            {
                var s = SensesOf(access);
                if (s.canSee) list.Add(MonsterGenome.CreateDefault(GenomeSense.Sight, access));
                if (s.canHear) list.Add(MonsterGenome.CreateDefault(GenomeSense.Hearing, access, s.canSee));
            }

        var pool = new System.Collections.Generic.List<MonsterGenome>();
        foreach (var g in list)
            if (g != null && HasSense(g) && (allowClimber || g.access != GenomeAccess.Climber) &&
                (!alwaysClimber || g.access == GenomeAccess.Climber)) pool.Add(g);
        if (pool.Count == 0) { Debug.LogWarning("GenomeManager: no usable genomes."); return; }

        Current = pool[Random.Range(0, pool.Count)];
        if (!SensesOf(Current.access).canSee && !Current.blind)
        {
            // A hand-made genome for a monster type that can't see: make a blind copy (never edit the asset)
            Current = Instantiate(Current);
            Current.sightRangeMultiplier = 0f;
            Current.blind = true;
        }
        // Every monster gets the genome; only the one matching its Access shows up (Reacher = giant, Climber = climber)
        var giant = FindAnyObjectByType<MonsterAI>();
        var climber = FindAnyObjectByType<ClimberAI>();
        if (giant != null) giant.ApplyGenome(Current);
        if (climber != null) climber.ApplyGenome(Current);
        if (Current.access == GenomeAccess.Climber && climber == null)
            Debug.LogWarning("GenomeManager: Climber genome but no ClimberAI in scene (Tools > RunForYourLife > Climber Setup).");
        Debug.Log($"Genome picked: {Current.name} (sense {Current.sense}, access {Current.access}, blind {Current.blind}, " +
                  $"sight x{Current.sightRangeMultiplier}, hearing x{Current.hearingMultiplier})");
    }
}
