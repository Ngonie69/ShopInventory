using System.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.SapConfiguration.Commands.UpdateSAPSettings;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Settings page's SAP password field says "leave blank to keep the current password" and never
/// pre-fills it. Saving with it blank used to write an empty SAP__Password into web.config, which
/// broke the SAP login at the next app pool restart, and the test-on-save step tested the empty
/// password too.
/// </summary>
public sealed class UpdateSAPSettingsPasswordTests : IDisposable
{
    private const string ExistingPassword = "configured-secret";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "sap-settings-" + Guid.NewGuid().ToString("N"));

    private string WebConfigPath => Path.Combine(_directory, "web.config");

    public UpdateSAPSettingsPasswordTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(WebConfigPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <system.webServer>
                <aspNetCore processPath="dotnet">
                  <environmentVariables>
                    <environmentVariable name="SAP__ServiceLayerUrl" value="https://old:50000/b1s/v1/" />
                    <environmentVariable name="SAP__CompanyDB" value="OLDDB" />
                    <environmentVariable name="SAP__Username" value="olduser" />
                    <environmentVariable name="SAP__Password" value="{ExistingPassword}" />
                  </environmentVariables>
                </aspNetCore>
              </system.webServer>
            </configuration>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_password_keeps_the_configured_one_and_tests_with_it(string? blank)
    {
        string? testedWith = null;
        var handler = CreateHandler(password => testedWith = password);

        var result = await handler.Handle(
            new UpdateSAPSettingsCommand(Request(blank), "admin"), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(ExistingPassword, ReadVariable("SAP__Password"));
        Assert.Equal("NEWDB", ReadVariable("SAP__CompanyDB"));
        Assert.Equal(ExistingPassword, testedWith);
        Assert.True(result.Value.ConnectionTestPassed);
    }

    [Fact]
    public async Task Supplied_password_replaces_the_configured_one_and_is_tested()
    {
        string? testedWith = null;
        var handler = CreateHandler(password => testedWith = password);

        var result = await handler.Handle(
            new UpdateSAPSettingsCommand(Request("new-secret"), "admin"), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("new-secret", ReadVariable("SAP__Password"));
        Assert.Equal("new-secret", testedWith);
    }

    private UpdateSAPSettingsHandler CreateHandler(Action<string> onTest)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.TestConnectionWithCredentialsAsync) =>
                Record(onTest, (string)args![3]!),
            _ => null
        });

        return new UpdateSAPSettingsHandler(
            sap,
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            new StaticOptionsMonitor<SAPSettings>(new SAPSettings { Password = ExistingPassword }),
            NullLogger<UpdateSAPSettingsHandler>.Instance)
        {
            WebConfigPath = WebConfigPath
        };
    }

    private static Task<bool> Record(Action<string> onTest, string password)
    {
        onTest(password);
        return Task.FromResult(true);
    }

    private static UpdateSAPSettingsRequest Request(string? password) => new()
    {
        ServiceLayerUrl = "https://new:50000/b1s/v1/",
        CompanyDB = "NEWDB",
        UserName = "newuser",
        Password = password,
        TestConnection = true
    };

    private string? ReadVariable(string name)
    {
        var xml = new XmlDocument();
        xml.Load(WebConfigPath);
        return xml.SelectSingleNode($"//aspNetCore/environmentVariables/environmentVariable[@name='{name}']")
            ?.Attributes?["value"]?.Value;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
