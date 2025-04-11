#include "FClient.h"
#include <iostream>
#include <sstream>
#include <iomanip>
#include <cstring>

#ifdef _WIN32
    // Prevent Windows.h from defining a macro named SendMessage
    #define NOMINMAX
    #define WIN32_LEAN_AND_MEAN
    #define NOGDI
    #define NOUSER
    #include <winsock2.h>
    #include <ws2tcpip.h>
    #pragma comment(lib, "ws2_32.lib")
    typedef int socklen_t;
#else
    #include <unistd.h>
    #include <sys/socket.h>
    #include <arpa/inet.h>
    #include <netinet/in.h>
    #include <netdb.h>
    #include <fcntl.h>
    #define SOCKET int
    #define SOCKET_ERROR -1
    #define INVALID_SOCKET -1
    #define closesocket close
#endif

// Make sure Windows.h macros don't interfere with our methods
#ifdef SendMessage
    #undef SendMessage
#endif

namespace FastUDP {

// FastPacket Implementation
FastPacket::FastPacket(EPacketType type) : type(type) {
}

FastPacket::FastPacket(EPacketType type, const std::string& dataStr, const std::string& sessionId) 
    : type(type), sessionId(sessionId) {
    // Convert string data to bytes
    data.assign(dataStr.begin(), dataStr.end());
}

FastPacket::FastPacket(EPacketType type, const std::vector<uint8_t>& data, const std::string& sessionId) 
    : type(type), data(data), sessionId(sessionId) {
}

FastPacket::FastPacket(const std::vector<uint8_t>& packetData) {
    if (packetData.empty()) 
        return;
    
    // Extract type
    type = static_cast<EPacketType>(packetData[0]);

    // Extract sessionId if present
    if (packetData.size() > 1) {
        uint8_t sessionIdLength = packetData[1];
        
        if (sessionIdLength > 0 && packetData.size() >= 2 + static_cast<size_t>(sessionIdLength)) {
            sessionId = std::string(packetData.begin() + 2, packetData.begin() + 2 + sessionIdLength);
            
            // Extract data if present
            if (packetData.size() > 2 + static_cast<size_t>(sessionIdLength)) {
                data = std::vector<uint8_t>(packetData.begin() + 2 + sessionIdLength, packetData.end());
            }
        } else if (packetData.size() > 2) {
            // No sessionId, just data after type and sessionIdLength
            data = std::vector<uint8_t>(packetData.begin() + 2, packetData.end());
        }
    }
}

std::string FastPacket::GetDataAsString() const {
    return std::string(data.begin(), data.end());
}

std::vector<uint8_t> FastPacket::Serialize() const {
    std::vector<uint8_t> result;

    // Add packet type
    result.push_back(static_cast<uint8_t>(type));

    // Add sessionId length and sessionId if present
    uint8_t sessionIdLength = static_cast<uint8_t>(sessionId.size());
    result.push_back(sessionIdLength);

    if (sessionIdLength > 0) {
        result.insert(result.end(), sessionId.begin(), sessionId.end());
    }

    // Add data if present
    if (!data.empty()) {
        result.insert(result.end(), data.begin(), data.end());
    }

    return result;
}

// Static factory methods
FastPacket FastPacket::CreatePing() {
    return FastPacket(EPacketType::Ping);
}

FastPacket FastPacket::CreatePong() {
    return FastPacket(EPacketType::Pong);
}

FastPacket FastPacket::CreateConnect() {
    return FastPacket(EPacketType::Connect);
}

FastPacket FastPacket::CreateConnectResponse(const std::string& sessionId) {
    return FastPacket(EPacketType::ConnectResponse, "", sessionId);
}

FastPacket FastPacket::CreateDisconnect(const std::string& reason) {
    return FastPacket(EPacketType::Disconnect, reason);
}

FastPacket FastPacket::CreateMessage(const std::string& message, const std::string& sessionId) {
    return FastPacket(EPacketType::Message, message, sessionId);
}

FastPacket FastPacket::CreateReconnect() {
    return FastPacket(EPacketType::Reconnect);
}

// FastUdpClient Implementation
FastUdpClient::FastUdpClient(const std::string& serverIp, int serverPort)
    : serverIp(serverIp), serverPort(serverPort),
      connected(false), isReconnecting(false), serverShutdown(false), waitingForConnectResponse(false),
      running(true), pingTimerRunning(false), reconnectTimerRunning(false), connectTimeoutTimerRunning(false) {
    
    // Initialize socket
    InitializeSocket();
    
    // Start listener thread
    StartListeningThread();
    
    // Connect to server
    Connect();
}

FastUdpClient::~FastUdpClient() {
    // Set running to false to stop all threads
    running = false;
    
    // Try to send disconnect packet
    if (connected) {
        try {
            SendPacket(FastPacket::CreateDisconnect("Client disconnected normally"));
        } catch (...) {
            // Ignore errors during shutdown
        }
    }
    
    // Stop all timers
    StopPingTimer();
    StopReconnectTimer();
    StopConnectTimeoutTimer();
    
    // Wait for threads to finish
    if (listenerThread.joinable()) {
        listenerThread.join();
    }
    
    if (pingTimerThread.joinable()) {
        pingTimerThread.join();
    }
    
    if (reconnectTimerThread.joinable()) {
        reconnectTimerThread.join();
    }
    
    if (connectTimeoutTimerThread.joinable()) {
        connectTimeoutTimerThread.join();
    }
    
    // Close socket
    CloseSocket();
    
    LogDebug("Client disposed", LogLevel::Basic);
}

void FastUdpClient::InitializeSocket() {
#ifdef _WIN32
    // Initialize WinSock
    WSADATA wsaData;
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) {
        LogDebug("Failed to initialize WinSock", LogLevel::Critical);
        throw std::runtime_error("Failed to initialize WinSock");
    }
#endif

    // Create UDP socket
    socketFd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socketFd == INVALID_SOCKET) {
        LogDebug("Failed to create socket", LogLevel::Critical);
        throw std::runtime_error("Failed to create socket");
    }

    // Bind to any local address to receive replies - THIS WAS MISSING
    struct sockaddr_in localAddr;
    memset(&localAddr, 0, sizeof(localAddr));
    localAddr.sin_family = AF_INET;
    localAddr.sin_addr.s_addr = htonl(INADDR_ANY); // Any local address
    localAddr.sin_port = htons(0);  // Let the OS choose a port

    if (bind(socketFd, (struct sockaddr*)&localAddr, sizeof(localAddr)) == SOCKET_ERROR) {
#ifdef _WIN32
        LogDebug("Failed to bind socket: " + std::to_string(WSAGetLastError()), LogLevel::Critical);
        closesocket(socketFd);
        WSACleanup();
#else
        LogDebug("Failed to bind socket: " + std::string(strerror(errno)), LogLevel::Critical);
        close(socketFd);
#endif
        throw std::runtime_error("Failed to bind socket to local address");
    }
    
    LogDebug("Socket successfully bound to local address", LogLevel::Basic);

    // Set non-blocking mode
#ifdef _WIN32
    u_long mode = 1;  // 1 to enable non-blocking, 0 to disable
    if (ioctlsocket(socketFd, FIONBIO, &mode) != 0) {
        closesocket(socketFd);
        LogDebug("Failed to set non-blocking mode", LogLevel::Critical);
        throw std::runtime_error("Failed to set non-blocking mode");
    }
#else
    int flags = fcntl(socketFd, F_GETFL, 0);
    if (flags == -1) {
        close(socketFd);
        LogDebug("Failed to get socket flags", LogLevel::Critical);
        throw std::runtime_error("Failed to get socket flags");
    }
    
    if (fcntl(socketFd, F_SETFL, flags | O_NONBLOCK) == -1) {
        close(socketFd);
        LogDebug("Failed to set non-blocking mode", LogLevel::Critical);
        throw std::runtime_error("Failed to set non-blocking mode");
    }
#endif
}

void FastUdpClient::CloseSocket() {
    if (socketFd != INVALID_SOCKET) {
        closesocket(socketFd);
        socketFd = INVALID_SOCKET;
    }

#ifdef _WIN32
    WSACleanup();
#endif
}

void FastUdpClient::StartListeningThread() {
    listenerThread = std::thread(&FastUdpClient::ListenerThreadFunction, this);
}

void FastUdpClient::ListenerThreadFunction() {
    constexpr size_t BUFFER_SIZE = 8192;
    std::vector<uint8_t> buffer(BUFFER_SIZE);
    
    // Create a sockaddr_in struct for the sender
    struct sockaddr_in senderAddr;
    socklen_t senderAddrLen = sizeof(senderAddr);
    
    while (running) {
        // Try to receive data
        int bytesReceived = recvfrom(socketFd, reinterpret_cast<char*>(buffer.data()), 
                                     BUFFER_SIZE, 0, 
                                     (struct sockaddr*)&senderAddr, &senderAddrLen);
        
        if (bytesReceived > 0) {
            // Resize the vector to match the received bytes and process
            buffer.resize(bytesReceived);
            ProcessReceivedPacket(buffer);
            // Restore the buffer size for next receive
            buffer.resize(BUFFER_SIZE);
        } else if (bytesReceived == SOCKET_ERROR) {
            // Check if this is a would-block error (expected in non-blocking mode)
#ifdef _WIN32
            int error = WSAGetLastError();
            if (error != WSAEWOULDBLOCK) {
                LogDebug("Socket error: " + std::to_string(error), LogLevel::Critical);
            }
#else
            if (errno != EAGAIN && errno != EWOULDBLOCK) {
                LogDebug("Socket error: " + std::string(strerror(errno)), LogLevel::Critical);
            }
#endif
        }
        
        // Small delay to avoid busy waiting
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
}

void FastUdpClient::Connect() {
    // Don't start a new connection attempt if we're already connected
    if (connected) {
        LogDebug("Connect called but client is already connected", LogLevel::Warning);
        return;
    }
    
    // Don't start a new connection attempt if we're already waiting for a response
    if (waitingForConnectResponse) {
        LogDebug("Connect called but already waiting for connection response", LogLevel::Warning);
        return;
    }
    
    LogDebug("Starting connection to server...", LogLevel::Basic);
    serverShutdown = false;
    connected = false;
    isReconnecting = false;
    
    // Cancel any previous timeout timer
    StopConnectTimeoutTimer();
    
    // Mark that we're waiting for a response
    waitingForConnectResponse = true;
    
    try {
        LogDebug("Sending CONNECT packet to server...", LogLevel::Basic);
        
        // Setup server address
        struct sockaddr_in serverAddr;
        memset(&serverAddr, 0, sizeof(serverAddr));
        serverAddr.sin_family = AF_INET;
        serverAddr.sin_port = htons(serverPort);
        
#ifdef _WIN32
        serverAddr.sin_addr.s_addr = inet_addr(serverIp.c_str());
#else
        inet_pton(AF_INET, serverIp.c_str(), &serverAddr.sin_addr);
#endif

        // Create and send the Connect packet
        FastPacket connectPacket = FastPacket::CreateConnect();
        std::vector<uint8_t> packetData = connectPacket.Serialize();
        
        // Send the packet
        int bytesSent = sendto(socketFd, reinterpret_cast<const char*>(packetData.data()), 
                              packetData.size(), 0, 
                              (struct sockaddr*)&serverAddr, sizeof(serverAddr));
        
        if (bytesSent == SOCKET_ERROR) {
#ifdef _WIN32
            LogDebug("Error sending CONNECT packet: " + std::to_string(WSAGetLastError()), LogLevel::Critical);
#else
            LogDebug("Error sending CONNECT packet: " + std::string(strerror(errno)), LogLevel::Critical);
#endif
            // Still start the timeout timer to handle the failure properly
            waitingForConnectResponse = true;
            StartConnectTimeoutTimer();
            return;
        }
        
        // Start timeout timer
        StartConnectTimeoutTimer();
    } catch (const std::exception& ex) {
        waitingForConnectResponse = false;
        LogDebug("Failed to send CONNECT: " + std::string(ex.what()), LogLevel::Critical);
        StartReconnect();
    }
}

void FastUdpClient::SendMessage(const std::string& message) {
    if (!this->connected || this->sessionId.empty()) {
        throw std::runtime_error("Not connected to server. Wait for connection before sending messages.");
    }
    
    this->SendPacket(FastPacket::CreateMessage(message, this->sessionId));
}

void FastUdpClient::SendBinaryData(const std::vector<uint8_t>& data) {
    if (!connected || sessionId.empty()) {
        throw std::runtime_error("Not connected to server. Wait for connection before sending data.");
    }
    
    SendPacket(FastPacket(EPacketType::BinaryData, data, sessionId));
}

std::vector<std::string> FastUdpClient::GetJoinedChannels() const {
    // Return a copy of the channels vector
    std::lock_guard<std::mutex> lock(const_cast<std::mutex&>(mutex));
    return channels;
}

void FastUdpClient::SendPacket(const FastPacket& packet) {
    // CONNECT is the only packet that can be sent without an established connection
    if (packet.GetType() != EPacketType::Connect && !connected) {
        LogDebug("Attempted to send while client is disconnected", LogLevel::Warning);
        throw std::runtime_error("Client is not connected. Wait for connection to be established before sending data.");
    }
    
    try {
        // Serialize the packet
        std::vector<uint8_t> packetData = packet.Serialize();
        
        // Prepare destination address
        struct sockaddr_in serverAddr;
        memset(&serverAddr, 0, sizeof(serverAddr));
        serverAddr.sin_family = AF_INET;
        serverAddr.sin_port = htons(serverPort);
        
#ifdef _WIN32
        serverAddr.sin_addr.s_addr = inet_addr(serverIp.c_str());
#else
        inet_pton(AF_INET, serverIp.c_str(), &serverAddr.sin_addr);
#endif
        
        // Send the packet
        int bytesSent = sendto(socketFd, reinterpret_cast<const char*>(packetData.data()), 
                              packetData.size(), 0, 
                              (struct sockaddr*)&serverAddr, sizeof(serverAddr));
        
        if (bytesSent == SOCKET_ERROR) {
#ifdef _WIN32
            LogDebug("Error sending packet: " + std::to_string(WSAGetLastError()), LogLevel::Critical);
#else
            LogDebug("Error sending packet: " + std::string(strerror(errno)), LogLevel::Critical);
#endif
            if (connected) {
                connected = false;
                StartReconnect();
                throw std::runtime_error("Failed to send packet");
            }
        }
    } catch (const std::exception& ex) {
        LogDebug("Error sending packet: " + std::string(ex.what()), LogLevel::Critical);
        if (connected) {
            connected = false;
            StartReconnect();
            throw;
        }
    }
}

void FastUdpClient::ProcessReceivedPacket(const std::vector<uint8_t>& data) {
    try {
        // Deserialize the packet
        FastPacket packet(data);
        
        if (debugMode) {
            std::string packetTypeName;
            switch (packet.GetType()) {
                case EPacketType::Ping: packetTypeName = "Ping"; break;
                case EPacketType::Pong: packetTypeName = "Pong"; break;
                case EPacketType::Connect: packetTypeName = "Connect"; break;
                case EPacketType::ConnectResponse: packetTypeName = "ConnectResponse"; break;
                case EPacketType::Reconnect: packetTypeName = "Reconnect"; break;
                case EPacketType::Disconnect: packetTypeName = "Disconnect"; break;
                case EPacketType::Shutdown: packetTypeName = "Shutdown"; break;
                case EPacketType::Message: packetTypeName = "Message"; break;
                case EPacketType::BinaryData: packetTypeName = "BinaryData"; break;
                case EPacketType::Ack: packetTypeName = "Ack"; break;
                case EPacketType::Error: packetTypeName = "Error"; break;
                default: packetTypeName = "Unknown"; break;
            }
            
            LogDebug("Received packet type " + packetTypeName + 
                    " (" + std::to_string(static_cast<int>(packet.GetType())) + ")" +
                    " from server, SessionId='" + packet.GetSessionId() + "'", LogLevel::Verbose);
            DebugPacketBytes(data);
        }
        
        // Process protocol messages
        switch (packet.GetType()) {
            case EPacketType::Pong:
                // Simple pong just confirms server is alive
                break;
                
            case EPacketType::Reconnect:
                LogDebug("Server requested reconnection", LogLevel::Basic);
                connected = false;
                Connect();
                break;
                
            case EPacketType::Shutdown:
                LogDebug("Server is shutting down", LogLevel::Basic);
                connected = false;
                StopPingTimer();
                
                // Schedule a delayed reconnection attempt
                StopReconnectTimer();
                StartReconnectTimer(10000); // Try to reconnect after 10 seconds
                isReconnecting = true;
                
                // Notify the application
                if (disconnectedHandler) {
                    disconnectedHandler("Server shutdown");
                }
                LogDebug("Will attempt to reconnect in 10 seconds...", LogLevel::Basic);
                break;
                
            case EPacketType::Message: {
                // Normal message, notify the application
                std::string message = packet.GetDataAsString();
                
                // Check if this is a channel message
                if (message.find("[Channel:") == 0 && message.find("]") != std::string::npos) {
                    // Extract channel info and message
                    size_t closeBracketIndex = message.find(']');
                    if (closeBracketIndex > 0) {
                        std::string channelSection = message.substr(1, closeBracketIndex - 1);
                        size_t colonIndex = channelSection.find(':');
                        std::string channelName = channelSection.substr(colonIndex + 1);
                        std::string channelMessage = message.substr(closeBracketIndex + 1);
                        
                        // Trim whitespace
                        channelMessage.erase(0, channelMessage.find_first_not_of(" \t\n\r\f\v"));
                        
                        // Notify about channel message
                        if (channelMessageReceivedHandler) {
                            channelMessageReceivedHandler("Channel " + channelName + ": " + channelMessage);
                        }
                    } else {
                        // If can't parse channel format, treat as regular message
                        if (messageReceivedHandler) {
                            messageReceivedHandler(message);
                        }
                    }
                } else {
                    // Regular message
                    if (messageReceivedHandler) {
                        messageReceivedHandler(message);
                    }
                }
                break;
            }
                
            case EPacketType::ConnectResponse:
                HandleConnectResponse(packet);
                break;
                
            case EPacketType::Error:
                LogDebug("Error received from server: " + packet.GetDataAsString(), LogLevel::Critical);
                break;
                
            default:
                LogDebug("Unhandled packet type: " + std::to_string(static_cast<int>(packet.GetType())), LogLevel::Verbose);
                break;
        }
    } catch (const std::exception& ex) {
        LogDebug("Error processing packet: " + std::string(ex.what()), LogLevel::Critical);
        if (debugMode) {
            DebugPacketBytes(data);
        }
    }
}

void FastUdpClient::HandleConnectResponse(const FastPacket& packet) {
    // Critical: Always stop the timeout timer as soon as we receive a response
    StopConnectTimeoutTimer();
    
    // Important: Reset waiting flag immediately to prevent race conditions
    waitingForConnectResponse = false;
    
    LogDebug("Processing ConnectResponse with SessionId: '" + packet.GetSessionId() + "'", LogLevel::Basic);
        
    // If already connected, and it's the same session, just ignore and don't create a duplicate
    if (connected) {
        LogDebug("Received ConnectResponse while already connected. Current: " + sessionId + 
                ", Received: " + packet.GetSessionId(), LogLevel::Basic);
                
        // Cancel any reconnection attempts that might have been scheduled
        StopReconnectTimer();
        return;
    }
    
    // Store the session ID
    sessionId = packet.GetSessionId();
    
    // Update state - IMPORTANT: Do this before calling any handlers to prevent race conditions
    connected = true;
    isReconnecting = false;
    
    // Cancel reconnect timer
    StopReconnectTimer();
    
    // Start ping timer IMMEDIATELY to prevent server session timeout
    StartPingTimer();
    
    //LogDebug("Connected to server. Session ID: " + sessionId, LogLevel::Basic);
    
    // Start with a ping immediately to prevent session expiration
    try {
        SendPing();
    } catch (const std::exception& ex) {
        LogDebug("Error sending initial ping: " + std::string(ex.what()), LogLevel::Warning);
    }
    
    // Notify application through callback - this is critical
    if (connectedHandler) {
        try {
            connectedHandler(sessionId);
        } catch (const std::exception& ex) {
            LogDebug("Exception in connected handler: " + std::string(ex.what()), LogLevel::Critical);
        }
    }
}

void FastUdpClient::StartPingTimer() {
    // Stop existing timer if running
    StopPingTimer();
    
    // Log the ping timer start for debugging
    LogDebug("Starting ping timer with interval " + std::to_string(pingInterval) + "ms", LogLevel::Verbose);
    
    // Set flag and start new thread
    pingTimerRunning = true;
    pingTimerThread = std::thread(&FastUdpClient::PingTimerFunction, this);
}

void FastUdpClient::StopPingTimer() {
    pingTimerRunning = false;
    if (pingTimerThread.joinable()) {
        pingTimerThread.join();
    }
}

void FastUdpClient::PingTimerFunction() {
    while (pingTimerRunning && running) {
        // Sleep for ping interval
        std::this_thread::sleep_for(std::chrono::milliseconds(pingInterval));
        
        if (pingTimerRunning && running) {
            SendPing();
        }
    }
}

void FastUdpClient::SendPing() {
    // Check if we're connected before sending ping
    if (!connected || sessionId.empty()) {
        LogDebug("Attempted to send PING without an established connection", LogLevel::Warning);
        StopPingTimer();
        StartReconnect();
        return;
    }
    
    try {
        LogDebug("Sending PING to server...", LogLevel::All);
        SendPacket(FastPacket::CreatePing());
    } catch (const std::exception& ex) {
        connected = false;
        LogDebug("Failed to send PING: " + std::string(ex.what()), LogLevel::Critical);
        if (disconnectedHandler) {
            disconnectedHandler("Failed to send ping");
        }
        StartReconnect();
    }
}

void FastUdpClient::StartReconnectTimer(int interval) {
    // Stop existing timer if running
    StopReconnectTimer();
    
    // Set interval and flag
    reconnectInterval = interval;
    reconnectTimerRunning = true;
    
    // Start new thread
    reconnectTimerThread = std::thread(&FastUdpClient::ReconnectTimerFunction, this);
}

void FastUdpClient::StopReconnectTimer() {
    reconnectTimerRunning = false;
    if (reconnectTimerThread.joinable()) {
        reconnectTimerThread.join();
    }
}

void FastUdpClient::ReconnectTimerFunction() {
    while (reconnectTimerRunning && running) {
        // Sleep for reconnect interval
        std::this_thread::sleep_for(std::chrono::milliseconds(reconnectInterval));
        
        if (reconnectTimerRunning && running) {
            TryReconnect();
        }
    }
}

void FastUdpClient::StartReconnect() {
    std::lock_guard<std::mutex> lock(mutex);
    
    // Stop ping timer as we're no longer connected
    StopPingTimer();
    
    // Update connection state
    connected = false;
    
    // Don't start reconnection if already in reconnection mode
    if (isReconnecting) {
        LogDebug("Already in reconnection mode", LogLevel::Verbose);
        return;
    }
    
    isReconnecting = true;
    LogDebug("Starting reconnection process", LogLevel::Basic);
    
    // Notify application about reconnection
    if (reconnectingHandler) {
        try {
            // Unlock to avoid deadlock if handler calls back into client
            mutex.unlock();
            reconnectingHandler("Starting reconnection process");
            mutex.lock();
        } catch (const std::exception& ex) {
            // Ensure lock is reacquired if exception occurs
            if (!mutex.try_lock()) {
                mutex.lock();
            }
            LogDebug("Exception in reconnecting handler: " + std::string(ex.what()), LogLevel::Warning);
        }
    }
    
    // Start the reconnect timer
    StopReconnectTimer();
    StartReconnectTimer();
}

void FastUdpClient::TryReconnect() {
    // Always acquire a mutex lock before checking connection state
    // This prevents race conditions with other threads that might be modifying state
    std::lock_guard<std::mutex> lock(mutex);
    
    // Don't reconnect if already connected
    if (connected) {
        LogDebug("Already connected. Reconnection not needed.", LogLevel::Basic);
        StopReconnectTimer();
        isReconnecting = false;
        return;
    }
    
    // If we're already waiting for a connection response, don't initiate another
    if (waitingForConnectResponse) {
        LogDebug("Already waiting for connection response. Skipping reconnect attempt.", LogLevel::Basic);
        return;
    }

    LogDebug("Attempting to reconnect to server...", LogLevel::Basic);
    if (reconnectingHandler) {
        try {
            reconnectingHandler("Attempting to reconnect...");
        } catch (const std::exception& ex) {
            LogDebug("Exception in reconnecting handler: " + std::string(ex.what()), LogLevel::Warning);
        }
    }

    // Stop the reconnect timer while attempting connection
    StopReconnectTimer();
    
    // Initiate a new connection attempt
    // Call Connect() directly but without the lock to avoid deadlock
    // as Connect() might try to acquire the mutex again
    mutex.unlock();
    try {
        Connect();
    } catch(...) {
        // Re-acquire the lock before handling the error
        mutex.lock();
        LogDebug("Error during reconnection attempt", LogLevel::Critical);
        // Restart the timer to try again later
        StartReconnectTimer();
        throw;
    }
    // Re-acquire the lock
    mutex.lock();
}

void FastUdpClient::StartConnectTimeoutTimer() {
    // Stop existing timer if running
    StopConnectTimeoutTimer();
    
    // Set flag and start new thread
    connectTimeoutTimerRunning = true;
    connectTimeoutTimerThread = std::thread(&FastUdpClient::ConnectTimeoutTimerFunction, this);
}

void FastUdpClient::StopConnectTimeoutTimer() {
    connectTimeoutTimerRunning = false;
    if (connectTimeoutTimerThread.joinable()) {
        connectTimeoutTimerThread.join();
    }
}

void FastUdpClient::ConnectTimeoutTimerFunction() {
    LogDebug("Connection timeout timer started - waiting for " + std::to_string(connectTimeoutInterval) + "ms", LogLevel::Basic);
    // Sleep for timeout interval
    std::this_thread::sleep_for(std::chrono::milliseconds(connectTimeoutInterval));
    
    if (connectTimeoutTimerRunning && running) {
        HandleConnectTimeout();
    }
}

void FastUdpClient::HandleConnectTimeout() {
    // If we're already connected, ignore the timeout
    if (connected) {
        LogDebug("Connection timeout handler called, but client is already connected", LogLevel::Basic);
        return;
    }
    
    // Check again if we got connected between when the timeout was scheduled and now
    if (!waitingForConnectResponse) {
        LogDebug("Connection timeout handler called, but client is no longer waiting for connection", LogLevel::Basic);
        return;
    }
    
    // Add additional lock to ensure synchronization with ConnectResponse handling
    std::lock_guard<std::mutex> lock(mutex);
    
    // Double-check connection state after acquiring the lock
    if (connected || !waitingForConnectResponse) {
        LogDebug("Connection timeout handler: connection state changed, aborting timeout handling", LogLevel::Basic);
        return;
    }
    
    // We're still waiting for a response and not connected - handle the timeout
    waitingForConnectResponse = false;
    LogDebug("Connection timeout - Server offline or unreachable", LogLevel::Critical);
    
    if (disconnectedHandler) {
        try {
            disconnectedHandler("Server offline or unreachable");
        } catch (const std::exception& ex) {
            LogDebug("Exception in disconnect handler: " + std::string(ex.what()), LogLevel::Warning);
        }
    }
    
    // Always attempt to reconnect, even if server was shut down
    serverShutdown = false;
    
    // Schedule a new reconnection attempt
    StopReconnectTimer();
    LogDebug("Will retry connection in 15 seconds", LogLevel::Basic);
    StartReconnectTimer(15000); // Try in 15 seconds
    isReconnecting = true;
}

void FastUdpClient::LogDebug(const std::string& message) {
    if (debugMode) {
        std::string timestamp = [&]() {
            auto now = std::chrono::system_clock::now();
            auto time = std::chrono::system_clock::to_time_t(now);
            std::tm tm_time;
#ifdef _WIN32
            localtime_s(&tm_time, &time);
#else
            localtime_r(&time, &tm_time);
#endif
            std::stringstream ss;
            ss << std::put_time(&tm_time, "%H:%M:%S");
            return ss.str();
        }();
        
        std::string logMessage = "[Client] " + timestamp + " - " + message;
        std::cout << logMessage << std::endl;
        
        if (logMessageHandler) {
            logMessageHandler(logMessage);
        }
    }
}

void FastUdpClient::LogDebug(const std::string& message, LogLevel level) {
    if (debugMode && static_cast<int>(level) <= static_cast<int>(loggingLevel)) {
        std::string timestamp = [&]() {
            auto now = std::chrono::system_clock::now();
            auto time = std::chrono::system_clock::to_time_t(now);
            std::tm tm_time;
#ifdef _WIN32
            localtime_s(&tm_time, &time);
#else
            localtime_r(&time, &tm_time);
#endif
            std::stringstream ss;
            ss << std::put_time(&tm_time, "%H:%M:%S");
            return ss.str();
        }();
        
        std::string logMessage = "[Client] " + timestamp + " - " + message;
        std::cout << logMessage << std::endl;
        
        if (logMessageHandler) {
            logMessageHandler(logMessage);
        }
    }
}

void FastUdpClient::DebugPacketBytes(const std::vector<uint8_t>& data) {
    if (data.empty()) {
        LogDebug("Empty packet", LogLevel::Verbose);
        return;
    }
    
    std::stringstream ss;
    ss << "Packet bytes (" << data.size() << "): " << std::endl;
    
    // Show first bytes as hexadecimal
    size_t bytesToShow = std::min<size_t>(32, data.size());
    for (size_t i = 0; i < bytesToShow; ++i) {
        ss << std::uppercase << std::hex << std::setw(2) << std::setfill('0') 
           << static_cast<int>(data[i]) << " ";
        if ((i + 1) % 16 == 0) {
            ss << std::endl;
        }
    }
    
    // Interpret first byte (type)
    ss << std::endl;
    ss << "Type (byte 0): " << static_cast<int>(data[0]) << std::endl;
    
    // If more than 1 byte
    if (data.size() > 1) {
        ss << "SessionId length (byte 1): " << static_cast<int>(data[1]) << std::endl;
        
        // If has SessionId
        if (data[1] > 0 && data.size() >= 2 + static_cast<size_t>(data[1])) {
            std::string sessionId(data.begin() + 2, data.begin() + 2 + data[1]);
            ss << "SessionId: '" << sessionId << "'" << std::endl;
            
            // If has data after sessionId
            size_t dataOffset = 2 + data[1];
            if (data.size() > dataOffset) {
                size_t dataLength = data.size() - dataOffset;
                ss << "Data (" << dataLength << " bytes)" << std::endl;
            }
        } else {
            // Data without sessionId
            size_t dataLength = data.size() - 1;
            ss << "Data without sessionId (" << dataLength << " bytes)" << std::endl;
        }
    }
    
    LogDebug(ss.str(), LogLevel::Verbose);
}

// Channel-related methods
void FastUdpClient::JoinChannel(const std::string& channelId) {
    if (!this->connected || this->sessionId.empty()) {
        throw std::runtime_error("Not connected to server. Wait for connection before joining channels.");
    }
    
    std::string message = "#JOIN:" + channelId;
    this->SendMessage(message);
    LogDebug("Requested to join channel: " + channelId, LogLevel::Basic);
}

void FastUdpClient::LeaveChannel(const std::string& channelId) {
    if (!this->connected || this->sessionId.empty()) {
        throw std::runtime_error("Not connected to server. Wait for connection before leaving channels.");
    }
    
    std::string message = "#LEAVE:" + channelId;
    this->SendMessage(message);
    LogDebug("Requested to leave channel: " + channelId, LogLevel::Basic);
    
    // Remove from local tracking
    HandleChannelStateChange(channelId, false);
}

void FastUdpClient::CreateChannel(const std::string& channelName, bool isPublic) {
    if (!this->connected || this->sessionId.empty()) {
        throw std::runtime_error("Not connected to server. Wait for connection before creating channels.");
    }
    
    std::string type = isPublic ? "public" : "private";
    std::string message = "#CREATE:" + channelName + ":" + type;
    this->SendMessage(message);
    LogDebug("Requested to create " + type + " channel: " + channelName, LogLevel::Basic);
}

void FastUdpClient::SendChannelMessage(const std::string& channelId, const std::string& message) {
    if (!this->connected || this->sessionId.empty()) {
        throw std::runtime_error("Not connected to server. Wait for connection before sending channel messages.");
    }
    
    std::string channelMessage = "#CHANNEL:" + channelId + ":" + message;
    this->SendMessage(channelMessage);
    LogDebug("Sent message to channel " + channelId, LogLevel::Basic);
}

void FastUdpClient::RequestChannelList() {
    if (!this->connected || this->sessionId.empty()) {
        throw std::runtime_error("Not connected to server. Wait for connection before requesting channel list.");
    }
    
    std::string message = "#CHANNELS";
    this->SendMessage(message);
    LogDebug("Requested channel list from server", LogLevel::Basic);
}

void FastUdpClient::HandleChannelStateChange(const std::string& channelId, bool joined) {
    std::lock_guard<std::mutex> lock(mutex);
    
    if (joined) {
        // Check if already in the channel
        auto it = std::find(channels.begin(), channels.end(), channelId);
        if (it == channels.end()) {
            channels.push_back(channelId);
            if (channelJoinedHandler) {
                channelJoinedHandler(channelId);
            }
        }
    } else {
        // Remove if in the channel
        auto it = std::find(channels.begin(), channels.end(), channelId);
        if (it != channels.end()) {
            channels.erase(it);
            if (channelLeftHandler) {
                channelLeftHandler(channelId);
            }
        }
    }
}

void FastUdpClient::Disconnect(const std::string& reason) {
    // Only try to disconnect if already connected
    if (connected && !sessionId.empty()) {
        try {
            // Send disconnect packet
            SendPacket(FastPacket::CreateDisconnect(reason));
            
            // Update state
            connected = false;
            
            // Stop ping timer
            StopPingTimer();
            
            // Log the disconnect
            LogDebug("Disconnected from server: " + reason, LogLevel::Basic);
            
            // Notify application
            if (disconnectedHandler) {
                disconnectedHandler(reason);
            }
        } catch (const std::exception& ex) {
            // Just log the error but still mark as disconnected
            LogDebug("Error sending disconnect packet: " + std::string(ex.what()), LogLevel::Critical);
            connected = false;
        }
    }
}

} // namespace FastUDP