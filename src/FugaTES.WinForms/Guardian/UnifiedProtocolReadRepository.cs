using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.ProtocolosGuardian;

namespace FugaTES.WinForms.Guardian;

// Adapter over the existing authenticated GET-only reader. Never creates a datasource,
// reads another credential or falls back to a demo/runtime profile.
public sealed class UnifiedProtocolReadRepository(IMonitorReadRepository monitor,
    Func<Guid,CancellationToken,Task<MonitorAcmDetalhe>> readDetail):IProtocolosPreCadastroReadRepository
{
    public async Task<IReadOnlyList<ProtocoloPreCadastroLinha>> ConsultarAsync(CancellationToken ct)
    {
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var snapshot=await new MonitorOperacionalConsulta(monitor).ConsultarAsync(
                new(),bounded.Token);
            if(snapshot.Integracoes.Count>=500)throw new ReadLimitException();
            var rows=new List<ProtocoloPreCadastroLinha>();
            foreach(var integration in snapshot.Integracoes)
            {
                bounded.Token.ThrowIfCancellationRequested();
                if(integration.Operacao!=ProtocolosPreCadastroPostgreSqlReadRepository.Operacao)continue;
                if(integration.IntegracaoId==Guid.Empty)throw new InvalidOperationException();
                MonitorAcmDetalhe? detail=null;
                try { detail=await readDetail(integration.IntegracaoId,bounded.Token); }
                catch(OperationCanceledException) when(bounded.IsCancellationRequested){throw;}
                catch { /* Keep the physical integration; absence of detail is not absence of a protocol. */ }
                var row=ProtocolosPreCadastroPostgreSqlReadRepository.Montar(integration,detail);
                if(detail is null)row=row with { UltimoErro="PRECADASTRO_DETAIL_UNAVAILABLE — leitura parcial; erro persistido: "+(row.UltimoErro??"UNKNOWN") };
                rows.Add(row);
            }
            return rows;
        }
        catch(ReadLimitException){throw new InvalidOperationException("PRECADASTRO_DEV_READ_LIMIT_REACHED — limite de 500 integrações da API; listagem incompleta, não é prova de ausência de protocolos.");}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{throw new InvalidOperationException("PRECADASTRO_DEV_READ_UNAVAILABLE — consulta física indisponível; nenhum dado de demonstração foi usado.");}
    }
    private sealed class ReadLimitException:Exception {}
}
