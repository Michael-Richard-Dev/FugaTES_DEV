using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Contratos.Mm;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Aplicacao.Servicos.Mm;
public sealed record MmCicloEnvio(int Criados,int Bloqueados,string Estado);
/// <summary>Reserva antes do POST; nenhuma resposta ambígua libera a chave. Sem UI ou retry.</summary>
public sealed class EnvioAutomaticoMm(IRegistroProtocolosMm registro,IEnviarProtocoloMmGateway guardian,string callback,Func<CancellationToken,Task<bool>>? ownershipProof=null)
{
    private int ocupado;
    public async Task<MmCicloEnvio> ProcessarAsync(IReadOnlyList<PedidoCompraMmLeitura> linhas,CancellationToken ct,Func<bool>? admissaoAtual=null)
    {
        if(Interlocked.CompareExchange(ref ocupado,1,0)!=0)return new(0,0,"MM_GUARDIAN_OCUPADO");
        try
        {
            var anteriores=(await registro.LerAsync(ct)).ToDictionary(x=>x.Chave,StringComparer.Ordinal);
            int criados=0,bloqueados=0,admitidos=0;
            foreach(var linha in linhas)
            {
                ct.ThrowIfCancellationRequested();
                if(admitidos>=10||admissaoAtual?.Invoke()==false)break;
                string chave,json;
                try{chave=MapeadorProtocoloMm.ChaveNatural(linha);json=MapeadorProtocoloMm.Serializar(MapeadorProtocoloMm.Montar(linha,callback));}
                catch(InvalidOperationException){bloqueados++;continue;}
                if(anteriores.ContainsKey(chave))continue;
                var bytes=Encoding.UTF8.GetBytes(json);var hash=Convert.ToHexString(SHA256.HashData(bytes));
                var reserva=new RegistroProtocoloMm(Guid.NewGuid(),chave,MapeadorProtocoloMm.Codigo(linha.Pedido),MapeadorProtocoloMm.Codigo(linha.Item),linha.Centro,
                    json,hash,EstadoProtocoloMm.Indeterminado,null,null,"Reservado antes do POST; aguarda resultado comprovado.",DateTimeOffset.UtcNow,MapeadorProtocoloMm.SourceHash(linha));
                if(admissaoAtual?.Invoke()==false||(ownershipProof is not null&&!await ownershipProof(ct)))break;
                if(!await registro.TentarReservarAsync(reserva,ct))continue;
                anteriores.Add(chave,reserva);admitidos++;
                // Recheck after reservation: a failed SAP poll may have revoked admission while DB was busy.
                if(admissaoAtual?.Invoke()==false||(ownershipProof is not null&&!await ownershipProof(ct))){bloqueados++;continue;}
                ResultadoProtocoloMm resultado;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using var delivery=new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    resultado=guardian is IMmAdmittedGuardianGateway validated
                        ?await validated.EnviarAdmitidoAsync(bytes,async token=>admissaoAtual?.Invoke()!=false&&(ownershipProof is null||await ownershipProof(token)),delivery.Token)
                        :await guardian.EnviarAsync(bytes,delivery.Token);
                }
                catch(Exception){resultado=new(EstadoProtocoloMm.Indeterminado,null,null,"Resultado externo não comprovado; reconciliação necessária.");}
                if(resultado.Estado==EstadoProtocoloMm.Criado&&(resultado.ProtocoloId is not >0||resultado.Http is not(>=200 and <300)))
                    resultado=new(EstadoProtocoloMm.Indeterminado,resultado.Http,null,"Resposta sem confirmação suficiente do protocolo.");
                // Independent bounded close: cancellation of the consumer must not erase external evidence.
                using var closing=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await registro.FecharAsync(reserva.ReservaId,chave,resultado,closing.Token);
                if(resultado.Estado==EstadoProtocoloMm.Criado)criados++;else bloqueados++;
            }
            return new(criados,bloqueados,"MM_GUARDIAN_VERIFICADO");
        }
        finally{Volatile.Write(ref ocupado,0);}
    }
}
