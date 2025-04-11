using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Timers;

namespace FastUDP {
    public class FastUdpServer
    {
        private readonly Socket _socket;
        private readonly byte[] _buffer = new byte[8192];
        private readonly EndPoint _remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        
        // Dictionary of active sessions by ID
        private readonly ConcurrentDictionary<string, FastUdpSession> _sessions = new ConcurrentDictionary<string, FastUdpSession>();
        // Dictionary to map IP addresses to session IDs
        private readonly ConcurrentDictionary<string, string> _endpointToSessionId = new ConcurrentDictionary<string, string>();
        
        // Channel manager
        private readonly FastUdpChannelManager _channelManager;

        private bool _running;
        private System.Timers.Timer _sessionMonitor;
        
        public bool DebugMode { get; set; } = true;
        
        // Log levels - only levels less than or equal to the defined level will be displayed
        public enum LogLevel { None = 0, Critical = 1, Warning = 2, Basic = 3, Verbose = 4, All = 5 }
        public LogLevel LoggingLevel { get; set; } = LogLevel.Verbose; // Default level
        
        // Method for logging with level
        private void LogServer(string message, LogLevel level = LogLevel.Basic, ConsoleColor color = ConsoleColor.White, string? sessionId = null)
        {
            if (DebugMode && (int)level <= (int)LoggingLevel)
            {
                Console.ForegroundColor = color;
                
                // Include session ID in log message if provided
                string sessionInfo = string.IsNullOrEmpty(sessionId) ? "" : $" [SessionID: {sessionId}]";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]{sessionInfo} {message}");
                
                Console.ResetColor();
            }
        }

        public FastUdpServer(string ip, int port)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Parse(ip), port));
            _socket.ReceiveTimeout = 0;
            _socket.Blocking = false;
            
            // Initialize channel manager
            _channelManager = new FastUdpChannelManager();
            _channelManager.ChannelCreated += OnChannelCreated;
            _channelManager.ChannelRemoved += OnChannelRemoved;
            
            // Configure timer to monitor inactive sessions
            _sessionMonitor = new System.Timers.Timer(5000); // Check every 5 seconds
            _sessionMonitor.Elapsed += CheckSessionsActivity;
            _sessionMonitor.AutoReset = true;
        }
        
        private void CheckSessionsActivity(object? sender, ElapsedEventArgs e)
        {
            var now = DateTime.Now;
            List<string> sessionsToRemove = new List<string>();
            
            foreach (var session in _sessions.Values)
            {
                if (session.IsTimedOut)
                {
                    sessionsToRemove.Add(session.Id);
                    LogServer($"Session {session.Id} ({session.RemoteEndPoint}) expired due to inactivity", 
                              LogLevel.Basic, ConsoleColor.Yellow, session.Id);
                }
            }
            
            // Remove inactive sessions
            foreach (var id in sessionsToRemove)
            {
                if (_sessions.TryRemove(id, out FastUdpSession session))
                {
                    string endpointKey = session.RemoteEndPoint.ToString();
                    _endpointToSessionId.TryRemove(endpointKey, out _);
                }
            }
            
            if (sessionsToRemove.Count > 0)
            {
                LogServer($"{_sessions.Count} active sessions after cleanup", 
                          LogLevel.Verbose, ConsoleColor.DarkYellow);
            }
        }

        public void Start()
        {
            _running = true;
            _sessionMonitor.Start();

            Task.Run(() =>
            {
                while (_running)
                {
                    try
                    {
                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        int bytes = _socket.ReceiveFrom(_buffer, 0, _buffer.Length, SocketFlags.None, ref remote);
                        if (bytes > 0)
                        {
                            byte[] packetData = new byte[bytes];
                            Buffer.BlockCopy(_buffer, 0, packetData, 0, bytes);
                            ProcessPacket((IPEndPoint)remote, packetData);
                        }
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode != SocketError.WouldBlock)
                            LogServer($"Socket error: {ex.Message}", LogLevel.Critical, ConsoleColor.Red);
                        Thread.SpinWait(10); // leve wait
                    }
                }
            });

            LogServer("Fast UDP Server started", LogLevel.Critical, ConsoleColor.Green);
        }

        private void ProcessPacket(IPEndPoint remote, byte[] packetData)
        {
            var endpointKey = remote.ToString();
            var now = DateTime.Now;
            
            try
            {
                var packet = new FastPacket(packetData);
                
                // ATRIBUIR SessionId DE ACORDO COM O ENDPOINT
                if (_endpointToSessionId.TryGetValue(endpointKey, out string? sessionId) && sessionId != null)
                {
                    // Associar o SessionId ao pacote, pois ele não vem mais no próprio pacote
                    packet.SessionId = sessionId;
                    
                    //LogServer($"Associando sessionId '{sessionId}' ao pacote de tipo {packet.Type} baseado no endpoint {endpointKey}", 
                    //          LogLevel.Verbose, ConsoleColor.Cyan, sessionId);
                }               
                
                // Process based on packet type
                switch (packet.Type)
                {
                    case EPacketType.Ping:
                        HandlePing(remote, endpointKey);
                        break;
                        
                    case EPacketType.Connect:
                        HandleConnect(remote, endpointKey);
                        break;
                        
                    case EPacketType.Disconnect:
                        HandleDisconnect(remote, endpointKey, packet);
                        break;
                        
                    case EPacketType.Message:
                    case EPacketType.BinaryData:
                        HandleDataPacket(remote, endpointKey, packet);
                        break;
                        
                    case EPacketType.ChannelJoin:
                        // Binary protocol for joining a channel - more efficient than text parsing
                        if (_endpointToSessionId.TryGetValue(endpointKey, out string? joinSessionId) && 
                            joinSessionId != null && 
                            _sessions.TryGetValue(joinSessionId, out FastUdpSession? joinSession) && 
                            joinSession != null)
                        {
                            // Extract channel ID from packet data
                            string channelId = packet.GetDataAsString();
                            LogServer($"Binary protocol: Session {joinSessionId} requesting to join channel {channelId}", 
                                     LogLevel.Basic, ConsoleColor.Green, joinSessionId);
                            
                            // Get the channel
                            var channel = _channelManager.GetChannel(channelId);
                            if (channel != null)
                            {
                                // Add the session to the channel
                                if (channel.Type == EChannelType.Private)
                                {
                                    // Private channels need owner permission
                                    SendErrorPacket(remote, $"Cannot join private channel {channelId}");
                                    LogServer($"Binary protocol: Session {joinSessionId} cannot join private channel {channelId}", 
                                             LogLevel.Basic, ConsoleColor.Yellow, joinSessionId);
                                }
                                else if (channel.AddSession(joinSession))
                                {
                                    // Send confirmation using binary protocol
                                    var confirmPacket = new FastPacket(EPacketType.ChannelJoinConfirm, 
                                                                    Encoding.UTF8.GetBytes(channelId), 
                                                                    joinSessionId);
                                    byte[] confirmData = confirmPacket.Serialize();
                                    joinSession.Send(confirmData);
                                    
                                    LogServer($"Binary protocol: Session {joinSessionId} joined channel {channel.Name} ({channelId})", 
                                             LogLevel.Basic, ConsoleColor.Green, joinSessionId);
                                    
                                    // Notify channel members
                                    channel.Broadcast($"[Channel:{channel.Name}] User {joinSessionId} joined the channel", null);
                                }
                                else
                                {
                                    // Already in channel - still send confirmation
                                    var confirmPacket = new FastPacket(EPacketType.ChannelJoinConfirm, 
                                                                    Encoding.UTF8.GetBytes(channelId), 
                                                                    joinSessionId);
                                    byte[] confirmData = confirmPacket.Serialize();
                                    joinSession.Send(confirmData);
                                    
                                    LogServer($"Binary protocol: Session {joinSessionId} already in channel {channel.Name} ({channelId})", 
                                             LogLevel.Basic, ConsoleColor.Yellow, joinSessionId);
                                }
                            }
                            else
                            {
                                // Channel not found
                                SendErrorPacket(remote, $"Channel {channelId} not found");
                                LogServer($"Binary protocol: Session {joinSessionId} attempted to join non-existent channel {channelId}", 
                                         LogLevel.Basic, ConsoleColor.Yellow, joinSessionId);
                            }
                        }
                        else
                        {
                            // Session not found
                            SendErrorPacket(remote, "Not connected");
                            LogServer($"Binary protocol: Unknown client {endpointKey} attempted channel join", 
                                     LogLevel.Basic, ConsoleColor.Yellow);
                        }
                        break;
                        
                    case EPacketType.ChannelBroadcast:
                        // Binary protocol for broadcasting to a channel - more efficient than text parsing
                        LogServer($"Received ChannelBroadcast packet from {endpointKey}, attempting to process", 
                                 LogLevel.Basic, ConsoleColor.Cyan);
                                 
                        if (_endpointToSessionId.TryGetValue(endpointKey, out string? broadcastSessionId) && 
                            broadcastSessionId != null && 
                            _sessions.TryGetValue(broadcastSessionId, out FastUdpSession? broadcastSession) && 
                            broadcastSession != null)
                        {
                            try
                            {
                                LogServer($"Processing ChannelBroadcast from session {broadcastSessionId}", 
                                         LogLevel.Basic, ConsoleColor.Cyan, broadcastSessionId);
                                         
                                // PROTOCOLO ATUALIZADO: Dados agora estão diretamente após o tipo (sem SessionId)
                                // Parse the binary format: channelId length (1 byte) + channelId + message
                                byte[] broadcastData = packet.Data;
                                LogServer($"ChannelBroadcast packet data length: {broadcastData.Length} bytes", 
                                         LogLevel.Basic, ConsoleColor.Cyan, broadcastSessionId);
                                
                                // Debug - show the raw packet data in hex
                                StringBuilder hexData = new StringBuilder("Packet Data (hex): ");
                                for (int i = 0; i < Math.Min(broadcastData.Length, 20); i++) {
                                    hexData.Append($"{broadcastData[i]:X2} ");
                                }
                                LogServer(hexData.ToString(), LogLevel.Basic, ConsoleColor.Cyan, broadcastSessionId);
                                
                                if (broadcastData.Length < 2) // Need at least 1 byte for length and 1 byte for channelId
                                {
                                    LogServer($"ChannelBroadcast packet too short ({broadcastData.Length} bytes), discarding", 
                                             LogLevel.Warning, ConsoleColor.Red, broadcastSessionId);
                                    break;
                                }
                                    
                                byte channelIdLength = broadcastData[0];
                                
                                // CRITICAL SECURITY FIX: Validate channel ID length more strictly
                                if (channelIdLength == 0)
                                {
                                    LogServer($"Invalid channel ID length: 0 - cannot have empty channel ID", 
                                             LogLevel.Warning, ConsoleColor.Red, broadcastSessionId);
                                    break;
                                }
                                
                                LogServer($"Channel ID length in packet: {channelIdLength}", 
                                         LogLevel.Basic, ConsoleColor.Cyan, broadcastSessionId);
                                                                             
                                // Extract channel ID
                                byte[] channelIdBytes = new byte[channelIdLength];
                                Buffer.BlockCopy(broadcastData, 1, channelIdBytes, 0, channelIdLength);
                                string broadcastChannelId = Encoding.UTF8.GetString(channelIdBytes);
                                
                                // Extract message
                                int messageLength = broadcastData.Length - 1 - channelIdLength;
                                byte[] messageBytes = new byte[messageLength];
                                Buffer.BlockCopy(broadcastData, 1 + channelIdLength, messageBytes, 0, messageLength);
                                string broadcastMessage = Encoding.UTF8.GetString(messageBytes);

                                Console.WriteLine($"Successfully parsed ChannelBroadcast: ChannelId={broadcastChannelId}, MessageLength={messageLength}");
                                
                                LogServer($"Successfully parsed ChannelBroadcast: ChannelId={broadcastChannelId}, MessageLength={messageLength}", 
                                         LogLevel.Verbose, ConsoleColor.Cyan, broadcastSessionId);
                                
                                // Get the channel
                                var channel = _channelManager.GetChannel(broadcastChannelId);
                                if (channel != null)
                                {
                                    LogServer($"Binary protocol: Session {broadcastSessionId} broadcasting to channel {broadcastChannelId}: {broadcastMessage.Substring(0, Math.Min(20, broadcastMessage.Length))}...", 
                                             LogLevel.Basic, ConsoleColor.Cyan, broadcastSessionId);
                                    
                                    // Check if session is in the channel and allowed to broadcast
                                    if (channel.HasSession(broadcastSessionId))
                                    {
                                        if (channel.IsAllowedToBroadcast(broadcastSession))
                                        {
                                            // Format the message for the channel and broadcast it
                                            string formattedMessage = $"[Channel:{channel.Name}] {broadcastSessionId}: {broadcastMessage}";
                                            
                                            if (channel.BroadcastChannelMessage(broadcastChannelId, broadcastMessage, broadcastSession))
                                            {
                                                LogServer($"Binary protocol: Successfully broadcast message from {broadcastSessionId} to channel {channel.Name}", 
                                                         LogLevel.Verbose, ConsoleColor.Cyan, broadcastSessionId);
                                            }
                                            else
                                            {
                                                LogServer($"Binary protocol: Failed to broadcast message from {broadcastSessionId} to channel {channel.Name}", 
                                                         LogLevel.Warning, ConsoleColor.Red, broadcastSessionId);
                                                SendErrorPacket(remote, $"Failed to broadcast to channel {channel.Name}");
                                            }
                                        }
                                        else
                                        {
                                            LogServer($"Binary protocol: Session {broadcastSessionId} not allowed to broadcast to channel {channel.Name}", 
                                                     LogLevel.Warning, ConsoleColor.Red, broadcastSessionId);
                                            SendErrorPacket(remote, $"Not allowed to broadcast to channel {channel.Name}");
                                        }
                                    }
                                    else
                                    {
                                        LogServer($"Binary protocol: Session {broadcastSessionId} not in channel {channel.Name}", 
                                                 LogLevel.Basic, ConsoleColor.Yellow, broadcastSessionId);
                                        SendErrorPacket(remote, $"Not in channel {channel.Name}");
                                    }
                                }
                                else
                                {
                                    LogServer($"Binary protocol: Session {broadcastSessionId} attempted to broadcast to non-existent channel {broadcastChannelId}", 
                                             LogLevel.Basic, ConsoleColor.Yellow, broadcastSessionId);
                                    SendErrorPacket(remote, $"Channel {broadcastChannelId} not found");
                                }
                            }
                            catch (Exception ex)
                            {
                                LogServer($"Binary protocol: Error processing channel broadcast: {ex.Message}", 
                                         LogLevel.Critical, ConsoleColor.Red, broadcastSessionId);
                                SendErrorPacket(remote, $"Error processing channel broadcast: {ex.Message}");
                            }
                        }
                        else
                        {
                            // Session not found
                            SendErrorPacket(remote, "Not connected");
                            LogServer($"Binary protocol: Unknown client {endpointKey} attempted channel broadcast", 
                                     LogLevel.Basic, ConsoleColor.Yellow);
                        }
                        break;
                        
                    default:
                        // Unknown packet type
                        LogServer($"Unknown packet type: {packet.Type} from {endpointKey}", 
                                  LogLevel.Verbose, ConsoleColor.Yellow, packet.SessionId);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Error processing packet
                LogServer($"Error processing packet from {endpointKey}: {ex.Message}", 
                          LogLevel.Critical, ConsoleColor.Red);
                
                // Send error message to client
                SendErrorPacket(remote, $"Error processing packet: {ex.Message}");
            }
        }
        
        private void HandlePing(IPEndPoint remote, string endpointKey)
        {
            var now = DateTime.Now;
            
            // In Ping packets, just inform if there's an existing session
            if (_endpointToSessionId.TryGetValue(endpointKey, out string? sessionId) && sessionId != null)
            {
                if (_sessions.TryGetValue(sessionId, out FastUdpSession? session) && session != null)
                {
                    session.UpdateActivity();
                    LogServer($"Updated session activity for {sessionId} to {endpointKey}", 
                              LogLevel.All, ConsoleColor.Cyan, sessionId);
                }
            }
            
            // Send simple PONG (just 1 byte)
            var pongPacket = FastPacket.CreatePong();
            byte[] responseData = pongPacket.Serialize();
            _socket.SendTo(responseData, remote);
            
            LogServer($"Sending simple PONG to {endpointKey}", 
                      LogLevel.All, ConsoleColor.Cyan, endpointKey);
        }
        
        private void HandleConnect(IPEndPoint remote, string endpointKey)
        {
            var now = DateTime.Now;
            string? sessionId;
            bool isNewSession = false;
            
            // Check if there's already a session for this endpoint
            if (!_endpointToSessionId.TryGetValue(endpointKey, out sessionId))
            {
                // Create new session
                sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
                var session = new FastUdpSession(sessionId, remote, _socket);
                session.SetAuthenticationState(true);
                isNewSession = true;
                
                // Register callbacks for session events
                session.Disconnected += OnSessionDisconnected;
                session.MessageSent += OnSessionMessageSent;
                
                if (_sessions.TryAdd(sessionId, session))
                {
                    _endpointToSessionId[endpointKey] = sessionId;
                    LogServer($"New session created: {sessionId} for {endpointKey}", 
                              LogLevel.Basic, ConsoleColor.Green, sessionId);
                    
                    // Add new session to the system channel
                    _channelManager.AddSessionToSystemChannel(session);
                    
                    // Add session to any public channels
                    _channelManager.AddSessionToPublicChannels(session);
                }
            }
            else if (sessionId != null)
            {
                // Update existing session
                if (_sessions.TryGetValue(sessionId, out FastUdpSession? session) && session != null)
                {
                    // Just mark as reconnection if this session has already sent significant data
                    if (session.LastPacketTime.AddSeconds(5) < DateTime.Now)
                    {
                        session.UpdateActivity();
                        session.SetAuthenticationState(true);
                        LogServer($"Reconnection for session {sessionId} to {endpointKey}", 
                                LogLevel.Basic, ConsoleColor.Yellow, sessionId);
                    }
                    else
                    {
                        // Otherwise, just silently update activity
                        session.UpdateActivity();
                    }
                }
            }
            
            // Ensure we have a valid session ID
            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
                isNewSession = true;
                
                // We need to create a new session here as well
                var session = new FastUdpSession(sessionId, remote, _socket);
                session.SetAuthenticationState(true);
                
                // Register callbacks for session events
                session.Disconnected += OnSessionDisconnected;
                session.MessageSent += OnSessionMessageSent;
                
                if (_sessions.TryAdd(sessionId, session))
                {
                    _endpointToSessionId[endpointKey] = sessionId;
                }
            }
            
            // Send connection response with session ID
            var connectResponse = FastPacket.CreateConnectResponse(sessionId);
            byte[] responseData = connectResponse.Serialize();
            
            // Get session and use Send method
            if (_sessions.TryGetValue(sessionId, out FastUdpSession? existingSession) && existingSession != null)
            {
                existingSession.Send(responseData);
            }
            else
            {
                // Fallback to traditional method if session not available
                _socket.SendTo(responseData, remote);
            }
            
            // Log only if it's a new session to reduce noise
            if (isNewSession)
            {
                LogServer($"Sending connection response to {endpointKey} with ID: {sessionId}", 
                        LogLevel.Basic, ConsoleColor.Green, sessionId);
            }
            else
            {
                LogServer($"Sending connection response to {endpointKey}", 
                        LogLevel.Verbose, ConsoleColor.Green, sessionId);
            }
        }
        
        // Session event handlers
        private void OnSessionDisconnected(object? sender, string reason)
        {
            if (sender is FastUdpSession session)
            {
                var endpointKey = session.RemoteEndPoint.ToString();
                LogServer($"Session {session.Id} disconnected: {reason}", LogLevel.Basic, ConsoleColor.Yellow, session.Id);
                
                // Remove from all channels
                var sessionChannels = _channelManager.GetSessionChannels(session.Id).ToList();
                foreach (var channel in sessionChannels)
                {
                    // Don't need to explicitly remove - the session's Disconnected event
                    // is handled by the channel itself through event subscription
                    
                    // But we'll check if this was the owner of a private channel
                    if (channel.Type == EChannelType.Private && 
                        channel.Owner.Id == session.Id && 
                        channel.SessionCount <= 1)
                    {
                        // Owner disconnected and no other members, delete the channel
                        _channelManager.RemoveChannel(channel.Id);
                        LogServer($"Channel {channel.Name} ({channel.Id}) deleted after owner disconnected", 
                                  LogLevel.Basic, ConsoleColor.Yellow, session.Id);
                    }
                }
                
                // Remove from session ID mapping
                _endpointToSessionId.TryRemove(endpointKey, out _);
                
                // Remove from session dictionary
                _sessions.TryRemove(session.Id, out _);
            }
        }
        
        private void OnSessionMessageSent(object? sender, string message)
        {
            if (sender is FastUdpSession session)
            {
                //LogServer($"Message sent to session {session.Id}: {message}", LogLevel.Verbose, ConsoleColor.Cyan, session.Id);
            }
        }
        
        private void HandleDisconnect(IPEndPoint remote, string endpointKey, FastPacket packet)
        {
            if (_endpointToSessionId.TryGetValue(endpointKey, out string? sessionId) && sessionId != null)
            {
                if (_sessions.TryRemove(sessionId, out FastUdpSession? session) && session != null)
                {
                    _endpointToSessionId.TryRemove(endpointKey, out _);
                    
                    string reason = packet.GetDataAsString();
                    LogServer($"Client disconnected: {sessionId} ({endpointKey}), Reason: {reason}", 
                              LogLevel.Basic, ConsoleColor.Yellow, sessionId);
                }
            }
        }
        
        private void HandleDataPacket(IPEndPoint remote, string endpointKey, FastPacket packet)
        {
            var now = DateTime.Now;
            
            // For data packets, check if there's an active session
            if (_endpointToSessionId.TryGetValue(endpointKey, out string? existingSessionId) && existingSessionId != null)
            {
                if (_sessions.TryGetValue(existingSessionId, out FastUdpSession? existingSession) && existingSession != null)
                {
                    existingSession.UpdateActivity();
                    
                    // Update connection statistics
                    existingSession.Connection.AddReceivedBytes(packet.Data.Length + 1); // +1 for the type
                    
                    // Check for channel commands
                    if (packet.Type == EPacketType.Message)
                    {
                        string message = packet.GetDataAsString();
                        
                        // Check if it's a channel command (starts with #)
                        if (message.StartsWith("#"))
                        {
                            //LogServer($"CHANNEL COMMAND from {existingSessionId}: {message}", 
                            //      LogLevel.Basic, ConsoleColor.Magenta, existingSessionId);
                            ProcessChannelCommand(existingSession, message);
                            return; // Don't echo channel commands
                        }
                        
                        // Regular message - log it
                        //LogServer($"Message from {existingSessionId} ({endpointKey}): {message}", 
                        //          LogLevel.Verbose, ConsoleColor.Cyan, existingSessionId);
                    }
                    else
                    {
                        LogServer($"Binary data from {existingSessionId} ({endpointKey}): {packet.Data.Length} bytes", 
                                  LogLevel.Verbose, ConsoleColor.Yellow, existingSessionId);
                    }
                    
                    // Echo the message (default response) using the session's Send method
                    existingSession.SendPacket(packet);
                }
                else
                {
                    // Session not found, request reconnection
                    SendReconnectPacket(remote);
                    LogServer($"Requesting reconnection for client {endpointKey} (session not found)", 
                              LogLevel.Basic, ConsoleColor.Yellow, existingSessionId);
                }
            }
            else
            {
                // Unknown endpoint, request connection
                SendConnectRequiredPacket(remote);
                LogServer($"Requesting connection for unknown client {endpointKey}", 
                          LogLevel.Basic, ConsoleColor.Yellow);
            }
        }
        
        private void SendErrorPacket(IPEndPoint remote, string errorMessage)
        {
            var errorPacket = new FastPacket(EPacketType.Error, errorMessage);
            byte[] packetData = errorPacket.Serialize();
            _socket.SendTo(packetData, remote);
        }
        
        private void SendReconnectPacket(IPEndPoint remote)
        {
            var reconnectPacket = FastPacket.CreateReconnect();
            byte[] packetData = reconnectPacket.Serialize();
            _socket.SendTo(packetData, remote);
        }
        
        private void SendConnectRequiredPacket(IPEndPoint remote)
        {
            var connectPacket = FastPacket.CreateConnect();
            byte[] packetData = connectPacket.Serialize();
            _socket.SendTo(packetData, remote);
        }

        public void Stop()
        {
            _running = false;
            _sessionMonitor.Stop();
            
            // Send system broadcast about shutdown
            SystemBroadcast("Server is shutting down");
            
            LogServer($"Disconnecting {_sessions.Count} clients", LogLevel.Basic, ConsoleColor.Yellow);
            
            // Iterate through sessions and call Disconnect method for each
            foreach (var session in _sessions.Values)
            {
                try
                {
                    // Use session's Disconnect method to send notification
                    session.Disconnect("Server shutdown");
                    LogServer($"Notifying shutdown to {session.Id}", LogLevel.Verbose, ConsoleColor.Magenta, session.Id);
                }
                catch
                {
                    // Ignore errors when trying to notify
                }
            }
            
            // Small pause to allow messages to be sent
            Thread.Sleep(500);
            
            // Clear collections
            _sessions.Clear();
            _endpointToSessionId.Clear();
            
            _socket.Close();
            LogServer("UDP server stopped.", LogLevel.Critical, ConsoleColor.Red);
        }

        // Channel-related methods
        
        // Event handlers for channel manager
        private void OnChannelCreated(object? sender, FastUdpChannel channel)
        {
            LogServer($"Channel '{channel.Name}' created with ID {channel.Id}, Type: {channel.Type}", 
                      LogLevel.Basic, ConsoleColor.Green, channel.Id);
        }
        
        private void OnChannelRemoved(object? sender, FastUdpChannel channel)
        {
            LogServer($"Channel '{channel.Name}' with ID {channel.Id} removed", 
                      LogLevel.Basic, ConsoleColor.Yellow, channel.Id);
        }
        
        // Create a new channel with the specified session as owner
        public FastUdpChannel CreateChannel(string sessionId, string channelName, EChannelType type = EChannelType.Private)
        {
            if (_sessions.TryGetValue(sessionId, out FastUdpSession? session) && session != null)
            {
                var channel = _channelManager.CreateChannel(session, channelName, type);
                LogServer($"Created new channel '{channelName}' (ID: {channel.Id}) with owner {sessionId}", 
                          LogLevel.Basic, ConsoleColor.Green, sessionId);
                return channel;
            }
            
            throw new ArgumentException($"Session with ID {sessionId} not found");
        }
        
        // Add a session to a channel
        public bool AddSessionToChannel(string sessionId, string channelId)
        {
            if (_sessions.TryGetValue(sessionId, out FastUdpSession? session) && session != null)
            {
                var channel = _channelManager.GetChannel(channelId);
                if (channel != null)
                {
                    bool result = channel.AddSession(session);
                    if (result)
                    {
                        LogServer($"Added session {sessionId} to channel '{channel.Name}' (ID: {channelId})", 
                                  LogLevel.Basic, ConsoleColor.Green, sessionId);
                    }
                    return result;
                }
            }
            
            return false;
        }
        
        // Remove a session from a channel
        public bool RemoveSessionFromChannel(string sessionId, string channelId)
        {
            var channel = _channelManager.GetChannel(channelId);
            if (channel != null)
            {
                bool result = channel.RemoveSession(sessionId);
                if (result)
                {
                    LogServer($"Removed session {sessionId} from channel '{channel.Name}' (ID: {channelId})", 
                              LogLevel.Basic, ConsoleColor.Yellow, sessionId);
                }
                return result;
            }
            
            return false;
        }
        
        // Get all channels where a session is a member
        public IEnumerable<FastUdpChannel> GetSessionChannels(string sessionId)
        {
            return _channelManager.GetSessionChannels(sessionId);
        }
        
        // Delete a channel
        public bool DeleteChannel(string channelId)
        {
            bool result = _channelManager.RemoveChannel(channelId);
            if (result)
            {
                LogServer($"Deleted channel with ID {channelId}", 
                          LogLevel.Basic, ConsoleColor.Yellow, channelId);
            }
            return result;
        }
        
        // Get a channel by ID
        public FastUdpChannel? GetChannel(string channelId)
        {
            return _channelManager.GetChannel(channelId);
        }
        
        // Get all channels
        public IEnumerable<FastUdpChannel> GetAllChannels()
        {
            return _channelManager.GetAllChannels();
        }
        
        // Broadcast a message to all sessions in a channel
        public bool BroadcastToChannel(string channelId, string message, string? senderSessionId = null)
        {
            var channel = _channelManager.GetChannel(channelId);
            if (channel != null)
            {
                FastUdpSession? sender = null;
                if (senderSessionId != null)
                {
                    _sessions.TryGetValue(senderSessionId, out sender);
                }
                
                bool result = channel.Broadcast(message, sender);
                if (result)
                {
                    LogServer($"Broadcast message to channel '{channel.Name}' (ID: {channelId})", 
                              LogLevel.Basic, ConsoleColor.Cyan, channelId);
                }
                return result;
            }
            
            return false;
        }
        
        // Broadcast a message to all connected sessions (system channel)
        public bool SystemBroadcast(string message)
        {
            bool result = _channelManager.SystemBroadcast(message);
            if (result)
            {
                LogServer($"System broadcast message sent to all sessions", 
                          LogLevel.Basic, ConsoleColor.Cyan);
            }
            return result;
        }
        
        // Get the system channel
        public FastUdpSystemChannel GetSystemChannel()
        {
            return _channelManager.SystemChannel;
        }

        // Process channel-related commands from clients
        private void ProcessChannelCommand(FastUdpSession session, string command)
        {
            try
            {
                // Split the command into parts
                string[] parts = command.Split(':', StringSplitOptions.RemoveEmptyEntries);
                
                if (parts.Length < 1)
                    return;
                
                // Get the command type (first part, removing the # prefix)
                string commandType = parts[0].TrimStart('#');
                
                //LogServer($"Processing channel command: {commandType} from session {session.Id}", 
                //         LogLevel.Basic, ConsoleColor.Magenta, session.Id);
                
                switch (commandType.ToUpper())
                {
                    case "JOIN":
                        // #JOIN:channelId
                        if (parts.Length >= 2)
                        {
                            string channelId = parts[1];
                            //LogServer($"JOIN command for channel {channelId} from session {session.Id}", 
                            //         LogLevel.Basic, ConsoleColor.Green, session.Id);
                            HandleJoinChannelCommand(session, channelId);
                        }
                        break;
                        
                    case "LEAVE":
                        // #LEAVE:channelId
                        if (parts.Length >= 2)
                        {
                            string channelId = parts[1];
                            HandleLeaveChannelCommand(session, channelId);
                        }
                        break;
                        
                    case "CREATE":
                        // #CREATE:channelName:type
                        if (parts.Length >= 3)
                        {
                            string channelName = parts[1];
                            bool isPublic = parts[2].ToLower() == "public";
                            LogServer($"CREATE command for channel '{channelName}' (public: {isPublic}) from session {session.Id}", 
                                     LogLevel.Basic, ConsoleColor.Green, session.Id);
                            HandleCreateChannelCommand(session, channelName, isPublic);
                        }
                        break;
                        
                    case "CHANNEL":
                        // #CHANNEL:channelId:message
                        if (parts.Length >= 3)
                        {
                            string channelId = parts[1];
                            string message = string.Join(":", parts.Skip(2));
                            LogServer($"CHANNEL message command for channel {channelId} from session {session.Id}: {message}", 
                                     LogLevel.Basic, ConsoleColor.Cyan, session.Id);
                            HandleChannelMessageCommand(session, channelId, message);
                        }
                        break;
                        
                    case "CHANNELS":
                        // #CHANNELS - Request channel list
                        HandleChannelListCommand(session);
                        break;
                        
                    default:
                        LogServer($"Unknown channel command from {session.Id}: {command}", 
                                  LogLevel.Verbose, ConsoleColor.Yellow, session.Id);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogServer($"Error processing channel command from {session.Id}: {ex.Message}", 
                          LogLevel.Warning, ConsoleColor.Red, session.Id);
            }
        }
        
        private void HandleJoinChannelCommand(FastUdpSession session, string channelId)
        {
            var channel = _channelManager.GetChannel(channelId);
            if (channel != null)
            {
                //LogServer($"Join request for channel {channelId} ({channel.Name}) by session {session.Id}", 
                //         LogLevel.Basic, ConsoleColor.Green, session.Id);
                
                // Check if it's a private channel
                if (channel.Type == EChannelType.Private)
                {
                    // For private channels, only the owner can add users
                    // We'll respond with an error
                    session.SendMessage($"Error: Cannot join private channel {channelId}. Must be invited by channel owner.");
                    //LogServer($"Session {session.Id} attempted to join private channel {channelId}", 
                    //          LogLevel.Basic, ConsoleColor.Yellow, session.Id);
                    return;
                }
                
                // Add the session to the channel
                if (channel.AddSession(session))
                {
                    // List all members in the channel for debug
                    var members = channel.GetSessions().Select(s => s.Id).ToList();
                    /*LogServer($"Session {session.Id} successfully joined channel {channel.Name} ({channelId}). " +
                             $"Channel now has {members.Count} members: {string.Join(", ", members)}", 
                             LogLevel.Basic, ConsoleColor.Green, session.Id);*/
                    
                    // Notify the session
                    session.SendMessage($"Joined channel: {channel.Name} ({channelId})");
                    
                    // Notify other channel members
                    channel.Broadcast($"[Channel:{channel.Name}] User {session.Id} joined the channel", null);
                }
                else
                {
                    // Session might already be in the channel
                    session.SendMessage($"Already in channel: {channel.Name} ({channelId})");
                    //LogServer($"Session {session.Id} was already in channel {channel.Name} ({channelId})", 
                    //         LogLevel.Basic, ConsoleColor.Yellow, session.Id);
                }
            }
            else
            {
                // Channel not found
                session.SendMessage($"Error: Channel {channelId} not found");
                //LogServer($"Session {session.Id} attempted to join non-existent channel {channelId}", 
                //          LogLevel.Basic, ConsoleColor.Yellow, session.Id);
            }
        }
        
        private void HandleLeaveChannelCommand(FastUdpSession session, string channelId)
        {
            var channel = _channelManager.GetChannel(channelId);
            if (channel != null)
            {
                // Check if session is in the channel
                if (channel.HasSession(session.Id))
                {
                    // Don't allow leaving system channel
                    if (channel.Type == EChannelType.System)
                    {
                        session.SendMessage("Error: Cannot leave system channel");
                        return;
                    }
                    
                    // Check if the session is the owner
                    bool isOwner = channel.Owner.Id == session.Id;
                    
                    // Remove the session from the channel
                    if (channel.RemoveSession(session.Id))
                    {
                        // Notify the session
                        session.SendMessage($"Left channel: {channel.Name} ({channelId})");
                        LogServer($"Session {session.Id} left channel {channel.Name} ({channelId})", 
                                  LogLevel.Basic, ConsoleColor.Yellow, session.Id);
                        
                        // Notify other channel members
                        channel.Broadcast($"[Channel:{channel.Name}] User {session.Id} left the channel", null);
                        
                        // If owner left and no sessions remain, delete the channel
                        if (isOwner && channel.SessionCount == 0)
                        {
                            _channelManager.RemoveChannel(channelId);
                            LogServer($"Channel {channel.Name} ({channelId}) deleted after owner left", 
                                      LogLevel.Basic, ConsoleColor.Yellow, session.Id);
                        }
                    }
                    else
                    {
                        // Failed to remove
                        session.SendMessage($"Error: Failed to leave channel {channel.Name} ({channelId})");
                    }
                }
                else
                {
                    // Not in channel
                    session.SendMessage($"Error: Not in channel {channel.Name} ({channelId})");
                }
            }
            else
            {
                // Channel not found
                session.SendMessage($"Error: Channel {channelId} not found");
            }
        }
        
        private void HandleCreateChannelCommand(FastUdpSession session, string channelName, bool isPublic)
        {
            try
            {
                // Check if session is authenticated
                if (!session.IsAuthenticated)
                {
                    session.SendMessage("Error: Must be authenticated to create channels");
                    return;
                }
                
                // Create the channel
                var type = isPublic ? EChannelType.Public : EChannelType.Private;
                var channel = _channelManager.CreateChannel(session, channelName, type);
                
                LogServer($"Created {(isPublic ? "public" : "private")} channel: {channelName} " +
                         $"(ID: {channel.Id}) with owner {session.Id}", 
                         LogLevel.Critical, ConsoleColor.Green, session.Id);
                
                // Notify the session
                session.SendMessage($"Created {(isPublic ? "public" : "private")} channel: {channelName} (ID: {channel.Id})");
                
                // For public channels, broadcast to system channel that a new channel is available
                /*if (isPublic)
                {
                    _channelManager.SystemBroadcast($"New public channel created: {channelName} (ID: {channel.Id})");
                }*/
            }
            catch (Exception ex)
            {
                session.SendMessage($"Error creating channel: {ex.Message}");
                LogServer($"Error creating channel for session {session.Id}: {ex.Message}", 
                          LogLevel.Warning, ConsoleColor.Red, session.Id);
            }
        }
        
        private void HandleChannelMessageCommand(FastUdpSession session, string channelId, string message)
        {
            var channel = _channelManager.GetChannel(channelId);
            if (channel != null)
            {
                LogServer($"Channel message request from {session.Id} to channel {channelId} ({channel.Name}): {message}", 
                         LogLevel.Basic, ConsoleColor.Cyan, session.Id);
                
                // Check if session is in the channel
                if (channel.HasSession(session.Id))
                {
                    // Check if this session is allowed to broadcast
                    bool canBroadcast = channel.IsAllowedToBroadcast(session);
                    LogServer($"Session {session.Id} allowed to broadcast to channel {channel.Name} ({channelId}): {canBroadcast}", 
                             LogLevel.Basic, ConsoleColor.Magenta, session.Id);
                    
                    if (canBroadcast)
                    {
                        // Broadcast the message to the channel
                        string formattedMessage = $"[Channel:{channel.Name}] {session.Id}: {message}";
                        
                        // Get list of recipients for logging
                        var recipients = channel.GetSessions()
                            .Where(s => s.Id != session.Id) // Exclude sender
                            .Select(s => s.Id)
                            .ToList();
                        
                        //LogServer($"Broadcasting message to {recipients.Count} members of channel {channel.Name} ({channelId}): " +
                        //         $"{(recipients.Count > 0 ? string.Join(", ", recipients) : "none")}", 
                        //         LogLevel.Basic, ConsoleColor.Green, session.Id);
                        
                        if (channel.Broadcast(formattedMessage, session))
                        {
                            LogServer($"Successfully broadcast message from {session.Id} to channel {channel.Name} ({channelId})", 
                                     LogLevel.Verbose, ConsoleColor.Cyan, session.Id);
                        }
                        else
                        {
                            LogServer($"Failed to broadcast message from {session.Id} to channel {channel.Name} ({channelId})", 
                                     LogLevel.Warning, ConsoleColor.Red, session.Id);
                            session.SendMessage($"Error: Failed to broadcast to channel {channel.Name} ({channelId})");
                        }
                    }
                    else
                    {
                        LogServer($"Session {session.Id} NOT allowed to broadcast to channel {channel.Name} ({channelId})", 
                                 LogLevel.Warning, ConsoleColor.Red, session.Id);
                        session.SendMessage($"Error: Not allowed to broadcast to channel {channel.Name} ({channelId})");
                    }
                }
                else
                {
                    // Not in channel
                    session.SendMessage($"Error: Not in channel {channel.Name} ({channelId})");
                    LogServer($"Session {session.Id} attempted to broadcast to channel {channel.Name} ({channelId}) but is not a member", 
                             LogLevel.Basic, ConsoleColor.Yellow, session.Id);
                }
            }
            else
            {
                // Channel not found
                session.SendMessage($"Error: Channel {channelId} not found");
                LogServer($"Session {session.Id} attempted to broadcast to non-existent channel {channelId}", 
                         LogLevel.Basic, ConsoleColor.Yellow, session.Id);
            }
        }
        
        private void HandleChannelListCommand(FastUdpSession session)
        {
            try
            {
                // Get all public channels
                var publicChannels = _channelManager.GetChannelsByType(EChannelType.Public).ToList();
                
                // Get all channels owned by the session
                var ownedChannels = _channelManager.GetAllChannels()
                    .Where(c => c.Type == EChannelType.Private && c.Owner.Id == session.Id)
                    .ToList();
                
                // Get all channels the session is a member of
                var memberChannels = _channelManager.GetSessionChannels(session.Id).ToList();
                
                // Build the response
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Channel List:");
                
                sb.AppendLine("--- Public Channels ---");
                foreach (var channel in publicChannels)
                {
                    sb.AppendLine($"{channel.Name} (ID: {channel.Id}) - {channel.SessionCount} members");
                }
                
                sb.AppendLine("--- Owned Channels ---");
                foreach (var channel in ownedChannels)
                {
                    sb.AppendLine($"{channel.Name} (ID: {channel.Id}) - {channel.SessionCount} members");
                }
                
                sb.AppendLine("--- Member Channels ---");
                foreach (var channel in memberChannels.Except(publicChannels).Except(ownedChannels))
                {
                    sb.AppendLine($"{channel.Name} (ID: {channel.Id}) - {channel.SessionCount} members");
                }
                
                // Send the response
                session.SendMessage(sb.ToString());
                LogServer($"Sent channel list to session {session.Id}", LogLevel.Verbose, ConsoleColor.Cyan, session.Id);
            }
            catch (Exception ex)
            {
                session.SendMessage($"Error retrieving channel list: {ex.Message}");
                LogServer($"Error retrieving channel list for session {session.Id}: {ex.Message}", 
                          LogLevel.Warning, ConsoleColor.Red, session.Id);
            }
        }
    }
}


