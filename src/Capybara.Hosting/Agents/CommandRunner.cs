using System.Diagnostics;

namespace Capybara.Hosting.Agents;

/// <summary>Command execution seam that always passes executable and arguments separately, never shell strings (FR-HST-01).</summary>
public interface ICommandRunner
{
    ValueTask<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A process invocation with argument-list semantics and optional standard input (FR-HST-01).</summary>
public sealed record CommandRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? StandardInput = null);

/// <summary>Captured command completion details (FR-HST-01).</summary>
public sealed record CommandRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Production command runner based on <see cref="ProcessStartInfo.ArgumentList" /> (FR-HST-01).</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public async ValueTask<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            UseShellExecute = false,
        };

        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CommandRunResult(process.ExitCode, await stdout, await stderr);
    }
}
