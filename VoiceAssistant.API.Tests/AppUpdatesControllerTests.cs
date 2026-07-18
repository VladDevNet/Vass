using VoiceAssistant.API.Controllers;

namespace VoiceAssistant.API.Tests;

public class AppUpdatesControllerTests
{
    private static readonly AndroidUpdateRelease ValidRelease = new(
        "1.2.17",
        21,
        0,
        "https://vass.it-consult.services/downloads/vass-1.2.17.apk",
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "Улучшения голосового контура и проверка обновлений.");

    [Fact]
    public void BuildAndroidResponse_OlderBuildGetsOptionalUpdate()
    {
        var response = AppUpdatesController.BuildAndroidResponse(20, ValidRelease);

        Assert.True(response.UpdateAvailable);
        Assert.False(response.Mandatory);
        Assert.Equal(21, response.LatestVersionCode);
        Assert.Equal("1.2.17", response.LatestVersion);
        Assert.NotNull(response.DownloadUrl);
    }

    [Fact]
    public void BuildAndroidResponse_BelowMinimumBuildGetsMandatoryUpdate()
    {
        var release = ValidRelease with { MinimumSupportedVersionCode = 20 };

        var response = AppUpdatesController.BuildAndroidResponse(19, release);

        Assert.True(response.UpdateAvailable);
        Assert.True(response.Mandatory);
        Assert.Equal(20, response.MinimumSupportedVersionCode);
    }

    [Fact]
    public void BuildAndroidResponse_CurrentBuildDoesNotGetUpdate()
    {
        var response = AppUpdatesController.BuildAndroidResponse(21, ValidRelease);

        Assert.False(response.UpdateAvailable);
        Assert.False(response.Mandatory);
    }

    [Fact]
    public void BuildAndroidResponse_InvalidDownloadFailsClosed()
    {
        var release = ValidRelease with { DownloadUrl = "http://vass.it-consult.services/update.apk" };

        var response = AppUpdatesController.BuildAndroidResponse(1, release);

        Assert.False(response.UpdateAvailable);
        Assert.False(response.Mandatory);
        Assert.Null(response.DownloadUrl);
    }

    [Fact]
    public void BuildAndroidResponse_InvalidHashIsNotAdvertised()
    {
        var release = ValidRelease with { Sha256 = "not-a-hash" };

        var response = AppUpdatesController.BuildAndroidResponse(20, release);

        Assert.True(response.UpdateAvailable);
        Assert.Null(response.Sha256);
    }
}
