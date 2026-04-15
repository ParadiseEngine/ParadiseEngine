using System.Collections;
using System.Globalization;

namespace Paradise.BLOB.Test;

public static class Assert
{
    public static void AreEqual<T>(T expected, T actual, string? message = null)
    {
        if (!ConstraintHelpers.AreEqual(expected!, actual!))
        {
            Fail(message ?? $"Expected: {ConstraintHelpers.Format(expected!)} Actual: {ConstraintHelpers.Format(actual!)}");
        }
    }

    public static void IsNotNull(object? value, string? message = null)
    {
        if (value is null)
        {
            Fail(message ?? "Expected value to be non-null.");
        }
    }

    public static void GreaterOrEqual<T>(T actual, T expected, string? message = null) where T : IComparable<T>
    {
        if (actual.CompareTo(expected) < 0)
        {
            Fail(message ?? $"Expected {ConstraintHelpers.Format(actual!)} to be greater than or equal to {ConstraintHelpers.Format(expected!)}.");
        }
    }

    public static void That(object actual, IConstraint constraint, string? message = null)
    {
        if (!constraint.Matches(actual, out string failure))
        {
            Fail(message is null ? failure : $"{message} {failure}");
        }
    }

    public static TException Catch<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            Fail($"Expected exception of type {typeof(TException).Name} but got {ex.GetType().Name}.");
        }

        Fail($"Expected exception of type {typeof(TException).Name} but no exception was thrown.");
        return null!;
    }

    private static void Fail(string message)
    {
        throw new InvalidOperationException(message);
    }
}

public static class Is
{
    public static IConstraint EqualTo(object expected) => new EqualConstraint(expected);

    public static IConstraint EquivalentTo(IEnumerable expected) => new EquivalentConstraint(expected);
}

public interface IConstraint
{
    bool Matches(object actual, out string failure);
}

internal sealed class EqualConstraint : IConstraint
{
    private readonly object _expected;

    public EqualConstraint(object expected)
    {
        _expected = expected;
    }

    public bool Matches(object actual, out string failure)
    {
        if (ConstraintHelpers.AreEqual(_expected, actual))
        {
            failure = string.Empty;
            return true;
        }

        failure = $"Expected: {ConstraintHelpers.Format(_expected)} Actual: {ConstraintHelpers.Format(actual)}";
        return false;
    }
}

internal sealed class EquivalentConstraint : IConstraint
{
    private readonly object[] _expected;

    public EquivalentConstraint(IEnumerable expected)
    {
        _expected = expected.Cast<object>().ToArray();
    }

    public bool Matches(object actual, out string failure)
    {
        if (actual is not IEnumerable actualEnumerable)
        {
            failure = $"Expected an enumerable but got {actual?.GetType().Name ?? "<null>"}";
            return false;
        }

        var actualItems = actualEnumerable.Cast<object>().ToArray();
        if (_expected.Length != actualItems.Length)
        {
            failure = $"Expected collection length {_expected.Length} but got {actualItems.Length}.";
            return false;
        }

        var remaining = new List<object>(_expected);
        foreach (var item in actualItems)
        {
            int index = remaining.FindIndex(candidate => ConstraintHelpers.AreEqual(candidate, item));
            if (index < 0)
            {
                failure = $"Unexpected item {ConstraintHelpers.Format(item)}.";
                return false;
            }

            remaining.RemoveAt(index);
        }

        if (remaining.Count > 0)
        {
            failure = $"Missing items: {string.Join(", ", remaining.Select(ConstraintHelpers.Format))}.";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}

internal static class ConstraintHelpers
{
    public static bool AreEqual(object? expected, object? actual)
    {
        if (ReferenceEquals(expected, actual))
        {
            return true;
        }

        if (expected is null || actual is null)
        {
            return false;
        }

        if (expected is string expectedString && actual is string actualString)
        {
            return expectedString == actualString;
        }

        if (TryGetEnumerable(expected, out var expectedEnumerable) &&
            TryGetEnumerable(actual, out var actualEnumerable))
        {
            return AreSequencesEqual(expectedEnumerable!, actualEnumerable!);
        }

        if (IsNumeric(expected) && IsNumeric(actual))
        {
            return AreNumericEqual(expected, actual);
        }

        return expected.Equals(actual);
    }

    public static string Format(object? value)
    {
        if (value is null)
        {
            return "<null>";
        }

        if (value is string s)
        {
            return $"\"{s}\"";
        }

        if (TryGetEnumerable(value, out var enumerable))
        {
            return "[" + string.Join(", ", enumerable!.Cast<object>().Select(Format)) + "]";
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? value.GetType().Name;
    }

    private static bool TryGetEnumerable(object value, out IEnumerable? enumerable)
    {
        if (value is string)
        {
            enumerable = null;
            return false;
        }

        enumerable = value as IEnumerable;
        return enumerable is not null;
    }

    private static bool AreSequencesEqual(IEnumerable expected, IEnumerable actual)
    {
        object[] expectedItems = expected.Cast<object>().ToArray();
        object[] actualItems = actual.Cast<object>().ToArray();

        if (expectedItems.Length != actualItems.Length)
        {
            return false;
        }

        for (int i = 0; i < expectedItems.Length; i++)
        {
            if (!AreEqual(expectedItems[i], actualItems[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreNumericEqual(object expected, object actual)
    {
        if (expected is float or double || actual is float or double)
        {
            return Convert.ToDouble(expected, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(actual, CultureInfo.InvariantCulture));
        }

        if (expected is decimal || actual is decimal)
        {
            return Convert.ToDecimal(expected, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDecimal(actual, CultureInfo.InvariantCulture));
        }

        return Convert.ToInt64(expected, CultureInfo.InvariantCulture)
            .Equals(Convert.ToInt64(actual, CultureInfo.InvariantCulture));
    }

    private static bool IsNumeric(object value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }
}
