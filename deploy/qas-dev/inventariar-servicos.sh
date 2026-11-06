#!/usr/bin/env bash
# Inventario dos servicos e da porta de entrada da VM, para planejar o QAS120.
#
# NAO ALTERA NADA: so le estado do systemd, do nginx e das portas em escuta.
#
# POR QUE VEM ANTES DO INSTALADOR, pelo mesmo motivo que o inventario do cluster veio antes do
# banco: o DEV110 nasceu ao longo de semanas e tem particularidades que nenhum script do
# repositorio descreve por inteiro - onze drop-ins num servico, um binding canonico que venceu a
# corrida de nomes, portas escolhidas caso a caso. Escrever o segundo conjunto de servicos a
# partir do que eu SUPONHO produziria algo que funciona pela metade num host que ja atende o DEV.
#
# O QUE PRECISO DESCOBRIR, e que este inventario responde:
#   - quais portas ja estao ocupadas, para o QAS nao disputar nenhuma;
#   - como cada unidade e montada: usuario, ExecStart efetivo, drop-ins e a ordem deles;
#   - onde cada uma le ambiente e credencial - os CAMINHOS, nunca o conteudo;
#   - como o nginx leva /recebimento e /venda ate a porta local, para os blocos -qas nascerem
#     iguais em tudo menos no caminho e no destino;
#   - se o firewall precisa de regra nova.
#
# SEGREDO NENHUM E LIDO. De EnvironmentFile e LoadCredential sai so o CAMINHO; do ExecStart,
# qualquer coisa que pareca senha, token ou cadeia de conexao e mascarada antes de imprimir.
#
# Uso:  sudo ./inventariar-servicos.sh
set -euo pipefail

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }

# Mascara o que nao deve sair daqui, mesmo que apareca por engano numa linha de comando.
mascarar() {
  sed -E -e 's/((assword|pwd|token|secret|apikey|api_key)[=:])[^ ;"'"'"']*/\1***/Ig' \
         -e 's/(Password=)[^;"]*/\1***/Ig' \
         -e 's/((User ID|Username|UID)=)[^;"]*/\1***/Ig'
}

UNIDADES="$(systemctl list-unit-files --no-legend --type=service 2>/dev/null \
            | awk '{print $1}' | grep -E '^fugates-' | sort || true)"

echo "=============================================================="
echo " UNIDADES fugates-*"
echo "=============================================================="
[ -n "$UNIDADES" ] || echo "  (nenhuma encontrada)"
for u in $UNIDADES; do
  estado="$(systemctl is-active "$u" 2>/dev/null || true)"
  hab="$(systemctl is-enabled "$u" 2>/dev/null || true)"
  printf '  %-34s %-10s %s\n' "$u" "$estado" "$hab"
done

echo
echo "=============================================================="
echo " COMO CADA UNIDADE ATIVA ESTA MONTADA"
echo "=============================================================="
for u in $UNIDADES; do
  [ "$(systemctl is-active "$u" 2>/dev/null || true)" = active ] || continue
  echo
  echo "---- $u"
  echo "  usuario ......... $(systemctl show "$u" -p User --value) / $(systemctl show "$u" -p Group --value)"
  echo "  diretorio ....... $(systemctl show "$u" -p WorkingDirectory --value)"
  echo "  ExecStart efetivo:"
  systemctl show "$u" -p ExecStart --value \
    | sed -n 's/.*argv\[\]=\(.*\) ; ignore_errors.*/\1/p' | head -1 | mascarar | fold -w 100 -s | sed 's/^/      /'
  echo "  EnvironmentFile (so o caminho):"
  systemctl show "$u" -p EnvironmentFiles --value | tr ' ' '\n' \
    | sed 's/ (ignore_errors=.*//' | grep -v '^$' | sed 's/^/      /' || echo "      (nenhum)"
  echo "  LoadCredential (nome:caminho, nunca valor):"
  systemctl show "$u" -p LoadCredential --value | tr ' ' '\n' | grep -v '^$' | sed 's/^/      /' || echo "      (nenhum)"
  echo "  drop-ins, NA ORDEM em que o systemd os aplica:"
  d="/etc/systemd/system/$u.d"
  if [ -d "$d" ]; then
    ls -1 "$d"/*.conf 2>/dev/null | sort | while read -r f; do
      # grep -c imprime "0" E sai com 1 quando nao acha: o "|| echo 0" de antes acrescentava um
      # segundo zero, e a coluna saia com "0\n0". Pequeno, mas e um relatorio dizendo duas coisas
      # onde ha uma.
      n="$(grep -c '^ExecStart' "$f" 2>/dev/null || true)"
      printf '      %-58s %s linha(s) ExecStart\n' "$(basename "$f")" "${n:-0}"
    done
  else
    echo "      (nenhum)"
  fi
done

echo
echo "=============================================================="
echo " PORTAS EM ESCUTA  (para o QAS nao disputar nenhuma)"
echo "=============================================================="
ss -lntp 2>/dev/null | awk 'NR==1 || /127.0.0.1|0.0.0.0|\[::\]/' | mascarar | sed 's/^/  /' \
  || echo "  (ss nao disponivel)"

echo
echo "=============================================================="
echo " NGINX — as rotas do Guardian"
echo "=============================================================="
echo "arquivos que o nginx REALMENTE le:"
nginx -T 2>/dev/null | grep -E '^# configuration file' | sed 's/^/  /' || echo "  (nginx -T falhou)"
echo
echo "blocos de location e para onde cada um aponta:"
nginx -T 2>/dev/null | awk '
  /location[ \t]*=?[ \t]*\/api\/guardian/ { dentro=1; chaves=0 }
  dentro {
    print "  " $0
    chaves += gsub(/{/,"{"); chaves -= gsub(/}/,"}")
    if (chaves<=0 && NR>1 && /}/) { dentro=0; print "" }
  }' || echo "  (nenhum bloco encontrado)"

echo
echo "server_name e listen:"
nginx -T 2>/dev/null | grep -E '^\s*(server_name|listen)' | sort -u | sed 's/^/  /'

echo
echo "=============================================================="
echo " FIREWALL"
echo "=============================================================="
if command -v nft >/dev/null 2>&1; then
  nft list ruleset 2>/dev/null | grep -iE 'fugates|dport (80|443|18[0-9]{3})' | sed 's/^/  /' || echo "  (nenhuma regra relacionada)"
else
  echo "  (nft nao disponivel)"
fi

echo
echo "=============================================================="
echo " RELEASES JA PUBLICADOS"
echo "=============================================================="
for r in /opt/fugates/releases/*/; do
  [ -d "$r" ] || continue
  echo "  $(basename "$r"): $(ls -1 "$r" 2>/dev/null | wc -l) release(s), ultimo $(ls -1t "$r" 2>/dev/null | head -1)"
done

echo
echo "=============================================================="
echo " FIM — nada foi alterado."
echo "=============================================================="
