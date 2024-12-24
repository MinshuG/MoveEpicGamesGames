using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FluentAvalonia.UI.Controls;

namespace MoveEpicGamesGames.Utils
{
    public static class ProcessHelper
    {
        private static readonly string[] EpicProcessNames = 
        {
            "EpicGamesLauncher",
            "EpicWebHelper"
        };

        public static async Task<bool> CheckAndWarnForEpicProcesses()
        {
            while (true)
            {
                var runningEpicProcesses = Process.GetProcesses()
                    .Where(p => EpicProcessNames.Contains(p.ProcessName))
                    .ToList();

                if (!runningEpicProcesses.Any())
                    return true;

                var dialog = new ContentDialog
                {
                    Title = "Epic Games Launcher Running",
                    Content = "Please close Epic Games Launcher before continuing.",
                    PrimaryButtonText = "Retry",
                    CloseButtonText = "Cancel"
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                    return false;
            }
        }
    }
}
