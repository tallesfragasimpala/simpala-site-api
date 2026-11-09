param([Parameter(Mandatory=$true)][string]$ApiOrigin)
$ErrorActionPreference = 'Stop'
$origin = [uri]$ApiOrigin
if (-not $origin.IsAbsoluteUri -or $origin.Scheme -ne 'https' -or $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.Fragment -or $origin.UserInfo -or $origin.IsLoopback) { throw 'Informe a origem HTTPS real da API, sem caminho.' }
$url = $origin.GetLeftPart([UriPartial]::Authority) | ConvertTo-Json -Compress
$package = Split-Path $PSScriptRoot -Parent
foreach ($item in @(@{Folder='Financeira';Company='financeira';Host='www.simpalafinanceira.com.br'},@{Folder='Consorcios';Company='consorcio';Host='www.simpalaconsorcios.com.br'})) {
    $path = Join-Path $package ('Sites\'+$item.Folder+'\contact-config.js')
    if (-not (Test-Path -LiteralPath $path)) { throw 'Execute o script dentro do pacote de entrega, mantendo as pastas Sites e Scripts.' }
    $content = "window.SIMPALA_CONTACT = Object.freeze({ apiBaseUrl: $url, company: '$($item.Company)', requestScope: 'production', allowSending: location.hostname === '$($item.Host)' });"
    [IO.File]::WriteAllText($path,$content,[Text.UTF8Encoding]::new($false))
}
Write-Host 'Configuracoes dos dois sites atualizadas no pacote. Publique os arquivos nos sites existentes. Nenhum arquivo no servidor foi publicado automaticamente.'
