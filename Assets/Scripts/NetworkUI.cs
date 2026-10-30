using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

// Main menu + pause menu. Sits on the NetworkManager object and builds its own UI at startup.
// Main menu: HOST / JOIN / QUIT over the foggy street. In game: Esc opens the pause menu.
// Online play goes through Unity Relay: HOST gets a join code (copied to the clipboard, shown in the pause menu),
// friends type it into JOIN. Typing an IP address instead connects directly (LAN / 127.0.0.1 testing).
public class NetworkUI : MonoBehaviour
{
    public static bool MenuOpen { get; private set; }

    [Header("Connection")]
    public string address = "127.0.0.1";
    public ushort port = 7777;
    [Tooltip("Host through Unity Relay (join codes, works over the internet). Off = direct IP only.")]
    public bool useRelay = true;
    [Tooltip("How many friends can join a Relay game (the host doesn't count).")]
    public int maxFriends = 3;

    [Header("Look")]
    public string title = "RUN FOR YOUR LIFE";
    public string subtitle = "it can see over the rooftops";
    [Tooltip("Optional: drag in a .ttf/.otf font for the title (e.g. a typewriter or horror font).")]
    public Font titleFont;
    [Tooltip("Optional: font for buttons and text.")]
    public Font bodyFont;
    public Color textColor = new Color(0.82f, 0.82f, 0.8f);
    public Color accentColor = new Color(0.8f, 0.12f, 0.1f);
    public bool driftLobbyCamera = true;

    GameObject mainPanel, joinPanel, pausePanel, statusPanel;
    Text statusText, pauseInfo, mainMessage, timerText;
    InputField addressField;
    Font defaultFont;
    bool connecting;
    string joinCode = "";      // Relay code of the game we're hosting (empty = direct IP)
    string statusWord = "CONNECTING";
    int attempt;               // bumped by CANCEL so a Relay request that finishes later is ignored
    GameObject copyButton;

    // ---------- Lifecycle ----------

    void Start()
    {
        defaultFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        BuildUI();
        ShowOnly(mainPanel);

        var nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
        }

        if (driftLobbyCamera)
        {
            foreach (var cam in Camera.allCameras)
                if (cam.GetComponentInParent<FirstPersonController>() == null && cam.GetComponent<LobbyCameraDrift>() == null)
                    cam.gameObject.AddComponent<LobbyCameraDrift>();
        }

        // Testing from the command line: Game.exe -host, or Game.exe -join 192.168.1.20 (or a join code)
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-host") Host();
            else if (args[i] == "-join")
            {
                addressField.text = i + 1 < args.Length ? args[i + 1] : "127.0.0.1";
                Join();
            }
        }
    }

    void OnDestroy()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnClientConnectedCallback -= OnClientConnected;
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
        }
        MenuOpen = false;
    }

    void Update()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        bool inGame = nm.IsServer || nm.IsConnectedClient;

        if (inGame && !endUi && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            if (pausePanel.activeSelf) Resume();
            else OpenPause();
        }

        if (pausePanel.activeSelf)
        {
            string join = joinCode != "" ? $"JOIN CODE: {joinCode}" : $"Friends join with: {LocalIP()}";
            string info = nm.IsHost ? $"Hosting  ·  {nm.ConnectedClientsIds.Count} player(s)\n{join}" : "Connected";
            pauseInfo.text = info;
            copyButton.SetActive(nm.IsHost && joinCode != "");
        }

        UpdateRunHud(inGame);

        if (connecting && statusPanel.activeSelf)
            statusText.text = statusWord + new string('.', 1 + (int)(Time.time * 2f) % 3);
    }

    // Top-of-screen timer and the win / lose screen
    void UpdateRunHud(bool inGame)
    {
        var flow = GameFlow.Instance;
        bool show = inGame && flow != null && flow.IsSpawned && !mainPanel.activeSelf && !joinPanel.activeSelf && !statusPanel.activeSelf;
        timerText.gameObject.SetActive(show);
        endMessage = "";
        if (!show) { endUi = false; return; }

        float t = flow.SecondsLeft;
        string clock = $"{(int)t / 60}:{(int)t % 60:00}";
        switch (flow.Phase)
        {
            case GamePhase.Prep:  timerText.text = $"PREPARE  {clock}"; break;
            case GamePhase.Night: timerText.text = $"NIGHT  {clock}"; break;
            default:              timerText.text = ""; break;
        }
        // The generator keeps the van's beacon on: no power, no dawn
        var generator = GeneratorJob.Instance;
        if (!flow.RunOver && generator != null && generator.IsSpawned)
        {
            float f = generator.Fuel;
            timerText.text += flow.DawnOnHold ? "\n<size=24>NO SIGNAL - DAWN WON'T COME - REFUEL THE GENERATOR</size>"
                                              : $"\n<size=24>GENERATOR  {(int)f / 60}:{(int)f % 60:00}</size>";
        }
        // Everyone sees how long each hidden player has left
        bool anyTaken = false;
        if (!flow.RunOver)
            foreach (var p in FirstPersonController.All)
            {
                if (!p.IsSpawned || !p.IsHidden) continue;
                if (p.IsDowned)
                {
                    timerText.text += $"\n<size=24>{(p.IsOwner ? "YOU ARE" : $"PLAYER {p.OwnerClientId + 1} IS")} DOWN</size>";
                    continue;
                }
                float h = p.HiddenSecondsLeft;
                string who = p.IsOwner ? "YOU WERE TAKEN" : $"PLAYER {p.OwnerClientId + 1} WAS TAKEN";
                timerText.text += $"\n<size=24>{who}  {(int)h / 60}:{(int)h % 60:00}</size>";
            }
        foreach (var p in FirstPersonController.All) anyTaken |= p.IsSpawned && p.IsHidden;
        if (flow.RunOver)
        {
            bool won = flow.Phase == GamePhase.Won;
            endMessage = won ? "YOU SURVIVED THE NIGHT" : anyTaken ? "NOBODY LEFT TO SAVE THEM" : "EVERYONE IS DEAD";
            endColor = won ? textColor : accentColor;
        }
        else
        {
            // Dead but teammates still alive
            foreach (var p in FirstPersonController.All)
                if (p.IsOwner && p.IsDead) { endMessage = "YOU DIED"; endColor = accentColor; }
        }
    }

    string endMessage = "";
    Color endColor;
    GUIStyle endStyle, endButtonStyle;
    bool endUi;   // the end / death screen is up, with its own buttons (the canvas menu would be hidden behind the black fade)

    void LateUpdate()
    {
        if (!string.IsNullOrEmpty(endMessage) && !endUi)
        {
            endUi = true;
            MenuOpen = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (string.IsNullOrEmpty(endMessage) && endUi)
        {
            endUi = false;
            if (!pausePanel.activeSelf && !mainPanel.activeSelf) CloseMenus();
        }
    }

    // Drawn with IMGUI, in front of the black death fade (which is IMGUI too, depth -1000)
    void OnGUI()
    {
        if (string.IsNullOrEmpty(endMessage)) return;
        GUI.depth = -2000;
        if (endStyle == null)
            endStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        if (bodyFont != null) endStyle.font = bodyFont;
        endStyle.fontSize = Mathf.RoundToInt(Screen.height * 0.09f);
        endStyle.normal.textColor = endColor;
        GUI.Label(new Rect(0, 0, Screen.width, Screen.height), endMessage, endStyle);

        if (endButtonStyle == null)
            endButtonStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleCenter };
        if (bodyFont != null) endButtonStyle.font = bodyFont;
        endButtonStyle.fontSize = Mathf.RoundToInt(Screen.height * 0.035f);
        float w = Screen.height * 0.4f, h = Screen.height * 0.08f, x = (Screen.width - w) / 2f, y = Screen.height * 0.62f;
        if (GUI.Button(new Rect(x, y, w, h), "MAIN MENU", endButtonStyle)) Leave();
        if (GUI.Button(new Rect(x, y + h * 1.2f, w, h), "QUIT", endButtonStyle)) Quit();
    }

    // ---------- Actions ----------

    async void Host()
    {
        int me = ++attempt;
        joinCode = "";
        if (useRelay)
        {
            statusWord = "STARTING";
            connecting = true;
            ShowOnly(statusPanel);
            try
            {
                await SignIn();
                Allocation alloc = await RelayService.Instance.CreateAllocationAsync(maxFriends);
                string code = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
                if (me != attempt) return;   // cancelled meanwhile
                Transport().SetRelayServerData(alloc.ToRelayServerData("dtls"));
                joinCode = code;
            }
            catch (System.Exception e)
            {
                if (me != attempt) return;
                Debug.LogWarning($"Relay failed, hosting by IP instead (only LAN players can join): {e.Message}");
            }
            connecting = false;
        }
        if (joinCode == "") Transport().SetConnectionData("127.0.0.1", port, "0.0.0.0");   // direct IP: listen on every network card

        if (CityManager.Instance != null) CityManager.Instance.PrepareHost();   // new city + NavMesh before anything spawns
        if (NetworkManager.Singleton.StartHost())
        {
            CloseMenus();
            if (joinCode != "") GUIUtility.systemCopyBuffer = joinCode;   // ready to paste to friends
        }
        else ShowMessage("Couldn't start hosting (is the port in use?)");
    }

    async void Join()
    {
        int me = ++attempt;
        address = addressField.text.Trim();
        if (string.IsNullOrEmpty(address)) address = "127.0.0.1";
        statusWord = "CONNECTING";
        connecting = true;
        ShowOnly(statusPanel);

        if (LooksLikeAddress(address)) Transport().SetConnectionData(address, port);
        else
        {
            address = address.ToUpperInvariant();
            try
            {
                await SignIn();
                JoinAllocation alloc = await RelayService.Instance.JoinAllocationAsync(address);
                if (me != attempt) return;
                Transport().SetRelayServerData(alloc.ToRelayServerData("dtls"));
            }
            catch (System.Exception e)
            {
                if (me != attempt) return;
                connecting = false;
                Debug.LogWarning($"Relay join failed: {e.Message}");
                ShowMessage($"No game found with code {address}");
                return;
            }
        }
        if (!NetworkManager.Singleton.StartClient())
        {
            connecting = false;
            ShowMessage("Couldn't start the client.");
        }
    }

    // An IP / hostname connects directly; anything else is a Relay join code
    static bool LooksLikeAddress(string s) => s.Contains(".") || s.Contains(":") || s.ToLowerInvariant() == "localhost";

    // Unity Services + anonymous sign-in, once per session. Each copy of the game on this PC (MPPM players, a second
    // build folder) gets its own profile, otherwise Relay sees host and friend as the same player.
    static async Task SignIn()
    {
        if (UnityServices.State == ServicesInitializationState.Uninitialized)
        {
            string profile = "p" + ((uint)Application.dataPath.GetHashCode()).ToString("x8");
            await UnityServices.InitializeAsync(new InitializationOptions().SetProfile(profile));
        }
        while (UnityServices.State == ServicesInitializationState.Initializing) await Task.Yield();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    void Leave()
    {
        attempt++;
        if (NetworkManager.Singleton.IsListening) NetworkManager.Singleton.Shutdown();
        connecting = false;
        joinCode = "";
        ShowOnly(mainPanel);
        mainMessage.text = "";
    }

    void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void OpenPause()
    {
        copyButton.GetComponentInChildren<Text>().text = "COPY JOIN CODE";
        ShowOnly(pausePanel);
    }

    void Resume() => CloseMenus();

    void OnClientConnected(ulong id)
    {
        if (id != NetworkManager.Singleton.LocalClientId) return;
        connecting = false;
        if (!NetworkManager.Singleton.IsHost) CloseMenus();
    }

    void OnClientDisconnected(ulong id)
    {
        var nm = NetworkManager.Singleton;
        if (nm.IsServer && id != nm.LocalClientId) return;   // someone else left; we're still hosting

        bool wasConnecting = connecting;
        connecting = false;
        ShowOnly(mainPanel);
        mainMessage.text = wasConnecting ? $"Couldn't connect to {address}" : "Disconnected from host";
    }

    void ShowMessage(string msg)
    {
        ShowOnly(mainPanel);
        mainMessage.text = msg;
    }

    // ---------- Menu state ----------

    void ShowOnly(GameObject panel)
    {
        foreach (var l in lookLabels) l.text = LookText();   // saved choice / changed in the other menu
        foreach (var l in micLabels) l.text = VoiceChat.DeviceLabel;
        mainPanel.SetActive(panel == mainPanel);
        joinPanel.SetActive(panel == joinPanel);
        pausePanel.SetActive(panel == pausePanel);
        statusPanel.SetActive(panel == statusPanel);
        MenuOpen = panel != null;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void CloseMenus()
    {
        mainPanel.SetActive(false);
        joinPanel.SetActive(false);
        pausePanel.SetActive(false);
        statusPanel.SetActive(false);
        MenuOpen = false;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    static UnityTransport Transport() => (UnityTransport)NetworkManager.Singleton.NetworkConfig.NetworkTransport;

    static string LocalIP()
    {
        try
        {
            foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)) return ip.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    // ---------- Building the UI ----------

    void BuildUI()
    {
        if (FindAnyObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            DontDestroyOnLoad(es);
        }

        var canvasGo = new GameObject("Menu Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        var root = canvasGo.transform;

        // Run HUD (timer at the top, big end-of-run message)
        timerText = AddHudText(root, "Timer", 40, new Vector2(0.5f, 1f), new Vector2(0, -30), TextAnchor.UpperCenter);

        // MAIN
        mainPanel = AddPanel(root, "Main", 0.35f);
        AddTitle(mainPanel.transform, title, subtitle);
        float y = -60f;
        AddButton(mainPanel.transform, "HOST", ref y, Host);
        AddButton(mainPanel.transform, "JOIN", ref y, () => { ShowOnly(joinPanel); addressField.text = address; });
        AddLookButton(mainPanel.transform, ref y);
        AddMicButton(mainPanel.transform, ref y);
        AddButton(mainPanel.transform, "QUIT", ref y, Quit);
        mainMessage = AddLabel(mainPanel.transform, "", 24, new Vector2(140, y - 30), accentColor, TextAnchor.UpperLeft, 900);

        // JOIN
        joinPanel = AddPanel(root, "Join", 0.55f);
        AddTitle(joinPanel.transform, "JOIN A GAME", "the host's join code (or an IP on the same Wi-Fi)");
        addressField = AddInput(joinPanel.transform, new Vector2(140, -40));
        y = -140f;
        AddButton(joinPanel.transform, "CONNECT", ref y, Join);
        AddButton(joinPanel.transform, "BACK", ref y, () => ShowOnly(mainPanel));

        // STATUS
        statusPanel = AddPanel(root, "Status", 0.65f);
        statusText = AddLabel(statusPanel.transform, "CONNECTING", 44, new Vector2(140, 60), textColor, TextAnchor.UpperLeft, 900);
        y = -40f;
        AddButton(statusPanel.transform, "CANCEL", ref y, Leave);

        // PAUSE
        pausePanel = AddPanel(root, "Pause", 0.7f);
        AddTitle(pausePanel.transform, "PAUSED", "");
        pauseInfo = AddLabel(pausePanel.transform, "", 24, new Vector2(140, -10), new Color(0.6f, 0.6f, 0.6f), TextAnchor.UpperLeft, 900);
        y = -110f;
        AddButton(pausePanel.transform, "RESUME", ref y, Resume);
        Text copyLabel = null;
        copyLabel = AddButton(pausePanel.transform, "COPY JOIN CODE", ref y, () => { GUIUtility.systemCopyBuffer = joinCode; copyLabel.text = "COPIED"; });
        copyButton = copyLabel.transform.parent.gameObject;
        AddLookButton(pausePanel.transform, ref y);
        AddMicButton(pausePanel.transform, ref y);
        AddButton(pausePanel.transform, "LEAVE GAME", ref y, Leave);
        AddButton(pausePanel.transform, "QUIT", ref y, Quit);
    }

    Text AddHudText(Transform parent, string name, int size, Vector2 anchor, Vector2 pos, TextAnchor align)
    {
        var t = AddLabel(parent, "", size, pos, textColor, align, 1600);
        t.name = name;
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, anchor.y);
        rt.anchoredPosition = pos;
        t.gameObject.SetActive(false);
        return t;
    }

    GameObject AddPanel(Transform parent, string name, float darkness)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        go.GetComponent<Image>().color = new Color(0f, 0f, 0f, darkness);

        // Darker band on the left so the text always reads over the scene
        var band = new GameObject("Band", typeof(RectTransform), typeof(Image));
        band.transform.SetParent(go.transform, false);
        var brt = band.GetComponent<RectTransform>();
        brt.anchorMin = new Vector2(0f, 0f); brt.anchorMax = new Vector2(0.45f, 1f);
        brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
        band.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.45f);
        return go;
    }

    void AddTitle(Transform parent, string text, string sub)
    {
        var t = AddLabel(parent, text, 92, new Vector2(140, 260), textColor, TextAnchor.LowerLeft, 1400);
        if (titleFont != null) t.font = titleFont;
        t.rectTransform.pivot = new Vector2(0f, 0f);

        // Thin red rule under the title
        var rule = new GameObject("Rule", typeof(RectTransform), typeof(Image));
        rule.transform.SetParent(parent, false);
        var rrt = rule.GetComponent<RectTransform>();
        rrt.anchorMin = rrt.anchorMax = new Vector2(0f, 0.5f);
        rrt.pivot = new Vector2(0f, 0.5f);
        rrt.anchoredPosition = new Vector2(144, 240);
        rrt.sizeDelta = new Vector2(120, 3);
        rule.GetComponent<Image>().color = accentColor;

        if (!string.IsNullOrEmpty(sub))
            AddLabel(parent, sub.ToUpperInvariant(), 22, new Vector2(144, 200), new Color(0.55f, 0.55f, 0.55f), TextAnchor.UpperLeft, 1200);
    }

    Text AddLabel(Transform parent, string text, int size, Vector2 pos, Color color, TextAnchor anchor, float width)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(width, size * 2.2f);
        var t = go.GetComponent<Text>();
        t.text = text;
        t.font = bodyFont != null ? bodyFont : defaultFont;
        t.fontSize = size;
        t.color = color;
        t.alignment = anchor;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    Text AddButton(Transform parent, string text, ref float y, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(130, y);
        rt.sizeDelta = new Vector2(520, 62);
        go.GetComponent<Image>().color = new Color(0, 0, 0, 0);   // invisible hit area

        var b = go.GetComponent<Button>();
        b.transition = Selectable.Transition.None;
        b.onClick.AddListener(onClick);

        var label = AddLabel(go.transform, text, 40, new Vector2(10, 0), textColor, TextAnchor.MiddleLeft, 500);
        var lrt = label.rectTransform;
        lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(1f, 1f);
        lrt.pivot = new Vector2(0f, 0.5f);
        lrt.offsetMin = new Vector2(10, 0); lrt.offsetMax = Vector2.zero;

        var mb = go.AddComponent<MenuButton>();
        mb.label = label;
        mb.normalColor = textColor;
        mb.hoverColor = accentColor;

        y -= 70f;
        return label;
    }

    // "LOOK: NORMAL / RETRO" (RetroLook): each player picks their own, it's remembered on their PC
    void AddLookButton(Transform parent, ref float y)
    {
        Text label = null;
        label = AddButton(parent, LookText(), ref y, () =>
        {
            if (RetroLook.Instance != null) RetroLook.Instance.Toggle();
            label.text = LookText();
        });
        lookLabels.Add(label);
    }

    readonly System.Collections.Generic.List<Text> lookLabels = new System.Collections.Generic.List<Text>();

    // "MIC: DEFAULT / <device>" (VoiceChat): each click picks the next microphone, remembered on this PC
    void AddMicButton(Transform parent, ref float y)
    {
        Text label = null;
        label = AddButton(parent, VoiceChat.DeviceLabel, ref y, () =>
        {
            VoiceChat.NextDevice();
            label.text = VoiceChat.DeviceLabel;
        });
        micLabels.Add(label);
    }

    readonly System.Collections.Generic.List<Text> micLabels = new System.Collections.Generic.List<Text>();
    static string LookText() => RetroLook.On ? "LOOK: RETRO" : "LOOK: NORMAL";

    InputField AddInput(Transform parent, Vector2 pos)
    {
        var go = new GameObject("Address", typeof(RectTransform), typeof(Image), typeof(InputField));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(520, 64);
        go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.06f);

        var text = AddLabel(go.transform, "", 34, Vector2.zero, textColor, TextAnchor.MiddleLeft, 500);
        var trt = text.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(18, 0); trt.offsetMax = new Vector2(-18, 0);
        text.supportRichText = false;

        var ph = AddLabel(go.transform, "JOIN CODE", 34, Vector2.zero, new Color(1f, 1f, 1f, 0.25f), TextAnchor.MiddleLeft, 500);
        var prt = ph.rectTransform;
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(18, 0); prt.offsetMax = new Vector2(-18, 0);

        var field = go.GetComponent<InputField>();
        field.textComponent = text;
        field.placeholder = ph;
        field.caretColor = accentColor;
        field.selectionColor = new Color(accentColor.r, accentColor.g, accentColor.b, 0.4f);
        return field;
    }
}
