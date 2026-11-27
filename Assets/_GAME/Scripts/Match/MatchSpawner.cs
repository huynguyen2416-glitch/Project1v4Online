using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Collections;

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
    readonly HashSet<ulong> spawnedBots = new HashSet<ulong>();   // bot đã spawn trong trận này
    SpawnPointSet spawnSet;   // bộ 9 điểm spawn của bản đồ đang chơi (null nếu bản đồ chưa có)
    int[] pointOrder;         // thứ tự điểm đã xáo trộn: người ở slot i đứng điểm pointOrder[i]
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
    // Mỗi trận xáo trộn 9 điểm một lần (Fisher–Yates). Slot 0..4 lấy 5 điểm đầu: khác nhau, ai vào điểm nào là ngẫu nhiên.
    void PrepareSpawnPoints()
    {
        spawnSet = FindFirstObjectByType<SpawnPointSet>();
        pointOrder = null;
        if (spawnSet == null) return;   // bản đồ chưa có điểm: dùng hàng ngang cũ
        if (spawnSet.Count < 5)
        {
            Debug.LogWarning("MatchSpawner: SpawnPointSet chỉ có " + spawnSet.Count + " điểm (cần ít nhất 5), dùng chỗ đứng cũ.");
            spawnSet = null;
            return;
        }

        pointOrder = new int[spawnSet.Count];
        for (int i = 0; i < pointOrder.Length; i++) pointOrder[i] = i;
        for (int i = pointOrder.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            int tmp = pointOrder[i]; pointOrder[i] = pointOrder[j]; pointOrder[j] = tmp;
        }
        Debug.Log($"[Spawn] Bốc điểm xuất phát: {pointOrder[0]}, {pointOrder[1]}, {pointOrder[2]}, {pointOrder[3]}, {pointOrder[4]}");
    }

    // Chỗ đứng đầu trận của slot; không có bộ điểm thì dùng hàng ngang cũ.
    void GetStartPose(NetworkPlayerMovement mv, int slot, out Vector3 pos, out float yaw)
    {
        if (pointOrder != null && spawnSet != null && slot >= 0 && slot < pointOrder.Length
            && spawnSet.TryGetPose(pointOrder[slot], out pos, out yaw)) return;
        mv.GetSlotPose(slot, out pos, out yaw);
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
        if (!IsMatchScene(sceneName)) { spawnedBots.Clear(); return; }
        PrepareSpawnPoints();
        foreach (ulong id in completed) SpawnFor(id);
        foreach (ulong id in timedOut) Debug.LogWarning("Client " + id + " tai scene qua lau, khong spawn.");
        SpawnBots();
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
        NetworkObject netObj = go.GetComponent<NetworkObject>();
        netObj.DontDestroyWithOwner = true;   // giữ object khi owner disconnect → bot tiếp quản
        netObj.SpawnAsPlayerObject(clientId, true);
        if (MatchManager.Instance != null)
            MatchManager.Instance.RegisterPlayerObject(clientId, netObj);

        // Chỗ đứng đã chia ở phòng chờ (người nối lại không có slot -> PlayerIdentity khôi phục vị trí cũ).
        if (SlotAssignments.TryGet(clientId, out int slot))
        {
            NetworkPlayerMovement mv = go.GetComponent<NetworkPlayerMovement>();
            PlayerIdentity pid = go.GetComponent<PlayerIdentity>();
            if (mv != null && pid != null)
            {
                GetStartPose(mv, slot, out Vector3 slotPos, out float slotYaw);
                pid.ServerPlaceOwner(slotPos, slotYaw);
            }
        }
        Debug.Log($"Spawn client {clientId} vai {role}, nhan vat {ci}");
        // PlayerIdentity.RegisterServerRpc se duoc goi ngay sau khi client nhan duoc spawn,
        // va se xu ly khoi phuc state (vi tri, mau) neu day la reconnect.
    }

    // ---------------- Bot (do server điều khiển, không có kết nối thật) ----------------

    void SpawnBots()
    {
        foreach (ulong id in BotRoster.Ids) SpawnBot(id);
    }

    void SpawnBot(ulong botId)
    {
        if (!spawnedBots.Add(botId)) return;
        if (!RoleAssignments.Has(botId)) return;

        Role role = RoleAssignments.Get(botId);
        int ci = CharacterAssignments.Get(botId);
        GameObject prefab = role == Role.Hunter
            ? Pick(hunterPrefab, extraHunterPrefabs, ci)
            : Pick(survivorPrefab, extraSurvivorPrefabs, ci);
        if (prefab == null) { Debug.LogError("MatchSpawner: chua gan prefab cho bot vai " + role); return; }

        GameObject go = Instantiate(prefab);
        NetworkPlayerMovement mv = go.GetComponent<NetworkPlayerMovement>();
        if (mv != null && SlotAssignments.TryGet(botId, out int slot))
        {
            GetStartPose(mv, slot, out Vector3 pos, out float yaw);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        }

        NetworkObject netObj = go.GetComponent<NetworkObject>();
        netObj.DontDestroyWithOwner = true;
        netObj.SpawnWithOwnership(NetworkManager.ServerClientId, true);   // server sở hữu, KHÔNG phải PlayerObject

        // PlayerRoleInfo / PlayerIdentity lấy vai và tên theo OwnerClientId (= host) khi spawn: ghi đè bằng id giả của bot.
        PlayerRoleInfo info = go.GetComponent<PlayerRoleInfo>();
        if (info != null) info.RoleValue.Value = (byte)role;
        PlayerIdentity ident = go.GetComponent<PlayerIdentity>();
        if (ident != null) ident.PlayerName.Value = new FixedString64Bytes(NameAssignments.Get(botId));

        if (MatchManager.Instance != null) MatchManager.Instance.RegisterPlayerObject(botId, netObj);

        BotController bc = BotController.Of(go);
        bc.BeginStandIn(botId);   // đặt OriginalClientId = id giả và đăng ký vào ActiveBots
        bc.StartBot();            // chạy não ngay, không đứng im
        Debug.Log($"Spawn bot {botId} vai {role}, nhan vat {ci}");
    }
}
