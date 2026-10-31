namespace FugaTES.WinForms.Transportadoras.MasterdataR3;
public enum SituacaoConsultaProtegidaMasterdataR3 { Executada, Ocupada, Cancelada, Encerrada }
public sealed record ResultadoConsultaProtegidaMasterdataR3<T>(SituacaoConsultaProtegidaMasterdataR3 Situacao, T? Resultado);
