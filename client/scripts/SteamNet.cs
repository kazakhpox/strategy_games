using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using IH.Shared;
using Steamworks;

namespace IH.Client;

public sealed class SteamGame
{
    public ulong HostId;
    public string HostName = "", Info = "";
}

/// <summary>
/// Optional Steam transport. The game has no Steam app of its own, so it runs under Valve's public test
/// app "Spacewar" (AppID 480). Steam is used for two things only: a lobby list to find hosts, and P2P
/// messages (relayed by Valve, so no port forwarding or VPN) that tunnel the game's normal UDP traffic.
/// </summary>
public static class SteamNet
{
    public const uint AppId = 480;
    private const string GameTag = "InfiniteHorizons";
    private const int Channel = 0;

    public static bool Ready { get; private set; }
    public static string Status { get; private set; } = "Steam не инициализирован";
    public static string PersonaName { get; private set; } = "";
    public static ulong MyId { get; private set; }
    public static readonly List<SteamGame> Games = new();
    public static bool Searching { get; private set; }
    public static event Action? Changed;

    private static bool _tried;
    private static TunnelHost? _host;
    private static TunnelClient? _client;
    private static CSteamID _lobby;
    private static readonly Link LinkInstance = new();
    private static readonly IntPtr[] Incoming = new IntPtr[64];
    private static readonly byte[] RecvBuf = new byte[65536];
    private static IntPtr _sendBuf = IntPtr.Zero;
    // delegates are kept in fields so the garbage collector does not drop the native callbacks
    private static Callback<SteamNetworkingMessagesSessionRequest_t>? _onSession;
    private static CallResult<LobbyCreated_t>? _onLobbyCreated;
    private static CallResult<LobbyMatchList_t>? _onLobbyList;

    public static bool Hosting => _host != null;
    public static int? ClientPort => _client?.LocalPort;

    private sealed class Link : IPeerLink
    {
        public void Send(ulong peer, byte[] data, int length)
        {
            if (!Ready || length <= 0 || length > 65536) return;
            var id = new SteamNetworkingIdentity();
            id.SetSteamID64(peer);
            Marshal.Copy(data, 0, _sendBuf, length);
            // unreliable: the game protocol (LiteNetLib) does its own acknowledgements and resends
            SteamNetworkingMessages.SendMessageToUser(ref id, _sendBuf, (uint)length,
                Constants.k_nSteamNetworkingSend_UnreliableNoNagle | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession, Channel);
        }
    }

    /// <summary>Connects to the running Steam client. Safe to call repeatedly; returns whether Steam is usable.</summary>
    public static bool Init()
    {
        if (Ready) return true;
        if (_tried && Status.StartsWith("Библиотека")) return false; // a missing DLL will not appear by itself
        if (!_tried)
        {
            // look for the Steam library next to the game's own assemblies wherever the wrapper DLL was loaded from
            NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly, (name, _, _) =>
            {
                foreach (string file in new[] { name + ".dll", "lib" + name + ".so", "steam_api64.dll", "libsteam_api.so" })
                    if (NativeLibrary.TryLoad(System.IO.Path.Combine(AppContext.BaseDirectory, file), out IntPtr handle)) return handle;
                return IntPtr.Zero; // fall back to the default search
            });
        }
        _tried = true;
        try
        {
            // "AppID substitution": tell the Steam client we are Spacewar
            System.Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString());
            System.Environment.SetEnvironmentVariable("SteamGameId", AppId.ToString());
            WriteAppIdFile(System.IO.Directory.GetCurrentDirectory());
            if (OS.HasFeature("template")) WriteAppIdFile(OS.GetExecutablePath().GetBaseDir());

            if (!SteamAPI.Init())
            {
                Status = "Steam не запущен. Запустите Steam, войдите в аккаунт и нажмите «Обновить список».";
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            Status = "Библиотека Steam (steam_api64.dll) не найдена рядом с игрой.";
            return false;
        }
        catch (Exception ex)
        {
            Status = "Ошибка Steam: " + ex.Message;
            return false;
        }

        _sendBuf = Marshal.AllocHGlobal(65536);
        MyId = SteamUser.GetSteamID().m_SteamID;
        PersonaName = SteamFriends.GetPersonaName();
        SteamNetworkingUtils.InitRelayNetworkAccess();
        _onSession = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(req =>
        {
            // only a host accepts strangers; a client only ever talks to the host it chose itself
            if (_host != null) SteamNetworkingMessages.AcceptSessionWithUser(ref req.m_identityRemote);
        });
        _onLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _onLobbyList = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
        Ready = true;
        Status = $"Steam: {PersonaName}";
        return true;
    }

    private static void WriteAppIdFile(string dir)
    {
        try
        {
            string path = System.IO.Path.Combine(dir, "steam_appid.txt");
            if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, AppId.ToString());
        }
        catch { /* read-only folder: the environment variables above still work on Windows */ }
    }

    /// <summary>Call every frame: Steam callbacks, incoming P2P data and the UDP side of the tunnels.</summary>
    public static void Poll()
    {
        if (!Ready) return;
        SteamAPI.RunCallbacks();
        int n;
        do
        {
            n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, Incoming, Incoming.Length);
            for (int i = 0; i < n; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(Incoming[i]);
                int size = msg.m_cbSize;
                ulong peer = msg.m_identityPeer.GetSteamID64();
                if (size > 0 && size <= RecvBuf.Length)
                {
                    Marshal.Copy(msg.m_pData, RecvBuf, 0, size);
                    if (_host != null) _host.OnPeerData(peer, RecvBuf, size);
                    else _client?.OnPeerData(peer, RecvBuf, size);
                }
                SteamNetworkingMessage_t.Release(Incoming[i]);
            }
        } while (n == Incoming.Length);
        _host?.Poll();
        _client?.Poll();
    }

    // -------------------------------------------------------------- host
    /// <summary>Publishes this machine's game server (already listening on <paramref name="serverPort"/>) to Steam.</summary>
    public static void StartHosting(int serverPort)
    {
        if (!Ready) return;
        StopClient();
        _host?.Dispose();
        _host = new TunnelHost(LinkInstance, serverPort);
        if (_lobby.IsValid()) return;
        _onLobbyCreated!.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 16));
    }

    private static void OnLobbyCreated(LobbyCreated_t r, bool ioFailure)
    {
        if (ioFailure || r.m_eResult != EResult.k_EResultOK)
        {
            Status = $"Steam не создал лобби ({(ioFailure ? "нет связи" : r.m_eResult.ToString())}) — друзья не увидят игру в списке";
            Changed?.Invoke();
            return;
        }
        _lobby = new CSteamID(r.m_ulSteamIDLobby);
        if (_host == null) { LeaveLobby(); return; } // hosting was cancelled meanwhile
        // Spacewar's lobby list is shared by every project that uses AppID 480: tag ours so we can filter
        SteamMatchmaking.SetLobbyData(_lobby, "game", GameTag);
        SteamMatchmaking.SetLobbyData(_lobby, "proto", Codec.ProtocolVersion.ToString());
        SteamMatchmaking.SetLobbyData(_lobby, "host_id", MyId.ToString());
        SteamMatchmaking.SetLobbyData(_lobby, "host", PersonaName);
        Status = $"Steam: {PersonaName} — игра опубликована, друзья видят её в списке «Игры в Steam»";
        Changed?.Invoke();
    }

    private static void LeaveLobby()
    {
        if (_lobby.IsValid()) SteamMatchmaking.LeaveLobby(_lobby);
        _lobby = default;
    }

    public static void StopHosting()
    {
        _host?.Dispose();
        _host = null;
        if (Ready) LeaveLobby();
    }

    // ------------------------------------------------------------ client
    public static void RefreshGames()
    {
        if (!Ready || Searching) return;
        Searching = true;
        SteamMatchmaking.AddRequestLobbyListStringFilter("game", GameTag, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
        _onLobbyList!.Set(SteamMatchmaking.RequestLobbyList());
        Changed?.Invoke();
    }

    private static void OnLobbyList(LobbyMatchList_t r, bool ioFailure)
    {
        Searching = false;
        Games.Clear();
        if (!ioFailure)
            for (int i = 0; i < r.m_nLobbiesMatching; i++)
            {
                var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                if (SteamMatchmaking.GetLobbyData(lobby, "game") != GameTag) continue;
                if (!ulong.TryParse(SteamMatchmaking.GetLobbyData(lobby, "host_id"), out ulong host) || host == 0 || host == MyId) continue;
                string proto = SteamMatchmaking.GetLobbyData(lobby, "proto");
                string name = SteamMatchmaking.GetLobbyData(lobby, "host");
                if (name.Length > 32) name = name[..32];
                bool friend = SteamFriends.HasFriend(new CSteamID(host), EFriendFlags.k_EFriendFlagImmediate);
                Games.Add(new SteamGame
                {
                    HostId = host, HostName = name.Length > 0 ? name : host.ToString(),
                    Info = (friend ? "друг" : "не в друзьях") + (proto == Codec.ProtocolVersion.ToString() ? "" : " · другая версия игры"),
                });
            }
        Games.Sort((a, b) => string.CompareOrdinal(b.Info, a.Info)); // friends first
        Changed?.Invoke();
    }

    /// <summary>Opens a tunnel to a host; the game then connects to 127.0.0.1 on the returned port.</summary>
    public static int Join(ulong hostId)
    {
        StopHosting();
        StopClient();
        _client = new TunnelClient(LinkInstance, hostId);
        return _client.LocalPort;
    }

    public static void StopClient()
    {
        if (_client == null) return;
        if (Ready)
        {
            var id = new SteamNetworkingIdentity();
            id.SetSteamID64(_client.HostPeer);
            SteamNetworkingMessages.CloseSessionWithUser(ref id);
        }
        _client.Dispose();
        _client = null;
    }

    public static void Shutdown()
    {
        StopClient();
        StopHosting();
        if (!Ready) return;
        Ready = false;
        SteamAPI.Shutdown();
    }
}
