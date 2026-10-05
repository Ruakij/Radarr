using FluentValidation;
using NzbDrone.Core.Configuration;
using Radarr.Http;
using Radarr.Http.Validation;

namespace Radarr.Api.V3.Config
{
    [V3ApiController("config/indexer")]
    public class IndexerConfigController : ConfigController<IndexerConfigResource>
    {
        public IndexerConfigController(IConfigService configService)
            : base(configService)
        {
            SharedValidator.RuleFor(c => c.MinimumAge)
                           .GreaterThanOrEqualTo(0);

            SharedValidator.RuleFor(c => c.MaximumSize)
                           .GreaterThanOrEqualTo(0);

            SharedValidator.RuleFor(c => c.Retention)
                           .GreaterThanOrEqualTo(0);

            SharedValidator.RuleFor(c => c.RssSyncInterval)
                           .IsValidRssSyncInterval();

            SharedValidator.RuleFor(c => c.SearchResultCacheLifetime)
                           .GreaterThanOrEqualTo(0);
        }

        protected override IndexerConfigResource ToResource(IConfigService model)
        {
            return IndexerConfigResourceMapper.ToResource(model);
        }
    }
}
