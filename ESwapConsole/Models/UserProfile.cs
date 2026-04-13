namespace ESwapConsole.Models;

public sealed class UserProfile
{
    public string UserId { get; init; } = null!;

    public string Password { get; init; } = null!;

    public int[] AccountIds { get; init; } = [];
}
