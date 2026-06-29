using MemoryPack;

namespace CnoawaProtocol
{
    // === 认证 ===

    [MemoryPackable]
    public partial class AuthMessage
    {
        public string Token { get; set; } = "";
        public byte ProtocolVersion { get; set; } = 1;
    }

    [MemoryPackable]
    public partial class AuthResultMessage
    {
        public bool Success { get; set; }
        public int UserId { get; set; }
        public byte PlayerId { get; set; }
        public string Reason { get; set; } = "";
    }

    // === 房间管理 ===

    [MemoryPackable]
    public partial class CreateRoomMessage
    {
        public int RoomId { get; set; }
        public string RoomName { get; set; } = "";
        public int MaxPlayers { get; set; } = 8;
        public bool IsPrivate { get; set; }
        public string? Password { get; set; }
    }

    [MemoryPackable]
    public partial class CreateRoomResultMessage
    {
        public bool Success { get; set; }
        public string Reason { get; set; } = "";
    }

    [MemoryPackable]
    public partial class JoinRoomMessage
    {
        public int RoomId { get; set; }
        public string? Password { get; set; }
    }

    [MemoryPackable]
    public partial class JoinRoomResultMessage
    {
        public bool Success { get; set; }
        public string Reason { get; set; } = "";
        public PlayerInfo[]? Players { get; set; }
        public byte State { get; set; }
        public byte RoomType { get; set; }
    }

    [MemoryPackable]
    public partial class PlayerJoinedMessage
    {
        public byte PlayerId { get; set; }
        public int UserId { get; set; }
    }

    [MemoryPackable]
    public partial class PlayerLeftMessage
    {
        public byte PlayerId { get; set; }
        public string Reason { get; set; } = "";
    }

    [MemoryPackable]
    public partial class KickMessage
    {
        public byte PlayerId { get; set; }
    }

    [MemoryPackable]
    public partial class RoomSnapshotMessage
    {
        public byte State { get; set; }
        public byte RoomType { get; set; }
        public byte HostPlayerId { get; set; }
        public byte LocalPlayerId { get; set; }
        public SnapshotPlayer[]? Players { get; set; }
        public int? SelectedLevelId { get; set; }
        public string? SelectedLevelName { get; set; }
    }

    [MemoryPackable]
    public partial class SnapshotPlayer
    {
        public byte PlayerId { get; set; }
        public int UserId { get; set; }
        public bool IsReady { get; set; }
        public float DownloadProgress { get; set; }
    }

    // === 房间配置与状态 ===

    [MemoryPackable]
    public partial class RoomConfigMessage
    {
        public byte RoomType { get; set; }
        public byte MaxPlayers { get; set; }
    }

    [MemoryPackable]
    public partial class ReadyMessage
    {
        public bool IsReady { get; set; }
    }

    [MemoryPackable]
    public partial class ReadyStatusMessage
    {
        public PlayerReadyInfo[]? Players { get; set; }
    }

    [MemoryPackable]
    public partial class PlayerReadyInfo
    {
        public byte PlayerId { get; set; }
        public int UserId { get; set; }
        public bool IsReady { get; set; }
    }

    [MemoryPackable]
    public partial class StateChangeMessage
    {
        public byte State { get; set; }
    }

    // === 选曲与下载 ===

    [MemoryPackable]
    public partial class ChartVoteMessage
    {
        public int LevelId { get; set; }
        public string LevelName { get; set; } = "";
    }

    [MemoryPackable]
    public partial class VoteStatusMessage
    {
        public VoteEntry[]? Votes { get; set; }
    }

    [MemoryPackable]
    public partial class VoteEntry
    {
        public byte PlayerId { get; set; }
        public int LevelId { get; set; }
        public string LevelName { get; set; } = "";
    }

    [MemoryPackable]
    public partial class ChartResultMessage
    {
        public int LevelId { get; set; }
        public string LevelName { get; set; } = "";
        public string[]? Candidates { get; set; }
    }

    [MemoryPackable]
    public partial class DownloadProgressMessage
    {
        public float Progress { get; set; }
    }

    [MemoryPackable]
    public partial class DownloadStatusMessage
    {
        public PlayerDownloadInfo[]? Players { get; set; }
    }

    [MemoryPackable]
    public partial class PlayerDownloadInfo
    {
        public byte PlayerId { get; set; }
        public float Progress { get; set; }
        public bool Complete { get; set; }
    }

    // === 游玩同步 ===

    [MemoryPackable]
    public partial class CountdownMessage
    {
        public int Seconds { get; set; }
    }

    [MemoryPackable]
    public partial class ComboUpdateMessage
    {
        public int Combo { get; set; }
        public int Score { get; set; }
        public int MaxCombo { get; set; }
    }

    [MemoryPackable]
    public partial class PlayerSyncMessage
    {
        public byte PlayerId { get; set; }
        public int Combo { get; set; }
        public int Score { get; set; }
        public int MaxCombo { get; set; }
    }

    [MemoryPackable]
    public partial class SkillCastMessage
    {
        public byte SkillId { get; set; }
        public byte TargetPlayerId { get; set; }
    }

    [MemoryPackable]
    public partial class SkillEffectMessage
    {
        public byte CasterPlayerId { get; set; }
        public byte TargetPlayerId { get; set; }
        public byte SkillId { get; set; }
        public float Duration { get; set; }
    }

    [MemoryPackable]
    public partial class PlayerFinishedMessage
    {
        public byte PlayerId { get; set; }
        public int FinalScore { get; set; }
        public int MaxPure { get; set; }
        public int Pure { get; set; }
        public int Far { get; set; }
        public int Lost { get; set; }
        public int MaxCombo { get; set; }
    }

    // === 结算 ===

    [MemoryPackable]
    public partial class ResultsMessage
    {
        public PlayerResult[]? Rankings { get; set; }
    }

    [MemoryPackable]
    public partial class PlayerResult
    {
        public byte PlayerId { get; set; }
        public int UserId { get; set; }
        public int Score { get; set; }
        public int MaxPure { get; set; }
        public int Pure { get; set; }
        public int Far { get; set; }
        public int Lost { get; set; }
        public int MaxCombo { get; set; }
        public int Rank { get; set; }
    }

    // === 通用 ===

    [MemoryPackable]
    public partial class PlayerInfo
    {
        public byte PlayerId { get; set; }
        public int UserId { get; set; }
    }

    [MemoryPackable]
    public partial class PingMessage
    {
        public long Timestamp { get; set; }
    }

    [MemoryPackable]
    public partial class ErrorMessage
    {
        public int Code { get; set; }
        public string Message { get; set; } = "";
    }
}
