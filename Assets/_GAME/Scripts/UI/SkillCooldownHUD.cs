using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HUD kỹ năng Survivor: 1 ô vuông góc dưới trái hiện tên kỹ năng, nút bấm, vòng cooldown.
/// Chỉ máy chủ nhân thấy. Gắn vào Canvas scene Match.
/// Tự dựng bằng code, không cần prefab.
/// </summary>
public class SkillCooldownHUD : MonoBehaviour
{
    [Header("Vị trí (góc dưới trái)")]
    [SerializeField] Vector2 offset = new Vector2(20f, 20f);
    [SerializeField] float boxSize = 80f;

    [Header("Tùy chọn")]
    [SerializeField] Font font;
    [SerializeField] Color readyColor   = new Color(0.3f, 0.85f, 0.4f, 0.9f);
    [SerializeField] Color cooldownColor = new Color(0.3f, 0.3f, 0.3f, 0.7f);
    [SerializeField] Color activeColor   = new Color(1f, 0.9f, 0.1f, 0.9f);

    // UI elements (tự tạo)
    RectTransform box;
    Image bgImage;
    Image fillImage;
    Text nameText;
    Text cdText;
    Text keyText;

    bool built;

    void Awake()
    {
        if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    void Update()
    {
        SurvivorSkill sk = SurvivorSkill.Local;
        if (sk == null || !sk.IsSpawned)
        {
            if (box != null) box.gameObject.SetActive(false);
            return;
        }

        if (!built) Build();
        box.gameObject.SetActive(true);

        bool isReady = sk.IsReady;
        bool isActive = sk.SkillActive.Value;
        float remain = sk.CooldownRemaining;
        float total  = sk.CooldownTotal;

        // Tên kỹ năng
        if (nameText != null)
            nameText.text = sk.SkillName;

        // Fill (cooldown)
        if (total > 0f && remain > 0f)
        {
            fillImage.fillAmount = remain / total;
            fillImage.color = cooldownColor;
            bgImage.color = new Color(0.15f, 0.15f, 0.15f, 0.8f);
        }
        else
        {
            fillImage.fillAmount = 0f;
            bgImage.color = isActive ? activeColor : new Color(0.1f, 0.1f, 0.1f, 0.65f);
        }

        // Chữ cooldown / trạng thái
        if (cdText != null)
        {
            if (remain > 0f)
                cdText.text = Mathf.CeilToInt(remain) + "s";
            else if (isActive)
                cdText.text = "▲";
            else
                cdText.text = "";
        }

        // Phím
        if (keyText != null)
        {
            keyText.color = isReady && !isActive ? readyColor : new Color(0.6f, 0.6f, 0.6f);
        }

        // Doctor: hiện nhắc giữ phím khi Injured
        if (sk.Class == SurvivorClass.Doctor && sk.DoctorPrompt != null)
        {
            cdText.text = sk.DoctorPrompt.Contains("...") 
                ? Mathf.RoundToInt(sk.DoctorHealPercent * 100f) + "%" 
                : "Giữ Q";
        }
    }

    void Build()
    {
        built = true;

        // ─── Khung chính ───
        var go = new GameObject("SkillBox", typeof(RectTransform), typeof(Image));
        box = go.GetComponent<RectTransform>();
        box.SetParent(transform, false);
        box.anchorMin = box.anchorMax = box.pivot = new Vector2(0f, 0f);   // góc dưới trái
        box.anchoredPosition = offset;
        box.sizeDelta = new Vector2(boxSize, boxSize);
        bgImage = go.GetComponent<Image>();
        bgImage.color = new Color(0.1f, 0.1f, 0.1f, 0.65f);
        bgImage.raycastTarget = false;

        // ─── Fill overlay (cooldown) ───
        var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        RectTransform fillRT = fillGo.GetComponent<RectTransform>();
        fillRT.SetParent(box, false);
        fillRT.anchorMin = Vector2.zero;
        fillRT.anchorMax = Vector2.one;
        fillRT.sizeDelta = Vector2.zero;
        fillImage = fillGo.GetComponent<Image>();
        fillImage.type = Image.Type.Filled;
        fillImage.fillMethod = Image.FillMethod.Vertical;
        fillImage.fillOrigin = 0;   // bottom to top
        fillImage.fillAmount = 0f;
        fillImage.color = cooldownColor;
        fillImage.raycastTarget = false;

        // ─── Tên kỹ năng ───
        nameText = MakeText(box, "Name", 13, TextAnchor.UpperCenter,
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -4f), new Vector2(0f, 22f));

        // ─── Cooldown / trạng thái ───
        cdText = MakeText(box, "CD", 18, TextAnchor.MiddleCenter,
            Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero);
        cdText.fontStyle = FontStyle.Bold;

        // ─── Phím bấm ───
        keyText = MakeText(box, "Key", 12, TextAnchor.LowerCenter,
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 4f), new Vector2(0f, 18f));
        keyText.text = "[Q]";
        keyText.color = readyColor;
    }

    Text MakeText(Transform parent, string objName, int size, TextAnchor align,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
        Vector2 anchoredPos, Vector2 sizeDelta)
    {
        var go = new GameObject(objName, typeof(RectTransform), typeof(Text));
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = sizeDelta;

        Text t = go.GetComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }
}
