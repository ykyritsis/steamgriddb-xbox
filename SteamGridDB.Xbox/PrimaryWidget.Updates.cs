using System;
using System.Threading.Tasks;

using Windows.UI.Xaml;

using SteamGridDB.Xbox.Services;

namespace SteamGridDB.Xbox
{
    // Checking for and installing new releases of the widget.
    public sealed partial class PrimaryWidget
    {
        private UpdateInfo availableUpdate;

        /// <summary>
        /// Looks for a newer release and shows the update button when there is one.
        /// </summary>
        private async Task CheckForUpdateAsync()
        {
            availableUpdate = await UpdateService.CheckAsync();

            if (availableUpdate != null)
            {
                UpdateButtonText.Text = $"Update to {availableUpdate.DisplayVersion}";
                UpdateButton.Visibility = Visibility.Visible;
            }
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (availableUpdate == null)
            {
                return;
            }

            if (IsBusy)
            {
                StatusText.Text = "Please wait for the current operation to finish";

                return;
            }

            IsBusy = true;
            UpdateButton.IsEnabled = false;
            StatusText.Text = $"Downloading version {availableUpdate.DisplayVersion}. The widget will close to finish updating - reopen it afterwards.";

            // Windows closes the widget when the update installs, so execution only continues here if it failed
            string error = await UpdateService.InstallAsync(availableUpdate);

            StatusText.Text = error == null
                ? "Update installed - close and reopen the widget to use it"
                : $"Update failed: {error}. You can run the installer from the Releases page instead.";

            UpdateButton.IsEnabled = true;
            IsBusy = false;
        }
    }
}
