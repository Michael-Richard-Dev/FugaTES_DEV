using System.Text.Json;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.WinForms.Monitor;

namespace FugaTES.Testes;

public sealed class MonitorReadbackTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task Missing_unprivileged_updated_timestamp_remains_unavailable_without_vault_or_fake_date()
    {
        var directory=Path.Combine(Path.GetTempPath(),"R165Y-MONITOR-UNKNOWN-DATE-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"ACM_MONITOR_READBACK.json");
        try
        {
            var now=DateTimeOffset.UtcNow;
            File.WriteAllText(path,JsonSerializer.Serialize(new {
                schema="R165Y_ACM_MONITOR_READBACK_V1",status="READ_ONLY_DURABLE_STATE_VERIFIED",captured_utc=now,integration_id=Id,
                identity=new {port=5433,cluster="18/fugatesacmdev",database="fugates_dev",role="fugates_acm_hml_app",read_only="on"},
                integration=new {id=Id,chave_negocio="0000001140",operacao="SAP_CONTRATO_TOLEDO",estado="FALHA_DEFINITIVA",correlation_id="SYNTHETIC_ONLY",criado_em=now},
                h4=new {integracao_id=Id,numero_contrato="0000001140",versao=1,centro="3011",guardian_codigo_confirmado="0000001140",guardian_numero_referencia="OC1140"},
                projection=new[]{new {fluxo="ACM_IN",status_funcional="FALHA_NAO_REPROCESSAVEL",etapa_funcional="R165Y_REST_REJECTED",erro_codigo="GUARDIAN_R57_HTTP_400"}}
            }));
            using var session=AcmMonitorSession.Criar(new AcmMonitorProfile {
                Enabled=false,ReadbackEnabled=true,ReadbackFile=path,ReadbackIntegrationId=Id,
                ReadbackContract="0000001140",ReadbackMaxAgeSeconds=60
            },(_,_)=>throw new Exception("VAULT_MUST_NOT_BE_CALLED"));
            Assert.True(session.Configurado);
            var row=Assert.Single((await session.Consulta.ConsultarAsync(new(),default)).Integracoes);
            Assert.Equal(default,row.AtualizadoEm);
            Assert.Equal("GUARDIAN_R57_HTTP_400",row.UltimoErro);
            Assert.Contains("ultima alteracao indisponivel",session.Estado,StringComparison.OrdinalIgnoreCase);
        }
        finally {File.Delete(path);Directory.Delete(directory);}
    }

    [Theory]
    [InlineData(5433, "18/fugatesacmdev", "fugates_acm_hml_app", 0, true)]
    [InlineData(5432, "18/main", "fugates_acm_hml_app", 0, false)]
    [InlineData(5433, "18/fugatesacmdev", "fugates_dev_app", 0, false)]
    [InlineData(5433, "18/fugatesacmdev", "fugates_acm_hml_app", -600, false)]
    public async Task Exported_real_cluster_contract_is_enforced_without_Windows_password(int port, string cluster, string role, int age, bool accepted)
    {
        var directory=Path.Combine(Path.GetTempPath(), "R165Y-MONITOR-SYNTHETIC-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"ACM_MONITOR_READBACK.json");
        try
        {
            var now=DateTimeOffset.UtcNow;
            File.WriteAllText(path,JsonSerializer.Serialize(new {
                schema="R165Y_ACM_MONITOR_READBACK_V1", status="READ_ONLY_DURABLE_STATE_VERIFIED",captured_utc=now.AddSeconds(age), integration_id=Id,
                identity=new { port,cluster,role,database="fugates_dev",read_only="on" },
                integration=new {id=Id,chave_negocio="0000001144",operacao="SAP_CONTRATO_TOLEDO",estado="RESULTADO_INDETERMINADO",correlation_id="SYNTHETIC-ONLY",criado_em=now,atualizado_em=now},
                h4=new {integracao_id=Id,numero_contrato="0000001144",versao=1,centro="3011",guardian_codigo_confirmado="001144",guardian_numero_referencia="SYNTHETIC-REF"},
                projection=new[]{new {fluxo="ACM_IN",status_funcional="RESULTADO_INDETERMINADO",etapa_funcional="R165U_REST_ARMED",resultado_funcional=$"U:{new string('A',64)}:{new string('B',64)}:582",erro_codigo="SYNTHETIC_REST_OUTCOME_UNKNOWN"}}
            }));
            var profile=JsonSerializer.Deserialize<AcmMonitorProfile>(JsonSerializer.Serialize(new {
                Enabled=false,ReadbackEnabled=true,ReadbackFile=path,ReadbackIntegrationId=Id,ReadbackContract="0000001144",ReadbackMaxAgeSeconds=60
            }))!;
            var vaultCalls=0;
            using var session=AcmMonitorSession.Criar(profile,(_,_)=>{vaultCalls++;throw new Exception("VAULT_MUST_NOT_BE_CALLED");});
            Assert.Equal(0,vaultCalls);
            if(!accepted)
            {
                if(session.Configurado) await Assert.ThrowsAsync<InvalidOperationException>(()=>session.Consulta.ConsultarAsync(new(),default));
                return;
            }
            Assert.True(session.Configurado);
            var snapshot=await session.Consulta.ConsultarAsync(new(),default);
            var row=Assert.Single(snapshot.Integracoes);
            Assert.Equal(Id,row.IntegracaoId);Assert.Equal("0000001144",row.Documento);Assert.Equal("RESULTADO_INDETERMINADO",row.EstadoMotor);
            var detail=await session.DetalharAcmAsync(Id,default);
            Assert.Equal("582",detail.Acm.ComMetadataU(row).SequencialContrato);
            Assert.False(snapshot.EventosDisponiveis);
            Assert.Empty((await session.Consulta.ConsultarAsync(new(Documento:"not-present"),default)).Integracoes);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>session.DetalharAcmAsync(Guid.NewGuid(),default));
            Assert.Contains("5433",session.Estado);Assert.Contains("exportad",session.Estado,StringComparison.OrdinalIgnoreCase);
        }
        finally{File.Delete(path);Directory.Delete(directory);}
    }
}
