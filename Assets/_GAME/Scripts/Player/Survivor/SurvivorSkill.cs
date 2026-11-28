using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Hệ thống kỹ năng Survivor. Gắn vào prefab Survivor.
/// Server quyết định lớp từ CharacterAssignments; mỗi máy đọc NetworkVariable để biết lớp + cooldown.
/// Owner bấm Q → gửi RPC lên server → server kiểm tra cooldown → kích hoạt → đồng bộ trạng thái.
///
/// 4 lớp:
///  0 Knight  – hóa vàng, chịu 1 đòn từ Hunter, CD 45s.
///  1 Athlete – tăng tốc 70% tức thì giảm dần trong 2s, CD 45s.
///  2 Doctor  – tự hồi máu (Injured→Healthy) không cần gục, không cooldown.
///  3 Sorcerer – tàng hình 2s (vẫn lộ dấu chân), CD 20s.
/// </summary>
[RequireComponent(typeof(SurvivorState))]
public class SurvivorSkill : NetworkBehaviour
{
    public static SurvivorSkill Local { get; private set; }

    [Header("Phím kích hoạt kỹ năng")]
    [SerializeField] KeyCode skillKey = KeyCode.Q;

    [Header("Knight – Tráng sĩ")]
    [SerializeField] float knightCooldown = 45f;
    [SerializeField] Color knightShieldColor = new Color(1f, 0.9f, 0.1f);   // vàng

    [Header("Athlete – VĐV")]
    [SerializeField] float athleteCooldown = 45f;
    [SerializeField] float athleteBoostPercent = 0.70f;   // 70%
    [SerializeField] float athleteBoostDuration = 2f;

    [Header("Doctor – Bác sĩ")]
    [SerializeField] float doctorHealDuration = 8f;   // thời gian giữ phím để tự lành (giây)

    [Header("Sorcerer – Ma thuật sư")]
    [SerializeField] float sorcererCooldown = 20f;
    [SerializeField] float sorcererDuration = 2f;

    // ─── Đồng bộ ─────────────────────────────────────────
    /// <summary>Lớp nhân vật. Server ghi lúc spawn.</summary>
    public readonly NetworkVariable<byte> ClassId = new NetworkVariable<byte>(0);

    /// <summary>Thời điểm server mà kỹ năng sẵn sàng (0 = sẵn sàng ngay). Mỗi máy đọc để hiện cooldown.</summary>
    public readonly NetworkVariable<float> CooldownReadyAt = new NetworkVariable<float>(0f);

    /// <summary>Tiến độ tự lành của Doctor (0..100, %). Server ghi khi đổi, mọi máy đọc để UI hiện cả tiến độ đã lưu.</summary>
    public readonly NetworkVariable<byte> DoctorHealPct = new NetworkVariable<byte>(0);

    /// <summary>Kỹ năng đang hoạt động (Knight shield / Sorcerer invis). Mỗi máy đọc để áp hiệu ứng.</summary>
    public readonly NetworkVariable<bool> SkillActive = new NetworkVariable<bool>(false);

    // ─── Nội bộ ──────────────────────────────────────────
    SurvivorState state;
    NetworkPlayerMovement move;
    CharacterInput input;
    BotController botCtrlCache;   // lấy lười: BotController được gắn SAU khi object spawn

    // Server – Knight
    bool knightShieldUp;

    // Server – Sorcerer
    float sorcererEndTime;

    // Server – Doctor tự lành
    float doctorHealProgress;   // 0..1, được GIỮ LẠI khi nhả phím; chỉ về 0 khi lành xong hoặc không còn Bị thương
    bool doctorHealing;         // owner đang giữ phím

    // Owner – Doctor
    bool ownerDoctorHolding;

    public SurvivorClass Class => (SurvivorClass)ClassId.Value;

    /// <summary>Cooldown còn lại (giây). Dùng cho UI.</summary>
    public float CooldownRemaining
    {
        get
        {
            if (Class == SurvivorClass.Doctor) return 0f;   // Doctor không có cooldown
            float left = CooldownReadyAt.Value - NetworkTime;
            return left > 0f ? left : 0f;
        }
    }

    /// <summary>Tổng cooldown (giây) của lớp hiện tại.</summary>
    public float CooldownTotal
    {
        get
        {
            switch (Class)
            {
                case SurvivorClass.Knight: return knightCooldown;
                case SurvivorClass.Athlete: return athleteCooldown;
                case SurvivorClass.Sorcerer: return sorcererCooldown;
                default: return 0f;
            }
        }
    }

    /// <summary>Kỹ năng sẵn sàng chưa.</summary>
    public bool IsReady => CooldownRemaining <= 0f;

    /// <summary>Tên kỹ năng.</summary>
    public string SkillName
    {
        get
        {
            switch (Class)
            {
                case SurvivorClass.Knight: return "Tráng sĩ";
                case SurvivorClass.Athlete: return "Tăng tốc";
                case SurvivorClass.Doctor: return "Tự lành";
                case SurvivorClass.Sorcerer: return "Tàng hình";
                default: return "";
            }
        }
    }

    /// <summary>Mô tả ngắn.</summary>
    public string SkillDescription
    {
        get
        {
            switch (Class)
            {
                case SurvivorClass.Knight: return "Chịu 1 đòn";
                case SurvivorClass.Athlete: return "+70% tốc độ";
                case SurvivorClass.Doctor: return "Tự hồi máu";
                case SurvivorClass.Sorcerer: return "Tàng hình 2s";
                default: return "";
            }
        }
    }

    float NetworkTime => (float)(NetworkManager.Singleton != null
        ? NetworkManager.Singleton.ServerTime.Time : Time.timeAsDouble);

    // ═══════════════════════ Unity ═══════════════════════

    void Awake()
    {
        state = GetComponent<SurvivorState>();
        move = GetComponent<NetworkPlayerMovement>();
        input = CharacterInput.Of(gameObject);
    }

    public override void OnNetworkSpawn()
    {
        if (IsLocalPlayer) Local = this;

        // Server: gán lớp dựa vào CharacterAssignments (chỉ số nhân vật = lớp).
        // Bot không phải PlayerObject và lúc này chưa có BotController (OwnerClientId = host): MatchSpawner.SpawnBot
        // gọi ServerSetClass sau khi gắn BotController, nên bỏ qua ở đây để bot không nhận nhầm lớp của host.
        if (IsServer && NetworkObject.IsPlayerObject)
            ServerSetClass(CharacterAssignments.Get(OwnerClientId));

        ClassId.OnValueChanged += OnClassChanged;
        SkillActive.OnValueChanged += OnActiveChanged;
    }

    /// <summary>SERVER: đặt lớp cho Survivor (bot gọi sau khi spawn; người chơi gọi trong OnNetworkSpawn). Chỉ có tác dụng với vai Survivor.</summary>
    public void ServerSetClass(int ci)
    {
        if (!IsServer) return;
        PlayerRoleInfo info = GetComponent<PlayerRoleInfo>();
        if (info != null && info.CurrentRole == Role.Survivor)
            ClassId.Value = (byte)ci;
    }

    /// <summary>SERVER: khôi phục cooldown và khiên cho Survivor mới sau khi nối lại, để rớt mạng không reset kỹ năng.</summary>
    public void ServerRestoreSkill(float readyAt, bool shieldUp)
    {
        if (!IsServer) return;
        if (readyAt > CooldownReadyAt.Value) CooldownReadyAt.Value = readyAt;
        if (Class == SurvivorClass.Knight && shieldUp && !SkillActive.Value)
        {
            knightShieldUp = true;
            SkillActive.Value = true;
        }
    }

    /// <summary>SERVER: khiên Tráng sĩ đang bật (ReconnectManager lưu lại).</summary>
    public bool ServerShieldUp => knightShieldUp;

    /// <summary>Màu đè lên màu trạng thái (khiên vàng của Tráng sĩ). SurvivorState.ApplyTint hỏi để không ghi đè.</summary>
    public bool TryGetTintOverride(out Color c)
    {
        if (Class == SurvivorClass.Knight && SkillActive.Value) { c = knightShieldColor; return true; }
        c = default;
        return false;
    }

    public override void OnNetworkDespawn()
    {
        if (Local == this) Local = null;
        ClassId.OnValueChanged -= OnClassChanged;
        SkillActive.OnValueChanged -= OnActiveChanged;
    }

    void OnClassChanged(byte old, byte nw) { /* UI tự cập nhật từ ClassId */ }

    void OnActiveChanged(bool old, bool nw)
    {
        // Áp hiệu ứng trên mọi máy
        if (Class == SurvivorClass.Knight)
        {
            ApplyKnightVisual(nw);
            if (nw) SoundManager.I?.PlaySFX(SoundManager.I.skillKnight, transform.position);
            else if (old && !nw) SoundManager.I?.PlaySFX(SoundManager.I.skillKnightAbsorb, transform.position);
        }
        else if (Class == SurvivorClass.Sorcerer)
        {
            ApplySorcererVisual(nw);
            if (nw) SoundManager.I?.PlaySFX(SoundManager.I.skillSorcerer, transform.position);
        }
    }

    void Update()
    {
        if (!IsSpawned) return;

        if (IsOwner) OwnerUpdate();
        if (IsServer) ServerUpdate();
    }

    // ═══════════════════════ Owner ═══════════════════════

    void OwnerUpdate()
    {
        if (MatchManager.Ended) return;
        if (state == null || state.IsOut) return;

        // Bot (và bot thay người rớt mạng) do host sở hữu nên cũng chạy OwnerUpdate: không được đọc bàn phím của host.
        if (botCtrlCache == null) botCtrlCache = GetComponent<BotController>();
        if (botCtrlCache != null && botCtrlCache.State != BotController.BotState.Off) return;

        // Doctor: giữ phím Q liên tục để tự lành (Injured → Healthy, không cần gục)
        if (Class == SurvivorClass.Doctor)
        {
            bool wantHold = state.Current == SurvivorHealth.Injured && Input.GetKey(skillKey);
            if (wantHold != ownerDoctorHolding)
            {
                ownerDoctorHolding = wantHold;
                DoctorHoldServerRpc(wantHold);
                if (move != null) move.SetLocked(this, wantHold);
            }
            return;
        }

        // Các lớp khác: bấm Q 1 lần
        if (Input.GetKeyDown(skillKey))
            UseSkillServerRpc();
    }

    // ═══════════════════════ Server ═══════════════════════

    void ServerUpdate()
    {
        // Sorcerer: hết thời gian tàng hình
        if (Class == SurvivorClass.Sorcerer && SkillActive.Value && Time.time >= sorcererEndTime)
        {
            SkillActive.Value = false;
        }

        // Knight: mất shield khi trạng thái đổi (đã chịu đòn rồi server xử lý ở ServerHitWithShield)
        if (Class == SurvivorClass.Knight && knightShieldUp && !SkillActive.Value)
        {
            knightShieldUp = false;
        }

        // Doctor: không còn Bị thương (bị đánh gục, được người khác chữa, bị loại...) thì bỏ tiến độ đã lưu
        if (Class == SurvivorClass.Doctor && state.Current != SurvivorHealth.Injured
            && (doctorHealProgress > 0f || doctorHealing))
        {
            doctorHealing = false;
            doctorHealProgress = 0f;
            SyncDoctorPct();
        }

        // Doctor: server cộng tiến độ (chỉ khi đang giữ phím; nhả phím thì dừng nhưng GIỮ tiến độ)
        if (Class == SurvivorClass.Doctor && doctorHealing)
        {
            if (MatchManager.Ended)
            {
                doctorHealing = false;
                return;
            }

            doctorHealProgress += Time.deltaTime / Mathf.Max(0.1f, doctorHealDuration);
            SyncDoctorPct();
            if (doctorHealProgress >= 1f)
            {
                doctorHealProgress = 0f;
                doctorHealing = false;
                SyncDoctorPct();
                state.ServerHeal();   // Injured → Healthy
                // Báo owner mở khóa di chuyển
                DoctorReleaseClientRpc(new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
                });
            }
        }
    }

    // ─── RPC ─────────────────────────────────────────────

    [ServerRpc]
    void UseSkillServerRpc()
    {
        if (state == null || !state.IsActive || MatchManager.Ended) return;

        float now = NetworkTime;
        if (CooldownReadyAt.Value > now) return;   // còn cooldown

        switch (Class)
        {
            case SurvivorClass.Knight:
                knightShieldUp = true;
                SkillActive.Value = true;
                CooldownReadyAt.Value = now + knightCooldown;
                break;

            case SurvivorClass.Athlete:
                // Tăng tốc 70% giảm dần trong 2s – gửi RPC cho owner
                AthleteBoostClientRpc(1f + athleteBoostPercent, athleteBoostDuration,
                    new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
                    });
                CooldownReadyAt.Value = now + athleteCooldown;
                break;

            case SurvivorClass.Sorcerer:
                sorcererEndTime = Time.time + sorcererDuration;
                SkillActive.Value = true;
                CooldownReadyAt.Value = now + sorcererCooldown;
                break;
        }
    }

    [ServerRpc]
    void DoctorHoldServerRpc(bool holding)
    {
        // Nhả phím chỉ tạm dừng: tiến độ vẫn được giữ cho lần giữ phím sau.
        doctorHealing = holding && Class == SurvivorClass.Doctor && state.Current == SurvivorHealth.Injured;
    }

    void SyncDoctorPct()
    {
        byte pct = (byte)Mathf.Clamp(Mathf.RoundToInt(doctorHealProgress * 100f), 0, 100);
        if (DoctorHealPct.Value != pct) DoctorHealPct.Value = pct;
    }

    [ClientRpc]
    void AthleteBoostClientRpc(float multiplier, float duration, ClientRpcParams rpcParams = default)
    {
        if (IsOwner && move != null)
        {
            move.AddSpeedBoost(multiplier, duration);
            SoundManager.I?.PlaySFX(SoundManager.I.skillAthlete, transform.position);
        }
    }

    [ClientRpc]
    void DoctorReleaseClientRpc(ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        ownerDoctorHolding = false;
        if (move != null) move.SetLocked(this, false);
        SoundManager.I?.PlaySFX(SoundManager.I.skillDoctor, transform.position);
    }

    // ═══════════════════ Knight – Chịu đòn ═══════════════════

    /// <summary>
    /// Gọi từ SurvivorState.ServerHit() TRƯỚC khi trừ máu.
    /// Trả true nếu Knight đang có shield → hấp thụ đòn, không trừ máu.
    /// </summary>
    public bool TryAbsorbHit()
    {
        if (!IsServer) return false;
        if (Class != SurvivorClass.Knight || !knightShieldUp) return false;

        knightShieldUp = false;
        SkillActive.Value = false;   // tắt hiệu ứng vàng
        return true;   // đòn bị hấp thụ
    }

    // ═══════════════════ Sorcerer – Tàng hình ═══════════════════

    /// <summary>Mọi máy gọi: Sorcerer đang tàng hình?</summary>
    public bool IsInvisible => Class == SurvivorClass.Sorcerer && SkillActive.Value;

    // ═══════════════════ Doctor – Tự lành ═══════════════════

    /// <summary>Tiến độ tự lành (0..1). UI đọc.</summary>
    public float DoctorHealPercent => DoctorHealPct.Value / 100f;

    /// <summary>Lời nhắc cho InteractUI khi Doctor đang tự lành.</summary>
    public string DoctorPrompt
    {
        get
        {
            if (!IsOwner || Class != SurvivorClass.Doctor) return null;
            if (state.Current != SurvivorHealth.Injured) return null;
            int pct = DoctorHealPct.Value;
            if (ownerDoctorHolding) return $"Đang tự lành... {pct}%";
            return pct > 0 ? $"Giữ [{skillKey}] để tiếp tục tự lành ({pct}%)" : $"Giữ [{skillKey}] để tự lành";
        }
    }

    // ═══════════════════ Hiệu ứng ═══════════════════

    // Toàn bộ việc bật/tắt renderer và tô màu nằm ở SurvivorState (một nơi duy nhất ghi), nên hai bên không đè nhau:
    // SurvivorState.Apply đọc IsInvisible và TryGetTintOverride, còn ở đây chỉ báo nó vẽ lại.
    void ApplyKnightVisual(bool on) { if (state != null) state.RefreshVisuals(); }

    void ApplySorcererVisual(bool on) { if (state != null) state.RefreshVisuals(); }
}