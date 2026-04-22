using System.ComponentModel.DataAnnotations;

namespace ESwapConsole.Models;

public sealed record UserProfile
{
    [Required(AllowEmptyStrings = false)]
    public string UserId { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Password { get; init; } = string.Empty;

    [MinLength(1)]
    public int[] AccountIds { get; init; } = [];
}
