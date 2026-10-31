using Npgsql;

namespace FugaTES.MvpPostgreSqlE2E;

internal static class OfflineChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("OFFLINE_ASSERTION_FAILED"); count++; }
        void Reject(Dictionary<string, string?> env)
        {
            try { Runner.ValidateRuntime(k => env.GetValueOrDefault(k)); }
            catch { count++; return; }
            throw new InvalidOperationException("EXPECTED_FAIL_CLOSED");
        }
        try
        {
            var env = new Dictionary<string, string?>();
            Reject(env);
            env["FUGATES_MVP_E2E_ENABLED"] = "true"; Reject(env);
            env["FUGATES_MVP_E2E_ISOLATED_SESSION"] = "true"; Reject(env);
            // Synthetic configuration is parsed only; no data source or connection is created.
            var config = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 15432,
                Database = "fugates_dev", Username = "OFFLINE_ONLY", Password = "SYNTHETIC_ONLY", SearchPath = "fugates" };
            env["ConnectionStrings__FugaTES"] = config.ConnectionString;
            Check(!string.IsNullOrWhiteSpace(Runner.ValidateRuntime(k => env.GetValueOrDefault(k))));
            foreach (var bad in new[] { "Host=external.invalid", "Port=5432", "Database=other", "Username=", "Password=", "Search Path=public" })
            {
                env["ConnectionStrings__FugaTES"] = config.ConnectionString + ";" + bad; Reject(env);
            }
            env["ConnectionStrings__FugaTES"] = config.ConnectionString;
            env["FUGATES_MVP_E2E_ENABLED"] = "True"; Reject(env);
            var a = new FixtureIdentity(Guid.NewGuid()); var b = new FixtureIdentity(Guid.NewGuid());
            Check(a.Key != b.Key && a.Fingerprint != b.Fingerprint);
            Check(a.Fingerprint.Length == 64 && a.Fingerprint.All(Uri.IsHexDigit));
            Check(a == new FixtureIdentity(a.RunId));
            Check(Runner.Proofs.Length == 11 && Runner.Proofs.Distinct().Count() == 11);
            foreach (var line in Runner.CleanupSql.Split('\n').Where(l => l.TrimStart().StartsWith("delete", StringComparison.Ordinal)))
                Check(line.Contains("where", StringComparison.Ordinal) && line.Contains("@id", StringComparison.Ordinal));
            Check(!Runner.CleanupSql.Contains("truncate", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"OFFLINE_TESTS_PASS={count}");
            Console.WriteLine("OFFLINE_TESTS_FAIL=0");
            Console.WriteLine("REAL_DATABASE_CALLS=0");
            return 0;
        }
        catch { Console.WriteLine("OFFLINE_TESTS=FAIL"); return 1; }
    }
}
