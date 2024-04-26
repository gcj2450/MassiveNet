﻿// MIT License (MIT) - Copyright (c) 2014 jakevn - Please see included LICENSE file
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace MassiveNet {
    /// <summary>
    /// Commands are non-attribute, non-reflection/invoke based RPCs that must be
    /// manually registered. They do not target specific NetViews.
    /// The CommandID (enum ushort value) is used as the messageId.
    /// Useable Range: 1801-2047
    /// </summary>
    internal enum Cmd : ushort {
        /// <summary> Sent by server to new connection with server's RPC names and the assigned RPC IDs. </summary>
        RemoteAssignment = 2047,

        /// <summary> Sent by client to server upon connecting, requesting a numeric RPC ID for the supplied RPC method name. </summary>
        AssignmentRequest = 2046,

        /// <summary> Sent by server to new client connection in response to an AssignmentRequest. The response contains the RPC ID assignment and the method name. </summary>
        AssignmentResponse = 2045,

        /// <summary> Sends requirements for finishing connection setup. </summary>
        ConnectionRequirements = 2044,

        /// <summary> Signals that requirements for finishing connection setup have been met. </summary>
        RequirementsMet = 2043,

        /// <summary> Command containing the response to a request. </summary>
        RequestResponse = 2042
    }

    public class NetSocket : MonoBehaviour {
        /// <summary> All active connections. Includes both client and server connections. </summary>
        internal readonly List<NetConnection> Connections = new List<NetConnection>(); 

        private readonly Dictionary<IPEndPoint, NetConnection> endpointToConnection =
            new Dictionary<IPEndPoint, NetConnection>();

        private readonly List<IPEndPoint> connectingEndpoints = new List<IPEndPoint>();
        private readonly List<NetStream> connectingData = new List<NetStream>();
        private readonly List<uint> connectingTimes = new List<uint>();
        private readonly List<int> connectingRetriesRemaining = new List<int>();

        /// <summary> Dummy NetConnection used to represent self for authority checks. </summary>
        internal NetConnection Self;

        internal readonly CommandDispatcher Command = new CommandDispatcher();

        internal RequestDispatcher Request;

        internal RpcDispatcher Rpc;

        /// <summary> Contains all events related to this socket. </summary>
        public readonly NetSocketEvents Events = new NetSocketEvents();

        /// <summary> Monobehaviour instances for viewless-RPC methods. The key is the method name and the value is the Monobehaviour instance. </summary>
        private readonly Dictionary<string, object> rpcObjectInstances = new Dictionary<string, object>();

        /// <summary> If true, all incoming connections will have their RPC IDs generated by this socket. If false,
        /// this socket will need to first connect to a protocol authority. </summary>
        public bool ProtocolAuthority = false;

        /// <summary> Returns the port number for this socket. 0 if socket not yet initialized. </summary>
        public int Port {
            get { return Self.Endpoint.Port; }
        }

        /// <summary> Returns the IP address and port this socket is listening on. E.g., "192.168.1.1:17603" </summary>
        public string Address {
            get { return Self.Endpoint.Address.ToString(); }
        }

        /// <summary> The current number of total connections. Includes clients, servers, and peers.
        /// Compared against MaxConnections to determine if incoming connections should be refused. </summary>
        public int ConnectionCount { get { return Connections.Count; } }

        /// <summary> If ConnectionCount == MaxConnections, incoming connections will be refused. Outgoing connections
        /// are counted in ConnectionCount, but are allowed to exceed MaxConnections. </summary>
        public int MaxConnections { get; set; }

        /// <summary> Sets Unity's Application.targetFrameRate. It is recommended to set this to a resonable
        /// number such as 60 so that timing-related functionality remains more consistent. </summary>
        public int TargetFrameRate = 60;

        /// <summary> Handle to OS-level socket, created on successful StartSocket. </summary>
        private Socket socket;

        /// <summary> All incoming connections are refused when set to false. Clients should be false. </summary>
        public bool AcceptConnections { get; set; }

        /// <summary> Awake is a Unity convention. Unity invokes this method first when instantiated. </summary>
        private void Awake() {
            Application.runInBackground = true;
            Application.targetFrameRate = TargetFrameRate;

            Request = new RequestDispatcher(this);
            Rpc = new RpcDispatcher(this);

            RpcInfoCache.RpcMethods();

            RegisterCommandParams();
        }

        /// <summary> Starts the socket using an automatically selected endpoint. </summary>
        public void StartSocket() {
            StartSocket(new IPEndPoint(IPAddress.Any, 0));
        }

        /// <summary>
        /// Starts the socket using an address in the following format: "192.168.1.1:17010"
        /// If the port is taken, the given port will be incremented to a free port.
        /// </summary>
        public void StartSocket(string fullAddress) {
            StartSocket(StringToEndPoint(fullAddress));
        }

        /// <summary>
        /// Starts the socket using the supplied endpoint.
        /// If the port is taken, the given port will be incremented to a free port.
        /// </summary>
        public void StartSocket(IPEndPoint endpoint) {

            Self = new NetConnection(false, false, this, endpoint);
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            try {
                const uint IOC_IN = 0x80000000;
                const uint IOC_VENDOR = 0x18000000;
                uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                socket.IOControl((int)SIO_UDP_CONNRESET, new[] { Convert.ToByte(false) }, null);
            } catch {
                NetLog.Warning("Failed to set control code for ignoring ICMP port unreachable.");
            }

            socket.ReceiveBufferSize = 4194304;
            if (socket.ReceiveBufferSize != 4194304) NetLog.Warning("ReceiveBufferSize restricted by OS.");
            socket.SendBufferSize = 1048576;
            socket.Blocking = false;

            try {
                socket.Bind(endpoint);
            } catch (SocketException e) {
                if (e.ErrorCode == 10048) {
                    var newEp = new IPEndPoint(endpoint.Address, endpoint.Port + 1);
                    NetLog.Warning("Port in use. Incrementing and retrying...");
                    StartSocket(newEp);
                } else {
                    NetLog.Error(e.Message);
                }
                return;
            }

            NetLog.Info(NetTime.StartDateTime() + " | Socket Started. Bound to: " + endpoint);
            NetTime.Milliseconds();

            if (ProtocolAuthority) Rpc.AssignLocalRpcs();
            Events.SocketStart();
        }

        /// <summary> Closes the socket and performs cleanup for active connections. </summary>
        public void Shutdown() {
            DisconnectAll();
            socket.Close();
            socket = null;
        }

        /// <summary> Byte values for connection control commands. </summary>
        private enum ByteCmd : byte {
            Connect = 245,
            ConnectToPeer = 244,
            RefuseConnection = 243,
            Disconnect = 240
        }

        /// <summary> Attempts to connect to a server with address format: "192.168.1.1:17001" </summary>
        public void Connect(string fullAddress) {
            Connect(StringToEndPoint(fullAddress));
        }

        /// <summary> Attempts to connect to a peer with address format: "192.168.1.1:17001" </summary>
        public void ConnectToPeer(string fullAddress) {
            ConnectToPeer(StringToEndPoint(fullAddress));
        }

        /// <summary> Attempts to connect to a server located at the supplied endpoint. </summary>
        public void Connect(IPEndPoint endpoint) {
            if (EndpointConnected(endpoint) || connectingEndpoints.Contains(endpoint)) return;
            NetStream approvalData = NetStream.Create();
            approvalData.WriteByte((byte)ByteCmd.Connect);
            Events.WriteApproval(endpoint, approvalData);
            Connect(endpoint, approvalData);
        }

        /// <summary> Attempts to connect to a peer located at the supplied endpoint. </summary>
        public void ConnectToPeer(IPEndPoint endpoint) {
            if (EndpointConnected(endpoint) || ConnectingTo(endpoint)) return;
            NetStream approvalData = NetStream.Create();
            approvalData.WriteByte((byte)ByteCmd.ConnectToPeer);
            Events.WritePeerApproval(endpoint, approvalData);
            Connect(endpoint, approvalData);
        }

        /// <summary> Returns true if socket is currently attempting a connection with supplied endpoint. </summary>
        public bool ConnectingTo(IPEndPoint endpoint) {
            return connectingEndpoints.Contains(endpoint);
        }

        /// <summary> Handles connecting status. Tracks attempted connections and (re)sends connection data. </summary>
        private void Connect(IPEndPoint ep, NetStream approvalData) {
            if (!connectingEndpoints.Contains(ep)) {
                // We aren't currently trying to connect to this endpoint, add to lists:
                connectingEndpoints.Add(ep);
                connectingTimes.Add(NetTime.Milliseconds());
                connectingData.Add(approvalData);
                connectingRetriesRemaining.Add(4);
                NetLog.Info("Connecting to: " + ep);
            } else {
                // We are already trying to connect, update attempt data:
                int index = connectingEndpoints.IndexOf(ep);
                connectingRetriesRemaining[index]--;
                if (connectingRetriesRemaining[index] <= 0) {
                    // Retried max amount of times, notify failure:
                    RemoveFromConnecting(ep, false);
                    return;
                }
                connectingTimes[index] = NetTime.Milliseconds();
                NetLog.Info("Retrying connection to: " + ep);
            }
            // Send the connection request data to the endpoint:
            SendStream(ep, approvalData);
        }

        /// <summary> Cleans up a connection attempt and returns true if it is a peer connection. </summary>
        internal bool RemoveFromConnecting(IPEndPoint ep, bool successful) {
            bool isPeer = false;
            if (!successful) {
                NetLog.Info("Failed to connect to: " + ep);
                Events.FailedToConnect(ep);
            }
            int index = connectingEndpoints.IndexOf(ep);
            connectingTimes.RemoveAt(index);
            connectingRetriesRemaining.RemoveAt(index);
            connectingEndpoints.Remove(ep);
            if (connectingData[index].Data[0] == (byte)ByteCmd.ConnectToPeer) isPeer = true;
            connectingData[index].Release();
            connectingData.RemoveAt(index);
            return isPeer;
        }

        /// <summary> Creates an IPEndPoint by parsing a string with format "192.168.1.1:17010" </summary>
        internal IPEndPoint StringToEndPoint(string address) {
            string[] split = address.Split(':');
            string stringAddress = split[0];
            string stringPort = split[1];
            int port = int.Parse(stringPort);
            var ipaddress = IPAddress.Parse(stringAddress);
            var endpoint = new IPEndPoint(ipaddress, port);
            if (endpoint == null) throw new Exception("Failed to parse address: " + address);
            return endpoint;
        }

        /// <summary> Closes all connections. </summary>
        public void DisconnectAll() {
            for (int i = Connections.Count - 1; i >= 0; i--) Disconnect(Connections[i]);
        }

        /// <summary> Closes the supplied connection. </summary>
        public void Disconnect(NetConnection connection) {
            Connections.Remove(connection);
            endpointToConnection.Remove(connection.Endpoint);

            if (connection.IsPeer) Events.PeerDisconnected(connection);
            else if (connection.IsServer) Events.DisconnectedFromServer(connection);
            else Events.ClientDisconnected(connection);
        }

        /// <summary> Returns true if OS socket has data available for read. </summary>
        private bool CanReceive() {
            return socket.Poll(0, SelectMode.SelectRead);
        }

        private EndPoint epCache = new IPEndPoint(IPAddress.Any, 0);
        /// <summary> The starting point for incoming data. Attempts to read from OS socket buffer. False = failed. </summary>
        private bool TryReceive(byte[] buffer, int size, ref int receiveCount, ref IPEndPoint remoteEndpoint) {
            try {
                receiveCount = socket.ReceiveFrom(buffer, size, SocketFlags.None, ref epCache);
                remoteEndpoint = epCache as IPEndPoint;
                return (receiveCount > 0);
            } catch {
                return false;
            }
        }

        /// <summary> Final frontier for outgoing data. Attempts to send data to endpoint via OS socket. False = failed. </summary>
        private bool TrySend(byte[] buffer, int sendCount, IPEndPoint endpoint) {
            try {
                int bytesSent = socket.SendTo(buffer, sendCount, SocketFlags.None, endpoint);
                return bytesSent == sendCount;
            } catch {
                return false;
            }
        }

        private void Update() {
            ReceiveAll();
        }

        private void LateUpdate() {
            EndFrameTasks();
        }

        private IPEndPoint endPoint = default(IPEndPoint);

        /// <summary> Receives data until CanReceive is no longer true (receive buffer empty). </summary>
        private void ReceiveAll() {
            if (socket == null) return;

            while (CanReceive()) {
                var readStream = NetStream.Create();
                readStream.Socket = this;
                int received = 0;
                if (!TryReceive(readStream.Data, readStream.Data.Length, ref received, ref endPoint)) return;
                readStream.Length = received << 3;
                ProcessReceived(endPoint, received, readStream);
            }
        }

        internal void SendStream(NetConnection connection, NetStream stream) {
            SendStream(connection.Endpoint, stream);
        }

        internal void SendStream(IPEndPoint endpoint, NetStream stream) {
            TrySend(stream.Data, stream.Pos + 7 >> 3, endpoint);
        }

        private void ProcessReceived(IPEndPoint endpoint, int bytesReceived, NetStream readStream) {
            if (EndpointConnected(endpoint)) endpointToConnection[endpoint].ReceiveStream(readStream);
            else ProcessUnconnected(endpoint, bytesReceived, readStream);
        }

        private void ProcessUnconnected(IPEndPoint endpoint, int bytesReceived, NetStream readStream) {
            if (connectingEndpoints.Contains(endpoint)) ReceiveConnectionResponse(endpoint, bytesReceived, readStream);
            else if (bytesReceived > 0) {
                byte cmd = readStream.ReadByte();
                if (cmd == (byte)ByteCmd.Connect) ReceiveConnectionRequest(endPoint, readStream);
                else if (cmd == (byte)ByteCmd.ConnectToPeer) ReceivePeerConnectionRequest(endPoint, readStream);
            }
        }

        private void ReceiveConnectionResponse(IPEndPoint endpoint, int bytesReceived, NetStream readStream) {
            bool isPeer = RemoveFromConnecting(endpoint, true);
            if (bytesReceived == 1 && readStream.Data[0] == (byte)ByteCmd.RefuseConnection) {
                NetLog.Info("Connection refused by: " + endpoint);
                return;
            }
            var connection = CreateConnection(endpoint, true, isPeer);
            if (bytesReceived > 1) connection.ReceiveStream(readStream);
        }

        private void ReceiveConnectionRequest(IPEndPoint endpoint, NetStream readStream) {
            if (!AcceptConnections || Connections.Count >= MaxConnections || !Events.ClientApproval(endpoint, readStream)) {
                NetLog.Info("Refused connection: " + endpoint);
                TrySend(new[] { (byte)ByteCmd.RefuseConnection }, 1, endpoint);
            } else CreateConnection(endpoint, false, false);
        }

        private void ReceivePeerConnectionRequest(IPEndPoint endpoint, NetStream readStream) {
            if (!AcceptConnections || Connections.Count >= MaxConnections || !Events.PeerApproval(endpoint, readStream)) {
                NetLog.Info("Refused peer connection: " + endpoint);
                TrySend(new[] { (byte)ByteCmd.RefuseConnection }, 1, endpoint);
            } else CreateConnection(endpoint, false, true);
        }

        /// <summary> Iterates through pending connections and retries any timeouts. </summary>
        private void CheckForTimeouts() {
            if (connectingEndpoints.Count == 0) return;
            for (int i = connectingEndpoints.Count - 1; i >= 0; i--) {
                if (NetTime.Milliseconds() - connectingTimes[i] > 2000) Connect(connectingEndpoints[i], connectingData[i]);
            }
        }

        /// <summary> Timeouts, disconnects, heartbeats, forced-acks, etc. need to be performed at end of frame. </summary>
        private void EndFrameTasks() {
            uint currentTime = NetTime.Milliseconds();
            for (int i = Connections.Count - 1; i >= 0; i--) Connections[i].EndOfFrame(currentTime);
            CheckForTimeouts();
        }

        internal bool EndpointConnected(IPEndPoint ep) {
            return endpointToConnection.ContainsKey(ep);
        }

        internal NetConnection EndpointToConnection(IPEndPoint ep) {
            return endpointToConnection[ep];
        }

        /// <summary> Adds a new NetConnection to the connection list. </summary>
        internal NetConnection CreateConnection(IPEndPoint ep, bool isServer, bool isPeer) {
            bool wasServer = false;
            // Connection cannot be both server and peer:
            if (isPeer) {
                isServer = false;
                wasServer = true;
            }

            var connection = new NetConnection(isServer, isPeer, this, ep);

            Connections.Add(connection);
            endpointToConnection.Add(ep, connection);
            if (isPeer) NetLog.Info("Peer connection created: " + ep);
            else if (isServer) NetLog.Info("Server connection created: " + ep);
            else NetLog.Info("Client connection created: " + ep);

            if (ProtocolAuthority && !isServer && !wasServer) {
                SendConnectionRequirements(connection);
                Rpc.SendLocalAssignments(connection);
            } 
            else if (connection.IsPeer && Rpc.IdCount == RpcInfoCache.Count) Events.PeerConnected(connection);
            else if (isServer && Rpc.IdCount == RpcInfoCache.Count) Events.ConnectedToServer(connection);
            else if (!isServer && !isPeer) Events.ClientConnected(connection);

            return connection;
        }

        /// <summary> Sends a reliable RPC that does not target a specific view. </summary>
        public void Send(string methodName, NetConnection target, params object[] parameters) {
            if (!Rpc.HasId(methodName)) {
                NetLog.Error("Remote RPC does not have an assigned ID: " + methodName);
                return;
            }
            var message = NetMessage.Create(Rpc.NameToId(methodName), 0, parameters, true);
            target.Send(message);
        }

        /// <summary> Sends a request to the target connection without an associated view. </summary>
        public Request<T> SendRequest<T>(string methodName, NetConnection target, params object[] parameters) {
            return Request.Send<T>(methodName, target, parameters);
        }

        /// <summary> Dispatches received commands and RPCs based on the messageID. </summary>
        internal void ReceiveMessage(NetMessage message, NetConnection connection) {
            if (message.MessageId > 1800) Command.Dispatch(message, connection);
            else if (message.ViewId == 0) DispatchRpc(message, connection);
            else Events.MessageReceived(message, connection);

            message.Release();
        }

        /// <summary>
        /// Passes parameters from an incoming network message to the method associated with the RPC.
        /// </summary>
        internal void DispatchRpc(NetMessage message, NetConnection connection) {
            string methodName = Rpc.IdToName(message.MessageId);

            if (!rpcObjectInstances.ContainsKey(methodName)) {
                NetLog.Error(string.Format("Can't find method \"{0}\" for Viewless RPC.", methodName));
                return;
            }

            Rpc.Invoke(rpcObjectInstances[methodName], methodName, message, connection);
        }

        /// <summary> Sends connection configuration requirements command to a new client connection. </summary>
        private void SendConnectionRequirements(NetConnection connection) {
            Command.Send((int)Cmd.ConnectionRequirements, connection, Rpc.IdCount);
        }

        /// <summary> Handles connection configuration requirements sent by server upon connection. </summary>
        private void ReceiveConnectionRequirements(NetMessage message, NetConnection connection) {
            if (!connection.IsServer && !connection.IsPeer) return;
            Rpc.WaitingForRpcs += (int)message.Parameters[0];
        }

        /// <summary> Sends command to server to signal that connection requirements have been met. </summary>
        internal void SendRequirementsMet(NetConnection connection) {
            Command.Send((int)Cmd.RequirementsMet, connection);
            if (connection.IsPeer) Events.PeerConnected(connection);
            else Events.ConnectedToServer(connection);
        }

        /// <summary> Handles RequirementsMet command sent by client to signal the client is ready. </summary>
        private void ReceiveRequirementsMet(NetMessage message, NetConnection connection) {
            if (!connection.IsPeer && !connection.IsServer) Events.ClientConnected(connection);
            else if (connection.IsPeer) Events.PeerConnected(connection);
        }

        /// <summary> Populates CommandParameterTypes with the type data for each command.
        ///  This is necessary to allow proper deserialization of incoming data for these commands. </summary>
        private void RegisterCommandParams() {
            Command.Register((ushort)Cmd.RemoteAssignment, Rpc.ReceiveRemoteAssignment,
                new List<Type> { typeof(ushort), typeof(string) });
            Command.Register((ushort)Cmd.AssignmentResponse, Rpc.ReceiveAssignmentResponse,
                new List<Type> { typeof(ushort), typeof(string) });
            Command.Register((ushort)Cmd.AssignmentRequest, Rpc.ReceiveAssignmentRequest,
                new List<Type> { typeof(string) });
            Command.Register((ushort)Cmd.ConnectionRequirements, ReceiveConnectionRequirements,
                new List<Type> { typeof(int) });
            Command.Register((ushort)Cmd.RequirementsMet, ReceiveRequirementsMet, new List<Type>());
            Command.Register((ushort)Cmd.RequestResponse, Request.SetResponse, new List<Type>());
        }

        /// <summary> Registers an instance of a MonoBehaviour to receive RPCs not associated with a NetView. </summary>
        public void RegisterRpcListener(MonoBehaviour listener) {
            foreach (KeyValuePair<string, RpcMethodInfo> cachedRpc in RpcInfoCache.RpcMethods()) {
                if (!cachedRpc.Value.MethodInfoLookup.ContainsKey(listener.GetType())) continue;
                rpcObjectInstances.Add(cachedRpc.Value.Name, listener);
            }
        }
    }
}