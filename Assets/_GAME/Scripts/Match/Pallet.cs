using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public enum PalletState : byte { Standing = 0, Dropped = 1, Destroyed = 2 }

/// <summary>
/// Pallet (kiểu Dead by Daylight / Identity V). Phím tương tác: SPACE (xem PalletInteract).
///
///   Standing  → Survivor nhấn Space → Dropped (tức thì + chạy anim "Drop") và choáng Hunter đang đứng trong vùng.
///   Dropped   → Survivor nhấn Space → trèo qua (giống cửa sổ).
///             → Hunter nhấn Space   → phá (anim "Break" ~1.5 giây) → Destroyed.
///   Destroyed → không còn gì.
///
/// VÙNG TƯƠNG TÁC giống hệt VaultWindow: hộp chữ nhật rộng "width" (trục X) và sâu "approachDepth" mỗi phía (trục Z),
/// người chơi phải quay mặt về phía bên kia pallet. Chọn pallet trong Scene view sẽ thấy hộp vàng và 2 chấm xanh (điểm đáp).
///
/// SETUP PREFAB:
///   Root: NetworkObject, Pallet.cs, Animator (trigger "Drop", "Break"). Trục XANH (Z) của root phải đi XUYÊN qua pallet
///   (hướng người chạy qua). Chỉ xoay quanh trục Y, Scale (1, 1, 1).
///   blockCollider: BoxCollider (không tick Is Trigger) chặn đường khi pallet nằm xuống. Script tự bật/tắt.
///   standingVisual / droppedVisual: 2 child hiển thị pallet đứng / nằm.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class Pallet : NetworkBehaviour
{
    /// <summary>Mọi pallet trong scene. PalletInteract dùng để quét.</summary>
    public static readonly List<Pallet> All = new List<Pallet>();

    /// <summary>Server cho phép người chơi đứng lệch thêm chừng này (mét) so với vùng, bù độ trễ mạng.</summary>
    const float ServerRangeMargin = 1f;

    [Header("Visuals & Animator")]
    [Tooltip("Child hiển thị pallet dựng thẳng")]
    [SerializeField] GameObject standingVisual;
    [Tooltip("Child hiển thị pallet nằm xuống")]
    [SerializeField] GameObject droppedVisual;
    [Tooltip("Animator trên root prefab. Trigger: 'Drop', 'Break'")]
    [SerializeField] Animator animator;

    [Header("Collider")]
    [Tooltip("BoxCollider (KHÔNG trigger) chặn di chuyển khi Dropped. Bật/tắt tự động.")]
    [SerializeField] Collider blockCollider;

    [Header("Vùng tương tác (giống VaultWindow)")]
    [Tooltip("Bề ngang vùng đứng được (dọc trục X), mét.")]
    [SerializeField] float width = 2.4f;
    [Tooltip("Đứng cách tâm pallet tối đa bao nhiêu mét (dọc trục Z) thì được đập / trèo / phá.")]
    [SerializeField] float approachDepth = 1.5f;

    [Header("Kích thước pallet")]
    [Tooltip("Nửa chiều sâu pallet theo trục Z (mét). Dùng để đẩy Hunter ra đúng phía khi bị choáng. Chỉnh khớp mesh.")]
    [SerializeField] float halfDepth = 0.7f;

    [Header("Choáng Hunter lúc pallet rơi")]
    [SerializeField] float stunRadius = 2.2f;
    [SerializeField] float stunDuration = 2.5f;

    [Header("Trèo qua (Survivor)")]
    [Tooltip("Điểm đáp cách tâm pallet bao xa ở phía bên kia.")]
    [SerializeField] float vaultLanding = 1.3f;
    [SerializeField] float vaultArcHeight = 0.2f;
    [Tooltip("Thời gian trèo (giây), chỉnh khớp clip animation.")]
    [SerializeField] float vaultDuration = 0.55f;

    [Header("Phá pallet (Hunter)")]
    [Tooltip("Thời gian animation phá chạy trước khi pallet biến mất (giây). Hunter bị khóa trong lúc này.")]
    [SerializeField] float breakAnimDuration = 1.5f;

    // ── NetworkVariables ──────────────────────────────────────────────────────

    public readonly NetworkVariable<byte> State =
        new NetworkVariable<byte>((byte)PalletState.Standing);

    /// <summary>True khi Hunter đang phá (animation chạy). Mọi máy đọc để chặn trèo qua pallet đang vỡ.</summary>
    public readonly NetworkVariable<bool> IsBreaking = new NetworkVariable<bool>(false);

    public PalletState CurrentState => (PalletState)State.Value;
    public bool IsStanding => CurrentState == PalletState.Standing;
    public bool IsDropped => CurrentState == PalletState.Dropped;
    public bool IsDestroyed => CurrentState == PalletState.Destroyed;

    public float VaultLanding => vaultLanding;
    public float VaultDuration => vaultDuration;
    public float VaultArcHeight => vaultArcHeight;
    public float BreakDuration => breakAnimDuration;

    // ── Hình học (giống VaultWindow) ──────────────────────────────────────────

    /// <summary>Hướng đi xuyên pallet (nằm ngang, độ dài 1).</summary>
    Vector3 Forward
    {
        get
        {
            Vector3 f = transform.forward;
            f.y = 0f;
            return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
        }
    }

    Vector3 Right => Vector3.Cross(Vector3.up, Forward);

    /// <summary>
    /// Người chơi có đang đứng trong vùng không? side = +1 nếu đứng phía +Z, -1 nếu phía -Z.
    /// margin cho phép đứng lệch thêm (server dùng để bù độ trễ).
    /// KHÔNG kiểm tra hướng nhìn — Survivor không cần quay mặt vào pallet.
    /// </summary>
    public bool TryGetSide(Vector3 worldPos, out int side, out float distance, float margin = 0f)
    {
        Vector3 d = worldPos - transform.position;
        d.y = 0f;
        float x = Vector3.Dot(d, Right);
        float z = Vector3.Dot(d, Forward);
        side = z >= 0f ? 1 : -1;
        distance = d.magnitude;
        return Mathf.Abs(x) <= width * 0.5f + margin && Mathf.Abs(z) <= approachDepth + margin;
    }

    /// <summary>Hướng nhảy khi đứng ở phía fromSide (đi sang phía đối diện).</summary>
    public Vector3 JumpDirection(int fromSide) => -fromSide * Forward;

    /// <summary>Điểm đáp ở phía bên kia, giữ nguyên độ cao y của người chơi.</summary>
    public Vector3 GetLandingPoint(int fromSide, float y)
    {
        Vector3 p = transform.position + JumpDirection(fromSide) * vaultLanding;
        p.y = y;
        return p;
    }

    // ── Server-only ───────────────────────────────────────────────────────────

    bool breakInProgress;
    float breakTimer;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        State.OnValueChanged += OnStateChanged;
        ApplyState(CurrentState);
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        State.OnValueChanged -= OnStateChanged;
    }

    void OnStateChanged(byte _, byte next) => ApplyState((PalletState)next);

    void ApplyState(PalletState s)
    {
        if (standingVisual) standingVisual.SetActive(s == PalletState.Standing);
        if (droppedVisual) droppedVisual.SetActive(s == PalletState.Dropped);
        // blockCollider không trigger, chỉ chặn đường khi nằm xuống.
        if (blockCollider) blockCollider.enabled = s == PalletState.Dropped;
    }

    // ── Server: kiểm tra hợp lệ ───────────────────────────────────────────────

    /// <summary>
    /// Server kiểm tra người gửi yêu cầu: trận chưa kết thúc, đúng vai, đang đứng trong vùng pallet (cho dung sai),
    /// Survivor còn hoạt động (không gục / bị bế / lên ghế), Hunter đang rảnh tay (không đang đánh, không đang bế).
    /// </summary>
    bool ServerCheckActor(ulong clientId, Role expected, out GameObject player)
    {
        player = null;
        if (MatchManager.Ended) return false;
        if (RoleAssignments.Get(clientId) != expected) return false;
        if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return false;

        player = client.PlayerObject.gameObject;
        if (!TryGetSide(player.transform.position, out _, out _, ServerRangeMargin)) return false;

        if (expected == Role.Survivor)
        {
            SurvivorState st = player.GetComponent<SurvivorState>();
            return st != null && st.IsActive;
        }

        HunterAttack atk = player.GetComponent<HunterAttack>();
        if (atk != null && !atk.ServerCanAct) return false;
        HunterCarry carry = player.GetComponent<HunterCarry>();
        if (carry != null && carry.IsCarrying) return false;
        return true;
    }

    // ── RPC: Survivor đập pallet xuống ───────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void DropServerRpc(ServerRpcParams p = default)
    {
        if (!IsStanding) return;
        if (!ServerCheckActor(p.Receive.SenderClientId, Role.Survivor, out GameObject survivorGo)) return;

        State.Value = (byte)PalletState.Dropped;
        TriggerAnimClientRpc("Drop");
        StunHuntersInDropZone();            // Hunter: choáng + đẩy ra đúng phía
        PushSurvivorOutOfPallet(p.Receive.SenderClientId, survivorGo);  // Survivor: đẩy ra đúng phía, tránh kẹt
    }

    // ── RPC: Hunter phá pallet ───────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void BreakServerRpc(ServerRpcParams p = default)
    {
        if (!IsDropped || breakInProgress) return;
        if (!ServerCheckActor(p.Receive.SenderClientId, Role.Hunter, out GameObject hunter)) return;

        // Server cũng coi Hunter là đang bận để không đánh được trong lúc phá.
        HunterAttack atk = hunter.GetComponent<HunterAttack>();
        if (atk != null) atk.ServerBeginAction(breakAnimDuration);

        breakInProgress = true;
        breakTimer = 0f;
        IsBreaking.Value = true;
        TriggerAnimClientRpc("Break");
    }

    void Update()
    {
        if (!IsServer || !breakInProgress) return;

        breakTimer += Time.deltaTime;
        if (breakTimer >= breakAnimDuration)
        {
            breakInProgress = false;
            IsBreaking.Value = false;
            State.Value = (byte)PalletState.Destroyed;
        }
    }

    // ── Choáng Hunter khi pallet rơi ─────────────────────────────────────────

    void StunHuntersInDropZone()
    {
        foreach (PlayerRoleInfo info in FindObjectsByType<PlayerRoleInfo>(FindObjectsSortMode.None))
        {
            if (info.CurrentRole != Role.Hunter) continue;

            Vector3 toHunter = info.transform.position - transform.position;
            toHunter.y = 0f;
            if (toHunter.magnitude > stunRadius) continue;

            // Đẩy Hunter ra đúng phía mà họ đang đứng.
            Vector3 pushDir = Vector3.Dot(toHunter, Forward) >= 0f ? Forward : -Forward;
            Vector3 safePos = transform.position + pushDir * (halfDepth + 0.5f);
            safePos.y = info.transform.position.y;

            HunterAttack atk = info.GetComponent<HunterAttack>();
            if (atk != null) atk.ServerBeginAction(stunDuration);

            // Di chuyển là Owner-authoritative nên việc đẩy và khóa phải chạy trên MÁY CỦA HUNTER, không phải trên server.
            StunHunterClientRpc(safePos, info.transform.eulerAngles.y, stunDuration, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { info.OwnerClientId } }
            });
            Debug.Log($"[Pallet] Hunter {info.OwnerClientId} bị choáng {stunDuration}s");
        }
    }

    /// <summary>
    /// Đẩy Survivor (người vừa đập pallet) ra đúng phía để tránh bị kẹt bên trong pallet khi nó rơi.
    /// Dựa vào dot product vị trí Survivor với trục Z (forward) của pallet, giống cách xử lý Hunter.
    /// </summary>
    void PushSurvivorOutOfPallet(ulong survivorClientId, GameObject survivorGo)
    {
        if (survivorGo == null) return;

        Vector3 toSurvivor = survivorGo.transform.position - transform.position;
        toSurvivor.y = 0f;

        // Nếu Survivor ở quá xa thì không cần đẩy
        if (toSurvivor.magnitude > stunRadius) return;

        Vector3 pushDir = Vector3.Dot(toSurvivor, Forward) >= 0f ? Forward : -Forward;
        Vector3 safePos = transform.position + pushDir * (halfDepth + 0.5f);
        safePos.y = survivorGo.transform.position.y;

        // Gửi snap tới máy của Survivor (movement Owner-authoritative)
        PushSurvivorClientRpc(safePos, survivorGo.transform.eulerAngles.y, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { survivorClientId } }
        });
        Debug.Log($"[Pallet] Survivor {survivorClientId} được đẩy ra phía ({pushDir}) khi pallet rơi");
    }

    // ── ClientRpc ─────────────────────────────────────────────────────────────

    [ClientRpc]
    void TriggerAnimClientRpc(string triggerName)
    {
        if (animator != null) animator.SetTrigger(triggerName);
    }

    /// <summary>Chỉ gửi tới máy của Hunter bị choáng: đẩy ra khỏi pallet và khóa di chuyển.</summary>
    [ClientRpc]
    void StunHunterClientRpc(Vector3 pos, float yaw, float duration, ClientRpcParams rpcParams = default)
    {
        NetworkClient local = NetworkManager.LocalClient;
        if (local == null || local.PlayerObject == null) return;

        NetworkPlayerMovement move = local.PlayerObject.GetComponent<NetworkPlayerMovement>();
        if (move == null) return;

        move.SnapTo(pos, yaw);
        move.LockFor(duration);
    }

    /// <summary>Chỉ gửi tới máy của Survivor vừa đập pallet: đẩy ra đúng phía để tránh kẹt.</summary>
    [ClientRpc]
    void PushSurvivorClientRpc(Vector3 pos, float yaw, ClientRpcParams rpcParams = default)
    {
        NetworkClient local = NetworkManager.LocalClient;
        if (local == null || local.PlayerObject == null) return;

        NetworkPlayerMovement move = local.PlayerObject.GetComponent<NetworkPlayerMovement>();
        if (move == null) return;

        move.SnapTo(pos, yaw);   // đẩy ra, không khóa di chuyển
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        Vector3 f = Forward;
        Quaternion rot = Quaternion.LookRotation(f, Vector3.up);

        // Vùng choáng Hunter (đỏ nhạt)
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.2f);
        Gizmos.DrawSphere(transform.position, stunRadius);

        // Vùng đứng để đập / trèo / phá (hộp vàng, giống VaultWindow)
        Gizmos.color = Color.yellow;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, rot, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(width, 0.1f, approachDepth * 2f));
        Gizmos.matrix = Matrix4x4.identity;

        // Điểm đáp (xanh lá)
        Gizmos.color = Color.green;
        Gizmos.DrawSphere(transform.position + f * vaultLanding, 0.12f);
        Gizmos.DrawSphere(transform.position - f * vaultLanding, 0.12f);
        Gizmos.DrawLine(transform.position - f * vaultLanding, transform.position + f * vaultLanding);

        // Nửa chiều sâu pallet (xanh dương)
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position, f * halfDepth);
        Gizmos.DrawRay(transform.position, -f * halfDepth);
    }
}
