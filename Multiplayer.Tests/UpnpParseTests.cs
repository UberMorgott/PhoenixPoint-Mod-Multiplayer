using Multiplayer.Net;
using Xunit;

namespace Multiplayer.Tests
{
    public class UpnpParseTests
    {
        private const string SsdpOk =
            "HTTP/1.1 200 OK\r\n" +
            "CACHE-CONTROL: max-age=120\r\n" +
            "ST: urn:schemas-upnp-org:service:WANIPConnection:1\r\n" +
            "USN: uuid:abcd::urn:schemas-upnp-org:service:WANIPConnection:1\r\n" +
            "LOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n" +
            "SERVER: Router/1.0 UPnP/1.0\r\n\r\n";

        [Fact]
        public void ParseLocation_Happy()
        {
            Assert.Equal("http://192.168.1.1:5000/rootDesc.xml",
                UpnpPortMapper.ParseLocationFromSsdp(SsdpOk));
        }

        [Fact]
        public void ParseLocation_CaseInsensitiveHeader()
        {
            var resp = "HTTP/1.1 200 OK\r\nlocation: http://10.0.0.1/desc.xml\r\n\r\n";
            Assert.Equal("http://10.0.0.1/desc.xml", UpnpPortMapper.ParseLocationFromSsdp(resp));
        }

        [Fact]
        public void ParseLocation_MissingHeader_Null()
        {
            Assert.Null(UpnpPortMapper.ParseLocationFromSsdp("HTTP/1.1 200 OK\r\nST: foo\r\n\r\n"));
            Assert.Null(UpnpPortMapper.ParseLocationFromSsdp(null));
        }

        private const string DeviceXmlRelative =
            "<?xml version=\"1.0\"?>" +
            "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><serviceList>" +
            "<service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType>" +
            "<controlURL>/ctl/CommonIfCfg</controlURL></service>" +
            "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>" +
            "<controlURL>/ctl/IPConn</controlURL></service>" +
            "</serviceList></device></root>";

        [Fact]
        public void ParseControlUrl_WANIP_Relative_ResolvedAgainstLocation()
        {
            var svc = UpnpPortMapper.ParseControlUrl(DeviceXmlRelative, "http://192.168.1.1:5000/rootDesc.xml");
            Assert.NotNull(svc);
            Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", svc.Value.serviceType);
            Assert.Equal("http://192.168.1.1:5000/ctl/IPConn", svc.Value.controlUrl);
        }

        [Fact]
        public void ParseControlUrl_WANPPP_Absolute_PassThrough()
        {
            var xml =
                "<root><device><serviceList>" +
                "<service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType>" +
                "<controlURL>http://192.168.0.1:80/upnp/control/WANPPPConn1</controlURL></service>" +
                "</serviceList></device></root>";
            var svc = UpnpPortMapper.ParseControlUrl(xml, "http://192.168.0.1/gatedesc.xml");
            Assert.NotNull(svc);
            Assert.Equal("http://192.168.0.1:80/upnp/control/WANPPPConn1", svc.Value.controlUrl);
        }

        [Fact]
        public void ParseControlUrl_NoWanService_Null()
        {
            var xml =
                "<root><device><serviceList>" +
                "<service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>" +
                "<controlURL>/ctl/L3F</controlURL></service>" +
                "</serviceList></device></root>";
            Assert.Null(UpnpPortMapper.ParseControlUrl(xml, "http://192.168.1.1/desc.xml"));
        }

        [Fact]
        public void ParseControlUrl_Malformed_Null()
        {
            Assert.Null(UpnpPortMapper.ParseControlUrl("not xml at all", "http://192.168.1.1/desc.xml"));
            Assert.Null(UpnpPortMapper.ParseControlUrl(null, "http://x/y"));
        }

        [Fact]
        public void ParseExternalIp_Happy()
        {
            var soap =
                "<?xml version=\"1.0\"?><s:Envelope><s:Body>" +
                "<u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                "<NewExternalIPAddress>203.0.113.45</NewExternalIPAddress>" +
                "</u:GetExternalIPAddressResponse></s:Body></s:Envelope>";
            Assert.Equal("203.0.113.45", UpnpPortMapper.ParseExternalIp(soap));
        }

        [Fact]
        public void ParseExternalIp_EmptyOrMissing_Null()
        {
            Assert.Null(UpnpPortMapper.ParseExternalIp("<NewExternalIPAddress></NewExternalIPAddress>"));
            Assert.Null(UpnpPortMapper.ParseExternalIp("<s:Fault>error</s:Fault>"));
            Assert.Null(UpnpPortMapper.ParseExternalIp(null));
        }

        [Fact]
        public void ResolveUrl_RelativeAndAbsolute()
        {
            Assert.Equal("http://192.168.1.1:5000/ctl/IPConn",
                UpnpPortMapper.ResolveUrl("http://192.168.1.1:5000/rootDesc.xml", "/ctl/IPConn"));
            Assert.Equal("http://host/abs",
                UpnpPortMapper.ResolveUrl("http://192.168.1.1/desc.xml", "http://host/abs"));
            Assert.Null(UpnpPortMapper.ResolveUrl("http://192.168.1.1/desc.xml", null));
        }
    }
}
