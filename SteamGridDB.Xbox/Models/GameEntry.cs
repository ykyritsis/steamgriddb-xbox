using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace SteamGridDB.Xbox.Models
{
    public class GameEntry : INotifyPropertyChanged
    {
        private string name;
        private string xboxPlatformId;
        private string externalPlatformId;
        private GamePlatform platform;
        private DateTime addedDate;
        private string imageFileName;
        private string imageFilePath;
        private BitmapImage image;
        private bool hasBackup;
        private bool hasSteamGridDBMatch;

        public StorageFolder ImageFolder
        {
            get; set;
        }

        /// <summary>
        /// SteamGridDB game ID resolved from the platform ID, used to fetch artwork reliably for every platform.
        /// </summary>
        public int? SteamGridDbGameId
        {
            get; set;
        }

        public string Name
        {
            get => name;
            set
            {
                if (name != value)
                {
                    name = value;
                    OnPropertyChanged();
                }
            }
        }

        public string XboxPlatformId
        {
            get => xboxPlatformId;
            set
            {
                if (xboxPlatformId != value)
                {
                    xboxPlatformId = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ExternalPlatformId
        {
            get => externalPlatformId;
            set
            {
                if (externalPlatformId != value)
                {
                    externalPlatformId = value;
                    OnPropertyChanged();
                }
            }
        }

        public GamePlatform Platform
        {
            get => platform;
            set
            {
                if (platform != value)
                {
                    platform = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EditButtonVisibility));
                    OnPropertyChanged(nameof(SearchButtonVisibility));
                    OnPropertyChanged(nameof(PlatformLabel));
                    OnPropertyChanged(nameof(PlatformBrush));
                }
            }
        }

        public DateTime AddedDate
        {
            get => addedDate;
            set
            {
                if (addedDate != value)
                {
                    addedDate = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AddedDateFormatted));
                }
            }
        }

        public string AddedDateFormatted => AddedDate.ToString();

        public string ImageFileName
        {
            get => imageFileName;
            set
            {
                if (imageFileName != value)
                {
                    imageFileName = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ImageFilePath
        {
            get => imageFilePath;
            set
            {
                if (imageFilePath != value)
                {
                    imageFilePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasSteamGridDBMatch
        {
            get => hasSteamGridDBMatch;
            set
            {
                if (hasSteamGridDBMatch != value)
                {
                    hasSteamGridDBMatch = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EditButtonVisibility));
                    OnPropertyChanged(nameof(SearchButtonVisibility));
                }
            }
        }

        // Edit button visible when there is a match
        // Custom games (added in the Xbox app or by the widget) can always be edited: their name is known,
        // so the artwork panel can search by it even without an exact SteamGridDB match
        public Visibility EditButtonVisibility => HasSteamGridDBMatch || GamePlatformHelper.IsCustomLibrary(Platform) ? Visibility.Visible : Visibility.Collapsed;

        // Search button visible when there is no match
        public Visibility SearchButtonVisibility => !HasSteamGridDBMatch && !GamePlatformHelper.IsCustomLibrary(Platform) ? Visibility.Visible : Visibility.Collapsed;

        public bool HasBackup
        {
            get => hasBackup;
            set
            {
                if (hasBackup != value)
                {
                    hasBackup = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RestoreButtonVisibility));
                    OnPropertyChanged(nameof(ArtworkStatus));
                }
            }
        }

        public Visibility RestoreButtonVisibility => HasBackup ? Visibility.Visible : Visibility.Collapsed;

        public BitmapImage Image
        {
            get => image;
            set
            {
                if (image != value)
                {
                    image = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasImage));
                    OnPropertyChanged(nameof(ImageVisibility));
                    OnPropertyChanged(nameof(PlaceholderVisibility));
                    OnPropertyChanged(nameof(ArtworkStatus));
                }
            }
        }

        /// <summary>
        /// What the Xbox app currently shows for this entry, in plain words.
        /// </summary>
        public string ArtworkStatus
        {
            get
            {
                if (IsPendingInXboxApp)
                {
                    // Installed in Steam, but the Xbox app has not listed it yet (it scans Steam when it starts)
                    return Image == null ? "Not in the Xbox app yet" : "Not in the Xbox app yet - cover ready";
                }

                return Image == null ? "No cover yet" : HasBackup ? "Custom cover" : "Xbox app cover";
            }
        }

        /// <summary>
        /// True for a Steam game that is installed but not in the Xbox app's Steam list yet. Its cover can already be set:
        /// the file is written where the Xbox app will look for it.
        /// </summary>
        public bool IsPendingInXboxApp
        {
            get; set;
        }

        /// <summary>
        /// Short store name shown on the row's badge.
        /// </summary>
        public string PlatformLabel
        {
            get
            {
                switch (Platform)
                {
                    case GamePlatform.Ubisoft:
                        return "Ubisoft";
                    case GamePlatform.BattleNet:
                        return "Battle.net";
                    case GamePlatform.Custom:
                        return "Custom";
                    default:
                        return Platform.ToString();
                }
            }
        }

        /// <summary>
        /// Badge colour for the store, so a long list can be scanned by colour.
        /// </summary>
        public Brush PlatformBrush
        {
            get
            {
                switch (Platform)
                {
                    case GamePlatform.Steam:
                        return new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x47, 0x5E));
                    case GamePlatform.Epic:
                        return new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A));
                    case GamePlatform.GOG:
                        return new SolidColorBrush(Color.FromArgb(0xFF, 0x6D, 0x2F, 0x83));
                    case GamePlatform.Ubisoft:
                        return new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x5F, 0xBF));
                    case GamePlatform.EA:
                        return new SolidColorBrush(Color.FromArgb(0xFF, 0xB2, 0x3B, 0x33));
                    case GamePlatform.Custom:
                        return new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x7C, 0x10));
                    case GamePlatform.App:
                        return new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x55, 0x68));
                    default:
                        return new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A));
                }
            }
        }

        public bool HasImage => Image != null;
        public Visibility ImageVisibility => Image != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlaceholderVisibility => Image == null ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
