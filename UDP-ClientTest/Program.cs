using FastUDP;
using System.Text;
using System.Net;
using System.Net.Sockets;

class Program
{
    private static bool _running = true;
    private static string _currentInput = "";
    private static FastUdpClient _client;
    private static bool _isConnected = false;
    private static bool _waitingForConnection = true;
    private static string _currentChannel = ""; // Track current active channel
    private static UdpClient _directClient; // Client for direct UDP sending
    
    static void Main(string[] args)
    {
        Console.Clear();
        Console.WriteLine("Cliente UDP iniciando...");
        Console.WriteLine("Aguardando conexão com o servidor...");
        
        // Initialize direct UDP client for emergency direct messages
        _directClient = new UdpClient();
        
        // Criando cliente para enviar mensagens para o servidor na porta 2593
        _client = new FastUdpClient("127.0.0.1", 2593);
        _client.DebugMode = true;
        _client.LoggingLevel = FastUDP.FastUdpClient.LogLevel.Basic; // Reduzir logs para melhorar performance
        
        // Registrar handlers de eventos
        _client.Connected += (sender, sessionId) => {
            _isConnected = true;
            _waitingForConnection = false;
            PrintStatus($"Conectado ao servidor com ID de sessão: {sessionId}");
            PrintStatusPrompt("Conexão estabelecida, você já pode enviar mensagens.");
            PrintPrompt();
        };
        
        _client.Disconnected += (sender, reason) => {
            _isConnected = false;
            _waitingForConnection = true;
            PrintStatus($"Desconectado do servidor: {reason}");
            PrintStatusPrompt("Aguardando reconexão... (não é possível enviar mensagens)");
        };
        
        _client.Reconnecting += (sender, message) => {
            _isConnected = false;
            _waitingForConnection = true;
            PrintStatus($"Reconexão: {message}");
            PrintStatusPrompt("Tentando reconectar... (não é possível enviar mensagens)");
        };
        
        _client.MessageReceived += (sender, message) => {
            PrintStatus($"Resposta do servidor: {message}");
        };
        
        // Channel-specific events
        _client.ChannelJoined += (sender, channelId) => {
            PrintStatus($"Entrou no canal: {channelId}");
            _currentChannel = channelId; // Set as current channel
            PrintStatusPrompt($"Canal atual: {channelId}");
        };
        
        _client.ChannelLeft += (sender, channelId) => {
            PrintStatus($"Saiu do canal: {channelId}");
            if (_currentChannel == channelId)
            {
                _currentChannel = ""; // Clear current channel if it was this one
                PrintStatusPrompt("Sem canal ativo.");
            }
        };
        
        _client.ChannelMessageReceived += (sender, message) => {
            PrintStatus($"Mensagem do canal: {message}");
        };
        
        // Iniciar conexão explicitamente
        _client.Connect();
        
        // Thread separada para processar a entrada do usuário
        Thread inputThread = new Thread(ReadInput);
        inputThread.IsBackground = true;
        inputThread.Start();
        
        // Loop principal que permite exibir mensagens e atualizar status
        while (_running)
        {
            // Se estiver aguardando conexão, exibe mensagem de status
            if (_waitingForConnection && !_isConnected)
            {
                Console.CursorVisible = false;
            }
            else if (_isConnected)
            {
                Console.CursorVisible = true;
            }
            
            Thread.Sleep(100);
        }
        
        Console.WriteLine("Cliente encerrado.");
    }
    
    // Método para enviar mensagens binárias diretamente, sem depender da implementação defeituosa
    static void SendDirectBinaryMessage(string channelId, string message)
    {
        try
        {
            // Formatar corretamente: comprimento do ID do canal (1 byte) + ID do canal + mensagem
            byte[] channelIdBytes = Encoding.UTF8.GetBytes(channelId);
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            
            // Criar array de dados com o formato correto
            byte[] data = new byte[1 + channelIdBytes.Length + messageBytes.Length];
            data[0] = (byte)channelIdBytes.Length; // Primeiro byte é o comprimento do ID
            
            // Copiar ID do canal após o byte de comprimento
            Buffer.BlockCopy(channelIdBytes, 0, data, 1, channelIdBytes.Length);
            
            // Copiar mensagem após o ID do canal
            Buffer.BlockCopy(messageBytes, 0, data, 1 + channelIdBytes.Length, messageBytes.Length);
            
            // Criar o pacote diretamente
            var packet = new FastPacket(EPacketType.ChannelBroadcast, data, _client.SessionId);
            _client.SendPacket(packet);
            
            PrintStatus($"Mensagem binária enviada para canal {channelId}: {message}");
        }
        catch (Exception ex)
        {
            PrintStatus($"Erro ao enviar mensagem binária: {ex.Message}");
        }
    }
    
    // Método para enviar pacotes UDP diretamente, contornando completamente a biblioteca
    static void SendRawUdpPacket(string channelId, string message)
    {
        try
        {
            byte[] channelIdBytes = Encoding.UTF8.GetBytes(channelId);
            if (channelIdBytes.Length > 32) // Limite de segurança
            {
                channelIdBytes = Encoding.UTF8.GetBytes("test"); // Canal de fallback
            }
            
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            
            // PROTOCOLO SIMPLIFICADO - Sem SessionId
            
            // [ChannelIdLength (1 byte)][ChannelId (n bytes)][Mensagem]
            byte[] channelPacketData = new byte[1 + channelIdBytes.Length + messageBytes.Length];
            channelPacketData[0] = (byte)channelIdBytes.Length;
            Buffer.BlockCopy(channelIdBytes, 0, channelPacketData, 1, channelIdBytes.Length);
            Buffer.BlockCopy(messageBytes, 0, channelPacketData, 1 + channelIdBytes.Length, messageBytes.Length);
            
            // Pacote completo simplificado: [Tipo (ChannelBroadcast=9)][ChannelPacketData]
            byte[] fullPacket = new byte[1 + channelPacketData.Length];
            fullPacket[0] = (byte)EPacketType.ChannelBroadcast; // Tipo 9
            
            // Copiar dados do canal
            Buffer.BlockCopy(channelPacketData, 0, fullPacket, 1, channelPacketData.Length);
            
            // Debug - mostrar o pacote em hexadecimal
            StringBuilder hexDump = new StringBuilder("Raw packet: ");
            for (int i = 0; i < Math.Min(fullPacket.Length, 40); i++)
            {
                hexDump.Append($"{fullPacket[i]:X2} ");
            }
            PrintStatus(hexDump.ToString());
            
            // Enviar diretamente via UDP sem usar a biblioteca
            IPEndPoint serverEndpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 2593);
            _directClient.Send(fullPacket, fullPacket.Length, serverEndpoint);
            
            PrintStatus($"Pacote UDP enviado diretamente para o canal {channelId}: {message}");
        }
        catch (Exception ex)
        {
            PrintStatus($"Erro ao enviar pacote raw: {ex.Message}");
        }
    }

    static void ReadInput()
    {
        while (_running)
        {
            // Só permite entrada quando estiver conectado
            if (_isConnected && Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                
                if (key.Key == ConsoleKey.Enter)
                {
                    string mensagem = _currentInput.Trim();
                    _currentInput = "";
                    
                    Console.WriteLine(); // Nova linha após Enter
                    
                    if (string.IsNullOrEmpty(mensagem) || mensagem.ToLower() == "sair")
                    {
                        _running = false;
                        break;
                    }
                    
                    // Check for command patterns
                    if (mensagem.StartsWith("/join "))
                    {
                        // Join channel command with binary protocol
                        string channelName = mensagem.Substring(6).Trim();
                        
                        // Ensure a short, valid channel ID
                        string channelId;
                        if (channelName.Length > 16) {
                            // If name is too long, use a shorter ID
                            channelId = "public";
                            PrintStatus($"Channel name too long. Using 'public' as channel ID instead.");
                        } else {
                            channelId = channelName;
                        }
                        
                        try
                        {
                            _client.JoinChannelBinary(channelId);
                            PrintStatus($"Tentando entrar no canal: {channelId} (protocolo binário)");
                        }
                        catch (Exception ex)
                        {
                            PrintStatus($"Erro ao entrar no canal: {ex.Message}");
                        }
                    }
                    else if (mensagem.StartsWith("/leave"))
                    {
                        // Leave current channel
                        if (!string.IsNullOrEmpty(_currentChannel))
                        {
                            try
                            {
                                _client.LeaveChannel(_currentChannel);
                                PrintStatus($"Saindo do canal: {_currentChannel}");
                            }
                            catch (Exception ex)
                            {
                                PrintStatus($"Erro ao sair do canal: {ex.Message}");
                            }
                        }
                        else
                        {
                            PrintStatus("Não há canal ativo para sair.");
                        }
                    }
                    else if (mensagem.StartsWith("/list"))
                    {
                        // Request channel list
                        try
                        {
                            _client.RequestChannelList();
                            PrintStatus("Solicitando lista de canais...");
                        }
                        catch (Exception ex)
                        {
                            PrintStatus($"Erro ao solicitar lista de canais: {ex.Message}");
                        }
                    }
                    else if (mensagem.StartsWith("/create "))
                    {
                        // Create channel command
                        string channelName = mensagem.Substring(8).Trim();
                        try
                        {
                            _client.CreateChannel(channelName, true); // Create public channel
                            PrintStatus($"Criando canal público: {channelName}");
                        }
                        catch (Exception ex)
                        {
                            PrintStatus($"Erro ao criar canal: {ex.Message}");
                        }
                    }
                    else if (mensagem == "/raw")
                    {
                        // Modo de teste com pacotes UDP diretos
                        try
                        {
                            SendRawUdpPacket("test", "Mensagem enviada diretamente via UDP");
                        }
                        catch (Exception ex)
                        {
                            PrintStatus($"Erro ao enviar pacote raw: {ex.Message}");
                        }
                    }
                    else if (!string.IsNullOrEmpty(_currentChannel))
                    {
                        // If in a channel, send message to channel with binary protocol
                        try
                        {
                            // USAR O MÉTODO RAW UDP para maior controle sobre o formato do pacote
                            if (mensagem == "test")
                            {
                                // Teste com ID de canal fixo
                                SendRawUdpPacket("test", "This is a raw UDP test message");
                                PrintStatus("Teste enviado para canal 'test' (UDP direto)");
                            }
                            else
                            {
                                // Usar o método direto UDP para maior confiabilidade
                                SendRawUdpPacket(_currentChannel, mensagem);
                            }
                        }
                        catch (Exception ex)
                        {
                            PrintStatus($"Erro ao enviar mensagem para o canal: {ex.Message}");
                        }
                    }
                    else
                    {
                        // Regular message to server
                        try
                        {
                            _client.SendMessage(mensagem);
                        }
                        catch (Exception ex)
                        {
                            PrintStatus($"Erro ao enviar: {ex.Message}");
                            // Se houver erro de envio, provavelmente perdeu a conexão
                            _isConnected = false;
                            _waitingForConnection = true;
                        }
                    }
                    
                    PrintPrompt();
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (_currentInput.Length > 0)
                    {
                        _currentInput = _currentInput.Substring(0, _currentInput.Length - 1);
                        Console.Write("\b \b"); // Apaga o último caractere
                    }
                }
                else
                {
                    _currentInput += key.KeyChar;
                    Console.Write(key.KeyChar);
                }
            }
            else
            {
                Thread.Sleep(50);
            }
        }
    }
    
    static void PrintStatus(string message)
    {
        // Guardar posição do cursor e texto atual
        int cursorLeft = Console.CursorLeft;
        
        // Limpar a linha atual
        Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
        
        // Imprimir a mensagem
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
        
        // Reimprimir o prompt e a entrada atual se estiver conectado
        if (_isConnected)
        {
            PrintPrompt();
            Console.Write(_currentInput);
            
            // Restaurar posição do cursor
            Console.CursorLeft = cursorLeft;
        }
    }
    
    static void PrintStatusPrompt(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(message);
        Console.ResetColor();
    }
    
    static void PrintPrompt()
    {
        if (_isConnected)
        {
            if (!string.IsNullOrEmpty(_currentChannel))
            {
                Console.Write($"[Canal: {_currentChannel}] Digite uma mensagem (ou 'sair' para encerrar): ");
            }
            else
            {
                Console.Write("Digite uma mensagem (ou 'sair' para encerrar): ");
            }
        }
    }
}
