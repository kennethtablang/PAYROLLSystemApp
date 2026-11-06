using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// The Install button, wherever it is pressed (the update banner or Help &amp;
/// Updates): confirm, check the downloaded file again, back the database up,
/// record it, hand over to the installer and close.
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>Installing backs the database up first, so it needs the backup permission.</summary>
    bool CanInstall { get; }

    /// <summary>
    /// Returns null when the app is closing for the installer or the user chose
    /// "Not now"; otherwise the reason nothing was installed.
    /// </summary>
    Task<string?> InstallAsync();
}

public sealed class UpdateInstaller(
    ISessionService session,
    IUpdateService updates,
    IDataManagementService data,
    IDialogService dialogs,
    IAuditService audit) : IUpdateInstaller
{
    private int _running;

    public bool CanInstall => session.Has(Permission.ManageBackups);

    public async Task<string?> InstallAsync()
    {
        // The banner and Help & Updates can both be clicked; one install at a time.
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return null;

        try
        {
            return await RunAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<string?> RunAsync()
    {
        var user = session.CurrentUser;
        var update = updates.Available;

        if (user is null || update is null || updates.Stage != UpdateStage.Ready)
            return "The update has not been downloaded yet.";

        if (!CanInstall)
            return "Only an administrator can install updates.";

        if (!await dialogs.ConfirmAsync(
                $"Install version {update.VersionDisplay}",
                "The app will back up the database, close, install the new version and open again. " +
                "It takes a minute or two.\n\nMake sure no one else is using the app and that nothing is half-saved.",
                "Back up and install", "Not now"))
            return null;

        // 1. The file may have sat in the temp folder for hours: hash it again.
        string installer;
        try
        {
            installer = await updates.GetVerifiedInstallerAsync();
        }
        catch (UpdateException ex)
        {
            return ex.Message;
        }

        // 2. A backup the new version can be rolled back to.
        var backup = await data.CreateBackupAsync(user);
        if (!backup.Succeeded)
            return $"The update was not installed because the backup failed: {backup.Message}";

        await audit.WriteAsync(AuditActions.UpdateStarted, "Application", null, true,
            $"Updating from {AppVersion.Display} to {update.VersionDisplay}. {backup.Message}",
            user.Username, user.Id);

        // 3. Hand over to the installer, which reopens the app when it is done.
        try
        {
            updates.StartInstaller(installer);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UpdateInstaller] {ex}");
            return "The installer could not be started. If Windows asked for permission, choose Yes and try again.";
        }

        Application.Current?.Quit();
        return null;
    }
}
