# Publicação do SD — 06/11/2026, segunda do dia

Substitui a `publicacao-20261106`, que já está no ar (`SD_LACRES_20261106_114921Z` no receptor e
`MOTOR_SD_20261106_114548Z` no motor). Commit `14683e9`.

**São os mesmos dois serviços**, e os dois precisam ir:

- `fugates-toledo-callback` — recebe a devolutiva;
- `fugates-sd-protocolo` (motor SD) — compõe o texto da NF-e.

A correção está no `FugaTES.Aplicacao.dll`, que os dois carregam.

## O que esta publicação corrige

**Primeiro "é nossa?", depois "está completa?".**

O `Centro` sai do campo adicional **1001**, e só o pré-cadastro do SD grava o 1001 — o do ACM grava
**1000, 1002 e 1003**. Numa pesagem do ACM o `Centro` chega vazio, e o leitor exigia o centro
**antes** de olhar o tipo: a pesagem morria em `SD_DEVOLUTIVA_CAMPO_AUSENTE`, 400, e o Guardian
reenviava para sempre um documento que está certo e só não é nosso. A verificação de tipo e a de
contrato nem chegavam a rodar.

Agora o tipo é lido logo depois do `IdPesagem`, antes de qualquer exigência de completude:

| situação | antes | agora |
|---|---|---|
| pesagem do ACM (tipo `AC3S`, sem centro) | 400 `CAMPO_AUSENTE` | 200 `NAO_E_DO_SD` |
| pesagem de entrada (tipo `E`) | 200 `NAO_E_DO_SD` | igual |
| pesagem avulsa (tipo vazio, sem pré-cadastro) | 400 `CAMPO_AUSENTE` | 200 `NAO_E_DO_SD` |
| **venda nossa, incompleta** | 400 com a causa | **igual — continua 400** |

Nós sempre gravamos `"S"` no campo 1000, então tipo vazio quer dizer "não nasceu de pré-cadastro do
SD": é pesagem avulsa da balança, não documento errado. Responde 200, não grava nada, e deixa linha
no journal — decidido pelo operador em 06/11/2026.

Exigir campo de um documento que não vamos processar é cobrar completude de quem não nos deve nada.
Exigir de uma venda nossa continua sendo obrigação, e isso está travado em teste.

## Como publicar

Receptor primeiro, motor em seguida. Entre um e outro, não crie remessa.

### Receptor

```powershell
cd "C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\deploy\sd-dev\publicacao-20261106-2"; .\Enviar-CallbackSd.ps1
```

Tem que imprimir `sha256 ... 14325de5...` nas três linhas (pacote, enviador e publicador).

```bash
sudo ~/publicar-callback-sd.sh conferir
```

```bash
sudo ~/publicar-callback-sd.sh instalar
```

O release tem que sair como **`SD_DONO_...`**. Se sair com outro prefixo, o publicador que rodou não
é o desta pasta.

### Motor SD

```powershell
.\Enviar-MotorSd.ps1
```

```bash
sudo ~/instalar-motor-sd.sh conferir
```

```bash
sudo ~/instalar-motor-sd.sh instalar
```

## Conferir o efeito, e não o relatório

As três situações que mudaram, nenhuma delas grava nada:

```bash
curl -s -w ' <- %{http_code}\n' -X POST http://127.0.0.1:18086/api/guardian/set26/callback/venda -H 'Content-Type: application/json' -d '{"IdPesagem":"T1","Centro":"3011","TipoPesagem":"E"}'
```

```bash
curl -s -w ' <- %{http_code}\n' -X POST http://127.0.0.1:18086/api/guardian/set26/callback/venda -H 'Content-Type: application/json' -d '{"IdPesagem":"T2","Centro":"","TipoPesagem":"AC3S"}'
```

```bash
curl -s -w ' <- %{http_code}\n' -X POST http://127.0.0.1:18086/api/guardian/set26/callback/venda -H 'Content-Type: application/json' -d '{"IdPesagem":"T3","Centro":"","TipoPesagem":""}'
```

As três têm de responder **200** com `"status":"NAO_E_DO_SD"`. A segunda é a que não funcionava: é a
pesagem do ACM como ela realmente chega, sem centro.

E o limite, que tem de continuar **400**:

```bash
curl -s -w ' <- %{http_code}\n' -X POST http://127.0.0.1:18086/api/guardian/set26/callback/venda -H 'Content-Type: application/json' -d '{"IdPesagem":"T4","TipoPesagem":"S"}'
```

No journal, cada causa tem a sua marca:

```bash
sudo journalctl -u fugates-toledo-callback --since '-10m' --no-pager | grep -E 'SD_DEVOLUTIVA'
```

- `SD_DEVOLUTIVA_E_DE_ENTRADA` — recebimento ou pesagem do ACM;
- `SD_DEVOLUTIVA_SEM_TIPO` — pesagem sem pré-cadastro. Se isto virar rotina, alguém está pesando
  fora do fluxo — e só o journal mostra, porque a resposta é 200 e nada é gravado;
- `SD_DEVOLUTIVA_RECUSADA` — documento nosso e errado. Este merece olhada.

## O campo Observação no Guardian

Não mudou nesta publicação. Duas linhas: a primeira os lacres da carga, a segunda os das
testemunhas.

```
134955,134956,134957,134958
65593,65592,65591
```
