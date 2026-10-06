using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;
using Runspace.Core;

namespace Runspace.PowerShell;

internal sealed class InvocationContext(Guid sessionId, Guid id, CancellationToken token,
    Func<HostPrompt, CancellationToken, Task<HostResponse?>>? handler, Action<InvocationStatus> notify) : IDisposable
{
    private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
    private readonly List<HostResponse> responses = [];
    private readonly object gate = new();
    private bool finished;
    public CancellationToken Token => cancellation.Token;
    public bool HasInput { get; private set; }
    public bool HasSecrets { get; private set; }
    public bool Stop(Guid invocationId)
    {
        lock (gate)
        {
            if (finished || invocationId != id) return false;
            cancellation.Cancel();
            return true;
        }
    }

    public void Finish() { lock (gate) finished = true; }

    public void Notify(InvocationState state)
    {
        lock (gate)
        {
            if (finished || Token.IsCancellationRequested && state is InvocationState.Running or InvocationState.AwaitingInput) return;
            notify(new(sessionId, id, state));
        }
    }

    public HostResponse Ask(HostPromptKind kind, string caption, string message, IReadOnlyList<HostField> fields,
        IReadOnlyList<HostChoice>? choices = null, int defaultChoice = -1,
        Func<HostResponse, string?>? validate = null)
    {
        Token.ThrowIfCancellationRequested();
        if (handler is null)
            throw new NotSupportedException("This invocation requires interactive host input, but no prompt handler is connected.");
        HasInput = true;
        HasSecrets |= kind is HostPromptKind.Credential or HostPromptKind.SecureInput ||
            fields.Any(field => field.TypeName is "System.Security.SecureString" or "System.Management.Automation.PSCredential");
        var prompt = new HostPrompt(sessionId, id, Guid.NewGuid(), kind, caption, message, fields, choices ?? [],
            defaultChoice, response => Task.Run(() => validate?.Invoke(response), Token));
        Notify(InvocationState.AwaitingInput);
        try
        {
            // Only the engine thread waits; the dispatcher presents the prompt asynchronously.
            var pending = handler(prompt, Token);
            HostResponse? response;
            try { response = pending.WaitAsync(Token).GetAwaiter().GetResult(); }
            catch
            {
                _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result?.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            if (response is null)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(Token);
            }
            responses.Add(response);
            Token.ThrowIfCancellationRequested();
            var error = validate?.Invoke(response);
            if (error is not null) throw new ArgumentException(error);
            return response;
        }
        finally { Notify(InvocationState.Running); }
    }

    public void Dispose()
    {
        Finish();
        foreach (var response in responses) response.Dispose();
        cancellation.Dispose();
    }
}

internal sealed class InvocationHost : PSHost
{
    private readonly Guid id = Guid.NewGuid();
    private readonly InvocationHostUI ui;
    public InvocationHost(Action<string, string> write) => ui = new(this, write);
    private InvocationContext? current;
    public InvocationContext? Current { get => Volatile.Read(ref current); set => Volatile.Write(ref current, value); }
    public InvocationContext Context => Current ?? throw new NotSupportedException("Host input is only supported within an invocation.");
    public override Guid InstanceId => id;
    public override string Name => "Runspace";
    public override Version Version => new(1, 0);
    public override PSHostUserInterface UI => ui;
    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
    public override void SetShouldExit(int exitCode) => throw new NotSupportedException("Scripts cannot close the console host.");
    public override void EnterNestedPrompt() => throw new NotSupportedException("Nested terminal prompts are not supported by this GUI host.");
    public override void ExitNestedPrompt() => throw new NotSupportedException("Nested terminal prompts are not supported by this GUI host.");
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
}

internal sealed class InvocationHostUI(InvocationHost host, Action<string, string> write) : PSHostUserInterface
{
    private readonly PSHostRawUserInterface raw = new GuiRawUI();
    public override PSHostRawUserInterface RawUI => raw;
    public override string ReadLine() => (string)host.Context.Ask(HostPromptKind.Input, "Input", string.Empty,
        [new("Value", "Value", "System.String", false)]).Values["Value"]!;
    public override SecureString ReadLineAsSecureString() => (SecureString)host.Context.Ask(HostPromptKind.SecureInput,
        "Secure input", "Input is not stored in history.", [new("Value", "Value", "System.Security.SecureString", true)]).Values["Value"]!;

    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
    {
        var types = descriptions.ToDictionary(field => field.Name, field =>
            Type.GetType(field.ParameterAssemblyFullName, throwOnError: true)!);
        var fields = descriptions.Select(field => new HostField(field.Name,
            string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label, types[field.Name].FullName!, field.IsMandatory,
            field.DefaultValue?.BaseObject is string text ? text : null, field.HelpMessage)).ToArray();
        Dictionary<string, PSObject> ConvertResponse(HostResponse response) => fields.ToDictionary(field => field.Name,
            field => PSObject.AsPSObject(ConvertField(response, field, types[field.Name])));
        string? Validate(HostResponse response)
        {
            try { ConvertResponse(response); return null; }
            catch (Exception exception) when (exception is PSInvalidCastException or ArgumentException)
            {
                // Conversion errors may include the input value.
                return "Enter all required fields using the displayed PowerShell types.";
            }
        }
        var kind = fields.Count(field => field.TypeName == "System.Security.SecureString") == fields.Length && fields.Length > 0
            ? HostPromptKind.SecureInput : HostPromptKind.Fields;
        return ConvertResponse(host.Context.Ask(kind, caption, message, fields, validate: Validate));
    }

    private static object ConvertField(HostResponse response, HostField field, Type type)
    {
        if (!response.Values.TryGetValue(field.Name, out var value) ||
            field.Required && (value is null || value is string text && string.IsNullOrWhiteSpace(text) ||
                value is SecureString secure && secure.Length == 0 ||
                value is HostCredential requiredCredential && (string.IsNullOrWhiteSpace(requiredCredential.UserName) || requiredCredential.Password.Length == 0)))
            throw new ArgumentException("A required field is missing.");
        if (type == typeof(PSCredential) && value is HostCredential credential)
            return new PSCredential(credential.UserName, credential.Password);
        if (type == typeof(bool) && value is string boolean)
            return bool.TryParse(boolean, out var parsed) ? parsed : throw new ArgumentException("Enter True or False.");
        return LanguagePrimitives.ConvertTo(value, type, CultureInfo.CurrentCulture);
    }

    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
    {
        string? Validate(HostResponse response) => response.Choice is { } index && index >= 0 && index < choices.Count
            ? null : "Select one of the offered choices.";
        return host.Context.Ask(HostPromptKind.Choice, caption, message, [],
            choices.Select(choice => new HostChoice(choice.Label, choice.HelpMessage)).ToArray(), defaultChoice, Validate).Choice!.Value;
    }

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
        PromptForCredential(caption, message, userName, targetName, PSCredentialTypes.Default, PSCredentialUIOptions.Default);

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName,
        PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
    {
        var response = host.Context.Ask(HostPromptKind.Credential, caption, message,
            [new("Credential", "Credential", "System.Management.Automation.PSCredential", true, userName,
                Help: string.IsNullOrWhiteSpace(targetName) ? null : $"Target: {targetName}",
                IsReadOnly: options == PSCredentialUIOptions.ReadOnlyUserName)],
            validate: response => response.Values.GetValueOrDefault("Credential") is HostCredential credential &&
                !string.IsNullOrWhiteSpace(credential.UserName) && credential.Password.Length > 0 &&
                (options != PSCredentialUIOptions.ReadOnlyUserName || credential.UserName == userName)
                ? null : "A user name and password are required.");
        var credential = (HostCredential)response.Values["Credential"]!;
        return new PSCredential(credential.UserName, credential.Password);
    }

    public override void Write(string value) => write("Host", value);
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => Write(value);
    public override void WriteLine(string value) => Write(value);
    public override void WriteErrorLine(string value) => write("Error", value);
    public override void WriteDebugLine(string message) => write("Debug", message);
    public override void WriteVerboseLine(string message) => write("Verbose", message);
    public override void WriteWarningLine(string message) => write("Warning", message);
    public override void WriteProgress(long sourceId, ProgressRecord record) => write("Progress", $"{record.Activity}: {record.StatusDescription}");
}

internal sealed class GuiRawUI : PSHostRawUserInterface
{
    private static NotSupportedException Unsupported() => new("Raw terminal operations are not supported by this GUI host.");
    public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Black;
    public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.White;
    public override Coordinates CursorPosition { get => new(0, 0); set => throw Unsupported(); }
    public override Coordinates WindowPosition { get => new(0, 0); set => throw Unsupported(); }
    public override int CursorSize { get => 25; set => throw Unsupported(); }
    public override Size BufferSize { get => new(120, 30); set => throw Unsupported(); }
    public override Size WindowSize { get => new(120, 30); set => throw Unsupported(); }
    public override Size MaxWindowSize => new(120, 30);
    public override Size MaxPhysicalWindowSize => new(120, 30);
    public override bool KeyAvailable => throw Unsupported();
    public override string WindowTitle { get => "Runspace"; set => throw Unsupported(); }
    public override KeyInfo ReadKey(ReadKeyOptions options) => throw Unsupported();
    public override void FlushInputBuffer() => throw Unsupported();
    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) => throw Unsupported();
    public override void SetBufferContents(Rectangle rectangle, BufferCell fill) => throw Unsupported();
    public override BufferCell[,] GetBufferContents(Rectangle rectangle) => throw Unsupported();
    public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) => throw Unsupported();
}
