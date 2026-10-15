using System.IO;

namespace TWS2CESteamModManager.App.Services;

/// <summary>Remembers an explicit agreement only when the user asks to hide the notice.</summary>
public sealed class NoticePreferencesStore
{
    private const int CurrentNoticeVersion = 1;
    private readonly AppConfigStore config;

    public NoticePreferencesStore(string? path = null)
    {
        config = new AppConfigStore(path);
    }

    public bool ShouldShow()
    {
        try
        {
            return config.Load().AcceptedNoticeVersion != CurrentNoticeVersion;
        }
        // Missing, unreadable or malformed preferences must never imply agreement.
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return true; }
    }

    public void RememberAgreement()
    {
        config.Update(current => current with { AcceptedNoticeVersion = CurrentNoticeVersion });
    }

}
