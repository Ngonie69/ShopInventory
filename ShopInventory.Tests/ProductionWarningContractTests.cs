using System.ComponentModel.DataAnnotations;
using ShopInventory.Common.Validation;
using ShopInventory.DTOs;

namespace ShopInventory.Tests;

public class ProductionWarningContractTests
{
    [Fact]
    public void Firebase_registration_token_shape_is_accepted()
    {
        var request = new RegisterDeviceRequest
        {
            DeviceToken = "dQw4w9WgXcQ-token_123:APA91b",
            Platform = "Android"
        };

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void Push_registration_rejects_non_token_characters()
    {
        var request = new RegisterDeviceRequest
        {
            DeviceToken = "token<script>",
            Platform = "Android"
        };

        Assert.Contains(
            Validate(request),
            result => result.MemberNames.Contains(nameof(RegisterDeviceRequest.DeviceToken)));
    }

    [Fact]
    public void Pod_report_is_complete_by_default()
    {
        var report = new PodUploadStatusReportDto();

        Assert.True(report.CreditNoteDataComplete);
        Assert.Null(report.CreditNoteDataWarning);
    }

    [Theory]
    [InlineData("yog149", "YOG149")]
    [InlineData(" YOG149 ", "YOG149")]
    public void Item_codes_are_normalized_to_the_canonical_sap_case(
        string itemCode,
        string expected)
    {
        Assert.Equal(expected, UomQuantityValidation.NormalizeItemCode(itemCode));
    }

    private static IReadOnlyCollection<ValidationResult> Validate(object value)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(
            value,
            new ValidationContext(value),
            results,
            validateAllProperties: true);
        return results;
    }
}
