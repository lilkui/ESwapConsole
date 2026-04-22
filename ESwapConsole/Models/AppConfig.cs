using System.ComponentModel.DataAnnotations;
using ESwapSharp;

namespace ESwapConsole.Models;

public sealed record AppConfig
{
    [Required(AllowEmptyStrings = false)]
    public string FrontAddress { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string DataFrontAddress { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string BrokerId { get; init; } = string.Empty;

    [MinLength(1)]
    public UserProfile[] Users { get; init; } = [];

    [Required]
    public QpsLimitOptions RequestQpsLimit { get; init; } = new();

    [Required(AllowEmptyStrings = false)]
    public string SblDemandFilePath { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string RecallFilePath { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string ReturnFilePath { get; init; } = string.Empty;
}
