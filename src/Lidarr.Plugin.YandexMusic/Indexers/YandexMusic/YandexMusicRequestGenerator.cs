using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using YandexMusicSharp;
using YandexMusicSharp.Http;

namespace NzbDrone.Core.Indexers.YandexMusic
{
    public class YandexMusicRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 50;

        public YandexMusicIndexerSettings Settings { get; set; } = new();
        public Logger Logger { get; set; }

        public IndexerPageableRequestChain GetRecentRequests()
        {
            // Streaming services have no recent-releases feed; return a harmless dummy so
            // that Lidarr's "Test" button has something to call when the user saves.
            var chain = new IndexerPageableRequestChain();
            chain.Add(BuildSearch("never gonna give you up", searchType: "album"));
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            // A Yandex metadata provider can persist either a Yandex foreign album id
            // (yandex:album:<id>[:artist:<id>]) or a Yandex Music external album URL.
            // Prefer that authoritative id over a fuzzy artist/title text search.
            if (TryResolveAlbumId(searchCriteria.Albums, out var albumId, out var source))
            {
                Logger?.Debug(
                    "Yandex.Music indexer: using direct album lookup {0} from {1} instead of text search for {2}",
                    albumId,
                    source,
                    searchCriteria);
                chain.AddTier(BuildAlbumById(albumId));
                return chain;
            }

            var query = $"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}".Trim();
            chain.AddTier(BuildSearch(query, searchType: "album"));
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            // Artist searches carry the monitored albums in SearchCriteriaBase.Albums.
            // Resolve every Yandex-backed album directly and only use title search for
            // albums that do not carry a Yandex id/link.  Keeping all requests in one
            // tier makes Lidarr execute them as one artist-search batch.
            if (searchCriteria.Albums is { Count: > 0 })
            {
                var requests = new List<IndexerRequest>();
                var directIds = new HashSet<long>();
                var fallbackQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var album in searchCriteria.Albums)
                {
                    if (TryResolveAlbumId(new[] { album }, out var albumId, out var source))
                    {
                        if (directIds.Add(albumId))
                        {
                            Logger?.Debug(
                                "Yandex.Music indexer: artist search will use direct album lookup {0} from {1} ({2})",
                                albumId,
                                source,
                                album.Title);
                            requests.AddRange(BuildAlbumById(albumId));
                        }
                    }
                    else
                    {
                        var query = $"{searchCriteria.ArtistQuery} {album.Title}".Trim();
                        if (!string.IsNullOrWhiteSpace(query) && fallbackQueries.Add(query))
                        {
                            requests.AddRange(BuildSearch(query, searchType: "album"));
                        }
                    }
                }

                if (requests.Count > 0)
                {
                    chain.AddTier(requests);
                    return chain;
                }
            }

            // Preserve the previous fallback for unusual artist searches where Lidarr
            // did not attach album entities to the criteria.
            chain.AddTier(BuildSearch(searchCriteria.ArtistQuery, searchType: "artist"));
            return chain;
        }

        private IEnumerable<IndexerRequest> BuildAlbumById(long albumId)
        {
            var baseUrl = GetBaseUrl();
            var url = string.Concat(
                baseUrl,
                "/albums/",
                albumId.ToString(CultureInfo.InvariantCulture),
                "/with-tracks");

            yield return BuildRequest(url);
        }

        private IEnumerable<IndexerRequest> BuildSearch(string query, string searchType)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                yield break;
            }

            var baseUrl = GetBaseUrl();
            var url = string.Concat(
                baseUrl,
                "/search?",
                "text=", Uri.EscapeDataString(query),
                "&nocorrect=False",
                "&type=", searchType,
                "&page=0",
                "&page-size=", PageSize.ToString(CultureInfo.InvariantCulture),
                "&playlist-in-best=True");

            yield return BuildRequest(url);
        }

        private string GetBaseUrl()
        {
            return string.IsNullOrWhiteSpace(Settings.BaseUrl)
                ? YandexMusicHttpClient.BaseUrl
                : Settings.BaseUrl.TrimEnd('/');
        }

        private IndexerRequest BuildRequest(string url)
        {
            var request = new HttpRequest(url, HttpAccept.Json)
            {
                Method = HttpMethod.Get,
            };
            request.Headers.Add("Authorization", $"OAuth {Settings.OAuthToken}");
            request.Headers.Add("X-Yandex-Music-Client", YandexMusicHttpClient.DefaultClientHeader);
            // Do not override User-Agent here. Lidarr's ManagedHttpDispatcher rejects
            // non-Lidarr User-Agent values; X-Yandex-Music-Client is sufficient.

            return new IndexerRequest(request);
        }

        private static bool TryResolveAlbumId(
            IEnumerable<Album>? albums,
            out long albumId,
            out string source)
        {
            albumId = 0;
            source = string.Empty;
            if (albums is null)
            {
                return false;
            }

            foreach (var album in albums)
            {
                if (YandexAlbumReference.TryParseAlbumId(album.ForeignAlbumId, out albumId))
                {
                    source = "ForeignAlbumId";
                    return true;
                }

                if (album.OldForeignAlbumIds is not null)
                {
                    foreach (var oldId in album.OldForeignAlbumIds)
                    {
                        if (YandexAlbumReference.TryParseAlbumId(oldId, out albumId))
                        {
                            source = "OldForeignAlbumIds";
                            return true;
                        }
                    }
                }

                if (album.Links is not null)
                {
                    foreach (var link in album.Links)
                    {
                        if (YandexAlbumReference.TryParseAlbumId(link.Url, out albumId))
                        {
                            source = "Yandex Music link";
                            return true;
                        }
                    }
                }
            }

            albumId = 0;
            source = string.Empty;
            return false;
        }
    }
}
