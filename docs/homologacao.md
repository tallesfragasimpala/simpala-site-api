# Homologação antes de habilitar o canal

1. Conferir as prévias sem transmitir e-mails, separadamente por empresa.
2. Confirmar parser e banco do SimDesk para CPF, CNPJ numérico e alfanumérico.
3. Aprovar assunto, prefixos e campos de preferência/política vazios. Não gerar `ON` sem aceite real.
4. Reutilizar o aplicativo existente do SimDesk; conferir autenticacao e proteger suas credenciais no servidor.
5. Configurar RBAC `Application Mail.Send` exclusivamente para as duas caixas.
   Conferir concessões amplas no Entra: permissões são cumulativas.
6. Testar autorização para as duas caixas e negação para uma caixa fora do escopo.
7. Implementar o transporte e os estados de envio antes de alterar a trava de inicialização.
   Persistir antes de despachar, usar alteração de estado atômica e não repetir envios incertos.
8. Definir reconciliação administrativa por protocolo e rastreamento do Exchange,
   sem acrescentar permissões de leitura por conveniência. `202` não confirma entrega.
9. Somente com autorização explícita, realizar teste controlado em HML com
   `[TESTE HML]` no campo Mensagem.
10. Conferir remetente, destinatário, cabeçalhos, autenticação, acentos, CRLF,
    corpo recebido/conversão para texto e chamado efetivamente criado pelo SimDesk.
11. Testar timeout, encerramento do processo durante envio, retentativas,
    concorrência e restauração após falhas.
12. Integrar os sites, mantendo uma chave por tentativa lógica e informando
    sucesso somente com a semântica aprovada. Aprovar antispam, retenção e operação.

Não publicar coleta pública nem habilitar produção antes dessas verificações.
