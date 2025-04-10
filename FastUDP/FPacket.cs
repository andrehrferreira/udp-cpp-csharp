using System.Text;

namespace FastUDP
{
    public class FastPacket
    {
        public EPacketType Type { get; private set; }
        public byte[] Data { get; private set; }
        public string SessionId { get; private set; }
        
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
            
            // Verificar se há dados além do tipo
            if (rawData.Length > 1)
            {
                // Verificar se há ID de sessão
                int sessionIdLength = 0;
                
                if (rawData.Length > 1)
                {
                    sessionIdLength = rawData[1];
                }
                
                if (sessionIdLength > 0 && rawData.Length > 2 + sessionIdLength)
                {
                    // Extrair ID de sessão
                    SessionId = Encoding.ASCII.GetString(rawData, 2, sessionIdLength);
                    
                    // Extrair dados após o ID de sessão
                    int dataLength = rawData.Length - 2 - sessionIdLength;
                    if (dataLength > 0)
                    {
                        Data = new byte[dataLength];
                        Buffer.BlockCopy(rawData, 2 + sessionIdLength, Data, 0, dataLength);
                    }
                }
                else
                {
                    // Sem ID de sessão, apenas dados
                    int dataLength = rawData.Length - 1;
                    Data = new byte[dataLength];
                    Buffer.BlockCopy(rawData, 1, Data, 0, dataLength);
                }
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
            
            // Converter SessionId em bytes se existir
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
            
            // Calcular tamanho total do pacote
            int totalSize = 1; // Tipo de pacote (1 byte)
            
            if (sessionIdBytes.Length > 0)
            {
                totalSize += 1 + sessionIdBytes.Length; // Tamanho do ID (1 byte) + ID
            }
            
            totalSize += Data.Length; // Dados
            
            // Criar array para o pacote serializado
            byte[] packet = new byte[totalSize];
            
            // Adicionar o tipo de pacote
            packet[0] = (byte)Type;
            
            int currentIndex = 1;
            
            // Adicionar ID de sessão se existir
            if (sessionIdBytes.Length > 0)
            {
                packet[currentIndex++] = (byte)sessionIdBytes.Length;
                Buffer.BlockCopy(sessionIdBytes, 0, packet, currentIndex, sessionIdBytes.Length);
                currentIndex += sessionIdBytes.Length;
            }
            
            // Adicionar dados se existirem
            if (Data.Length > 0)
            {
                Buffer.BlockCopy(Data, 0, packet, currentIndex, Data.Length);
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
            return new FastPacket(EPacketType.ConnectResponse, message, sessionId);
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