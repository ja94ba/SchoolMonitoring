namespace SafeClass.Application.Exceptions;

public class NotFoundException(string message) : Exception(message);

/// <summary>Invalid state transition, duplicate, or concurrent change.</summary>
public class ConflictException(string message) : Exception(message);

public class ForbiddenException(string message) : Exception(message);

public class UnauthorizedAppException(string message) : Exception(message);

public class AppValidationException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public AppValidationException(IDictionary<string, string[]> errors) : base("One or more validation errors occurred.")
    {
        Errors = new Dictionary<string, string[]>(errors);
    }

    public AppValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] }) { }
}
