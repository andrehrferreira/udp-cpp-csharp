using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FastUDP {
    public class FastUdpSession
    {
        // Propriedades básicas da sessão
        public string Id { get; private set; }
        public DateTime LastPacketTime { get; private set; }
        public IPEndPoint RemoteEndPoint { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsAuthenticated { get; private set; }
        public FastUdpConnection Connection { get; private set; }
        
        // Referência ao socket para envio direto
        private Socket _socket;
        
        // Timeout após 10s de inatividade
        public bool IsTimedOut => (DateTime.Now - LastPacketTime).TotalSeconds > 30;
        
        // Eventos para notificar sobre mudanças de estado
        public event EventHandler<string>? Disconnected;
        public event EventHandler<string>? MessageSent;
        public event EventHandler<bool>? AuthenticationChanged;
        
        public FastUdpSession(string id, IPEndPoint remoteEndPoint, Socket socket)
        {
            Id = id;
            RemoteEndPoint = remoteEndPoint;
            LastPacketTime = DateTime.Now;
            IsConnected = true;
            IsAuthenticated = false;
            Connection = new FastUdpConnection(remoteEndPoint);
            _socket = socket;
        }
        
        public void UpdateActivity()
        {
            LastPacketTime = DateTime.Now;
        }
        
        public bool Send(byte[] data)
        {
            if (!IsConnected)
            {
                return false;
            }
            
            try
            {
                _socket.SendTo(data, RemoteEndPoint);
                MessageSent?.Invoke(this, $"Data sent to {Id} ({data.Length} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending data to {Id}: {ex.Message}");
                return false;
            }
        }
        
        // Método para enviar pacote formatado
        public bool SendPacket(FastPacket packet)
        {
            return Send(packet.Serialize());
        }
        
        // Conveniência para enviar mensagem de texto
        public bool SendMessage(string message)
        {
            try
            {
                var packet = new FastPacket(EPacketType.Message, message, Id);
                return SendPacket(packet);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating message packet: {ex.Message}");
                return false;
            }
        }
        
        // Método para desconectar este cliente
        public void Disconnect(string reason = "Session closed by server")
        {
            if (!IsConnected)
                return;
                
            try
            {
                // Enviar pacote de desconexão
                var packet = new FastPacket(EPacketType.Disconnect, reason);
                SendPacket(packet);
                
                // Marcar como desconectado
                IsConnected = false;
                
                // Disparar evento
                Disconnected?.Invoke(this, reason);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disconnecting session {Id}: {ex.Message}");
                // Marcar como desconectado mesmo em caso de erro
                IsConnected = false;
            }
        }
        
        // Método para autenticar a sessão (pode ser usado para sessões que requerem login)
        public void Authenticate()
        {
            IsAuthenticated = true;
            UpdateActivity();
            AuthenticationChanged?.Invoke(this, true);
        }
        
        // Método para definir o estado de autenticação diretamente
        public void SetAuthenticationState(bool authenticated)
        {
            if (IsAuthenticated != authenticated)
            {
                IsAuthenticated = authenticated;
                UpdateActivity();
                AuthenticationChanged?.Invoke(this, authenticated);
            }
        }
    }
}

