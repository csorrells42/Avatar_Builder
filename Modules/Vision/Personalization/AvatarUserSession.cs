namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarUserSession
{
    public string LoggedInProfileId { get; private set; } = "";

    public long Generation { get; private set; }

    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(LoggedInProfileId);

    public void LogIn(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        LoggedInProfileId = profileId;
        Generation++;
    }

    public void LogOut()
    {
        LoggedInProfileId = "";
        Generation++;
    }

    public bool Matches(string profileId, long generation)
    {
        return IsLoggedIn
            && Generation == generation
            && string.Equals(LoggedInProfileId, profileId, StringComparison.OrdinalIgnoreCase);
    }
}
