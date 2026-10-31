using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.Persistencia.Recebimento;

namespace FugaTES.HmlFixture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var enabled = Environment.GetEnvironmentVariable("FUGATES_HML_FIXTURE_ENABLED");
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            var protocol = Environment.GetEnvironmentVariable("PROTOCOLO_GUARDIAN_TESTE");
            FixtureRunner.ValidateExecutionGuard(enabled, environment);
            var input = ReadInput(args);
            FixtureRunner.ValidateGuards(enabled, environment, protocol, input);
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FugaTES");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Runtime database configuration is absent.");

            await using var dataSource = NpgsqlDataSourceFactory.Criar(connectionString);
            var result = await FixtureRunner.ExecuteAsync(enabled, environment, protocol, input,
                new RepositorioCorrelacaoGuardianSapPostgreSql(dataSource),
                new PostgresFixtureReadback(dataSource), CancellationToken.None);
            Console.WriteLine("FIXTURE_STATUS=CREATED");
            Console.WriteLine($"CORRELACAO_ID={result.CorrelacaoId}");
            Console.WriteLine($"PROTOCOLO_GUARDIAN_TESTE={result.ProtocoloGuardianTeste}");
            Console.WriteLine($"TICKET_GUARDIAN_IS_NULL={result.TicketGuardianIsNull}");
            Console.WriteLine($"PARENT_ROW_COUNT={result.ParentRowCount}");
            Console.WriteLine($"EVENT_LEDGER_ROW_COUNT={result.EventLedgerRowCount}");
            return 0;
        }
        catch (FixtureAssociationException)
        {
            Console.Error.WriteLine("FIXTURE_STATUS=ASSOCIATION_FAILED_PARENT_MAY_EXIST");
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("FIXTURE_STATUS=FAILED");
            return 1;
        }
    }

    private static RecebimentoSapCpiMvp? ReadInput(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
            throw new InvalidOperationException("An explicit HML snapshot JSON file is required.");
        return JsonSerializer.Deserialize<RecebimentoSapCpiMvp>(File.ReadAllText(args[0]),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
