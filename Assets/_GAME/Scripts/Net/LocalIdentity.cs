using System;
using System.Text;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

/// <summary>
/// Định danh BỀN của máy này, dùng để server nhận ra "đây là người chơi cũ nối lại".
///  - Có đăng nhập Unity (chơi qua Relay): dùng PlayerId của Authentication.
///  - Chơi LAN (chưa đăng nhập): dùng một GUID lưu trong PlayerPrefs, nên vẫn giữ nguyên khi mở lại game.
/// Chuỗi này được gửi kèm lúc kết nối (ConnectionData) và được server kiểm tra trong ConnectionGate.
/// </summary>
public static class LocalIdentity
{
    const string GuidKey = "net.deviceGuid";
    const char Sep = '|';

    /// <summary>ID bền của người chơi này.</summary>
    public static string PlayerId
    {
        get
        {
            if (UnityServices.State == ServicesInitializationState.Initialized
                && AuthenticationService.Instance.IsSignedIn)
                return AuthenticationService.Instance.PlayerId;
            return DeviceGuid;
        }
    }

    /// <summary>GUID của máy (tạo một lần, lưu PlayerPrefs).</summary>
    public static string DeviceGuid
    {
        get
        {
            string g = PlayerPrefs.GetString(GuidKey, "");
            if (string.IsNullOrEmpty(g))
            {
                g = Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(GuidKey, g);
                PlayerPrefs.Save();
            }
#if UNITY_EDITOR
            // Nhiều cửa sổ Editor (Multiplayer Play Mode) dùng chung PlayerPrefs, nên thêm PID để không trùng ID.
            g += "_" + System.Diagnostics.Process.GetCurrentProcess().Id;
#endif
            return g;
        }
    }

    /// <summary>Gọi TRƯỚC StartClient / Join / Reconnect: gắn "phiên bản|PlayerId" vào dữ liệu kết nối.</summary>
    public static void ApplyConnectionData(NetworkManager nm)
    {
        if (nm == null) return;
        nm.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(Application.version + Sep + PlayerId + Sep + PlayerProfile.Name);
    }

    /// <summary>Server tách dữ liệu kết nối ra phiên bản và PlayerId.</summary>
    public static bool TryParse(byte[] payload, out string version, out string playerId, out string name)
    {
        version = ""; playerId = ""; name = "";
        if (payload == null || payload.Length == 0) return false;
        string[] parts = Encoding.UTF8.GetString(payload).Split(Sep);
        if (parts.Length < 2 || parts[1].Length == 0) return false;
        version = parts[0]; playerId = parts[1];
        if (parts.Length >= 3) name = PlayerProfile.Sanitize(parts[2]);
        return true;
    }
}
