using FugaTES.MvpPostgreSqlE2E;

if (args.SequenceEqual(new[] { "--offline-self-test" }))
    return OfflineChecks.Run();

using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    Guid? cleanup = null;
    if (args.Length == 2 && args[0] == "--cleanup-run" && Guid.TryParseExact(args[1], "D", out var id))
        cleanup = id;
    else if (args.Length != 0)
        throw new InvalidOperationException("INVALID_ARGUMENTS");
    var connection = Runner.ValidateRuntime(Environment.GetEnvironmentVariable);
    return await Runner.RunAsync(connection, cleanup, stop.Token);
}
catch
{
    Console.WriteLine("RUNNER_STATUS=FAIL_CLOSED");
    return 1;
}
