using UnityEngine;

/// <summary>
/// Quản lý âm thanh toàn game. Singleton, không bị hủy khi đổi scene.
/// Cách dùng:
///   1. Tạo 1 GameObject "SoundManager" ở scene đầu tiên (Lobby hoặc MainMenu).
///   2. Gắn script này, kéo AudioSource / AudioClip vào từng ô.
///   3. Gọi từ bất kỳ script nào: SoundManager.I.PlaySFX(SoundManager.I.palletDrop, position);
///
/// Hai loại âm thanh:
///   - SFX (hiệu ứng): PlayClipAtPoint (3D, tự hủy) hoặc PlayOneShot (2D, chồng tiếng).
///   - Nhạc nền: AudioSource riêng, loop, fade in/out.
///
/// Tự thêm clip mới: khai báo [SerializeField] AudioClip ..., kéo clip vào Inspector, gọi PlaySFX/Play2D.
/// </summary>
public class SoundManager : MonoBehaviour
{
    // ─── Singleton ───────────────────────────────────────
    public static SoundManager I { get; private set; }

    void Awake()
    {
        if (I != null && I != this) { Destroy(gameObject); return; }
        I = this;
        DontDestroyOnLoad(gameObject);
        EnsureSources();
    }

    void OnDestroy() { if (I == this) I = null; }

    // ═══════════════════════ CLIP ═══════════════════════
    // Kéo AudioClip vào Inspector. Thêm bao nhiêu tùy ý.

    [Header("─── Hunter ───")]
    public AudioClip hunterSwing;         // vung tay đánh
    public AudioClip hunterHitSurvivor;   // đánh trúng
    public AudioClip hunterMiss;          // đánh hụt
    public AudioClip hunterPickup;        // bế người
    public AudioClip hunterDrop;          // thả người
    public AudioClip hunterStun;          // bị choáng (pallet)

    [Header("─── Survivor ───")]
    public AudioClip survivorHurt;        // bị thương
    public AudioClip survivorDown;        // gục
    public AudioClip survivorHeal;        // hồi máu xong 1 bậc
    public AudioClip survivorRescued;     // được cứu khỏi ghế
    public AudioClip survivorEscaped;     // thoát qua cổng / hầm

    [Header("─── Kỹ năng ───")]
    public AudioClip skillKnight;         // Tráng sĩ kích hoạt shield
    public AudioClip skillKnightAbsorb;   // Tráng sĩ chịu đòn
    public AudioClip skillAthlete;        // VĐV tăng tốc
    public AudioClip skillDoctor;         // Bác sĩ tự lành xong
    public AudioClip skillSorcerer;       // Ma thuật sư tàng hình

    [Header("─── Pallet ───")]
    public AudioClip palletDrop;          // đập pallet
    public AudioClip palletBreak;         // phá pallet
    public AudioClip palletVault;         // trèo qua pallet
    public AudioClip palletStun;          // pallet choáng Hunter

    [Header("─── Thế giới ───")]
    public AudioClip cipherProgress;      // giải mã máy (loop ngắn / tick)
    public AudioClip cipherComplete;      // giải xong 1 máy
    public AudioClip gatePowered;         // cổng có điện (còi)
    public AudioClip gateOpen;            // cổng mở
    public AudioClip chairPlace;          // đặt lên ghế tên lửa
    public AudioClip chairTick;           // đếm ngược trên ghế
    public AudioClip vaultWindow;         // nhảy cửa sổ

    [Header("─── Chung ───")]
    public AudioClip footstepRun;         // bước chân chạy
    public AudioClip footstepCrouch;      // bước chân đi mò
    public AudioClip uiClick;             // nút bấm UI
    public AudioClip matchStart;          // trận bắt đầu
    public AudioClip matchEnd;            // trận kết thúc

    [Header("─── Nhạc nền ───")]
    public AudioClip musicLobby;
    public AudioClip musicMatch;
    public AudioClip musicChase;          // bị Hunter đuổi (tùy chọn)

    // ═══════════════════════ NGUỒN PHÁT ═══════════════════════

    [Header("─── Cài đặt ───")]
    [Range(0f, 1f)] public float sfxVolume = 1f;
    [Range(0f, 1f)] public float musicVolume = 0.4f;
    [Tooltip("Khoảng cách tối đa nghe được SFX 3D (mét).")]
    public float maxSFXDistance = 40f;

    AudioSource musicSource;
    AudioSource sfx2DSource;   // dùng cho PlayOneShot 2D (UI, thông báo)
    Coroutine musicCoroutine;
    AudioClip targetMusicClip;
    float currentMusicScale = 1f;

    float TargetMusicVolume => Mathf.Clamp01(musicVolume * currentMusicScale);

    void EnsureSources()
    {
        if (musicSource == null)
        {
            musicSource = gameObject.AddComponent<AudioSource>();
            musicSource.loop = true;
            musicSource.playOnAwake = false;
            musicSource.spatialBlend = 0f;   // 2D
            musicSource.volume = musicVolume;
        }
        if (sfx2DSource == null)
        {
            sfx2DSource = gameObject.AddComponent<AudioSource>();
            sfx2DSource.loop = false;
            sfx2DSource.playOnAwake = false;
            sfx2DSource.spatialBlend = 0f;   // 2D
        }
    }

    // ═══════════════════════ API ═══════════════════════

    /// <summary>
    /// Phát SFX 3D tại vị trí trong thế giới (tôn trọng maxSFXDistance). Mọi người gần đó đều nghe.
    /// Dùng cho: pallet, đánh, bước chân, kỹ năng...
    /// </summary>
    public void PlaySFX(AudioClip clip, Vector3 position, float volumeScale = 1f)
    {
        if (clip == null) return;
        float vol = Mathf.Clamp01(sfxVolume * volumeScale);
        if (vol <= 0f) return;

        GameObject go = new GameObject("SFX_3D");
        go.transform.position = position;
        AudioSource src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.volume = vol;
        src.spatialBlend = 1f;
        src.rolloffMode = AudioRolloffMode.Linear;
        src.minDistance = 2f;
        src.maxDistance = Mathf.Max(2f, maxSFXDistance);
        src.Play();
        Destroy(go, clip.length + 0.1f);
    }

    /// <summary>
    /// Phát SFX 2D (không phụ thuộc vị trí). Chồng tiếng được.
    /// Dùng cho: UI, thông báo, hiệu ứng chỉ local nghe.
    /// </summary>
    public void Play2D(AudioClip clip, float volumeScale = 1f)
    {
        if (clip == null || sfx2DSource == null) return;
        sfx2DSource.PlayOneShot(clip, sfxVolume * volumeScale);
    }

    /// <summary>Phát nhanh âm thanh bấm nút UI.</summary>
    public void PlayUIClick() => Play2D(uiClick);

    /// <summary>
    /// Phát SFX 3D trên 1 AudioSource cụ thể (loop, dừng được).
    /// Dùng cho: giải mã máy (loop), bước chân liên tục...
    /// </summary>
    public void PlayLoop(AudioSource source, AudioClip clip, float volumeScale = 1f)
    {
        if (source == null || clip == null) return;
        source.volume = Mathf.Clamp01(sfxVolume * volumeScale);
        if (source.spatialBlend > 0f)
        {
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 2f;
            source.maxDistance = Mathf.Max(2f, maxSFXDistance);
        }
        if (source.isPlaying && source.clip == clip) return;   // đang phát rồi
        source.clip = clip;
        source.loop = true;
        source.Play();
    }

    /// <summary>Dừng loop trên AudioSource.</summary>
    public void StopLoop(AudioSource source)
    {
        if (source != null && source.isPlaying) source.Stop();
    }

    // ─── Nhạc nền ────────────────────────────────────────

    /// <summary>Chuyển nhạc nền (fade nhanh). Truyền null để tắt nhạc.</summary>
    public void PlayMusic(AudioClip clip, float fadeTime = 0.5f, float volumeScale = 1f)
    {
        if (musicSource == null) return;

        if (clip == null) { StopMusic(fadeTime); return; }

        currentMusicScale = Mathf.Max(0f, volumeScale);
        if (targetMusicClip == clip && (musicSource.isPlaying || musicCoroutine != null))
        {
            if (musicCoroutine == null) musicSource.volume = TargetMusicVolume;
            return;
        }

        targetMusicClip = clip;
        StopMusicCoroutine();

        if (fadeTime <= 0f || !musicSource.isPlaying)
        {
            musicSource.clip = clip;
            musicSource.volume = TargetMusicVolume;
            musicSource.Play();
        }
        else
        {
            musicCoroutine = StartCoroutine(CrossfadeMusic(clip, fadeTime));
        }
    }

    public void StopMusic(float fadeTime = 0.5f)
    {
        if (musicSource == null) return;
        targetMusicClip = null;
        StopMusicCoroutine();
        if (!musicSource.isPlaying) return;
        if (fadeTime <= 0f)
        {
            musicSource.Stop();
            musicSource.volume = TargetMusicVolume;
            return;
        }
        musicCoroutine = StartCoroutine(FadeOutMusic(fadeTime));
    }

    void StopMusicCoroutine()
    {
        if (musicCoroutine != null)
        {
            StopCoroutine(musicCoroutine);
            musicCoroutine = null;
        }
    }

    System.Collections.IEnumerator CrossfadeMusic(AudioClip newClip, float duration)
    {
        float startVol = musicSource.volume;
        float half = duration * 0.5f;

        // Fade out
        for (float t = 0f; t < half; t += Time.unscaledDeltaTime)
        {
            musicSource.volume = Mathf.Lerp(startVol, 0f, t / half);
            yield return null;
        }

        musicSource.Stop();
        musicSource.clip = newClip;
        musicSource.Play();

        // Fade in
        for (float t = 0f; t < half; t += Time.unscaledDeltaTime)
        {
            musicSource.volume = Mathf.Lerp(0f, TargetMusicVolume, t / half);
            yield return null;
        }
        musicSource.volume = TargetMusicVolume;
        musicCoroutine = null;
    }

    System.Collections.IEnumerator FadeOutMusic(float duration)
    {
        float startVol = musicSource.volume;
        for (float t = 0f; t < duration; t += Time.unscaledDeltaTime)
        {
            musicSource.volume = Mathf.Lerp(startVol, 0f, t / duration);
            yield return null;
        }
        musicSource.Stop();
        musicSource.volume = TargetMusicVolume;
        musicCoroutine = null;
    }

    // ─── Tiện ích ────────────────────────────────────────

    /// <summary>Đổi âm lượng SFX (0..1). Lưu vào PlayerPrefs.</summary>
    public void SetSFXVolume(float vol)
    {
        sfxVolume = Mathf.Clamp01(vol);
        PlayerPrefs.SetFloat("sfxVolume", sfxVolume);
    }

    /// <summary>Đổi âm lượng nhạc (0..1). Lưu vào PlayerPrefs.</summary>
    public void SetMusicVolume(float vol)
    {
        musicVolume = Mathf.Clamp01(vol);
        if (musicSource != null && musicCoroutine == null) musicSource.volume = TargetMusicVolume;
        PlayerPrefs.SetFloat("musicVolume", musicVolume);
    }

    void Start()
    {
        // Khôi phục âm lượng đã lưu (chỉ áp dụng nếu hợp lệ > 0 để tránh bị tắt nhầm)
        if (PlayerPrefs.HasKey("sfxVolume")) sfxVolume = PlayerPrefs.GetFloat("sfxVolume");
        if (PlayerPrefs.HasKey("musicVolume"))
        {
            musicVolume = PlayerPrefs.GetFloat("musicVolume");
            if (musicSource != null) musicSource.volume = TargetMusicVolume;
        }
    }
}
