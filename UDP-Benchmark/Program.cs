using FastUDP;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text;

namespace UDPBenchmark
{
    // Improved channel operations with binary protocol
    public static class ChannelOperations
    {
        // Send a join channel request using binary protocol instead of text
        public static void JoinChannel(FastUdpClient client, string channelId)
        {
            // Create a binary packet with ChannelJoin type and channel ID as data
            var packet = new FastPacket(EPacketType.ChannelJoin, System.Text.Encoding.UTF8.GetBytes(channelId));
            client.SendPacket(packet);
        }
        
        // Send a channel message using binary protocol
        public static void SendChannelMessage(FastUdpClient client, string channelId, string message)
        {
            //Console.WriteLine($"Sending channel message to {channelId}: {message}");
            // Format: channelId length (1 byte) + channelId + message
            byte[] channelIdBytes = System.Text.Encoding.UTF8.GetBytes(channelId);
            byte[] messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
            
            byte[] data = new byte[1 + channelIdBytes.Length + messageBytes.Length];
            data[0] = (byte)channelIdBytes.Length;
            
            Buffer.BlockCopy(channelIdBytes, 0, data, 1, channelIdBytes.Length);
            Buffer.BlockCopy(messageBytes, 0, data, 1 + channelIdBytes.Length, messageBytes.Length);
            
            var packet = new FastPacket(EPacketType.ChannelBroadcast, data);
            client.SendPacket(packet);
        }
    }
    
    class Program
    {
        // Configuration
        private const int NUM_CLIENTS = 100;
        private const string DEFAULT_SERVER_IP = "127.0.0.1";
        private const int DEFAULT_SERVER_PORT = 2593;
        private const int TEST_DURATION_SECONDS = 20; // Duração do teste em segundos
        private const int PARALLEL_SENDERS = 20; // Número de threads paralelas enviando
        private const int STATS_INTERVAL_MS = 1000; // Intervalo para estatísticas (1 segundo)
        
        // Batch sizes for connection
        private const int CONNECT_BATCH_SIZE = 50;
        private const int BATCH_DELAY_MS = 500;
        
        // Tracking
        private static int _connectedClients = 0;
        private static List<FastUdpClient> _clients = new List<FastUdpClient>();
        private static object _consoleLock = new object();
        private static ConcurrentDictionary<string, bool> _connectedSessionIds = new ConcurrentDictionary<string, bool>();
        
        // Metrics for performance benchmarking
        private static long _totalMessagesSent = 0;
        private static long _totalMessagesReceived = 0;
        private static long _lastMessagesSent = 0;
        private static long _lastMessagesReceived = 0;
        private static DateTime _testStartTime;
        private static volatile bool _continueSending = false; // Flag para controlar o envio
        private static ManualResetEvent _testCompleted = new ManualResetEvent(false);
        
        // Channel testing
        private static string _channelId = string.Empty;
        private static string _channelName = "BenchmarkTestChannel"; // Changed to avoid conflicts
        private static FastUdpClient _channelOwner = null;
        private static string _channelOwnerSessionId = string.Empty;
        private static ConcurrentDictionary<string, ConcurrentDictionary<int, bool>> _messageReceivedByClient = 
            new ConcurrentDictionary<string, ConcurrentDictionary<int, bool>>();
        private static bool _channelCreated = false;
        private static bool _waitingForChannelCreation = false;
        private static ManualResetEvent _channelCreatedEvent = new ManualResetEvent(false);
        private static int _messageCounter = 0;
        private static HashSet<string> _receivedMessageIds = new HashSet<string>();
        private static ConcurrentDictionary<int, int> _clientsJoinedChannel = new ConcurrentDictionary<int, int>();
        private static int _joinedClientsCount = 0;
        private static ManualResetEvent _allClientsJoined = new ManualResetEvent(false);
        private static bool _ownerConfirmed = false;
        private static bool _debugAllMessages = true; // Enable full message logging
        private static int _systemMessagesReceived = 0;

        static void Main(string[] args)
        {
            Console.WriteLine("UDP Benchmark starting...");
            
            // Parse command line arguments
            string serverIp = DEFAULT_SERVER_IP;
            int serverPort = DEFAULT_SERVER_PORT;
            
            if (args.Length > 0)
            {
                serverIp = args[0];
            }
            
            if (args.Length > 1 && int.TryParse(args[1], out int port))
            {
                serverPort = port;
            }
            
            Console.WriteLine($"Target server: {serverIp}:{serverPort}");
            Console.WriteLine($"Creating {NUM_CLIENTS} UDP clients...");

            try
            {
                // Create and connect all clients
                for (int i = 0; i < NUM_CLIENTS; i++)
                {
                    int clientId = i;
                    var client = new FastUdpClient(serverIp, serverPort);
                    
                    // Initialize message tracking for this client
                    _messageReceivedByClient[clientId.ToString()] = new ConcurrentDictionary<int, bool>();
                    
                    // Configure client
                    client.DebugMode = true; // Enable debug to see all messages
                    client.LoggingLevel = FastUdpClient.LogLevel.Basic;
                    
                    // Specifically subscribe to the ChannelMessageReceived event
                    client.ChannelMessageReceived += (sender, channelMessage) =>
                    {
                        // Contar todas as mensagens recebidas para o benchmark
                        var match = Regex.Match(channelMessage, @"Test message (\d+)");
                        if (match.Success && clientId != 0) // Não contar mensagens no cliente que envia
                        {
                            // Incrementar contador global de mensagens recebidas
                            Interlocked.Increment(ref _totalMessagesReceived);
                            
                            // Opcional: registrar qual mensagem foi recebida para análise detalhada
                            if (int.TryParse(match.Groups[1].Value, out int msgNum))
                            {
                                _messageReceivedByClient[clientId.ToString()].TryAdd(msgNum, true);
                            }
                        }
                    };
                    
                    client.Connected += (sender, sessionId) => 
                    {
                        // Only count a session ID once to prevent duplicate counts
                        if (_connectedSessionIds.TryAdd(sessionId, true))
                        {
                            int count = Interlocked.Increment(ref _connectedClients);
                            
                            // Ensure we don't report more than our actual client count
                            if (count <= NUM_CLIENTS)
                            {
                                LogMessage($"Connected clients: {count}/{NUM_CLIENTS}");
                            }
                            
                            // Only designate first client as channel owner
                            if (_channelOwner == null && sender is FastUdpClient ownerClient && clientId == 0)
                            {
                                LogMessage($"Designating client {clientId} (session {sessionId}) as channel owner");
                                _channelOwner = ownerClient;
                                _channelOwnerSessionId = sessionId;
                                
                                // Create the channel after a short delay to ensure connection is stable
                                Task.Run(async () => 
                                {
                                    await Task.Delay(2000);
                                    CreateChannel(ownerClient);
                                });
                            }
                        }
                    };
                    
                    client.Disconnected += (sender, reason) => 
                    {
                        // Get the session ID from the sender
                        if (sender is FastUdpClient senderClient && senderClient.SessionId != null)
                        {
                            // Only decrement if we had counted this session
                            if (_connectedSessionIds.TryRemove(senderClient.SessionId, out _))
                            {
                                Interlocked.Decrement(ref _connectedClients);
                                LogMessage($"Client {clientId} disconnected: {reason}");
                            }
                        }
                    };
                    
                    // Subscribe to ChannelJoined event for binary protocol
                    client.ChannelJoined += (sender, channelId) => 
                    {
                        LogMessage($"Client {clientId} joined channel {channelId} via binary protocol");
                        
                        // Only count once per client
                        if (_clientsJoinedChannel.TryAdd(clientId, 1))
                        {
                            int joinedCount = Interlocked.Increment(ref _joinedClientsCount);
                            LogMessage($"Client {clientId} confirmed joined to channel (Total: {joinedCount}/{NUM_CLIENTS - 1})");
                            
                            // Signal when all clients have joined
                            if (joinedCount >= NUM_CLIENTS - 1) // Exclude owner
                            {
                                LogMessage($"All non-owner clients have joined the channel!");
                                _allClientsJoined.Set();
                            }
                        }
                    };
                    
                    // We can remove or comment out the old check in the MessageReceived event
                    // Since that's now handled by the ChannelJoined event
                    client.MessageReceived += (sender, message) => 
                    {
                        // Log ALL messages for debugging if enabled
                        if (_debugAllMessages)
                        {
                            //LogMessage($"Client {clientId} RAW RECEIVED: '{message}'");
                        }
                        
                        // Check for channel creation confirmation 
                        if (_waitingForChannelCreation && 
                            message.Contains($"Created public channel: {_channelName}") && 
                            sender is FastUdpClient && 
                            sender == _channelOwner)
                        {
                            // Extract channel ID from the message
                            var match = Regex.Match(message, $"Created public channel: {_channelName} \\(ID: ([a-z0-9]+)\\)");
                            if (match.Success && match.Groups.Count > 1)
                            {
                                _channelId = match.Groups[1].Value;
                                LogMessage($"Channel created with ID: {_channelId}");
                                _channelCreated = true;
                                _waitingForChannelCreation = false;
                                _channelCreatedEvent.Set();
                                
                                // Owner is automatic, we just need to verify
                                _ownerConfirmed = true;
                                
                                // Allow some time for the channel to be fully registered
                                Task.Run(async () => 
                                {
                                    await Task.Delay(3000);
                                    
                                    // Have other clients join this channel
                                    foreach (var client in _clients)
                                    {
                                        if (client != _channelOwner)
                                        {
                                            JoinChannel(client);
                                        }
                                    }
                                });
                            }
                        }
                        
                        // Count system messages (for diagnostics)
                        if (message.StartsWith("[") && !message.Contains("Test message"))
                        {
                            Interlocked.Increment(ref _systemMessagesReceived);
                        }
                        
                        // Log all messages for debugging
                        //LogMessage($"Client {clientId} received: {message}");
                    };
                    
                    // Store client reference
                    _clients.Add(client);
                }
                
                // Connect all clients
                Console.WriteLine("Connecting all clients to the server...");

                ConnectClients(_clients, serverIp, serverPort);
                
                // Wait for connections to establish
                Console.WriteLine("Waiting for connections to establish...");
                
                // Wait up to 30 seconds for connections
                for (int i = 0; i < 30; i++)
                {
                    int currentConnected = Math.Min(_connectedClients, NUM_CLIENTS);
                    Console.WriteLine($"Connected: {currentConnected}/{NUM_CLIENTS}");
                    
                    if (currentConnected >= NUM_CLIENTS)
                    {
                        break;
                    }
                    
                    Thread.Sleep(1000);
                }
                
                Console.WriteLine("\nBenchmark initialization complete.");
                Console.WriteLine($"Successfully connected {Math.Min(_connectedClients, NUM_CLIENTS)} clients out of {NUM_CLIENTS}");
                
                // Wait for channel to be created (max 20 seconds)
                if (_channelCreatedEvent.WaitOne(20000))
                {
                    LogMessage("Channel successfully created, waiting for clients to join...");
                    
                    // Wait for all clients to join the channel
                    if (_allClientsJoined.WaitOne(15000))
                    {
                        LogMessage("All non-owner clients have successfully joined the channel!");
                        
                        // Additional wait to ensure channel operations are fully established
                        Thread.Sleep(2000);
                        
                        // Start the channel message sender if we have a channel owner
                        if (_channelOwner != null && !string.IsNullOrEmpty(_channelId))
                        {                            
                            // Run the performance test
                            LogMessage("==== STARTING UDP PERFORMANCE TEST ====");
                            LogMessage($"Duration: {TEST_DURATION_SECONDS} seconds");
                            LogMessage($"Connected clients: {_connectedClients}");
                            LogMessage($"Test channel: {_channelId}");
                            LogMessage("=========================================");
                            
                            // Run the test sending messages at high speed
                            SendChannelMessages();
                            
                            // Display final statistics
                            PrintPerformanceStatistics();
                        }
                        else
                        {
                            LogMessage("Failed to establish channel owner or create channel", true);
                        }
                    }
                    else
                    {
                        LogMessage("Timed out waiting for all clients to join the channel", true);
                        // Print which clients joined and which didn't
                        for (int i = 0; i < NUM_CLIENTS; i++)
                        {
                            bool joined = _clientsJoinedChannel.ContainsKey(i);
                            //LogMessage($"Client {i} joined channel: {joined}");
                        }
                    }
                }
                else
                {
                    LogMessage("Timed out waiting for channel creation", true);
                }
                
                Console.WriteLine("Press Enter to exit...");
                Console.ReadLine();
                
                // Clean up
                _clients.Clear();
                GC.Collect();
                
                Console.WriteLine("Benchmark shutdown complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
            }
        }
        
        static void CreateChannel(FastUdpClient owner)
        {
            try
            {
                // Create a public channel with a timestamp to avoid conflicts
                _channelName = $"BenchmarkChannel_{DateTime.Now:HHmmss}";
                string createCommand = $"#CREATE:{_channelName}:public";
                LogMessage($"Creating channel with command: {createCommand}");
                _waitingForChannelCreation = true;
                owner.SendMessage(createCommand);
                
                // The rest will be handled in the MessageReceived event handler
            }
            catch (Exception ex)
            {
                LogMessage($"Error creating channel: {ex.Message}", true);
            }
        }
        
        static void JoinChannel(FastUdpClient client)
        {
            try
            {
                if (!string.IsNullOrEmpty(_channelId))
                {
                    // Use the more efficient binary join method
                    ChannelOperations.JoinChannel(client, _channelId);
                    //LogMessage($"Client {client.SessionId} joining channel: {_channelId}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error joining channel: {ex.Message}", true);
            }
        }
        
        static void SendChannelMessages()
        {
            try
            {
                _testStartTime = DateTime.Now;
                int messageCounter = 0;
                _continueSending = true;
                
                LogMessage($"Starting performance test for {TEST_DURATION_SECONDS} seconds...");
                LogMessage($"Using {PARALLEL_SENDERS} parallel sender threads");
                
                // Start timer to end the test after the defined duration
                Timer timerEndTest = new Timer(_ => 
                {
                    _continueSending = false;
                    LogMessage("Ending message sending phase");
                    
                    // Wait for in-transit messages to be delivered (2 seconds)
                    LogMessage("Waiting 2 seconds for in-transit messages to arrive...");
                    Thread.Sleep(2000);
                    _testCompleted.Set();
                }, null, TEST_DURATION_SECONDS * 1000, Timeout.Infinite);
                
                // Create multiple threads to send messages simultaneously
                Task[] tasks = new Task[PARALLEL_SENDERS];
                for (int t = 0; t < PARALLEL_SENDERS; t++)
                {
                    int threadId = t;
                    tasks[t] = Task.Run(() => 
                    {
                        Random rnd = new Random(threadId); // Different seed for each thread
                        while (_continueSending)
                        {
                            try
                            {
                                // Use message ID based on counter with thread ID
                                int msgId = Interlocked.Increment(ref messageCounter);
                                string message = $"Test message {msgId}";
                                
                                // Send the message to the channel
                                ChannelOperations.SendChannelMessage(_channelOwner, _channelId, message);
                                
                                // Increment sent message counter
                                Interlocked.Increment(ref _totalMessagesSent);
                                
                                // Small delay to prevent system overload
                                Thread.Sleep(rnd.Next(1, 5));
                            }
                            catch (Exception ex)
                            {
                                LogMessage($"Error sending from thread {threadId}: {ex.Message}", true);
                                Thread.Sleep(100); // Longer pause in case of error
                            }
                        }
                    });
                }
                
                // Start thread to display real-time statistics
                Task statsTask = Task.Run(() => 
                {
                    while (_continueSending)
                    {
                        Thread.Sleep(STATS_INTERVAL_MS);
                        
                        // Calculate messages per second (last interval)
                        long currentSent = _totalMessagesSent;
                        long currentReceived = _totalMessagesReceived;
                        
                        long sentPerSecond = currentSent - _lastMessagesSent;
                        long receivedPerSecond = currentReceived - _lastMessagesReceived;
                        
                        _lastMessagesSent = currentSent;
                        _lastMessagesReceived = currentReceived;
                        
                        // Display real-time statistics
                        LogMessage($"Messages sent: {currentSent} ({sentPerSecond}/s) | " +
                                   $"Received: {currentReceived} ({receivedPerSecond}/s) | " +
                                   $"Rate: {(currentReceived > 0 ? (double)currentReceived / currentSent * 100 : 0):F2}%");
                    }
                });
                
                // Wait for test completion
                _testCompleted.WaitOne();
                
                // Wait for all threads to complete
                Task.WaitAll(tasks);
                
                // Calculate final statistics
                TimeSpan duration = DateTime.Now - _testStartTime;
                double messagesPerSecond = _totalMessagesSent / duration.TotalSeconds;
                double receivedPerSecond = _totalMessagesReceived / duration.TotalSeconds;
                
                LogMessage($"Test completed in {duration.TotalSeconds:F1} seconds");
                LogMessage($"Total messages sent: {_totalMessagesSent}");
                LogMessage($"Send rate: {messagesPerSecond:F2} messages/second");
                LogMessage($"Receive rate: {receivedPerSecond:F2} messages/second");
                
                // Calculate expected delivery ratio 
                // For broadcast messages, each sent message can be received by (NUM_CLIENTS - 1) clients
                double actualMessagesSent = _totalMessagesSent;
                double maxPossibleReceptions = actualMessagesSent * (NUM_CLIENTS - 1); // Excluding sender
                double deliveryRatio = (_totalMessagesReceived > 0 && maxPossibleReceptions > 0) 
                    ? (double)_totalMessagesReceived / maxPossibleReceptions * 100 
                    : 0;
                    
                LogMessage($"Delivery ratio: {deliveryRatio:F2}%");
            }
            catch (Exception ex)
            {
                LogMessage($"Error in performance test: {ex.Message}", true);
            }
        }
        
        static void PrintPerformanceStatistics()
        {
            // Calculate total test duration
            TimeSpan testDuration = DateTime.Now - _testStartTime;
            
            LogMessage("\n===== UDP PERFORMANCE REPORT =====");
            LogMessage($"Total test duration: {testDuration.TotalSeconds:F2} seconds");
            LogMessage($"Number of clients: {NUM_CLIENTS}");
            LogMessage($"Server: {DEFAULT_SERVER_IP}:{DEFAULT_SERVER_PORT}");
            LogMessage($"Parallel sender threads: {PARALLEL_SENDERS}");
            
            // Volume metrics
            LogMessage("\n--- MESSAGE VOLUME ---");
            LogMessage($"Total messages sent: {_totalMessagesSent:N0}");
            LogMessage($"Total message receptions: {_totalMessagesReceived:N0}");
            
            // Throughput metrics
            double msgsPerSecond = _totalMessagesSent / testDuration.TotalSeconds;
            double receptionPerSecond = _totalMessagesReceived / testDuration.TotalSeconds;
            
            LogMessage("\n--- THROUGHPUT ---");
            LogMessage($"Messages sent per second: {msgsPerSecond:N2}");
            LogMessage($"Receptions per second: {receptionPerSecond:N2}");
            
            // Calculate expected vs. actual delivery
            int nonOwnerClients = NUM_CLIENTS - 1; // Excluding the channel owner
            long expectedReceptions = _totalMessagesSent * nonOwnerClients;
            double deliveryRate = (double)_totalMessagesReceived / expectedReceptions * 100;
            
            LogMessage("\n--- RELIABILITY ---");
            LogMessage($"Maximum possible receptions: {expectedReceptions:N0}");
            LogMessage($"Actual delivery rate: {deliveryRate:F2}%");
            LogMessage($"Loss rate: {100 - deliveryRate:F2}%");
            
            // Per-client efficiency
            double msgsPerClientPerSecond = receptionPerSecond / nonOwnerClients;
            
            LogMessage("\n--- EFFICIENCY ---");
            LogMessage($"Messages received per client per second: {msgsPerClientPerSecond:F2}");
            LogMessage("======================================");
        }
        
        static void PrintChannelStatistics()
        {
            LogMessage("===== Channel Test Statistics =====");
            LogMessage($"Channel Name: {_channelName}");
            LogMessage($"Channel ID: {_channelId}");
            LogMessage($"Channel Owner: {_channelOwnerSessionId}");
            LogMessage($"Owner Confirmed: {_ownerConfirmed}");
            LogMessage($"Messages Sent to Channel: {_totalMessagesSent}");
            LogMessage($"Total Message Receptions: {_totalMessagesReceived}");
            LogMessage($"System Messages Received: {_systemMessagesReceived}");
            
            // Calculate expected messages (each message should be received by every non-owner client)
            int nonOwnerClients = NUM_CLIENTS - 1; // Exclude the owner
            long expectedReceptions = _totalMessagesSent * nonOwnerClients;
            
            if (expectedReceptions > 0)
            {
                double receiveRate = (double)_totalMessagesReceived / expectedReceptions * 100;
                LogMessage($"Delivery Rate: {_totalMessagesReceived}/{expectedReceptions} = {receiveRate:F2}%");
            }
            
            LogMessage("==================================");
        }
        
        static void PrintClientReceptionDetails()
        {
            LogMessage("\n===== Client Reception Details =====");
            
            // Print per-client stats
            for (int clientId = 0; clientId < NUM_CLIENTS; clientId++)
            {
                if (clientId == 0)
                {
                    // Skip owner
                    LogMessage($"Client {clientId}: Owner/Sender (not expected to receive messages)");
                    continue;
                }
                
                // Get this client's received messages
                if (_messageReceivedByClient.TryGetValue(clientId.ToString(), out var receivedMsgs))
                {
                    int count = receivedMsgs.Count;
                    double rate = (double)count / _totalMessagesSent * 100;
                    LogMessage($"Client {clientId}: Received {count}/{_totalMessagesSent} messages ({rate:F2}%)");
                    
                    // List missing messages if any
                    if (count < _totalMessagesSent)
                    {
                        List<int> missing = new List<int>();
                        for (int i = 0; i < (int)Math.Min(_totalMessagesSent, int.MaxValue); i++)
                        {
                            if (!receivedMsgs.ContainsKey(i))
                            {
                                missing.Add(i);
                            }
                        }
                        if (missing.Count > 0)
                        {
                            LogMessage($"  Missing messages: {string.Join(", ", missing)}");
                        }
                    }
                }
            }
            
            LogMessage("===================================");
        }
        
        static void LogMessage(string message, bool isError = false)
        {
            lock (_consoleLock)
            {
                if (isError)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                }
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                Console.ResetColor();
            }
        }
        
        // Update the client connection method to connect in batches 
        static void ConnectClients(List<FastUdpClient> clients, string serverIp, int serverPort)
        {
            // Connect clients in batches to prevent overwhelming the server
            int totalBatches = (clients.Count + CONNECT_BATCH_SIZE - 1) / CONNECT_BATCH_SIZE;
            
            for (int batch = 0; batch < totalBatches; batch++)
            {
                int startIdx = batch * CONNECT_BATCH_SIZE;
                int endIdx = Math.Min(startIdx + CONNECT_BATCH_SIZE, clients.Count);
                
                LogMessage($"Connecting batch {batch+1}/{totalBatches} (clients {startIdx}-{endIdx-1})");
                
                // Connect this batch in parallel
                Parallel.For(startIdx, endIdx, i =>
                {
                    try
                    {
                        clients[i].Connect();
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Error connecting client {i}: {ex.Message}", true);
                    }
                });
                
                // Wait between batches
                if (batch < totalBatches - 1)
                {
                    Thread.Sleep(BATCH_DELAY_MS);
                }
            }
        }
    }
}
