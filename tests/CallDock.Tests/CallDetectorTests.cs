using CallDock.Core;

namespace CallDock.Tests;

public sealed class CallDetectorTests
{
    [Theory]
    [InlineData(@"C:#Users#me#AppData#Local#CallDock#current#CallDock.exe", true)]
    [InlineData(@"D:#Tools#CallDock#CallDock.exe", true)]
    [InlineData(@"C:#Program Files#Zoom#bin#Zoom.exe", false)]
    [InlineData(@"C:#Program Files#Google#Chrome#Application#chrome.exe", false)]
    public void CallDockIsNotACall(string key, bool own) => Assert.Equal(own, CallDetector.IsCallDock(key));

    [Theory]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", "WhatsApp")]
    [InlineData("MSTeams_8wekyb3d8bbwe", "Microsoft Teams")]
    [InlineData("Microsoft.SkypeApp_kzf8qxf38zg5c", "Skype")]
    [InlineData("TelegramMessengerLLP.TelegramDesktop_t4vj0pshhgkwm", "Telegram")]
    public void StoreAppsGetReadableNames(string family, string name) => Assert.Equal(name, CallDetector.PackagedName(family));

    [Fact]
    public void AProgramThatIsGoneIsNamedByItsFile() =>
        Assert.Equal("Telemost", CallDetector.DesktopName(@"C:#Nowhere#Yandex#Telemost.exe"));
}
