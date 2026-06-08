using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Multipleer.Transport
{
    public class SteamTransport : ITransport
    {
        public TransportType TransportType => TransportType.SteamP2P;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public bool IsHost { get; private set; }
        public string LocalEndpoint { get; private set; } = "";

        public event Action<ConnectionState> OnStateChanged;
        public event Action<ulong, byte[]> OnPacketReceived;
        public event Action<ulong, string> OnPeerConnected;
        public event Action<ulong, string> OnPeerDisconnected;

        private ulong _localSteamId;
        private readonly HashSet<ulong> _connectedPeers = new HashSet<ulong>();
        private readonly Queue<(ulong, byte[])> _incomingQueue = new Queue<(ulong, byte[])>();

        // Reflection handles for Facepunch.Steamworks (runtime-resolved)
        private static Type _steamClientType;
        private static Type _steamNetworkingType;
        private static object _steamNetworkingInstance;

        static SteamTransport()
        {
            try
            {
                var assembly = typeof(UnityEngine.Application).Assembly
                    .GetType("Steamworks.SteamClient")?.Assembly;
                if (assembly == null)
                    assembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

                if (assembly != null)
                {
                    _steamClientType = assembly.GetType("Steamworks.SteamClient");
                    _steamNetworkingType = assembly.GetType("Steamworks.SteamNetworking");

                    if (_steamNetworkingType != null)
                    {
                        var prop = _steamNetworkingType.GetProperty("Instance",
                            BindingFlags.Public | BindingFlags.Static);
                        _steamNetworkingInstance = prop?.GetValue(null, null);
                    }
                }
            }
            catch
            {
                // Steamworks not available — SteamTransport will fail gracefully
            }
        }

        public void Initialize()
        {
            try
            {
                if (_steamClientType == null)
                    throw new InvalidOperationException("Steamworks not available");

                var steamIdProp = _steamClientType.GetProperty("SteamId",
                    BindingFlags.Public | BindingFlags.Static);
                _localSteamId = (ulong)steamIdProp?.GetValue(null, null);
                LocalEndpoint = $"Steam({_localSteamId})";

                if (_steamNetworkingInstance != null)
                {
                    var evt = _steamNetworkingType.GetEvent("OnP2PSessionRequest");
                    if (evt != null)
                    {
                        var handler = Delegate.CreateDelegate(evt.EventHandlerType,
                            this, GetType().GetMethod("OnSessionRequest",
                                BindingFlags.NonPublic | BindingFlags.Instance));
                        evt.AddEventHandler(_steamNetworkingInstance, handler);
                    }
                }

                State = ConnectionState.Connected;
                OnStateChanged?.Invoke(State);
            }
            catch (Exception ex)
            {
                LocalEndpoint = $"SteamError: {ex.Message}";
                State = ConnectionState.Failed;
                OnStateChanged?.Invoke(State);
            }
        }

        public void Shutdown()
        {
            foreach (var peer in _connectedPeers)
            {
                CallNetworkingMethod("CloseP2PSession", peer);
            }
            _connectedPeers.Clear();
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Host(int port = 0)
        {
            IsHost = true;
        }

        public void Connect(string address, int port)
        {
            if (ulong.TryParse(address, out ulong targetId))
            {
                State = ConnectionState.Connecting;
                OnStateChanged?.Invoke(State);
                CallNetworkingMethod("AcceptP2PSession", targetId);
                _connectedPeers.Add(targetId);
                State = ConnectionState.Connected;
                OnStateChanged?.Invoke(State);
                OnPeerConnected?.Invoke(targetId, $"Steam({targetId})");
            }
        }

        public void Disconnect()
        {
            foreach (var peer in _connectedPeers)
            {
                CallNetworkingMethod("CloseP2PSession", peer);
                OnPeerDisconnected?.Invoke(peer, $"Steam({peer})");
            }
            _connectedPeers.Clear();
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Send(ulong peerId, byte[] data, bool reliable = true)
        {
            var sendType = reliable ? 0 : 1; // Reliable=0, Unreliable=1
            CallNetworkingMethod("SendP2PPacket", peerId, data, sendType, 0);
        }

        public void Broadcast(byte[] data, bool reliable = true)
        {
            var sendType = reliable ? 0 : 1;
            foreach (var peer in _connectedPeers)
            {
                CallNetworkingMethod("SendP2PPacket", peer, data, sendType, 0);
            }
        }

        public void Update()
        {
            while (IsP2PPacketAvailable())
            {
                var packet = ReadP2PPacket();
                if (packet.HasValue)
                {
                    _incomingQueue.Enqueue((packet.Value.SteamId, packet.Value.Data));
                }
            }

            while (_incomingQueue.Count > 0)
            {
                var (steamId, data) = _incomingQueue.Dequeue();
                OnPacketReceived?.Invoke(steamId, data);
            }
        }

        // ─── Reflection Helpers ──────────────────────────────────────────

        private void CallNetworkingMethod(string name, params object[] args)
        {
            try
            {
                if (_steamNetworkingInstance != null)
                {
                    var method = _steamNetworkingType.GetMethod(name,
                        BindingFlags.Public | BindingFlags.Instance);
                    method?.Invoke(_steamNetworkingInstance, args);
                }
            }
            catch { }
        }

        private bool IsP2PPacketAvailable()
        {
            try
            {
                if (_steamNetworkingType != null)
                {
                    var method = _steamNetworkingType.GetMethod("IsP2PPacketAvailable",
                        BindingFlags.Public | BindingFlags.Static);
                    return method != null && (bool)method.Invoke(null, null);
                }
            }
            catch { }
            return false;
        }

        private P2PPacket? ReadP2PPacket()
        {
            try
            {
                if (_steamNetworkingType != null)
                {
                    var method = _steamNetworkingType.GetMethod("ReadP2PPacket",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        var result = method.Invoke(null, null);
                        if (result != null)
                        {
                            var type = result.GetType();
                            var steamIdProp = type.GetProperty("SteamId");
                            var dataProp = type.GetProperty("Data");
                            if (steamIdProp != null && dataProp != null)
                            {
                                return new P2PPacket
                                {
                                    SteamId = (ulong)steamIdProp.GetValue(result, null),
                                    Data = (byte[])dataProp.GetValue(result, null)
                                };
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void OnSessionRequest(ulong remoteSteamId)
        {
            CallNetworkingMethod("AcceptP2PSession", remoteSteamId);
            _connectedPeers.Add(remoteSteamId);
            OnPeerConnected?.Invoke(remoteSteamId, $"Steam({remoteSteamId})");
        }

        private struct P2PPacket
        {
            public ulong SteamId;
            public byte[] Data;
        }
    }

}
