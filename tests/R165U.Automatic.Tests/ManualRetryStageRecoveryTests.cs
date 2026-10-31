using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Servicos.Mvp;
using Xunit;

public sealed class ManualRetryStageRecoveryTests
{
    [Fact]
    public async Task Corrected_known_REST_rejection_resumes_existing_pipeline_without_second_SOAP_or_new_H4()
    {
        var f=new AutomaticFixture();var (v,claim)=f.Add("987659");
        f.Rest.Failure=new Known400();
        var failed=await f.Pipeline().ExecutarAsync(claim,default);
        Assert.Equal("R165Y_REST_REJECTED",failed.Etapa);Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);
        var link=f.H4.Items[v.IntegracaoId];var originalMetadata=failed.ResultadoFuncional!;
        var facts=new AcmRetrySnapshot{IntegrationId=v.IntegracaoId,Contract=v.NumeroContrato,Item=v.Snapshot.Item,Version=v.Versao,
            Fingerprint=v.FingerprintSha256,CanonicalFingerprint=v.FingerprintSha256,IdentityVerified=true,CorrelationId=claim.CorrelationId,
            State="FALHA_DEFINITIVA",Fence=1,AttemptId=claim.TentativaId,AttemptOutcome="FALHA",JournalStatus="FALHA_NAO_REPROCESSAVEL",
            Stage=failed.Etapa,Metadata=originalMetadata,ErrorCategory=failed.ErroCategoria,ErrorCode=failed.ErroCodigo,
            GuardianRequestedCode=link.CodigoContratoSolicitado,GuardianConfirmedCode=link.CodigoContratoConfirmado,Reference=link.NumeroReferenciaSolicitado};
        var now=DateTimeOffset.UtcNow;var actor=new AcmRetryActor(true,"S-1-5-21-1-2-3-1001");
        var approval=new AcmRetryApproval{Homologated=true,ExecutionAuthorized=true,ExclusiveWorker=true,IntegrationId=v.IntegracaoId,
            Contract=v.NumeroContrato,Item=v.Snapshot.Item,Fingerprint=v.FingerprintSha256,ContractMappingHash=originalMetadata.Split(':')[1],
            OperatorSid=actor.Sid,AuthorizationReference="OFFLINE_ONLY_APPROVAL",BusinessOriginEvidence="OFFLINE_ONLY_SOURCE_ATTESTATION",
            NotBeforeUtc=now.AddMinutes(-1),UntilUtc=now.AddMinutes(5),AllowedStage=AcmRetryStage.RestOnly,
            ObservedSnapshotDigest=facts.Digest(),CorrectedFailureDigest=facts.Digest(),ExternalCorrectionEvidence="OFFLINE_ONLY_EXTERNAL_CORRECTION"};
        var decision=AcmManualRetryPolicy.Evaluate(facts,approval,actor,now);Assert.True(decision.Eligible,decision.Code);
        // Simulated admission checkpoint; the production transaction is independently reviewed, never executed in this test.
        var preservedFailure=failed;
        f.Journal.Results[v.IntegracaoId]=failed with{Status=StatusFluxoMvp.EmProcessamento,Etapa="R165U_SOAP_CONFIRMED",ResultadoFuncional=decision.ResumeMetadata,
            Erro=null,ErroCodigo=null,ErroCategoria=null,ErroEtapa=null,ErroReprocessavel=null};
        var retryClaim=claim with{FencingToken=2,TentativaId=Guid.NewGuid()};f.Journal.Claims[v.IntegracaoId]=retryClaim;f.Rest.Failure=null;
        var result=await f.Pipeline().ExecutarAsync(retryClaim,default);
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,result.Status);Assert.Equal(1,f.Soap.Creates);Assert.Equal(2,f.Rest.Creates);
        Assert.Equal(originalMetadata,preservedFailure.ResultadoFuncional);Assert.Single(f.Versions.Items);
        Assert.Equal(link.NumeroReferenciaSolicitado,f.H4.Items[v.IntegracaoId].NumeroReferenciaSolicitado);
        await f.Pipeline().ExecutarAsync(retryClaim,default);Assert.Equal(2,f.Rest.Creates);Assert.Equal(1,f.Soap.Creates);
    }
    sealed class Known400:Exception,IAcmGuardianHttpFailure
    {public int HttpStatusCode=>400;public bool ResultadoIndeterminado=>false;public string SafeCode=>"OFFLINE_400";public string? SafeBusinessMessage=>"Offline validation fixture";}
}
