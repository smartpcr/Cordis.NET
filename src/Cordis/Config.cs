using System.ComponentModel.DataAnnotations;

namespace Cordis;

public sealed record ConfigIssue(string Message, string? Path = null)
{
    public override string ToString() => Path is null ? $"  - {Message}" : $"  - {Message} (at {Path})";
}

public sealed class ConfigValidationException(IReadOnlyList<ConfigIssue> issues)
    : CordisException("invalid config:\n" + string.Join('\n', issues))
{
    public IReadOnlyList<ConfigIssue> Issues { get; } = issues;
}

/// <summary>
/// Validator/normaliser for plugin config. The analogue of cordis' Standard Schema hook:
/// synchronous only, returns the normalised value or throws <see cref="ConfigValidationException"/>.
/// </summary>
public interface IConfigSchema
{
    Type ConfigType { get; }

    object? Validate(object? input);

    /// <summary>Reverse transform, used by the loader when writing config back to disk.</summary>
    object? Simplify(object? value) => value;
}

/// <summary>Validates a POCO with <c>System.ComponentModel.DataAnnotations</c> attributes.</summary>
public sealed class DataAnnotationsSchema<T> : IConfigSchema where T : class, new()
{
    public static readonly DataAnnotationsSchema<T> Instance = new();

    public Type ConfigType => typeof(T);

    public object? Validate(object? input)
    {
        var value = input switch
        {
            null => new T(),
            T typed => typed,
            _ => throw new ConfigValidationException([new ConfigIssue($"expected {typeof(T).Name}, got {input.GetType().Name}")]),
        };

        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true))
        {
            return value;
        }

        var issues = results
            .Select(r => new ConfigIssue(r.ErrorMessage ?? "invalid value", r.MemberNames.FirstOrDefault()))
            .ToArray();
        throw new ConfigValidationException(issues);
    }
}

/// <summary>Schema built from a delegate. Use for hand-rolled validation or normalisation.</summary>
public sealed class DelegateSchema<T>(Func<object?, T> validate, Func<T, object?>? simplify = null) : IConfigSchema
{
    public Type ConfigType => typeof(T);

    public object? Validate(object? input) => validate(input);

    public object? Simplify(object? value) => simplify is null || value is not T typed ? value : simplify(typed);
}
