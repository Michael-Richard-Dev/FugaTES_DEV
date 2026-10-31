# ACM — validação dinâmica de emissores existentes no Guardian
Requisito aprovado pelo operador: eliminar dependência da lista fixa para novos emissores.
Não cadastrar emissor automaticamente; validar cadastro remoto existente antes de envio.

## Escolha e limites
Manter whitelist manual foi rejeitado pelo operador. Usar consulta SOAP ConsultaPessoa foi
descartado: o projeto SD documenta falsos negativos. Escolha aprovada funcionalmente: GET
REST oficial /api/v0/Guardian/Emissor/{codigoEmissor}?idAssinatura=<escopo aprovado>, com
mesmo GuardianTokenClient/provider existente, HTTPS CloudPrix. O Swagger preservado em
evidence/ACM_DOCUMENT_ALIGNMENT_20261013_112813/guardian-openapi-v2.json documenta esse
GET e EEmissor: codigo string, sequencial int64, estado0desativado/1ativado. Não confundir
idAssinatura público da consulta com segredo CreateToken.assinatura. Escopo133289 já
referenciado no projeto; deverá ser binding explícito, não extraído do segredo.

## Mecanismo
Normalizar somente a representação numérica SAP (zeros à esquerda), como os pares já
homologados, e consultar o código exato; código real retornado deve corresponder
exatamente, com sequencial positivo, estado1 e resposta única. Não mapear nomes, CPF,
CNPJ ou sequencial em lugar de codigo. 404/ausência/inativo/401/403/400/timeout/resposta
ambígua/divergente bloqueiam, com motivo técnico sanitizado; nenhuma criação silenciosa,
nenhum fallback de consulta para escrita, nenhuma repetição empresarial automática.

Novo opt-in AcmR57.Normal.DynamicEmitterValidationEnabled (defaultfalse), SignatureId
positivo e DynamicEmitterValidationFromUtc obrigatório. Data é limite inferior único,
sem expiração/renovação. Version.RecebidoEmUtc vem da coluna H4 criado_em existente,
somente SELECT; não altera fingerprint/payload/schema. Versões antigas ao cutoff usam
somente o caminho legado aprovado, impedindo que o deploy libere1175 ou backlog antigo.
Versão2 continua bloqueada pelo escopo R170. Recuperação nominal1175 é etapa separada.

Um resolver scoped por execução guarda prova GET por IntegrationId/fingerprint/BP,
assinatura e código real por no máximo2min; não altera ApprovedPartnerMappings nem
inventa aprovações. Gate permanece fechado enquanto não há prova; pipeline executa a
consulta após identidade/journal e guardas estruturais válidos e revalida gate antes
de qualquer SOAP/REST. Preparação usa a MESMA prova e mantém material/callback/janela/
escopo/idempotência/fencing. Digest contratual e formato U de journal permanecem iguais.
Comando manual também valida pelo mesmo caminho, sem repetir SOAP. Sem cache negativo:
um cadastro novo pode ser reconhecido em tentativa futura autorizada. Sem log de
cadastro/JSON pessoal, token ou segredos. Apenas códigos/HTTP/estado são observáveis.

## Arquivos previstos
Novos: Aplicacao/Contratos/Guardian/GuardianEmitterRead.cs; Aplicacao/Servicos/Mvp/
AcmGuardianEmitterResolver.cs; Integracao.Guardian/GuardianEmitterReadAdapter.cs;
testes focais nos projetos existentes R158 e R165U.Automatic.
Alterações focais: AcmR57NormalPreparation.cs, AcmR57Pipeline.Normal.cs,
MvpOrchestrationComposition.cs, GuardianRuntimeComposition.cs,
GuardianProtocolCommandService.cs, IContratoSapToledoVersaoStore.cs e o SELECT por
IntegrationId de ContratoSapToledoVersaoStorePostgreSql.cs. Nenhum componente MM/SD/
Toledo/WinForms será alterado. Sem migration, GRANT ou edição de snapshots.

## Aceite
Emissor novo20000169 confirmado por GET deixa de depender da whitelist; ausência,
inativo, divergência, assinatura inválida, timeout e consulta indisponível impedem
SOAP/REST. Prova não reutilizada em outro contrato/fingerprint; material ainda requer
aprovação; execução desabilitada não consulta. Regressão de fluxos legados, retries e
resultado desconhecido permanece verde. Testes com transports simulados, build e
candidato versionado com hashes, configuração inicialmente inerte e rollback.
Publicação do motor e ativação exigem aprovação operacional com pacote pronto; não
reenviar1175, não enviar negócio real durante desenvolvimento. Consulta real somente
GET de emissor pode ser feita pelo provider existente para validar o contrato.
