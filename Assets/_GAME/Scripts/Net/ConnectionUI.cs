using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

/// Nhập IP/port, bấm Host hoặc Client (LAN). Gắn vào GameObject NetworkManager.
/// Hiện IP LAN của máy để người kia nhập. Chỉ khóa chuột trong scene trận (Match).
public class ConnectionUI : MonoBehaviour
{
    [SerializeField] string hostIp = "127.0.0.1";
    [SerializeField] ushort port = 7777;
    [SerializeField] string matchSceneName = "Match";
    [SerializeField] string lobbySceneName = "Lobby";

    string portText;
    string localIps;
    bool? wasInMatch;
    bool wasListening;

    void Start()
    {
        portText = port.ToString();
        localIps = GetLocalIps();
    }

    void Update()
    {
        var nm = NetworkManager.Singleton;
        bool listening = nm != null && nm.IsListening;
        bool inMatch = listening && SceneManager.GetActiveScene().name == matchSceneName;

        // Mất kết nối khi đang ở scene Match (bấm Disconnect, bị kick, host thoát): về Lobby để vào lại.
        if (wasListening && !listening && SceneManager.GetActiveScene().name == matchSceneName)
            SceneManager.LoadScene(lobbySceneName);
        wasListening = listening;

        if (wasInMatch != inMatch)
        {
            wasInMatch = inMatch;
            Cursor.lockState = inMatch ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !inMatch;
        }

        // Tab: bật/tắt con trỏ trong trận để bấm Disconnect
        if (inMatch && Input.GetKeyDown(KeyCode.Tab))
        {
            bool unlock = Cursor.lockState == CursorLockMode.Locked;
            Cursor.lockState = unlock ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = unlock;
        }
    }

    void OnGUI()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        GUILayout.BeginArea(new Rect(10, 10, 260, 320));
        GUILayout.Label("LAN IP of this PC: " + localIps);

        if (nm.IsListening)
        {
            if (nm.IsServer) GUILayout.Label("Host - Clients: " + nm.ConnectedClientsIds.Count);
            else GUILayout.Label(nm.IsConnectedClient ? "Connected as client" : "Connecting...");
            if (GUILayout.Button("Disconnect")) nm.Shutdown();
        }
        else
        {
            GUILayout.Label("Host IP (for Client):");
            hostIp = GUILayout.TextField(hostIp);
            GUILayout.Label("Port:");
            portText = GUILayout.TextField(portText);

            if (GUILayout.Button("Host")) StartNet(nm, true);
            if (GUILayout.Button("Client")) StartNet(nm, false);
        }
        GUILayout.EndArea();
    }

    void StartNet(NetworkManager nm, bool host)
    {
        if (!ushort.TryParse(portText, out ushort p)) { Debug.LogError("Port không hợp lệ"); return; }
        var utp = nm.GetComponent<UnityTransport>();
        if (utp == null) { Debug.LogError("NetworkManager chưa dùng UnityTransport"); return; }

        if (host)
        {
            // forceOverrideCommandLineArgs = true: bỏ qua mọi tham số -ip/-port trên dòng lệnh
            utp.SetConnectionData(true, "127.0.0.1", p, "0.0.0.0");
            nm.StartHost();
            Debug.Log("HOST: listen=" + utp.ConnectionData.ServerListenAddress +
                      " | listenEndpoint=" + utp.ConnectionData.ListenEndPoint);
        }
        else
        {
            utp.SetConnectionData(hostIp.Trim(), p);
            nm.StartClient();
            Debug.Log("CLIENT: ket noi toi " + hostIp.Trim() + ":" + p);
        }
    }

    static string GetLocalIps()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(ua.Address.ToString());
        }
        return list.Count > 0 ? string.Join(", ", list) : "?";
    }
}