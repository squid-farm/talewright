using System.Runtime.InteropServices;
using Talewright.Cli;

if (args.Length == 0 || args[0] is not ("run" or "debug"))
{
    return CommandLine.Run(args, Console.Out, Console.Error);
}

using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cancellation.Cancel();
});
ConsoleCancelEventHandler handler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += handler;
try
{
    return CommandLine.Run(args, Console.Out, Console.Error, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= handler;
}
