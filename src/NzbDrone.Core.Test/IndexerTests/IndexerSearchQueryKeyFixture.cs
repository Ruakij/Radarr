using System.Net.Http;
using FluentAssertions;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerTests
{
    [TestFixture]
    public class IndexerSearchQueryKeyFixture : CoreTest
    {
        private TestIndexer _indexer;
        private IndexerPageableRequestChain _chain;

        [SetUp]
        public void Setup()
        {
            _chain = new IndexerPageableRequestChain();

            var generator = new Mock<IIndexerRequestGenerator>();
            generator.Setup(s => s.GetSearchRequests(It.IsAny<MovieSearchCriteria>())).Returns(() => _chain);

            _indexer = new TestIndexer(new Mock<IHttpClient>().Object,
                new Mock<IIndexerStatusService>().Object,
                new Mock<IConfigService>().Object,
                new Mock<IParsingService>().Object,
                new Mock<Logger>().Object)
            {
                Definition = new IndexerDefinition { Settings = new TestIndexerSettings { ApiKey = "secret" } },
                _requestGenerator = generator.Object
            };
        }

        private static IndexerRequest Request(string url, string body = null)
        {
            var request = new HttpRequest(url);

            if (body != null)
            {
                request.Method = HttpMethod.Post;
                request.SetContent(body);
            }

            return new IndexerRequest(request);
        }

        private string Key()
        {
            return _indexer.GetSearchQueryKey(new MovieSearchCriteria());
        }

        [Test]
        public void should_drop_credentials_and_sort_parameters()
        {
            _chain.Add(new[] { Request("https://indexer.test/api?t=movie&apikey=secret&imdbid=tt1") });

            Key().Should().Be("GET https://indexer.test:/api?imdbid=tt1&t=movie");
        }

        [Test]
        public void should_match_requests_differing_only_in_parameter_order()
        {
            _chain.Add(new[] { Request("https://indexer.test/api?t=movie&imdbid=tt1") });
            var first = Key();

            _chain = new IndexerPageableRequestChain();
            _chain.Add(new[] { Request("https://indexer.test/api?imdbid=tt1&t=movie") });

            Key().Should().Be(first);
        }

        [Test]
        public void should_use_first_page_of_each_request_and_every_tier()
        {
            _chain.Add(new[] { Request("https://indexer.test/api?q=a&offset=0"), Request("https://indexer.test/api?q=a&offset=100") });
            _chain.Add(new[] { Request("https://indexer.test/api?q=b") });
            _chain.AddTier(new[] { Request("https://indexer.test/api?q=c") });

            Key().Should().Be("GET https://indexer.test:/api?offset=0&q=a\nGET https://indexer.test:/api?q=b\n--\nGET https://indexer.test:/api?q=c");
        }

        [Test]
        public void should_remove_credentials_from_json_body()
        {
            _chain.Add(new[] { Request("https://indexer.test/api", "{\"passkey\":\"secret\",\"query\":\"a\"}") });

            Key().Should().Be("POST https://indexer.test:/api? {\"passkey\":\"\",\"query\":\"a\"}");
        }

        [Test]
        public void should_not_cache_search_without_requests()
        {
            Key().Should().BeNull();
        }
    }
}
