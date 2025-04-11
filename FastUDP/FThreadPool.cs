using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FastUDP 
{
    /// <summary>
    /// ThreadPool especializado para processar operações de rede UDP de forma eficiente
    /// </summary>
    public class FastThreadPool : IDisposable
    {
        // Configuração do pool de threads
        private readonly int _receiveThreadCount;
        private readonly int _sendThreadCount;
        
        // Filas para tarefas pendentes
        private readonly BlockingCollection<ReceiveTask> _receiveQueue;
        private readonly BlockingCollection<SendTask> _sendQueue;
        
        // Threads de trabalho
        private readonly List<Thread> _receiveThreads;
        private readonly List<Thread> _sendThreads;
        
        // Controle de estado
        private bool _isRunning;
        private readonly CancellationTokenSource _cts;
        
        // Delegados para processamento de pacotes
        public delegate void PacketProcessor(IPEndPoint remoteEndPoint, byte[] packetData);
        private readonly PacketProcessor _packetProcessor;
        
        // Estatísticas
        private long _totalPacketsProcessed;
        private long _totalPacketsSent;
        private long _queueOverflows;
        
        /// <summary>
        /// Total de pacotes processados desde o início
        /// </summary>
        public long TotalPacketsProcessed => Interlocked.Read(ref _totalPacketsProcessed);
        
        /// <summary>
        /// Total de pacotes enviados desde o início
        /// </summary>
        public long TotalPacketsSent => Interlocked.Read(ref _totalPacketsSent);
        
        /// <summary>
        /// Número de vezes que uma fila transbordou (queue overflow)
        /// </summary>
        public long QueueOverflows => Interlocked.Read(ref _queueOverflows);
        
        /// <summary>
        /// Número atual de pacotes aguardando processamento
        /// </summary>
        public int ReceiveQueueSize => _receiveQueue.Count;
        
        /// <summary>
        /// Número atual de pacotes aguardando envio
        /// </summary>
        public int SendQueueSize => _sendQueue.Count;
        
        /// <summary>
        /// Cria uma nova instância do pool de threads UDP
        /// </summary>
        /// <param name="receiveThreadCount">Número de threads para processamento de pacotes recebidos</param>
        /// <param name="sendThreadCount">Número de threads para envio de pacotes</param>
        /// <param name="packetProcessor">Callback para processar pacotes recebidos</param>
        public FastThreadPool(int receiveThreadCount, int sendThreadCount, PacketProcessor packetProcessor)
        {
            _receiveThreadCount = receiveThreadCount;
            _sendThreadCount = sendThreadCount;
            _packetProcessor = packetProcessor ?? throw new ArgumentNullException(nameof(packetProcessor));
            
            // Inicializar filas com limite para evitar consumo excessivo de memória
            const int maxQueueSize = 10000;
            _receiveQueue = new BlockingCollection<ReceiveTask>(new ConcurrentQueue<ReceiveTask>(), maxQueueSize);
            _sendQueue = new BlockingCollection<SendTask>(new ConcurrentQueue<SendTask>(), maxQueueSize);
            
            // Inicializar listas de threads
            _receiveThreads = new List<Thread>(_receiveThreadCount);
            _sendThreads = new List<Thread>(_sendThreadCount);
            
            // Inicializar controle de estado
            _isRunning = false;
            _cts = new CancellationTokenSource();
            
            // Inicializar contadores
            _totalPacketsProcessed = 0;
            _totalPacketsSent = 0;
            _queueOverflows = 0;
        }
        
        /// <summary>
        /// Inicializa e inicia as threads do pool
        /// </summary>
        public void Start()
        {
            if (_isRunning)
                return;
                
            _isRunning = true;
            
            // Inicializar threads de recebimento
            for (int i = 0; i < _receiveThreadCount; i++)
            {
                var thread = new Thread(ReceiveThreadWorker)
                {
                    Name = $"UDP-Receive-{i}",
                    IsBackground = true
                };
                _receiveThreads.Add(thread);
                thread.Start();
            }
            
            // Inicializar threads de envio
            for (int i = 0; i < _sendThreadCount; i++)
            {
                var thread = new Thread(SendThreadWorker)
                {
                    Name = $"UDP-Send-{i}",
                    IsBackground = true
                };
                _sendThreads.Add(thread);
                thread.Start();
            }
            
            Console.WriteLine($"[ThreadPool] Started with {_receiveThreadCount} receive threads and {_sendThreadCount} send threads");
        }
        
        /// <summary>
        /// Enfileira um pacote recebido para processamento
        /// </summary>
        /// <param name="remoteEndPoint">Endpoint de origem do pacote</param>
        /// <param name="packetData">Dados recebidos</param>
        /// <returns>True se enfileirado com sucesso, False caso contrário</returns>
        public bool EnqueuePacketForProcessing(IPEndPoint remoteEndPoint, byte[] packetData)
        {
            if (!_isRunning)
                return false;
                
            var task = new ReceiveTask(remoteEndPoint, packetData);
            
            try
            {
                // Tenta adicionar com timeout para evitar bloqueio indefinido
                return _receiveQueue.TryAdd(task, 10);
            }
            catch (InvalidOperationException) // Fila completa ou cancelada
            {
                Interlocked.Increment(ref _queueOverflows);
                return false;
            }
        }
        
        /// <summary>
        /// Enfileira um pacote para envio
        /// </summary>
        /// <param name="socket">Socket a ser usado para envio</param>
        /// <param name="endpoint">Endpoint de destino</param>
        /// <param name="data">Dados a serem enviados</param>
        /// <returns>True se enfileirado com sucesso, False caso contrário</returns>
        public bool EnqueuePacketForSending(System.Net.Sockets.Socket socket, EndPoint endpoint, byte[] data)
        {
            if (!_isRunning)
                return false;
                
            var task = new SendTask(socket, endpoint, data);
            
            try
            {
                // Tenta adicionar com timeout para evitar bloqueio indefinido
                return _sendQueue.TryAdd(task, 10);
            }
            catch (InvalidOperationException) // Fila completa ou cancelada
            {
                Interlocked.Increment(ref _queueOverflows);
                return false;
            }
        }
        
        /// <summary>
        /// Enfileira múltiplos pacotes para broadcast
        /// </summary>
        /// <param name="socket">Socket a ser usado para envio</param>
        /// <param name="targets">Lista de endpoints alvo</param>
        /// <param name="data">Dados a serem enviados</param>
        /// <returns>Número de pacotes enfileirados com sucesso</returns>
        public int EnqueueBroadcast(System.Net.Sockets.Socket socket, IEnumerable<EndPoint> targets, byte[] data)
        {
            if (!_isRunning)
                return 0;
                
            int count = 0;
            foreach (var target in targets)
            {
                if (EnqueuePacketForSending(socket, target, data))
                    count++;
            }
            
            return count;
        }
        
        // Thread worker para processamento de pacotes recebidos
        private void ReceiveThreadWorker()
        {
            while (_isRunning && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    // Tentar pegar uma tarefa da fila com timeout para permitir checagem de cancelamento
                    if (_receiveQueue.TryTake(out ReceiveTask task, 100))
                    {
                        try
                        {
                            // Processar o pacote usando o callback fornecido
                            _packetProcessor(task.RemoteEndPoint, task.PacketData);
                            Interlocked.Increment(ref _totalPacketsProcessed);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ReceiveThread] Error processing packet: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelamento normal
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ReceiveThread] Unexpected error: {ex.Message}");
                    // Pequena pausa para evitar 100% CPU em caso de erros contínuos
                    Thread.Sleep(10);
                }
            }
        }
        
        // Thread worker para envio de pacotes
        private void SendThreadWorker()
        {
            while (_isRunning && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    // Tentar pegar uma tarefa da fila com timeout para permitir checagem de cancelamento
                    if (_sendQueue.TryTake(out SendTask task, 100))
                    {
                        try
                        {
                            // Enviar o pacote
                            task.Socket.SendTo(task.Data, task.Endpoint);
                            Interlocked.Increment(ref _totalPacketsSent);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SendThread] Error sending packet: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelamento normal
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SendThread] Unexpected error: {ex.Message}");
                    // Pequena pausa para evitar 100% CPU em caso de erros contínuos
                    Thread.Sleep(10);
                }
            }
        }
        
        /// <summary>
        /// Para todas as threads e limpa recursos
        /// </summary>
        public void Stop()
        {
            if (!_isRunning)
                return;
                
            _isRunning = false;
            
            // Cancelar todas as operações pendentes
            _cts.Cancel();
            
            // Limpar filas
            _receiveQueue.CompleteAdding();
            _sendQueue.CompleteAdding();
            
            // Aguardar threads com timeout para não bloquear indefinidamente
            const int threadJoinTimeoutMs = 1000;
            
            foreach (var thread in _receiveThreads)
            {
                if (!thread.Join(threadJoinTimeoutMs))
                {
                    Console.WriteLine($"[ThreadPool] Receive thread {thread.Name} did not exit gracefully");
                }
            }
            
            foreach (var thread in _sendThreads)
            {
                if (!thread.Join(threadJoinTimeoutMs))
                {
                    Console.WriteLine($"[ThreadPool] Send thread {thread.Name} did not exit gracefully");
                }
            }
            
            // Limpar coleções
            _receiveThreads.Clear();
            _sendThreads.Clear();
            
            Console.WriteLine($"[ThreadPool] Stopped - Processed: {TotalPacketsProcessed}, Sent: {TotalPacketsSent}");
        }
        
        /// <summary>
        /// Libera recursos utilizados pelo pool
        /// </summary>
        public void Dispose()
        {
            Stop();
            
            _receiveQueue.Dispose();
            _sendQueue.Dispose();
            _cts.Dispose();
        }
        
        /// <summary>
        /// Estrutura que representa uma tarefa de processamento de pacote recebido
        /// </summary>
        private readonly struct ReceiveTask
        {
            public IPEndPoint RemoteEndPoint { get; }
            public byte[] PacketData { get; }
            
            public ReceiveTask(IPEndPoint remoteEndPoint, byte[] packetData)
            {
                RemoteEndPoint = remoteEndPoint;
                PacketData = packetData;
            }
        }
        
        /// <summary>
        /// Estrutura que representa uma tarefa de envio de pacote
        /// </summary>
        private readonly struct SendTask
        {
            public System.Net.Sockets.Socket Socket { get; }
            public EndPoint Endpoint { get; }
            public byte[] Data { get; }
            
            public SendTask(System.Net.Sockets.Socket socket, EndPoint endpoint, byte[] data)
            {
                Socket = socket;
                Endpoint = endpoint;
                Data = data;
            }
        }
    }
} 