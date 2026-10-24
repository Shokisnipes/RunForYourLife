using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

// Simple Host / Join menu. Put on the NetworkManager object.
public class NetworkUI : MonoBehaviour
{
    public string address = "127.0.0.1";
    public ushort port = 7777;

    void OnGUI()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        GUILayout.BeginArea(new Rect(20, 20, 260, 220), GUI.skin.box);

        if (!nm.IsClient && !nm.IsServer)
        {
            GUILayout.Label("Run For Your Life");
            GUILayout.Label("Host address (for Join):");
            address = GUILayout.TextField(address);

            if (GUILayout.Button("Host", GUILayout.Height(30)))
            {
                // Listen on all network cards so friends on your Wi-Fi can join
                Transport(nm).SetConnectionData("127.0.0.1", port, "0.0.0.0");
                nm.StartHost();
            }
            if (GUILayout.Button("Join", GUILayout.Height(30)))
            {
                Transport(nm).SetConnectionData(address, port);
                nm.StartClient();
            }
        }
        else
        {
            GUILayout.Label(nm.IsHost ? "Hosting" : nm.IsConnectedClient ? "Connected" : "Connecting...");
            if (nm.IsServer) GUILayout.Label($"Players: {nm.ConnectedClientsIds.Count}");
            GUILayout.Label("Esc frees the mouse");
            if (GUILayout.Button("Leave")) nm.Shutdown();
        }

        GUILayout.EndArea();
    }

    static UnityTransport Transport(NetworkManager nm) => (UnityTransport)nm.NetworkConfig.NetworkTransport;
}
