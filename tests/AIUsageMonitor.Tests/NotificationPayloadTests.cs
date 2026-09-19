using System.Xml.Linq;
using AIUsageMonitor.Core.Notifications;

namespace AIUsageMonitor.Tests;

public sealed class NotificationPayloadTests
{
    [Fact]
    public void Create_escapes_title_and_body_as_text_and_keeps_newline()
    {
        const string title = "A&B <title> \"quoted\"";
        const string body = "line 1 & <body> > line\nline 2 \"quoted\"";

        var document = XDocument.Parse(NotificationPayload.Create(title, body, LogoUri()));
        var text = document.Descendants().Where(e => e.Name.LocalName == "text").ToList();

        Assert.Equal(2, text.Count);
        Assert.Equal(title, text[0].Value);
        Assert.Equal(body, text[1].Value);
        Assert.DoesNotContain(document.Descendants(), e => e.Name.LocalName is "title" or "body");
    }

    [Fact]
    public void Create_preserves_explicit_logo_uri_and_toast_launch_action()
    {
        var logo = LogoUri();

        var document = XDocument.Parse(NotificationPayload.Create("title", "body", logo));
        var toast = document.Root;
        var image = document.Descendants().Single(e => e.Name.LocalName == "image");

        Assert.NotNull(toast);
        Assert.Equal("show-notch", (string?)toast!.Attribute("launch"));
        Assert.Equal("appLogoOverride", (string?)image.Attribute("placement"));
        Assert.Equal(logo.AbsoluteUri, (string?)image.Attribute("src"));
        Assert.Equal("AIUsageMonitor", (string?)image.Attribute("alt"));
    }

    private static Uri LogoUri() =>
        new("file:///C:/Program%20Files/AI%23Usage%20Monitor/logo%20%26%20mark.png");
}
