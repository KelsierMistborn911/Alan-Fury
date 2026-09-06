using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Простой Host / Client для этапа 1 коопа.
/// Вешать на UI-объект или пустышку на bootstrap/игровой сцене.
/// Одиночная игра: не вызывать StartHost/StartClient — сцена работает как раньше.
/// </summary>
public class NetworkBootstrap : MonoBehaviour
{
    [Header("Подключение")]
    [Tooltip("Адрес для Client. Для двух окон на одной машине — 127.0.0.1")]
    public string connectAddress = "127.0.0.1";

    [Tooltip("Порт UnityTransport (должен совпадать у Host и Client).")]
    public ushort port = 7777;

    [Header("Клавиши (опционально)")]
    public KeyCode hostKey = KeyCode.H;
    public KeyCode clientKey = KeyCode.C;
    public KeyCode shutdownKey = KeyCode.Escape;

    [Header("UI (можно оставить пустым и жать клавиши)")]
    public bool showOnGui = true;

    void Update()
    {
        if (NetworkManager.Singleton == null) return;

        if (Input.GetKeyDown(hostKey) && !NetworkManager.Singleton.IsListening)
            StartHost();

        if (Input.GetKeyDown(clientKey) && !NetworkManager.Singleton.IsListening)
            StartClient();

        if (Input.GetKeyDown(shutdownKey) && NetworkManager.Singleton.IsListening)
            Shutdown();
    }

    public void StartHost()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkBootstrap: на сцене нет NetworkManager.Singleton.");
            return;
        }

        ApplyTransportAddress();
        bool ok = NetworkManager.Singleton.StartHost();
        Debug.Log(ok ? "NetworkBootstrap: Host started." : "NetworkBootstrap: StartHost failed.");
    }

    public void StartClient()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkBootstrap: на сцене нет NetworkManager.Singleton.");
            return;
        }

        ApplyTransportAddress();
        bool ok = NetworkManager.Singleton.StartClient();
        Debug.Log(ok ? "NetworkBootstrap: Client started." : "NetworkBootstrap: StartClient failed.");
    }

    public void Shutdown()
    {
        if (NetworkManager.Singleton == null) return;
        NetworkManager.Singleton.Shutdown();
        Debug.Log("NetworkBootstrap: Shutdown.");
    }

    void ApplyTransportAddress()
    {
        var nm = NetworkManager.Singleton;
        if (nm.NetworkConfig.NetworkTransport is Unity.Netcode.Transports.UTP.UnityTransport utp)
        {
            utp.ConnectionData.Address = connectAddress;
            utp.ConnectionData.Port = port;
        }
    }

    void OnGUI()
    {
        if (!showOnGui) return;

        const int w = 160;
        const int h = 28;
        int x = 12;
        int y = 12;

        bool listening = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

        GUI.enabled = !listening;
        if (GUI.Button(new Rect(x, y, w, h), "Host (H)"))
            StartHost();
        y += h + 6;

        if (GUI.Button(new Rect(x, y, w, h), "Client (C)"))
            StartClient();
        y += h + 6;

        GUI.enabled = listening;
        if (GUI.Button(new Rect(x, y, w, h), "Shutdown (Esc)"))
            Shutdown();
        GUI.enabled = true;

        y += h + 10;
        string status = "Offline";
        if (NetworkManager.Singleton != null)
        {
            if (NetworkManager.Singleton.IsHost) status = "Host";
            else if (NetworkManager.Singleton.IsServer) status = "Server";
            else if (NetworkManager.Singleton.IsClient) status = "Client";
        }
        GUI.Label(new Rect(x, y, 300, h), "Status: " + status);
    }
}
