# Modelo operacional autônomo

Este documento descreve o modelo conceitual futuro do FugaTES como middleware autônomo entre Toledo/Guardian e SAP. Não implementa máquina de estados, tempos, retries, polling ou integração funcional.

## Pipeline conceitual

1. DETECTAR: identificar uma operação elegível para integração.
2. REGISTRAR: persistir a intenção ou ocorrência de integração.
3. VALIDAR: confirmar pré-condições conceituais antes de tentar processar.
4. CLAIM: obter posse temporária de processamento para uma instância.
5. CRIAR TENTATIVA: registrar uma tentativa de integração associada ao item.
6. PROCESSAR: executar o fluxo futuro por meio das fronteiras de aplicação e adapters.
7. PERSISTIR RESULTADO: registrar sucesso, falha, necessidade de reprocessamento ou resultado indeterminado.
8. CONCLUIR / PROGRAMAR NOVA TENTATIVA: encerrar a integração ou deixá-la elegível para tratamento posterior.

## Papel do WinForms

WinForms não processa integração. A interface deve atuar como monitor, controle, consulta e solicitação operacional. O processamento autônomo pertence ao HostIntegracao.

## Fluxos de entrada

AUTOMATICO, INTEGRAR_AGORA e REPROCESSAR convergem para o mesmo pipeline conceitual. A diferença está na origem da elegibilidade, não no motor de processamento.

INTEGRAR_AGORA=antecipar/criar elegibilidade de operação ainda não executada pelo fluxo normal.

REPROCESSAR=nova tentativa de integração existente que já possui histórico de execução.

Integrar Agora não é substituto genérico de Reprocessar.

## Resultado indeterminado

RESULTADO_INDETERMINADO é conceito obrigatório. Após o marco de possível envio externo, uma falha local, queda de processo ou perda de resposta não permite concluir com segurança se o efeito externo ocorreu. Nessa condição, o FugaTES deve assumir postura conservadora e exigir tratamento apropriado antes de repetir efeitos externos.

## Marco de possível envio externo

MARCO_DE_POSSIVEL_ENVIO_EXTERNO é o ponto conceitual após o qual o FugaTES deverá assumir conservadoramente que o efeito externo pode ter ocorrido.

Nome físico futuro recomendado: marco_envio_externo_em.

Esse marco não comprova transmissão efetiva e não deve ser documentado como prova de chamada iniciada.

## Estados conceituais

- PENDENTE: integração registrada e ainda não processada.
- EM_PROCESSAMENTO: integração sob posse de uma instância.
- AGUARDANDO_REPROCESSAMENTO: integração com histórico e elegível para nova tentativa futura.
- AGUARDANDO_ACAO_MANUAL: integração bloqueada para decisão ou intervenção humana.
- RESULTADO_INDETERMINADO: não há certeza se o efeito externo ocorreu após o marco de possível envio externo.
- CONCLUIDA: integração finalizada com sucesso conceitual.
- FALHA_DEFINITIVA: integração encerrada por falha não recuperável.
- CANCELADA: integração cancelada por decisão operacional autorizada.

AGUARDANDO_REPROCESSAMENTO indica nova tentativa futura de item já tentado. AGUARDANDO_ACAO_MANUAL indica necessidade de decisão humana antes de prosseguir. RESULTADO_INDETERMINADO indica incerteza conservadora sobre possível efeito externo.

## Pausa

Pausa bloqueia novos claims. Pausa não aborta automaticamente chamada externa em andamento. A pausa pode existir conceitualmente para SAP, TOLEDO ou OPERACAO.

A persistência física da pausa ainda não está implementada.
