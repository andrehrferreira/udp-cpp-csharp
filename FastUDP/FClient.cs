using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Timers;

namespace FastUDP {
    public class FastUdpClient : IDisposable
    {
        private readonly UdpClient _client;
        private readonly IPEndPoint _serverEndPoint;
        private string? _sessionId;
        private bool _connected;
        private readonly System.Timers.Timer _pingTimer;
        private readonly System.Timers.Timer _reconnectTimer;
        private System.Timers.Timer _connectTimeoutTimer;
        private bool _isReconnecting;
        private bool _serverShutdown;
        private bool _waitingForConnectResponse;
        private CancellationTokenSource _listenerCts;
        
        // List of channels user is part of
        private readonly List<string> _channels = new();
        
        // Eventos para notificar a aplicação
        public event EventHandler<string>? MessageReceived;
        public event EventHandler<string>? Connected;
        public event EventHandler<string>? Disconnected;
        public event EventHandler<string>? Reconnecting;
        public event EventHandler<string>? LogMessage;
        
        // Channel-related events
        public event EventHandler<string>? ChannelMessageReceived;
        public event EventHandler<string>? ChannelJoined;
        public event EventHandler<string>? ChannelLeft;
        
        public bool IsConnected => _connected;
        public string? SessionId => _sessionId;
        public bool DebugMode { get; set; } = false;
        
        // Níveis de log - apenas os níveis menores ou iguais ao definido serão exibidos
        public enum LogLevel { None = 0, Critical = 1, Warning = 2, Basic = 3, Verbose = 4, All = 5 }
        public LogLevel LoggingLevel { get; set; } = LogLevel.Basic; // Nível padrão
        
        public FastUdpClient(string serverIp, int serverPort)
        {
            _client = new UdpClient();
            _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIp), serverPort);
            _connected = false;
            _isReconnecting = false;
            _serverShutdown = false;
            _waitingForConnectResponse = false;
            _listenerCts = new CancellationTokenSource();
            
            // Configurar o socket para não bloquear
            _client.Client.Blocking = false;
            
            // Configurar timer de ping (5 segundos) - NÃO iniciar até ter conexão
            _pingTimer = new System.Timers.Timer(5000);
            _pingTimer.Elapsed += (s, e) => SendPing();
            _pingTimer.AutoReset = true;
            
            // Configurar timer de reconexão (30 segundos)
            _reconnectTimer = new System.Timers.Timer(30000);
            _reconnectTimer.Elapsed += (s, e) => TryReconnect();
            _reconnectTimer.AutoReset = true;
            
            // Configurar timer de timeout de conexão (15 segundos)
            _connectTimeoutTimer = new System.Timers.Timer(15000);
            _connectTimeoutTimer.Elapsed += HandleConnectTimeout;
            _connectTimeoutTimer.AutoReset = false;
            
            // Iniciar thread de escuta
            Task.Run(() => ListenForMessagesAsync(_listenerCts.Token));
            
            // Iniciar processo de conexão
            Connect();
        }
        
        public void Connect()
        {
            LogDebug("Starting connection to server...", LogLevel.Basic);
            _serverShutdown = false;
            _connected = false;
            _isReconnecting = false;
            
            // Cancel any previous timeout timer
            _connectTimeoutTimer.Stop();
            
            // Mark that we're waiting for a response
            _waitingForConnectResponse = true;
            
            // Send a Connect packet to establish a session
            try
            {
                LogDebug("Sending CONNECT packet to server...", LogLevel.Basic);
                // Bypass connection check for this specific packet
                byte[] packetData = FastPacket.CreateConnect().Serialize();
                _client.Send(packetData, packetData.Length, _serverEndPoint);
                
                // Start timeout timer (15 seconds)
                _connectTimeoutTimer.Start();
            }
            catch (Exception ex)
            {
                _waitingForConnectResponse = false;
                LogDebug($"Failed to send CONNECT: {ex.Message}", LogLevel.Critical);
                StartReconnect();
            }
        }
        
        private void SendPing()
        {
            // Check if we're connected before sending ping
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                LogDebug("Attempted to send PING without an established connection", LogLevel.Warning);
                _pingTimer.Stop(); // Stop the ping timer
                StartReconnect(); // Start reconnection
                return;
            }
            
            try
            {
                LogDebug("Sending PING to server...", LogLevel.All);
                SendPacket(FastPacket.CreatePing());
            }
            catch (Exception ex)
            {
                _connected = false;
                LogDebug($"Failed to send PING: {ex.Message}", LogLevel.Critical);
                Disconnected?.Invoke(this, "Failed to send ping");
                StartReconnect();
            }
        }
        
        private void StartReconnect()
        {
            _pingTimer.Stop();
            _connected = false; // Ensure we're marked as disconnected
            
            if (!_isReconnecting)
            {
                _isReconnecting = true;
                LogDebug("Starting reconnection process", LogLevel.Basic);
                Reconnecting?.Invoke(this, "Starting reconnection process");
                
                // Ensure the reconnect timer is running with the correct interval
                _reconnectTimer.Stop();
                _reconnectTimer.Start();
            }
        }
        
        private void TryReconnect()
        {
            if (_connected)
            {
                LogDebug("Already connected. Reconnection not needed.", LogLevel.Basic);
                _reconnectTimer.Stop();
                _isReconnecting = false;
                return;
            }
            
            LogDebug("Attempting to reconnect to server...", LogLevel.Basic);
            Reconnecting?.Invoke(this, "Attempting to reconnect...");
            
            // Temporarily stop the reconnect timer while attempting connection
            // It will be restarted by HandleConnectTimeout if connection fails
            _reconnectTimer.Stop();
            
            Connect();
        }
        
        private async Task ListenForMessagesAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Configurar para receber de forma não bloqueante
                    if (_client.Available > 0)
                    {
                        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = _client.Receive(ref remoteEP);
                        ProcessReceivedPacket(data);
                    }
                    else
                    {
                        // Pequena pausa para não sobrecarregar a CPU
                        await Task.Delay(10, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Token de cancelamento foi acionado
                    break;
                }
                catch (SocketException ex)
                {
                    // Apenas registrar e continuar para erros de socket
                    if (ex.SocketErrorCode != SocketError.WouldBlock && 
                        ex.SocketErrorCode != SocketError.TimedOut)
                    {
                        LogDebug($"Erro de socket: {ex.Message}", LogLevel.Critical);
                    }
                    await Task.Delay(10, cancellationToken);
                }
                catch (Exception ex)
                {
                    LogDebug($"Erro ao receber mensagem: {ex.Message}", LogLevel.Critical);
                    await Task.Delay(100, cancellationToken);
                }
            }
        }
        
        private void ProcessReceivedPacket(byte[] data)
        {
            try
            {
                // Deserialize the packet
                var packet = new FastPacket(data);
                
                if (DebugMode)
                {
                    LogDebug($"Received packet type {packet.Type} from server, SessionId='{packet.SessionId}'", LogLevel.Verbose);
                    DebugPacketBytes(data);
                }
                
                // Process protocol messages
                switch (packet.Type)
                {
                    case EPacketType.Pong:
                        // Simple pong just confirms server is alive
                        //LogDebug("Received PONG from server - server is active", LogLevel.Basic);
                        break;
                        
                    case EPacketType.Reconnect:
                        LogDebug("Server requested reconnection", LogLevel.Basic);
                        _connected = false;
                        Connect();
                        break;
                        
                    case EPacketType.Shutdown:
                        LogDebug("Server is shutting down", LogLevel.Basic);
                        _connected = false;
                        _pingTimer.Stop();
                        
                        // Instead of stopping reconnection, schedule a delayed reconnection attempt
                        _reconnectTimer.Stop();
                        _reconnectTimer.Interval = 10000; // Try to reconnect after 10 seconds
                        _reconnectTimer.Start();
                        _isReconnecting = true;
                        
                        // Notify the application
                        Disconnected?.Invoke(this, "Server shutdown");
                        LogDebug("Will attempt to reconnect in 10 seconds...", LogLevel.Basic);
                        break;
                        
                    case EPacketType.Message:
                        // Normal message, notify the application
                        string message = packet.GetDataAsString();
                        
                        // Check if this is a channel message
                        if (message.StartsWith("[Channel:") && message.Contains("]"))
                        {
                            // Extract channel info and message
                            int closeBracketIndex = message.IndexOf(']');
                            if (closeBracketIndex > 0)
                            {
                                string channelSection = message.Substring(1, closeBracketIndex - 1);
                                string channelName = channelSection.Substring(channelSection.IndexOf(':') + 1);
                                string channelMessage = message.Substring(closeBracketIndex + 1).Trim();
                                
                                // Notify about channel message
                                ChannelMessageReceived?.Invoke(this, $"Channel {channelName}: {channelMessage}");
                            }
                            else
                            {
                                // If can't parse channel format, treat as regular message
                                MessageReceived?.Invoke(this, message);
                            }
                        }
                        else
                        {
                            // Regular message
                            MessageReceived?.Invoke(this, message);
                        }
                        break;
                        
                    case EPacketType.ConnectResponse:
                        HandleConnectResponse(packet);
                        break;
                        
                    case EPacketType.Error:
                        LogDebug($"Error received from server: {packet.GetDataAsString()}", LogLevel.Critical);
                        break;
                        
                    default:
                        LogDebug($"Unhandled packet type: {packet.Type}", LogLevel.Verbose);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error processing packet: {ex.Message}", LogLevel.Critical);
                if (DebugMode)
                {
                    DebugPacketBytes(data);
                }
            }
        }
        
        private void HandleConnectResponse(FastPacket packet)
        {
            // Stop the timeout timer as we received a response
            _connectTimeoutTimer.Stop();
            _waitingForConnectResponse = false;
            
            //LogDebug($"Processing ConnectResponse, received SessionId: '{packet.SessionId}'", LogLevel.Basic);
            
            if (string.IsNullOrEmpty(packet.SessionId))
            {
                LogDebug("WARNING: Empty session ID received in ConnectResponse packet!", LogLevel.Warning);
                // Try to reconnect if the session is invalid
                StartReconnect();
                return;
            }
            
            _sessionId = packet.SessionId;
            
            if (!_connected)
            {
                _connected = true;
                _isReconnecting = false;
                
                _reconnectTimer.Stop();
                // Restore default reconnection interval
                _reconnectTimer.Interval = 30000;
                
                // Start ping timer ONLY after establishing connection
                _pingTimer.Start();
                
                LogDebug($"Connected to server. Session ID: {_sessionId}", LogLevel.Basic);
                Connected?.Invoke(this, _sessionId);
            }
        }
        
        // Method to send data with a specific type
        public void SendPacket(FastPacket packet)
        {
            // CONNECT is the only packet that can be sent without an established connection
            if (packet.Type != EPacketType.Connect && !_connected)
            {
                LogDebug("Attempted to send while client is disconnected", LogLevel.Warning);
                throw new InvalidOperationException("Client is not connected. Wait for connection to be established before sending data.");
            }
            
            try
            {
                byte[] packetData = packet.Serialize();
                _client.Send(packetData, packetData.Length, _serverEndPoint);
            }
            catch (Exception ex)
            {
                LogDebug($"Error sending packet: {ex.Message}", LogLevel.Critical);
                if (_connected)
                {
                    _connected = false;
                    StartReconnect();
                    throw;
                }
            }
        }
        
        // Convenience method to send text message
        public void SendMessage(string message)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                throw new InvalidOperationException("Not connected to server. Wait for connection before sending messages.");
            }
            
            SendPacket(FastPacket.CreateMessage(message, _sessionId));
        }
        
        // Method to send binary data
        public void SendBinaryData(byte[] data)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                throw new InvalidOperationException("Not connected to server. Wait for connection before sending data.");
            }
            
            SendPacket(new FastPacket(EPacketType.BinaryData, data, _sessionId));
        }
        
        private void LogDebug(string message)
        {
            if (DebugMode)
            {
                var logMessage = $"[Client] {DateTime.Now:HH:mm:ss} - {message}";
                Console.WriteLine(logMessage);
                LogMessage?.Invoke(this, logMessage);
            }
        }
        
        private void LogDebug(string message, LogLevel level)
        {
            if (DebugMode && (int)level <= (int)LoggingLevel)
            {
                var logMessage = $"[Client] {DateTime.Now:HH:mm:ss} - {message}";
                Console.WriteLine(logMessage);
                LogMessage?.Invoke(this, logMessage);
            }
        }
        
        private void DebugPacketBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                LogDebug("Empty packet", LogLevel.Verbose);
                return;
            }
            
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Packet bytes ({data.Length}): ");
            
            // Show first bytes as hexadecimal
            int bytesToShow = Math.Min(32, data.Length);
            for (int i = 0; i < bytesToShow; i++)
            {
                sb.Append($"{data[i]:X2} ");
                if ((i + 1) % 16 == 0)
                    sb.AppendLine();
            }
            
            // Interpret first byte (type)
            sb.AppendLine();
            sb.AppendLine($"Type (byte 0): {(EPacketType)data[0]}");
            
            // If more than 1 byte
            if (data.Length > 1)
            {
                sb.AppendLine($"SessionId length (byte 1): {data[1]}");
                
                // If has SessionId
                if (data[1] > 0 && data.Length >= 2 + data[1])
                {
                    byte[] sessionIdBytes = new byte[data[1]];
                    Buffer.BlockCopy(data, 2, sessionIdBytes, 0, data[1]);
                    string sessionId = Encoding.ASCII.GetString(sessionIdBytes);
                    sb.AppendLine($"SessionId: '{sessionId}'");
                    
                    // If has data after sessionId
                    int dataOffset = 2 + data[1];
                    if (data.Length > dataOffset)
                    {
                        int dataLength = data.Length - dataOffset;
                        sb.AppendLine($"Data ({dataLength} bytes)");
                    }
                }
                else
                {
                    // Data without sessionId
                    int dataLength = data.Length - 1;
                    sb.AppendLine($"Data without sessionId ({dataLength} bytes)");
                }
            }
            
            LogDebug(sb.ToString(), LogLevel.Verbose);
        }
        
        // Handler para timeout da conexão
        private void HandleConnectTimeout(object? sender, ElapsedEventArgs e)
        {
            if (_waitingForConnectResponse && !_connected)
            {
                _waitingForConnectResponse = false;
                LogDebug("Connection timeout - Server offline or unreachable", LogLevel.Critical);
                Disconnected?.Invoke(this, "Server offline or unreachable");
                
                // Always attempt to reconnect, even if server was shut down
                _serverShutdown = false;
                
                // Schedule a new reconnection attempt after 15 seconds
                _reconnectTimer.Stop();
                _reconnectTimer.Interval = 15000; // Change to 15 seconds
                _reconnectTimer.Start(); // Explicitly start the timer to ensure it's running
                _isReconnecting = true;
                
                LogDebug("Will retry connection in 15 seconds", LogLevel.Basic);
            }
        }
        
        public void Dispose()
        {
            // Send disconnect packet if connected
            if (_connected)
            {
                try
                {
                    SendPacket(FastPacket.CreateDisconnect("Client disconnected normally"));
                }
                catch
                {
                    // Ignore errors when trying to disconnect
                }
            }
            
            _pingTimer.Stop();
            _reconnectTimer.Stop();
            _connectTimeoutTimer.Stop();
            _listenerCts.Cancel();
            _client.Close();
            
            LogDebug("Client disposed", LogLevel.Basic);
        }
        
        // Channel-related methods
        
        // Join a channel
        public void JoinChannel(string channelId)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                throw new InvalidOperationException("Not connected to server. Wait for connection before joining channels.");
            }
            
            string message = $"#JOIN:{channelId}";
            SendMessage(message);
            LogDebug($"Requested to join channel: {channelId}", LogLevel.Basic);
        }
        
        // Leave a channel
        public void LeaveChannel(string channelId)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                throw new InvalidOperationException("Not connected to server. Wait for connection before leaving channels.");
            }
            
            string message = $"#LEAVE:{channelId}";
            SendMessage(message);
            LogDebug($"Requested to leave channel: {channelId}", LogLevel.Basic);
            
            // Remove from local tracking
            _channels.Remove(channelId);
            ChannelLeft?.Invoke(this, channelId);
        }
        
        // Create a new channel
        public void CreateChannel(string channelName, bool isPublic)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                throw new InvalidOperationException("Not connected to server. Wait for connection before creating channels.");
            }
            
            string type = isPublic ? "public" : "private";
            string message = $"#CREATE:{channelName}:{type}";
            SendMessage(message);
            LogDebug($"Requested to create {type} channel: {channelName}", LogLevel.Basic);
        }
        
        // Send a message to a specific channel
        public void SendChannelMessage(string channelId, string message)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                throw new InvalidOperationException("Not connected to server. Wait for connection before sending channel messages.");
            }
            
            string channelMessage = $"#CHANNEL:{channelId}:{message}";
            SendMessage(channelMessage);
            LogDebug($"Sent message to channel {channelId}", LogLevel.Basic);
        }
        
        // Get list of channels
        public void RequestChannelList()
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                throw new InvalidOperationException("Not connected to server. Wait for connection before requesting channel list.");
            }
            
            string message = "#CHANNELS";
            SendMessage(message);
            LogDebug("Requested channel list from server", LogLevel.Basic);
        }
        
        // Internal method to handle channel state changes
        private void HandleChannelStateChange(string channelId, bool joined)
        {
            if (joined)
            {
                if (!_channels.Contains(channelId))
                {
                    _channels.Add(channelId);
                    ChannelJoined?.Invoke(this, channelId);
                }
            }
            else
            {
                _channels.Remove(channelId);
                ChannelLeft?.Invoke(this, channelId);
            }
        }
        
        // Get list of joined channels
        public IReadOnlyList<string> GetJoinedChannels()
        {
            return _channels.AsReadOnly();
        }
    }
}



