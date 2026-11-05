using System.Text.Json;
using System.Xml;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the SAP attachments folder the API's web.config ships to the one appsettings.json names.
/// </summary>
/// <remarks>
/// Every deploy copies <c>SAP__AttachmentsPath</c> and <c>SAP__AttachmentsServiceLayerSourcePath</c>
/// from the package's web.config onto the server (the "managed" variables in Update-Production.ps1),
/// and an environment variable beats appsettings.json. So when the two files disagree, production runs
/// on web.config's value whatever appsettings.json says.
///
/// They did disagree. On 2026-09-03 appsettings.json was corrected from <c>\\kfdb\</c>, a host that does
/// not resolve, to the file server <c>\\kfldb\</c>; web.config kept <c>\\kfdb\</c> until 2026-11-05, so
/// every deploy in between put the dead host back on the server.
/// </remarks>
public class ShippedAttachmentsPathTests
{
    [Theory]
    [InlineData("AttachmentsPath")]
    [InlineData("AttachmentsServiceLayerSourcePath")]
    public void The_shipped_web_config_names_the_same_folder_as_appsettings(string setting)
    {
        var root = RepositoryRoot();

        using var appSettings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "ShopInventory", "appsettings.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var fromAppSettings = appSettings.RootElement.GetProperty("SAP").GetProperty(setting).GetString();

        var webConfig = new XmlDocument();
        webConfig.Load(Path.Combine(root, "ShopInventory", "web.config"));
        var fromWebConfig = webConfig
            .SelectSingleNode(
                "/configuration/location/system.webServer/aspNetCore/environmentVariables/" +
                $"environmentVariable[@name='SAP__{setting}']")
            ?.Attributes?["value"]?.Value;

        Assert.False(string.IsNullOrWhiteSpace(fromAppSettings), $"appsettings.json has no SAP:{setting}.");
        Assert.False(string.IsNullOrWhiteSpace(fromWebConfig), $"web.config has no SAP__{setting}.");
        Assert.Equal(fromAppSettings, fromWebConfig);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
