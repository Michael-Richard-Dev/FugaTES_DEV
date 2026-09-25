using System.Security.Cryptography;
using System.Text;

namespace FugaTES.Aplicacao.CasosDeUso.Recebimento;

public static class GuardianPesagemSapMotorWork
{
    public const string Operacao = "SAP_RECEBIMENTO_GUARDIAN_PESAGEM";

    public static string Fingerprint(string protocolo, string callbackFingerprint) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Operacao}:v1:{protocolo}:{callbackFingerprint}")));
}
