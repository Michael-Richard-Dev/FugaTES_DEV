using FugaTES.Aplicacao.Configuracao;
using System.Text.Json;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.Persistencia.Recebimento;
using Npgsql;
namespace FugaTES.Callback.Http.ManualRetry;
public static class ToledoSapCommandComposition
{
 public static bool Configure(IServiceCollection services,AcmManualRetryCommandProfile profile)
 {
  if(profile.ToledoSapRetryApprovalsFile is null&&profile.ToledoSapOperatorWindowFile is null)return false;
  if(profile.ToledoSapRetryApprovalsFile is not null&&profile.ToledoSapOperatorWindowFile is not null)throw new InvalidOperationException("TOLEDO_SAP_COMMAND_AUTHORIZATION_AMBIGUOUS");
  ToledoSapOperatorWindow? window=null;
  if(profile.ToledoSapOperatorWindowFile is not null)
  {
   window=JsonSerializer.Deserialize<ToledoSapOperatorWindow>(PrivateCommandFile.Read(profile.ToledoSapOperatorWindowFile,4096),AcmManualRetryCommandProfile.Json);
   if(window?.Valida!=true||window.OperadorSid!=profile.OperatorSid)throw new InvalidOperationException("TOLEDO_SAP_OPERATOR_WINDOW_INVALID");
  }
  var list=profile.ToledoSapRetryApprovalsFile is null?[]:JsonSerializer.Deserialize<ToledoSapRetryApproval[]>(PrivateCommandFile.Read(profile.ToledoSapRetryApprovalsFile,65536),AcmManualRetryCommandProfile.Json)
    ??throw new InvalidOperationException("TOLEDO_SAP_COMMAND_APPROVALS_REQUIRED");
  if(list.Length>100||list.Select(a=>a.ReciboId).Distinct().Count()!=list.Length||list.Any(a=>a.OperadorSid!=profile.OperatorSid||
    a.ReciboId==Guid.Empty||a.IntegracaoId==Guid.Empty||!AcmManualRetryPolicy.Hash(a.Revisao)||!AcmManualRetryPolicy.Evidence(a.ReferenciaAutorizacao)||
    a.ContinuoAutorizado||!ToledoSapActivationScope.Valid(a.InicioUtc,a.FimUtc,false)))throw new InvalidOperationException("TOLEDO_SAP_COMMAND_APPROVAL_SCOPE_INVALID");
  var ambiente=AcmManualRetryCommandHost.ReadEnvironment(profile);
  PrivateCommandFile.ValidateMetadata(profile.Passfile,65536);
  services.AddKeyedSingleton<NpgsqlDataSource>(ToledoSapReturnComposition.Key,(_,_)=>NpgsqlDataSourceFactory.CriarAcm(BuildConnection(ambiente,profile.Passfile).ConnectionString,ambiente));
  services.AddScoped<IToledoSapRetryStore>(sp=>new ReenvioToledoSapPostgreSql(sp.GetRequiredKeyedService<NpgsqlDataSource>(ToledoSapReturnComposition.Key)));
  if(window is null)services.AddSingleton<IToledoSapRetryApprovals>(new Approvals(list));
  else services.AddScoped<IToledoSapRetryApprovals>(sp=>new ToledoSapWindowRetryApprovals(sp.GetRequiredService<IToledoSapRetryStore>(),(IToledoSapAdmissionProof)sp.GetRequiredService<IToledoSapRetryStore>(),window,TimeProvider.System));
  services.AddScoped<ReenvioToledoSapService>();return true;
 }
 public static NpgsqlConnectionStringBuilder BuildConnection(AmbienteBanco ambiente,string passfile)
 {
  if(AmbientesBanco.Por(ambiente.Banco,ambiente.PapelAcm,AmbientesBanco.Dev110,AmbientesBanco.Qas120)!=ambiente)throw new InvalidOperationException("TOLEDO_SAP_COMMAND_DATABASE_REJECTED");
  return new(){Host="127.0.0.1",Port=5433,Database=ambiente.Banco,SearchPath="fugates",Username=ambiente.PapelAcm,Passfile=passfile,Enlist=false,IncludeErrorDetail=false,ApplicationName="FugaTES.ToledoSap.RetryCommand"};
 }
 sealed class Approvals(ToledoSapRetryApproval[] list):IToledoSapRetryApprovals
 {public Task<ToledoSapRetryApproval?> ObterAsync(Guid id,AcmRetryActor actor,CancellationToken ct)=>Task.FromResult(actor.Authenticated?list.SingleOrDefault(a=>a.ReciboId==id&&a.OperadorSid==actor.Sid):null);}
}
