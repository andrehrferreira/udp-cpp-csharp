using System.Text;

namespace FastUDP
{
    public class FastPacket
    {
        public EPacketType Type { get; private set; }
        public byte[] Data { get; private set; }
        public string SessionId { get; set; }
        
        // Construtor para criar pacote a partir de tipo e dados binários
        public FastPacket(EPacketType type, byte[]? data = null, string sessionId = "")
        {
            Type = type;
            Data = data ?? Array.Empty<byte>();
            SessionId = sessionId;
        }
        
        // Construtor para criar pacote a partir de tipo e mensagem texto
        public FastPacket(EPacketType type, string message, string sessionId = "")
            : this(type, string.IsNullOrEmpty(message) ? new byte[0] : Encoding.UTF8.GetBytes(message), sessionId)
        {
        }
        
        // Construtor para desserializar pacote a partir de dados brutos
        public FastPacket(byte[] rawData)
        {
            if (rawData == null || rawData.Length < 1)
                throw new ArgumentException("Dados de pacote inválidos ou vazios");
                
            Type = (EPacketType)rawData[0];
            SessionId = string.Empty;
            Data = new byte[0];
            
            // Para pacotes simples como Ping e Pong, não há mais dados
            if (Type == EPacketType.Ping || Type == EPacketType.Pong)
            {
                return;
            }
            
            // CASO ESPECIAL: ConnectResponse precisa extrair o SessionId
            if (Type == EPacketType.ConnectResponse && rawData.Length > 2)
            {
                int sessionIdLength = rawData[1];
                
                if (sessionIdLength > 0 && rawData.Length >= 2 + sessionIdLength)
                {
                    // Extrair o SessionId
                    SessionId = Encoding.ASCII.GetString(rawData, 2, sessionIdLength);
                    
                    // Extrair os dados se houver
                    int dataOffset = 2 + sessionIdLength;
                    if (rawData.Length > dataOffset)
                    {
                        int dataLength = rawData.Length - dataOffset;
                        Data = new byte[dataLength];
                        Buffer.BlockCopy(rawData, dataOffset, Data, 0, dataLength);
                    }
                    
                    return;
                }
            }
            
            // PROTOCOLO SIMPLIFICADO para outros tipos: Não temos mais SessionId nos pacotes
            // Dados começam imediatamente após o tipo
            
            // Verificar se há dados além do tipo
            if (rawData.Length > 1)
            {
                // Extrair os dados após o tipo de pacote
                int dataLength = rawData.Length - 1;
                Data = new byte[dataLength];
                Buffer.BlockCopy(rawData, 1, Data, 0, dataLength);
                
                // O SessionId é determinado pelo servidor baseado no endereço do cliente
                // e é atribuído externamente pelo código que processa o pacote
            }
        }
        
        // Serializa o pacote em um array de bytes para transmissão
        public byte[] Serialize()
        {
            // Para pacotes simples como Ping e Pong, apenas o tipo é enviado
            if (Type == EPacketType.Ping || Type == EPacketType.Pong)
            {
                return new byte[] { (byte)Type };
            }
            
            // CASO ESPECIAL: ConnectResponse PRECISA incluir o SessionId para o cliente estabelecer conexão
            if (Type == EPacketType.ConnectResponse)
            {
                // Converter SessionId em bytes
                byte[] sessionIdBytes = string.IsNullOrEmpty(SessionId) 
                    ? new byte[0] 
                    : Encoding.ASCII.GetBytes(SessionId);
                
                // Limitar comprimento do ID de sessão a 255 caracteres
                if (sessionIdBytes.Length > 255)
                {
                    byte[] truncated = new byte[255];
                    Buffer.BlockCopy(sessionIdBytes, 0, truncated, 0, 255);
                    sessionIdBytes = truncated;
                }
                
                // Calcular tamanho total: tipo + tamanho sessionId + sessionId + dados
                int totalSize = 1 + 1 + sessionIdBytes.Length + Data.Length;
                
                // Criar array para o pacote serializado
                byte[] packet = new byte[totalSize];
                
                // Adicionar o tipo
                packet[0] = (byte)Type;
                
                // Adicionar o tamanho do SessionId
                packet[1] = (byte)sessionIdBytes.Length;
                
                // Adicionar o SessionId
                Buffer.BlockCopy(sessionIdBytes, 0, packet, 2, sessionIdBytes.Length);
                
                // Adicionar os dados após o SessionId
                if (Data.Length > 0)
                {
                    Buffer.BlockCopy(Data, 0, packet, 2 + sessionIdBytes.Length, Data.Length);
                }
                
                return packet;
            }
            
            // PROTOCOLO SIMPLIFICADO para outros tipos de pacotes: Não incluir SessionId
            // O servidor já conhece o remetente pelo IP/porta
            
            // Calcular tamanho total do pacote: tipo + dados
            int totalSize = 1 + Data.Length;
            
            // Criar array para o pacote serializado
            byte[] packet = new byte[totalSize];
            
            // Adicionar o tipo de pacote
            packet[0] = (byte)Type;
            
            // Adicionar dados diretamente após o tipo
            if (Data.Length > 0)
            {
                Buffer.BlockCopy(Data, 0, packet, 1, Data.Length);
            }
            
            return packet;
        }
        
        // Obter os dados como string UTF-8
        public string GetDataAsString()
        {
            if (Data == null || Data.Length == 0)
                return string.Empty;
                
            try
            {
                return Encoding.UTF8.GetString(Data);
            }
            catch
            {
                return string.Empty;
            }
        }
        
        // Funções de conveniência para criar pacotes comuns
        
        public static FastPacket CreatePing()
        {
            return new FastPacket(EPacketType.Ping);
        }
        
        public static FastPacket CreatePong()
        {
            return new FastPacket(EPacketType.Pong);
        }
        
        public static FastPacket CreateConnect()
        {
            return new FastPacket(EPacketType.Connect);
        }
        
        public static FastPacket CreateConnectResponse(string sessionId, string message = "Conexão aceita")
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("sessionId não pode ser nulo ou vazio para um pacote ConnectResponse");
            }
            
            // Garantir que o SessionId seja definido corretamente para o pacote ConnectResponse
            var packet = new FastPacket(EPacketType.ConnectResponse, message, sessionId);
            
            // Verificar se o SessionId foi definido corretamente
            if (string.IsNullOrEmpty(packet.SessionId))
            {
                throw new InvalidOperationException("Falha ao criar pacote ConnectResponse: SessionId vazio");
            }
            
            return packet;
        }
        
        public static FastPacket CreateMessage(string message, string sessionId = "")
        {
            return new FastPacket(EPacketType.Message, message, sessionId);
        }
        
        public static FastPacket CreateReconnect()
        {
            return new FastPacket(EPacketType.Reconnect);
        }
        
        public static FastPacket CreateDisconnect(string reason = "")
        {
            return new FastPacket(EPacketType.Disconnect, reason);
        }
        
        public static FastPacket CreateShutdown()
        {
            return new FastPacket(EPacketType.Shutdown);
        }
    }
} 