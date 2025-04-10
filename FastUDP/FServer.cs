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

                if (_endpointToSessionId.TryGetValue(endpointKey, out string? sessionId) && sessionId != null){
                    LogServer($"Packet received from {endpointKey}: Type={packet.Type} SessionId={sessionId}", 
                              LogLevel.Verbose, ConsoleColor.Cyan, sessionId);
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
                LogServer($"Message sent to session {session.Id}: {message}", LogLevel.Verbose, ConsoleColor.Cyan, session.Id);
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
                            ProcessChannelCommand(existingSession, message);
                            return; // Don't echo channel commands
                        }
                        
                        // Regular message - log it
                        LogServer($"Message from {existingSessionId} ({endpointKey}): {message}", 
                                  LogLevel.Verbose, ConsoleColor.Cyan, existingSessionId);
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
                
                switch (commandType.ToUpper())
                {
                    case "JOIN":
                        // #JOIN:channelId
                        if (parts.Length >= 2)
                        {
                            string channelId = parts[1];
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
                            HandleCreateChannelCommand(session, channelName, isPublic);
                        }
                        break;
                        
                    case "CHANNEL":
                        // #CHANNEL:channelId:message
                        if (parts.Length >= 3)
                        {
                            string channelId = parts[1];
                            string message = string.Join(":", parts.Skip(2));
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
                // Check if it's a private channel
                if (channel.Type == EChannelType.Private)
                {
                    // For private channels, only the owner can add users
                    // We'll respond with an error
                    session.SendMessage($"Error: Cannot join private channel {channelId}. Must be invited by channel owner.");
                    LogServer($"Session {session.Id} attempted to join private channel {channelId}", 
                              LogLevel.Basic, ConsoleColor.Yellow, session.Id);
                    return;
                }
                
                // Add the session to the channel
                if (channel.AddSession(session))
                {
                    // Notify the session
                    session.SendMessage($"Joined channel: {channel.Name} ({channelId})");
                    LogServer($"Session {session.Id} joined channel {channel.Name} ({channelId})", 
                              LogLevel.Basic, ConsoleColor.Green, session.Id);
                    
                    // Notify other channel members
                    channel.Broadcast($"[Channel:{channel.Name}] User {session.Id} joined the channel", null);
                }
                else
                {
                    // Session might already be in the channel
                    session.SendMessage($"Already in channel: {channel.Name} ({channelId})");
                }
            }
            else
            {
                // Channel not found
                session.SendMessage($"Error: Channel {channelId} not found");
                LogServer($"Session {session.Id} attempted to join non-existent channel {channelId}", 
                          LogLevel.Basic, ConsoleColor.Yellow, session.Id);
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
                
                // Notify the session
                session.SendMessage($"Created {(isPublic ? "public" : "private")} channel: {channelName} (ID: {channel.Id})");
                LogServer($"Session {session.Id} created {(isPublic ? "public" : "private")} channel {channelName} ({channel.Id})", 
                          LogLevel.Basic, ConsoleColor.Green, session.Id);
                
                // For public channels, broadcast to system channel that a new channel is available
                if (isPublic)
                {
                    _channelManager.SystemBroadcast($"New public channel created: {channelName} (ID: {channel.Id})");
                }
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
                // Check if session is in the channel
                if (channel.HasSession(session.Id))
                {
                    // Broadcast the message to the channel
                    string formattedMessage = $"[Channel:{channel.Name}] {session.Id}: {message}";
                    if (channel.Broadcast(formattedMessage, session))
                    {
                        LogServer($"Session {session.Id} sent message to channel {channel.Name} ({channelId}): {message}", 
                                  LogLevel.Verbose, ConsoleColor.Cyan, session.Id);
                    }
                    else
                    {
                        session.SendMessage($"Error: Not allowed to broadcast to channel {channel.Name} ({channelId})");
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


