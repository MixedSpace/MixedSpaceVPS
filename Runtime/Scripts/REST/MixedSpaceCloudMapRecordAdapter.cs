using MixedSpace.ServiceContracts;

namespace MixedSpace.XR
{
    /// <summary>
    /// 在 REST 边界将当前云 DTO 转成 SDK 地图模型，避免地图准备逻辑依赖具体后端或原生包格式。
    /// Converts the current cloud transport DTO into the SDK-owned map model.
    /// Keeping this translation at the REST edge prevents provisioning code from
    /// depending on one backend or native package representation.
    /// </summary>
    internal static class MixedSpaceCloudMapRecordAdapter
    {
        public static MixedSpaceCloudMapDescriptor ToDomain(
            SDKMapMetadataGetResult transport)
        {
            return ToDomain(transport, transport.id, transport.name);
        }

        public static MixedSpaceCloudMapDescriptor ToDomain(
            SDKMapMetadataGetResult transport,
            int key,
            string label)
        {
            return new MixedSpaceCloudMapDescriptor(
                key,
                label,
                new MixedSpaceMapAlignment
                {
                    tx = transport.tx,
                    ty = transport.ty,
                    tz = transport.tz,
                    qw = transport.qw,
                    qx = transport.qx,
                    qy = transport.qy,
                    qz = transport.qz,
                    scale = transport.scale
                },
                new MixedSpaceGeoOrigin
                {
                    latitude = transport.latitude,
                    longitude = transport.longitude,
                    altitude = transport.altitude
                },
                transport.privacy);
        }
    }
}
