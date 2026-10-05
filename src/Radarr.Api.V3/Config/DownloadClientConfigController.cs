using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using Radarr.Http;
using Radarr.Http.REST.Attributes;

namespace Radarr.Api.V3.Config
{
    [V3ApiController("config/downloadclient")]
    public class DownloadClientConfigController : ConfigController<DownloadClientConfigResource>
    {
        public DownloadClientConfigController(IConfigService configService)
            : base(configService)
        {
        }

        [RestPutById]
        public override ActionResult<DownloadClientConfigResource> SaveConfig([FromBody] DownloadClientConfigResource resource)
        {
            // Null values are skipped when saving, an empty timeout has to be stored explicitly to disable it again
            resource.ManualImportTimeout ??= -1;

            return base.SaveConfig(resource);
        }

        protected override DownloadClientConfigResource ToResource(IConfigService model)
        {
            return DownloadClientConfigResourceMapper.ToResource(model);
        }
    }
}
