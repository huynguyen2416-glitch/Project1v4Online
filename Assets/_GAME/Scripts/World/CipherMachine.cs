using UnityEngine;

/// <summary>
/// Máy mã hóa. Gắn NetworkObject + script này vào CipherMachine_1..7 trong scene Match.
/// Đủ 5 máy thì còi vang và các máy còn lại bị khóa (đúng luật Identity V).
/// Giải xong: đèn con sáng dần lên (mọi máy đều thấy), tùy chọn phát hạt.
/// </summary>
public class CipherMachine : NetInteractable
{
    [Header("Hiệu ứng khi giải xong")]
    [Tooltip("Point Light là con của máy, để tắt sẵn cũng được. Trống = không có đèn.")]
    [SerializeField] Light doneLight;
    [SerializeField] Color doneColor = new Color(1.0f, 1.0f, 1.0f);
    [SerializeField] float doneIntensity = 3f;
    [SerializeField] float fadeInSeconds = 0.6f;
    [Tooltip("Tùy chọn: Particle System phát một lần lúc vừa xong (tia lửa, khói...).")]
    [SerializeField] ParticleSystem doneBurst;

    float lightTarget;

    public override string PromptText => "Giải mã máy";
    public override bool TerrorShockOnUse => true;
    public override bool RequireLineOfSight => true;   // không giải mã xuyên tường

    public override bool IsAvailable =>
        !Done.Value && !(MatchManager.Instance != null && MatchManager.Instance.GatesPowered.Value);

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        Done.OnValueChanged += OnDoneChanged;
        ApplyDone(Done.Value, false);   // người vào muộn thấy máy đã xong sáng sẵn
    }

    public override void OnNetworkDespawn()
    {
        Done.OnValueChanged -= OnDoneChanged;
        base.OnNetworkDespawn();
    }

    void OnDoneChanged(bool previous, bool value) => ApplyDone(value, true);

    void ApplyDone(bool done, bool animate)
    {
        lightTarget = done ? doneIntensity : 0f;
        if (doneLight != null)
        {
            doneLight.color = doneColor;
            if (!animate) { doneLight.intensity = lightTarget; doneLight.enabled = done; }
            else if (done) doneLight.enabled = true;
        }
        if (done && animate && doneBurst != null) doneBurst.Play();
    }

    protected override void Update()
    {
        base.Update();
        if (doneLight == null || !doneLight.enabled) return;
        float step = doneIntensity * Time.deltaTime / Mathf.Max(0.05f, fadeInSeconds);
        doneLight.intensity = Mathf.MoveTowards(doneLight.intensity, lightTarget, step);
    }

    protected override void OnCompleted()
    {
        if (MatchManager.Instance != null) MatchManager.Instance.OnMachineCompleted();
    }
}