# Publicação no IIS — preparação

Esta entrega é de simulação. Publicar os binários não habilita envio nem coleta pública.

- Windows Server 2022 com IIS e Hosting Bundle do ASP.NET Core 10, atualizado.
- Application Pool dedicado, `No Managed Code`, identidade com permissões mínimas.
- Ativar `Load User Profile` para a identidade que usa as chaves DPAPI.
- Site/aplicação separado do SimDesk, com TLS válido e ambiente explicitamente definido.
- Pasta de dados fora da pasta publicada: acesso de escrita apenas para a identidade da API.
- Pasta de binários: leitura/execução; não conceder escrita além do necessário.
- Não armazenar certificados privados, banco, backups e chaves em pastas servidas por HTTP.
- Configurar `AllowedHosts` com os nomes reais e CORS com origens exatas, se necessário.
- Definir limite de corpo no Request Filtering do IIS, coerente com os 16 KiB da API.
- Manter logs de aplicação sem dados pessoais e proteger logs do IIS.
- Revisar reciclagem, aquecimento e monitoramento antes de adicionar um despachante.

```powershell
dotnet publish src/Simpala.Site.Api -c Release -o artifacts/publish
```

O SDK Web gera `web.config` no publish. Não publicar fontes, testes ou `App_Data`.
Configure ambiente e caminho de dados no servidor, não em credenciais fixas no código.
Em HML o endpoint de simulação continua limitado a loopback e HTTPS; em Production
o contato fica indisponível. O transporte Graph e sua homologação são uma etapa posterior.
