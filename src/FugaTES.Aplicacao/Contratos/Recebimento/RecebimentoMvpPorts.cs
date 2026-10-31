using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.Contratos.Recebimento;

public interface IPreCadastroGuardianSet26Gateway { Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 preCadastro, CancellationToken cancellationToken); }
public interface IAutenticacaoGuardianSet26 { Task<string> ObterCredencialRuntimeAsync(CancellationToken cancellationToken); }
public sealed record CredenciaisGuardianSet26(string Usuario, string Senha, string? Assinatura = null);
public interface ICredenciaisGuardianSet26Provider { Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken cancellationToken); }
public interface IEmissorGuardianSet26Gateway { Task EnviarAsync(EmissorGuardianSet26 emissor, CancellationToken cancellationToken); }
public interface ITransportadoraGuardianSet26Gateway { Task EnviarAsync(TransportadoraGuardianSet26 transportadora, CancellationToken cancellationToken); }
public interface IItemGuardianSet26Gateway { Task EnviarAsync(ItemGuardianSet26 item, CancellationToken cancellationToken); }
public interface IRepositorioCorrelacaoRecebimento
{
    Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp entrada, CancellationToken cancellationToken);
    Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string documentoSap, CancellationToken cancellationToken);
    Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string documentoSap, string contratoAcmSap, CancellationToken cancellationToken);
    Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string documentoSap, string protocoloGuardian, CancellationToken cancellationToken, DateTimeOffset protocoloCriadoEm);
    Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocoloGuardian, CancellationToken cancellationToken);
    Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken cancellationToken);
}
public interface IPesagemSapMvpGateway { Task EnviarAsync(PesagemSapMvp pesagem, CancellationToken cancellationToken); }
public interface IRegistroCallbackPesagemSapStore
{
    Task<ResultadoRegistroCallbackMvp> RegistrarEEnfileirarAsync(CallbackGuardianMvp callback, CandidatoIntegracao candidato, CancellationToken cancellationToken);
}
