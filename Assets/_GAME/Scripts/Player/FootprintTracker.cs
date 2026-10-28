using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Dấu chân ĐỎ (tuần 4, P1). Gắn vào prefab HUNTER. Đã bỏ hẳn dấu chân xanh (dấu của người bị thương).
///  - Hunter DI CHUYỂN thì để lại dấu chân đỏ dưới chân; chỉ các Survivor nhìn thấy (chạy trên máy của Survivor, đọc trạng thái di chuyển đã đồng bộ).
///  - (Tùy chọn, bật mặc định) Survivor đang CHẠY thì để lại dấu chân đỏ, chỉ Hunter nhìn thấy (chạy trên máy của Hunter).
///    Tắt bằng "Survivor để lại dấu cho Hunter" nếu chỉ muốn Hunter để lại dấu.
/// Không cần mạng thêm: dùng trạng thái di chuyển đã đồng bộ. Dấu là tấm Quad dẹt dùng lại (pool), không cần asset.
/// </summary>
public class FootprintTracker : NetworkBehaviour
{
    [Header("Dấu chân đỏ của HUNTER (Survivor nhìn thấy)")]
    [SerializeField] bool hunterLeavesMarks = true;
    [SerializeField] float hunterInterval = 0.6f;
    [SerializeField] float hunterLifetime = 5f;
    [Tooltip("Tắt = Hunter đi bộ/ngồi thấp (CrouchWalk) không để lại dấu, chỉ khi chạy mới có dấu.")]
    [SerializeField] bool hunterMarksWhenCrouchWalking = true;

    [Header("Dấu chân đỏ của SURVIVOR (Hunter nhìn thấy)")]
    [SerializeField] bool survivorLeavesMarks = true;
    [SerializeField] float runInterval = 1f;
    [SerializeField] float runLifetime = 4f;

    [Header("Chung")]
    [SerializeField] Color markColor = new Color(1f, 0.25f, 0.2f);
    [SerializeField] float markSize = 0.5f;
    [Tooltip("Chỉ tạo dấu cho người trong tầm này (m).")]
    [SerializeField] float viewRange = 40f;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    class Mark
    {
        public GameObject go;
        public float dieAt;
    }

    class Timers { public float nextRun; }

    readonly List<Mark> active = new List<Mark>();
    readonly Stack<Mark> pool = new Stack<Mark>();
    readonly Dictionary<SurvivorState, Timers> timers = new Dictionary<SurvivorState, Timers>();
    MaterialPropertyBlock block;
    float nextRefresh;
    float nextHunterMark;
    NetworkPlayerMovement hunterMove;
    Transform localPlayer;
    bool localIsSurvivor;

    public override void OnNetworkSpawn()
    {
        // Chạy trên MỌI máy: máy Hunter vẽ dấu của Survivor, máy Survivor vẽ dấu của Hunter.
        block = new MaterialPropertyBlock();
        hunterMove = GetComponent<NetworkPlayerMovement>();
    }

    public override void OnNetworkDespawn()
    {
        foreach (Mark m in active) if (m.go != null) Destroy(m.go);
        foreach (Mark m in pool) if (m.go != null) Destroy(m.go);
        active.Clear();
        pool.Clear();
    }

    void Update()
    {
        if (!IsSpawned) return;

        // Dọn dấu hết hạn.
        for (int i = active.Count - 1; i >= 0; i--)
        {
            if (Time.time >= active[i].dieAt)
            {
                active[i].go.SetActive(false);
                pool.Push(active[i]);
                active.RemoveAt(i);
            }
        }

        if (Time.time >= nextRefresh)
        {
            nextRefresh = Time.time + 1f;
            RefreshLocalPlayer();
        }

        if (IsOwner)
        {
            if (survivorLeavesMarks) TrackSurvivors();
        }
        else if (hunterLeavesMarks && localIsSurvivor)
        {
            TrackHunter();
        }
    }

    void RefreshLocalPlayer()
    {
        localPlayer = null;
        localIsSurvivor = false;
        NetworkManager nm = NetworkManager.Singleton;
        NetworkObject po = nm != null && nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
        if (po == null) return;
        localPlayer = po.transform;
        PlayerRoleInfo info = po.GetComponent<PlayerRoleInfo>();
        localIsSurvivor = info != null && info.CurrentRole == Role.Survivor;
    }

    // Máy Hunter: Survivor đang chạy thì để lại dấu đỏ.
    void TrackSurvivors()
    {
        // Dọn key đã despawn khỏi timers để tránh rò bộ nhớ và NullRef.
        var toRemove = new System.Collections.Generic.List<SurvivorState>();
        foreach (var k in timers.Keys)
            if (k == null || !k.IsSpawned) toRemove.Add(k);
        foreach (var k in toRemove) timers.Remove(k);

        foreach (SurvivorState st in SurvivorState.All)
        {
            if (st == null || !st.IsSpawned || !st.IsActive) continue;     // chỉ Khỏe hoặc Bị thương
            NetworkPlayerMovement mv = st.GetComponent<NetworkPlayerMovement>();
            if (mv == null || !mv.IsRunningNet) continue;                   // đi bộ / ngồi thấp không để lại dấu

            Vector3 p = st.transform.position;
            if (!InRange(p, transform.position)) continue;

            if (!timers.TryGetValue(st, out Timers t)) { t = new Timers(); timers[st] = t; }
            if (Time.time >= t.nextRun)
            {
                t.nextRun = Time.time + runInterval;
                Spawn(p, markColor, runLifetime);
            }
        }
    }

    // Máy Survivor: Hunter đang di chuyển thì để lại dấu đỏ.
    void TrackHunter()
    {
        if (hunterMove == null || !hunterMove.IsMovingNet) return;
        if (!hunterMarksWhenCrouchWalking && !hunterMove.IsRunningNet) return;
        if (localPlayer != null && !InRange(transform.position, localPlayer.position)) return;
        if (Time.time < nextHunterMark) return;

        nextHunterMark = Time.time + hunterInterval;
        Spawn(transform.position, markColor, hunterLifetime);
    }

    bool InRange(Vector3 a, Vector3 b)
    {
        Vector3 d = a - b;
        d.y = 0f;
        return d.sqrMagnitude <= viewRange * viewRange;
    }

    void Spawn(Vector3 pos, Color color, float lifetime)
    {
        Mark m = pool.Count > 0 ? pool.Pop() : CreateMark();
        m.go.transform.SetPositionAndRotation(
            new Vector3(pos.x, pos.y + 0.03f, pos.z),
            Quaternion.Euler(90f, Random.Range(0f, 360f), 0f));
        m.go.transform.localScale = Vector3.one * markSize;

        var r = m.go.GetComponent<MeshRenderer>();
        r.GetPropertyBlock(block);
        block.SetColor(BaseColorId, color);   // URP
        block.SetColor(ColorId, color);       // shader cũ
        r.SetPropertyBlock(block);

        m.dieAt = Time.time + lifetime;
        m.go.SetActive(true);
        active.Add(m);
    }

    Mark CreateMark()
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "Footprint";
        Destroy(go.GetComponent<Collider>());                     // chỉ là hình, không va chạm
        var r = go.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return new Mark { go = go };
    }
}
