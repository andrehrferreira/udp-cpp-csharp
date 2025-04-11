using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Timers;
using System.Threading;
using System.Collections.Concurrent;

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
        private bool _waitingForConnectResponse;
        private CancellationTokenSource _listenerCts;
        private Thread _listenerThread; // Thread dedicada para receber mensagens
        private bool _isListenerRunning;
        
        // Client thread pool para processamento de pacotes e envio
        private const int RECEIVE_THREADS = 3; // 3 threads para processamento
        private const int SEND_THREADS = 2;    // 2 threads para envio
        private BlockingCollection<ReceivedPacket> _receiveQueue; // Fila de pacotes recebidos
        private BlockingCollection<OutgoingPacket> _sendQueue;    // Fila de pacotes para enviar
        private Thread[] _receiveThreads;  // Pool de threads para processamento
        private Thread[] _sendThreads;     // Pool de threads para envio
        private bool _threadPoolRunning;
        
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
            _waitingForConnectResponse = false;
            _listenerCts = new CancellationTokenSource();
            
            // Configurar o socket para não bloquear
            _client.Client.Blocking = false;
            
            // Inicializar filas e threads do pool
            _threadPoolRunning = true;
            _receiveQueue = new BlockingCollection<ReceivedPacket>(new ConcurrentQueue<ReceivedPacket>(), 1000);
            _sendQueue = new BlockingCollection<OutgoingPacket>(new ConcurrentQueue<OutgoingPacket>(), 1000);
            _receiveThreads = new Thread[RECEIVE_THREADS];
            _sendThreads = new Thread[SEND_THREADS];
            
            // Iniciar threads de processamento
            for (int i = 0; i < RECEIVE_THREADS; i++)
            {
                _receiveThreads[i] = new Thread(ProcessPacketWorker)
                {
                    Name = $"UDP-Client-Process-{i}",
                    IsBackground = true
                };
                _receiveThreads[i].Start();
            }
            
            // Iniciar threads de envio
            for (int i = 0; i < SEND_THREADS; i++)
            {
                _sendThreads[i] = new Thread(SendPacketWorker)
                {
                    Name = $"UDP-Client-Send-{i}",
                    IsBackground = true
                };
                _sendThreads[i].Start();
            }
            
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
            
            // Iniciar thread de escuta - usar um método de wrapper que não retorna async Task
            _listenerThread = new Thread(ListenerThreadWrapper)
            {
                Name = "UDP-Client-Listener",
                IsBackground = true
            };
            _isListenerRunning = true;
            _listenerThread.Start();
            
            // Iniciar processo de conexão
            Connect();
        }
        
        // Método wrapper para executar a tarefa de escuta sem gerar o aviso CS4014
        private void ListenerThreadWrapper()
        {
            // Executa o método assíncrono e aguarda a conclusão de forma segura
            ListenForMessagesAsync(_listenerCts.Token).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        
        // Estrutura para pacotes recebidos na fila de processamento
        private struct ReceivedPacket
        {
            public byte[] Data { get; }
            
            public ReceivedPacket(byte[] data)
            {
                Data = data;
            }
        }
        
        // Estrutura para pacotes na fila de envio
        private struct OutgoingPacket
        {
            public byte[] Data { get; }
            public IPEndPoint Endpoint { get; }
            
            public OutgoingPacket(byte[] data, IPEndPoint endpoint)
            {
                Data = data;
                Endpoint = endpoint;
            }
        }
        
        public void Connect()
        {
            LogDebug("Starting connection to server...", LogLevel.Basic);
            
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
            LogDebug("Starting dedicated listener thread", LogLevel.Basic);
            
            // Buffer para recebimento de dados
            byte[] buffer = new byte[8192]; // 8KB buffer para mensagens
            
            while (!cancellationToken.IsCancellationRequested && _isListenerRunning)
            {
                try
                {
                    // Verificar se há dados disponíveis para leitura
                    if (_client.Available > 0)
                    {
                        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = _client.Receive(ref remoteEP);
                        
                        // Fazer uma cópia dos dados e colocar na fila para processamento
                        byte[] dataCopy = new byte[data.Length];
                        Buffer.BlockCopy(data, 0, dataCopy, 0, data.Length);
                        
                        // Enfileirar para processamento através do thread pool
                        if (!_receiveQueue.TryAdd(new ReceivedPacket(dataCopy), 1000))
                        {
                            LogDebug("Fila de recebimento cheia, pacote descartado", LogLevel.Warning);
                        }
                    }
                    else
                    {
                        // Pausa pequena para não sobrecarregar a CPU
                        await Task.Delay(1, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Token de cancelamento foi acionado, sair do loop
                    break;
                }
                catch (SocketException ex)
                {
                    // Apenas registrar e continuar para erros de socket não críticos
                    if (ex.SocketErrorCode != SocketError.WouldBlock && 
                        ex.SocketErrorCode != SocketError.TimedOut)
                    {
                        LogDebug($"Erro de socket na thread de escuta: {ex.Message} (Code: {ex.SocketErrorCode})", LogLevel.Critical);
                    }
                    
                    // Pausa maior em caso de erro
                    await Task.Delay(5, cancellationToken);
                }
                catch (Exception ex)
                {
                    LogDebug($"Erro na thread de escuta: {ex.Message}", LogLevel.Critical);
                    
                    // Pausa maior em caso de erro
                    await Task.Delay(10, cancellationToken);
                }
            }
            
            LogDebug("Listener thread terminated", LogLevel.Basic);
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
                        
                    case EPacketType.ChannelJoinConfirm:
                        try
                        {
                            // Extract channel ID from the packet data
                            string channelId = packet.GetDataAsString();
                            
                            // Add to local tracking
                            if (!_channels.Contains(channelId))
                            {
                                _channels.Add(channelId);
                                ChannelJoined?.Invoke(this, channelId);
                                LogDebug($"Joined channel: {channelId} (binary protocol)", LogLevel.Basic);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Error processing channel join confirmation: {ex.Message}", LogLevel.Critical);
                        }
                        break;
                        
                    case EPacketType.ChannelBroadcast:
                        try
                        {
                            //LogDebug($"Received ChannelBroadcast packet, attempting to parse (SessionId={packet.SessionId})", LogLevel.Basic);
                            
                            // Parse the binary format: channelId length (1 byte) + channelId + message
                            byte[] packetData = packet.Data;
                            //LogDebug($"ChannelBroadcast data length: {packetData.Length} bytes", LogLevel.Basic);
                            
                            if (packetData.Length < 2) // Need at least 1 byte for length and 1 byte for channel ID
                            {
                                break;
                            }
                                
                            byte channelIdLength = packetData[0];
                            //LogDebug($"Channel ID length in packet: {channelIdLength}", LogLevel.Basic);
                            
                            if (packetData.Length < 1 + channelIdLength) // Ensure we have enough data
                            {
                                break;
                            }
                                
                            // Extract channel ID
                            byte[] channelIdBytes = new byte[channelIdLength];
                            Buffer.BlockCopy(packetData, 1, channelIdBytes, 0, channelIdLength);
                            string channelId = Encoding.UTF8.GetString(channelIdBytes);
                            
                            // Extract message
                            int messageLength = packetData.Length - 1 - channelIdLength;
                            byte[] messageBytes = new byte[messageLength];
                            Buffer.BlockCopy(packetData, 1 + channelIdLength, messageBytes, 0, messageLength);
                            string channelMessage = Encoding.UTF8.GetString(messageBytes);
                            
                            //LogDebug($"Successfully parsed ChannelBroadcast: From={packet.SessionId}, ChannelId={channelId}, MessageLength={messageLength}", LogLevel.Basic);
                            
                            // Log packet details
                            //LogDebug($"Received ChannelBroadcast packet: From={packet.SessionId}, ChannelId={channelId}, MessageBytes={messageBytes.Length}", LogLevel.Basic);
                            
                            // Notify using the channel message event
                            ChannelMessageReceived?.Invoke(this, $"Channel {channelId} [{packet.SessionId}]: {channelMessage}");
                            //LogDebug($"Received channel message from {channelId} (sender: {packet.SessionId}): {channelMessage}", LogLevel.Basic);
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Error processing channel broadcast: {ex.Message}", LogLevel.Critical);
                        }
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
            

            if (string.IsNullOrEmpty(packet.SessionId))
            {
                StartReconnect();
                return;
            }
            
            // Armazenar o SessionId recebido
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
                
                //LogDebug($"Connected to server. Session ID: {_sessionId}", LogLevel.Basic);
                Connected?.Invoke(this, _sessionId);
            }
        }
        
        // Method to send data with a specific type
        public void SendPacket(FastPacket packet)
        {
            // CONNECT é o único pacote que pode ser enviado sem uma conexão estabelecida
            if (packet.Type != EPacketType.Connect && !_connected)
            {
                // Silenciosamente descartar pacotes em vez de lançar exceção
                // quando não estamos conectados (evita inundar o log)
                if (packet.Type != EPacketType.Ping)
                {
                    LogDebug("Dropping packet while disconnected: " + packet.Type, LogLevel.Verbose);
                }
                return; // Simplesmente retornar sem lançar exceção
            }
            
            try
            {
                byte[] packetData = packet.Serialize();
                
                // Enfileirar para envio através do thread pool
                if (_threadPoolRunning)
                {
                    if (!_sendQueue.TryAdd(new OutgoingPacket(packetData, _serverEndPoint), 1000))
                    {
                        LogDebug("Send queue full, packet dropped", LogLevel.Warning);
                    }
                }
                else
                {
                    // Fallback para envio direto se o pool não estiver rodando
                    _client.Send(packetData, packetData.Length, _serverEndPoint);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error preparing packet: {ex.Message}", LogLevel.Critical);
                if (_connected)
                {
                    _connected = false;
                    StartReconnect();
                }
            }
        }
        
        // Convenience method to send text message
        public void SendMessage(string message)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                // Silenciosamente ignorar mensagens quando desconectado
                LogDebug($"Dropping message while disconnected: {message.Substring(0, Math.Min(message.Length, 20))}...", LogLevel.Verbose);
                return;
            }
            
            SendPacket(FastPacket.CreateMessage(message, _sessionId));
        }
        
        // Method to send binary data
        public void SendBinaryData(byte[] data)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                // Silenciosamente ignorar dados quando desconectado
                LogDebug($"Dropping binary data while disconnected: {data.Length} bytes", LogLevel.Verbose);
                return;
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
                // _serverShutdown = false;
                
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
            
            // Encerrar todos os timers
            _pingTimer.Stop();
            _reconnectTimer.Stop();
            _connectTimeoutTimer.Stop();
            
            // Encerrar thread pools
            _threadPoolRunning = false;
            
            // Sinalizar para completar as filas
            try
            {
                _receiveQueue.CompleteAdding();
                _sendQueue.CompleteAdding();
            }
            catch
            {
                // Ignorar erros ao finalizar filas
            }
            
            // Sinalizar para a thread de escuta parar
            _isListenerRunning = false;
            _listenerCts.Cancel();
            
            // Aguardar a thread de escuta terminar (com timeout para evitar bloqueio)
            try 
            {
                if (_listenerThread.IsAlive)
                {
                    _listenerThread.Join(1000); // Esperar até 1 segundo
                }
            }
            catch
            {
                // Ignorar erros ao encerrar a thread
            }
            
            // Aguardar as threads de processamento terminarem
            for (int i = 0; i < _receiveThreads.Length; i++)
            {
                try
                {
                    if (_receiveThreads[i].IsAlive)
                    {
                        _receiveThreads[i].Join(500); // Timeout de 500ms por thread
                    }
                }
                catch
                {
                    // Ignorar erros ao encerrar as threads
                }
            }
            
            // Aguardar as threads de envio terminarem
            for (int i = 0; i < _sendThreads.Length; i++)
            {
                try
                {
                    if (_sendThreads[i].IsAlive)
                    {
                        _sendThreads[i].Join(500); // Timeout de 500ms por thread
                    }
                }
                catch
                {
                    // Ignorar erros ao encerrar as threads
                }
            }
            
            // Dispor recursos
            _receiveQueue.Dispose();
            _sendQueue.Dispose();
            
            // Fechar o cliente UDP
            _client.Close();
            
            LogDebug("Client disposed", LogLevel.Basic);
        }
        
        // Channel-related methods
        
        // Join a channel
        public void JoinChannel(string channelId)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                LogDebug($"Cannot join channel '{channelId}' while disconnected", LogLevel.Verbose);
                return;
            }
            
            string message = $"#JOIN:{channelId}";
            SendMessage(message);
            LogDebug($"Requested to join channel: {channelId}", LogLevel.Basic);
        }
        
        // Join a channel using binary protocol (more efficient)
        public void JoinChannelBinary(string channelId)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                LogDebug($"Cannot join channel '{channelId}' using binary protocol while disconnected", LogLevel.Verbose);
                return;
            }
            
            var packet = new FastPacket(EPacketType.ChannelJoin, channelId);
            SendPacket(packet);
        }
        
        // Leave a channel
        public void LeaveChannel(string channelId)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                LogDebug($"Cannot leave channel '{channelId}' while disconnected", LogLevel.Verbose);
                return;
            }
            
            string message = $"#LEAVE:{channelId}";
            SendMessage(message);
            LogDebug($"Requested to leave channel: {channelId}", LogLevel.Basic);
            
            _channels.Remove(channelId);
            ChannelLeft?.Invoke(this, channelId);
        }
        
        // Create a new channel
        public void CreateChannel(string channelName, bool isPublic)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                LogDebug($"Cannot create channel '{channelName}' while disconnected", LogLevel.Verbose);
                return;
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
                LogDebug($"Cannot send message to channel '{channelId}' while disconnected", LogLevel.Verbose);
                return;
            }
            
            string channelMessage = $"#CHANNEL:{channelId}:{message}";
            SendMessage(channelMessage);
            LogDebug($"Sent message to channel {channelId}", LogLevel.Basic);
        }
        
        // Send a message to a specific channel using binary protocol (more efficient)
        public void SendChannelBroadcastBinary(string channelId, string message)
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                LogDebug($"Cannot send binary message to channel '{channelId}' while disconnected", LogLevel.Verbose);
                return;
            }
            
            // Debug log
            LogDebug($"Preparing binary channel broadcast: Channel={channelId}, Message={message}", LogLevel.Basic);
            
            try {
                // FIXED IMPLEMENTATION: Properly format the binary packet
                
                // Format: channelId length (1 byte) + channelId + message
                byte[] channelIdBytes = Encoding.UTF8.GetBytes(channelId);
                
                // Safety check - ensure channel ID is not too long
                if (channelIdBytes.Length > 255)
                {
                    throw new ArgumentException("Channel ID is too long (max 255 bytes)");
                }
                
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                
                // Create properly formatted data: [channelIdLength (1 byte)][channelId (n bytes)][message (remaining bytes)]
                byte[] packetData = new byte[1 + channelIdBytes.Length + messageBytes.Length];
                
                // First byte is the channel ID length
                packetData[0] = (byte)channelIdBytes.Length;
                
                // Debug - verify the length byte is being set correctly
                LogDebug($"Setting channel ID length byte to: {packetData[0]} for channel ID '{channelId}'", LogLevel.Basic);
                
                // Copy channel ID bytes after the length byte
                Buffer.BlockCopy(channelIdBytes, 0, packetData, 1, channelIdBytes.Length);
                
                // Copy message bytes after the channel ID
                Buffer.BlockCopy(messageBytes, 0, packetData, 1 + channelIdBytes.Length, messageBytes.Length);
                
                // Debug - print the packet structure
                LogDebug($"Binary packet structure: [Length={packetData[0]}][ChannelID='{channelId}' ({channelIdBytes.Length} bytes)][Message='{message}' ({messageBytes.Length} bytes)]", LogLevel.Basic);
                
                // Create packet with the binary data - NÃO INCLUI SessionId no pacote
                var packet = new FastPacket(EPacketType.ChannelBroadcast, packetData);
                
                // Send the packet
                SendPacket(packet);
                
                LogDebug($"Successfully sent binary broadcast to channel {channelId}", LogLevel.Basic);
            }
            catch (Exception ex) {
                LogDebug($"Error creating channel broadcast packet: {ex.Message}", LogLevel.Critical);
            }
        }
        
        // Get list of channels
        public void RequestChannelList()
        {
            if (!_connected || string.IsNullOrEmpty(_sessionId))
            {
                LogDebug("Cannot request channel list while disconnected", LogLevel.Verbose);
                return;
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
        
        // Worker thread para processar pacotes recebidos
        private void ProcessPacketWorker()
        {
            LogDebug($"Thread de processamento {Thread.CurrentThread.Name} iniciada", LogLevel.Basic);
            
            while (_threadPoolRunning)
            {
                try
                {
                    // Tentar obter um pacote da fila com timeout para poder verificar _threadPoolRunning
                    if (_receiveQueue.TryTake(out ReceivedPacket packet, 100))
                    {
                        try
                        {
                            // Processar o pacote
                            ProcessReceivedPacket(packet.Data);
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Erro ao processar pacote: {ex.Message}", LogLevel.Critical);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignorar cancelamento normal
                    break;
                }
                catch (Exception ex)
                {
                    LogDebug($"Erro na thread de processamento: {ex.Message}", LogLevel.Critical);
                    Thread.Sleep(10); // Pequena pausa em caso de erro
                }
            }
            
            LogDebug($"Thread de processamento {Thread.CurrentThread.Name} encerrada", LogLevel.Basic);
        }
        
        // Worker thread para enviar pacotes
        private void SendPacketWorker()
        {
            LogDebug($"Thread de envio {Thread.CurrentThread.Name} iniciada", LogLevel.Basic);
            
            while (_threadPoolRunning)
            {
                try
                {
                    // Tentar obter um pacote da fila com timeout para poder verificar _threadPoolRunning
                    if (_sendQueue.TryTake(out OutgoingPacket packet, 100))
                    {
                        try
                        {
                            // Verificar novamente a conexão antes de enviar (pode ter mudado desde o enfileiramento)
                            // Packets do tipo Connect devem ser enviados mesmo sem conexão
                            if (!_connected && packet.Data.Length > 0 && packet.Data[0] != (byte)EPacketType.Connect)
                            {
                                // Silenciosamente descartar pacotes (exceto Connect) quando não estamos conectados
                                continue;
                            }
                            
                            // Enviar o pacote
                            _client.Send(packet.Data, packet.Data.Length, packet.Endpoint);
                        }
                        catch (SocketException sockEx)
                        {
                            // Erros de rede são esperados, apenas log e não marcar como erro crítico
                            LogDebug($"Network error while sending: {sockEx.Message}", LogLevel.Warning);
                            
                            if (_connected && sockEx.SocketErrorCode != SocketError.WouldBlock)
                            {
                                _connected = false;
                                StartReconnect();
                            }
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Error sending packet: {ex.Message}", LogLevel.Critical);
                            if (_connected)
                            {
                                _connected = false;
                                StartReconnect();
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignorar cancelamento normal
                    break;
                }
                catch (Exception ex)
                {
                    LogDebug($"Erro na thread de envio: {ex.Message}", LogLevel.Critical);
                    Thread.Sleep(10); // Pequena pausa em caso de erro
                }
            }
            
            LogDebug($"Thread de envio {Thread.CurrentThread.Name} encerrada", LogLevel.Basic);
        }
    }
}



