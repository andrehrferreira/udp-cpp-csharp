namespace FastUDP
{
    public enum EPacketType : byte
    {
        // Pacotes de controle (0-9)
        Ping = 0,
        Pong = 1,
        Connect = 2,
        ConnectResponse = 3,
        Reconnect = 4,
        Disconnect = 5,
        Shutdown = 6,
        
        // Pacotes de dados (10+)
        Message = 10,
        BinaryData = 11,
        Ack = 12,
        Error = 13
    }
}
