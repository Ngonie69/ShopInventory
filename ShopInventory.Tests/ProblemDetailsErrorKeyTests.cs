using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Controllers;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The refusal body <see cref="ApiControllerBase"/> writes has exactly one <c>errors</c> key, and
/// that key always means the same thing.
/// </summary>
/// <remarks>
/// <para>
/// It used to carry two. <see cref="ValidationProblemDetails"/> serialises its own dictionary under
/// the JSON name <c>errors</c>, and the controller base then wrote a second, differently shaped
/// <c>errors</c> into <see cref="ProblemDetails.Extensions"/> — which is <c>[JsonExtensionData]</c>,
/// so System.Text.Json emitted both, in the same object, for every endpoint returning an
/// all-Validation <see cref="ErrorOr"/> result. Nothing here broke because
/// <see cref="JsonDocument"/> tolerates a duplicate and hands back the first match, but a strict
/// parser is entitled to reject the whole document, and the handset app and any third-party caller
/// are the ones that would.
/// </para>
/// <para>
/// The dictionary is the contract: it is the RFC 9457 / ASP.NET shape, it is what API.md documents,
/// and it is what every reader in ShopInventory.Web is already written against. The code-and-type
/// detail moved to <c>errorDetails</c>, following <c>ValidationExceptionHandler</c>, which already
/// hangs its side-channel off <c>errorCodes</c> for the same reason.
/// </para>
/// <para>
/// The count is taken with a <see cref="Utf8JsonReader"/> and not with <see cref="JsonDocument"/>,
/// because JsonDocument is exactly the thing that hid the bug: it would have reported one key
/// while the wire carried two.
/// </para>
/// </remarks>
public sealed partial class ProblemDetailsErrorKeyTests
{
    [Fact]
    public void An_all_validation_refusal_names_errors_once()
    {
        var body = Refuse(
            Error.Validation("WhatsApp.Disabled", "WhatsApp integration is disabled"),
            Error.Validation("WhatsApp.NoSession", "No session is paired"));

        Assert.Equal(1, CountTopLevelKeys(body, "errors"));
    }

    /// <summary>
    /// The other branch of <see cref="ApiControllerBase.Problem(List{Error})"/>, which builds a plain
    /// <see cref="ProblemDetails"/> and never had a dictionary to collide with. Guarded so that
    /// naming the extension back to <c>errors</c> cannot pass here while failing above.
    /// </summary>
    [Theory]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Failure)]
    [InlineData(ErrorType.Unexpected)]
    public void A_refusal_of_any_other_type_names_errors_at_most_once(ErrorType type)
    {
        var body = Refuse(Error.Custom((int)type, "Order.Missing", "Order 4711 was not found"));

        Assert.InRange(CountTopLevelKeys(body, "errors"), 0, 1);
    }

    /// <summary>
    /// The one <c>errors</c> is the dictionary, not the array — the half of the pair that survived
    /// is the half API.md documents and the Web reads.
    /// </summary>
    [Fact]
    public void The_surviving_errors_key_is_the_dictionary()
    {
        var body = Refuse(Error.Validation("WhatsApp.Disabled", "WhatsApp integration is disabled"));

        using var document = JsonDocument.Parse(body);
        var errors = document.RootElement.GetProperty("errors");

        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        Assert.Equal(
            "WhatsApp integration is disabled",
            errors.GetProperty("WhatsApp.Disabled")[0].GetString());
    }

    /// <summary>
    /// The code and type the array carried are not lost, only renamed.
    /// </summary>
    [Fact]
    public void The_code_and_type_move_to_errorDetails()
    {
        var body = Refuse(Error.Validation("WhatsApp.Disabled", "WhatsApp integration is disabled"));

        using var document = JsonDocument.Parse(body);
        var detail = document.RootElement.GetProperty("errorDetails")[0];

        Assert.Equal("WhatsApp.Disabled", detail.GetProperty("code").GetString());
        Assert.Equal("WhatsApp integration is disabled", detail.GetProperty("description").GetString());
        Assert.Equal("Validation", detail.GetProperty("type").GetString());
        Assert.Equal("WhatsApp.Disabled", document.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// The reader in ShopInventory.Web still finds the sentence to put on screen. It reads the
    /// dictionary, so the rename costs it nothing — this is the check that says so rather than
    /// assuming it.
    /// </summary>
    [Fact]
    public void The_web_still_reads_a_message_out_of_the_refusal()
    {
        var body = Refuse(Error.Validation("WhatsApp.Disabled", "WhatsApp integration is disabled"));

        Assert.Equal(
            "WhatsApp integration is disabled.",
            ApiErrorResponse.GetFriendlyMessage(
                System.Net.HttpStatusCode.BadRequest,
                body,
                "Something went wrong."));
    }

    /// <summary>
    /// And on the branch that has no dictionary at all, where the rename takes <c>errors</c> away
    /// entirely and the reader has to fall through to <c>detail</c>.
    /// </summary>
    [Fact]
    public void The_web_still_reads_a_message_off_the_non_validation_branch()
    {
        var body = Refuse(Error.NotFound("Order.Missing", "Order 4711 was not found"));

        Assert.Equal(
            "Order 4711 was not found.",
            ApiErrorResponse.GetFriendlyMessage(
                System.Net.HttpStatusCode.NotFound,
                body,
                "Something went wrong."));
    }

    /// <summary>
    /// Serialises the refusal the way MVC does: <c>JsonSerializerDefaults.Web</c>, which is what
    /// <c>AddControllers().AddJsonOptions(...)</c> in Program.cs leaves in place — it sets number
    /// handling and no naming policy, so the camelCase default stands.
    /// </summary>
    private static string Refuse(params Error[] errors)
    {
        var controller = new RefusingController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Path = "/api/whatsapp/sessions" }
                }
            }
        };

        var result = Assert.IsAssignableFrom<ObjectResult>(controller.Refuse([.. errors]));

        return JsonSerializer.Serialize(
            result.Value,
            result.Value!.GetType(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>
    /// Counts how many times <paramref name="name"/> appears as a property of the root object.
    /// Depth 1 only, so an <c>errors</c> nested inside the dictionary's own value is not counted.
    /// </summary>
    private static int CountTopLevelKeys(string json, string name)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        var count = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName
                && reader.CurrentDepth == 1
                && reader.ValueTextEquals(name))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The same collision, caught for every extension member anyone writes from here on rather than
    /// only for the one that caused it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>errors</c> is not special: <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c> and
    /// <c>instance</c> are declared members of <see cref="ProblemDetails"/> too, and writing any of
    /// them into <see cref="ProblemDetails.Extensions"/> duplicates that key in exactly the same
    /// silent way. Both projects build problem details in a dozen handlers, so the names are read
    /// off the framework types by reflection instead of being listed here — a member added to
    /// <see cref="ProblemDetails"/> in a future ASP.NET Core becomes reserved without an edit.
    /// </para>
    /// <para>
    /// A source scan rather than a runtime one, because there is no single place every problem
    /// details object passes through and no test would otherwise see a handler nobody exercises.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_extension_member_is_named_after_a_declared_one()
    {
        var reserved = ReservedMemberNames();
        Assert.Contains("errors", reserved);

        var offenders = new List<string>();

        foreach (var project in new[] { "ShopInventory", "ShopInventory.Web" })
        {
            var root = Path.Combine(RepositoryRoot(), project);

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Generated and build output, which nobody edits and which would swamp the message.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);

                for (var index = 0; index < lines.Length; index++)
                {
                    var match = ExtensionAssignment().Match(lines[index]);
                    if (match.Success && reserved.Contains(match.Groups[1].Value))
                    {
                        offenders.Add(
                            $"{Path.GetRelativePath(RepositoryRoot(), file)}:{index + 1}: {lines[index].Trim()}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These write an extension member that ProblemDetails already declares, so the key lands "
            + "in the response twice:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The JSON names <see cref="ValidationProblemDetails"/> and its base already write, which an
    /// extension member therefore may not reuse. <c>Extensions</c> itself is left out: it is the
    /// <c>[JsonExtensionData]</c> bag rather than a member of its own.
    /// </summary>
    private static HashSet<string> ReservedMemberNames()
        => [.. typeof(ValidationProblemDetails)
            .GetProperties()
            .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name))];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    [GeneratedRegex(@"Extensions\[""([^""]+)""\]\s*=")]
    private static partial Regex ExtensionAssignment();

    private sealed class RefusingController : ApiControllerBase
    {
        public IActionResult Refuse(List<Error> errors) => Problem(errors);
    }
}
