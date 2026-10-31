using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Persistencia.Transportadoras;
using Microsoft.Extensions.Configuration;
using Npgsql;
namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

// Liga o journal durable ao CREATE unitario ja existente (R31/R41). Nao habilita escrita por si:
// o caso de uso continua exigindo writeGate, aprovacao especifica, identidade e assinatura.
// Desligado por ausencia: sem Carrier:SingleCreate:Enabled no perfil HML nada e montado.
//
// A infraestrutura de transporte permanece FORA desta camada: ela entra por
// SingleCreateGatewayFactoryR42 (FugaTES.HostIntegracao), e esta classe apenas compoe o contrato
// comprovado com o predicado de autorizacao. Nenhum cliente de transporte e nomeado,
// construido ou possuido aqui.
public sealed record SingleCreateBindingR41(GuardianSingleCreateUseCaseR31? UseCase, string Code)
{
    public bool Ready => UseCase is not null;
    public override string ToString() => Code;
}

public static class SingleCreateDesktopBindingR41
{
    public const string JournalTable = SingleCreateJournalPostgreSqlR31.Table;

    // Preflight proprio do journal. Nao altera a verificacao compartilhada das tabelas
    // carrier_sync_*, para nao mover o contrato de preflight que o Codex tambem consome.
    // Exige exatamente SELECT/INSERT/UPDATE e ausencia de DELETE/TRUNCATE, como o papel de aplicacao.
    public const string JournalPrivilegeSql = """
        select case when to_regclass(@name) is null then false else
            has_table_privilege(current_user,@name,'SELECT')
            and has_table_privilege(current_user,@name,'INSERT')
            and has_table_privilege(current_user,@name,'UPDATE')
            and not has_table_privilege(current_user,@name,'DELETE,TRUNCATE') end
        """;

    public static async Task<bool> JournalReadyAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("set transaction read only", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(JournalPrivilegeSql, connection, transaction);
        command.Parameters.AddWithValue("name", JournalTable);
        var granted = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return granted;
    }

    // Autorizacao de escrita identica a que o R41 aplicava internamente: exige o gate ligado e o
    // escopo funcional exato (acao CREATE, BP e codigo aprovados, payload identico ao preparado).
    // O escopo e verificado pelo proprio EncodeWrite publico do R41, que lanca fora dele; assim a
    // regra permanece na preparacao e nao e reimplementada aqui.
    public static Func<CarrierWriteAttempt, bool> Autorizacao(PreparedSingleCreateR41 prepared, Func<bool>? gate)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return attempt =>
        {
            try { return gate?.Invoke() == true && prepared.EncodeWrite(attempt) is not null; }
            catch { return false; }
        };
    }

    public static async Task<SingleCreateBindingR41> BindAsync(IConfiguration config, PreparedSingleCreateR41 prepared,
        IGuardianCarrierManualReadOnly reader, SingleCreateGatewayFactoryR42 gatewayFactory,
        NpgsqlDataSource source, Func<bool>? gate = null, TimeProvider? clock = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(source);
        // Cancelamento e verificado antes das travas funcionais: um bind cancelado nao deve
        // reportar bloqueio de negocio que nao foi realmente avaliado.
        if (ct.IsCancellationRequested) return new(null, "SINGLE_CREATE_CANCELLED");
        if (!CarrierDesktopProfile.True(config, "Carrier:SingleCreate:Enabled"))
            return new(null, "SINGLE_CREATE_DESKTOP_DISABLED");
        if (config["Carrier:Environment"] != "HML")
            return new(null, "CARRIER_DESKTOP_HML_NOT_PROVEN");
        if (CarrierProtocolBinding.BuildGuardianContract(config) is not { } proven)
            return new(null, "CARRIER_DESKTOP_GUARDIAN_HML_NOT_PROVEN");
        if (!prepared.Ready) return new(null, prepared.Code);
        if (gatewayFactory is null) return new(null, "SINGLE_CREATE_GATEWAY_FACTORY_ABSENT");
        if(new NpgsqlConnectionStringBuilder(source.ConnectionString).Enlist)
            return new(null,"SINGLE_CREATE_DATASOURCE_ENLISTMENT_FORBIDDEN");
        try
        {
            if (!await JournalReadyAsync(source, ct).ConfigureAwait(false))
                return new(null, "SINGLE_CREATE_JOURNAL_MIGRATION_PENDING_GAIA");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(null, "SINGLE_CREATE_CANCELLED"); }
        catch { return new(null, "SINGLE_CREATE_JOURNAL_PREFLIGHT_FAILED"); }
        var journal = new SingleCreateJournalPostgreSqlR31(source, clock);
        // Mesma composicao que o R41 ja fazia: contrato comprovado ligado a rota de CREATE aprovada,
        // codec de resposta do R41 e autorizacao por tentativa. So o transporte vem de fora.
        IGuardianCarrierGateway writer;
        try { writer = gatewayFactory(prepared.BindSingleCreateContract(proven), Autorizacao(prepared, gate)); }
        catch { return new(null, "SINGLE_CREATE_GATEWAY_NOT_COMPOSED"); }
        return new(prepared.CreateUseCase(reader, writer, journal, gate, clock),
            "SINGLE_CREATE_BOUND_NOT_RUNTIME_AUTHORIZATION");
    }
}
