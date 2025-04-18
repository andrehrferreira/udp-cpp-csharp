using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace FastUDP {
    public enum EChannelType
    {
        System,     // System channel, used for global broadcasts and management
        Private,    // Private channel, only accessible by selected sessions
        Public      // Public channel, any session can join
    }
    
    public class FastUdpChannel
    {
        // Basic channel properties
        public string Id { get; private set; }
        public FastUdpSession Owner { get; private set; }
        public string Name { get; private set; }
        public EChannelType Type { get; private set; }
        
        // Collection of sessions in this channel
        protected readonly ConcurrentDictionary<string, FastUdpSession> _sessions = new();
        
        // Thread pool reference (optional)
        private FastThreadPool? _threadPool;
        private Socket? _sharedSocket;
        
        // Events
        public event EventHandler<FastUdpSession>? SessionAdded;
        public event EventHandler<FastUdpSession>? SessionRemoved;
        public event EventHandler<string>? BroadcastSent;
        public event EventHandler<string>? ChannelMessageReceived;
        
        // Protected methods to raise events (for derived classes)
        protected virtual void OnSessionAdded(FastUdpSession session)
        {
            SessionAdded?.Invoke(this, session);
        }
        
        protected virtual void OnSessionRemoved(FastUdpSession session)
        {
            SessionRemoved?.Invoke(this, session);
        }
        
        protected virtual void OnBroadcastSent(string message)
        {
            BroadcastSent?.Invoke(this, message);
        }
        
        public FastUdpChannel(string id, FastUdpSession owner, string name, EChannelType type)
        {
            Id = id;
            Owner = owner;
            Name = name;
            Type = type;
            
            // Register for owner's events to auto-cleanup
            owner.Disconnected += OnSessionDisconnected;
            
            // Add owner to the channel automatically
            AddSession(owner);
        }
        
        // Static method to create a system channel (no owner)
        public static FastUdpChannel CreateSystemChannel(string id, string name)
        {
            // For system channels, we don't have an owner session
            // We'll pass null and handle it in the constructor
            return new FastUdpSystemChannel(id, name);
        }
        
        // Get all sessions in this channel
        public IEnumerable<FastUdpSession> GetSessions()
        {
            return _sessions.Values;
        }
        
        // Get session count
        public int SessionCount => _sessions.Count;
        
        // Add a session to this channel
        public virtual bool AddSession(FastUdpSession session)
        {
            if (session == null)
                return false;
                
            // Register for disconnection event to auto-cleanup
            session.Disconnected += OnSessionDisconnected;
            
            if (_sessions.TryAdd(session.Id, session))
            {
                // Use the protected method to raise the event
                OnSessionAdded(session);
                return true;
            }
            
            return false;
        }
        
        // Remove a session from this channel
        public virtual bool RemoveSession(string sessionId)
        {
            if (_sessions.TryRemove(sessionId, out FastUdpSession? session) && session != null)
            {
                // Unregister from events
                session.Disconnected -= OnSessionDisconnected;
                
                // Use the protected method to raise the event
                OnSessionRemoved(session);
                return true;
            }
            
            return false;
        }
        
        // Check if a session is in this channel
        public bool HasSession(string sessionId)
        {
            return _sessions.ContainsKey(sessionId);
        }
        
        // Handler for session disconnection
        private void OnSessionDisconnected(object? sender, string reason)
        {
            if (sender is FastUdpSession session)
            {
                RemoveSession(session.Id);
            }
        }
        
        // Set thread pool for optimization
        public void SetThreadPool(FastThreadPool threadPool, Socket sharedSocket)
        {
            _threadPool = threadPool;
            _sharedSocket = sharedSocket;
        }
        
        // Broadcast a message to all sessions in this channel
        public virtual bool Broadcast(string message, FastUdpSession? sender = null)
        {
            try
            {
                // Skip if no sessions or sender is the only session
                if (_sessions.Count == 0 || (_sessions.Count == 1 && sender != null && _sessions.ContainsKey(sender.Id)))
                {
                    return true;
                }
                
                var packet = new FastPacket(EPacketType.Message, message);
                byte[] packetData = packet.Serialize();
                
                // If we have a thread pool, use optimized broadcast method
                if (_threadPool != null && _sharedSocket != null)
                {
                    // Get all endpoints except sender
                    List<EndPoint> targets = new List<EndPoint>();
                    foreach (var session in _sessions.Values)
                    {
                        if (sender == null || session.Id != sender.Id)
                        {
                            targets.Add(session.RemoteEndPoint);
                        }
                    }
                    
                    // Use thread pool for broadcast
                    int enqueued = _threadPool.EnqueueBroadcast(_sharedSocket, targets, packetData);
                    
                    // Log the broadcast
                    BroadcastSent?.Invoke(this, $"Broadcast enqueued to {enqueued} clients");
                    
                    return enqueued > 0;
                }
                
                // Traditional method as fallback
                int sent = 0;
                foreach (var session in _sessions.Values)
                {
                    if (sender == null || session.Id != sender.Id)
                    {
                        session.Send(packetData);
                        sent++;
                    }
                }
                
                BroadcastSent?.Invoke(this, $"Broadcast sent to {sent} clients");
                return sent > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in channel broadcast: {ex.Message}");
                return false;
            }
        }
        
        // Broadcast binary data to all sessions in this channel
        public virtual bool BroadcastBinary(byte[] data, FastUdpSession? sender = null)
        {
            // Check permissions - only owner or system can broadcast
            if (sender != null && !IsAllowedToBroadcast(sender))
                return false;
                
            int sentCount = 0;
            
            // Create a proper packet
            var packet = new FastPacket(EPacketType.BinaryData, data);
            
            // Send to all sessions in the channel
            foreach (var session in _sessions.Values)
            {
                if (sender == null || session.Id != sender.Id)
                {
                    try
                    {
                        // SendPacket agora retorna void, então simplesmente chamamos o método
                        session.SendPacket(packet);
                        sentCount++;
                    }
                    catch
                    {
                        // Ignore send failures for individual clients
                    }
                }
            }
            
            if (sentCount > 0)
            {
                // Use the protected method to raise the event
                OnBroadcastSent($"Binary broadcast sent to {sentCount} sessions");
                return true;
            }
            
            return false;
        }
        
        // Broadcast a message to all sessions in this channel using the binary ChannelBroadcast protocol
        public virtual bool BroadcastChannelMessage(string channelId, string message, FastUdpSession? sender = null)
        {
            // Check permissions - only owner or system can broadcast
            if (sender != null && !IsAllowedToBroadcast(sender))
            {
                return false;
            }
            
            int sentCount = 0;
            
            try
            {                
                byte[] channelIdBytes = System.Text.Encoding.UTF8.GetBytes(channelId);
                if (channelIdBytes.Length > 255)
                {
                    throw new ArgumentException("Channel ID is too long (max 255 bytes)");
                }
                
                // Convert message to bytes
                byte[] messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
                
                // Create the complete data payload: [channelIdLength (1 byte)][channelId (n bytes)][message (remaining bytes)]
                byte[] packetData = new byte[1 + channelIdBytes.Length + messageBytes.Length];
                packetData[0] = (byte)channelIdBytes.Length;
                Buffer.BlockCopy(channelIdBytes, 0, packetData, 1, channelIdBytes.Length);
                Buffer.BlockCopy(messageBytes, 0, packetData, 1 + channelIdBytes.Length, messageBytes.Length);
                
                // Create a proper packet - with the sender's session ID
                var packet = new FastPacket(EPacketType.ChannelBroadcast, packetData, sender?.Id ?? "system");
                                
                // Send to all sessions in the channel except the sender (to avoid echo)
                foreach (var session in _sessions.Values)
                {
                    if (sender != null && session.Id == sender.Id)
                    {
                        continue; 
                    }
                    
                    try
                    {
                        // SendPacket agora retorna void, então simplesmente chamamos o método
                        session.SendPacket(packet);
                        sentCount++;
                    }
                    catch
                    {
                        // Ignore errors for individual sessions
                    }
                }
                                
                if (sentCount > 0)
                {
                    // Use the protected method to raise the event
                    OnBroadcastSent($"Binary channel broadcast sent to {sentCount} sessions");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[CHANNEL BROADCAST] Error creating binary broadcast: {ex.Message}");
                Console.ResetColor();
            }
            
            return false;
        }
        
        // Check if a session is allowed to broadcast
        public virtual bool IsAllowedToBroadcast(FastUdpSession session)
        {
            if (Type == EChannelType.Public)
            {
                bool isMember = HasSession(session.Id);
                return isMember;
            }
            
            bool result = session.Id == Owner.Id;
            return result;
        }
    }
    
    // Special system channel class (no owner, different broadcast permissions)
    public class FastUdpSystemChannel : FastUdpChannel
    {
        // Create a dummy session that can be used as the owner for a system channel
        private static FastUdpSession CreateDummySession()
        {
            // Create a dummy socket that won't be used
            Socket dummySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            
            // Create a dummy IPEndPoint
            IPEndPoint dummyEndpoint = new IPEndPoint(IPAddress.Any, 0);
            
            // Create a dummy session as the owner
            return new FastUdpSession("system", dummyEndpoint, dummySocket);
        }
        
        public FastUdpSystemChannel(string id, string name)
            : base(id, CreateDummySession(), name, EChannelType.System)
        {
            // System channels don't have a real owner in the traditional sense
        }
        
        // For system channels, all authenticated sessions can broadcast
        public override bool IsAllowedToBroadcast(FastUdpSession session)
        {
            // In system channel, any authenticated session can broadcast
            bool result = session.IsAuthenticated;
            if (result)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SYSTEM CHANNEL] Session {session.Id} IS authenticated and CAN broadcast to system channel");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[SYSTEM CHANNEL] Session {session.Id} is NOT authenticated and CANNOT broadcast to system channel");
                Console.ResetColor();
            }
            return result;
        }
        
        // Override to avoid null reference with Owner
        public override bool AddSession(FastUdpSession session)
        {
            if (session == null)
                return false;
                
            // Register for disconnection event to auto-cleanup
            session.Disconnected += OnSessionDisconnected;
            
            if (_sessions.TryAdd(session.Id, session))
            {
                // Use the protected method to raise the event
                OnSessionAdded(session);
                return true;
            }
            
            return false;
        }
        
        // Handle disconnection for system channel
        private void OnSessionDisconnected(object? sender, string reason)
        {
            if (sender is FastUdpSession session)
            {
                RemoveSession(session.Id);
            }
        }
    }
    
    // Channel manager to be used by the server
    public class FastUdpChannelManager
    {
        private readonly ConcurrentDictionary<string, FastUdpChannel> _channels = new();
        private FastUdpSystemChannel? _systemChannel;
        
        // Events
        public event EventHandler<FastUdpChannel>? ChannelCreated;
        public event EventHandler<FastUdpChannel>? ChannelRemoved;
        
        public FastUdpChannelManager()
        {
            // Create system channel by default
            _systemChannel = (FastUdpSystemChannel)CreateSystemChannel("system", "System Channel");
        }
        
        // Get the system channel
        public FastUdpSystemChannel SystemChannel => _systemChannel ?? 
            (_systemChannel = (FastUdpSystemChannel)CreateSystemChannel("system", "System Channel"));
        
        // Create a new channel
        public FastUdpChannel CreateChannel(FastUdpSession owner, string name, EChannelType type = EChannelType.Private)
        {
            // Generate a unique ID
            string channelId = Guid.NewGuid().ToString("N").Substring(0, 8);
            
            // Create channel instance
            var channel = new FastUdpChannel(channelId, owner, name, type);
            
            if (_channels.TryAdd(channelId, channel))
            {
                // Use proper event invocation pattern with null check
                if (ChannelCreated != null)
                {
                    ChannelCreated(this, channel);
                }
                return channel;
            }
            
            throw new InvalidOperationException("Failed to create channel. Duplicate ID generated.");
        }
        
        // Create a system channel (should be called only once at startup)
        public FastUdpChannel CreateSystemChannel(string id, string name)
        {
            var channel = FastUdpChannel.CreateSystemChannel(id, name);
            
            if (_channels.TryAdd(id, channel))
            {
                // Use proper event invocation pattern with null check
                if (ChannelCreated != null)
                {
                    ChannelCreated(this, channel);
                }
                return channel;
            }
            
            throw new InvalidOperationException("Failed to create system channel. ID already exists.");
        }
        
        // Remove a channel
        public bool RemoveChannel(string channelId)
        {
            // Don't allow removing the system channel
            if (channelId == _systemChannel?.Id)
                return false;
                
            if (_channels.TryRemove(channelId, out FastUdpChannel? channel) && channel != null)
            {
                // Use proper event invocation pattern with null check
                if (ChannelRemoved != null)
                {
                    ChannelRemoved(this, channel);
                }
                return true;
            }
            
            return false;
        }
        
        // Get a channel by ID
        public FastUdpChannel? GetChannel(string channelId)
        {
            return _channels.TryGetValue(channelId, out FastUdpChannel? channel) ? channel : null;
        }
        
        // Get all channels
        public IEnumerable<FastUdpChannel> GetAllChannels()
        {
            return _channels.Values;
        }
        
        // Get channels by type
        public IEnumerable<FastUdpChannel> GetChannelsByType(EChannelType type)
        {
            return _channels.Values.Where(c => c.Type == type);
        }
        
        // Get channels where a session is member
        public IEnumerable<FastUdpChannel> GetSessionChannels(string sessionId)
        {
            return _channels.Values.Where(c => c.HasSession(sessionId));
        }
        
        // Add a session to all public channels
        public void AddSessionToPublicChannels(FastUdpSession session)
        {
            foreach (var channel in GetChannelsByType(EChannelType.Public))
            {
                channel.AddSession(session);
            }
        }
        
        // Add a session to the system channel
        public void AddSessionToSystemChannel(FastUdpSession session)
        {
            SystemChannel.AddSession(session);
        }
        
        // Broadcast a message to all sessions on the system channel
        public bool SystemBroadcast(string message)
        {
            return SystemChannel.Broadcast(message);
        }
        
        // Broadcast binary data to all sessions on the system channel
        public bool SystemBroadcastBinary(byte[] data)
        {
            return SystemChannel.BroadcastBinary(data);
        }
    }
}
