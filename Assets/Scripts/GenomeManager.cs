using System.Collections.Generic;
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

    [Header("Evolution (host save file)")]
    [Tooltip("Off = every genome is equally likely and nothing is saved.")]
    public bool useEvolution = true;
    [Tooltip("Weight added to each trait of a monster that won (killed everyone, or the host quit mid-run).")]
    public float winBoost = 0.5f;
    [Tooltip("Weight taken from each trait of a monster the players beat (survived to dawn).")]
    public float lossPenalty = 0.3f;
    [Tooltip("A trait's weight never drops below this, so a beaten trait can still come back.")]
    public float minWeight = 0.25f;
    [Tooltip("A trait's weight never goes above this, so one lineage can't take over completely.")]
    public float maxWeight = 4f;
    [Tooltip("Mutation floor: chance each run to ignore the weights and pick any genome evenly (keeps surprises).")]
    [Range(0f, 1f)] public float mutationChance = 0.15f;
    [Tooltip("Save file name in Application.persistentDataPath.")]
    public string saveFileName = "evolution.json";

    public static MonsterGenome Current { get; private set; }

    bool applied;
    bool resultRecorded;   // this run's win/loss is already saved

    // One saved trait weight, e.g. "Sense.Hearing" = 1.5. Traits not in the file start at 1.
    [System.Serializable] class TraitWeight { public string trait; public float weight = 1f; }
    [System.Serializable] class EvolutionSave { public int runs; public List<TraitWeight> traits = new List<TraitWeight>(); }
    EvolutionSave save;

    string SavePath => System.IO.Path.Combine(Application.persistentDataPath, saveFileName);
    static string SenseKey(GenomeSense s) => "Sense." + s;
    static string AccessKey(GenomeAccess a) => "Access." + a;

    // Polls instead of using an event, so it can't miss the moment the host starts.
    void Update()
    {
        var nm = NetworkManager.Singleton;
        bool hosting = nm != null && nm.IsServer && nm.IsListening;
        if (hosting && !applied) { applied = true; resultRecorded = false; PickAndApply(); }
        else if (hosting && !resultRecorded && Current != null && GameFlow.Instance != null && GameFlow.Instance.RunOver)
            RecordResult(GameFlow.Instance.Phase == GamePhase.Lost);   // Lost = everyone died = monster won
        else if (!hosting)
        {
            // The host left before dawn or a wipe: quitting/disconnecting counts as a monster win
            if (applied && !resultRecorded && Current != null) RecordResult(true);
            applied = false; Current = null;   // new genome next time we host; never a stale one
        }
    }

    void OnApplicationQuit()
    {
        if (applied && !resultRecorded && Current != null) RecordResult(true);
    }

    // ---- Evolution save ----

    void LoadSave()
    {
        save = null;
        try
        {
            if (System.IO.File.Exists(SavePath))
                save = JsonUtility.FromJson<EvolutionSave>(System.IO.File.ReadAllText(SavePath));
        }
        catch (System.Exception e) { Debug.LogWarning($"GenomeManager: couldn't read {SavePath}: {e.Message}"); }
        if (save == null) save = new EvolutionSave();
        if (save.traits == null) save.traits = new List<TraitWeight>();
    }

    void WriteSave()
    {
        try { System.IO.File.WriteAllText(SavePath, JsonUtility.ToJson(save, true)); }
        catch (System.Exception e) { Debug.LogWarning($"GenomeManager: couldn't write {SavePath}: {e.Message}"); }
    }

    TraitWeight Trait(string key)
    {
        foreach (var t in save.traits) if (t.trait == key) return t;
        var n = new TraitWeight { trait = key, weight = 1f };
        save.traits.Add(n);
        return n;
    }

    float Weight(MonsterGenome g) =>
        Mathf.Clamp(Trait(SenseKey(g.sense)).weight, minWeight, maxWeight) *
        Mathf.Clamp(Trait(AccessKey(g.access)).weight, minWeight, maxWeight);

    // Winning traits gain weight, beaten ones lose it (clamped between the floor and the cap)
    void RecordResult(bool monsterWon)
    {
        resultRecorded = true;
        if (!useEvolution) return;
        LoadSave();   // fresh from disk, in case another instance wrote it since the pick
        float delta = monsterWon ? winBoost : -lossPenalty;
        foreach (var key in new[] { SenseKey(Current.sense), AccessKey(Current.access) })
        {
            var t = Trait(key);
            t.weight = Mathf.Clamp(t.weight + delta, minWeight, maxWeight);
        }
        save.runs++;
        WriteSave();
        Debug.Log($"Evolution: {Current.name} {(monsterWon ? "won" : "was beaten")} -> " +
                  string.Join(", ", save.traits.ConvertAll(t => $"{t.trait} {t.weight:0.##}")) + $" (saved to {SavePath})");
    }

    // Weighted by the lineage's trait weights, except on a mutation roll (or with evolution off): any genome evenly
    MonsterGenome PickWeighted(List<MonsterGenome> pool)
    {
        if (!useEvolution || Random.value < mutationChance) return pool[Random.Range(0, pool.Count)];
        LoadSave();
        float total = 0f;
        foreach (var g in pool) total += Weight(g);
        float roll = Random.value * total;
        foreach (var g in pool)
        {
            roll -= Weight(g);
            if (roll <= 0f) return g;
        }
        return pool[pool.Count - 1];
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

        Current = PickWeighted(pool);
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
