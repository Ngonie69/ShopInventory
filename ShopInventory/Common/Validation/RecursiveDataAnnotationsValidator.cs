using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace ShopInventory.Common.Validation;

public static class RecursiveDataAnnotationsValidator
{
    public static List<string> Validate(object? instance)
    {
        var errors = new List<string>();

        if (instance is null)
        {
            errors.Add("Request body is required");
            return errors;
        }

        ValidateInstance(instance, errors, null);
        return errors;
    }

    private static void ValidateInstance(object instance, List<string> errors, string? path)
    {
        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(instance);

        Validator.TryValidateObject(instance, context, validationResults, true);

        foreach (var validationResult in validationResults)
        {
            var message = validationResult.ErrorMessage ?? "Validation failed";
            var memberNames = validationResult.MemberNames?.ToList();

            if (memberNames is { Count: > 0 })
            {
                foreach (var memberName in memberNames)
                {
                    var fullPath = string.IsNullOrWhiteSpace(path)
                        ? memberName
                        : $"{path}.{memberName}";

                    errors.Add($"{fullPath}: {message}");
                }

                continue;
            }

            errors.Add(string.IsNullOrWhiteSpace(path) ? message : $"{path}: {message}");
        }

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            var value = property.GetValue(instance);
            if (value is null || value is string)
                continue;

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (IsSimpleType(propertyType))
                continue;

            var propertyPath = string.IsNullOrWhiteSpace(path)
                ? property.Name
                : $"{path}.{property.Name}";

            if (value is IEnumerable enumerable)
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    if (item is null || item is string)
                    {
                        index++;
                        continue;
                    }

                    var itemType = Nullable.GetUnderlyingType(item.GetType()) ?? item.GetType();
                    if (!IsSimpleType(itemType))
                        ValidateInstance(item, errors, $"{propertyPath}[{index}]");

                    index++;
                }

                continue;
            }

            ValidateInstance(value, errors, propertyPath);
        }
    }

    private static bool IsSimpleType(Type type)
    {
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid);
    }
}