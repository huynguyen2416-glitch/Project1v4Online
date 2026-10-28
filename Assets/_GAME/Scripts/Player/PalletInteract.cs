using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gang vao prefab SURVIVOR va HUNTER (ca 2 dung cung 1 script, tu phan biet vai).
///
/// SURVIVOR:
///   - Gan pallet STANDING  + nhan F → dap xuong (tuc thi)
///   - Gan pallet DROPPED   + nhan F → vault qua (giong cua so)
///
/// HUNTER:
///   - Gan pallet DROPPED   + nhan F (1 lan) → pallet chay animation pha ~1.5s roi bi pha
///   - Hunter KHONG bi khoa di chuyen; pallet tu chay anim roi bien mat
///   - Khong the nhan F lan 2 khi pallet dang chay animation pha (IsBreaking=true)
/// </summary>
public class PalletInteract : NetworkBehaviour
{
    [Header("Phim tuong tac")]
    [SerializeField] KeyCode interactKey = KeyCode.Space;

    [Header("UI Prompt (tuy chon)")]
    [Tooltip("Panel/GameObject hien thi loi nhac. De trong neu khong co.")]
    [SerializeField] GameObject palletPromptUI;
    [Tooltip("Text hien thi noi dung prompt")]
    [SerializeField] UnityEngine.UI.Text promptLabel;

    // Cache components
    PlayerRoleInfo        roleInfo;
    NetworkPlayerMovement move;
    CharacterController   cc;
    SurvivorState         survivorState;

    // Trang thai
    Pallet nearestPallet;
    int    vaultSide;
    bool   vaulting;
    bool   promptVisible;
    Pallet breakingPallet;   // pallet Hunter dang pha (null neu khong pha)

    // ── Spawn ─────────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) { enabled = false; return; }
        roleInfo      = GetComponent<PlayerRoleInfo>();
        move          = GetComponent<NetworkPlayerMovement>();
        cc            = GetComponent<CharacterController>();
        survivorState = GetComponent<SurvivorState>();
    }

    // ── Update ────────────────────────────────────────────────────────────────

    void Update()
    {
        if (vaulting) return;

        bool isHunter = roleInfo != null && roleInfo.CurrentRole == Role.Hunter;

        // Theo doi trang thai pha: khi IsBreaking het hoac pallet bi pha xong -> mo khoa
        if (breakingPallet != null)
        {
            bool stillBreaking = breakingPallet.IsDropped && breakingPallet.IsBreaking.Value;
            if (!stillBreaking)
            {
                move.SetLocked(this, false);
                breakingPallet = null;
            }
        }

        // Tim pallet phu hop
        nearestPallet = isHunter
            ? FindNearestDropped()
            : FindNearestForSurvivor(out vaultSide);

        UpdatePrompt(isHunter);

        if (nearestPallet == null) return;

        if (isHunter) HandleHunterInput();
        else          HandleSurvivorInput();
    }

    // ── Tim pallet ────────────────────────────────────────────────────────────

    /// <summary>Survivor: tim pallet Standing de dap, hoac pallet Dropped de vault.</summary>
    Pallet FindNearestForSurvivor(out int side)
    {
        side = 1;
        Pallet best  = null;
        float  bestD = float.MaxValue;

        foreach (Pallet p in Pallet.All)
        {
            if (p == null || p.IsDestroyed) continue;

            if (p.IsStanding)
            {
                float d = Vector3.Distance(transform.position, p.transform.position);
                if (d <= p.DropRadius && d < bestD)
                { bestD = d; best = p; side = 1; }
            }
            else if (p.IsDropped)
            {
                if (p.TryGetVaultSide(transform.position, out int s, out float d) && d < bestD)
                { bestD = d; best = p; side = s; }
            }
        }
        return best;
    }

    /// <summary>Hunter: tim pallet Dropped gan nhat (chua hoac dang bi pha deu hien prompt).</summary>
    Pallet FindNearestDropped()
    {
        Pallet best  = null;
        float  bestD = float.MaxValue;
        foreach (Pallet p in Pallet.All)
        {
            if (p == null || !p.IsDropped) continue;
            float d = Vector3.Distance(transform.position, p.transform.position);
            if (d <= p.BreakRadius && d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    // ── Input: Survivor ───────────────────────────────────────────────────────

    void HandleSurvivorInput()
    {
        // Khong tuong tac khi dang bi khoa (bi bat, tren ghe, ...)
        if (move != null && move.IsLocked) return;
        // Survivor da het hoat dong (bi loai, thoat) -> khong vault
        if (survivorState != null && survivorState.IsOut) return;

        if (!Input.GetKeyDown(interactKey)) return;

        if (nearestPallet.IsStanding)
        {
            // Dap pallet xuong
            nearestPallet.DropServerRpc();
        }
        else if (nearestPallet.IsDropped && !nearestPallet.IsBreaking.Value)
        {
            // Vault qua (neu pallet dang bi pha thi khong vault)
            StartCoroutine(VaultRoutine(nearestPallet, vaultSide));
        }
    }

    IEnumerator VaultRoutine(Pallet pallet, int side)
    {
        vaulting      = true;
        nearestPallet = null;

        move.SetVaulting(true);
        if (cc != null) cc.enabled = false;

        Vector3 start = transform.position;
        Vector3 end   = pallet.GetVaultLanding(side, start.y);

        // Quay mat ve huong vault
        Vector3 dir = end - start; dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);

        float duration   = Mathf.Max(0.1f, pallet.VaultDuration);
        bool  interrupted = false;

        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            float k = t / duration;
            Vector3 pos = Vector3.Lerp(start, end, k);
            pos.y += Mathf.Sin(k * Mathf.PI) * pallet.VaultArcHeight;
            transform.position = pos;

            // Dung lai neu pallet bi pha giua chung (Hunter pha pallet -> vault abort)
            if (pallet.IsDestroyed) { interrupted = true; break; }
            // Survivor bi danh guc giua chung
            if (survivorState != null && !survivorState.IsActive) { interrupted = true; break; }

            yield return null;
        }

        if (!interrupted) transform.position = end;
        if (cc != null) cc.enabled = survivorState == null || !survivorState.IsOut;
        move.SetVaulting(false);
        vaulting = false;
    }

    // ── Input: Hunter ─────────────────────────────────────────────────────────

    void HandleHunterInput()
    {
        // Dang bi khoa (dang pha) -> khong lam gi
        if (breakingPallet != null) return;

        // Khong pha khi pallet dang chay animation pha (tranh double-trigger)
        if (nearestPallet.IsBreaking.Value) return;

        if (Input.GetKeyDown(interactKey))
        {
            // Khoa di chuyen Hunter trong luc pallet chay animation pha
            move.SetLocked(this, true);
            breakingPallet = nearestPallet;
            nearestPallet.BreakServerRpc();
        }
    }

    // ── Prompt UI ─────────────────────────────────────────────────────────────

    void UpdatePrompt(bool isHunter)
    {
        bool show = nearestPallet != null;
        if (show != promptVisible)
        {
            promptVisible = show;
            if (palletPromptUI != null) palletPromptUI.SetActive(show);
        }
        if (!show || promptLabel == null) return;

        if (isHunter)
        {
            promptLabel.text = nearestPallet.IsBreaking.Value
                ? "Dang pha pallet..."
                : $"[{interactKey}] Pha pallet";
        }
        else if (nearestPallet.IsStanding)
            promptLabel.text = $"[{interactKey}] Dap pallet";
        else
            promptLabel.text = $"[{interactKey}] Treo qua";
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public override void OnNetworkDespawn()
    {
        if (breakingPallet != null) { move?.SetLocked(this, false); breakingPallet = null; }
        if (palletPromptUI != null) palletPromptUI.SetActive(false);
    }
}
