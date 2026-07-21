namespace BubblesBot.Bot.Modes;

/// <summary>Pure navigation policy for drive-by proximity combat inside Delirium fog.</summary>
public static class DeliriumPackPolicy
{
    public static bool DwellExpired(TimeSpan enteredPackAt, TimeSpan now, double maximumSeconds)
        => enteredPackAt != TimeSpan.MinValue
           && now >= enteredPackAt
           && (now - enteredPackAt).TotalSeconds >= maximumSeconds;
}
