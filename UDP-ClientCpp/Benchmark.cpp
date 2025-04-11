#include "FClient.h"
#include <iostream>
#include <vector>
#include <atomic>
#include <thread>
#include <chrono>
#include <mutex>
#include <string>
#include <iomanip>
#include <signal.h>

using namespace FastUDP;

// Globals
std::atomic<bool> g_running(true);
std::atomic<uint64_t> g_totalMessagesSent(0);
std::atomic<uint64_t> g_totalMessagesReceived(0);
std::mutex g_consoleMutex;
std::atomic<int> g_connectedClients(0);

// Configuration
const int NUM_CLIENTS = 100;
const int PACKETS_PER_SECOND = 100;
const int TEST_DURATION_SECONDS = 60;

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
    
    if (isError) {
        std::cout << "\033[31m"; // Red text for errors
    } else {
        std::cout << "\033[36m"; // Cyan text for normal logs
    }
    
    std::cout << "[" << std::put_time(&tm_time, "%H:%M:%S") << "] " 
              << message << "\033[0m" << std::endl;
}

// Client class to manage a single client connection
class BenchmarkClient {
private:
    FastUdpClient* m_client;
    std::string m_id;
    std::thread m_sendThread;
    std::atomic<bool> m_isConnected;
    std::atomic<uint64_t> m_messagesSent;
    std::atomic<uint64_t> m_messagesReceived;

public:
    BenchmarkClient(const std::string& serverIp, int serverPort, int id) 
        : m_isConnected(false), m_messagesSent(0), m_messagesReceived(0) {
        
        m_id = "client_" + std::to_string(id);
        m_client = new FastUdpClient(serverIp, serverPort);
        m_client->SetDebugMode(false);
        m_client->SetLoggingLevel(LogLevel::None);

        // Set handlers
        m_client->SetConnectedHandler([this](const std::string& sessionId) {
            m_isConnected = true;
            g_connectedClients++;
            LogMessage("Client " + m_id + " connected with session ID: " + sessionId);
        });

        m_client->SetDisconnectedHandler([this](const std::string& reason) {
            m_isConnected = false;
            g_connectedClients--;
        });

        m_client->SetMessageReceivedHandler([this](const std::string& message) {
            m_messagesReceived++;
            g_totalMessagesReceived++;
        });
    }

    ~BenchmarkClient() {
        Stop();
        if (m_client) {
            delete m_client;
            m_client = nullptr;
        }
    }

    void Connect() {
        m_client->Connect();
    }

    void StartSending() {
        m_sendThread = std::thread([this]() {
            const int sleepTime = 1000 / PACKETS_PER_SECOND;
            
            while (g_running && m_isConnected) {
                std::string message = "benchmark_message_" + m_id + "_" + std::to_string(m_messagesSent);
                
                try {
                    m_client->SendMessage(message);
                    m_messagesSent++;
                    g_totalMessagesSent++;
                }
                catch (const std::exception& ex) {
                    // Silently ignore errors to avoid console spam
                }
                
                std::this_thread::sleep_for(std::chrono::milliseconds(sleepTime));
            }
        });
    }

    void Stop() {
        if (m_isConnected) {
            m_client->Disconnect("Benchmark complete");
            m_isConnected = false;
        }
        
        if (m_sendThread.joinable()) {
            m_sendThread.join();
        }
    }

    uint64_t GetMessagesSent() const {
        return m_messagesSent;
    }

    uint64_t GetMessagesReceived() const {
        return m_messagesReceived;
    }

    bool IsConnected() const {
        return m_isConnected;
    }
};

// Display statistics
void DisplayStats() {
    static auto startTime = std::chrono::steady_clock::now();
    auto now = std::chrono::steady_clock::now();
    auto elapsedSec = std::chrono::duration_cast<std::chrono::seconds>(now - startTime).count();
    
    if (elapsedSec == 0) elapsedSec = 1;  // Avoid division by zero
    
    uint64_t sentCount = g_totalMessagesSent.load();
    uint64_t receivedCount = g_totalMessagesReceived.load();
    double lossRate = (sentCount > 0) ? (1.0 - static_cast<double>(receivedCount) / sentCount) * 100.0 : 0.0;
    
    LogMessage("======= BENCHMARK STATISTICS =======");
    LogMessage("Runtime: " + std::to_string(elapsedSec) + " seconds");
    LogMessage("Connected clients: " + std::to_string(g_connectedClients) + "/" + std::to_string(NUM_CLIENTS));
    LogMessage("Total messages sent: " + std::to_string(sentCount));
    LogMessage("Total messages received: " + std::to_string(receivedCount));
    LogMessage("Message loss rate: " + std::to_string(lossRate) + "%");
    LogMessage("Send rate: " + std::to_string(sentCount / elapsedSec) + " messages/second");
    LogMessage("Receive rate: " + std::to_string(receivedCount / elapsedSec) + " messages/second");
    LogMessage("===================================");
}

int main(int argc, char* argv[]) {    
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
            return 1;
        }
    }
    
    std::cout << "UDP Benchmark starting..." << std::endl;
    std::cout << "Target server: " << serverIp << ":" << serverPort << std::endl;
    std::cout << "Configuration: " << NUM_CLIENTS << " clients, " 
              << PACKETS_PER_SECOND << " packets/second per client" << std::endl;
    std::cout << "Test duration: " << TEST_DURATION_SECONDS << " seconds" << std::endl;

    try {
        // Create clients
        std::vector<BenchmarkClient*> clients;
        LogMessage("Creating " + std::to_string(NUM_CLIENTS) + " UDP clients...");
        
        for (int i = 0; i < NUM_CLIENTS; i++) {
            BenchmarkClient* client = new BenchmarkClient(serverIp, serverPort, i);
            clients.push_back(client);
        }
        
        // Connect all clients
        LogMessage("Connecting clients to server...");
        for (auto& client : clients) {
            client->Connect();
        }
        
        // Wait for all clients to connect
        LogMessage("Waiting for connections to establish...");
        int maxWaitSecs = 30;
        for (int i = 0; i < maxWaitSecs; i++) {
            if (g_connectedClients >= NUM_CLIENTS) {
                break;
            }
            std::this_thread::sleep_for(std::chrono::seconds(1));
            LogMessage("Connected: " + std::to_string(g_connectedClients) + "/" + std::to_string(NUM_CLIENTS));
        }
        
        if (g_connectedClients == 0) {
            LogMessage("No clients could connect to the server. Aborting benchmark.", true);
            g_running = false;
        } else {
            LogMessage("Starting benchmark with " + std::to_string(g_connectedClients) + " connected clients");
            
            // Start sending messages from all connected clients
            for (auto& client : clients) {
                if (client->IsConnected()) {
                    client->StartSending();
                }
            }
            
            // Run benchmark for specified duration
            auto startTime = std::chrono::steady_clock::now();
            while (g_running) {
                // Display stats every 5 seconds
                static auto lastStatTime = std::chrono::steady_clock::now();
                auto now = std::chrono::steady_clock::now();
                
                if (std::chrono::duration_cast<std::chrono::seconds>(now - lastStatTime).count() >= 5) {
                    DisplayStats();
                    lastStatTime = now;
                }
                
                // Check if test duration has elapsed
                if (std::chrono::duration_cast<std::chrono::seconds>(now - startTime).count() >= TEST_DURATION_SECONDS) {
                    LogMessage("Benchmark duration completed");
                    g_running = false;
                }
                
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
            }
        }
        
        // Final statistics
        DisplayStats();
        
        // Clean shutdown
        LogMessage("Shutting down clients...");
        for (auto& client : clients) {
            client->Stop();
            delete client;
        }
        clients.clear();
        
        LogMessage("Benchmark complete");
    } catch (const std::exception& ex) {
        LogMessage("Fatal error: " + std::string(ex.what()), true);
        return 1;
    }
    
    return 0;
}
