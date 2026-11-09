# Entrega — SIMPALA Fale Conosco

## Situação da entrega

Sites confirmados:
- https://www.simpalafinanceira.com.br/
- https://www.simpalaconsorcios.com.br/

Ainda precisam ser definidos pela infraestrutura: origem HTTPS da API, pastas da
aplicação/dados, nome do pool e certificado. Não foi presumido um domínio da API.
O pacote não contém credenciais nem banco de testes. Acesso público vem bloqueado.

## Conteúdo

- API/: aplicação .NET publicada, com web.config e configuração base segura.
- Sites/Financeira e Sites/Consorcios: arquivos públicos completos dos sites atuais.
- Scripts/Configure-Server.ps1: configura a primeira instalação no Windows/IIS.
- Scripts/Configure-Websites.ps1: informa aos dois sites a origem HTTPS real da API.
- SHA256SUMS.txt: hashes dos arquivos antes das alterações locais de configuração.

Fluxo: navegador → API no IIS → caixa Fale Conosco da empresa → SimDesk.
Não é necessário instalar um serviço Windows para a API e não se usa Python no servidor.
O SimDesk e seu banco não são instalados/alterados por estes scripts.

## 1. Decisões da infraestrutura

Definir:
1. Origem HTTPS da API, sem caminho: https://DOMINIO-DEFINIDO-PELA-INFRA.
2. Pasta da aplicação: recebe os arquivos API/ e pode ser atualizada.
3. Pasta de dados separada: guarda SQLite, chaves e segredo protegido, deve persistir
   e ter backup. Nunca usar a pasta dos sites nem uma pasta compartilhada de rede.
4. Application Pool dedicado e identidade ApplicationPoolIdentity.
5. Certificado TLS válido e DNS; permitir saída HTTPS para login.microsoftonline.com
   e graph.microsoft.com, com resolução DNS, relógio correto e conectividade Microsoft.

## 2. Preparação do IIS

- Windows Server 2022, IIS e ASP.NET Core Hosting Bundle .NET 10 x64 atualizado.
- Application Pool: No Managed Code, Enable 32-Bit Applications=False, máximo de um worker.
- Site separado: pasta física aponta para a cópia de API/, binding HTTPS no domínio
  correto, SNI/certificado conforme o padrão da infra. Configurar reinício/aquecimento.
- Parar o pool desta API para a primeira configuração; não parar SimDesk ou outros sites.
- Verificar limite de request filtering de 16 KiB, identidade/permissões, IP real do
  cliente e Request.IsHttps. Proxies adicionais precisam de configuração explícita;
  não confiar em X-Forwarded-For arbitrário.

DNS, certificado, criação do site/pool e instalação do Hosting Bundle não são feitos
automaticamente pelos scripts. A configuração de primeira instalação exige administrador.

## 3. Configurar a API no próprio servidor

Copiar API/ para a pasta definitiva da aplicação. A pasta de dados deve ser nova,
exclusiva e vazia; o script se recusa a modificar um diretório já ocupado.

Exemplo (substituir os valores de exemplo pelos aprovados):

```powershell
.\Scripts\Configure-Server.ps1 `
  -ApplicationPath 'D:\Apps\SimpalaSiteApi' `
  -DataDirectory 'D:\Dados\SimpalaSiteApi' `
  -ApiOrigin 'https://DOMINIO-REAL-DA-API' `
  -AppPoolName 'SimpalaSiteApi'
```

O script solicita o valor do ClientSecret no terminal do servidor, sem eco. Ele não
deve ser informado por e-mail, colocado no pacote, Git, linha de comando ou screenshot.
TenantId e ClientId conhecidos estão no appsettings.json; são identificadores públicos.
Conferir que o aplicativo Entra é o aprovado e tem envio restrito às duas caixas.

O segredo é criptografado com Data Protection + DPAPI LocalMachine, com chaveiro em
Dados/secrets/mail-keys e arquivo Dados/secrets/mail.protected. ACLs permitem leitura
à identidade do pool e acesso administrativo a SYSTEM/Administradores; o restante
da pasta de dados permite escrita à API para SQLite e desafios. Não há segredo em
texto puro no appsettings.json ou no web.config. O processo usa o segredo em memória.

IMPORTANTE: DPAPI LocalMachine protege no disco neste Windows, mas qualquer conta
com acesso ao arquivo e chaveiro nesta máquina poderia decifrá-lo. A ACL é essencial.
Administradores do servidor continuam tendo acesso. Não é solução de cofre distribuído.
Em migração para outro servidor/reinstalação do Windows, provisionar novamente o segredo;
não assumir que copiar o arquivo permite decifrá-lo. Manter política de rotação/backup.
Não configurar simultaneamente SupportMail__ClientSecret no ambiente e ClientSecretFile.

O script não envia e-mails, não instala DNS/SSL nem inicia o pool. Mantém acesso
externo bloqueado para revisão inicial. Não deve ser usado como script de atualização.

## 4. Configurar e publicar os sites

No pacote extraído:

```powershell
.\Scripts\Configure-Websites.ps1 -ApiOrigin 'https://DOMINIO-REAL-DA-API'
```

Isso altera apenas contact-config.js nas duas pastas Sites/ do pacote. Depois publicar
o conteúdo correspondente em cada site existente. HTML já referencia scripts versionados
para reduzir cache de versões antigas. Não é necessário mudar layout nem instalar Node.
Os formulários são habilitados somente nos hosts oficiais www informados acima.
Se existir outro host efetivo, confirmar e acrescentar explicitamente tanto no site
quanto no CORS da API. Não usar wildcard ou origem null/file://.

## 5. Liberação e homologação

Antes de liberar: validar a estratégia antispam (soma/honeypot atuais são barreiras
básicas), escopo Microsoft, retenção aprovada e rotina de conferência de Unknown.
Com a configuração inicial pronta, definir Contact.EnablePublicAccess=true no
appsettings.json do servidor e iniciar/reciclar somente o pool desta API.
Alternativamente, na primeira instalação revisada, usar -EnablePublicAccess no script.

Validar no domínio definitivo:
1. GET /health retorna status ok e modo graph; isso não comprova autorização de envio.
2. O formulário carrega o desafio sem erro CORS, certificado ou mixed content.
3. Com autorização, enviar uma solicitação por empresa e conferir as caixas atuais.
4. Conferir assunto Entre em contato, campos, acentos e documento alfanumérico completo.
5. Conferir o chamado no SimDesk. Se TestMode estiver ligado, digitar manualmente
   [TESTE HML] no início da Mensagem; a API não adiciona nem remove a tag.
6. Duplo clique não pode produzir duas tentativas; perda de resposta consulta o estado.

O Nº Registro FORMS permanece no e-mail e no controle interno; não é o protocolo SD
do SimDesk e não aparece como protocolo de atendimento no site.

## Atualizações e operação

- Parar o pool da API antes de substituir binários; conservar sua configuração do
  servidor e o web.config configurado. Não substituir cegamente pelo appsettings base.
- Preservar a pasta de dados e seu ACL; não copiar banco/chaves locais para produção.
- Backup consistente do SQLite e plano de restauração/reconciliação de envios.
- Unknown não pode ser reenviado automaticamente. Conferir por registro no Exchange/SimDesk.
- Retenção padrão de 30 dias precisa de aprovação. Sending/Unknown não são apagados
  automaticamente; manter procedimento de resolução para evitar retenção indefinida.
- Para rotacionar o segredo: com pool parado, provisionar novo arquivo protegido em
  diretório privado, atualizar ClientSecretFile, validar e só então revogar o antigo.
  Se o segredo for compartilhado com SimDesk, coordenar a troca nas duas aplicações.
- Logs não devem incluir corpo, CPF, telefone, segredo ou tokens. Guardar somente
  diagnósticos seguros e restringir também os logs do IIS.
- Os scripts foram preparados e testados localmente onde possível; a instalação no
  IIS e o teste de ACL com a identidade real do pool devem ser validados pela infra.

Referências oficiais:
- https://learn.microsoft.com/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0
- https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-encryption-at-rest?view=aspnetcore-10.0
