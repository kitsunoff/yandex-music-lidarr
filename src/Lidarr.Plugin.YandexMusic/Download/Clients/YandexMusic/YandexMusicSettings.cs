using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers.YandexMusic;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.YandexMusic
{
    public class YandexMusicSettingsValidator : AbstractValidator<YandexMusicSettings>
    {
        public YandexMusicSettingsValidator()
        {
            RuleFor(x => x.OAuthToken).NotEmpty()
                .WithMessage("OAuth token is required.  Reuse the token configured on the indexer.");
            RuleFor(x => x.DownloadPath).NotEmpty()
                .WithMessage("Download path must be set so Lidarr can find the imported files.");
            RuleFor(x => x.MaxConcurrentTracks).GreaterThan(0).LessThanOrEqualTo(8);
            RuleFor(x => x.DownloadDelayMs).GreaterThanOrEqualTo(0).LessThanOrEqualTo(5000);
        }
    }

    public class YandexMusicSettings : IProviderConfig
    {
        private static readonly YandexMusicSettingsValidator Validator = new();

        public YandexMusicSettings()
        {
            Quality = (int)YandexMusicQualityOption.Lossless;
            MaxConcurrentTracks = 2;
            DownloadDelayMs = 0;
            CoverResolution = (int)YandexMusicCoverResolutionOption.Large;
            EmbedLyrics = true;
        }

        [FieldDefinition(0, Label = "OAuth Token", Type = FieldType.Password,
            Privacy = PrivacyLevel.Password,
            HelpText = "Same OAuth token as on the Yandex.Music indexer.")]
        public string OAuthToken { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Download Path", Type = FieldType.Path,
            HelpText = "Where the downloaded tracks are written before Lidarr imports them.")]
        public string DownloadPath { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Quality", Type = FieldType.Select,
            SelectOptions = typeof(YandexMusicQualityOption),
            HelpText = "Audio quality requested per track.  Lossless requires Yandex Plus.")]
        public int Quality { get; set; }

        [FieldDefinition(3, Label = "Embed Lyrics", Type = FieldType.Checkbox,
            HelpText = "Download lyrics from Yandex.Music and embed them into the audio file tag. Plain text is preferred; synced LRC is used as a fallback.")]
        public bool EmbedLyrics { get; set; }

        [FieldDefinition(4, Label = "Cover Resolution", Type = FieldType.Select,
            SelectOptions = typeof(YandexMusicCoverResolutionOption),
            HelpText = "Album cover size to download and embed.  'Original' fetches the largest available.")]
        public int CoverResolution { get; set; }

        [FieldDefinition(5, Label = "Concurrent Tracks", Type = FieldType.Number,
            Advanced = true,
            HelpText = "Max tracks downloaded in parallel within a single album.  Keep low to avoid rate limits.")]
        public int MaxConcurrentTracks { get; set; }

        [FieldDefinition(6, Label = "Download Delay", Type = FieldType.Number,
            Unit = "ms", Advanced = true,
            HelpText = "Sleep between consecutive track requests.  Anti-rate-limiting backstop.")]
        public int DownloadDelayMs { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum YandexMusicCoverResolutionOption
    {
        [FieldOption(Label = "400x400")]
        Small = 0,

        [FieldOption(Label = "1000x1000")]
        Large = 1,

        [FieldOption(Label = "Original")]
        Original = 2,
    }
}
