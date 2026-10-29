using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Gang vao GameObject NetworkManager (object nay khong bi huy khi doi scene).
/// Khi moi may da tai xong scene ban do, server spawn nhan vat dung prefab theo VAI va NHAN VAT.
/// Ho tro ca spawn lan dau (OnLoadEventCompleted) va reconnect mid-game (OnSceneEvent SynchronizeComplete).
/// </summary>
public class MatchSpawner : MonoBehaviour
{
    public static MatchSpawner Instance { get; private set; }

    [Header("Nhan vat so 0 (mac dinh)")]
    [SerializeField] GameObject survivorPrefab;
    [SerializeField] GameObject hunterPrefab;

    [Header("Nhan vat so 1, 2, ... (tuy chon)")]
    [SerializeField] GameObject[] extraSurvivorPrefabs;
    [SerializeField] GameObject[] extraHunterPrefabs;

    [Header("Ban do")]
    [SerializeField] string matchScene = "Match";
    [Tooltip("Scene cua cac ban do con lai (ban do 2, 3, ...). Phai trung LobbyManager.mapScenes.")]
    [SerializeField] string[] extraMatchScenes;

    NetworkManager nm;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void Start()
    {
        nm = GetComponent<NetworkManager>();
        if (nm == null) nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogError("MatchSpawner: khong thay NetworkManager.");
            enabled = false;
            return;
        }
        nm.OnServerStarted += HookSceneEvents;
        if (nm.IsServer && nm.SceneManager != null) HookSceneEvents();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (nm != null)
        {
            nm.OnServerStarted -= HookSceneEvents;
            if (nm.SceneManager != null)
            {
                nm.SceneManager.OnLoadEventCompleted -= OnLoadCompleted;
                nm.SceneManager.OnSceneEvent         -= OnSceneEventReceived;
            }
        }
    }

    void HookSceneEvents()
    {
        // Lan dau tran bat dau: tat ca client load xong cung luc
        nm.SceneManager.OnLoadEventCompleted -= OnLoadCompleted;
        nm.SceneManager.OnLoadEventCompleted += OnLoadCompleted;

        // Reconnect mid-game: client moi sync xong scene (SynchronizeComplete)
        nm.SceneManager.OnSceneEvent -= OnSceneEventReceived;
        nm.SceneManager.OnSceneEvent += OnSceneEventReceived;
    }

    // Kich hoat sau khi TAT CA client load xong (bat dau tran binh thuong)
    void OnLoadCompleted(string sceneName, LoadSceneMode mode, List<ulong> completed, List<ulong> timedOut)
    {
        if (!IsMatchScene(sceneName)) return;
        foreach (ulong id in completed) SpawnFor(id);
        foreach (ulong id in timedOut) Debug.LogWarning("Client " + id + " tai scene qua lau, khong spawn.");
    }

    // Kich hoat cho TUNG client khi ho sync scene xong (danh cho reconnect / late-join)
    void OnSceneEventReceived(SceneEvent ev)
    {
        if (!nm.IsServer) return;
        if (ev.SceneEventType != SceneEventType.SynchronizeComplete) return;
        // Kiem tra scene hien tai tren server co phai scene tran khong
        if (!IsMatchScene(SceneManager.GetActiveScene().name)) return;
        SpawnFor(ev.ClientId);
    }

    bool IsMatchScene(string sceneName)
    {
        if (sceneName == matchScene) return true;
        if (extraMatchScenes != null)
            foreach (string s in extraMatchScenes) if (s == sceneName) return true;
        return false;
    }

    static GameObject Pick(GameObject main, GameObject[] extra, int index)
    {
        if (index <= 0 || extra == null || index - 1 >= extra.Length || extra[index - 1] == null) return main;
        return extra[index - 1];
    }

    void SpawnFor(ulong clientId)
    {
        // Neu da co PlayerObject (co the da spawn o vong truoc) thi bo qua
        if (nm.ConnectedClients.TryGetValue(clientId, out var c) && c.PlayerObject != null) return;
        if (!RoleAssignments.Has(clientId))  //chỉ spawn client đã có vai (người chơi từ Lobby hoặc người nối lại đã được ConnectionGate gán vai). Client nào lọt qua mà chưa có vai sẽ không bị spawn nhầm thành Survivor.
        {
            Debug.LogWarning("MatchSpawner: client " + clientId + " chua duoc gan vai, khong spawn.");
            return;
        }
        Role role = RoleAssignments.Get(clientId);
        int ci = CharacterAssignments.Get(clientId);
        GameObject prefab = role == Role.Hunter
            ? Pick(hunterPrefab, extraHunterPrefabs, ci)
            : Pick(survivorPrefab, extraSurvivorPrefabs, ci);
        if (prefab == null) { Debug.LogError("MatchSpawner: chua gan prefab cho vai " + role); return; }

        GameObject go = Instantiate(prefab);
        go.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId, true);
        Debug.Log($"Spawn client {clientId} vai {role}, nhan vat {ci}");
        // PlayerIdentity.RegisterServerRpc se duoc goi ngay sau khi client nhan duoc spawn,
        // va se xu ly khoi phuc state (vi tri, mau) neu day la reconnect.
    }
}
