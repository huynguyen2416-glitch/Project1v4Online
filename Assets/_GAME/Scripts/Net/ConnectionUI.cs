using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

// Thay cho phần nhập IP/port của ConnectionUI. Gắn lên object NetworkManager (Lobby).
public class RelayConnectionUI : MonoBehaviour
{
    public static string JoinCode { get; private set; } = "";   // LobbyUI đọc để hiện mã phòng

    string codeInput = "";
    string status = "Chưa kết nối";
    ISession session;
    bool busy;

    static async Task EnsureSignedInAsync()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            var opt = new InitializationOptions();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Mỗi tiến trình một profile: 2 bản chạy cùng máy không bị trùng PlayerId
            opt.SetProfile("p" + System.Diagnostics.Process.GetCurrentProcess().Id);
#endif
            await UnityServices.InitializeAsync(opt);
        }
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    async void HostAsync()
    {
        if (busy) return;
        busy = true;
        try
        {
            status = "Đang tạo phòng...";
            await EnsureSignedInAsync();
            // WithRelayNetwork(): SDK xin Relay allocation, cấu hình UnityTransport
            // và tự gọi StartHost cho NetworkManager. Không gọi StartHost thủ công.
            var options = new SessionOptions { MaxPlayers = LobbyManager.MaxPlayers }
                .WithRelayNetwork();
            session = await MultiplayerService.Instance.CreateSessionAsync(options);
            JoinCode = session.Code;
            status = "Đang host. Mã phòng: " + JoinCode;
        }
        catch (System.Exception e) { status = "Lỗi: " + e.Message; Debug.LogException(e); }
        finally { busy = false; }
    }

    async void JoinAsync()
    {
        if (busy || string.IsNullOrWhiteSpace(codeInput)) return;
        busy = true;
        try
        {
            status = "Đang vào phòng...";
            await EnsureSignedInAsync();
            string code = codeInput.Trim().ToUpperInvariant();
            session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
            status = "Đã vào phòng";
        }
        catch (System.Exception e) { status = "Lỗi: " + e.Message; Debug.LogException(e); }
        finally { busy = false; }
    }

    async void LeaveAsync()
    {
        try
        {
            if (session != null)
            {
                if (session.IsHost) await session.AsHost().DeleteAsync();
                else await session.LeaveAsync();
                session = null;
            }
        }
        catch (System.Exception e) { Debug.LogException(e); }
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening) nm.Shutdown();
        JoinCode = "";
        status = "Chưa kết nối";
    }

    void OnGUI()
    {
        NetworkManager nm = NetworkManager.Singleton;
        GUILayout.BeginArea(new Rect(10, 10, 320, 260), GUI.skin.box);
        GUILayout.Label(status);
        if (nm != null && !nm.IsListening && !busy)
        {
            if (GUILayout.Button("Host (tạo phòng)")) HostAsync();
            GUILayout.Label("Mã phòng:");
            codeInput = GUILayout.TextField(codeInput, 8);
            if (GUILayout.Button("Join (vào bằng mã)")) JoinAsync();
        }
        else if (nm != null && nm.IsListening)
        {
            if (GUILayout.Button("Rời phòng")) LeaveAsync();
        }
        GUILayout.EndArea();
    }
}