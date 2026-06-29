namespace CnoawaProtocol
{
    public enum MessageType : byte
    {
        // 认证
        Auth = 0x00,
        AuthResult = 0x01,

        // 房间管理
        CreateRoom = 0x02,
        CreateRoomResult = 0x03,
        JoinRoom = 0x04,
        JoinRoomResult = 0x05,
        PlayerJoined = 0x06,
        PlayerLeft = 0x07,
        LeaveRoom = 0x08,
        Kick = 0x09,
        RoomSnapshot = 0x0A,

        // 房间配置与状态
        RoomConfig = 0x10,
        Ready = 0x11,
        ReadyStatus = 0x12,
        StateChange = 0x13,

        // 选曲与下载
        EnterChartSelect = 0x20,
        ChartVote = 0x21,
        VoteStatus = 0x22,
        VoteChange = 0x23,
        AllVoted = 0x24,
        ChartResult = 0x25,
        DownloadProgress = 0x26,
        DownloadComplete = 0x27,
        DownloadStatus = 0x28,
        AllReady = 0x29,

        // 游玩同步
        Countdown = 0x30,
        GameStart = 0x31,
        ComboUpdate = 0x32,
        PlayerSync = 0x33,
        SkillCast = 0x34,
        SkillEffect = 0x35,
        PlayerFinished = 0x36,

        // 结算
        Results = 0x40,
        PlayAgain = 0x41,
        BackToLobby = 0x42,

        // 心跳
        Ping = 0xF0,
        Pong = 0xF1,
        Error = 0xFF
    }

    public enum RoomState : byte
    {
        Lobby,
        ChartSelect,
        Downloading,
        Playing,
        Results
    }

    public enum RoomType : byte
    {
        Competitive,
        Casual
    }

    public enum SkillId : byte
    {
        None = 0,
        NoteScramble = 1,
        ScreenFlash = 2,
        SpeedChange = 3,
        PerfectWindow = 11,
        ScoreBoost = 12,
        ShieldMiss = 13,
        ComboBreak = 21,
        ScoreSteal = 22
    }
}
