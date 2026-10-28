using Unity.Netcode;
using UnityEngine;

public enum GamePhase { Prep, Night, Won, Lost }

// Host-run, synced run timer. Prep (monster asleep) -> Night -> dawn (win) or nobody left free (lose: everyone
// is dead or lying hidden, so there's no one to come and free them).
// Place ONE of these on a scene object that also has a NetworkObject.
public class GameFlow : NetworkBehaviour
{
    [Header("Timers (seconds)")]
    [Tooltip("How long the players get to prepare. The monster is disabled during this time.")]
    public float prepSeconds = 360f;
    [Tooltip("How long the night lasts. Survive until dawn to win.")]
    public float nightSeconds = 600f;

    public static GameFlow Instance { get; private set; }

    readonly NetworkVariable<GamePhase> phase = new NetworkVariable<GamePhase>(GamePhase.Prep);
    // Network time at which the current timed phase ends
    readonly NetworkVariable<double> phaseEndsAt = new NetworkVariable<double>(0);

    public GamePhase Phase => phase.Value;
    public bool MonsterActive => !IsSpawned || phase.Value == GamePhase.Night;
    public bool RunOver => IsSpawned && (phase.Value == GamePhase.Won || phase.Value == GamePhase.Lost);

    public float SecondsLeft => Mathf.Max(0f, (float)(phaseEndsAt.Value - NetworkManager.ServerTime.Time));

    void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        phase.Value = GamePhase.Prep;
        phaseEndsAt.Value = NetworkManager.ServerTime.Time + prepSeconds;
        Debug.Log($"GameFlow: prep phase started ({prepSeconds:0}s)");
    }

    void Update()
    {
        if (!IsServer || !IsSpawned) return;

        if (phase.Value == GamePhase.Prep && NetworkManager.ServerTime.Time >= phaseEndsAt.Value)
        {
            phase.Value = GamePhase.Night;
            phaseEndsAt.Value = NetworkManager.ServerTime.Time + nightSeconds;
            Debug.Log($"GameFlow: night begins ({nightSeconds:0}s)");
        }
        else if (phase.Value == GamePhase.Night)
        {
            if (NetworkManager.ServerTime.Time >= phaseEndsAt.Value)
            {
                phase.Value = GamePhase.Won;
                Debug.Log("GameFlow: dawn - players win");
            }
            else if (NobodyFree())
            {
                phase.Value = GamePhase.Lost;
                Debug.Log("GameFlow: every player is dead or hidden - players lose");
            }
        }
    }

    // Someone still in a monster's hand isn't counted out yet: the carry to the hiding spot plays out first
    static bool NobodyFree()
    {
        int count = 0;
        foreach (var p in FirstPersonController.All)
        {
            if (!p.IsSpawned) continue;
            count++;
            if (!p.IsDead && !p.IsHidden) return false;
        }
        return count > 0;
    }
}
