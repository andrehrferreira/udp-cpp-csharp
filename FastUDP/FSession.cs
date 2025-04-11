using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FastUDP {
    // Class to represent an active UDP session
    public class FastUdpSession
    {
        // Propriedades básicas da sessão
        public string Id { get; private set; }
        public IPEndPoint RemoteEndPoint { get; private set; }
        
        // Referência ao socket para envio direto
        private readonly Socket _socket;
        
        // Estado da sessão
        private bool _isAuthenticated = false;
        private bool _isDisconnected = false;
        private DateTime _lastActivity = DateTime.Now;
        private DateTime _lastPacketTime = DateTime.Now;
        
        // Para controle de vazão
        private readonly FastUdpConnection _connection;
        
        // Referência ao thread pool (adicionado do servidor)
        private FastThreadPool? _threadPool;
        
        // Timeout após 5 minutos de inatividade
        public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(5);
        
        // Eventos para notificar sobre mudanças de estado
        public event EventHandler<string>? MessageSent;
        public event EventHandler<string>? Disconnected;
        public event EventHandler<bool>? AuthenticationChanged;
        
        // Propriedades
        public bool IsConnected => !_isDisconnected;
        public bool IsAuthenticated => _isAuthenticated;
        public bool IsTimedOut => DateTime.Now - _lastActivity > SessionTimeout;
        public DateTime LastActivity => _lastActivity;
        public DateTime LastPacketTime => _lastPacketTime;
        public FastUdpConnection Connection => _connection;

        public FastUdpSession(string id, IPEndPoint remoteEndPoint, Socket socket)
        {
            Id = id;
            RemoteEndPoint = remoteEndPoint;
            _lastActivity = DateTime.Now;
            _lastPacketTime = DateTime.Now;
            _isAuthenticated = false;
            _isDisconnected = false;
            _connection = new FastUdpConnection(remoteEndPoint);
            _socket = socket;
        }
        
        // Configurar o thread pool
        public void SetThreadPool(FastThreadPool threadPool)
        {
            _threadPool = threadPool;
        }
        
        public void UpdateActivity()
        {
            _lastActivity = DateTime.Now;
            _lastPacketTime = DateTime.Now;
        }
        
        // Método para enviar dados usando o thread pool se disponível
        public void Send(byte[] data)
        {
            if (_isDisconnected)
                return;
                
            try
            {
                // Atualizar estatísticas de envio
                _connection.AddSentBytes(data.Length);
                
                // Usar thread pool se disponível
                if (_threadPool != null)
                {
                    _threadPool.EnqueuePacketForSending(_socket, RemoteEndPoint, data);
                }
                else
                {
                    // Fallback para envio síncrono se thread pool não estiver configurado
                    _socket.SendTo(data, RemoteEndPoint);
                }
                
                UpdateActivity();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending to session {Id}: {ex.Message}");
                // Não marcar como desconectado aqui, deixar para o servidor decidir
            }
        }
        
        // Método para enviar pacote formatado
        public void SendPacket(FastPacket packet)
        {
            if (_isDisconnected)
                return;
                
            byte[] data = packet.Serialize();
            Send(data);
            MessageSent?.Invoke(this, $"Packet {packet.Type} sent to {Id} ({data.Length} bytes)");
        }
        
        // Conveniência para enviar mensagem de texto
        public void SendMessage(string message)
        {
            if (_isDisconnected)
                return;
                
            var packet = new FastPacket(EPacketType.Message, message);
            SendPacket(packet);
        }
        
        public void Disconnect(string reason = "Session closed by server")
        {
            if (!IsConnected)
                return;
                
            try
            {
                var packet = new FastPacket(EPacketType.Disconnect, reason);
                SendPacket(packet);
                
                _isDisconnected = true;
                
                Disconnected?.Invoke(this, reason);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disconnecting session {Id}: {ex.Message}");
                _isDisconnected = true;
            }
        }
        
        public void Authenticate()
        {
            _isAuthenticated = true;
            UpdateActivity();
            AuthenticationChanged?.Invoke(this, true);
        }
        
        public void SetAuthenticationState(bool authenticated)
        {
            if (_isAuthenticated != authenticated)
            {
                _isAuthenticated = authenticated;
                UpdateActivity();
                AuthenticationChanged?.Invoke(this, authenticated);
            }
        }
    }
}

