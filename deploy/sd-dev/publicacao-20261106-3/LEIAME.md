# Publicação do motor SD — 06/11/2026, terceira do dia

**Só o motor desta vez.** Commit `c499569`.

As duas publicações anteriores mexiam no `FugaTES.Aplicacao.dll`, que os dois serviços carregam.
Esta mexe no `FugaTES.Integracao.SAP.dll`, e quem usa o que mudou são três clientes SD — criar
remessa, ajustar e gravar o texto —, **todos no motor**. O receptor (`fugates-toledo-callback`) só
usa o executor SAP na checagem administrativa, que é leitura: nada muda para ele.

Ele continua em `SD_DONO_20261106_135922Z`, e está correto assim.

## O que esta publicação corrige

A remessa **80003934**, hoje, ficou com o item 000010 zerado e o 000020 não:

```
item 000010: HTTP 204 ok
item 000020: HTTP 500 — The delivery is currently being processed by user FUGATES
```

São duas causas, e as duas foram corrigidas.

**1. A trava não estava sendo reconhecida.** Já existia retentativa (3 tentativas, 2s de espera),
mas ela só conhecia `MC/601` e o texto `currently locked`. O serviço da remessa diz
`currently being processed`, com outro código. A recusa passou como definitiva e **não houve uma
tentativa sequer**. Sozinha, esta correção já teria salvado a remessa.

Erro de negócio continua não sendo trava — insistir ali só repetiria a recusa e esconderia a causa.

**2. Cada item ia num PATCH próprio.** Isso cria uma LUW por item sobre o mesmo documento, em
sequência rápida: o update task do primeiro ainda roda quando o segundo chega, e o SAP recusa
dizendo que a remessa está sendo processada — por nós mesmos, na requisição anterior.

Agora os itens vão num `$batch` com um changeset: um enqueue, um commit. Não existe segundo PATCH
para colidir com o primeiro, e o ETag não envelhece no meio porque nada é aceito até o fim (a
releitura entre itens, que era o contorno do 412 de 05/11, saiu junto).

**O ganho que mais importa: ou todos os itens são zerados, ou nenhum.** O estado "metade zerada"
deixa de existir.

O **cabeçalho continua em PATCH próprio**, depois do lote — de propósito. O SAP recalcula o peso do
cabeçalho a partir dos itens, e em 02/11 isso já custou um cabeçalho 1199/699 quando a ordem das
gravações mudou. Juntar no mesmo commit mudaria quando o recálculo acontece em relação à nossa
escrita, e isso não se confere sem SAP na frente.

## Como publicar

```powershell
cd "C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\deploy\sd-dev\publicacao-20261106-3"; .\Enviar-MotorSd.ps1
```

Tem que imprimir `sha256 ... 5b67baf0...`.

```bash
sudo ~/instalar-motor-sd.sh conferir
```

```bash
sudo ~/instalar-motor-sd.sh instalar
```

## Conferir o efeito

Este é o caso em que a prova só vem **da próxima remessa de biodiesel com duas linhas** — não há
chamada local que exercite o `$batch` sem falar com o SAP de verdade.

O que olhar:

```bash
sudo journalctl -u fugates-sd-protocolo -f
```

**Como era** (remessa 80003934):

```
SD_AJUSTE_NAO_CONCLUIDO: ... item 000010: HTTP 204 ok | item 000020: HTTP 500 — ...
```

**Como tem de ficar:** as duas etapas de item com o mesmo desfecho — as duas `ok`, porque o
changeset é tudo-ou-nada. Se a trava aparecer, ela agora é reconhecida e o lote é reenviado inteiro
antes de desistir; só depois de três tentativas é que a etapa é dada como recusada.

Se vier `SD_AJUSTE_NAO_CONCLUIDO` com **um item ok e outro não**, o que subiu ainda é o código
antigo — no novo isso não é um desfecho possível.

## Pendente, e que esta publicação não resolve

A remessa **80003934** continua com o item 000020 por ajustar, e a pesagem 0030392 saiu da fila
automática. A correção evita novas; essa precisa de mão no SAP.
