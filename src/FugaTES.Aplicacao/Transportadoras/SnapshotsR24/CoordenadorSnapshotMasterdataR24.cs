namespace FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
// Explicitly invoked facade only: owns no scheduler, WinForms lifetime or shared UI lock.
public sealed class CoordenadorSnapshotMasterdataR24(ISnapshotMasterdataStoreR24 store)
{
 public bool RuntimeActivation => false;
 public Task<ResultadoSnapshotR24<RecuperacaoSnapshotR24>> RecuperarAposReinicioAsync(RecuperarSnapshotAposReinicioR24 input,CancellationToken ct=default)=>store.RecuperarAposReinicioAsync(input,ct);
 public Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> PromoverSnapshotAsync(PromoverSnapshotR24 input,CancellationToken ct=default)=>store.PromoverSnapshotAsync(input,ct);
}
