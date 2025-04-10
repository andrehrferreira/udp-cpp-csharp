#pragma once

#include <string>
#include <vector>
#include <unordered_map>
#include <functional>
#include <memory>
#include <thread>
#include <atomic>
#include <mutex>
#include <condition_variable>
#include <chrono>
#include <queue>

// Forward declaration of FastPacket
class FastPacket;

namespace FastUDP {

// Define event handler types
using StringEventHandler = std::function<void(const std::string&)>;

enum class LogLevel {
    None = 0,
    Critical = 1,
    Warning = 2,
    Basic = 3,
    Verbose = 4,
    All = 5
};

// Enum for packet types - MUST match the C# version exactly
enum class EPacketType : uint8_t {
    // Control packets (0-9)
    Ping = 0,
    Pong = 1,
    Connect = 2,
    ConnectResponse = 3,
    Reconnect = 4,
    Disconnect = 5,
    Shutdown = 6,
    
    // Data packets (10+)
    Message = 10,
    BinaryData = 11,
    Ack = 12,
    Error = 13
};

// Forward declaration
class FastPacket;

class FastUdpClient {
public:
    // Constructor
    FastUdpClient(const std::string& serverIp, int serverPort);
    
    // Destructor
    ~FastUdpClient();
    
    // Prevent copying or moving
    FastUdpClient(const FastUdpClient&) = delete;
    FastUdpClient& operator=(const FastUdpClient&) = delete;
    FastUdpClient(FastUdpClient&&) = delete;
    FastUdpClient& operator=(FastUdpClient&&) = delete;
    
    // Public methods
    void Connect();
    void SendMessage(const std::string& message);
    void SendBinaryData(const std::vector<uint8_t>& data);
    void Disconnect(const std::string& reason = "Client disconnected normally");
    void SetDebugMode(bool mode) { debugMode = mode; }
    void SetLoggingLevel(LogLevel level) { loggingLevel = level; }
    bool IsConnected() const { return connected; }
    std::string GetSessionId() const { return sessionId; }
    
    // Channel-related methods
    void JoinChannel(const std::string& channelId);
    void LeaveChannel(const std::string& channelId);
    void CreateChannel(const std::string& channelName, bool isPublic);
    void SendChannelMessage(const std::string& channelId, const std::string& message);
    void RequestChannelList();
    std::vector<std::string> GetJoinedChannels() const;
    
    // Event registration methods
    void SetMessageReceivedHandler(StringEventHandler handler) { messageReceivedHandler = handler; }
    void SetConnectedHandler(StringEventHandler handler) { connectedHandler = handler; }
    void SetDisconnectedHandler(StringEventHandler handler) { disconnectedHandler = handler; }
    void SetReconnectingHandler(StringEventHandler handler) { reconnectingHandler = handler; }
    void SetLogMessageHandler(StringEventHandler handler) { logMessageHandler = handler; }
    void SetChannelMessageReceivedHandler(StringEventHandler handler) { channelMessageReceivedHandler = handler; }
    void SetChannelJoinedHandler(StringEventHandler handler) { channelJoinedHandler = handler; }
    void SetChannelLeftHandler(StringEventHandler handler) { channelLeftHandler = handler; }
    
private:
    // Socket and connection info
    int socketFd;
    std::string serverIp;
    int serverPort;
    std::string sessionId;
    std::atomic<bool> connected;
    
    // Reconnection state
    std::atomic<bool> isReconnecting;
    std::atomic<bool> serverShutdown;
    std::atomic<bool> waitingForConnectResponse;
    
    // Thread management
    std::thread listenerThread;
    std::atomic<bool> running;
    std::mutex mutex;
    
    // Timer management
    std::thread pingTimerThread;
    std::thread reconnectTimerThread;
    std::thread connectTimeoutTimerThread;
    std::atomic<bool> pingTimerRunning;
    std::atomic<bool> reconnectTimerRunning;
    std::atomic<bool> connectTimeoutTimerRunning;
    int pingInterval = 5000;  // 5 seconds
    int reconnectInterval = 30000; // 30 seconds
    int connectTimeoutInterval = 15000; // 15 seconds
    
    // Channel tracking
    std::vector<std::string> channels;
    
    // Debug settings
    bool debugMode = false;
    LogLevel loggingLevel = LogLevel::Basic;
    
    // Event handlers
    StringEventHandler messageReceivedHandler;
    StringEventHandler connectedHandler;
    StringEventHandler disconnectedHandler;
    StringEventHandler reconnectingHandler;
    StringEventHandler logMessageHandler;
    StringEventHandler channelMessageReceivedHandler;
    StringEventHandler channelJoinedHandler;
    StringEventHandler channelLeftHandler;
    
    // Private methods
    void InitializeSocket();
    void CloseSocket();
    void StartListeningThread();
    void ListenerThreadFunction();
    void ProcessReceivedPacket(const std::vector<uint8_t>& data);
    void HandleConnectResponse(const FastPacket& packet);
    
    // Timer functions
    void StartPingTimer();
    void StopPingTimer();
    void PingTimerFunction();
    void SendPing();
    
    void StartReconnectTimer(int interval = 30000);
    void StopReconnectTimer();
    void ReconnectTimerFunction();
    void StartReconnect();
    void TryReconnect();
    
    void StartConnectTimeoutTimer();
    void StopConnectTimeoutTimer();
    void ConnectTimeoutTimerFunction();
    void HandleConnectTimeout();
    
    // Packet sending
    void SendPacket(const FastPacket& packet);
    
    // Channel management
    void HandleChannelStateChange(const std::string& channelId, bool joined);
    
    // Logging
    void LogDebug(const std::string& message);
    void LogDebug(const std::string& message, LogLevel level);
    void DebugPacketBytes(const std::vector<uint8_t>& data);
};

// FastPacket class for C++
class FastPacket {
public:
    // Constructors
    FastPacket(EPacketType type);
    FastPacket(EPacketType type, const std::string& data, const std::string& sessionId = "");
    FastPacket(EPacketType type, const std::vector<uint8_t>& data, const std::string& sessionId = "");
    FastPacket(const std::vector<uint8_t>& packetData); // Deserialize constructor
    
    // Getters
    EPacketType GetType() const { return type; }
    std::vector<uint8_t> GetData() const { return data; }
    std::string GetSessionId() const { return sessionId; }
    std::string GetDataAsString() const;
    
    // Serialize the packet to bytes
    std::vector<uint8_t> Serialize() const;
    
    // Static factory methods for common packet types
    static FastPacket CreatePing();
    static FastPacket CreatePong();
    static FastPacket CreateConnect();
    static FastPacket CreateConnectResponse(const std::string& sessionId);
    static FastPacket CreateDisconnect(const std::string& reason);
    static FastPacket CreateMessage(const std::string& message, const std::string& sessionId = "");
    static FastPacket CreateReconnect();
    
private:
    EPacketType type;
    std::vector<uint8_t> data;
    std::string sessionId;
};

} // namespace FastUDP
