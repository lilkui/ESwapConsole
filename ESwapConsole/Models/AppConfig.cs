using ESwapSharp;

namespace ESwapConsole.Models;

public sealed class AppConfig
{
    public string FrontAddress { get; init; } = null!;

    public string BrokerId { get; init; } = null!;

    public UserProfile[] Users { get; init; } = [];

    public QpsLimitOptions RequestQpsLimit { get; init; } = new();

    public string SblDemandFilePath { get; init; } = null!;

    public string RecallFilePath { get; init; } = null!;

    public string ReturnFilePath { get; init; } = null!;
}
