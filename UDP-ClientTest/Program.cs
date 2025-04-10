using FastUDP;
using System.Text;

class Program
{
    private static bool _running = true;
    private static string _currentInput = "";
    private static FastUdpClient _client;
    private static bool _isConnected = false;
    private static bool _waitingForConnection = true;
    
    static void Main(string[] args)
    {
        Console.Clear();
        Console.WriteLine("Cliente UDP iniciando...");
        Console.WriteLine("Aguardando conexão com o servidor...");
        
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
                    
                    // Enviando a mensagem
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
            Console.Write("Digite uma mensagem (ou 'sair' para encerrar): ");
        }
    }
}
