/// <summary>
/// Lớp nhân vật Survivor. Mỗi Survivor chọn 1 lớp ở phòng chờ → gán CharacterAssignments.
/// MatchSpawner dùng chỉ số này để spawn prefab (vẫn giữ nguyên), skill system đọc để biết lớp.
/// </summary>
public enum SurvivorClass : byte
{
    /// <summary>Tráng sĩ: hóa vàng, chịu 1 đòn từ Hunter. CD 45s.</summary>
    Knight = 0,

    /// <summary>VĐV: tăng tốc 30% tức thì, giảm dần trong 2s. CD 45s.</summary>
    Athlete = 1,

    /// <summary>Bác sĩ: tăng khả năng chữa thương, có thể tự lành mà không cần gục. Không cooldown.</summary>
    Doctor = 2,

    /// <summary>Ma thuật sư: tàng hình 2s nhưng vẫn lộ dấu chân. CD 20s.</summary>
    Sorcerer = 3,
}
