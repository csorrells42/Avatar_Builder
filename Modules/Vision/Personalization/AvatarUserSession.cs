namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarUserSession
{
    private readonly object _sync = new();
    private string _loggedInProfileId = "";
    private long _generation;

    public string LoggedInProfileId
    {
        get
        {
            lock (_sync)
            {
                return _loggedInProfileId;
            }
        }
    }

    public long Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    public bool IsLoggedIn
    {
        get
        {
            lock (_sync)
            {
                return !string.IsNullOrWhiteSpace(_loggedInProfileId);
            }
        }
    }

    public void LogIn(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        lock (_sync)
        {
            _loggedInProfileId = profileId;
            _generation++;
        }
    }

    public void LogOut()
    {
        lock (_sync)
        {
            _loggedInProfileId = "";
            _generation++;
        }
    }

    public bool Matches(string profileId, long generation)
    {
        lock (_sync)
        {
            return !string.IsNullOrWhiteSpace(_loggedInProfileId)
                && _generation == generation
                && string.Equals(_loggedInProfileId, profileId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
