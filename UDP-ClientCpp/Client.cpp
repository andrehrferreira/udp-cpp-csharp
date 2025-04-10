#include "FClient.h"
#include <iostream>
#include <atomic>
#include <thread>
#include <string>
#include <mutex>
#include <chrono>
#include <iomanip>
#include <signal.h>

#ifdef _WIN32
    #include <conio.h>
    #define CLEAR_SCREEN() system("cls")
#else
    #include <termios.h>
    #include <unistd.h>
    #include <fcntl.h>
    #define CLEAR_SCREEN() std::cout << "\033[2J\033[H"

    int kbhit() {
        termios oldt, newt;
        int ch;
        int oldf;

        tcgetattr(STDIN_FILENO, &oldt);
        newt = oldt;
        newt.c_lflag &= ~(ICANON | ECHO);
        tcsetattr(STDIN_FILENO, TCSANOW, &newt);
        oldf = fcntl(STDIN_FILENO, F_GETFL, 0);
        fcntl(STDIN_FILENO, F_SETFL, oldf | O_NONBLOCK);

        ch = getchar();

        tcsetattr(STDIN_FILENO, TCSANOW, &oldt);
        fcntl(STDIN_FILENO, F_SETFL, oldf);

        if (ch != EOF) {
            ungetc(ch, stdin);
            return 1;
        }

        return 0;
    }

    char getch() {
        char buf = 0;
        termios old = {};
        tcgetattr(STDIN_FILENO, &old);
        termios newt = old;
        newt.c_lflag &= ~(ICANON | ECHO);
        tcsetattr(STDIN_FILENO, TCSANOW, &newt);
        ssize_t result = read(STDIN_FILENO, &buf, 1);
        (void)result;
        tcsetattr(STDIN_FILENO, TCSANOW, &old);
        return buf;
    }
#endif

using namespace FastUDP;

// Globals
std::atomic<bool> g_running(true);
std::string g_currentInput;
FastUdpClient* g_client = nullptr;
std::atomic<bool> g_isConnected(false);
std::mutex g_consoleMutex;

// Log with timestamp
void LogMessage(const std::string& message, bool isError = false) {
    auto now = std::chrono::system_clock::now();
    auto time = std::chrono::system_clock::to_time_t(now);
    std::tm tm_time;
#ifdef _WIN32
    localtime_s(&tm_time, &time);
#else
    localtime_r(&time, &tm_time);
#endif
    
    std::lock_guard<std::mutex> lock(g_consoleMutex);
    std::cout << "\r" << std::string(80, ' ') << "\r";
    
    if (isError) {
        std::cout << "\033[31m"; // Red text for errors
    } else {
        std::cout << "\033[36m"; // Cyan text for normal logs
    }
    
    std::cout << "[" << std::put_time(&tm_time, "%H:%M:%S") << "] " 
              << message << "\033[0m" << std::endl;
              
    // Reprint prompt if connected
    if (g_isConnected) {
        std::cout << "Digite uma mensagem (ou 'sair' para encerrar): " << g_currentInput;
    }
}

// Signal handler to handle Ctrl+C
void signalHandler(int signum) {
    if (signum == SIGINT || signum == SIGTERM) {
        g_running = false;
        std::cout << std::endl;
        std::cout << "Received termination signal, shutting down..." << std::endl;
    }
}

// Display usage
void DisplayUsage() {
    std::cout << "Usage: app [server_ip] [server_port]" << std::endl;
    std::cout << "  server_ip   - Server IP address (default: 127.0.0.1)" << std::endl;
    std::cout << "  server_port - Server port (default: 2593)" << std::endl;
}

// Read input in a separate thread
void ReadInput() {
    while (g_running) {
        if (g_isConnected) {
#ifdef _WIN32
            if (_kbhit()) {
                char key = _getch();
#else
            if (kbhit()) {
                char key = getch();
#endif
                if (key == '\r' || key == '\n') {
                    std::string message = g_currentInput;
                    g_currentInput.clear();

                    {
                        std::lock_guard<std::mutex> lock(g_consoleMutex);
                        std::cout << std::endl;
                    }

                    if (message.empty() || message == "sair") {
                        g_running = false;
                        break;
                    }

                    try {
                        if (g_client && g_isConnected) {
                            LogMessage("Enviando: " + message);
                            g_client->SendMessage(message);
                        } else {
                            LogMessage("Não foi possível enviar: cliente não está conectado", true);
                        }
                    }
                    catch (const std::exception& ex) {
                        LogMessage("Erro ao enviar: " + std::string(ex.what()), true);
                        g_isConnected = false;
                    }

                    // Reprint prompt
                    std::lock_guard<std::mutex> lock(g_consoleMutex);
                    std::cout << "Digite uma mensagem (ou 'sair' para encerrar): ";
                }
                else if (key == '\b' || key == 127) {
                    if (!g_currentInput.empty()) {
                        g_currentInput.pop_back();
                        std::lock_guard<std::mutex> lock(g_consoleMutex);
                        std::cout << "\b \b";
                    }
                }
                else {
                    g_currentInput += key;
                    std::lock_guard<std::mutex> lock(g_consoleMutex);
                    std::cout << key;
                }
            }
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }
}

int main(int argc, char* argv[]) {
    // Setup signal handling
    //signal(SIGINT, signalHandler);
    //signal(SIGTERM, signalHandler);
    
    // Parse command line arguments
    std::string serverIp = "127.0.0.1";
    int serverPort = 2593;
    
    if (argc > 1) {
        serverIp = argv[1];
    }
    
    if (argc > 2) {
        try {
            serverPort = std::stoi(argv[2]);
        } catch (const std::exception& ex) {
            std::cerr << "Invalid port number: " << argv[2] << std::endl;
            DisplayUsage();
            return 1;
        }
    }
    
    CLEAR_SCREEN();
    std::cout << "Cliente UDP iniciando..." << std::endl;
    std::cout << "Tentando conectar a " << serverIp << ":" << serverPort << std::endl;

    try {
        // Create client
        LogMessage("Criando cliente UDP");
        g_client = new FastUdpClient(serverIp, serverPort);
        g_client->SetDebugMode(true);
        g_client->SetLoggingLevel(LogLevel::Basic);

        // Set handlers
        LogMessage("Configurando manipuladores de eventos");
        
        g_client->SetConnectedHandler([](const std::string& sessionId) {
            g_isConnected = true;
            LogMessage("Conectado ao servidor com ID de sessão: " + sessionId);
            
            std::lock_guard<std::mutex> lock(g_consoleMutex);
            std::cout << std::endl;
            std::cout << "\033[32mConexão estabelecida! Você já pode enviar mensagens.\033[0m" << std::endl;
            std::cout << "Digite uma mensagem (ou 'sair' para encerrar): ";
        });

        g_client->SetDisconnectedHandler([](const std::string& reason) {
            g_isConnected = false;
            LogMessage("Desconectado do servidor: " + reason, true);
        });

        g_client->SetReconnectingHandler([](const std::string& message) {
            g_isConnected = false;
            LogMessage("Reconexão: " + message);
        });

        g_client->SetMessageReceivedHandler([](const std::string& message) {
            LogMessage("Resposta do servidor: " + message);
        });

        // Start input thread
        std::thread inputThread(ReadInput);
        
        // Connect to server - this is now synchronous in the main thread
        LogMessage("Conectando ao servidor...");
        g_client->Connect();
        
        LogMessage("Inicialização concluída, aguardando eventos");
        
        // Main loop - periodically check connection status
        while (g_running) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1000));
            
            // Check connection status every 5 seconds
            static auto lastCheck = std::chrono::steady_clock::now();
            auto now = std::chrono::steady_clock::now();
            if (std::chrono::duration_cast<std::chrono::seconds>(now - lastCheck).count() >= 5) {
                lastCheck = now;
                
                if (!g_isConnected) {
                    LogMessage("Aguardando conexão com o servidor...", true);
                }
            }
        }
        
        // Clean shutdown
        LogMessage("Encerrando cliente...");
        
        if (g_client) {
            if (g_isConnected) {
                LogMessage("Enviando desconexão ao servidor");
                g_client->Disconnect("Client shutdown normally");
            }
            
            delete g_client;
            g_client = nullptr;
        }
        
        // Wait for input thread to finish
        if (inputThread.joinable()) {
            inputThread.join();
        }
        
        std::cout << "Cliente finalizado." << std::endl;
    } catch (const std::exception& ex) {
        std::cerr << "Erro fatal: " << ex.what() << std::endl;
    }
    
    return 0;
}
