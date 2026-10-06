using System.Security;

namespace Runspace.Core;

public enum InvocationState { Running, AwaitingInput, Stopping, Unresponsive }
public sealed record InvocationStatus(Guid SessionId, Guid InvocationId, InvocationState State);
public enum HostPromptKind { Fields, Choice, Credential, SecureInput, Input }
public sealed record HostField(string Name, string Label, string TypeName, bool Required,
    string? DefaultValue = null, string? Help = null, bool IsReadOnly = false);
public sealed record HostChoice(string Label, string? Help);

public sealed record HostPrompt(Guid SessionId, Guid InvocationId, Guid Id, HostPromptKind Kind,
    string Caption, string Message, IReadOnlyList<HostField> Fields, IReadOnlyList<HostChoice> Choices,
    int DefaultChoice, Func<HostResponse, Task<string?>> ValidateAsync);

// Ownership transfers to the invocation, which disposes secure values after the pipeline ends.
public sealed record HostCredential(string UserName, SecureString Password);
public sealed record HostResponse(IReadOnlyDictionary<string, object?> Values, int? Choice = null) : IDisposable
{
    public void Dispose()
    {
        foreach (var value in Values.Values)
        {
            if (value is SecureString secure) secure.Dispose();
            if (value is HostCredential credential) credential.Password.Dispose();
        }
    }
}

public interface IInvocationHostSession
{
    Func<HostPrompt, CancellationToken, Task<HostResponse?>>? PromptHandler { get; set; }
    event Action<InvocationStatus>? StateChanged;
    bool StopInvocation(Guid invocationId);
}

public enum ActionResultPolicy { Retain, Refresh, Replace, Related }
