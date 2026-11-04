# Publicação do receptor compartilhado — 03/11/2026

Serviço `fugates-toledo-callback` (127.0.0.1:18086), que atende as duas rotas do Guardian:

- `/api/guardian/set26/callback/recebimento` — ACM
- `/api/guardian/set26/callback/venda` — devolutiva SD

## O que esta publicação corrige

**1. A data do ACM recusava dia de um dígito.** A lista de formatos exigia `MM/dd`; o Guardian manda
`"11/3/2026 9:23:54 AM"` no caminho automático. A pesagem 0020345 foi recusada por isso — e a
0020344 já tinha passado porque veio no formato manual, `dd/MM` com relógio de 24h. Agora o ACM usa
os mesmos quatro formatos do SD: `M/d` com AM/PM, `d/M` com 24 horas, um ou dois dígitos.

**2. Zero virava ordem de venda.** O Guardian preenche os campos 1003 e 1009 com zeros quando a
pesagem não pertence a uma ordem — toda pesagem do ACM chega assim. A normalização removia os zeros
à esquerda e devolvia `"0"`, então a pesagem entrava como CORRELACIONADA com a chave `0/0`. Foi o
que gravou as linhas das pesagens 0020344 e 0020345. Agora zero não correlaciona, e o par continua
valendo tudo-ou-nada: ordem zerada ou linha zerada invalida as duas.

**3. Pesagem do ACM virava linha em branco no SD.** O Guardian manda a mesma finalização para as
duas rotas. Quando `ContratoACM` vem preenchido, a pesagem é do ACM: o receptor do SD responde 200
com `mensagem: OK` e **não grava**. Vazio, ausente ou só zeros continua sendo do SD.

## Como publicar

No Windows, nesta pasta:

```powershell
.\Enviar-CallbackSd.ps1
```

Na VM, nesta ordem. Os dois primeiros **não alteram nada**:

```bash
chmod +x ~/publicar-callback-sd.sh && sudo ~/publicar-callback-sd.sh conferir
```

```bash
sudo ~/publicar-callback-sd.sh ensaio
```

```bash
sudo ~/publicar-callback-sd.sh instalar
```

Para voltar ao release anterior:

```bash
sudo ~/publicar-callback-sd.sh reverter
```

## O que o publicador faz, e o que ele se recusa a fazer

Ele troca **apenas o caminho do release** dentro do binding canônico
`/etc/systemd/system/fugates-toledo-callback.service.d/90-fugates-runtime.conf`, preservando o resto
da linha — `--urls`, `--SdInbound:DevolutivaEnabled=true` e demais chaves. Não cria drop-in novo:
disputar precedência com mais "z" no nome é corrida de nomes, não solução.

Antes de reiniciar, confere que o `ExecStart` **efetivo** aponta para o release novo; se outro
drop-in vencer a precedência, desfaz e para. Depois de reiniciar, exige GET 200 nas **duas** rotas;
se qualquer uma falhar, volta sozinho ao release anterior e imprime o journal.

Recusa publicar se o SHA do pacote não conferir, ou se o conjunto de arquivos do pacote for
diferente do release que está rodando — são 17 arquivos, sem `appsettings`, sem apphosts, sem
staticwebassets.

Backup do binding em `/var/backups/fugates/toledo/`, release anterior anotado em
`/etc/fugates/callback-sd-release-anterior`.

## Pendente depois da publicação

As duas linhas com chave `0/0` continuam na tabela — as pesagens 0020344 e 0020345, que são do ACM e
nunca deveriam ter entrado no SD:

```bash
sudo -u postgres psql -p 5433 -d fugates_dev -c "select id_pesagem, chave_natural, estado, recebido_em from fugates.devolutiva_sd_pesagem where chave_natural = '0/0'"
```

A remoção é decisão do operador e não faz parte desta publicação.

## Registro para o Codex

O deploy deste serviço está documentado em `deploy/callback/DEPLOYMENT_OWNER.md` como
responsabilidade dele, e esta publicação **não** passou pelo publicador versionado dele — foi feita
por decisão do operador em 03/11/2026, com o Codex parado. O binding canônico foi respeitado e
nenhum drop-in novo foi criado. O arquivo `src/FugaTES.Callback.Http/ToledoPesagemCallbackDto.cs`,
que é do caminho do ACM, foi alterado nesta publicação (formatos de data).
