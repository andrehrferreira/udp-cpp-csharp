using System.Net;

namespace FastUDP {
    public class FastUdpConnection
    {
        // Informações básicas de conexão
        public IPEndPoint RemoteEndPoint { get; private set; }
        public string Host => RemoteEndPoint.Address.ToString();
        public int Port => RemoteEndPoint.Port;
        
        // Estatísticas de uso
        public DateTime ConnectedTime { get; private set; }
        public DateTime LastActiveTime { get; private set; }
        public int BytesSent { get; private set; }
        public int BytesReceived { get; private set; }
        public int PacketsSent { get; private set; }
        public int PacketsReceived { get; private set; }
        
        public FastUdpConnection(IPEndPoint remoteEndPoint)
        {
            RemoteEndPoint = remoteEndPoint;
            ConnectedTime = DateTime.Now;
            LastActiveTime = DateTime.Now;
            BytesSent = 0;
            BytesReceived = 0;
            PacketsSent = 0;
            PacketsReceived = 0;
        }
        
        // Métodos para atualizar estatísticas
        public void UpdateLastActiveTime()
        {
            LastActiveTime = DateTime.Now;
        }
        
        public void AddSentBytes(int bytes)
        {
            BytesSent += bytes;
            PacketsSent++;
            UpdateLastActiveTime();
        }
        
        public void AddReceivedBytes(int bytes)
        {
            BytesReceived += bytes;
            PacketsReceived++;
            UpdateLastActiveTime();
        }
        
        // Métodos para obter informações estatísticas
        public TimeSpan GetUptime()
        {
            return DateTime.Now - ConnectedTime;
        }
        
        public TimeSpan GetIdleTime()
        {
            return DateTime.Now - LastActiveTime;
        }
        
        public double GetBytesPerSecond()
        {
            double totalSeconds = GetUptime().TotalSeconds;
            if (totalSeconds <= 0) return 0;
            return BytesSent / totalSeconds;
        }
        
        public override string ToString()
        {
            return $"{Host}:{Port} - Up: {GetUptime().ToString(@"hh\:mm\:ss")} - Sent: {BytesSent} bytes - Rcvd: {BytesReceived} bytes";
        }
    }
}
