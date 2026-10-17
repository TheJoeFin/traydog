using System;                            // IAsyncOperation awaiter
using System.Threading.Tasks;
using Windows.ApplicationModel;

// Wraps the MSIX "start with Windows" task declared in Package.appxmanifest (opt-in: Enabled="false").
static class StartupService
{
    const string TaskId = "TrayDogStartup"; // must match uap5:StartupTask/@TaskId

    // Returns the current state, or null when the app runs without package identity.
    public static async Task<StartupTaskState?> GetStateAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<StartupTaskState?> SetEnabledAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
                return await task.RequestEnableAsync();

            task.Disable();
            return task.State;
        }
        catch
        {
            return null;
        }
    }
}
