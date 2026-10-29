using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Collections;
public struct LobbyEntry : INetworkSerializable, IEquatable<LobbyEntry>
{
    public ulong ClientId;
    public bool Ready;
    public bool WantsHunter;
    public int CharacterIndex;
    public FixedString64Bytes Name;
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref Ready);
        serializer.SerializeValue(ref WantsHunter);
        serializer.SerializeValue(ref CharacterIndex);
        serializer.SerializeValue(ref Name);
    }

    public bool Equals(LobbyEntry o) =>
        ClientId == o.ClientId && Ready == o.Ready && WantsHunter == o.WantsHunter && CharacterIndex == o.CharacterIndex && Name == o.Name;
}

/// <summary>
/// Phòng chờ (scene Lobby). Đặt 1 GameObject trong scene Lobby, gắn NetworkObject + script này.
/// Tối đa 5 chỗ. Server giữ danh sách, client gửi Ready / "Muốn làm Hunter" / Nhân vật qua ServerRpc.
/// Host chọn BẢN ĐỒ (MapIndex đồng bộ cho mọi người). Mỗi người chọn NHÂN VẬT (CharacterIndex) cho vai mình sẽ nhận.
/// Chạy giống nhau cho LAN và WAN (Relay): kết nối xong là NetworkManager đã chạy, script này không quan tâm cách kết nối.
/// Không vẽ giao diện: LobbyUI và LobbySelectionUI (Canvas) đọc dữ liệu qua các thuộc tính/sự kiện public bên dưới.
/// </summary>
public class LobbyManager : NetworkBehaviour
{
    public const int MaxPlayers = 5;

    [Header("Bản đồ (host chọn)")]
    [Tooltip("Tên scene của từng bản đồ (phải có trong Build Profiles). Phần tử đầu là mặc định.")]
    [SerializeField] string[] mapScenes = { "Match" };
    [Tooltip("Tên hiển thị tương ứng từng bản đồ.")]
    [SerializeField] string[] mapNames = { "Bản đồ 1" };

    [Header("Nhân vật (mỗi người chọn)")]
    [Tooltip("Tên hiển thị các nhân vật. Chỉ số này dùng cho cả Survivor lẫn Hunter: MatchSpawner lấy prefab thứ i của vai mình nhận.")]
    [SerializeField] string[] characterNames = { "Mặc định" };

    /// <summary>Bản đồ đang chọn (chỉ số trong mapScenes), đồng bộ cho mọi máy.</summary>
    public readonly NetworkVariable<int> MapIndex = new NetworkVariable<int>(0);

    NetworkList<LobbyEntry> entries;
    bool debugAnyCount;

    /// <summary>Bắn khi danh sách hoặc bản đồ đổi, khi bật/tắt Debug hoặc khi vừa spawn (để UI vẽ lại).</summary>
    public event Action OnChanged;
    /// <summary>Thông báo cho người bấm Bắt đầu (chưa đủ người, còn người chưa sẵn sàng...).</summary>
    public event Action<string> OnMessage;

    public int Count => entries.Count;
    public LobbyEntry Get(int i) => entries[i];
    public bool DebugAnyCount => debugAnyCount;

    public int MapCount => mapScenes != null ? mapScenes.Length : 0;
    public string MapName(int i) => (mapNames != null && i >= 0 && i < mapNames.Length) ? mapNames[i] : (mapScenes != null && i >= 0 && i < mapScenes.Length ? mapScenes[i] : "?");
    public int CharacterCount => characterNames != null ? characterNames.Length : 0;
    public string CharacterName(int i) => (characterNames != null && i >= 0 && i < characterNames.Length) ? characterNames[i] : "?";

    void Awake()
    {
        entries = new NetworkList<LobbyEntry>();
    }

    public override void OnNetworkSpawn()
    {
        entries.OnListChanged += HandleListChanged;
        MapIndex.OnValueChanged += HandleMapChanged;

        if (IsServer)
        {
            foreach (ulong id in NetworkManager.ConnectedClientsIds) AddClient(id);
            NetworkManager.OnClientConnectedCallback += AddClient;
            NetworkManager.OnClientDisconnectCallback += RemoveClient;
        }
        OnChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        entries.OnListChanged -= HandleListChanged;
        MapIndex.OnValueChanged -= HandleMapChanged;
        if (IsServer && NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= AddClient;
            NetworkManager.OnClientDisconnectCallback -= RemoveClient;
        }
    }

    void HandleListChanged(NetworkListEvent<LobbyEntry> e) => OnChanged?.Invoke();
    void HandleMapChanged(int oldValue, int newValue) => OnChanged?.Invoke();

    int IndexOf(ulong id)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].ClientId == id) return i;
        return -1;
    }

    /// <summary>Lấy dòng của chính máy này trong danh sách.</summary>
    public bool TryGetMine(out LobbyEntry mine)
    {
        mine = default;
        if (!IsSpawned) return false;
        int i = IndexOf(NetworkManager.LocalClientId);
        if (i < 0) return false;
        mine = entries[i];
        return true;
    }

    public bool IsMe(ulong clientId) => IsSpawned && clientId == NetworkManager.LocalClientId;

    // ---------------- Server: quản lý danh sách ----------------

    void AddClient(ulong id)
    {
        if (IndexOf(id) >= 0) return;
        if (entries.Count >= MaxPlayers)
        {
            Debug.LogWarning("Phòng đã đủ 5 người, từ chối client " + id);
            NetworkManager.DisconnectClient(id);
            return;
        }
        entries.Add(new LobbyEntry { ClientId = id, Name = new FixedString64Bytes(NameAssignments.Get(id)) });
    }

    void RemoveClient(ulong id)
    {
        int i = IndexOf(id);
        if (i >= 0) entries.RemoveAt(i);
    }

    [ServerRpc(RequireOwnership = false)]
    void SetReadyServerRpc(bool ready, ServerRpcParams p = default)
    {
        int i = IndexOf(p.Receive.SenderClientId);
        if (i < 0) return;
        LobbyEntry e = entries[i];
        e.Ready = ready;
        entries[i] = e;
    }

    [ServerRpc(RequireOwnership = false)]
    void WantHunterServerRpc(bool want, ServerRpcParams p = default)
    {
        int i = IndexOf(p.Receive.SenderClientId);
        if (i < 0) return;
        LobbyEntry e = entries[i];
        e.WantsHunter = want;
        entries[i] = e;
    }

    [ServerRpc(RequireOwnership = false)]
    void SetCharacterServerRpc(int characterIndex, ServerRpcParams p = default)
    {
        int i = IndexOf(p.Receive.SenderClientId);
        if (i < 0) return;
        if (characterIndex < 0 || characterIndex >= Mathf.Max(1, CharacterCount)) return;   // server kiểm tra chỉ số hợp lệ
        LobbyEntry e = entries[i];
        e.CharacterIndex = characterIndex;
        entries[i] = e;
    }

    // ---------------- Hàm public cho nút UI (OnClick) ----------------

    /// <summary>Nút Sẵn sàng / Hủy sẵn sàng.</summary>
    public void ToggleReady()
    {
        if (TryGetMine(out LobbyEntry mine)) SetReadyServerRpc(!mine.Ready);
    }

    /// <summary>Toggle "Tôi muốn làm Hunter" (OnClick, chọn Dynamic bool).</summary>
    public void SetWantHunter(bool want)
    {
        if (TryGetMine(out LobbyEntry mine) && mine.WantsHunter != want) WantHunterServerRpc(want);
    }

    /// <summary>Chọn nhân vật (chỉ số trong characterNames).</summary>
    public void SetCharacter(int characterIndex)
    {
        if (TryGetMine(out LobbyEntry mine) && mine.CharacterIndex != characterIndex) SetCharacterServerRpc(characterIndex);
    }

    /// <summary>Host chọn bản đồ. Người khác gọi hàm này không có tác dụng.</summary>
    public void SetMap(int mapIndex)
    {
        if (!IsSpawned || !IsServer) return;
        if (mapIndex < 0 || mapIndex >= MapCount) return;
        MapIndex.Value = mapIndex;
    }

    /// <summary>Toggle Debug: bắt đầu trận với số người tùy ý (chỉ có tác dụng trên host).</summary>
    public void SetDebug(bool on)
    {
        debugAnyCount = on;
        OnChanged?.Invoke();
    }

    /// <summary>Nút BẮT ĐẦU TRẬN (chỉ host).</summary>
    public void StartMatch()
    {
        if (!IsSpawned || !IsServer) return;

        int n = entries.Count;
        if (n == 0) return;

        if (!debugAnyCount && n < MaxPlayers)
        {
            Say($"Chưa đủ {MaxPlayers} người ({n}/{MaxPlayers}). Bật Debug để bắt đầu với số người tùy ý.");
            return;
        }
        for (int i = 0; i < n; i++)
        {
            // Host bấm Start nên không bắt buộc host phải bấm Sẵn sàng.
            if (!entries[i].Ready && entries[i].ClientId != NetworkManager.LocalClientId)
            {
                Say("Còn người chưa sẵn sàng: người chơi #" + entries[i].Name.ToString());
                return;
            }
        }

        // Chọn Hunter: ưu tiên người tick "Muốn làm Hunter", không ai tick thì random.
        // Chỉ 1 người trong phòng (debug): mặc định là Survivor để test máy/cổng/hầm; tick Hunter thì làm Hunter.
        ulong hunterId = ulong.MaxValue;
        if (n >= 2)
        {
            var wants = new System.Collections.Generic.List<ulong>();
            var all = new System.Collections.Generic.List<ulong>();
            for (int i = 0; i < n; i++)
            {
                all.Add(entries[i].ClientId);
                if (entries[i].WantsHunter) wants.Add(entries[i].ClientId);
            }
            var pool = wants.Count > 0 ? wants : all;
            hunterId = pool[UnityEngine.Random.Range(0, pool.Count)];
        }
        else if (entries[0].WantsHunter)
        {
            hunterId = entries[0].ClientId;
        }

        RoleAssignments.Clear();
        CharacterAssignments.Clear();
        for (int i = 0; i < n; i++)
        {
            ulong id = entries[i].ClientId;
            RoleAssignments.Set(id, id == hunterId ? Role.Hunter : Role.Survivor);
            CharacterAssignments.Set(id, entries[i].CharacterIndex);
        }

        string scene = mapScenes[Mathf.Clamp(MapIndex.Value, 0, mapScenes.Length - 1)];
        Debug.Log($"[Lobby] Bắt đầu trận: {n} người, bản đồ \"{scene}\", Hunter = " +
                  (hunterId == ulong.MaxValue ? "không có" : "#" + hunterId));

        NetworkManager.SceneManager.LoadScene(scene, LoadSceneMode.Single);
    }

    void Say(string msg)
    {
        Debug.LogWarning(msg);
        OnMessage?.Invoke(msg);
    }
}
