public enum SurvivorHealth : byte
{
    Healthy = 0,     // Khỏe
    Injured = 1,     // Bị thương (trúng 1 đòn)
    Downed = 2,      // Gục (trúng đòn thứ 2): bò chậm, không làm được gì
    OnChair = 3,     // Bị trói trên ghế tên lửa, đếm ngược rồi bị loại
    Escaped = 4,     // Đã thoát: gắn cờ, không bị tấn công, không tương tác nữa
    Eliminated = 5,  // Bị loại
    Carried = 6      // Đang bị Hunter bế (tuần 3)
}
