using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamGridDB.Xbox.Models
{
    /// <summary>
    /// Represents a grid image item for display in the selection panel.
    /// </summary>
    public class GridImageItem : INotifyPropertyChanged
    {
        private string url;
        private string thumbUrl;
        private string style;
        private string author;
        private int score;
        private int id;
        private string source = "SteamGridDB";

        /// <summary>
        /// Where the artwork comes from (SteamGridDB, Xbox Store, PlayStation Store or IGN).
        /// </summary>
        public string Source
        {
            get => source;
            set
            {
                if (source != value)
                {
                    source = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Tooltip text describing the artwork.
        /// </summary>
        public string Description => Source == "SteamGridDB" ? $"SteamGridDB • {Style} • by {Author} • score {Score}" : $"{Source} • {Author}";

        public string Url
        {
            get => url;
            set
            {
                if (url != value)
                {
                    url = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ThumbUrl
        {
            get => thumbUrl;
            set
            {
                if (thumbUrl != value)
                {
                    thumbUrl = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Style
        {
            get => style;
            set
            {
                if (style != value)
                {
                    style = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Author
        {
            get => author;
            set
            {
                if (author != value)
                {
                    author = value;
                    OnPropertyChanged();
                }
            }
        }

        public int Score
        {
            get => score;
            set
            {
                if (score != value)
                {
                    score = value;
                    OnPropertyChanged();
                }
            }
        }

        public int Id
        {
            get => id;
            set
            {
                if (id != value)
                {
                    id = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
