using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that nothing the Web uses to authenticate to its own API reaches the AI provider.
/// </summary>
/// <remarks>
/// <para>
/// The assistant used to send on the circuit's API client. That client carries the portal's
/// <c>X-API-Key</c> as a default header and, once somebody signs in, their bearer token, and
/// HttpClient puts default headers on every request whatever host it is for. So the key went to the
/// provider on every call, and on the two paths that set no <c>Authorization</c> of their own (Azure
/// OpenAI and Ollama) the signed-in user's token went with it.
/// </para>
/// <para>
/// The container here is put together the way <c>ShopInventory.Web/Program.cs</c> does it: the API's
/// named client, the scoped <see cref="HttpClient"/> built from it, then <c>AddAIService</c>. The
/// service is resolved from that container rather than constructed, so the test fails if the
/// registration ever hands it the circuit's client again.
/// </para>
/// </remarks>
public sealed class AIServiceProviderCredentialTests
{
    private const string PortalApiKey = "portal-api-key";
    private const string UserToken = "signed-in-user-jwt";
    private const string ProviderKey = "provider-key";

    [Fact]
    public async Task OpenAI_is_sent_the_provider_key_and_nothing_of_the_APIs()
    {
        var network = new RecordingNetwork();
        using var portal = SignedInCircuit(network, new()
        {
            ["AI:Provider"] = "OpenAI",
            ["AI:ApiKey"] = ProviderKey,
            ["AI:Endpoint"] = "https://ai.example.test/v1"
        });

        var reply = await portal.Assistant.ChatAsync("hello");

        Assert.Equal("hello from the provider", reply);
        var request = Assert.Single(network.Requests);
        Assert.Equal("https://ai.example.test/v1/responses", request.Url);
        Assert.Equal([$"Authorization: Bearer {ProviderKey}"], request.Headers);
    }

    [Fact]
    public async Task Azure_OpenAI_is_sent_the_provider_key_and_nothing_of_the_APIs()
    {
        var network = new RecordingNetwork();
        using var portal = SignedInCircuit(network, new()
        {
            ["AI:Provider"] = "AzureOpenAI",
            ["AI:ApiKey"] = ProviderKey,
            ["AI:Endpoint"] = "https://ai.example.test",
            ["AI:AzureDeploymentName"] = "assistant"
        });

        var reply = await portal.Assistant.ChatAsync("hello");

        Assert.Equal("hello from the provider", reply);
        var request = Assert.Single(network.Requests);
        Assert.StartsWith("https://ai.example.test/openai/deployments/assistant/chat/completions", request.Url);
        Assert.Equal([$"api-key: {ProviderKey}"], request.Headers);
    }

    [Fact]
    public async Task Ollama_is_sent_no_credentials_at_all()
    {
        var network = new RecordingNetwork();
        using var portal = SignedInCircuit(network, new()
        {
            ["AI:Provider"] = "Ollama",
            ["AI:Endpoint"] = "http://ai.example.test:11434"
        });

        var reply = await portal.Assistant.ChatAsync("hello");

        Assert.Equal("hello from the provider", reply);
        var request = Assert.Single(network.Requests);
        Assert.Equal("http://ai.example.test:11434/api/chat", request.Url);
        Assert.Empty(request.Headers);
    }

    [Fact]
    public async Task The_circuits_client_would_have_carried_both_credentials_to_the_provider()
    {
        // The control for the three above: the same container, the same recorder, the same host.
        // Without it they could pass because the recorder never sees default headers at all.
        var network = new RecordingNetwork();
        using var portal = SignedInCircuit(network, new() { ["AI:Provider"] = "Ollama" });

        using var response = await portal.ApiClient.PostAsync("http://ai.example.test:11434/api/chat", null);

        var request = Assert.Single(network.Requests);
        Assert.Contains($"X-API-Key: {PortalApiKey}", request.Headers);
        Assert.Contains($"Authorization: Bearer {UserToken}", request.Headers);
    }

    /// <summary>One circuit of the Web with somebody signed in, and the assistant resolved from it.</summary>
    private static Portal SignedInCircuit(RecordingNetwork network, Dictionary<string, string?> aiSettings)
    {
        var services = new ServiceCollection();

        // The API's client and the circuit's HttpClient, as Program.cs registers them.
        services.AddHttpClient("ShopInventoryApi", client =>
        {
            client.BaseAddress = new Uri("http://localhost:5106/");
            client.DefaultRequestHeaders.Add("X-API-Key", PortalApiKey);
            WebApiClientIdentity.IdentifyAsWebPortal(client);
        })
        .ConfigurePrimaryHttpMessageHandler(() => network);
        services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("ShopInventoryApi"));

        services.AddSingleton(StubProxy.Unused<IInvoiceService>());
        services.AddSingleton(StubProxy.Unused<IMasterDataCacheService>());

        services.AddAIService(new ConfigurationBuilder().AddInMemoryCollection(aiSettings).Build());

        // Whatever client the assistant is given, its requests end at the recorder, not the network.
        services.AddHttpClient("AiProvider").ConfigurePrimaryHttpMessageHandler(() => network);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        // What CustomAuthStateProvider does to the circuit's client at sign-in.
        var apiClient = scope.ServiceProvider.GetRequiredService<HttpClient>();
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken);

        return new Portal(provider, scope, apiClient, scope.ServiceProvider.GetRequiredService<IAIService>());
    }

    private sealed record Portal(ServiceProvider Provider, IServiceScope Scope, HttpClient ApiClient, IAIService Assistant)
        : IDisposable
    {
        public void Dispose()
        {
            Scope.Dispose();
            Provider.Dispose();
        }
    }

    private sealed record SentRequest(string Url, List<string> Headers);

    /// <summary>Every request that left the Web, with the headers it carried, answered as each provider would.</summary>
    private sealed class RecordingNetwork : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(
                request.RequestUri!.ToString(),
                request.Headers.Select(header => $"{header.Key}: {string.Join(", ", header.Value)}").ToList()));

            var body = request.RequestUri.AbsolutePath switch
            {
                var path when path.EndsWith("/responses", StringComparison.Ordinal) =>
                    """{"output":[{"type":"message","content":[{"type":"output_text","text":"hello from the provider"}]}]}""",
                var path when path.EndsWith("/chat/completions", StringComparison.Ordinal) =>
                    """{"choices":[{"message":{"content":"hello from the provider"}}]}""",
                _ => """{"message":{"content":"hello from the provider"}}"""
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
