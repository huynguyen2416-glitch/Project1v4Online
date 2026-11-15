using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Phím tắt để thử bot ngay trên nhân vật của chính mình (chỉ trong Editor và Development Build). Tự khởi động, không cần đặt vào Scene.
///   F9  : bot tiếp quản NGAY. Bấm lần nữa: trả lại cho bạn.
///   F10 : giả lập rớt mạng: đứng im 20 giây rồi bot tiếp quản. Bấm lần nữa: trả lại cho bạn.
/// Lưu ý: đây là bot chạy trên máy chủ nhân của nhân vật. Bot do server sở hữu (người thay thế) cần lộ trình C1 và C3.
/// </summary>
public class BotDebugHotkey : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Debug.isDebugBuild) return;
        GameObject go = new GameObject("BotDebugHotkey");
        DontDestroyOnLoad(go);
        go.AddComponent<BotDebugHotkey>();
    }

    void Update()
    {
        bool f9 = Input.GetKeyDown(KeyCode.F9);
        bool f10 = Input.GetKeyDown(KeyCode.F10);
        if (!f9 && !f10) return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsClient || nm.LocalClient == null || nm.LocalClient.PlayerObject == null) return;

        BotController bc = BotController.Of(nm.LocalClient.PlayerObject.gameObject);
        if (bc.State != BotController.BotState.Off) bc.Release();
        else if (f9) bc.StartBot();
        else bc.BeginStandIn();
    }
}
