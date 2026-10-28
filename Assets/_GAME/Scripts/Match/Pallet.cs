using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public enum PalletState : byte { Standing = 0, Dropped = 1, Destroyed = 2 }

/// <summary>
/// Pallet - co che giong Dead by Daylight / Identity V.
///
/// LUONG HOAT DONG:
///   Standing  → Survivor nhan F → Dropped (tuc thi + choi anim "Drop")
///   Dropped   → Hunter dung trong vung ROI XUONG → choan + bi day ra dung phia
///             → Survivor nhan F → Vault qua (nhu cua so)
///             → Hunter nhan F (1 lan) → Bat dau pha (1.5s anim) → Destroyed
///   Destroyed → khong con gi
///
/// SETUP PREFAB:
///   Root:       NetworkObject, Rigidbody (isKinematic=true), Pallet.cs, Animator
///   blockCollider: BoxCollider (non-trigger) - pallet co the chan di chuyen
///   standingVisual: child mesh pallet dung
///   droppedVisual:  child mesh pallet nam
///
/// TRUC Z (Forward) phai VUONG GOC voi than pallet (huong nguoi chay qua).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class Pallet : NetworkBehaviour
{
    // Danh sach tat ca pallet trong scene - PalletInteract dung de scan
    public static readonly List<Pallet> All = new List<Pallet>();

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Visuals & Animator")]
    [Tooltip("Child GameObject hien thi pallet dung thang")]
    [SerializeField] GameObject standingVisual;
    [Tooltip("Child GameObject hien thi pallet nam xuong (co animator de choi clip Drop)")]
    [SerializeField] GameObject droppedVisual;
    [Tooltip("Animator tren root prefab. Trigger: 'Drop', 'Break'")]
    [SerializeField] Animator   animator;

    [Header("Collider")]
    [Tooltip("BoxCollider (NON-trigger) chan di chuyen khi Dropped. Bat/tat tu dong.")]
    [SerializeField] Collider blockCollider;

    [Header("Kich thuoc pallet")]
    [Tooltip("Nua chieu sau pallet theo truc Z (met). Dung de day Hunter ra dung phia khi stun. Chinh khop voi mesh.")]
    [SerializeField] float halfDepth = 0.7f;

    [Header("Thong so Stun")]
    [Tooltip("Ban kinh OverlapSphere tai thoi diem pallet roi xuong (met)")]
    [SerializeField] float stunRadius   = 2.2f;
    [Tooltip("Thoi gian Hunter bi choan (giay)")]
    [SerializeField] float stunDuration = 2.5f;

    [Header("Thong so Vault (Survivor)")]
    [Tooltip("Khoang cach Survivor co the dap pallet (Standing) hoac vault (Dropped)")]
    [SerializeField] float dropRadius     = 3f;
    [Tooltip("Reu chi dung trong goc nay (theo X cua pallet) thi moi vault duoc (met)")]
    [SerializeField] float vaultHalfWidth = 1.2f;
    [Tooltip("Diem dap sau khi vault qua (met tu tam pallet)")]
    [SerializeField] float vaultLanding   = 1.3f;
    [Tooltip("Do nang len khi vault")]
    [SerializeField] float vaultArcHeight = 0.2f;
    [Tooltip("Thoi gian vault (giay) - chinh khop clip animation")]
    [SerializeField] float vaultDuration  = 0.55f;

    [Header("Thong so Break (Hunter)")]
    [Tooltip("Khoang cach Hunter co the pha pallet")]
    [SerializeField] float breakRadius   = 2.5f;
    [Tooltip("Thoi gian animation pha chay tren pallet truoc khi bien mat (giay)")]
    [SerializeField] float breakAnimDuration = 1.5f;

    // ── NetworkVariables ──────────────────────────────────────────────────────

    /// <summary>Trang thai hien tai cua pallet. Moi may doc de hien thi dung.</summary>
    public readonly NetworkVariable<byte> State =
        new NetworkVariable<byte>((byte)PalletState.Standing);

    /// <summary>
    /// True khi Hunter da nhan F va animation pha dang chay.
    /// Moi may doc de: (1) UI feedback, (2) ngan vault qua pallet dang vo.
    /// </summary>
    public readonly NetworkVariable<bool> IsBreaking =
        new NetworkVariable<bool>(false);

    // ── Properties (public, doc-only) ─────────────────────────────────────────

    public PalletState CurrentState  => (PalletState)State.Value;
    public bool IsStanding           => CurrentState == PalletState.Standing;
    public bool IsDropped            => CurrentState == PalletState.Dropped;
    public bool IsDestroyed          => CurrentState == PalletState.Destroyed;

    // Doc boi PalletInteract
    public float DropRadius      => dropRadius;
    public float BreakRadius     => breakRadius;
    public float VaultHalfWidth  => vaultHalfWidth;
    public float VaultLanding    => vaultLanding;
    public float VaultDuration   => vaultDuration;
    public float VaultArcHeight  => vaultArcHeight;

    // ── Server-only ───────────────────────────────────────────────────────────

    bool  breakInProgress;   // dang dem nguoc 1.5s de pha
    float breakTimer;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        State.OnValueChanged     += OnStateChanged;
        IsBreaking.OnValueChanged += OnIsBreakingChanged;
        ApplyState(CurrentState);
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        State.OnValueChanged     -= OnStateChanged;
        IsBreaking.OnValueChanged -= OnIsBreakingChanged;
    }

    void OnStateChanged(byte _, byte next)     => ApplyState((PalletState)next);
    void OnIsBreakingChanged(bool _, bool now) => /* de trong: hien VFX neu can */ _ = now;

    void ApplyState(PalletState s)
    {
        bool standing  = s == PalletState.Standing;
        bool dropped   = s == PalletState.Dropped;

        if (standingVisual) standingVisual.SetActive(standing);
        if (droppedVisual)  droppedVisual.SetActive(dropped);
        // blockCollider KHONG co trigger, chi chan di chuyen khi Dropped
        if (blockCollider)  blockCollider.enabled = dropped;
    }

    // ── RPC: Survivor dap pallet xuong ───────────────────────────────────────

    /// <summary>PalletInteract.HandleSurvivorInput() goi khi Survivor nhan F gan pallet Standing.</summary>
    [ServerRpc(RequireOwnership = false)]
    public void DropServerRpc(ServerRpcParams p = default)
    {
        if (!IsStanding) return;
        if (RoleAssignments.Get(p.Receive.SenderClientId) != Role.Survivor) return;

        State.Value = (byte)PalletState.Dropped;
        TriggerAnimClientRpc("Drop");

        // STUN: CHI xay ra 1 lan duy nhat tai thoi diem pallet roi xuong.
        // Sau nay pallet la vat can thuan tuy, khong stun them.
        StunHuntersInDropZone();
    }

    // ── RPC: Hunter pha pallet (nhan 1 lan, anim 1.5s, roi pha) ─────────────

    /// <summary>
    /// PalletInteract.HandleHunterInput() goi khi Hunter nhan F (1 lan) gan pallet Dropped.
    /// Server bat dau dem nguoc breakAnimDuration giay, roi chuyen sang Destroyed.
    /// Hunter KHONG bi khoa di chuyen - chi pallet chay animation.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void BreakServerRpc(ServerRpcParams p = default)
    {
        if (!IsDropped || breakInProgress) return;
        if (RoleAssignments.Get(p.Receive.SenderClientId) != Role.Hunter) return;

        breakInProgress = true;
        breakTimer      = 0f;
        IsBreaking.Value = true;
        TriggerAnimClientRpc("Break");   // tat ca may choi animation pha (~1.5s)
    }

    void Update()
    {
        if (!IsServer || !breakInProgress) return;

        breakTimer += Time.deltaTime;
        if (breakTimer >= breakAnimDuration)
        {
            // Animation da xong, pha huy pallet
            breakInProgress  = false;
            IsBreaking.Value = false;
            State.Value = (byte)PalletState.Destroyed;
        }
    }

    // ── Stun khi pallet roi ──────────────────────────────────────────────────

    void StunHuntersInDropZone()
    {
        // OverlapSphere tai tam pallet, ban kinh stunRadius
        Collider[] hits = Physics.OverlapSphere(transform.position, stunRadius);
        foreach (Collider col in hits)
        {
            var roleInfo = col.GetComponent<PlayerRoleInfo>();
            if (roleInfo == null || roleInfo.CurrentRole != Role.Hunter) continue;

            var move = col.GetComponent<NetworkPlayerMovement>();
            if (move == null) continue;

            // Xac dinh phia: dot product (Vector tu tam pallet den Hunter) voi truc Z pallet
            Vector3 toHunter = col.transform.position - transform.position;
            toHunter.y = 0f;
            float dot = Vector3.Dot(toHunter.normalized, transform.forward);
            // dot >= 0: Hunter phia truoc pallet (+Z); dot < 0: phia sau (-Z)
            Vector3 pushDir = dot >= 0f ? transform.forward : -transform.forward;
            Vector3 safePos = transform.position + pushDir * (halfDepth + 0.5f);
            safePos.y = col.transform.position.y;

            move.SnapTo(safePos, col.transform.eulerAngles.y);  // day ra dung phia
            move.LockFor(stunDuration);                          // choan X giay

            var no = col.GetComponent<NetworkObject>();
            if (no != null) ShowStunVFXClientRpc(no.NetworkObjectId);

            Debug.Log($"[Pallet] Hunter '{col.name}' bi choan {stunDuration}s (phia {pushDir})");
        }
    }

    // ── Vault helpers (Survivor) ──────────────────────────────────────────────

    /// <summary>
    /// Kiem tra Survivor co the vault qua pallet nay khong.
    /// Tra ve true + side (+1/-1) + distance neu hop le.
    /// </summary>
    public bool TryGetVaultSide(Vector3 worldPos, out int side, out float distance)
    {
        Vector3 d = worldPos - transform.position;
        d.y = 0f;
        float zDot = Vector3.Dot(d, transform.forward);
        float xDot = Vector3.Dot(d, transform.right);
        side     = zDot >= 0f ? 1 : -1;
        distance = d.magnitude;
        // Survivor phai dung trong vung (ngang <= vaultHalfWidth, khoang cach <= dropRadius)
        return Mathf.Abs(xDot) <= vaultHalfWidth && distance <= dropRadius;
    }

    /// <summary>Diem dap sau khi vault (phia doi dien, giu nguyen y).</summary>
    public Vector3 GetVaultLanding(int fromSide, float y)
    {
        Vector3 p = transform.position + (-fromSide * transform.forward) * vaultLanding;
        p.y = y;
        return p;
    }

    // ── ClientRpc ─────────────────────────────────────────────────────────────

    [ClientRpc]
    void TriggerAnimClientRpc(string triggerName)
    {
        if (animator != null) animator.SetTrigger(triggerName);
    }

    [ClientRpc]
    void ShowStunVFXClientRpc(ulong hunterNetId)
    {
        if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(hunterNetId, out var no)) return;
        // Them hieu ung: no.GetComponent<HunterVFX>()?.PlayStun();
        Debug.Log($"[Pallet] VFX choan Hunter {hunterNetId}");
    }

    // ── Gizmos (Scene View) ───────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        // Vung stun (do nhat)
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.2f);
        Gizmos.DrawSphere(transform.position, stunRadius);

        // Vung drop/vault (vang)
        Gizmos.color = new Color(1f, 0.9f, 0f, 0.15f);
        Gizmos.DrawSphere(transform.position, dropRadius);

        // Diem dap vault (xanh la)
        Gizmos.color = Color.green;
        Vector3 fwd = transform.forward;
        Gizmos.DrawSphere(transform.position + fwd  * vaultLanding, 0.12f);
        Gizmos.DrawSphere(transform.position - fwd  * vaultLanding, 0.12f);
        Gizmos.DrawLine(  transform.position - fwd  * vaultLanding,
                          transform.position + fwd  * vaultLanding);

        // HalfDepth (xanh duong) - huong trong/ngoai pallet
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position,  fwd * halfDepth);
        Gizmos.DrawRay(transform.position, -fwd * halfDepth);
    }
}
