using FastUDP;
using System;

class Program
{
    static void Main(string[] args)
    {
        Console.Title = "Servidor UDP - 127.0.0.1:2593";
        
        FastUdpServer server = new FastUdpServer("127.0.0.1", 2593);
        server.DebugMode = true; // Habilitar modo de depuração
        server.Start();
        
        /*Console.CancelKeyPress += (sender, e) => {
            Console.WriteLine("Encerrando servidor UDP...");
            server.Stop();
            e.Cancel = false;
        };
        
        // Handler para outros tipos de encerramento
        AppDomain.CurrentDomain.ProcessExit += (sender, e) => {
            Console.WriteLine("Encerrando servidor UDP...");
            server.Stop();
        };*/
        
        Console.WriteLine("┌────────────────────────────────────────────┐");
        Console.WriteLine("│     Servidor UDP iniciado na porta 2593    │");
        Console.WriteLine("│       Esperando por conexões...            │");
        Console.WriteLine("│                                            │");
        Console.WriteLine("│       Pressione CTRL+C para encerrar       │");
        Console.WriteLine("└────────────────────────────────────────────┘");
        Console.ResetColor();
        
        while (true)
        {
            Thread.Sleep(1000);
        }
    }
}