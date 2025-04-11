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
        
        // Channel-related packets (7-9)
        ChannelJoin = 7,
        ChannelJoinConfirm = 8,
        ChannelBroadcast = 9,
        
        // Pacotes de dados (10+)
        Message = 10,
        BinaryData = 11,
        Ack = 12,
        Error = 13
    }
}
