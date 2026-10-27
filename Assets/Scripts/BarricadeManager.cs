using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Lets players board up doorways during prep. Hold E next to a doorway to nail up a plank.
// A doorway with enough boards is blocked: the giant can't reach through it (see ReachPoint.Blocked).
// Switched off for now (boardDoors): E opens and closes doors (DoorManager); boards will go on windows later.
// Place ONE of these on the same scene object as GameFlow (it needs the NetworkObject).
public class BarricadeManager : NetworkBehaviour
{
    [Tooltip("Off: no boarding doors (E is the door key now). Boards move to windows later.")]
    public bool boardDoors = false;

    [Header("Planks")]
    [Tooltip("Total planks the team has for the whole prep phase.")]
    public int totalPlanks = 12;
    [Tooltip("Boards needed to fully block one doorway.")]
    public int boardsPerDoor = 3;

    [Header("Interaction")]
    [Tooltip("Seconds you hold E to nail up one plank.")]
    public float holdSeconds = 1.5f;
    [Tooltip("How close you must be to the doorway (metres, measured to the middle of the opening).")]
    public float interactRange = 3.5f;

    // Boards on each doorway, indexed by ReachPoint order (sorted by position so every machine agrees)
    readonly NetworkList<byte> boards = new NetworkList<byte>();
    readonly NetworkVariable<int> planksLeft = new NetworkVariable<int>(0);

    ReachPoint[] doors = new ReachPoint[0];
    float holdTime;
    int holdDoor = -1;
    bool holdRemoving;

    public static BarricadeManager Instance { get; private set; }

    void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        RefreshDoors();
        if (IsServer)
        {
            planksLeft.Value = totalPlanks;
            boards.Clear();
            for (int i = 0; i < doors.Length; i++) boards.Add(0);
        }
        boards.OnListChanged += OnBoardsChanged;
        CityManager.Built += OnCityBuilt;
        for (int i = 0; i < doors.Length && i < boards.Count; i++) doors[i].SetBoards(boards[i], boardsPerDoor);
    }

    // A client built its copy of the city after we spawned: pick up the new doorways
    void OnCityBuilt()
    {
        RefreshDoors();
        OnBoardsChanged(default);
    }

    public override void OnNetworkDespawn()
    {
        boards.OnListChanged -= OnBoardsChanged;
        CityManager.Built -= OnCityBuilt;
        foreach (var d in doors) if (d != null) d.SetBoards(0, boardsPerDoor);
    }

    // Same order on every machine: sort by position
    void RefreshDoors()
    {
        doors = ReachPoint.All.ToArray();
        System.Array.Sort(doors, (a, b) =>
        {
            Vector3 p = a.transform.position, q = b.transform.position;
            int c = p.x.CompareTo(q.x);
            if (c != 0) return c;
            c = p.z.CompareTo(q.z);
            return c != 0 ? c : p.y.CompareTo(q.y);
        });
    }

    void OnBoardsChanged(NetworkListEvent<byte> e)
    {
        for (int i = 0; i < doors.Length && i < boards.Count; i++) doors[i].SetBoards(boards[i], boardsPerDoor);
    }

    void Update()
    {
        if (!boardDoors || !IsSpawned || GameFlow.Instance == null || GameFlow.Instance.RunOver) { holdTime = 0f; return; }
        bool prep = GameFlow.Instance.Phase == GamePhase.Prep;

        var me = LocalPlayer();
        var kb = Keyboard.current;
        int door = (me == null || me.IsDead || NetworkUI.MenuOpen) ? -1 : NearestDoor(me.transform.position);

        // E adds a plank (prep only), R tears one off (any time, so nobody gets trapped inside)
        bool adding = prep && door >= 0 && kb != null && kb.eKey.isPressed && planksLeft.Value > 0 && boards[door] < boardsPerDoor;
        bool removing = !adding && door >= 0 && kb != null && kb.rKey.isPressed && boards[door] > 0;
        bool holding = adding || removing;
        if (!holding || door != holdDoor || removing != holdRemoving) holdTime = 0f;
        holdDoor = holding ? door : -1;
        holdRemoving = removing;
        if (!holding) return;

        holdTime += Time.deltaTime;
        if (holdTime >= holdSeconds)
        {
            holdTime = 0f;
            ChangeBoardServerRpc(door, removing);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ChangeBoardServerRpc(int door, bool remove)
    {
        if (door < 0 || door >= boards.Count || GameFlow.Instance == null || GameFlow.Instance.RunOver) return;
        if (remove)
        {
            if (boards[door] == 0) return;
            planksLeft.Value++;   // the plank goes back to the pile
            boards[door] = (byte)(boards[door] - 1);
            return;
        }
        if (GameFlow.Instance.Phase != GamePhase.Prep) return;
        if (planksLeft.Value <= 0 || boards[door] >= boardsPerDoor) return;
        planksLeft.Value--;
        boards[door] = (byte)(boards[door] + 1);
    }

    int NearestDoor(Vector3 pos)
    {
        int best = -1;
        float bestDist = interactRange;
        for (int i = 0; i < doors.Length && i < boards.Count; i++)
        {
            float d = Vector3.Distance(pos, doors[i].DoorPoint);
            if (d > bestDist) continue;
            best = i;
            bestDist = d;
        }
        return best;
    }

    static FirstPersonController LocalPlayer()
    {
        foreach (var p in FirstPersonController.All) if (p.IsOwner) return p;
        return null;
    }

    // Small hint + progress bar near the bottom of the screen
    void OnGUI()
    {
        if (!boardDoors || !IsSpawned || GameFlow.Instance == null || GameFlow.Instance.RunOver) return;
        bool prep = GameFlow.Instance.Phase == GamePhase.Prep;
        var me = LocalPlayer();
        if (me == null || me.IsDead || NetworkUI.MenuOpen) return;

        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18 };
        style.normal.textColor = Color.white;
        float w = 420f, y = Screen.height - 110f, x = (Screen.width - w) / 2f;

        // Plank counter: bottom-left corner
        var left = new GUIStyle(style) { alignment = TextAnchor.MiddleLeft };
        GUI.Label(new Rect(16f, Screen.height - 40f, 240f, 24f), $"Planks left: {planksLeft.Value}", left);

        int door = NearestDoor(me.transform.position);
        if (door < 0) return;
        string msg;
        if (boards[door] >= boardsPerDoor) msg = "Doorway boarded up - hold R to tear a plank off";
        else if (prep && planksLeft.Value > 0)
            msg = $"Hold E to board up ({boards[door]}/{boardsPerDoor})" + (boards[door] > 0 ? " - hold R to remove" : "");
        else if (boards[door] > 0) msg = "Hold R to tear a plank off";
        else msg = prep ? "No planks left" : "";
        if (msg.Length == 0) return;
        GUI.Label(new Rect(x, y, w, 26f), msg, style);
        if (holdTime > 0f)
        {
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(x + 60f, y + 30f, w - 120f, 8f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(x + 60f, y + 30f, (w - 120f) * Mathf.Clamp01(holdTime / holdSeconds), 8f), Texture2D.whiteTexture);
        }
    }
}
