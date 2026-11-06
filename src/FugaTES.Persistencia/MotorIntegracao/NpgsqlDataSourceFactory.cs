using Npgsql;
using FugaTES.Aplicacao.Configuracao;

namespace FugaTES.Persistencia.MotorIntegracao;

public static class NpgsqlDataSourceFactory
{
    public const string ConnectionStringName = "FugaTES";
    public const string ConnectionStringKey = "ConnectionStrings:FugaTES";
    public const string AcmApplicationRole = "fugates_acm_hml_app";

    /// <summary>
    /// Identidade exclusiva proposta para o receptor SAP. Fica aqui, junto da role leitora, para
    /// que ambas passem exatamente pelas MESMAS guardas de conexao: destino 5433/fugates_dev,
    /// schema fugates, host loopback, Passfile explicito e verificacao de identidade fisica a
    /// cada conexao. Sem isto a role nova cairia no caminho generico, sem guarda alguma.
    /// A role ainda NAO existe no servidor; sua criacao depende de autorizacao da Hera.
    /// </summary>
    public const string SapInboundApplicationRole = "fugates_sap_inbound_app";

    /// <summary>
    /// Identidade GET-only exclusiva da API privada de leitura (18762). Existe porque
    /// fugates_acm_hml_app e compartilhada com o servico de comandos 18763, que precisa de
    /// escrita: separar a leitura em uma role sem nenhum INSERT/UPDATE/DELETE e a unica forma
    /// de dar isolamento a API sem quebrar os demais consumidores.
    /// A role ainda NAO existe no servidor; sua criacao depende de autorizacao da Hera.
    /// </summary>
    public const string DevReadOnlyApplicationRole = "fugates_dev_read_app";

    /// <summary>
    /// Identidades submetidas ao caminho reforcado de conexao, nos ambientes informados.
    ///
    /// Sao os MESMOS tres papeis de sempre - ACM, receptor SAP e leitura -, so que agora lidos do
    /// ambiente em vez de escritos aqui. Vazio significa DEV110, e ai a lista e identica a que
    /// existia antes de 06/11/2026.
    ///
    /// O papel do SD e do MM nao entram: eles nao passam por este caminho, e inclui-los daria a
    /// eles um reforco que o codigo deles nao espera.
    /// </summary>
    public static bool EhRoleGuardada(string? username, params AmbienteBanco[] ambientesAceitos)
    {
        var aceitos = ambientesAceitos is { Length: > 0 } ? ambientesAceitos : [AmbientesBanco.Dev110];
        return Array.Exists(aceitos, a =>
            username == a.PapelAcm || username == a.PapelSapInbound || username == a.PapelLeitura);
    }

    /// <summary>
    /// <paramref name="ambientesAceitos"/> diz QUAIS bancos esta conexao pode alcancar. Vazio
    /// significa so o DEV110, que e o comportamento de sempre - por isso nenhum chamador de hoje
    /// precisou mudar quando o QAS120 apareceu, em 06/11/2026.
    ///
    /// O par banco+papel e conferido JUNTO (ver <see cref="AmbientesBanco"/>): papel no PostgreSQL
    /// e do CLUSTER e nao do banco, entao conferir um sem o outro deixaria passar o banco do QAS
    /// com o papel do DEV.
    ///
    /// Ampliar um fluxo e acrescentar um ambiente NA CHAMADA, deliberadamente. Em 06/11/2026 so o
    /// receptor da devolutiva SD foi ampliado; ACM e MM seguem no DEV110.
    /// </summary>
    public static NpgsqlDataSource CriarAcm(string? connectionString, params AmbienteBanco[] ambientesAceitos)
    {
        var aceitos = ambientesAceitos is { Length: > 0 } ? ambientesAceitos : [AmbientesBanco.Dev110];
        NpgsqlConnectionStringBuilder metadata;
        try
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException();
            metadata = new NpgsqlConnectionStringBuilder(connectionString);
            if (metadata.Port != 5433 || !EhRoleGuardada(metadata.Username, aceitos) ||
                !AmbientesBanco.EhPar(metadata.Database, metadata.Username, aceitos) ||
                metadata.SearchPath != "fugates" ||
                (metadata.Host != "127.0.0.1" && metadata.Host != "localhost"))
                throw new InvalidOperationException();
        }
        catch
        {
            throw new InvalidOperationException("R165R_ACM_CONNECTION_METADATA_REQUIRED");
        }

        // Explicit Passfile overrides PGPASSFILE/default paths. PGPASSWORD must not override it.
        if (!HasExplicitAcmPassfile(metadata) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PGPASSWORD")))
            throw new InvalidOperationException("R165R_EXPLICIT_ACM_PASSFILE_REQUIRED");

        // A identidade esperada acompanha a role da propria connection string: cada conexao fisica
        // precisa confirmar que current_user e session_user sao exatamente essa role.
        var roleEsperada = metadata.Username!;
        // O banco vai junto: a cadeia DIZ um destino, e so a conferencia fisica prova onde a
        // conexao caiu. Ver AcmPostgreSqlIdentityGuard.
        var bancoEsperado = metadata.Database!;
        var builder = new NpgsqlDataSourceBuilder(metadata.ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            connection => AcmPostgreSqlIdentityGuard.VerifyAsync(connection, expectedRole: roleEsperada, expectedDatabase: bancoEsperado).GetAwaiter().GetResult(),
            connection => AcmPostgreSqlIdentityGuard.VerifyAsync(connection, expectedRole: roleEsperada, expectedDatabase: bancoEsperado));
        return builder.Build();
    }

    public static bool HasExplicitAcmPassfile(NpgsqlConnectionStringBuilder metadata) =>
        metadata.Password is null && !string.IsNullOrWhiteSpace(metadata.Passfile) &&
        metadata.Passfile.StartsWith("/", StringComparison.Ordinal) &&
        !metadata.Passfile.StartsWith("//", StringComparison.Ordinal) &&
        !metadata.Passfile.Contains('\\') && !metadata.Passfile.Contains(';') &&
        !metadata.Passfile.Any(char.IsControl) &&
        !metadata.Passfile.Contains(".fugates-r116-dev", StringComparison.OrdinalIgnoreCase);

    public static NpgsqlDataSource Criar(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{ConnectionStringKey} obrigatoria para PersistenceMode=PostgreSql.");
        }

        // Callers outside HostIntegracao (including callback TryAddSingleton registrations)
        // cannot bypass physical identity by using the ordinary factory com uma role guardada.
        //
        // AQUI A LISTA E A INTEIRA, E ISSO APERTA EM VEZ DE AFROUXAR.
        //
        // A pergunta desta linha nao e "este destino e permitido?" - e "este papel precisa ser
        // guardado?". Sao perguntas opostas: em ValidateDedicatedBinding, aceitar mais ambientes
        // ALARGA o que o processo alcanca; aqui, reconhecer mais papeis manda MAIS conexoes para
        // a guarda de identidade fisica.
        //
        // Com a lista restrita ao DEV110, um papel fugates_qas_* nao era reconhecido como
        // guardado e caia no NpgsqlDataSource.Create() cru - sem conferir que current_user e
        // session_user sao mesmo aquela role, e sem conferir em que banco a conexao caiu. Era um
        // buraco aberto pela conversao parcial de 06/11/2026: o QAS existia para o SD, e esta
        // funcao continuava enxergando so os papeis do DEV.
        //
        // O par (banco, papel) continua sendo conferido JUNTO dentro de CriarAcm, entao um papel
        // do QAS apontado para o banco do DEV continua sendo recusado.
        if (EhRoleGuardada(new NpgsqlConnectionStringBuilder(connectionString).Username,
                           AmbientesBanco.Aprovados))
            return CriarAcm(connectionString, AmbientesBanco.Aprovados);

        return NpgsqlDataSource.Create(connectionString);
    }
}
