# Lộ trình đồ án game 4v1 – Nhóm 11 (cập nhật 27/11/2026)

## Quyết định mới nhất
- Người chơi KHÔNG thấy tên của chính mình trong trận:
  - Tên trên đầu nhân vật (PlayerNameTag): ẩn bằng `IsLocalPlayer`.
  - Ô trạng thái Survivor (SurvivorStatusHUD): ô của mình ghi "Bạn" + biểu tượng lớp, không ghi tên.
- Luật hiển thị tên giữ nguyên: chỉ Survivor thấy tên Survivor; Hunter và Survivor không thấy tên nhau.

## Lỗi đang xử lý: bot không spawn ở map 2
- Đã thêm log chẩn đoán vào MatchSpawner (tên scene, bot trong roster, mọi chỗ return im lặng của SpawnBot).
- Mỗi lần nạp bản đồ đều reset danh sách bot đã spawn; một bot lỗi không làm các bot sau bị bỏ.
- Chờ log Console khi chạy map 2 để xác định nguyên nhân thật (xem hướng dẫn trong chat).
- Cần kiểm tra: tên scene map 2 trùng giữa LobbyManager.mapScenes, MatchSpawner.extraMatchScenes và Build Settings.

## Việc đã xong (gần đây)
- Tên người chơi (PlayerProfile, NameAssignments, ConnectionData), reconnect trận 2
- Animation Survivor/Hunter theo danh sách đã chốt, SpawnPointSet + MatchSpawner (5/9 điểm, không trùng)
- Ẩn tên của chính mình (tên đầu nhân vật + HUD)

## Đang/sắp làm
- Hitbox đòn đánh Hunter hình quạt, bế người/đặt ghế không xuyên tường
- Ability + StatusEffects, skill Ảo thuật gia → Côn đồ → Bác sĩ
- Bake NavMesh map Match, bot thay người disconnect (đứng im 20 giây rồi bot nhận vai)
- Relay (WAN), luồng tạo phòng → lobby → vào trận

## Cần test thủ công sau thay đổi này
1. Host: tên trên đầu mình ẩn; tên đồng đội hiện.
2. Client vào sau: tên mình ẩn ngay từ khi spawn.
3. Hunter: không thấy tên Survivor; Survivor không thấy tên Hunter.
4. Ngắt kết nối rồi vào lại: tên mình vẫn ẩn; bot thay thế bị gỡ.
5. Ô HUD: ô của mình ghi "Bạn", ô đồng đội ghi tên.
